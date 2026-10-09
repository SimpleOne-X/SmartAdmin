using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 单独授权接入两处聚合点:权限码(RbacPermissionProvider)与门户(MenuService)同口径,
/// 缓存不活过授权到期,菜单挪动后拒绝的展开结果随之变化。
/// </summary>
public class UserMenuGrantEffectTests
{
    private const long OrgCatalog = 200, PositionPage = 220, PositionQuery = 221;
    private const long OpsCatalog = 300, Ping = 301, RoleGrantMenus = 245, MenuQuery = 331;
    private const string PingCode = "GET:/api/v1/ping";

    [Fact]
    public async Task Allow_gives_codes_module_and_tree_to_user_without_roles()
    {
        using var f = new AdminAppFactory();
        var (uid, account) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, PositionQuery, UserMenuEffect.Allow);

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Contains("GET:/api/v1/sys/position/page", await GrantTestKit.CodesAsync(c));
        var moduleIds = await GrantTestKit.ModuleIdsAsync(c);
        Assert.Equal([1L], moduleIds);
        var tree = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([OrgCatalog], tree[0]);
        Assert.Equal([PositionPage], tree[OrgCatalog]);
    }

    [Fact]
    public async Task Deny_on_page_revokes_page_and_buttons_and_deleting_it_restores()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionPage, PositionQuery]);
        var (uid, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.NotEmpty(await GrantTestKit.CodesAsync(c));   // 预热缓存

        await GrantTestKit.InsertGrantAsync(f, uid, PositionPage, UserMenuEffect.Deny);
        Assert.Empty(await GrantTestKit.CodesAsync(c));                     // 拒绝扩展到按钮
        Assert.Empty(await GrantTestKit.ModuleIdsAsync(c));
        Assert.Empty((await GrantTestKit.MenuTreeAsync(c, 1))[0]);          // 被拒目录不会被脚手架加回来

        await GrantTestKit.DeleteGrantAsync(f, uid, PositionPage);
        Assert.NotEmpty(await GrantTestKit.CodesAsync(c));
        var restored = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([PositionPage], restored[OrgCatalog]);
    }

    [Fact]
    public async Task Deny_is_per_node_so_shared_code_survives_on_other_node()
    {
        // GET:/api/v1/sys/menu/tree 同时挂在「角色-授权菜单」与「菜单-查询」上
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [RoleGrantMenus, MenuQuery]);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        await GrantTestKit.InsertGrantAsync(f, uid, RoleGrantMenus, UserMenuEffect.Deny);

        var codes = await GrantTestKit.CodesOfAsync(f, uid);
        Assert.Contains("GET:/api/v1/sys/menu/tree", codes);        // 仍由「菜单-查询」携带
        Assert.DoesNotContain("PUT:/api/v1/sys/role/menu", codes);  // 只在被拒节点上的码收回了
    }

    [Fact]
    public async Task Expired_grant_is_ignored()
    {
        using var f = new AdminAppFactory();
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, DateTime.Now.AddMinutes(-1));
        Assert.DoesNotContain(PingCode, await GrantTestKit.CodesOfAsync(f, uid));
    }

    [Fact]
    public async Task Expiry_is_judged_by_injected_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var f = new AdminAppFactory
        {
            Overrides = s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
            },
        };
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, clock.GetLocalNow().DateTime.AddHours(1));
        Assert.Contains(PingCode, await GrantTestKit.CodesOfAsync(f, uid));

        clock.Advance(TimeSpan.FromHours(2));
        await GrantTestKit.ResetUserCachesAsync(f, uid);
        Assert.DoesNotContain(PingCode, await GrantTestKit.CodesOfAsync(f, uid));
    }

    [Fact]
    public async Task Permission_cache_does_not_outlive_grant_expiry()
    {
        // 真实时钟:配置的权限缓存是 20 分钟,不封顶的话到期后 ping 仍放行
        using var f = new AdminAppFactory();
        var (uid, account) = await GrantTestKit.CreateUserAsync(f, []);
        var c = await GrantTestKit.LoginAsync(f, account);

        var expire = DateTime.Now.AddSeconds(5);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, expire);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ping")).StatusCode);   // 这一次把缓存写进去

        var wait = expire - DateTime.Now + TimeSpan.FromSeconds(1.5);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/ping")).StatusCode);
    }

    [Fact]
    public async Task Moving_a_page_under_denied_catalog_revokes_it_immediately()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionQuery]);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        await GrantTestKit.InsertGrantAsync(f, uid, OpsCatalog, UserMenuEffect.Deny);
        Assert.Contains("GET:/api/v1/sys/position/page", await GrantTestKit.CodesOfAsync(f, uid));   // 预热缓存

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var page = (await sp.GetRequiredService<IRepository<SysMenu>>().GetByIdAsync(PositionPage))!;
            await sp.GetRequiredService<IMenuService>().UpdateAsync(PositionPage, new MenuInput
            {
                ParentId = OpsCatalog, Type = page.Type, Title = page.Title, Permission = page.Permission,
                Sort = page.Sort, Enabled = page.Enabled, ModuleId = null,
                Path = page.Path, Component = page.Component, Icon = page.Icon, Visible = page.Visible,
            });
        }

        // 挪进被拒目录后,按钮成了被拒目录的子孙;缓存必须已被菜单更新失效
        Assert.DoesNotContain("GET:/api/v1/sys/position/page", await GrantTestKit.CodesOfAsync(f, uid));
    }
}
