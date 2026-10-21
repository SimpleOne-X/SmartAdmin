using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件路径配置服务——运维在「文件路径」页登记业务落盘位置(文件服务器 + 子文件夹 + 命名、后缀、大小、角色规则)的标准 CRUD。
/// 上传、下载经 <see cref="CacheKeys.FilePaths"/> 缓存读本服务写入的数据,增删改因此必须失效该缓存。
/// </summary>
public interface IFilePathConfigService
{
    /// <summary>分页查询,按 Keyword 模糊过滤编码/名称 + ServerId、Enabled 精确过滤。</summary>
    Task<PagedList<FilePathView>> PageAsync(FilePathPageInput input);

    /// <summary>按 Id 取单条,不存在抛 <see cref="ErrorCode.FilePathNotFound"/>。</summary>
    Task<FilePathView> GetAsync(long id);

    /// <summary>
    /// 新增。编码只允许字母、数字、<c>.</c>、<c>_</c>、<c>-</c> 且不超过 64 个字符(<see cref="ErrorCode.FilePathCodeInvalid"/>),
    /// 须唯一(<see cref="ErrorCode.FilePathCodeExists"/>);文件服务器须存在;其余规则见 <see cref="FilePathConfigService"/>。返回新 Id。
    /// </summary>
    Task<long> AddAsync(FilePathAddInput input);

    /// <summary>更新,校验同 <see cref="AddAsync"/>(编码不可改)。不存在抛 <see cref="ErrorCode.FilePathNotFound"/>。</summary>
    Task UpdateAsync(long id, FilePathUpdateInput input);

    /// <summary>软删除,编码随之释放。不存在抛 <see cref="ErrorCode.FilePathNotFound"/>。</summary>
    Task DeleteAsync(long id);

    /// <summary>白名单根目录与当前平台是否支持账号连接。</summary>
    FilePathEnvironment GetEnvironment();

    /// <summary>
    /// 测试连接,测的是弹窗里当前填的内容,不要求先保存、也不落库。连接器忽略此前记下的失败重新连一次;
    /// 可写的行在文件夹里写入、读回、删除一个 <c>.smartadmin-probe-{GUID}</c> 文件(文件夹不存在时先创建),只读的行只检查文件夹存在。
    /// 子文件夹不合法、文件服务器不存在或根路径不可用抛 <see cref="AdminException"/>;其余失败(含 5 秒超时)放进结果的原因码,不抛异常。
    /// </summary>
    Task<FileAccessTestResult> TestAsync(FilePathTestInput input, CancellationToken ct = default);

    /// <summary>
    /// 批量在线状态:并行对每行确保服务器已连上(不重试已记下的失败)并检查文件夹存在,单行 3 秒超时,不写文件、不落库。
    /// 不存在的 Id 不出现在结果里。多副本部署时反映的是处理这次请求的副本能不能访问。
    /// </summary>
    Task<IReadOnlyList<FileAccessStatus>> StatusAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
}
