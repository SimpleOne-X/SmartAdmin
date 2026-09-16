using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

public class DefaultTenantSeedTests
{
    [Fact]
    public async Task Fresh_start_seeds_exactly_one_protected_default_tenant()
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<IRepository<SysTenant>>();

        var all = await tenants.AsQueryable().ToListAsync();
        var seeded = Assert.Single(all);
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, seeded.Id);
        Assert.Equal("default", seeded.Code);
        Assert.Equal(TenantIsolationMode.Shared, seeded.IsolationMode);
        Assert.True(seeded.Enabled);
    }
}
