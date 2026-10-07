using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SmartAdmin.Core;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 外部登录回调地址的唯一算法:登录控制器拼 <c>redirect_uri</c> 与管理页显示「请填到厂商后台的回调地址」用的是同一份,
/// 两处不会算出不同的值。
/// </summary>
public static class ExternalAuthCallback
{
    /// <summary>外部登录控制器的路由前缀,与 <c>ExternalAuthController</c> 上的 <c>[Route]</c> 一致</summary>
    public const string RoutePath = "/api/v1/auth/external";

    /// <summary>生产环境没配 <c>CallbackBaseUrl</c>:此时无法安全算出回调地址(不能信请求 Host 头)。</summary>
    public static bool BaseUrlMissing(AdminExternalAuthOptions options, IHostEnvironment env) =>
        string.IsNullOrWhiteSpace(options.CallbackBaseUrl) && !env.IsDevelopment();

    /// <summary>
    /// 浏览器眼中本应用的路径前缀:配了 <c>CallbackBaseUrl</c> 取它的路径部分(网关子路径部署,
    /// 如 https://gw.example.com/admin → /admin),没配时(仅开发环境)取 <c>Request.PathBase</c>(UsePathBase、IIS 子应用)。
    /// </summary>
    public static string PathBase(AdminExternalAuthOptions options, HttpRequest request) =>
        Uri.TryCreate(options.CallbackBaseUrl, UriKind.Absolute, out var baseUri)
            ? baseUri.AbsolutePath.TrimEnd('/')
            : request.PathBase.ToUriComponent();

    /// <summary>
    /// 回调地址:配了 <c>CallbackBaseUrl</c> 用之(生产必配),否则仅开发环境回退到请求主机。
    /// 生产未配时抛 <see cref="InvalidOperationException"/>:靠请求 Host 头推 redirect_uri 可被伪造带偏(OAuth mix-up 面)。
    /// </summary>
    public static string BuildUri(AdminExternalAuthOptions options, IHostEnvironment env, HttpRequest request, string provider)
    {
        if (BaseUrlMissing(options, env))
            throw new InvalidOperationException(
                "外部登录已启用,但生产环境未配置 SmartAdmin:ExternalAuth:CallbackBaseUrl。" +
                "回调基址(redirect_uri 来源)必须是稳定的后端公网地址,不能从请求 Host 头推断(可伪造)。");

        return string.IsNullOrWhiteSpace(options.CallbackBaseUrl)
            ? $"{request.Scheme}://{request.Host}{PathBase(options, request)}{RoutePath}/{provider}/callback"
            : $"{options.CallbackBaseUrl!.TrimEnd('/')}{RoutePath}/{provider}/callback";
    }
}
