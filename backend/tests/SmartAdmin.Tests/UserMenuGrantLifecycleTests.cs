using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>单独授权的关联清理:软删保留、恢复照旧生效;彻底删除用户或菜单时清掉指向它们的记录。</summary>
public class UserMenuGrantLifecycleTests
{
    private const long Ping = 301;

    private static async Task<int> CodeOf(Task<HttpResponseMessage> call) => (await (await call).ReadEnvelope()).GetProperty("code").GetInt32();

    private static async Task<List<JsonElement>> RecyclePage(HttpClient c, string type) =>
        (await (await c.GetAsync($"/api/v1/sys/recycle/{type}/page?Current=1&Size=100")).ReadEnvelope())
            .GetProperty("data").GetProperty("items").EnumerateArray().ToList();

    /// <summary>不带软删过滤,按 Id 取菜单行;不存在返回 null。</summary>
    private static async Task<SysMenu?> MenuRowAsync(AdminAppFactory f, long id)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IRepository<SysMenu>>().Db
            .Queryable<SysMenu>().ClearFilter().FirstAsync(m => m.Id == id);
    }

    [Fact]
    public async Task Soft_deleted_user_keeps_grants_and_restore_brings_them_back()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/user/{target}")));
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));                                   // 软删不清
        Assert.Equal(0, await CodeOf(super.PostAsync($"/api/v1/sys/recycle/user/{target}/restore", null)));

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ping")).StatusCode);
    }

    [Fact]
    public async Task Purging_user_removes_their_grants()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/user/{target}")));
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/user/{target}")));
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }

    [Fact]
    public async Task Purging_user_keeps_other_users_grants()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        var (other, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, other, [GrantTestKit.Allow(Ping)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/user/{target}")));
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/user/{target}")));

        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, other));
    }

    [Fact]
    public async Task Purging_menu_removes_grants_pointing_at_it()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Deny(page)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/menu/{page}")));
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));                                   // 软删菜单不清
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/menu/{page}")));
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }

    /// <summary>清理只针对被清除的那个菜单:同一用户指向别的菜单的授权不动。</summary>
    [Fact]
    public async Task Purging_menu_keeps_grants_on_other_menus()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(page), GrantTestKit.Allow(Ping)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/menu/{page}")));
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/menu/{page}")));

        var left = Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));
        Assert.Equal(Ping, left.MenuId);
    }

    /// <summary>软删菜单不清授权,从回收站恢复后指向它的授权原样生效:读接口重新列出,用户的门户菜单树里重新出现。</summary>
    [Fact]
    public async Task Restored_menu_brings_its_grants_back_into_effect()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(page)]));
        var user = await GrantTestKit.LoginAsync(f, account);
        Assert.Contains(page, (await GrantTestKit.MenuTreeAsync(user, 2)).Keys);                       // 授权生效

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/menu/{page}")));
        Assert.DoesNotContain(page, (await GrantTestKit.MenuTreeAsync(user, 2)).Keys);                 // 菜单没了,树里也没了
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));                                   // 授权行还在

        Assert.Equal(0, await CodeOf(super.PostAsync($"/api/v1/sys/recycle/menu/{page}/restore", null)));

        Assert.Contains(page, (await GrantTestKit.MenuTreeAsync(user, 2)).Keys);                       // 恢复后照旧生效
        var listed = (await (await super.GetAsync($"/api/v1/sys/user/{target}/menus")).ReadEnvelope()).GetProperty("data");
        Assert.Contains(listed.EnumerateArray(), n => n.GetProperty("menuId").GetInt64() == page);
    }

    /// <summary>菜单的回收站类型换成了专用登记项,路由段与既有行为不变:列出已删菜单、恢复、彻底删除。</summary>
    [Fact]
    public async Task Menu_recycle_bin_still_lists_restores_and_purges()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);

        // 活着的菜单不在回收站里
        Assert.DoesNotContain(await RecyclePage(super, "menu"), e => e.GetProperty("id").GetInt64() == page);

        // 软删 → 列出,名称是菜单标题
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/menu/{page}")));
        var listed = Assert.Single(await RecyclePage(super, "menu"), e => e.GetProperty("id").GetInt64() == page);
        Assert.Equal("授权测试页面", listed.GetProperty("name").GetString());
        Assert.True((await MenuRowAsync(f, page))!.IsDelete);

        // 恢复 → 回到活行,回收站清空
        Assert.Equal(0, await CodeOf(super.PostAsync($"/api/v1/sys/recycle/menu/{page}/restore", null)));
        Assert.False((await MenuRowAsync(f, page))!.IsDelete);
        Assert.DoesNotContain(await RecyclePage(super, "menu"), e => e.GetProperty("id").GetInt64() == page);

        // 再删再清除 → 行真的没了
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/menu/{page}")));
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/menu/{page}")));
        Assert.Null(await MenuRowAsync(f, page));

        // 已经清掉的再清除 → 与以往一致,报找不到
        Assert.Equal((int)ErrorCode.RecycleNotFound, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/menu/{page}")));
    }
}
