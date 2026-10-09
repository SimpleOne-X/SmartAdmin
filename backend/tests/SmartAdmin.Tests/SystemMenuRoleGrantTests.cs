using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 系统模块的菜单只能授给内置角色(种子里固定 Id 1–999 的角色)。界面上新建的角色授不了;
/// 后台代码在无登录上下文里调服务不受限(与超管专属守卫同一约定)。
/// 升级清理(<see cref="SystemMenuRoleGrantCleanup"/>)是破坏性的:这里的用例全部跑在测试自己的临时库里。
/// </summary>
public class SystemMenuRoleGrantTests
{
    private const long BuiltinRole = 1, BizWorkbench = 110, Ping = 301;
    private const long ConfigPage = 310, ConfigQueryButton = 311;   // 系统运维(300)→ 系统配置页(310)→ 配置-查询按钮(311)
    private const long SystemModule = 1, BusinessModule = 2;

    private static readonly DatabaseReadyContext UpgradeFromSix = new(true, true, true, "6", "7");

    private static async Task<int> PutRoleMenusAsync(HttpClient c, long roleId, long[] menuIds) =>
        (await (await c.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds })).ReadEnvelope()).GetProperty("code").GetInt32();

    private static async Task<long[]> MenusOfRoleAsync(AdminAppFactory f, long roleId)
    {
        using var s = f.Services.CreateScope();
        var ids = await s.ServiceProvider.GetRequiredService<IRbacService>().GetRoleMenuIdsAsync(roleId);
        return [.. ids.Order()];
    }

    /// <summary>直接改库造「中间节点停用 / 已软删」的菜单树(绕过服务,守卫与清理都得自己读全表)。</summary>
    private static async Task UpdateMenuAsync(AdminAppFactory f, long menuId, Func<ISqlSugarClient, long, Task> mutate)
    {
        using var s = f.Services.CreateScope();
        await mutate(s.ServiceProvider.GetRequiredService<ISqlSugarClient>(), menuId);
    }

    private static Task DisableMenuAsync(AdminAppFactory f, long menuId) =>
        UpdateMenuAsync(f, menuId, (db, id) => db.Updateable<SysMenu>()
            .SetColumns(x => new SysMenu { Enabled = false }).Where(x => x.Id == id).ExecuteCommandAsync());

    private static Task SoftDeleteMenuAsync(AdminAppFactory f, long menuId) =>
        UpdateMenuAsync(f, menuId, (db, id) => db.Updateable<SysMenu>()
            .SetColumns(x => new SysMenu { IsDelete = true }).Where(x => x.Id == id).ExecuteCommandAsync());

    /// <summary>取注册进 DI 的升级清理钩子(同时锁住它确实被登记)并以 <paramref name="context"/> 执行一次。</summary>
    private static async Task RunCleanupAsync(AdminAppFactory f, DatabaseReadyContext context)
    {
        using var s = f.Services.CreateScope();
        var hook = s.ServiceProvider.GetServices<IDatabaseReadyHook>().OfType<SystemMenuRoleGrantCleanup>().Single();
        await hook.OnDatabaseReadyAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task New_role_cannot_get_system_menus_but_builtin_role_can()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, []);

        Assert.Equal(41009, await PutRoleMenusAsync(super, role, [BizWorkbench, Ping]));
        Assert.Equal(0, await PutRoleMenusAsync(super, role, [BizWorkbench]));
        Assert.Equal(0, await PutRoleMenusAsync(super, BuiltinRole, [Ping]));
    }

    /// <summary>被拒的那次不能留下半截授权:整份替换没有发生,角色原有授权原样保留。</summary>
    [Fact]
    public async Task Rejected_request_leaves_existing_grants_untouched()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, [BizWorkbench]);

        Assert.Equal(41009, await PutRoleMenusAsync(super, role, [Ping]));

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Equal([BizWorkbench], menus);
    }

    /// <summary>清空授权不涉及任何系统菜单,新建角色也能清。</summary>
    [Fact]
    public async Task Clearing_menus_of_new_role_is_allowed()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, [BizWorkbench]);

        Assert.Equal(0, await PutRoleMenusAsync(super, role, []));

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Empty(menus);
    }

    /// <summary>
    /// 系统菜单与根目录之间隔着一个被停用的页面:判「属于系统模块」要读全表(含停用节点),
    /// 只读启用节点会在停用的那一层断链,把下面的按钮误判成「不属于任何模块」而放行。
    /// </summary>
    [Fact]
    public async Task Menu_below_a_disabled_directory_is_still_a_system_menu()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, []);
        await DisableMenuAsync(f, ConfigPage);

        Assert.Equal(41009, await PutRoleMenusAsync(super, role, [ConfigQueryButton]));
    }

    /// <summary>软删的系统菜单留在回收站里,恢复后会带着原来的授权回来,所以守卫同样要把它算作系统菜单。</summary>
    [Fact]
    public async Task Soft_deleted_system_menu_is_still_a_system_menu()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, []);
        await SoftDeleteMenuAsync(f, Ping);

        Assert.Equal(41009, await PutRoleMenusAsync(super, role, [Ping]));
    }

    [Fact]
    public async Task Roles_expose_builtin_flag()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, []);

        Assert.True((await (await super.GetAsync($"/api/v1/sys/role/{BuiltinRole}")).ReadEnvelope()).GetProperty("data").GetProperty("isBuiltin").GetBoolean());
        Assert.False((await (await super.GetAsync($"/api/v1/sys/role/{role}")).ReadEnvelope()).GetProperty("data").GetProperty("isBuiltin").GetBoolean());
    }

    /// <summary>内置 = 内核种子号段 1–999;消费者种子(≥ 1000)与雪花号都不是。</summary>
    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(999L, true)]
    [InlineData(1000L, false)]
    [InlineData(900_000_000_001L, false)]
    public void IsBuiltin_follows_kernel_seed_range(long id, bool expected) =>
        Assert.Equal(expected, new SysRole { Id = id }.IsBuiltin);

    [Fact]
    public async Task Background_code_without_login_is_not_restricted()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping]);   // 种子、启动任务同一约定:无登录上下文视为可信
        using var scope = f.Services.CreateScope();
        Assert.Contains(Ping, await scope.ServiceProvider.GetRequiredService<IRbacService>().GetRoleMenuIdsAsync(role));
    }

    // ───────── 升级清理钩子(直接以给定的版本现场调用;随版本重启的整条路径见 SeedUpgradeTests)─────────

    /// <summary>从版本 6 升上来:非内置角色的系统菜单(含停用目录下的)被删,业务菜单与内置角色的授权不动。</summary>
    [Fact]
    public async Task Cleanup_removes_system_menus_of_non_builtin_roles_including_below_disabled_directory()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, ConfigQueryButton, BizWorkbench]);
        await GrantTestKit.SetRoleMenusAsync(f, BuiltinRole, [Ping]);
        await DisableMenuAsync(f, ConfigPage);

        await RunCleanupAsync(f, UpgradeFromSix);

        long[] newRoleMenus = await MenusOfRoleAsync(f, role);
        long[] builtinMenus = await MenusOfRoleAsync(f, BuiltinRole);
        Assert.Equal([BizWorkbench], newRoleMenus);
        Assert.Equal([Ping], builtinMenus);
    }

    /// <summary>软删(在回收站里)的系统菜单上的授权同样要清:恢复菜单不能把授权带回来。</summary>
    [Fact]
    public async Task Cleanup_removes_grants_on_soft_deleted_system_menus()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, BizWorkbench]);
        await SoftDeleteMenuAsync(f, Ping);

        await RunCleanupAsync(f, UpgradeFromSix);

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Equal([BizWorkbench], menus);
    }

    /// <summary>只在「从低于 7 的版本升上来」那一次执行:版本不低于 7、空库(无旧版本)、非升级,都是空操作。</summary>
    [Theory]
    [InlineData(true, "7")]
    [InlineData(true, "8")]
    [InlineData(true, null)]
    [InlineData(false, "6")]
    public async Task Cleanup_is_noop_unless_upgrading_from_before_seven(bool upgraded, string? previous)
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, BizWorkbench]);

        await RunCleanupAsync(f, new DatabaseReadyContext(true, true, upgraded, previous, "7"));

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Equal([BizWorkbench, Ping], menus);
    }

    /// <summary>老版本里解析不出整数的版本号按「更早」处理,照样清。</summary>
    [Fact]
    public async Task Cleanup_treats_unparseable_previous_version_as_earlier()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, BizWorkbench]);

        await RunCleanupAsync(f, new DatabaseReadyContext(true, true, true, "0.0.1", "7"));

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Equal([BizWorkbench], menus);
    }

    /// <summary>用测试宿主的真实库与缓存、带日志捕获地跑一次清理钩子(<paramref name="make"/> 可换成覆写了某一步的子类)。</summary>
    private static async Task<CapturingLogger<SystemMenuRoleGrantCleanup>> RunLoggedCleanupAsync(
        AdminAppFactory f,
        DatabaseReadyContext context,
        Func<ISqlSugarClient, ICacheProvider, ILogger<SystemMenuRoleGrantCleanup>, SystemMenuRoleGrantCleanup>? make = null)
    {
        var logger = new CapturingLogger<SystemMenuRoleGrantCleanup>();
        using var s = f.Services.CreateScope();
        var sp = s.ServiceProvider;
        var db = sp.GetRequiredService<ISqlSugarClient>();
        var cache = sp.GetRequiredService<ICacheProvider>();
        var hook = make is null ? new SystemMenuRoleGrantCleanup(db, cache, logger) : make(db, cache, logger);
        await hook.OnDatabaseReadyAsync(context, CancellationToken.None);
        return logger;
    }

    /// <summary>系统模块下全部菜单的 Id(含停用与软删的节点,与清理的判定同口径)。</summary>
    private static async Task<long[]> SystemMenuIdsAsync(ISqlSugarClient db)
    {
        var byId = (await db.Queryable<SysMenu>().ClearFilter<ISoftDelete>().ToListAsync()).ToDictionary(m => m.Id);
        return [.. byId.Keys.Where(id => MenuTree.RootModuleId(id, byId) == SystemModule)];
    }

    /// <summary>直接插库造授权行(角色不必真实存在),分批插免得单条语句过长。</summary>
    private static async Task InsertLinksAsync(ISqlSugarClient db, IEnumerable<SysRoleMenu> links)
    {
        foreach (var chunk in links.Chunk(500))
            await db.Insertable(chunk.ToList()).ExecuteCommandAsync();
    }

    private const long FakeRoleBase = 900_000_100_000;

    /// <summary>
    /// 造足够多的系统模块授权(多个不存在的角色 × 全部系统菜单,保证要分成三批以上),外加第一个角色的一条业务菜单授权。
    /// 角色故意不建(清理按 sys_role_menu 的行删,不依赖角色表),日志里的角色名回落成 Id。返回系统授权的行数。
    /// </summary>
    private static async Task<int> SeedManySystemGrantsAsync(ISqlSugarClient db)
    {
        long[] systemMenus = await SystemMenuIdsAsync(db);
        var roleCount = 2200 / systemMenus.Length + 1;
        var links = Enumerable.Range(0, roleCount)
            .SelectMany(i => systemMenus.Select(m => new SysRoleMenu { RoleId = FakeRoleBase + i, MenuId = m }))
            .ToList();
        Assert.True(links.Count > 2 * 1000);   // 前提:确实要分成三批
        var systemGrants = links.Count;
        links.Add(new SysRoleMenu { RoleId = FakeRoleBase, MenuId = BizWorkbench });
        await InsertLinksAsync(db, links);
        return systemGrants;
    }

    private static Task<List<SysRoleMenu>> FakeRoleLinksAsync(ISqlSugarClient db) =>
        db.Queryable<SysRoleMenu>().Where(x => x.RoleId >= FakeRoleBase && x.RoleId < FakeRoleBase + 10_000).ToListAsync();

    /// <summary>授权行多到要分好几批删:一个事务里的每一批都得删净,留一条就是留一条越权入口。</summary>
    [Fact]
    public async Task Cleanup_deletes_every_grant_across_several_batches()
    {
        using var f = new AdminAppFactory();
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var systemGrants = await SeedManySystemGrantsAsync(db);

        var logger = await RunLoggedCleanupAsync(f, UpgradeFromSix);

        var only = Assert.Single(await FakeRoleLinksAsync(db));
        Assert.Equal(BizWorkbench, only.MenuId);   // 业务菜单那一条留着
        Assert.Equal(systemGrants, logger.Entries.Count(e => e.Message.Contains("升级清理删除角色")));   // 每删一行一条
    }

    /// <summary>内置与否的分界就在 999 / 1000:同样授了系统菜单,999 保留,1000(消费者种子号段)被删。</summary>
    [Fact]
    public async Task Cleanup_keeps_role_999_and_removes_role_1000()
    {
        using var f = new AdminAppFactory();
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        await db.Insertable(new List<SysRole>
        {
            new() { Id = 999, Name = "边界内置", Code = "edge-999", Enabled = true },
            new() { Id = 1000, Name = "边界消费者", Code = "edge-1000", Enabled = true },
        }).ExecuteCommandAsync();
        await InsertLinksAsync(db, [
            new SysRoleMenu { RoleId = 999, MenuId = Ping },
            new SysRoleMenu { RoleId = 1000, MenuId = Ping },
        ]);

        await RunCleanupAsync(f, UpgradeFromSix);

        long[] edge999 = await MenusOfRoleAsync(f, 999);
        long[] edge1000 = await MenusOfRoleAsync(f, 1000);
        Assert.Equal([Ping], edge999);
        Assert.Empty(edge1000);
    }

    /// <summary>软删(在回收站里)的非内置角色上的系统授权也要清,恢复角色不能把越权带回来;日志里角色名照常可读。</summary>
    [Fact]
    public async Task Cleanup_removes_system_grants_of_soft_deleted_non_builtin_role()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, BizWorkbench]);
        using (var s = f.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<ISqlSugarClient>().Updateable<SysRole>()
                .SetColumns(x => new SysRole { IsDelete = true }).Where(x => x.Id == role).ExecuteCommandAsync();

        var logger = await RunLoggedCleanupAsync(f, UpgradeFromSix);

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Equal([BizWorkbench], menus);
        Assert.Contains(logger.Entries, e => e.Message.Contains("授权测试角色") && e.Message.Contains("连通性探针"));
    }

    /// <summary>删除不可恢复,所以每一行授权一条 Warning(角色名、编码、菜单标题、菜单 Id),末尾再有一条汇总。</summary>
    [Fact]
    public async Task Cleanup_logs_one_warning_per_removed_grant()
    {
        using var f = new AdminAppFactory();
        await GrantTestKit.CreateRoleAsync(f, [Ping, ConfigQueryButton, BizWorkbench]);

        var logger = await RunLoggedCleanupAsync(f, UpgradeFromSix);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(4, warnings.Count);   // 事务前的「待删」汇总 + 两行授权 + 删完的汇总
        Assert.Contains(warnings, m => m.Contains("授权测试角色") && m.Contains("grant-") && m.Contains("连通性探针") && m.Contains($"Id {Ping}"));
        Assert.Contains(warnings, m => m.Contains("授权测试角色") && m.Contains("配置-查询") && m.Contains($"Id {ConfigQueryButton}"));
        Assert.Contains(warnings, m => m.Contains("待删除 2 条") && m.Contains("涉及 1 个角色") && m.Contains("授权测试角色"));
        Assert.Contains(warnings, m => m.Contains("共删除 2 条") && m.Contains("涉及 1 个角色"));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(warnings, m => m.Contains("工作台"));   // 业务菜单没动,不留删除痕迹
    }

    /// <summary>没有可清的存量时不写日志、不动缓存与门户代际。</summary>
    [Fact]
    public async Task Cleanup_with_nothing_to_remove_stays_silent()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [BizWorkbench]);
        var (userId, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        var cache = f.Services.GetRequiredService<ICacheProvider>();
        await cache.SetAsync(CacheKeys.UserPermissions(userId), "sentinel");   // 该用户的权限码缓存里放个哨兵
        var generation = await cache.GetAsync<long>(CacheKeys.PortalGeneration);

        var logger = await RunLoggedCleanupAsync(f, UpgradeFromSix);

        Assert.Empty(logger.Entries);
        Assert.Equal("sentinel", await cache.GetAsync<string>(CacheKeys.UserPermissions(userId)));   // 哨兵还在
        Assert.Equal(generation, await cache.GetAsync<long>(CacheKeys.PortalGeneration));            // 代际没动
    }

    /// <summary>
    /// 缓存跨重启存活(装了 Redis 时):受影响用户已缓存的权限码与门户模块必须随清理失效,
    /// 否则他们要等缓存过期才会失去系统模块入口。
    /// </summary>
    [Fact]
    public async Task Cleanup_invalidates_cached_permissions_and_portal_of_affected_users()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, BizWorkbench]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        var c = await GrantTestKit.LoginAsync(f, account);

        // 先读一遍把权限码与门户模块暖进缓存
        Assert.Contains("GET:/api/v1/ping", await GrantTestKit.CodesAsync(c));
        Assert.Contains(SystemModule, await GrantTestKit.ModuleIdsAsync(c));

        await RunCleanupAsync(f, UpgradeFromSix);

        Assert.DoesNotContain("GET:/api/v1/ping", await GrantTestKit.CodesAsync(c));
        long[] modules = await GrantTestKit.ModuleIdsAsync(c);
        Assert.DoesNotContain(SystemModule, modules);
        Assert.Contains(BusinessModule, modules);
    }

    // ───────── 失败路径:删除不可恢复,出了错要留下运维看得见的痕迹 ─────────

    /// <summary>
    /// 删除事务中途失败:整体回滚(前面已执行的批也要回到原样)、启动照常失败(异常原样抛出),
    /// 并留一条 Error 说明回滚了、没删任何授权、种子版本已前进不会重试、该怎么补救。
    /// 之所以要这条日志:版本行先于钩子写成 7,下次启动不再当升级,不留痕就是静默的越权残留。
    /// </summary>
    [Fact]
    public async Task Cleanup_delete_failure_rolls_back_logs_error_and_rethrows()
    {
        using var f = new AdminAppFactory();
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var systemGrants = await SeedManySystemGrantsAsync(db);
        var logger = new CapturingLogger<SystemMenuRoleGrantCleanup>();
        var hook = new FailOnBatchCleanup(db, s.ServiceProvider.GetRequiredService<ICacheProvider>(), logger, failOnBatch: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => hook.OnDatabaseReadyAsync(UpgradeFromSix, CancellationToken.None));

        Assert.Equal(systemGrants + 1, (await FakeRoleLinksAsync(db)).Count);   // 第一批真的执行过,仍被回滚:一条没少
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("事务已回滚", error.Message);
        Assert.Contains("没有删除任何授权", error.Message);
        Assert.Contains("不会自动重试", error.Message);
        Assert.Contains("重存", error.Message);
        Assert.IsType<InvalidOperationException>(error.Exception);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);   // 删除前的汇总;失败了就没有逐行与删完汇总
        Assert.Contains($"待删除 {systemGrants} 条", warning.Message);
    }

    /// <summary>
    /// 授权已经删了、只是缓存失效失败:只记 Error 不再抛——抛出会让启动失败,而下次启动不会重试,白白制造停机;
    /// 缓存本就有过期时间,失效失败只是旧权限多留一阵,日志里给出手工补救办法。授权删除与逐行日志照常完成。
    /// </summary>
    [Fact]
    public async Task Cleanup_cache_invalidation_failure_is_logged_not_thrown()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping, BizWorkbench]);
        await GrantTestKit.CreateUserAsync(f, [role]);   // 角色上要有用户,才会走到按用户失效权限码缓存这一步

        var logger = await RunLoggedCleanupAsync(f, UpgradeFromSix, (db, cache, log) =>
            new SystemMenuRoleGrantCleanup(db, new RemoveManyFailsCache(cache), log));   // 没抛出 = 通过这一步

        long[] menus = await MenusOfRoleAsync(f, role);
        Assert.Equal([BizWorkbench], menus);   // 授权已删
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("授权已删除", error.Message);
        Assert.Contains("缓存", error.Message);
        Assert.Contains("权限缓存", error.Message);       // 缓存管理页里对应的两张卡片:权限缓存、门户菜单缓存
        Assert.Contains("门户菜单缓存", error.Message);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("共删除 1 条"));
    }

    /// <summary>删完汇总的条数取各批删除语句实际影响的行数,不是计划数:计划里的一行被别处先删掉了,汇总就少一条。</summary>
    [Fact]
    public async Task Cleanup_summary_reports_rows_actually_deleted()
    {
        using var f = new AdminAppFactory();
        await GrantTestKit.CreateRoleAsync(f, [Ping, ConfigQueryButton, BizWorkbench]);

        var logger = await RunLoggedCleanupAsync(f, UpgradeFromSix, (db, cache, log) => new VanishingRowCleanup(db, cache, log));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("待删除 2 条"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("共删除 1 条"));
    }

    /// <summary>覆写逐批删除:执行完第 <c>failOnBatch</c> 批后抛异常,让前面的批真实执行过再整体回滚。</summary>
    private sealed class FailOnBatchCleanup(ISqlSugarClient db, ICacheProvider cache, ILogger<SystemMenuRoleGrantCleanup> logger, int failOnBatch)
        : SystemMenuRoleGrantCleanup(db, cache, logger)
    {
        private int _batches;

        protected override async Task<int> DeleteBatchAsync(IReadOnlyCollection<long> linkIds)
        {
            var deleted = await base.DeleteBatchAsync(linkIds);
            if (++_batches == failOnBatch) throw new InvalidOperationException("注入的删除失败");
            return deleted;
        }
    }

    /// <summary>覆写「找出待删授权」:返回之前先把其中第一行从库里删掉,模拟计划与实际删除之间被别处改动。</summary>
    private sealed class VanishingRowCleanup(ISqlSugarClient db, ICacheProvider cache, ILogger<SystemMenuRoleGrantCleanup> logger)
        : SystemMenuRoleGrantCleanup(db, cache, logger)
    {
        private readonly ISqlSugarClient _db = db;

        protected override async Task<StaleGrants> FindStaleGrantsAsync()
        {
            var stale = await base.FindStaleGrantsAsync();
            var firstId = stale.Links[0].Id;
            await _db.Deleteable<SysRoleMenu>().Where(x => x.Id == firstId).ExecuteCommandAsync();
            return stale;
        }
    }

    /// <summary>批量移除一律抛异常,其余透传:模拟缓存后端(如 Redis)失效时不可用。</summary>
    private sealed class RemoveManyFailsCache(ICacheProvider inner) : ICacheProvider
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => inner.GetAsync<T>(key, cancellationToken);

        public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken cancellationToken = default) =>
            inner.SetAsync(key, value, expiry, cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);

        public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("注入的缓存失效失败");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
