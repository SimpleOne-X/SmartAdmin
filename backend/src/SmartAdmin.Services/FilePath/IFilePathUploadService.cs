using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件路径上传服务:按编码找到文件夹,校验只读、后缀、大小,按命名规则定相对路径并写入。
/// 与 <c>IFileService</c> 的通用附件上传互相独立。本服务不做角色判断——后端代码直接调用由调用方自己负责,
/// HTTP 入口先过 <see cref="IFilePathAccessChecker"/>。
/// </summary>
public interface IFilePathUploadService
{
    /// <summary>
    /// 上传一个文件。编码不存在、停用或文件夹不可访问见 <see cref="IFilePathResolver.ResolveAsync"/>;
    /// 其后依次可能抛:<see cref="ErrorCode.FilePathReadOnly"/>、<see cref="ErrorCode.FilePathRuleIncomplete"/>(可写的行缺后缀白名单或大小上限)、
    /// <see cref="ErrorCode.FilePathExtNotAllowed"/>(args.ext)、
    /// <see cref="ErrorCode.FilePathTooLarge"/>(args.maxSizeMb)、<see cref="ErrorCode.FilePathInvalidPath"/>(subPath 不合法)、
    /// <see cref="ErrorCode.FilePathFileExists"/>(业务指定路径且同名策略为拒绝)。
    /// 写入时的 IO 失败转成 <see cref="ErrorCode.FilePathFolderUnavailable"/>。失败不留半截文件或临时文件。
    /// </summary>
    Task<FilePathUploadOutput> UploadAsync(string code, FilePathUploadInput input, CancellationToken ct = default);

    /// <summary>
    /// 删除文件夹里的一个文件(不删目录)。只读的行抛 <see cref="ErrorCode.FilePathReadOnly"/>;
    /// 路径不合法、含以点开头的段、文件不存在(包括路径指向目录)都一律抛 <see cref="ErrorCode.FilePathFileNotFound"/>;
    /// 访问失败(共享断开、拒绝访问)不当作「不存在」,抛 <see cref="ErrorCode.FilePathFolderUnavailable"/>。
    /// </summary>
    Task DeleteAsync(string code, string path, CancellationToken ct = default);
}
