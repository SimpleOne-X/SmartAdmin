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
    /// 新增。校验规则见 <see cref="FileServerService"/>:根路径须在白名单内;连接器支持账号时填了账号必须是共享路径;
    /// 同一主机只能用同一个账号;名称唯一。填了密码而数据保护密钥是临时密钥时抛 <see cref="ErrorCode.FileServerKeyMissing"/>。返回新 Id。
    /// </summary>
    Task<long> AddAsync(FileServerSaveInput input);

    /// <summary>更新,校验同 <see cref="AddAsync"/>。<c>Password</c> 为 null 或空串表示不改。不存在抛 <see cref="ErrorCode.FileServerNotFound"/>。</summary>
    Task UpdateAsync(long id, FileServerSaveInput input);

    /// <summary>软删除。仍有未删除的文件路径引用它时抛 <see cref="ErrorCode.FileServerInUse"/>。</summary>
    Task DeleteAsync(long id);

    /// <summary>全部文件服务器(含停用的)的下拉选项。</summary>
    Task<IReadOnlyList<FileServerOption>> OptionsAsync();
}
