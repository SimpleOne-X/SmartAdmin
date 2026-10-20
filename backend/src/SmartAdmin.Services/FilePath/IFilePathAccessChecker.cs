using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 文件路径的角色判断,只在 HTTP 接口上调用(后端代码直接调上传、下载服务时不判)。
/// 编码不存在或已停用抛 <see cref="ErrorCode.FilePathNotFound"/>。
/// </summary>
public interface IFilePathAccessChecker
{
    /// <summary>当前用户能否上传。未登录 false;超管 true;上传角色为空 = 所有登录用户 true;否则要有一个启用中的角色在列表里。</summary>
    Task<bool> CanUploadAsync(string code, CancellationToken ct = default);

    /// <summary>当前用户能否下载,规则同 <see cref="CanUploadAsync"/>,看的是下载角色。</summary>
    Task<bool> CanDownloadAsync(string code, CancellationToken ct = default);
}
