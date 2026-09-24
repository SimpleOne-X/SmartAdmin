using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 第三方登录连接配置的管理服务:目录、保存、清除、连接测试。机密加密入库,任何出参都不含明文。
/// 登录、回调、绑定读的是 <see cref="IExternalAuthProviderRegistry"/>,不经本服务。
/// </summary>
public interface IExternalAuthProviderService
{
    /// <summary>目录:已装类型、全部 provider(含代码注册的只读项)、主密钥状态。</summary>
    Task<ExternalAuthCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增或更新 <paramref name="code"/> 对应的配置。机密字段留空 = 不修改;改了决定请求去向的字段(如 OIDC Authority)
    /// 时机密必须重新输入。保存成功后失效注册表缓存。
    /// </summary>
    Task SaveAsync(string code, ExternalAuthProviderSaveInput input, CancellationToken cancellationToken = default);

    /// <summary>清除配置(软删,释放 Code)。不动 <c>sys_user_external</c> 里已有的绑定。</summary>
    Task DeleteAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>连接测试:不落库、不带用户登录态;网络与解析错误折成失败项返回,不抛。</summary>
    Task<ExternalAuthTestView> TestAsync(ExternalAuthProviderTestInput input, CancellationToken cancellationToken = default);
}
