using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 外部登录的内置装配:固定注册标准 OIDC 类型描述,以及走 SSRF 围栏的出站命名客户端。
/// 企业微信、钉钉、GitHub、微信等厂商类型由可选包各自注册类型描述;连接与密钥由管理员在「登录方式」页填写、加密入库,
/// 运行时由 <see cref="IExternalAuthProviderRegistry"/> 按类型装配成 <see cref="IExternalAuthProvider"/>。
/// </summary>
public static class ExternalAuthSetup
{
    /// <summary>
    /// 校验 <c>CallbackBaseUrl</c>,注册内置 OIDC 类型描述与围栏出站客户端。
    /// <para>类型描述用 <c>TryAddEnumerable</c>(与可选包的类型并存,按实现类型去重)。消费者接自有 IdP:在 <c>AddSmartAdmin()</c> 前
    /// 自行注册 <see cref="IExternalAuthProvider"/>(用不同 Code),与库里配置的并存;同 Code 时代码注册的优先。</para>
    /// <para>围栏处理器与 AI 网关、定时任务共用同一套 <see cref="HttpFence"/>,配置读 <c>AdminAiOptions.Http</c>。</para>
    /// </summary>
    public static IServiceCollection AddExternalAuthProviders(this IServiceCollection services, AdminExternalAuthOptions options)
    {
        ValidateCallbackBaseUrl(options);

        services.AddHttpClient(ExternalAuthHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(sp => HttpFence.CreateHandler(sp.GetRequiredService<AdminAiOptions>().Http));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProviderType, OidcExternalAuthProviderType>());

        return services;
    }

    /// <summary>
    /// <c>CallbackBaseUrl</c> 只填后端对外的根地址,回调路径由内核接在后面。填错了厂商照样跳转(它们只校验域名),
    /// 最后落到一条不存在的路径上,表现为「授权完什么也没发生」,所以启动时就拦下。
    /// 最容易填错的两种:整条回调地址,和前端结果页 <c>FrontendResultPath</c>。
    /// </summary>
    private static void ValidateCallbackBaseUrl(AdminExternalAuthOptions options)
    {
        var value = options.CallbackBaseUrl;
        if (string.IsNullOrWhiteSpace(value)) return;

        var valid = Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                    && uri.Query.Length == 0 && uri.Fragment.Length == 0;
        if (valid)
        {
            var path = uri!.AbsolutePath.TrimEnd('/');
            var frontendPath = Uri.TryCreate(options.FrontendResultPath, UriKind.Absolute, out var frontend)
                ? frontend.AbsolutePath
                : options.FrontendResultPath.Split('?')[0];
            valid = !path.Contains("/api/v1/auth/external", StringComparison.OrdinalIgnoreCase)
                    && (path.Length == 0 || !string.Equals(path, frontendPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }

        if (!valid)
            throw new InvalidOperationException(
                $"SmartAdmin:ExternalAuth:CallbackBaseUrl 只填后端对外的根地址,如 https://admin.example.com,当前值:{value}。" +
                "回调路径 /api/v1/auth/external/{provider}/callback 由内核拼接,不要填整条回调地址;" +
                "登录完成后跳回的前端页面是 SmartAdmin:ExternalAuth:FrontendResultPath,不是这一项。");
    }
}
