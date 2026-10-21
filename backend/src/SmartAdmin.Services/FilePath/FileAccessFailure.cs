using System.Diagnostics;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 把访问文件服务器的失败归成几个前端能翻译的原因码(对应语言包 <c>error.reason.*</c>);
/// 完整的异常和系统错误码另行写日志,不进信封。
/// </summary>
public static class FileAccessFailure
{
    public const string AccessDenied = "accessDenied",
        NetworkPathNotFound = "networkPathNotFound",
        ShareNotFound = "shareNotFound",
        BadCredentials = "badCredentials",
        AccountLocked = "accountLocked",
        CredentialConflict = "credentialConflict",
        Timeout = "timeout",
        FolderMissing = "folderMissing",
        Busy = "busy",
        Unknown = "unknown";

    /// <summary>Win32 错误码 → 原因码,没收录的是 <see cref="Unknown"/>。</summary>
    public static string FromWin32(int code) => code switch
    {
        5 => AccessDenied,                      // ERROR_ACCESS_DENIED
        53 or 51 or 59 or 64 => NetworkPathNotFound, // ERROR_BAD_NETPATH / ERROR_REM_NOT_LIST / ERROR_UNEXP_NET_ERR / ERROR_NETNAME_DELETED
        1203 or 1222 or 1231 or 1232 => NetworkPathNotFound, // ERROR_NO_NET_OR_BAD_PATH / ERROR_NO_NETWORK / ERROR_NETWORK_UNREACHABLE / ERROR_HOST_UNREACHABLE
        121 => Timeout,                         // ERROR_SEM_TIMEOUT
        67 => ShareNotFound,                    // ERROR_BAD_NET_NAME
        1326 or 86 or 1330 => BadCredentials,   // ERROR_LOGON_FAILURE / ERROR_INVALID_PASSWORD / ERROR_PASSWORD_EXPIRED
        1909 or 1331 => AccountLocked,          // ERROR_ACCOUNT_LOCKED_OUT / ERROR_ACCOUNT_DISABLED
        1219 => CredentialConflict,             // ERROR_SESSION_CREDENTIAL_CONFLICT
        _ => Unknown,
    };

    /// <summary>
    /// 异常 → 原因码。<see cref="DirectoryNotFoundException"/> 是 <see cref="IOException"/> 的子类,必须先判。
    /// 已经是「文件夹无法访问」的业务异常(熔断、繁忙)沿用它带的原因。
    /// </summary>
    public static string FromException(Exception e) => e switch
    {
        AdminException { Code: ErrorCode.FilePathFolderUnavailable, Args: { } args } when args.GetValueOrDefault("reason") is string r => r,
        FileServerConnectException c => c.Reason,
        TimeoutException => Timeout,
        UnauthorizedAccessException => AccessDenied,
        DirectoryNotFoundException => FolderMissing,
        // .NET 把 Win32 错误包成 0x8007xxxx 的 HRESULT,低 16 位就是原错误码
        IOException io when ((uint)io.HResult >> 16) == 0x8007 => FromWin32(io.HResult & 0xFFFF),
        _ => Unknown,
    };

    /// <summary>ERROR_INVALID_NAME 包成的 HRESULT:Windows 对含 <c>* ? &lt; &gt; | "</c> 的名字这样报。</summary>
    private const int InvalidName = unchecked((int)0x8007007B);

    /// <summary>
    /// 异常是否表示「这个文件不存在」:文件或其上级目录找不到,或者名字含 Windows 不接受的字符(这样的文件不可能存在)。
    /// 拒绝访问、共享断开、超时不算——它们是文件夹访问不了,不能让调用方当作文件已经没了。删除和下载共用这一个判断。
    /// </summary>
    public static bool IsMissingFile(Exception e) =>
        e is FileNotFoundException or DirectoryNotFoundException or IOException { HResult: InvalidName };

    /// <summary>异常 → 「文件夹不可访问」业务异常,原因码放进 args.reason,原异常留作 InnerException 供日志。</summary>
    public static AdminException ToError(Exception e) =>
        new(ErrorCode.FilePathFolderUnavailable, e, new Dictionary<string, object?> { ["reason"] = FromException(e) });
}

/// <summary>连接文件服务器(建立 SMB 会话)失败。<see cref="Reason"/> 是 <see cref="FileAccessFailure"/> 的原因码,<see cref="Win32Code"/> 是系统错误码。</summary>
public class FileServerConnectException(string reason, int win32Code)
    : IOException($"连接文件服务器失败:{reason}(Win32 错误 {win32Code})")
{
    public string Reason { get; } = reason;

    public int Win32Code { get; } = win32Code;
}

/// <summary>
/// 测试连接与在线状态共用的探测:在线程池里做文件 IO 并加时限(真实时间),失败和超时一律转成原因码,不往外抛;
/// 只有请求本身被取消时照常抛出。
/// </summary>
internal static class FileAccessProbe
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);

    public static async Task<FileAccessTestResult> TestAsync(Func<CancellationToken, Task> probe, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var reason = await RunAsync(probe, TestTimeout, ct);
        return new FileAccessTestResult(reason is null, reason, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public static async Task<FileAccessStatus> StatusAsync(long id, Func<CancellationToken, Task> probe, CancellationToken ct) =>
        await RunAsync(probe, StatusTimeout, ct) is { } reason ? new(id, false, reason) : new(id, true, null);

    /// <summary>文件夹必须存在。<see cref="Directory.Exists"/> 把拒绝访问、网络不通也报成 false,所以不存在时再列一次第一个条目,让它抛出具体原因。</summary>
    public static void RequireFolder(string path)
    {
        if (!Directory.Exists(path)) _ = Directory.EnumerateFileSystemEntries(path).Any();
    }

    /// <summary>
    /// 成功返回 null,失败返回原因码。到时限时取消交给探测的令牌:排在同一共享连接锁后面的探测随之退出,
    /// 不会在请求结束后一个接一个地去连;不看令牌的 IO 留在后台自己结束。
    /// </summary>
    private static async Task<string?> RunAsync(Func<CancellationToken, Task> probe, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await Task.Run(() => probe(limit.Token), ct).WaitAsync(limit.Token);
            return null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return limit.IsCancellationRequested ? FileAccessFailure.Timeout : FileAccessFailure.FromException(e);
        }
    }
}
