using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// <c>/scalar</c>(壳页面,始终匿名)与 <c>/openapi/{documentName}.json</c>(契约数据,生产开启时收紧)
/// 的生产环境授权网关。
/// <para><see cref="RolePermissionAttribute"/> 是 MVC <c>IAsyncAuthorizationFilter</c>,只在控制器 action
/// 管线生效;这两个是 Minimal API 端点,走 ASP.NET Core 授权中间件的 Policy 机制,两条管道不通用,
/// 故另起一份判定逻辑(已认证 → 会话仍活跃 → 超管放行 → 否则比对权限码),规则与
/// <see cref="RolePermissionAttribute"/> 保持一致但各自独立实现。</para>
/// </summary>
public sealed class ScalarAccessRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "ScalarAccess";
}

public class ScalarAccessAuthorizationHandler : AuthorizationHandler<ScalarAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScalarAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext) return;
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true) return;

        var sessionId = user.FindFirstValue(TokenClaimNames.SESSION_ID);
        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessions = httpContext.RequestServices.GetRequiredService<ISessionService>();
            if (!await sessions.IsActiveAsync(sessionId)) return;
        }

        if (user.HasClaim(TokenClaimNames.SUPER_ADMIN, "true"))
        {
            context.Succeed(requirement);
            return;
        }

        if (!long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)) return;

        var template = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var code = PermissionCode.Build(httpContext.Request.Method, template ?? httpContext.Request.Path.Value);
        var codes = await httpContext.RequestServices.GetRequiredService<IPermissionProvider>()
            .GetPermissionCodesAsync(userId, httpContext.RequestAborted);
        if (codes.Contains(code)) context.Succeed(requirement);
    }
}
