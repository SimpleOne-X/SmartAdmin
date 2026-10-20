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
}
