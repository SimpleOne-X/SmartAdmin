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
///   <item>每个共享记着当前连接用的凭据指纹(共享、账号、解开后的密码的哈希;信封每次加密都带随机数,不能拿来比);
///         配置改了就先断开旧连接再按新凭据连,账号清空了就断开。断开不强制,还有文件开着时继续用旧连接,下次访问再换。</item>
///   <item>同一个共享的连接操作串行(每个共享一把锁)。调用方的取消令牌只用来等锁;拿到锁之后 native 调用只受
///         <see cref="ConnectTimeout"/> 限制,结果一定记进状态。</item>
///   <item>账号或密码错误不自动重试:这套凭据记为失败,直到配置改了或有人点测试连接;否则每个请求都拿错密码去试,很快就把域账号锁住。
///         网络类失败(含超时)过 30 秒再试。</item>
/// </list>
/// 两个 native 调用是 <c>protected virtual</c>,测试用子类替掉,不碰真实的网络。
/// </summary>
public class WindowsFileServerConnector(ISecretProtector protector, ILogger<WindowsFileServerConnector> logger, TimeProvider time)
    : IFileServerConnector
{
    private const int ResourceTypeDisk = 1;                  // RESOURCETYPE_DISK
    private const int ErrorAlreadyAssigned = 85;             // ERROR_ALREADY_ASSIGNED
    private const int ErrorSessionCredentialConflict = 1219; // ERROR_SESSION_CREDENTIAL_CONFLICT
    private const int ErrorNotConnected = 2250;              // ERROR_NOT_CONNECTED
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

    /// <summary>单次 native 调用(连接或断开)的时限(真实时间)。主机不通时这个调用可能卡十几秒。</summary>
    protected virtual TimeSpan ConnectTimeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public virtual async Task EnsureConnectedAsync(FileServerTarget target, bool retryFailed, CancellationToken ct = default)
    {
        var isShare = FilePathRules.TryGetShare(target.RootPath, out _, out var share);
        if (string.IsNullOrEmpty(target.UserName))
        {
            if (isShare) await DisconnectAsync(share, ct);
            return;
        }
        // 保存时已校验「填了账号必须是共享路径」,走到这里说明库里的数据被改过
        if (!isShare) throw new FileServerConnectException(FileAccessFailure.Unknown, 0);

        await ConnectAsync(share, target.UserName, Unprotect(share, target.PasswordProtected), retryFailed, ct);
    }

    /// <summary>
    /// 账号清空之后断开这个共享先前用账号建的连接:不断开的话进程里访问这个共享仍带着旧凭据,测试连接也会误报正常。
    /// 断开成功(或本来就不在了)才忘掉旧连接;还有文件开着、超时等情况继续用旧连接,下次访问再断。
    /// </summary>
    protected virtual async Task DisconnectAsync(string share, CancellationToken ct)
    {
        // 从没用账号连过的共享不用拿锁
        if (!_shares.TryGetValue(share, out var state)) return;
        await state.Lock.WaitAsync(ct);
        try
        {
            if (state.ConnectedFingerprint is null) return;
            var rc = await RunNativeAsync(() => CancelConnection(share));
            if (rc is 0 or ErrorNotConnected)
            {
                state.ConnectedFingerprint = null;
                logger.LogInformation("文件服务器共享 {Share} 不再用账号访问,已断开先前的连接", share);
            }
            else
            {
                logger.LogInformation("断开文件服务器共享 {Share} 先前的连接失败:Win32 错误 {Win32Code},暂时继续用旧连接,下次访问再断", share, rc);
            }
        }
        catch (TimeoutException)
        {
            logger.LogWarning("断开文件服务器共享 {Share} 先前的连接超时,暂时继续用旧连接,下次访问再断", share);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    /// <summary>
    /// 按账号密码确保连上:同一套凭据已连上就返回;这套凭据记着失败且不该重试时直接抛;先断开别的凭据的旧连接,再连。
    /// 连接的结果(含超时)一律记进状态。调用方的令牌只用来等锁:调用方先放弃时结果照样记下,
    /// 否则错密码的结果被丢掉,下一个请求又拿它去试。
    /// </summary>
    protected virtual async Task ConnectAsync(string share, string userName, string password, bool retryFailed, CancellationToken ct)
    {
        var fingerprint = Fingerprint(share, userName, password);
        var state = _shares.GetOrAdd(share, _ => new ShareState());
        await state.Lock.WaitAsync(ct);
        try
        {
            if (state.ConnectedFingerprint == fingerprint) return;
            if (state.Failure is { } failed && failed.Fingerprint == fingerprint && !retryFailed
                && (CredentialErrors.Contains(failed.Win32Code) || time.GetUtcNow() - failed.At < NetworkRetryAfter))
                throw new FileServerConnectException(failed.Reason, failed.Win32Code);

            int rc;
            try
            {
                if (state.ConnectedFingerprint is not null)
                {
                    if (await RunNativeAsync(() => CancelConnection(share)) == ErrorOpenFiles)
                    {
                        logger.LogInformation("文件服务器共享 {Share} 还有文件开着,暂时继续用旧连接,下次访问再换新凭据", share);
                        return;
                    }
                    state.ConnectedFingerprint = null;
                }
                rc = await AddConnectionAsync(share, userName, password);
            }
            catch (TimeoutException)
            {
                // 按网络类失败记下:30 秒内不再去连,免得主机不通时每个请求都排在锁后面等满时限
                state.Failure = new ConnectFailure(fingerprint, FileAccessFailure.Timeout, 0, time.GetUtcNow());
                logger.LogWarning("用账号 {UserName} 连接文件服务器共享 {Share} 超时", userName, share);
                throw new FileServerConnectException(FileAccessFailure.Timeout, 0);
            }

            if (rc is 0 or ErrorAlreadyAssigned)
            {
                state.ConnectedFingerprint = fingerprint;
                state.Failure = null;
                logger.LogInformation("已用账号 {UserName} 连接文件服务器共享 {Share}", userName, share);
                return;
            }

            var reason = FileAccessFailure.FromWin32(rc);
            state.Failure = new ConnectFailure(fingerprint, reason, rc, time.GetUtcNow());
            logger.LogWarning("用账号 {UserName} 连接文件服务器共享 {Share} 失败:Win32 错误 {Win32Code}({Reason})",
                userName, share, rc, reason);
            throw new FileServerConnectException(reason, rc);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    /// <summary>
    /// 调 <see cref="AddConnection"/>;返回 1219(这台主机上还留着别的凭据的连接,常见于后端重启前用旧密码连过)时断开这个共享再连一次。
    /// 返回 Win32 错误码;任一次 native 调用超时抛 <see cref="TimeoutException"/>。
    /// </summary>
    protected virtual async Task<int> AddConnectionAsync(string share, string userName, string password)
    {
        var rc = await RunNativeAsync(() => AddConnection(share, userName, password));
        if (rc != ErrorSessionCredentialConflict) return rc;
        await RunNativeAsync(() => CancelConnection(share));
        return await RunNativeAsync(() => AddConnection(share, userName, password));
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

    /// <summary>在线程池里跑一个阻塞的 native 调用,超过 <see cref="ConnectTimeout"/>(真实时间)抛 <see cref="TimeoutException"/>。</summary>
    private Task<int> RunNativeAsync(Func<int> call)
    {
        // shortcut: 超时后放弃的那次 native 调用不再跟踪,它之后连上或失败都不进状态,退避过后的重试可能和它并发;真出现连接风暴时改成复用在途任务
        return Task.Run(call, CancellationToken.None).WaitAsync(ConnectTimeout);
    }

    /// <summary>
    /// 解开密码信封。没有密码时是空串:交给 native 的空串表示「不用密码」,null 则表示「用这个账号的默认密码」。
    /// 解不开(数据保护密钥换过)按连接失败处理,而不是让请求变成 500。
    /// </summary>
    private string Unprotect(string share, string? envelope)
    {
        if (string.IsNullOrEmpty(envelope)) return "";
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

    /// <summary>凭据指纹:只留哈希,明文密码不存进状态。</summary>
    private static string Fingerprint(string share, string userName, string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{share.ToLowerInvariant()}\n{userName}\n{password}")));

    /// <summary>一次连接失败;超时的 <see cref="Win32Code"/> 是 0,按网络类失败处理。</summary>
    private sealed record ConnectFailure(string Fingerprint, string Reason, int Win32Code, DateTimeOffset At);

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
