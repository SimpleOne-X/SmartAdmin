using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 租户隔离全局过滤器——直压 SqlSugar 层。与 DataScopeTests 的机构范围测试刻意不同:
/// 这里没有"数据范围=全部"那样的整体逃逸开关(见 ITenantScoped 接口注释),所以不需要、也不应该
/// 测"某个标志位=true 时能看到全部"这种用例。
/// </summary>
public class TenantScopeFilterTests
{
    private static async Task<ServiceProvider> BuildProvider(string id, string dbFile, long? tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AdminCacheOptions());
        services.AddSingleton<ICurrentUser>(new StubTenantUser(tenantId));
        services.AddSmartAdminSqlSugar(
            new AdminDatabaseOptions { DbType = TestDb.DbType, ConnectionString = TestDb.ConnectionString(id, dbFile) },
            [typeof(ServicesSetup).Assembly]);
        services.AddSmartAdminServices();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISqlSugarClient>().CodeFirst.InitTables(typeof(TenantScopeDoc));
        return sp;
    }

    [Fact]
    public async Task Tenant_filter_isolates_rows_with_no_override_and_hides_unbackfilled_legacy_rows()
    {
        var id = $"tenantscope-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");

        await using (var spSeed = await BuildProvider(id, dbFile, tenantId: null))
        {
            using var scope = spSeed.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>();
            await repo.InsertRangeAsync(
            [
                new() { Title = "A-doc", TenantId = 10 },
                new() { Title = "B-doc", TenantId = 20 },
                new() { Title = "Legacy-doc", TenantId = null },   // 模拟升级补列后、还没被 TenantBackfillHook 回填的老行
            ]);
        }

        await using (var spA = await BuildProvider(id, dbFile, tenantId: 10))
        {
            using var scope = spA.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>().AsQueryable().ToListAsync();
            var only = Assert.Single(rows);
            Assert.Equal("A-doc", only.Title);
        }

        await using (var spB = await BuildProvider(id, dbFile, tenantId: 20))
        {
            using var scope = spB.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>().AsQueryable().ToListAsync();
            var only = Assert.Single(rows);
            Assert.Equal("B-doc", only.Title);
        }

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task TenantId_is_filled_from_current_user_tenant_on_insert()
    {
        var id = $"tenantfill-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");

        await using var sp = await BuildProvider(id, dbFile, tenantId: 77);
        using (var scope = sp.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>();
            await repo.InsertAsync(new TenantScopeDoc { Title = "auto-filled" });   // 不显式设 TenantId
            var saved = await repo.AsQueryable().Where(d => d.Title == "auto-filled").FirstAsync();
            Assert.Equal(77, saved.TenantId);
        }

        TestDb.Cleanup(id, dbFile);
    }

    [SugarTable("tenant_scope_doc")]
    public class TenantScopeDoc : TenantEntity
    {
        [SugarColumn(Length = 64)] public string Title { get; set; } = "";
    }

    private sealed class StubTenantUser(long? tenantId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long? UserId => 1;
        public string? SessionId => null;
        public bool IsSuperAdmin => false;
        public long? OrgId => null;
        public long? TenantId => tenantId;
        public bool IsPlatformAdmin => false;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
