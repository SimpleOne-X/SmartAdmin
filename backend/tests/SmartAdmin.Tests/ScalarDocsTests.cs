using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

public class ScalarDocsTests
{
    [Fact]
    public void AdminScalarOptions_defaults_to_disabled_and_resolves_via_DI()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminOptions(new SmartAdminOptions());
        using var sp = services.BuildServiceProvider();

        var scalar = sp.GetRequiredService<AdminScalarOptions>();

        Assert.False(scalar.EnabledInProduction);
    }

    private static HttpClient WithToken(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>在宿主内造一个挂了指定权限码的临时菜单按钮 + 角色 + 用户,返回其登录账号/密码。</summary>
    private static async Task<(string account, string password)> SeedUserWithPermission(AdminAppFactory f, string permissionCode)
    {
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var menus = sp.GetRequiredService<IRepository<SysMenu>>();
        var roles = sp.GetRequiredService<IRepository<SysRole>>();
        var rbac = sp.GetRequiredService<IRbacService>();
        var users = sp.GetRequiredService<IUserService>();

        var menu = new SysMenu
        {
            ParentId = 300, Type = MenuType.Button, Title = "测试-" + permissionCode,
            Permission = permissionCode, Enabled = true, Visible = true,
        };
        await menus.InsertAsync(menu);

        // NewGuid(非 CreateVersion7):本用例内会调用本方法两次,v7 的前 8 位十六进制来自 48 位毫秒时间戳的高 32 位,
        // 同一测试运行内(远小于其 ~65s 翻动周期)两次调用会拿到相同前缀,导致 sys_role.Code 唯一约束冲突
        // (已实测触发)。NewGuid 全程随机,与 FileOwnerTests/MfaEnrollmentTests 等既有用例同一路数。
        var role = new SysRole { Name = "受限角色", Code = "limited-" + Guid.NewGuid().ToString("N")[..8], Enabled = true };
        await roles.InsertAsync(role);
        await rbac.SetRoleMenusAsync(role.Id, [menu.Id]);

        var account = "limited-" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Limited@123456";
        await users.AddAsync(new AddUserInput { Account = account, Password = password, Name = "受限用户", Enabled = true, RoleIds = [role.Id] });
        return (account, password);
    }

    [Fact]
    public async Task Development_exposes_scalar_and_openapi_anonymously()
    {
        using var f = new AdminAppFactory();   // 默认 Development
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/scalar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Production_without_opt_in_maps_neither_endpoint()
    {
        // 生产环境默认关闭 CodeFirst 建表(见 ProductionBootstrapTests);这里显式开启只是为了让空库能建表播种,
        // 从而让宿主真正起得来去验证路由,与本用例要测的"Scalar 是否挂载"无关。
        var settings = new Dictionary<string, string?> { ["SmartAdmin:Database:EnableCodeFirstInProduction"] = "true" };
        using var f = new AdminAppFactory { EnvironmentName = "Production", Settings = settings };
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/scalar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Production_opt_in_gates_openapi_json_but_leaves_scalar_shell_anonymous()
    {
        // 同上,EnableCodeFirstInProduction 只是让空库宿主能起来,与被测的 ScalarAccess 授权无关。
        var settings = new Dictionary<string, string?>
        {
            ["SmartAdmin:Scalar:EnabledInProduction"] = "true",
            ["SmartAdmin:Database:EnableCodeFirstInProduction"] = "true",
        };
        using var f = new AdminAppFactory { EnvironmentName = "Production", Settings = settings };

        // 壳页面始终匿名,不受下面的鉴权场景影响
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/scalar")).StatusCode);

        // 未登录 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/openapi/v1.json")).StatusCode);

        // 登录但权限码不对 → 403
        var wrongClient = f.CreateClient();
        var (wrongAccount, wrongPassword) = await SeedUserWithPermission(f, "GET:/api/v1/ping");
        WithToken(wrongClient, await wrongClient.LoginToken(wrongAccount, wrongPassword));
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongClient.GetAsync("/openapi/v1.json")).StatusCode);

        // 登录且权限码正确 → 200
        var rightClient = f.CreateClient();
        var (rightAccount, rightPassword) = await SeedUserWithPermission(f, "GET:/openapi/{documentname}.json");
        WithToken(rightClient, await rightClient.LoginToken(rightAccount, rightPassword));
        Assert.Equal(HttpStatusCode.OK, (await rightClient.GetAsync("/openapi/v1.json")).StatusCode);

        // 超管无视权限码 → 200
        var adminClient = f.CreateClient();
        WithToken(adminClient, await adminClient.LoginToken("superAdmin", AdminAppFactory.DefaultAdminPassword));
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/openapi/v1.json")).StatusCode);
    }

}

/// <summary>
/// <see cref="ScalarAccessAuthorizationHandler"/> 会话校验那一步的三个分支。
/// <para>为什么不走 HTTP 端到端:ScalarAccess 策略没有点名 <c>AuthenticationSchemes</c>,授权中间件因此只用
/// 默认 scheme(JwtBearer)认证,带 <c>X-Api-Key</c> 打 <c>/openapi/v1.json</c> 在认证阶段就是未认证 → 401,
/// 根本走不到本处理器的会话判定。而 JWT 一定带 sid,"没 sid 也不是机器主体"同样没有 HTTP 路径能进。
/// 两支今天都只在"消费者换掉 ITokenProvider/BuildClaims 或加第三个 scheme"时才会活过来——正是为此才要求它们
/// 与 <c>RolePermissionAttribute</c> 保持一致,所以在处理器这一层直接钉住,不为了造可达路径去伪造一个认证 scheme。</para>
/// </summary>
public class ScalarAccessHandlerBranchTests
{
    private const long USER_ID = 42L;
    private const string CODE = "GET:/openapi/v1.json";

    /// <summary>只装权限码来源,<b>故意不装 <c>ISessionService</c></b>:一旦机器主体的会话豁免没生效,
    /// <c>GetRequiredService&lt;ISessionService&gt;()</c> 会直接抛,本用例即红。</summary>
    private static ServiceProvider ProviderWithoutSessionService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionProvider>(new FixedPermissions(CODE));
        return services.BuildServiceProvider();
    }

    private static AuthorizationHandlerContext Context(ServiceProvider sp, params Claim[] claims)
    {
        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/openapi/v1.json";
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
        return new AuthorizationHandlerContext([new ScalarAccessRequirement()], user, httpContext);
    }

    /// <summary>机器主体(有 akn、没有 sid):跳过会话校验,权限仍按它绑定的用户判 → 放行。</summary>
    [Fact]
    public async Task Api_key_principal_skips_the_session_check()
    {
        using var sp = ProviderWithoutSessionService();
        var ctx = Context(sp,
            new Claim(TokenClaimNames.API_KEY, "docs"),
            new Claim(JwtRegisteredClaimNames.Sub, USER_ID.ToString(CultureInfo.InvariantCulture)));

        await new ScalarAccessAuthorizationHandler().HandleAsync(ctx);

        Assert.True(ctx.HasSucceeded);
    }

    /// <summary>既没有 sid 也不是机器主体:视为会话已失效直接拒,不许继续往下比对权限码
    /// (权限码这里是配齐的,所以一旦漏掉这道拒绝,本用例就会变成放行)。</summary>
    [Fact]
    public async Task Principal_without_session_id_is_denied_even_with_the_right_code()
    {
        using var sp = ProviderWithoutSessionService();
        var ctx = Context(sp, new Claim(JwtRegisteredClaimNames.Sub, USER_ID.ToString(CultureInfo.InvariantCulture)));

        await new ScalarAccessAuthorizationHandler().HandleAsync(ctx);

        Assert.False(ctx.HasSucceeded);
    }

    /// <summary>未认证主体连会话判定都不进。</summary>
    [Fact]
    public async Task Anonymous_principal_is_denied()
    {
        using var sp = ProviderWithoutSessionService();
        var httpContext = new DefaultHttpContext { RequestServices = sp };
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/openapi/v1.json";
        var ctx = new AuthorizationHandlerContext(
            [new ScalarAccessRequirement()], new ClaimsPrincipal(new ClaimsIdentity()), httpContext);

        await new ScalarAccessAuthorizationHandler().HandleAsync(ctx);

        Assert.False(ctx.HasSucceeded);
    }

    private sealed class FixedPermissions(params string[] codes) : IPermissionProvider
    {
        public Task<IReadOnlyCollection<string>> GetPermissionCodesAsync(long userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(codes);
    }
}
