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
}
