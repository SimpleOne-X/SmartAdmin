using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// Windows 上的连接器:填了账号的文件服务器,在第一次访问之前用 <c>WNetAddConnection2W</c>(<c>mpr.dll</c>)
/// 按账号密码建立不分配盘符的连接。连接属于后端进程的登录会话,之后进程里访问这个共享都用这套凭据。
/// <list type="bullet">
///   <item>每个共享记着当前连接用的凭据指纹(共享、账号、密码信封);配置改了就先断开旧连接再按新凭据连。
///         断开不强制,还有文件开着时继续用旧连接,下次访问再换。</item>
///   <item>同一个共享的连接操作串行(每个共享一把锁)。native 调用是阻塞的,放在线程池里跑并加超时。</item>
///   <item>账号或密码错误不自动重试:这套凭据记为失败,直到配置改了或有人点测试连接;否则每个请求都拿错密码去试,很快就把域账号锁住。
///         网络类失败过 30 秒再试。</item>
/// </list>
/// 两个 native 调用是 <c>protected virtual</c>,测试用子类替掉,不碰真实的网络。
/// </summary>
public class WindowsFileServerConnector(ISecretProtector protector, ILogger<WindowsFileServerConnector> logger, TimeProvider time)
    : IFileServerConnector
{
    private const int ResourceTypeDisk = 1;                  // RESOURCETYPE_DISK
    private const int ErrorAlreadyAssigned = 85;             // ERROR_ALREADY_ASSIGNED
    private const int ErrorSessionCredentialConflict = 1219; // ERROR_SESSION_CREDENTIAL_CONFLICT
    private const int ErrorOpenFiles = 2401;                 // ERROR_OPEN_FILES

    /// <summary>
    /// 账号或密码本身有问题的错误码:ERROR_INVALID_PASSWORD、ERROR_LOGON_FAILURE、ERROR_PASSWORD_EXPIRED、
    /// ERROR_ACCOUNT_DISABLED、ERROR_ACCOUNT_LOCKED_OUT。其余都按网络类处理。
    /// </summary>
    private static readonly HashSet<int> CredentialErrors = [86, 1326, 1330, 1331, 1909];

    private static readonly TimeSpan NetworkRetryAfter = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, ShareState> _shares = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public virtual bool SupportsCredentials => true;

    /// <summary>单次 native 连接调用的时限(真实时间)。主机不通时这个调用可能卡十几秒。</summary>
    protected virtual TimeSpan ConnectTimeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public virtual async Task EnsureConnectedAsync(FileServerTarget target, bool retryFailed, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(target.UserName)) return;
        // 保存时已校验「填了账号必须是共享路径」,走到这里说明库里的数据被改过
        if (!FilePathRules.TryGetShare(target.RootPath, out _, out var share))
            throw new FileServerConnectException(FileAccessFailure.Unknown, 0);

        var fingerprint = Fingerprint(share, target.UserName, target.PasswordProtected);
        var state = _shares.GetOrAdd(share, _ => new ShareState());
        await state.Lock.WaitAsync(ct);
        try
        {
            if (state.ConnectedFingerprint == fingerprint) return;
            if (state.Failure is { } failed && failed.Fingerprint == fingerprint && !retryFailed
                && (CredentialErrors.Contains(failed.Win32Code) || time.GetUtcNow() - failed.At < NetworkRetryAfter))
                throw new FileServerConnectException(FileAccessFailure.FromWin32(failed.Win32Code), failed.Win32Code);

            if (state.ConnectedFingerprint is not null)
            {
                if (CancelConnection(share) == ErrorOpenFiles)
                {
                    logger.LogInformation("文件服务器共享 {Share} 还有文件开着,暂时继续用旧连接,下次访问再换新凭据", share);
                    return;
                }
                state.ConnectedFingerprint = null;
            }

            var password = Unprotect(share, target.PasswordProtected);
            var rc = await ConnectAsync(share, target.UserName, password, ct);
            if (rc == ErrorSessionCredentialConflict)
            {
                // 常见于后端重启前用旧密码连过:断开这个共享再连一次
                CancelConnection(share);
                rc = await ConnectAsync(share, target.UserName, password, ct);
            }

            if (rc is 0 or ErrorAlreadyAssigned)
            {
                state.ConnectedFingerprint = fingerprint;
                state.Failure = null;
                logger.LogInformation("已用账号 {UserName} 连接文件服务器共享 {Share}", target.UserName, share);
                return;
            }

            var reason = FileAccessFailure.FromWin32(rc);
            state.Failure = new ConnectFailure(fingerprint, rc, time.GetUtcNow());
            logger.LogWarning("用账号 {UserName} 连接文件服务器共享 {Share} 失败:Win32 错误 {Win32Code}({Reason})",
                target.UserName, share, rc, reason);
            throw new FileServerConnectException(reason, rc);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("用账号 {UserName} 连接文件服务器共享 {Share} 超时", target.UserName, share);
            throw new FileServerConnectException(FileAccessFailure.Timeout, 0);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    /// <summary>
    /// 建立不分配盘符的连接(<c>WNetAddConnection2W</c>,<c>dwFlags = 0</c>:不记进用户配置文件,下次登录不恢复)。返回 Win32 错误码,0 为成功。
    /// </summary>
    protected virtual int AddConnection(string remoteName, string? userName, string? password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = remoteName };
        return WNetAddConnection2(ref resource, password, userName, 0);
    }

    /// <summary>断开连接(<c>WNetCancelConnection2W</c>,不强制:还有文件开着时返回 2401)。返回 Win32 错误码,0 为成功。</summary>
    protected virtual int CancelConnection(string remoteName) => WNetCancelConnection2(remoteName, 0, false);

    /// <summary>在线程池里调 <see cref="AddConnection"/>,超过 <see cref="ConnectTimeout"/> 抛 <see cref="TimeoutException"/>。</summary>
    private Task<int> ConnectAsync(string share, string userName, string? password, CancellationToken ct)
    {
        // shortcut: 超时后后台那次连接仍在跑，下一次请求可能并发再连；真出现连接风暴时改成复用在途任务
        return Task.Run(() => AddConnection(share, userName, password), CancellationToken.None).WaitAsync(ConnectTimeout, ct);
    }

    /// <summary>解开密码信封;解不开(数据保护密钥换过)按连接失败处理,而不是让请求变成 500。</summary>
    private string? Unprotect(string share, string? envelope)
    {
        if (string.IsNullOrEmpty(envelope)) return null;
        try
        {
            return protector.Unprotect(envelope);
        }
        catch (CryptographicException e)
        {
            logger.LogWarning(e, "文件服务器共享 {Share} 的密码解不开,数据保护密钥可能换过,需要重新保存密码", share);
            throw new FileServerConnectException(FileAccessFailure.Unknown, 0);
        }
    }

    private static string Fingerprint(string share, string userName, string? passwordProtected) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{share.ToLowerInvariant()}\n{userName}\n{passwordProtected}")));

    private sealed record ConnectFailure(string Fingerprint, int Win32Code, DateTimeOffset At);

    /// <summary>一个共享的连接状态;字段只在持有 <see cref="Lock"/> 时读写。</summary>
    private sealed class ShareState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public string? ConnectedFingerprint { get; set; }

        public ConnectFailure? Failure { get; set; }
    }

    // ── P/Invoke(winnetwk.h)──────────────────────────────────────────

    /// <summary>NETRESOURCEW,字段顺序与 winnetwk.h 一致;只填 dwType 与 lpRemoteName,其余为 0/null。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    // 注意参数顺序:密码在账号前面
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
