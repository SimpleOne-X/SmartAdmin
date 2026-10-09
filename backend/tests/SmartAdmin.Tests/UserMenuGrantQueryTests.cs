using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>单独授权的读路径:授权记录、有效权限与来源、单独授权一览,以及读接口的数据范围收口。</summary>
public class UserMenuGrantQueryTests
{
    private const long OrgCatalog = 200, PositionQuery = 221, RoleGrantMenus = 245, Ping = 301, MenuQuery = 331, BizWorkbench = 110;

    private static async Task<JsonElement> DataAsync(HttpClient c, string url)
    {
        var env = await (await c.GetAsync(url)).ReadEnvelope();
        Assert.Equal(0, env.GetProperty("code").GetInt32());
        return env.GetProperty("data");
    }

    private static async Task<int> CodeAsync(HttpClient c, string url) =>
        (await (await c.GetAsync(url)).ReadEnvelope()).GetProperty("code").GetInt32();

    [Fact]
    public async Task Grants_endpoint_lists_records_with_grantor()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping, remark: "排障")]));

        var item = Assert.Single((await DataAsync(super, $"/api/v1/sys/user/{target}/menus")).EnumerateArray());
        Assert.Equal(Ping, item.GetProperty("menuId").GetInt64());
        Assert.Equal(1, item.GetProperty("effect").GetInt32());
        Assert.Equal("排障", item.GetProperty("remark").GetString());
        Assert.Equal(await GrantTestKit.SuperAdminIdAsync(f), item.GetProperty("grantorId").GetInt64());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("grantorName").GetString()));
    }

    [Fact]
    public async Task Records_of_deleted_menu_are_hidden_everywhere()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(page)]));
        Assert.Equal(0, (await (await super.DeleteAsync($"/api/v1/sys/menu/{page}")).ReadEnvelope()).GetProperty("code").GetInt32());

        Assert.Empty((await DataAsync(super, $"/api/v1/sys/user/{target}/menus")).EnumerateArray());
        Assert.DoesNotContain((await DataAsync(super, $"/api/v1/sys/user/{target}/menus/effective")).GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("menuId").GetInt64() == page);
        Assert.Equal(0, (await DataAsync(super, $"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Effective_explains_sources_denials_and_leaked_codes()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, [MenuQuery, PositionQuery]);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target,
            [GrantTestKit.Allow(Ping), GrantTestKit.Deny(RoleGrantMenus), GrantTestKit.Deny(OrgCatalog)]));

        var data = await DataAsync(super, $"/api/v1/sys/user/{target}/menus/effective");
        var nodes = data.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("menuId").GetInt64());

        Assert.True(data.GetProperty("hasRoles").GetBoolean());
        Assert.True(nodes[MenuQuery].GetProperty("effective").GetBoolean());
        Assert.Single(nodes[MenuQuery].GetProperty("roles").EnumerateArray());            // 来自角色
        Assert.True(nodes[Ping].GetProperty("effective").GetBoolean());
        Assert.Equal(1, nodes[Ping].GetProperty("grant").GetInt32());                      // 来自允许
        Assert.False(nodes[PositionQuery].GetProperty("effective").GetBoolean());          // 角色授了,但目录被拒
        Assert.True(nodes[PositionQuery].GetProperty("deniedByAncestor").GetBoolean());

        // 「角色-授权菜单」被拒,但菜单树接口还由「菜单-查询」携带
        var leaked = Assert.Single(nodes[RoleGrantMenus].GetProperty("leakedCodes").EnumerateArray());
        Assert.Equal("GET:/api/v1/sys/menu/tree", leaked.GetProperty("code").GetString());
        Assert.Equal([MenuQuery], leaked.GetProperty("carrierMenuIds").EnumerateArray().Select(x => x.GetInt64()));
    }

    [Fact]
    public async Task Effective_keeps_expired_records_visible_but_inert_and_skips_disabled_roles()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var live = await GrantTestKit.CreateRoleAsync(f, [MenuQuery]);
        var stopped = await GrantTestKit.CreateRoleAsync(f, [Ping]);
        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRepository<SysRole>>().Db
                .Updateable<SysRole>().SetColumns(r => r.Enabled == false).Where(r => r.Id == stopped).ExecuteCommandAsync();
        var (target, _) = await GrantTestKit.CreateUserAsync(f, [live, stopped]);
        await GrantTestKit.InsertGrantAsync(f, target, MenuQuery, UserMenuEffect.Deny, DateTime.Now.AddDays(-1));       // 已过期的拒绝:不收回
        await GrantTestKit.InsertGrantAsync(f, target, BizWorkbench, UserMenuEffect.Allow, DateTime.Now.AddDays(-1));   // 已过期的允许:不授予

        var nodes = (await DataAsync(super, $"/api/v1/sys/user/{target}/menus/effective")).GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("menuId").GetInt64());

        var denied = nodes[MenuQuery];
        Assert.True(denied.GetProperty("effective").GetBoolean());
        Assert.Equal(2, denied.GetProperty("grant").GetInt32());                  // 记录还在,界面能看到它过期了
        Assert.True(denied.GetProperty("expired").GetBoolean());
        Assert.False(denied.GetProperty("deniedByAncestor").GetBoolean());
        Assert.Empty(denied.GetProperty("leakedCodes").EnumerateArray());        // 已过期的拒绝不提示漏网接口
        var allowed = nodes[BizWorkbench];
        Assert.False(allowed.GetProperty("effective").GetBoolean());
        Assert.True(allowed.GetProperty("expired").GetBoolean());
        Assert.False(nodes[Ping].GetProperty("effective").GetBoolean());          // 停用角色不贡献
        Assert.Empty(nodes[Ping].GetProperty("roles").EnumerateArray());          // 也不当作来源列出
    }

    [Fact]
    public async Task Effective_of_super_admin_target_is_all_effective_and_read_only()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);

        var data = await DataAsync(admin, $"/api/v1/sys/user/{await GrantTestKit.SuperAdminIdAsync(f)}/menus/effective");

        Assert.False(data.GetProperty("targetEditable").GetBoolean());
        Assert.Equal((int)ErrorCode.SuperAdminProtected, data.GetProperty("readOnlyReason").GetInt32());
        Assert.Contains(data.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("effective").GetBoolean());
    }

    [Fact]
    public async Task Effective_carries_modules_and_grantability_without_module_permission()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/sys/module/list")).StatusCode);   // 没有模块查询权限

        var data = await DataAsync(admin, $"/api/v1/sys/user/{target}/menus/effective");
        var modules = data.GetProperty("modules").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetInt64());
        Assert.False(modules[1].GetProperty("delegatable").GetBoolean());
        Assert.True(modules[2].GetProperty("delegatable").GetBoolean());
        var nodes = data.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("menuId").GetInt64());
        Assert.True(nodes[BizWorkbench].GetProperty("grantable").GetBoolean());
        Assert.False(nodes[Ping].GetProperty("grantable").GetBoolean());
        Assert.Equal(90, data.GetProperty("delegatedMaxDays").GetInt32());
        Assert.True(data.GetProperty("targetEditable").GetBoolean());
        Assert.False(data.GetProperty("hasRoles").GetBoolean());

        var asSuper = await DataAsync(await GrantTestKit.SuperAdminAsync(f), $"/api/v1/sys/user/{target}/menus/effective");
        Assert.Equal(JsonValueKind.Null, asSuper.GetProperty("delegatedMaxDays").ValueKind);
        Assert.All(asSuper.GetProperty("nodes").EnumerateArray(), n => Assert.True(n.GetProperty("grantable").GetBoolean()));
    }

    [Fact]
    public async Task Effective_marks_target_read_only_with_reason()
    {
        using var f = new AdminAppFactory();
        var (admin, adminId, adminRole) = await GrantTestKit.DelegatedAdminAsync(f);
        var (otherAdmin, _) = await GrantTestKit.CreateUserAsync(f, [adminRole]);

        var other = await DataAsync(admin, $"/api/v1/sys/user/{otherAdmin}/menus/effective");
        Assert.False(other.GetProperty("targetEditable").GetBoolean());
        Assert.Equal((int)ErrorCode.TargetIsDelegatedAdmin, other.GetProperty("readOnlyReason").GetInt32());

        var self = await DataAsync(admin, $"/api/v1/sys/user/{adminId}/menus/effective");
        Assert.Equal((int)ErrorCode.CannotOperateSelf, self.GetProperty("readOnlyReason").GetInt32());
    }

    [Fact]
    public async Task Reads_reject_users_outside_data_scope()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);
        var (outside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 6);
        Assert.Equal(41005, await CodeAsync(admin, $"/api/v1/sys/user/{outside}/menus"));
        Assert.Equal(41005, await CodeAsync(admin, $"/api/v1/sys/user/{outside}/menus/effective"));
    }

    [Fact]
    public async Task Grant_page_filters_by_status_effect_grantor_and_user()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target,
            [GrantTestKit.Allow(Ping), GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(3))]));
        await GrantTestKit.InsertGrantAsync(f, target, PositionQuery, UserMenuEffect.Deny, DateTime.Now.AddDays(-1));   // 已过期,授权人为空

        async Task<int> Total(string query) =>
            (await DataAsync(super, $"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}{query}")).GetProperty("total").GetInt32();

        Assert.Equal(3, await Total(""));
        Assert.Equal(2, await Total("&Status=1"));   // 生效中,含 7 天内到期
        Assert.Equal(1, await Total("&Status=2"));   // 7 天内到期
        Assert.Equal(1, await Total("&Status=3"));   // 已过期
        Assert.Equal(1, await Total("&Effect=2"));
        Assert.Equal(2, await Total("&Grantor=superAdmin"));
        Assert.Equal(0, (await DataAsync(super, "/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User=no-such-user")).GetProperty("total").GetInt32());

        var item = Assert.Single((await DataAsync(super, $"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}&Status=2"))
            .GetProperty("items").EnumerateArray());
        Assert.Equal(BizWorkbench, item.GetProperty("menuId").GetInt64());
        Assert.Equal(2, item.GetProperty("status").GetInt32());
        Assert.Equal(2, item.GetProperty("moduleId").GetInt64());
        Assert.Equal(account, item.GetProperty("userAccount").GetString());
    }

    [Fact]
    public async Task Grant_page_shows_delegated_admin_only_users_in_scope()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);
        var (inside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 3);
        var (outside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 6);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, inside, [GrantTestKit.Deny(BizWorkbench)]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, outside, [GrantTestKit.Deny(BizWorkbench)]));

        static List<long> UserIds(JsonElement data) => [.. data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetInt64())];
        var mine = UserIds(await DataAsync(admin, "/api/v1/sys/user/menu-grants/page?Current=1&Size=50"));
        Assert.Contains(inside, mine);
        Assert.DoesNotContain(outside, mine);

        var all = UserIds(await DataAsync(super, "/api/v1/sys/user/menu-grants/page?Current=1&Size=50"));
        Assert.Contains(inside, all);
        Assert.Contains(outside, all);
    }
}
