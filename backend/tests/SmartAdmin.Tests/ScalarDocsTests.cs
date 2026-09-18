using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
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
