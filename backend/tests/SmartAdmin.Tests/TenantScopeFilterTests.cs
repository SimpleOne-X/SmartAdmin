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

    /// <summary>
    /// 硬化回归:过滤器谓词曾写成 <c>e.TenantId == currentUser.TenantId</c>,注释假设"两边都是 null 时
    /// SQL 的 NULL = NULL 恒非真,天然拒绝"——但实测 SqlSugar 把它翻译成 <c>TenantId IS NULL</c>,
    /// 反而会命中真存在的 NULL 行(升级补列未回填的老行)。这里直接模拟"系统上下文/无租户调用者"
    /// (<c>tenantId: null</c>)去查一张明确插了 NULL 行的表,断言看不见——不管 ORM 具体怎么翻译
    /// 第二个子句,谓词里的显式 <c>currentUser.TenantId != null</c> 前置判空必须让这类调用者恒零行。
    /// </summary>
    [Fact]
    public async Task Null_tenant_caller_never_sees_unbackfilled_legacy_rows()
    {
        var id = $"tenantscope-null-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");

        await using (var spSeed = await BuildProvider(id, dbFile, tenantId: null))
        {
            using var scope = spSeed.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>();
            await repo.InsertRangeAsync(
            [
                new() { Title = "Tenanted-doc", TenantId = 10 },
                new() { Title = "Legacy-doc", TenantId = null },   // 模拟升级补列后、还没被 TenantBackfillHook 回填的老行
            ]);
        }

        // 系统上下文/无租户调用者(currentUser.TenantId 为 null,如登录前/回填钩子出 bug/新表漏挂钩子):
        // 一行都不该看见——包括 TenantId 恰好也是 null 的那一行。
        await using var spNull = await BuildProvider(id, dbFile, tenantId: null);
        using (var scope = spNull.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>().AsQueryable().ToListAsync();
            Assert.Empty(rows);
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
