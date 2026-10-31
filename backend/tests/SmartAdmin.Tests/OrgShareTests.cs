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

        Assert.Equal(["shared"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([], false, 0)));   // 空范围只能看到共享行

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
        Assert.Equal(0, await repo.AsQueryable().ClearFilter<IOrgScoped>().CountAsync());                        // 只清 IOrgScoped:清不掉共享过滤器
        Assert.Equal(1, await repo.AsQueryable().ClearFilter<IOrgScoped, IOrgShared>().CountAsync());            // 两个接口一起清才清得掉

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
        var privateParent = new FooShare { Title = "p-private", CreateOrgId = 10, IsShared = null };
        await InsertAsync(sp, parent, privateParent);
        await InsertAsync(sp,
            new FooShareChild { ParentId = parent.Id, Title = "child-null", CreateOrgId = 10, IsShared = null },
            new FooShareChild { ParentId = parent.Id, Title = "child-true", CreateOrgId = 10, IsShared = true },
            new FooShareChild { ParentId = privateParent.Id, Title = "child-of-private", CreateOrgId = 10, IsShared = true });

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([20], false, 0);
        using var scope = sp.CreateScope();
        var titles = await scope.ServiceProvider.GetRequiredService<ISqlSugarClient>()
            .Queryable<FooShare>()
            .InnerJoin<FooShareChild>((p, c) => c.ParentId == p.Id)
            .Select((p, c) => c.Title)
            .ToListAsync();

        // 父表共享行放行、私有父行被挡(child-of-private 本身是共享行,只因父表过滤才丢);
        // 子表只剩共享行(child-null 被挡),且两张表的 CreateOrgId/IsShared 列没有歧义
        Assert.Equal(["child-true"], titles);

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

    // ---- 归属判断与写守卫 ----

    private static IRepository<T> RepoOf<T>(IServiceScope scope) where T : AuditEntity, new() =>
        scope.ServiceProvider.GetRequiredService<IRepository<T>>();

    private static async Task<FooShare?> ReloadAsync(ServiceProvider sp, long rowId)
    {
        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Unrestricted;
        using var scope = sp.CreateScope();
        return await RepoOf<FooShare>(scope).GetByIdAsync(rowId);
    }

    // 每组:范围(是否不受限 / 机构集合 / 是否含仅本人 / 当前用户)× 行(归属机构 / 创建人)→ 是否归属
    [Theory]
    [InlineData(true, new long[0], false, 0L, 10L, 1L, true)]       // 不受限
    [InlineData(false, new[] { 10L }, false, 0L, 10L, 1L, true)]    // 机构在范围内
    [InlineData(false, new[] { 10L }, false, 0L, 20L, 1L, false)]   // 机构在范围外
    [InlineData(false, new[] { 10L }, false, 0L, null, 1L, false)]  // 行无归属机构
    [InlineData(false, new long[0], true, 7L, 20L, 7L, true)]       // 仅本人:创建人是自己
    [InlineData(false, new long[0], true, 7L, 20L, 8L, false)]      // 仅本人:创建人是别人
    public async Task Owns_matches_the_filter(bool unrestricted, long[] orgIds, bool includeSelf, long userId, long? rowOrg, long rowUser, bool expected)
    {
        var range = unrestricted ? DataScopeResult.Unrestricted : DataScopeResult.Restricted(orgIds, includeSelf, userId);

        Assert.Equal(expected, range.Owns(rowOrg, rowUser));

        var id = $"orgshare-owns-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        await InsertAsync(sp, new FooShare { Title = "row", CreateOrgId = rowOrg, CreateUserId = rowUser, IsShared = null });

        Assert.Equal(expected, (await ShareTitlesAsync(sp, range)).Count == 1);   // IsShared 为空时,Owns 与查询可见性必须一致

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Owner_can_update_and_delete_shared_row()
    {
        var id = $"orgshare-owner-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var row = new FooShare { Title = "route", CreateOrgId = 10, IsShared = true };
        await InsertAsync(sp, row);

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([10], false, 0);
        using var scope = sp.CreateScope();
        var repo = RepoOf<FooShare>(scope);
        row.Title = "route-2";
        Assert.Equal(1, await repo.UpdateAsync(row));
        Assert.Equal(1, await repo.DeleteAsync(row.Id));
        Assert.Null(await repo.GetByIdAsync(row.Id));

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Parent_org_scope_can_write_child_org_shared_row()
    {
        var id = $"orgshare-parent-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var row = new FooShare { Title = "route", CreateOrgId = 11, IsShared = true };
        await InsertAsync(sp, row);

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([10, 11], false, 0);   // 本机构及以下:范围含子机构
        using var scope = sp.CreateScope();
        row.Title = "route-2";
        Assert.Equal(1, await RepoOf<FooShare>(scope).UpdateAsync(row));

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Non_owner_write_to_shared_row_throws()
    {
        var id = $"orgshare-nonowner-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var row = new FooShare { Title = "route", CreateOrgId = 10, IsShared = true };
        await InsertAsync(sp, row);

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([20], false, 0);
        using var scope = sp.CreateScope();
        var repo = RepoOf<FooShare>(scope);
        Assert.NotNull(await repo.GetByIdAsync(row.Id));   // 读得到,所以静默返回 0 会被调用方当成保存成功

        row.Title = "tampered";
        Assert.Equal(ErrorCode.OrgOutOfScope, (await Assert.ThrowsAsync<AdminException>(() => repo.UpdateAsync(row))).Code);
        Assert.Equal(ErrorCode.OrgOutOfScope, (await Assert.ThrowsAsync<AdminException>(() => repo.DeleteAsync(row.Id))).Code);
        Assert.Equal(ErrorCode.OrgOutOfScope, (await Assert.ThrowsAsync<AdminException>(() => repo.HardDeleteAsync(row.Id))).Code);

        var after = await ReloadAsync(sp, row.Id);
        Assert.Equal("route", after?.Title);   // 行内容未变,也没被删

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Non_owner_write_to_private_row_returns_zero()
    {
        var id = $"orgshare-private-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var row = new FooShare { Title = "route", CreateOrgId = 10, IsShared = null };
        await InsertAsync(sp, row);

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([20], false, 0);
        using var scope = sp.CreateScope();
        var repo = RepoOf<FooShare>(scope);
        row.Title = "tampered";
        Assert.Equal(0, await repo.UpdateAsync(row));   // 私有行对他机构不可见,沿用“返回 0 行”的现有语义
        Assert.Equal(0, await repo.DeleteAsync(row.Id));
        Assert.Equal("route", (await ReloadAsync(sp, row.Id))?.Title);

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Shared_row_without_org_is_writable_only_by_creator_or_unrestricted()
    {
        var id = $"orgshare-noorgwrite-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var row = new FooShare { Title = "route", CreateOrgId = null, CreateUserId = 7, IsShared = true };
        await InsertAsync(sp, row);

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([20], false, 0);
        using (var scope = sp.CreateScope())
        {
            var ex = await Assert.ThrowsAsync<AdminException>(() => RepoOf<FooShare>(scope).UpdateAsync(row));
            Assert.Equal(ErrorCode.OrgOutOfScope, ex.Code);
        }

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([], true, 7);
        using (var scope = sp.CreateScope())
            Assert.Equal(1, await RepoOf<FooShare>(scope).UpdateAsync(row));

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Unrestricted;
        using (var scope = sp.CreateScope())
            Assert.Equal(1, await RepoOf<FooShare>(scope).UpdateAsync(row));

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Owner_edit_keeps_share_flag()
    {
        // 整行更新用读出来的实体回写,IsShared 随实体带回去,编辑不会悄悄把共享关掉
        var id = $"orgshare-keepflag-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var seed = new FooShare { Title = "route", CreateOrgId = 10, IsShared = true };
        await InsertAsync(sp, seed);

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([10], false, 0);
        using (var scope = sp.CreateScope())
        {
            var repo = RepoOf<FooShare>(scope);
            var row = await repo.GetByIdAsync(seed.Id);
            Assert.NotNull(row);
            row.Title = "route-2";
            Assert.Equal(1, await repo.UpdateAsync(row));
        }

        var after = await ReloadAsync(sp, seed.Id);
        Assert.NotNull(after);
        Assert.True(after.IsShared);
        Assert.Equal("route-2", after.Title);

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Turning_share_off_hides_row_from_other_orgs()
    {
        var id = $"orgshare-unshare-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var seed = new FooShare { Title = "route", CreateOrgId = 10, IsShared = true };
        await InsertAsync(sp, seed);
        Assert.Equal(["route"], await ShareTitlesAsync(sp, DataScopeResult.Restricted([20], false, 0)));

        sp.GetRequiredService<IDataScopeContext>().Current = DataScopeResult.Restricted([10], false, 0);
        using (var scope = sp.CreateScope())
        {
            var repo = RepoOf<FooShare>(scope);
            var row = await repo.GetByIdAsync(seed.Id);
            Assert.NotNull(row);
            row.IsShared = false;
            Assert.Equal(1, await repo.UpdateAsync(row));
        }

        Assert.Empty(await ShareTitlesAsync(sp, DataScopeResult.Restricted([20], false, 0)));

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task Repository_without_scope_context_refuses_shared_entity_writes()
    {
        var id = $"orgshare-noctx-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        await using var sp = BuildSp(id, dbFile);
        var shared = new FooShare { Title = "route", CreateOrgId = 10, IsShared = true };
        var priv = new FooPrivate { Title = "route", CreateOrgId = 10 };
        await InsertAsync(sp, shared);
        await InsertAsync(sp, priv);
        var db = sp.GetRequiredService<ISqlSugarClient>();

        var sharedRepo = new SqlSugarRepository<FooShare>(db);   // 没传 IDataScopeContext:拿不到当前范围,写操作不能静默放行
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharedRepo.UpdateAsync(shared));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sharedRepo.DeleteAsync(shared.Id));

        var privateRepo = new SqlSugarRepository<FooPrivate>(db);   // IOrgScoped 路径不需要范围上下文,行为不变
        priv.Title = "route-2";
        Assert.Equal(1, await privateRepo.UpdateAsync(priv));
        Assert.Equal(1, await privateRepo.DeleteAsync(priv.Id));

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
