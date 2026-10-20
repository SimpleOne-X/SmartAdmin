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
    /// 开始一次分片上传。只读、后缀、大小、业务路径在这里就按声明值拦截(错误码同 <see cref="UploadAsync"/>),
    /// 通过才建暂存会话,所以被拒绝的上传不会占暂存空间。<see cref="FilePathChunkInitInput.Size"/> 为负或
    /// <see cref="FilePathChunkInitInput.Sha256"/> 不是 64 位十六进制抛 <see cref="ErrorCode.FilePathUploadSessionInvalid"/>。
    /// 会话绑定当前用户和编码,长期没有新分片会被 <see cref="FilePathStagingSweeper"/> 清掉。
    /// </summary>
    Task<FilePathChunkInitOutput> ChunkInitAsync(string code, FilePathChunkInitInput input, CancellationToken ct = default);

    /// <summary>
    /// 上传一个分片,重传同一片是覆盖。<paramref name="uploadId"/> 不是本服务发的格式、会话不存在或已过期、
    /// 不属于当前用户和编码、<paramref name="index"/> 越界、分片长度与应有长度不符,一律抛 <see cref="ErrorCode.FilePathUploadSessionInvalid"/>。
    /// 长度不符时不留下这一片;重传失败时上一次的分片原样保留。
    /// </summary>
    Task ChunkAsync(string code, string uploadId, int index, Stream content, CancellationToken ct = default);

    /// <summary>
    /// 合并分片并写入文件夹。缺片抛 <see cref="ErrorCode.FilePathChunkMissing"/>(args.index 是第一个缺的编号),此时会话保留,补传后可再次合并。
    /// 缺片检查通过后先认领会话:同一会话同时只有一个合并能成功,另一个(重复点击、超时重试)抛 <see cref="ErrorCode.FilePathUploadSessionInvalid"/>,
    /// 合并期间到达的分片同样是无效会话。认领成功的合并、按实际大小和当前规则再校验(同 <see cref="UploadAsync"/>)、核对哈希
    /// (不符抛 <see cref="ErrorCode.FilePathChunkHashMismatch"/>)、写入,不论成败都删除会话。
    /// </summary>
    Task<FilePathUploadOutput> ChunkCompleteAsync(string code, string uploadId, CancellationToken ct = default);

    /// <summary>
    /// 删除文件夹里的一个文件(不删目录)。只读的行抛 <see cref="ErrorCode.FilePathReadOnly"/>;
    /// 路径不合法、含以点开头的段、文件不存在(包括路径指向目录、名字含 Windows 不接受的字符)都一律抛 <see cref="ErrorCode.FilePathFileNotFound"/>;
    /// 访问失败(共享断开、拒绝访问)不当作「不存在」,抛 <see cref="ErrorCode.FilePathFolderUnavailable"/>。
    /// </summary>
    Task DeleteAsync(string code, string path, CancellationToken ct = default);
}
