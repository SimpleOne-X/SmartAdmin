using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>ScalarAccess 策略的标记需求,判定逻辑见 <see cref="ScalarAccessAuthorizationHandler"/>。</summary>
public sealed class ScalarAccessRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "ScalarAccess";
}

/// <summary>
/// <c>/scalar</c>(壳页面,始终匿名)与 <c>/openapi/{documentName}.json</c>(契约数据,生产开启时收紧)
/// 的生产环境授权网关。
/// <para><see cref="RolePermissionAttribute"/> 是 MVC <c>IAsyncAuthorizationFilter</c>,只在控制器 action
/// 管线生效;这两个是 Minimal API 端点,走 ASP.NET Core 授权中间件的 Policy 机制,两条管道不通用,
/// 故另起一份判定逻辑(已认证 → 会话仍活跃 → 超管放行 → 否则比对权限码),规则与
/// <see cref="RolePermissionAttribute"/> 保持一致但各自独立实现。</para>
/// <para>每一步同样是 <c>protected virtual</c>,但只能用来<b>扩展</b>单步判定:本类以
/// <c>TryAddEnumerable&lt;IAuthorizationHandler&gt;</c> 注册,ASP.NET Core 的策略判定是"任一 handler
/// <c>Succeed</c> 即通过",消费者子类覆写某一步等于新增一条放行路径(只能<b>放宽</b>门槛)——原始实现的
/// 判定与 <c>Succeed</c> 仍然生效,子类无法收紧或替换它。要收紧或整体换掉这道网关,走
/// <see cref="ScalarAccessRequirement.PolicyName"/> 的策略预注册通道:在 <c>AddSmartAdmin()</c> 之前
/// 自建同名 <c>"ScalarAccess"</c> 策略即整体替换(连同其 Requirement 与 Handler),见
/// <c>SmartAdminSetup.AddSmartAdmin</c>。</para>
/// </summary>
public class ScalarAccessAuthorizationHandler : AuthorizationHandler<ScalarAccessRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScalarAccessRequirement requirement)
    {
        // 没有监听者时 StartActivity 返回 null,这一整段的开销就是一次判空(与 RolePermissionAttribute 共用同一份追踪)。
        using var activity = SmartAdminDiagnostics.StartActivity("smartadmin.authorize");

        // 策略以 HttpContext 为资源被调用(授权中间件传入);拿不到就无从判权限码,直接不 Succeed = 拒。
        if (context.Resource is not HttpContext httpContext)
        {
            Record(activity, "deny");
            return;
        }
        var user = context.User;

        // 1. 必须已通过认证
        if (!IsAuthenticated(user))
        {
            Record(activity, "unauthenticated");
            return;
        }

        // 2. 会话状态校验(强退即时生效),超管同样受此约束
        if (!await IsSessionActiveAsync(user, httpContext))
        {
            Record(activity, "session-dead");
            return;
        }

        // 3. 超管直接放行(claim 随令牌下发,零查库)
        if (IsSuperAdmin(user))
        {
            Record(activity, "allow");
            context.Succeed(requirement);
            return;
        }

        // 4. 权限码 = 规范化路由,与用户权限码集合比对
        if (!long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
        {
            Record(activity, "deny");
            return;
        }
        var code = BuildPermissionCode(httpContext);
        activity?.SetTag("smartadmin.permission_code", code);
        if (await HasPermissionAsync(httpContext, userId, code))
        {
            Record(activity, "allow");
            context.Succeed(requirement);
        }
        else
        {
            Record(activity, "deny");
        }
    }

    /// <summary>把判定结果同时记进 span 标签与计数器,与 <see cref="RolePermissionAttribute"/> 共用同一份指标。</summary>
    private static void Record(System.Diagnostics.Activity? activity, string result)
    {
        activity?.SetTag("smartadmin.result", result);
        SmartAdminDiagnostics.Authorizations.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    /// <summary>已通过认证(JWT 签名与有效期已在认证中间件校验)。</summary>
    protected virtual bool IsAuthenticated(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true;

    /// <summary>超管标记来自令牌 claim,零查库。</summary>
    protected virtual bool IsSuperAdmin(ClaimsPrincipal user) => user.HasClaim(TokenClaimNames.SUPER_ADMIN, "true");

    /// <summary>经 API Key 认证的机器主体:没有会话,跳过会话校验;权限仍按它绑定的用户判。</summary>
    protected virtual bool IsApiKeyPrincipal(ClaimsPrincipal user) => user.HasClaim(c => c.Type == TokenClaimNames.API_KEY);

    /// <summary>会话仍活跃(强退/登出后即时失效),规则与 <see cref="RolePermissionAttribute.IsSessionActiveAsync"/> 一致:
    /// 机器主体没有会话,直接视为活跃;否则没有 sid 视为会话已失效。</summary>
    protected virtual async Task<bool> IsSessionActiveAsync(ClaimsPrincipal user, HttpContext httpContext)
    {
        if (IsApiKeyPrincipal(user)) return true;

        var sessionId = user.FindFirstValue(TokenClaimNames.SESSION_ID);
        if (string.IsNullOrEmpty(sessionId)) return false;

        var sessions = httpContext.RequestServices.GetRequiredService<ISessionService>();
        return await sessions.IsActiveAsync(sessionId);
    }

    /// <summary>本请求对应的权限码(规范化规则见 <see cref="PermissionCode"/>——与 MVC 侧共用同一真源)。
    /// Minimal API 端点取 <see cref="RouteEndpoint.RoutePattern"/> 的原文模板,而不是已填好参数的实际路径。</summary>
    protected virtual string BuildPermissionCode(HttpContext httpContext)
    {
        var template = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return PermissionCode.Build(httpContext.Request.Method, template ?? httpContext.Request.Path.Value);
    }

    /// <summary>该用户是否持有这个权限码。</summary>
    protected virtual async Task<bool> HasPermissionAsync(HttpContext httpContext, long userId, string code)
    {
        var codes = await httpContext.RequestServices.GetRequiredService<IPermissionProvider>()
            .GetPermissionCodesAsync(userId, httpContext.RequestAborted);
        return codes.Contains(code);
    }
}
