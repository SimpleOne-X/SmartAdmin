using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

public class TenantServiceTests
{
    private static async Task<(AdminAppFactory f, ITenantService svc, IServiceScope scope)> PlatformAdminScope()
    {
        var f = new AdminAppFactory();
        _ = f.CreateClient();
        var scope = f.Services.CreateScope();
        // TenantService 的门禁校验 ICurrentUser.IsPlatformAdmin——单测不走 HTTP,直接前置注册一个平台管理员桩顶掉 SystemCurrentUser
        return (f, scope.ServiceProvider.GetRequiredService<ITenantService>(), scope);
    }

    [Fact]
    public async Task AddAsync_creates_tenant_and_its_initial_admin_user()
    {
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(new PlatformAdminStub()) };
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();

        var id = await svc.AddAsync(new TenantCreateInput
        {
            Code = "acme", Name = "Acme Inc", IsolationMode = TenantIsolationMode.Shared,
            AdminAccount = "acme_admin", AdminPassword = "Test@123456",
        });

        var tenant = await svc.GetAsync(id);
        Assert.Equal("acme", tenant.Code);

        var admin = await users.AsQueryable().ClearFilter<ITenantScoped>().FirstAsync(u => u.Account == "acme_admin");
        Assert.NotNull(admin);
        Assert.Equal(id, admin!.TenantId);
        Assert.True(admin.IsSuperAdmin);
        Assert.True(admin.MustChangePassword);

        f.Dispose();
    }

    [Fact]
    public async Task AddAsync_rejects_standalone_isolation_mode_in_phase_one()
    {
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(new PlatformAdminStub()) };
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => svc.AddAsync(new TenantCreateInput
        {
            Code = "standalone-try", Name = "X", IsolationMode = TenantIsolationMode.Standalone,
            AdminAccount = "x_admin", AdminPassword = "Test@123456",
        }));
        Assert.Equal(ErrorCode.TenantIsolationModeNotSupported, ex.Code);
        f.Dispose();
    }

    [Fact]
    public async Task AddAsync_without_platform_admin_is_rejected()
    {
        // 默认 SystemCurrentUser.IsPlatformAdmin=false,不前置注册桩
        using var f = new AdminAppFactory();
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => svc.AddAsync(new TenantCreateInput
        {
            Code = "no-perm", Name = "X", AdminAccount = "np_admin", AdminPassword = "Test@123456",
        }));
        Assert.Equal(ErrorCode.PlatformAdminRequired, ex.Code);
    }

    [Fact]
    public async Task DeleteAsync_protects_default_tenant()
    {
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(new PlatformAdminStub()) };
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => svc.DeleteAsync(DefaultTenantSeed.DEFAULT_TENANT_ID));
        Assert.Equal(ErrorCode.TenantProtected, ex.Code);
        f.Dispose();
    }

    /// <summary>
    /// 回归:UserService.AddAsync 的账号查重必须跨租户——Task 7 挂上 ITenantScoped 过滤器前这条查重
    /// 天然扫全表;不清过滤器会静默收窄成"调用者自己租户内查重",两个不同租户各自建同名账号都能
    /// 各自通过检查,直到第二次 InsertAsync 才撞 sys_user.Account 的全局唯一索引抛原生 500——
    /// 而不是这里该有的 42006 AccountExists(ADR-0010 决策 3:账号全平台唯一,同登录 ValidateUserAsync
    /// 早已按账号跨租户查找这一件事的另一面:两个租户各建一份同名账号会让登录结果不确定)。
    /// </summary>
    [Fact]
    public async Task AddAsync_regular_user_account_is_unique_across_tenants()
    {
        var stub = new MutableCurrentUserStub { IsPlatformAdmin = true, TenantId = null };
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(stub) };
        _ = f.CreateClient();

        long tenantAId, tenantBId;
        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();
            tenantAId = await svc.AddAsync(new TenantCreateInput
            {
                Code = "cross-a", Name = "Tenant A", IsolationMode = TenantIsolationMode.Shared,
                AdminAccount = "cross_a_admin", AdminPassword = "Test@123456",
            });
            tenantBId = await svc.AddAsync(new TenantCreateInput
            {
                Code = "cross-b", Name = "Tenant B", IsolationMode = TenantIsolationMode.Shared,
                AdminAccount = "cross_b_admin", AdminPassword = "Test@123456",
            });
        }

        const string dupAccount = "cross-tenant-dup";

        // 租户 A 的管理员(非平台管理员,普通租户内超管)建一个普通用户
        stub.IsPlatformAdmin = false;
        stub.TenantId = tenantAId;
        using (var scopeA = f.Services.CreateScope())
        {
            var usersA = scopeA.ServiceProvider.GetRequiredService<IUserService>();
            await usersA.AddAsync(new AddUserInput
            {
                Account = dupAccount, Password = "Test@123456", Name = "A租户的人", Enabled = true, RoleIds = [],
            });
        }

        // 租户 B 的管理员尝试建同名账号 → 必须被 AccountExists 挡住,而不是绕过查重直到插入才撞库
        stub.TenantId = tenantBId;
        using (var scopeB = f.Services.CreateScope())
        {
            var usersB = scopeB.ServiceProvider.GetRequiredService<IUserService>();
            var ex = await Assert.ThrowsAsync<AdminException>(() => usersB.AddAsync(new AddUserInput
            {
                Account = dupAccount, Password = "Test@123456", Name = "B租户的人", Enabled = true, RoleIds = [],
            }));
            Assert.Equal(ErrorCode.AccountExists, ex.Code);
        }

        f.Dispose();
    }

    private sealed class PlatformAdminStub : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long? UserId => DefaultTenantSeed.DEFAULT_TENANT_ID;
        public string? SessionId => null;
        public bool IsSuperAdmin => true;
        public long? OrgId => null;
        public long? TenantId => DefaultTenantSeed.DEFAULT_TENANT_ID;
        public bool IsPlatformAdmin => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }

    /// <summary>
    /// 同 <see cref="PlatformAdminStub"/>,但 TenantId/IsPlatformAdmin 可变——用于同一条测试里
    /// 先以平台管理员身份建两个租户,再切到各租户内的(非平台)管理员身份分别操作。
    /// ICurrentUser 在 AdminAppFactory 内是 Singleton,同一个 stub 实例跨 scope 复用,
    /// 顺序改属性即可模拟"换一个身份发起下一个请求"(测试单线程顺序执行,无并发改写风险)。
    /// </summary>
    private sealed class MutableCurrentUserStub : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long? UserId => 1;
        public string? SessionId => null;
        public bool IsSuperAdmin => true;
        public long? OrgId => null;
        public long? TenantId { get; set; }
        public bool IsPlatformAdmin { get; set; }
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
