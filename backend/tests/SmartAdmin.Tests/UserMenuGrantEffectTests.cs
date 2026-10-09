using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Core;
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

    // ── 缓存封顶:「最早到期」必须先于聚合取 ─────────────────────────────────
    // 做法:聚合刚算完(授权此刻仍生效),时钟立刻越过授权的到期时刻,模拟授权恰在两步之间到期。
    // 先取到期时刻再聚合:封顶用的到期时刻不晚于被计入的任何授权,缓存只剩 1 秒下限;
    // 先聚合再取到期时刻:查询看不到刚到期的那条,缓存按配置的整段 TTL 保留含已到期授权的结果。
    // 不睡眠、不依赖真实时间:时钟是注入的,TTL 取自写缓存时的入参。

    private static AdminAppFactory ClockedFactory(FakeTimeProvider clock) => new()
    {
        Overrides = s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(clock);
        },
    };

    private static void AssertCappedToFloor(TimeSpan? ttl) =>
        Assert.True(ttl is { } t && t <= TimeSpan.FromSeconds(1), $"缓存 TTL 应被封到 1 秒以内,实际 {ttl?.ToString() ?? "永不过期"}");

    [Fact]
    public async Task Permission_cache_ttl_is_capped_by_expiry_read_before_aggregation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var f = ClockedFactory(clock);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, clock.GetLocalNow().DateTime.AddMinutes(10));

        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cache = new TtlRecordingCache(sp.GetRequiredService<ICacheProvider>());
        var provider = new ExpiringDuringAggregationProvider(
            clock, TimeSpan.FromMinutes(11),
            sp.GetRequiredService<IRepository<SysUserRole>>(), sp.GetRequiredService<IRepository<SysRoleMenu>>(),
            sp.GetRequiredService<IRepository<SysMenu>>(), cache, sp.GetRequiredService<AdminCacheOptions>());

        var codes = await provider.GetPermissionCodesAsync(uid);

        Assert.Contains(PingCode, codes);   // 聚合时授权仍生效
        AssertCappedToFloor(cache.TtlOf(CacheKeys.UserPermissions(uid)));
    }

    [Fact]
    public async Task Portal_modules_cache_ttl_is_capped_by_expiry_read_before_aggregation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var f = ClockedFactory(clock);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, PositionQuery, UserMenuEffect.Allow, clock.GetLocalNow().DateTime.AddMinutes(10));

        using var scope = f.Services.CreateScope();
        var cache = new TtlRecordingCache(f.Services.GetRequiredService<ICacheProvider>());
        var menuService = NewExpiringMenuService(scope.ServiceProvider, clock, cache);

        var modules = await menuService.GetMyModulesAsync(uid, false);

        Assert.Equal([1L], modules.Select(m => m.Id));   // 聚合时授权仍生效
        AssertCappedToFloor(cache.TtlOf($"portal:mod:{uid}:"));
    }

    [Fact]
    public async Task Portal_menu_tree_cache_ttl_is_capped_by_expiry_read_before_aggregation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var f = ClockedFactory(clock);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, PositionQuery, UserMenuEffect.Allow, clock.GetLocalNow().DateTime.AddMinutes(10));

        using var scope = f.Services.CreateScope();
        var cache = new TtlRecordingCache(f.Services.GetRequiredService<ICacheProvider>());
        var menuService = NewExpiringMenuService(scope.ServiceProvider, clock, cache);

        var tree = await menuService.GetMyMenuTreeAsync(uid, false, 1);

        Assert.Equal([OrgCatalog], tree.Select(n => n.Id));   // 聚合时授权仍生效
        AssertCappedToFloor(cache.TtlOf($"portal:menu:{uid}:1:"));
    }

    private static ExpiringDuringAggregationMenuService NewExpiringMenuService(
        IServiceProvider sp, FakeTimeProvider clock, ICacheProvider cache) =>
        new(clock, TimeSpan.FromMinutes(11),
            sp.GetRequiredService<IRepository<SysUserRole>>(), sp.GetRequiredService<IRepository<SysRoleMenu>>(),
            sp.GetRequiredService<IRepository<SysMenu>>(), sp.GetRequiredService<IRepository<SysModule>>(),
            sp.GetRequiredService<IRbacService>(), cache, sp.GetRequiredService<AdminCacheOptions>());
}

/// <summary>记下每次写入的键与过期时长,读写仍走真实缓存:要断言的是「缓存被封到了多久」,不是桩行为。</summary>
internal sealed class TtlRecordingCache(ICacheProvider inner) : ICacheProvider
{
    private readonly ConcurrentQueue<(string Key, TimeSpan? Ttl)> _sets = new();

    /// <summary>键以 <paramref name="keyPrefix"/> 开头的那一次写入的过期时长(必须恰好一次)。</summary>
    public TimeSpan? TtlOf(string keyPrefix) =>
        _sets.Single(x => x.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).Ttl;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => inner.GetAsync<T>(key, cancellationToken);

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        _sets.Enqueue((key, expiry));
        return inner.SetAsync(key, value, expiry, cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);
}

/// <summary>聚合刚算完就把时钟推过授权的到期时刻:模拟授权恰在「聚合」与「封顶」两步之间到期。</summary>
internal sealed class ExpiringDuringAggregationProvider(
    FakeTimeProvider clock, TimeSpan advance,
    IRepository<SysUserRole> userRoles, IRepository<SysRoleMenu> roleMenus, IRepository<SysMenu> menus,
    ICacheProvider cache, AdminCacheOptions cacheOptions)
    : RbacPermissionProvider(userRoles, roleMenus, menus, cache, cacheOptions, clock)
{
    protected override async Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)
    {
        var effective = await base.ApplyUserGrantsAsync(userId, roleMenuIds);
        clock.Advance(advance);
        return effective;
    }
}

/// <summary>同上,针对门户 <see cref="MenuService"/>。</summary>
internal sealed class ExpiringDuringAggregationMenuService(
    FakeTimeProvider clock, TimeSpan advance,
    IRepository<SysUserRole> userRoles, IRepository<SysRoleMenu> roleMenus, IRepository<SysMenu> menus,
    IRepository<SysModule> modules, IRbacService rbac, ICacheProvider cache, AdminCacheOptions cacheOptions)
    : MenuService(userRoles, roleMenus, menus, modules, rbac, cache, cacheOptions, clock)
{
    protected override async Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)
    {
        var effective = await base.ApplyUserGrantsAsync(userId, roleMenuIds);
        clock.Advance(advance);
        return effective;
    }
}
