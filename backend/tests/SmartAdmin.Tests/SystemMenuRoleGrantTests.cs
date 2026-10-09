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

    /// <summary>删除不可恢复,所以每一行授权一条 Warning(角色名、编码、菜单标题、菜单 Id),末尾再有一条汇总。</summary>
    [Fact]
    public async Task Cleanup_logs_one_warning_per_removed_grant()
    {
        using var f = new AdminAppFactory();
        await GrantTestKit.CreateRoleAsync(f, [Ping, ConfigQueryButton, BizWorkbench]);
        var logger = new CapturingLogger<SystemMenuRoleGrantCleanup>();

        using (var s = f.Services.CreateScope())
        {
            var sp = s.ServiceProvider;
            var hook = new SystemMenuRoleGrantCleanup(
                sp.GetRequiredService<ISqlSugarClient>(), sp.GetRequiredService<ICacheProvider>(), logger);
            await hook.OnDatabaseReadyAsync(UpgradeFromSix, CancellationToken.None);
        }

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, m => m.Contains("授权测试角色") && m.Contains("grant-") && m.Contains("连通性探针") && m.Contains($"Id {Ping}"));
        Assert.Contains(warnings, m => m.Contains("授权测试角色") && m.Contains("配置-查询") && m.Contains($"Id {ConfigQueryButton}"));
        Assert.Contains(warnings, m => m.Contains("共删除 2 条") && m.Contains("涉及 1 个角色"));
        Assert.DoesNotContain(warnings, m => m.Contains("工作台"));   // 业务菜单没动,不留删除痕迹
    }

    /// <summary>没有可清的存量时不写日志、不动缓存与门户代际。</summary>
    [Fact]
    public async Task Cleanup_with_nothing_to_remove_stays_silent()
    {
        using var f = new AdminAppFactory();
        await GrantTestKit.CreateRoleAsync(f, [BizWorkbench]);
        var logger = new CapturingLogger<SystemMenuRoleGrantCleanup>();

        using (var s = f.Services.CreateScope())
        {
            var sp = s.ServiceProvider;
            var hook = new SystemMenuRoleGrantCleanup(
                sp.GetRequiredService<ISqlSugarClient>(), sp.GetRequiredService<ICacheProvider>(), logger);
            await hook.OnDatabaseReadyAsync(UpgradeFromSix, CancellationToken.None);
        }

        Assert.Empty(logger.Entries);
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
