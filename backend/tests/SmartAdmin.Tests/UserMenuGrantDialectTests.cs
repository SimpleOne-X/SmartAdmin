using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 单独授权里方言敏感的查询形态:可空时间比较、Contains、SELECT DISTINCT、排序分页。
/// 进 SqlServer 子集(ci.yml 与 scripts/ci-local.ps1 同一份 filter);与库无关的逻辑由其余用例在另外三种库上全量覆盖。
/// </summary>
public class UserMenuGrantDialectTests
{
    [Fact]
    public async Task Status_filters_and_expiry_queries_run_on_this_dialect()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, 301, UserMenuEffect.Allow);                             // 长期
        await GrantTestKit.InsertGrantAsync(f, target, 110, UserMenuEffect.Allow, DateTime.Now.AddDays(2));    // 将到期
        await GrantTestKit.InsertGrantAsync(f, target, 221, UserMenuEffect.Deny, DateTime.Now.AddDays(-2));    // 已过期

        Assert.Contains("GET:/api/v1/ping", await GrantTestKit.CodesOfAsync(f, target));   // 走单独授权叠加与最近到期查询
        foreach (var (status, expected) in new[] { (1, 2), (2, 1), (3, 1) })
        {
            var env = await (await super.GetAsync($"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}&Status={status}")).ReadEnvelope();
            Assert.Equal(expected, env.GetProperty("data").GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task Next_expiry_query_picks_the_earliest_future_expiry_on_this_dialect()
    {
        using var f = new AdminAppFactory();
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        var (expiredOnly, _) = await GrantTestKit.CreateUserAsync(f, []);
        var soon = DateTime.Today.AddDays(2).AddHours(10);   // 整秒:各库的时间列精度不同,不拿带小数的值比
        await GrantTestKit.InsertGrantAsync(f, target, 301, UserMenuEffect.Allow);                               // 长期
        await GrantTestKit.InsertGrantAsync(f, target, 110, UserMenuEffect.Allow, soon.AddDays(5));              // 较晚
        await GrantTestKit.InsertGrantAsync(f, target, 331, UserMenuEffect.Allow, soon);                         // 最早的未到期
        await GrantTestKit.InsertGrantAsync(f, target, 221, UserMenuEffect.Deny, DateTime.Today.AddDays(-2));    // 已过期
        await GrantTestKit.InsertGrantAsync(f, expiredOnly, 221, UserMenuEffect.Deny, DateTime.Today.AddDays(-2));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>().Db;   // 与权限聚合同一条路:仓储的 Db 逃生舱口
        Assert.Equal(soon, await UserMenuGrantQueries.NextExpiryAsync(db, target, DateTime.Now));
        Assert.Null(await UserMenuGrantQueries.NextExpiryAsync(db, expiredOnly, DateTime.Now));
    }

    [Fact]
    public async Task Menu_change_fans_out_to_every_user_with_grants()
    {
        using var f = new AdminAppFactory();
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, 301, UserMenuEffect.Allow);
        await GrantTestKit.InsertGrantAsync(f, target, 110, UserMenuEffect.Deny);
        Assert.Contains("GET:/api/v1/ping", await GrantTestKit.CodesOfAsync(f, target));   // 预热缓存

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            // 绕过服务删掉允许那一行、不失效缓存:之后只有菜单变更的扇出能让缓存失效
            await sp.GetRequiredService<IRepository<SysUserMenu>>().Db.Deleteable<SysUserMenu>()
                .Where(g => g.UserId == target && g.MenuId == 301).ExecuteCommandAsync();
            await sp.GetRequiredService<IRbacService>().InvalidatePermissionsByMenuAsync(500);   // 一个跟它无关的菜单
        }

        Assert.DoesNotContain("GET:/api/v1/ping", await GrantTestKit.CodesOfAsync(f, target));
    }
}
