using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.AspNetCore;

/// <summary>API Key 认证 scheme 的常量。</summary>
public static class ApiKeyDefaults
{
    /// <summary>scheme 名;<c>[ApiKey]</c> 即 <c>[Authorize(AuthenticationSchemes = "ApiKey")]</c>。</summary>
    public const string Scheme = "ApiKey";
}

/// <summary>
/// 机器端接入的认证 scheme:从请求头(默认 <c>X-Api-Key</c>,见 <see cref="AdminApiKeyOptions.HeaderName"/>)取 key,
/// 交 <see cref="IApiKeyValidator"/> 校验,通过即签发一个"像用户"的主体——<c>unique_name</c> 与 <c>akn</c> 是 key 的名字,
/// 绑了用户的 key 再带 <c>sub</c>,于是 <c>[RolePermission]</c>、数据范围、操作日志全部照旧复用,不另造一套授权模型。
/// <para>与 JWT 并存互不干扰:只有端点显式挂了 <c>[ApiKey]</c>(或 <c>AuthenticationSchemes</c> 点名本 scheme)才走到这里;
/// 没带头 = 无结果(叠 Bearer 的端点仍可用 JWT),带了错的 = 失败。401 / 403 都套统一信封。</para>
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminSecurityOptions security,
    IApiKeyValidator validator,
    IRepository<SysUser> users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = security.ApiKey.HeaderName;
        if (!Request.Headers.TryGetValue(header, out var values)) return AuthenticateResult.NoResult();
        var presented = values.ToString().Trim();
        if (presented.Length == 0) return AuthenticateResult.NoResult();

        var principal = await validator.ValidateAsync(presented, Context.RequestAborted);
        if (principal is null) return AuthenticateResult.Fail("API key 无效");

        var claimsPrincipal = BuildPrincipal(principal);
        if (principal.UserId is long userId)
            await AttachBoundUserClaimsAsync(claimsPrincipal, userId);

        return AuthenticateResult.Success(new AuthenticationTicket(claimsPrincipal, Scheme.Name));
    }

    /// <summary>key → ClaimsPrincipal。覆写可追加自己的 claim(如租户)。</summary>
    protected virtual ClaimsPrincipal BuildPrincipal(ApiKeyPrincipal principal)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.UniqueName, principal.Name),
            new(TokenClaimNames.API_KEY, principal.Name),
        };
        if (principal.UserId is long userId)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId.ToString(CultureInfo.InvariantCulture)));
        var identity = new ClaimsIdentity(claims, Scheme.Name, JwtRegisteredClaimNames.UniqueName, roleType: null);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// 给绑定了用户的 key 补上 tid/orgId 这组 claim——<see cref="BuildPrincipal"/> 只认得 key 本身,不查库。
    /// 这组 claim 决定了 <c>[RolePermission]</c>、数据范围、<c>ITenantScoped</c> 过滤器怎么看待这次调用:
    /// 不补 tid 的话,SysRole/SysUserRole/SysRoleMenu 等表挂了租户过滤器后,绑定用户的 key 会被过滤器
    /// 恒判"看不到自己的角色/菜单授权"——不是退化成无权限,是这把 key 彻底不能用了。
    /// <para><b>只补 tid/orgId,不补 sadm/platformAdmin</b>(与 <c>AuthService.CreateTokenAsync</c> 签发
    /// JWT 时的 <c>TokenSubject</c> 映射<b>刻意不同</b>,不要为了"对齐 JWT"顺手加回去):API Key 是写在
    /// 配置文件里的静态凭证,没有过期、没有吊销机制,也不受强退影响——给它超管绕过
    /// (<c>RolePermissionAttribute</c> 的 sadm 短路)或平台管理员权限(<c>TenantService</c> 的租户增删改)
    /// 是比"看得见自己租户内的角色/菜单授权"大得多的权限扩张,任务本身不需要,评审裁定不授予。</para>
    /// <para>停用的用户(<c>user.Enabled == false</c>)必须早退、不挂任何 claim:API Key 没有 <c>sid</c>
    /// 会话、不走 <c>ActiveSessionAttribute</c> 的强退检查,唯一能拦住"账号已停用但 key 还留着"的地方
    /// 就是这里——同 <c>AuthService</c> 登录路径(<c>CheckLoginPolicyAsync</c>)与
    /// <c>SessionService.RefreshAsync</c> 对 <c>Enabled</c> 的检查。早退而不抛异常:认证阶段抛异常会变成
    /// 500,不是预期的"当未绑定用户处理"(与下面"查不到用户"分支同一 fail-closed 风格)。</para>
    /// <para>按用户 Id 查询前必须 <c>ClearFilter&lt;ITenantScoped&gt;()</c>:这一步本身就是在确定"这个用户
    /// 属于哪个租户",此刻当前调用者还没有租户上下文,同 <c>AuthService</c> 登录路径按账号查用户那一步。
    /// 这条查询不清 <c>ISoftDelete</c>,软删用户查不到、自然早退——不要为了"对称"补上
    /// <c>ClearFilter&lt;ISoftDelete&gt;()</c>,那会让软删用户的 key 重新拿到 RBAC claim。</para>
    /// </summary>
    protected virtual async Task AttachBoundUserClaimsAsync(ClaimsPrincipal claimsPrincipal, long userId)
    {
        var user = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == userId).FirstAsync();
        if (user is null) return;
        if (!user.Enabled) return;

        var identity = (ClaimsIdentity)claimsPrincipal.Identity!;
        if (user.OrgId is { } orgId)
            identity.AddClaim(new Claim(TokenClaimNames.ORG_ID, orgId.ToString(CultureInfo.InvariantCulture)));
        if (user.TenantId is { } tenantId)
            identity.AddClaim(new Claim(TokenClaimNames.TENANT_ID, tenantId.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>401:key 缺失或无效(<see cref="ErrorCode.ApiKeyInvalid"/>),与 JWT 的 40006 分开,机器调用方一眼能分。</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        return Response.WriteAsJsonAsync(Result<object>.Fail(ErrorCode.ApiKeyInvalid), Context.RequestAborted);
    }

    /// <summary>403:统一信封 41001,与 <c>[RolePermission]</c> 的出口一致。</summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        return Response.WriteAsJsonAsync(Result<object>.Fail(ErrorCode.NoPermission), Context.RequestAborted);
    }
}
