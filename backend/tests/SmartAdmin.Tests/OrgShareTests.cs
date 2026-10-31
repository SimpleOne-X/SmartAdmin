using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 机构共享数据的读路径:<see cref="OrgAuditEntity"/> 实现 <see cref="IOrgShared"/>,
/// 其全局过滤器在机构范围之外再放行 <c>IsShared == true</c> 的行;<see cref="DataEntity"/> 不带共享能力。
/// 直压 SqlSugar 全局过滤器:先以不受限上下文写入,再切换 <see cref="IDataScopeContext.Current"/> 查询。
/// </summary>
public class OrgShareTests
{
    private static ServiceProvider BuildSp(string id, string dbFile)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AdminCacheOptions());
        services.AddSmartAdminSqlSugar(
            new AdminDatabaseOptions { DbType = TestDb.DbType, ConnectionString = TestDb.ConnectionString(id, dbFile) },
            [typeof(ServicesSetup).Assembly]);
        services.AddSmartAdminServices();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISqlSugarClient>().CodeFirst.InitTables(typeof(FooShare), typeof(FooShareChild), typeof(FooPrivate));
        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Unrestricted;   // 写入阶段不过滤
        return sp;
    }

    private static async Task InsertAsync<T>(ServiceProvider sp, params T[] rows) where T : AuditEntity, new()
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRepository<T>>().InsertRangeAsync([.. rows]);
    }

    private static async Task<List<string>> ShareTitlesAsync(ServiceProvider sp, DataScopeResult range)
    {
        sp.GetRequiredService<IDataScopeContext>().Current = range;
        using var scope = sp.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IRepository<FooShare>>().AsQueryable().ToListAsync();
        return [.. rows.Select(r => r.Title).Order()];
    }

    [Fact]
    public async Task Shared_row_is_visible_to_every_org()
    {
        var id = $"orgshare-all-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp,
            new FooShare { Title = "true", CreateOrgId = 10, IsShared = true },
            new FooShare { Title = "false", CreateOrgId = 10, IsShared = false },
            new FooShare { Title = "null", CreateOrgId = 10, IsShared = null });

        Assert.Equal(["false", "null", "true"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([10], false, 0)));   // 本机构:私有行与共享行都见
        Assert.Equal(["true"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([20], false, 0)));                    // 他机构:只见共享行,false 与 null 都不算共享

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Shared_row_without_org_is_visible_to_every_org()
    {
        var id = $"orgshare-noorg-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp, new FooShare { Title = "true", CreateOrgId = null, IsShared = true });

        Assert.Equal(["true"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([20], false, 0)));

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Unshared_row_without_org_stays_hidden()
    {
        // 存量空机构行(系统数据、无机构用户创建的数据)不能因为升级变成共享
        var id = $"orgshare-hidden-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp, new FooShare { Title = "legacy", CreateOrgId = null, IsShared = null, CreateUserId = 7 });

        Assert.Empty(await ShareTitlesAsync(sp, DataScopeResult.Restricted([20], false, 0)));
        Assert.Equal(["legacy"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([], true, 7)));   // 仅本人的创建人仍能看到

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Shared_row_is_visible_under_empty_scope()
    {
        var id = $"orgshare-empty-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp,
            new FooShare { Title = "shared", CreateOrgId = 10, IsShared = true },
            new FooShare { Title = "private", CreateOrgId = 10, IsShared = null });

        Assert.Equal(["shared"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([], false, 0)));   // 空范围不再是“什么都看不到”

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task ClearFilter_of_IOrgScoped_does_not_clear_IOrgShared()
    {
        var id = $"orgshare-clear-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp, new FooShare { Title = "other", CreateOrgId = 20, IsShared = null });
        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([10], false, 0);

        using var scope = sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<FooShare>>();
        Assert.Equal(0, await repo.AsQueryable().ClearFilter<IOrgScoped>().CountAsync());                        // 老写法清不掉共享过滤器
        Assert.Equal(1, await repo.AsQueryable().ClearFilter<IOrgScoped, IOrgShared>().CountAsync());            // 新写法才清得掉

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task DataEntity_is_not_shareable()
    {
        var id = $"orgshare-dataentity-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);

        var cols = sp.GetRequiredService<ISqlSugarClient>().EntityMaintenance.GetEntityInfo<FooPrivate>()
            .Columns.Select(c => c.PropertyName);
        Assert.DoesNotContain(nameof(IOrgShared.IsShared), cols);

        Assert.True(typeof(IOrgScoped).IsAssignableFrom(typeof(FooPrivate)));
        Assert.False(typeof(IOrgShared).IsAssignableFrom(typeof(FooPrivate)));
        Assert.True(typeof(IOrgShared).IsAssignableFrom(typeof(FooShare)));
        Assert.False(typeof(IOrgScoped).IsAssignableFrom(typeof(FooShare)));   // 不能同时实现:同一实体的多个过滤器是 AND,共享行会先被机构过滤器挡掉

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Join_of_two_shared_tables_filters_both()
    {
        var id = $"orgshare-join-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var parent = new FooShare { Title = "p", CreateOrgId = 10, IsShared = true };
        await InsertAsync(sp, parent);
        await InsertAsync(sp,
            new FooShareChild { ParentId = parent.Id, Title = "child-null", CreateOrgId = 10, IsShared = null },
            new FooShareChild { ParentId = parent.Id, Title = "child-true", CreateOrgId = 10, IsShared = true });

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([20], false, 0);
        using var scope = sp.CreateScope();
        var titles = await scope.ServiceProvider.GetRequiredService<ISqlSugarClient>()
            .Queryable<FooShare>()
            .InnerJoin<FooShareChild>((p, c) => c.ParentId == p.Id)
            .Select((p, c) => c.Title)
            .ToListAsync();

        Assert.Equal(["child-true"], titles);   // 父表共享行放行,子表只剩共享行,且两张表的 CreateOrgId/IsShared 列没有歧义

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Join_of_private_and_shared_tables_applies_both_filters()
    {
        var id = $"orgshare-mixjoin-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp,
            new FooPrivate { Title = "a", CreateOrgId = 20 },   // 主表被机构过滤器挡
            new FooPrivate { Title = "b", CreateOrgId = 10 });
        await InsertAsync(sp,
            new FooShare { Title = "a", CreateOrgId = 20, IsShared = true },
            new FooShare { Title = "b", CreateOrgId = 20, IsShared = null });   // 联表被共享过滤器挡

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([10], false, 0);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

        var blocked = await db.Queryable<FooPrivate>()
            .InnerJoin<FooShare>((p, s) => s.Title == p.Title)
            .Select((p, s) => p.Title)
            .ToListAsync();
        Assert.Empty(blocked);

        var cleared = await db.Queryable<FooPrivate>()
            .InnerJoin<FooShare>((p, s) => s.Title == p.Title)
            .ClearFilter<IOrgScoped, IOrgShared>()
            .Select((p, s) => p.Title)
            .ToListAsync();
        Assert.Equal(["a", "b"], cleared.Order());

        TestDb.Cleanup(id, dbFile);
    }

    /// <summary>可共享的机构审计实体(继承 OrgAuditEntity)。</summary>
    [SugarTable("foo_org_share")]
    public class FooShare : OrgAuditEntity
    {
        [SugarColumn(Length = 64)] public string Title { get; set; } = "";
    }

    /// <summary>引用 <see cref="FooShare"/> 的子表,同样继承 OrgAuditEntity,用来验证联表时两张表的过滤器都生效。</summary>
    [SugarTable("foo_org_share_child")]
    public class FooShareChild : OrgAuditEntity
    {
        public long ParentId { get; set; }
        [SugarColumn(Length = 64)] public string Title { get; set; } = "";
    }

    /// <summary>不可共享的机构实体(继承 DataEntity):只受 IOrgScoped 过滤器约束,表里没有 IsShared 列。</summary>
    [SugarTable("foo_org_private")]
    public class FooPrivate : DataEntity
    {
        [SugarColumn(Length = 64)] public string Title { get; set; } = "";
    }
}
