using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 单独授权的写路径与委派守卫:对自己 / 对超管 / 范围外 / 对方是管理员 / 委派限时 / 不可转授模块,
/// 变更集语义(没提到的记录不动、先后提交互不覆盖)、保存校验与事件。
/// </summary>
public class UserMenuGrantPolicyTests
{
    private const long BizWorkbench = 110;    // 业务中心(新库种子为可转授)的工作台
    private const long SystemModule = 1, BusinessModule = 2;
    private const long OrgQuery = 211, PositionQuery = 221, Ping = 301;   // 都在系统模块(不可转授)
    private const string PingCode = "GET:/api/v1/ping";

    private static object[] None => [];

    [Fact]
    public async Task Super_admin_grants_system_menu_without_expiry_and_cache_follows()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.DoesNotContain(PingCode, await GrantTestKit.CodesOfAsync(f, target));   // 预热缓存

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));

        var row = Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));
        Assert.Null(row.ExpireTime);
        Assert.Equal(await GrantTestKit.SuperAdminIdAsync(f), row.CreateUserId);
        Assert.Contains(PingCode, await GrantTestKit.CodesOfAsync(f, target));          // 保存即失效缓存
    }

    [Fact]
    public async Task Super_admin_is_not_bound_by_the_delegated_max_days()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(365))]));
    }

    [Fact]
    public async Task Nobody_can_grant_to_themselves()
    {
        using var f = new AdminAppFactory();
        var (admin, adminId, _) = await GrantTestKit.DelegatedAdminAsync(f);
        Assert.Equal(42029, await GrantTestKit.PutGrantsAsync(admin, adminId, [GrantTestKit.Deny(BizWorkbench)]));

        var super = await GrantTestKit.SuperAdminAsync(f);
        Assert.Equal(42029, await GrantTestKit.PutGrantsAsync(super, await GrantTestKit.SuperAdminIdAsync(f), [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Super_admin_target_is_protected()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        Assert.Equal(42007, await GrantTestKit.PutGrantsAsync(admin, await GrantTestKit.SuperAdminIdAsync(f), [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Delegated_admin_cannot_grant_user_outside_data_scope()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);   // 只管技术部
        var (target, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 6);                              // 产品部
        Assert.Equal(41005, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Only_super_admin_can_grant_to_another_admin()
    {
        using var f = new AdminAppFactory();
        var (admin, _, adminRole) = await GrantTestKit.DelegatedAdminAsync(f);
        var (otherAdmin, _) = await GrantTestKit.CreateUserAsync(f, [adminRole]);

        Assert.Equal(41007, await GrantTestKit.PutGrantsAsync(admin, otherAdmin, [GrantTestKit.Deny(BizWorkbench)]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(await GrantTestKit.SuperAdminAsync(f), otherAdmin, [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Target_promoted_to_admin_locks_out_original_grantor()
    {
        using var f = new AdminAppFactory();
        var (admin, _, adminRole) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(10))]));

        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRbacService>().SetUserRolesAsync(target, [adminRole]);

        Assert.Equal(41007, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(10), "续期")]));
    }

    [Fact]
    public async Task Delegated_allow_needs_expiry_no_later_than_max_days()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(41008, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench)]));
        Assert.Equal(41008, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(91))]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(90))]));   // 上限当天可以
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(BizWorkbench)]));                                // 拒绝只会收紧,不要求限时
    }

    [Fact]
    public async Task Zero_max_days_lifts_the_expiry_rule()
    {
        using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?> { ["SmartAdmin:Security:DelegatedGrantMaxDays"] = "0" },
        };
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench)]));
    }

    [Fact]
    public async Task Zero_max_days_sets_no_upper_bound_either()
    {
        using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?> { ["SmartAdmin:Security:DelegatedGrantMaxDays"] = "0" },
        };
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(3650))]));
    }

    [Fact]
    public async Task Delegated_admin_cannot_add_modify_or_remove_system_module_records()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping), GrantTestKit.Deny(PositionQuery)]));

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(OrgQuery)]));                                // 新增
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(Ping, GrantTestKit.EndOfDay(5), "改备注")]));   // 修改
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, None, [PositionQuery]));                                        // 移除
        Assert.Equal(2, (await GrantTestKit.GrantRowsAsync(f, target)).Count);
    }

    [Fact]
    public async Task Delegated_admin_grants_business_menu_he_does_not_hold()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);   // 角色只有「用户-授权菜单」
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(30))]));

        var modules = await GrantTestKit.ModuleIdsAsync(await GrantTestKit.LoginAsync(f, account));
        Assert.Equal([BusinessModule], modules);
    }

    [Fact]
    public async Task Business_change_saves_while_super_admin_system_record_stays_untouched()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var (admin, adminId, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Deny(PositionQuery)]));
        var before = Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5))]));

        var rows = (await GrantTestKit.GrantRowsAsync(f, target)).ToDictionary(g => g.MenuId);
        Assert.Equal(superId, rows[PositionQuery].CreateUserId);          // 变更集之外的记录原样
        Assert.Equal(before.CreateTime, rows[PositionQuery].CreateTime);
        Assert.Equal(before.UpdateTime, rows[PositionQuery].UpdateTime);
        Assert.Equal(adminId, rows[BizWorkbench].CreateUserId);           // 授权人就是建这一行的人
    }

    [Fact]
    public async Task Two_admins_saving_in_turn_do_not_overwrite_each_other()
    {
        using var f = new AdminAppFactory();
        var (admin1, id1, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (admin2, id2, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        var (_, bizPage) = await GrantTestKit.CreateCatalogWithPageAsync(f, BusinessModule);

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin1, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5))]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin2, target, [GrantTestKit.Deny(bizPage)]));

        var rows = (await GrantTestKit.GrantRowsAsync(f, target)).ToDictionary(g => g.MenuId);
        Assert.Equal(id1, rows[BizWorkbench].CreateUserId);
        Assert.Equal(id2, rows[bizPage].CreateUserId);
    }

    [Fact]
    public async Task Module_without_flag_is_not_delegatable()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var moduleId = (await (await super.PostJson("/api/v1/sys/module/add", new { code = "crm", title = "客户", sort = 5, enabled = true })).ReadEnvelope())
            .GetProperty("data").GetInt64();
        var (catalog, _) = await GrantTestKit.CreateCatalogWithPageAsync(f, moduleId);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(catalog)]));
    }

    [Fact]
    public async Task Builtin_system_module_stays_non_delegatable_even_if_the_column_says_true()
    {
        using var f = new AdminAppFactory();
        using (var scope = f.Services.CreateScope())   // 绕过服务直接写库,模拟被人改过的列值
            await scope.ServiceProvider.GetRequiredService<IRepository<SysModule>>().Db
                .Updateable<SysModule>().SetColumns(m => m.IsDelegatable == true)
                .Where(m => m.Id == SystemModule).ExecuteCommandAsync();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(OrgQuery)]));
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }

    [Fact]
    public async Task Closing_module_flag_keeps_existing_grants_but_blocks_edits()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5))]));

        await super.PutJson("/api/v1/sys/module/2", new
        {
            code = "business", title = "业务中心", icon = "lucide:briefcase-business", defaultRoute = "", apiPrefix = "biz",
            sort = 2, enabled = true, remark = "示例业务应用(可删除)", isDelegatable = false,
        });

        var modules = await GrantTestKit.ModuleIdsAsync(await GrantTestKit.LoginAsync(f, account));
        Assert.Equal([BusinessModule], modules);   // 关开关不是撤销
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5), "续期")]));
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, None, [BizWorkbench]));
    }

    [Fact]
    public async Task Menu_moved_into_non_delegatable_module_locks_delegated_record()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (catalog, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, BusinessModule);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(page, GrantTestKit.EndOfDay(5))]));

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var c = (await sp.GetRequiredService<IRepository<SysMenu>>().GetByIdAsync(catalog))!;
            await sp.GetRequiredService<IMenuService>().UpdateAsync(catalog, new MenuInput
            {
                ParentId = 0, Type = c.Type, Title = c.Title, Permission = c.Permission, Sort = c.Sort, Enabled = c.Enabled,
                ModuleId = 1, Path = c.Path, Component = c.Component, Icon = c.Icon, Visible = c.Visible,
            });
        }

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(page, GrantTestKit.EndOfDay(5), "续期")]));
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, None, [page]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, None, [page]));   // 超管照样能收拾
    }

    [Fact]
    public async Task Menu_under_a_disabled_directory_in_a_delegatable_module_is_still_grantable()
    {
        using var f = new AdminAppFactory();
        var (catalog, _) = await GrantTestKit.CreateCatalogWithPageAsync(f, BusinessModule);
        long disabledMiddle, page;
        using (var scope = f.Services.CreateScope())
        {
            var menus = scope.ServiceProvider.GetRequiredService<IMenuService>();
            // 模块归属沿 ParentId 上溯到根目录;中间这一层停用,也不能让上溯断链
            disabledMiddle = await menus.CreateAsync(new MenuInput
            {
                ParentId = catalog, Type = MenuType.Catalog, Title = "停用的中间目录", Permission = "", Sort = 2, Enabled = false, Visible = true,
            });
            page = await menus.CreateAsync(new MenuInput
            {
                ParentId = disabledMiddle, Type = MenuType.Menu, Title = "停用目录下的页面", Permission = "", Sort = 1, Enabled = true,
                Path = "/grant-test/" + Guid.NewGuid().ToString("N")[..8], Component = "dashboard/biz", Visible = true,
            });
        }
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(page, GrantTestKit.EndOfDay(5))]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(disabledMiddle)]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, None, [page]));
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));
    }

    [Fact]
    public async Task Invalid_change_sets_are_rejected()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(42031, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping, GrantTestKit.Local(DateTime.Now.AddMinutes(-5)))]));
        Assert.Equal(42031, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)], [Ping]));   // 同一菜单出现两次
        Assert.Equal(42015, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(999_999)]));        // 菜单不存在
        Assert.Equal(42001, await GrantTestKit.PutGrantsAsync(super, 999_999, [GrantTestKit.Allow(Ping)]));          // 用户不存在
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }

    [Fact]
    public async Task Empty_change_set_is_a_no_op()
    {
        using var f = new AdminAppFactory();
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(await GrantTestKit.SuperAdminAsync(f), target, None));
    }

    [Fact]
    public async Task Saving_publishes_change_event_with_details()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, Ping, UserMenuEffect.Allow);
        await GrantTestKit.InsertGrantAsync(f, target, PositionQuery, UserMenuEffect.Deny);

        var received = new TaskCompletionSource<UserMenuGrantsChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = f.Services.GetRequiredService<IEventBus>().Subscribe<UserMenuGrantsChangedEvent>((e, ct) =>
        {
            if (e.UserId == target) received.TrySetResult(e);
            return Task.CompletedTask;
        });

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target,
            [GrantTestKit.Deny(BizWorkbench), GrantTestKit.Allow(Ping, remark: "排障")], [PositionQuery]));

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(await GrantTestKit.SuperAdminIdAsync(f), evt.OperatorId);
        Assert.Equal([BizWorkbench], evt.Added.Select(x => x.MenuId));
        Assert.Equal([Ping], evt.Updated.Select(x => x.MenuId));
        Assert.Equal("排障", evt.Updated[0].Remark);
        Assert.Equal([PositionQuery], evt.RemovedMenuIds);
    }
}
