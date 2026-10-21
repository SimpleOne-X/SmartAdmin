using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件服务器管理服务——运维在「文件路径」页登记文件服务器(根路径 + 访问账号)的标准 CRUD。
/// 读取方(文件路径的上传、下载)经 <see cref="CacheKeys.FileServers"/> 缓存读本服务写入的数据,
/// 增删改因此必须失效该缓存。
/// </summary>
public interface IFileServerService
{
    /// <summary>分页查询,按 Keyword 模糊过滤名称/根路径 + Enabled 精确过滤。</summary>
    Task<PagedList<FileServerView>> PageAsync(FileServerPageInput input);

    /// <summary>按 Id 取单条,不存在抛 <see cref="ErrorCode.FileServerNotFound"/>。</summary>
    Task<FileServerView> GetAsync(long id);

    /// <summary>
    /// 新增。校验规则见 <see cref="FileServerService"/>:根路径须通过 <see cref="FilePathRules.IsAllowedRoot"/>;连接器支持账号时填了账号必须是共享路径;
    /// 同一主机只能用同一个账号;名称唯一。填了密码而数据保护密钥是临时密钥时抛 <see cref="ErrorCode.FileServerKeyMissing"/>。返回新 Id。
    /// </summary>
    Task<long> AddAsync(FileServerSaveInput input);

    /// <summary>
    /// 更新,校验同 <see cref="AddAsync"/>,但主机没变时不查同主机账号一致:改了账号或密码,同主机的其他行在同一个事务里一起改成相同的账号和密码。
    /// <c>Password</c> 为 null 或空串表示不改;换了主机或账号而密码留空抛 <see cref="ErrorCode.FileServerPasswordRequired"/>。
    /// 不存在抛 <see cref="ErrorCode.FileServerNotFound"/>。
    /// </summary>
    Task UpdateAsync(long id, FileServerSaveInput input);

    /// <summary>软删除。仍有未删除的文件路径引用它时抛 <see cref="ErrorCode.FileServerInUse"/>。</summary>
    Task DeleteAsync(long id);

    /// <summary>全部文件服务器(含停用的)的下拉选项。</summary>
    Task<IReadOnlyList<FileServerOption>> OptionsAsync();

    /// <summary>
    /// 测试连接,测的是弹窗里当前填的内容,不要求先保存、也不落库:连接(填了账号时,忽略此前记下的失败)→ 根路径存在 → 能取到第一个条目。
    /// 校验和保存相同(根路径、共享路径),但不查名称和同主机账号一致,好在保存前先试新账号;不通过抛 <see cref="AdminException"/>;
    /// 其余失败(含 5 秒超时)放进结果的原因码,不抛异常。
    /// </summary>
    Task<FileAccessTestResult> TestAsync(FileServerTestInput input, CancellationToken ct = default);

    /// <summary>
    /// 批量在线状态:并行对每行确保已连上(不重试已记下的失败)并检查根路径存在,单行 3 秒超时,不写文件、不落库。
    /// 不存在的 Id 不出现在结果里。多副本部署时反映的是处理这次请求的副本能不能访问。
    /// </summary>
    Task<IReadOnlyList<FileAccessStatus>> StatusAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
}
