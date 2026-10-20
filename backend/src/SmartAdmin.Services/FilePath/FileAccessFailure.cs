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
        Unknown = "unknown";

    /// <summary>Win32 错误码 → 原因码,没收录的是 <see cref="Unknown"/>。</summary>
    public static string FromWin32(int code) => code switch
    {
        5 => AccessDenied,                      // ERROR_ACCESS_DENIED
        53 => NetworkPathNotFound,              // ERROR_BAD_NETPATH
        67 => ShareNotFound,                    // ERROR_BAD_NET_NAME
        1326 or 86 or 1330 => BadCredentials,   // ERROR_LOGON_FAILURE / ERROR_INVALID_PASSWORD / ERROR_PASSWORD_EXPIRED
        1909 or 1331 => AccountLocked,          // ERROR_ACCOUNT_LOCKED_OUT / ERROR_ACCOUNT_DISABLED
        1219 => CredentialConflict,             // ERROR_SESSION_CREDENTIAL_CONFLICT
        _ => Unknown,
    };

    /// <summary>异常 → 原因码。<see cref="DirectoryNotFoundException"/> 是 <see cref="IOException"/> 的子类,必须先判。</summary>
    public static string FromException(Exception e) => e switch
    {
        FileServerConnectException c => c.Reason,
        TimeoutException => Timeout,
        UnauthorizedAccessException => AccessDenied,
        DirectoryNotFoundException => FolderMissing,
        // .NET 把 Win32 错误包成 0x8007xxxx 的 HRESULT,低 16 位就是原错误码
        IOException io when ((uint)io.HResult >> 16) == 0x8007 => FromWin32(io.HResult & 0xFFFF),
        _ => Unknown,
    };

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
