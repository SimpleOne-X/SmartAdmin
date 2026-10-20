using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 下载出参。<see cref="Content"/> 已打开且位置在开头,<b>由调用方负责释放</b>;<see cref="FileName"/> 是请求路径的最后一段
/// (大小写不区分的文件系统上,可能与磁盘上的写法不同),<see cref="Extension"/> 是它的后缀(小写、含点,没有后缀是 <c>""</c>),
/// <see cref="Length"/> 是字节数。
/// </summary>
public sealed record FilePathDownload(Stream Content, string FileName, string Extension, long Length);

/// <summary>
/// 文件路径下载服务:按编码找到文件夹,把业务保存的相对路径读出来。与 <c>IFileService</c> 的通用附件下载互相独立。
/// 本服务不做角色判断——后端代码直接调用由调用方自己负责,HTTP 入口先过 <see cref="IFilePathAccessChecker"/>。
/// 后缀白名单只约束上传,下载不看它:只读的行里本来就有各种后缀的存档。
/// </summary>
public interface IFilePathDownloadService
{
    /// <summary>
    /// 以只读方式打开文件夹里的一个文件,读取时允许别的上传同时覆盖或删除它。编码不存在、停用或文件夹不可访问见 <see cref="IFilePathResolver.ResolveAsync"/>;
    /// 路径不合法(含 <c>..</c>、绝对路径)抛 <see cref="ErrorCode.FilePathInvalidPath"/>;
    /// 含以点开头的段(探针文件、覆盖用的临时文件)、文件不存在(包括路径指向目录、名字含 Windows 不接受的字符)一律抛 <see cref="ErrorCode.FilePathFileNotFound"/>;
    /// 访问失败(共享断开、拒绝访问)不当作「不存在」,抛 <see cref="ErrorCode.FilePathFolderUnavailable"/>。
    /// </summary>
    Task<FilePathDownload> OpenReadAsync(string code, string path, CancellationToken ct = default);

    /// <summary>
    /// 文件的完全限定路径,给要自己打开文件的调用方(例如交给服务器直接发送)。路径处理和错误同 <see cref="OpenReadAsync"/>,
    /// 但只做路径运算:不检查文件是否存在,文件不在由调用方打开时发现。
    /// </summary>
    Task<string> GetFullPathAsync(string code, string path, CancellationToken ct = default);
}
