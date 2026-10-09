using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // 245 自己有生效的拒绝,祖先 200 也有(245 → 240 → 200):撤掉 245 自己的拒绝并不能恢复它,所以仍是「被祖先收回」
        Assert.Equal(2, nodes[RoleGrantMenus].GetProperty("grant").GetInt32());
        Assert.True(nodes[RoleGrantMenus].GetProperty("deniedByAncestor").GetBoolean());
        // 200 自己被拒,它之上没有任何拒绝:不是被祖先收回
        Assert.False(nodes[OrgCatalog].GetProperty("effective").GetBoolean());
        Assert.False(nodes[OrgCatalog].GetProperty("deniedByAncestor").GetBoolean());
    }

    [Fact]
    public async Task Effective_marks_only_descendants_of_a_denied_node_as_denied_by_ancestor()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, [MenuQuery, RoleGrantMenus]);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        const long roleManagePage = 240;   // 角色管理页:245 的父节点、200 的子节点
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Deny(roleManagePage)]));

        var nodes = (await DataAsync(super, $"/api/v1/sys/user/{target}/menus/effective")).GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("menuId").GetInt64());

        Assert.False(nodes[roleManagePage].GetProperty("deniedByAncestor").GetBoolean());    // 自己被拒,上面没有拒绝
        Assert.True(nodes[RoleGrantMenus].GetProperty("deniedByAncestor").GetBoolean());     // 子孙被连带收回
        Assert.False(nodes[OrgCatalog].GetProperty("deniedByAncestor").GetBoolean());        // 祖先不受影响
        Assert.False(nodes[MenuQuery].GetProperty("deniedByAncestor").GetBoolean());         // 别的分支不受影响
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

    /// <summary>
    /// 超管作为目标:全部启用节点都有效、只读展示,名下的拒绝记录不起作用。
    /// 已登录的非超管读不到超管(见 <c>SuperAdminHiddenFromAdminsTests</c>,HTTP 上得到 41005),
    /// 所以这条计算只在无登录上下文(种子、后台任务)里走得到,这里直接调服务,不经 HTTP。
    /// </summary>
    [Fact]
    public async Task Effective_of_super_admin_target_is_all_effective_and_read_only()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var (_, stoppedPage) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        long[] enabledIds;
        using (var scope = f.Services.CreateScope())
        {
            var menus = scope.ServiceProvider.GetRequiredService<IRepository<SysMenu>>();
            await menus.Db.Updateable<SysMenu>().SetColumns(m => m.Enabled == false).Where(m => m.Id == stoppedPage).ExecuteCommandAsync();
            enabledIds = [.. (await menus.AsQueryable().Where(m => m.Enabled == true).Select(m => m.Id).ToListAsync()).Order()];
        }
        // 超管名下留着一条拒绝记录(绕过服务直接写库):超管不走单独授权计算,它不能让任何节点失效
        await GrantTestKit.InsertGrantAsync(f, superId, BizWorkbench, UserMenuEffect.Deny);

        UserMenuEffectiveOutput data;
        using (var scope = f.Services.CreateScope())
            data = await scope.ServiceProvider.GetRequiredService<IUserMenuGrantService>().GetEffectiveAsync(superId);
        var nodes = data.Nodes;

        Assert.False(data.TargetEditable);
        Assert.Equal(ErrorCode.SuperAdminProtected, data.ReadOnlyReason);
        // 有效的恰好是全部启用节点:停用的那个不在内,被「拒绝」记录点名的 110 仍在
        long[] effectiveIds = [.. nodes.Where(n => n.Effective).Select(n => n.MenuId).Order()];
        Assert.Equal(enabledIds, effectiveIds);
        Assert.DoesNotContain(stoppedPage, effectiveIds);
        Assert.Contains(BizWorkbench, effectiveIds);
        Assert.All(nodes, n => Assert.False(n.DeniedByAncestor));
        Assert.All(nodes, n => Assert.Empty(n.LeakedCodes));
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
    public async Task Reads_succeed_for_users_inside_data_scope()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);
        var (inside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 3);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, inside, [GrantTestKit.Deny(BizWorkbench)]));

        var item = Assert.Single((await DataAsync(admin, $"/api/v1/sys/user/{inside}/menus")).EnumerateArray());
        Assert.Equal(BizWorkbench, item.GetProperty("menuId").GetInt64());
        var effective = await DataAsync(admin, $"/api/v1/sys/user/{inside}/menus/effective");
        Assert.True(effective.GetProperty("targetEditable").GetBoolean());
        Assert.Equal(inside, effective.GetProperty("userId").GetInt64());
    }

    [Fact]
    public async Task Reads_of_unknown_user_report_not_found()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(super, "/api/v1/sys/user/999999/menus"));
        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(super, "/api/v1/sys/user/999999/menus/effective"));
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

    /// <summary>三条授权的授权时间拉开到整天:同一秒内靠 Id 兜底的顺序在各库上精度不同,不拿它做断言。返回账号。</summary>
    private static async Task<string> ThreeGrantsOnDistinctDaysAsync(AdminAppFactory f)
    {
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        var granted = new (long MenuId, int DaysAgo)[] { (Ping, 3), (BizWorkbench, 2), (PositionQuery, 1) };   // 301 最旧,221 最新
        foreach (var (menuId, _) in granted) await GrantTestKit.InsertGrantAsync(f, target, menuId, UserMenuEffect.Allow);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>().Db;
        foreach (var (menuId, daysAgo) in granted)
        {
            var at = DateTime.Today.AddDays(-daysAgo).AddHours(10);
            await db.Updateable<SysUserMenu>().SetColumns(g => g.CreateTime == at).Where(g => g.UserId == target && g.MenuId == menuId).ExecuteCommandAsync();
        }
        return account;
    }

    /// <summary>取一览的一页,断言总数与这一页上的菜单 Id(按返回顺序)。</summary>
    private static async Task AssertGrantPageAsync(HttpClient c, string account, string query, int total, long[] menuIds)
    {
        var data = await DataAsync(c, $"/api/v1/sys/user/menu-grants/page?User={account}&{query}");
        Assert.Equal(total, data.GetProperty("total").GetInt32());
        long[] actual = [.. data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("menuId").GetInt64())];
        Assert.Equal(menuIds, actual);
    }

    [Fact]
    public async Task Grant_page_pages_newest_first_with_a_stable_total()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var account = await ThreeGrantsOnDistinctDaysAsync(f);

        await AssertGrantPageAsync(super, account, "Current=1&Size=1", 3, [PositionQuery]);
        await AssertGrantPageAsync(super, account, "Current=2&Size=1", 3, [BizWorkbench]);
        await AssertGrantPageAsync(super, account, "Current=3&Size=1", 3, [Ping]);
        await AssertGrantPageAsync(super, account, "Current=4&Size=1", 3, []);
        await AssertGrantPageAsync(super, account, "Current=2&Size=2", 3, [Ping]);
    }

    [Fact]
    public async Task Grant_page_honours_sort_field_and_ignores_unknown_ones()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var account = await ThreeGrantsOnDistinctDaysAsync(f);

        await AssertGrantPageAsync(super, account, "Current=1&Size=10&SortField=menuId&SortOrder=desc", 3, [Ping, PositionQuery, BizWorkbench]);
        await AssertGrantPageAsync(super, account, "Current=1&Size=10&SortField=MenuId&SortOrder=asc", 3, [BizWorkbench, PositionQuery, Ping]);
        // 不是实体列的排序字段被忽略,回到默认的授权时间倒序(而不是报错或拼进 SQL)
        await AssertGrantPageAsync(super, account, "Current=1&Size=10&SortField=nope;drop&SortOrder=desc", 3, [PositionQuery, BizWorkbench, Ping]);
    }

    [Fact]
    public async Task Grant_status_boundaries_follow_the_injected_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));   // 整秒:各库时间列精度不同
        using var f = new AdminAppFactory
        {
            Overrides = s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
            },
        };
        var now = clock.GetLocalNow().DateTime;
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, Ping, UserMenuEffect.Allow, now.AddDays(7));                // 恰好 7 天:7 天内到期
        await GrantTestKit.InsertGrantAsync(f, target, BizWorkbench, UserMenuEffect.Allow, now.AddDays(7).AddSeconds(1));   // 再多一秒:仍是生效中
        await GrantTestKit.InsertGrantAsync(f, target, PositionQuery, UserMenuEffect.Deny, now);                   // 到期时刻即失效:已过期
        await GrantTestKit.InsertGrantAsync(f, target, MenuQuery, UserMenuEffect.Allow, now.AddSeconds(1));        // 还差一秒:7 天内到期
        await GrantTestKit.InsertGrantAsync(f, target, RoleGrantMenus, UserMenuEffect.Allow);                      // 长期:生效中

        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserMenuGrantService>();
        async Task<Dictionary<long, UserMenuGrantStatus>> Page(UserMenuGrantStatus? status) =>
            (await service.GetGrantPageAsync(new UserMenuGrantPageInput { User = account, Size = 50, Status = status }))
                .Items.ToDictionary(i => i.MenuId, i => i.Status);

        Assert.Equal(new Dictionary<long, UserMenuGrantStatus>
        {
            [Ping] = UserMenuGrantStatus.Expiring,
            [BizWorkbench] = UserMenuGrantStatus.Active,
            [PositionQuery] = UserMenuGrantStatus.Expired,
            [MenuQuery] = UserMenuGrantStatus.Expiring,
            [RoleGrantMenus] = UserMenuGrantStatus.Active,
        }.OrderBy(x => x.Key), (await Page(null)).OrderBy(x => x.Key));
        // 筛选条件「生效中」含 7 天内到期的;「7 天内到期」「已过期」各取一档
        Assert.Equal([BizWorkbench, RoleGrantMenus, Ping, MenuQuery], (await Page(UserMenuGrantStatus.Active)).Keys.Order());
        Assert.Equal([Ping, MenuQuery], (await Page(UserMenuGrantStatus.Expiring)).Keys.Order());
        Assert.Equal([PositionQuery], (await Page(UserMenuGrantStatus.Expired)).Keys.Order());
    }

    [Fact]
    public async Task Grant_page_validates_paging_even_when_nothing_matches()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));
        const string pageUrl = "/api/v1/sys/user/menu-grants/page";

        // 超大页:有数据和没数据(用户筛选落空)一样被拒,而不是短路成一个「成功的空页」
        var withData = await CodeAsync(super, $"{pageUrl}?Current=1&Size=10001&User={account}");
        var noMatch = await CodeAsync(super, $"{pageUrl}?Current=1&Size=10001&User=no-such-user");
        Assert.Equal((int)ErrorCode.PageSizeExceeded, withData);
        Assert.Equal(withData, noMatch);

        // 页码、页大小的规整也与有数据时一致:Current=0 → 1,Size=0 → 20
        var empty = await DataAsync(super, $"{pageUrl}?Current=0&Size=0&User=no-such-user");
        Assert.Equal(1, empty.GetProperty("current").GetInt32());
        Assert.Equal(20, empty.GetProperty("size").GetInt32());
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
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
