using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using SqlSugar;
using SmartAdmin.Core;

namespace SmartAdmin.SqlSugar;

/// <summary>
/// <see cref="IRepository{TEntity}"/> 的 SqlSugar 默认实现。
/// <para>以开放泛型注册(<c>IRepository&lt;&gt;</c> → <c>SqlSugarRepository&lt;&gt;</c>),
/// 任意实体无需逐个注册即可注入。类 public、方法 virtual——遵循框架"继承覆写"承诺,
/// 用户可继承本类只改想改的方法,再以 TryAdd 前置注册接管。</para>
/// <para><paramref name="time"/> / <paramref name="currentUser"/> <b>必须保持可选参数</b>:本类是消费者可继承的
/// public 类型(ReplaceabilityContract 扩展点契约),加必需构造参数就是源码破坏性变更——现有 <c>: SqlSugarRepository&lt;T&gt;(db)</c>
/// 的子类会编译不过。<paramref name="time"/> 只在软删审计留痕时用到,缺省即回退系统时钟。<paramref name="currentUser"/>
/// 缺省时同样回退"无操作人",但它<b>还是</b> <see cref="InScopeAsync"/> 里 <c>ITenantScoped</c> 写路径守卫的启用判据——
/// 子类若继承本类却不把 <c>currentUser</c> 传下去,该守卫对自己的实体会悄悄失效(<c>needsCheck</c> 恒不含
/// <c>ITenantScoped</c> 那一支),不是只丢一条审计留痕那么轻。</para>
/// </summary>
public class SqlSugarRepository<TEntity>(ISqlSugarClient db, TimeProvider? time = null, ICurrentUser? currentUser = null)
    : IRepository<TEntity>
    where TEntity : AuditEntity, new()
{
    /// <inheritdoc />
    public ISqlSugarClient Db => db;

    // 编译期判定实体是否受机构数据范围约束(实现 IOrgScoped,即 DataEntity / OrgAuditEntity 子类)。
    // 用于写路径越权兜底:全局范围过滤器只作用于查询(SELECT),不作用于按主键的 Update/Delete。
    private static readonly bool IsOrgScoped = typeof(IOrgScoped).IsAssignableFrom(typeof(TEntity));

    // 编译期判定实体是否受租户隔离约束(实现 ITenantScoped,即 TenantEntity / TenantDataEntity 子类)。
    // 与 IsOrgScoped 同一类缺口:全局租户过滤器同样只作用于查询,按主键的 Update/Delete 得靠这里补上。
    // 是否真的启用检查还要看调用者有没有真实租户上下文——见 InScopeAsync。
    private static readonly bool IsTenantScoped = typeof(ITenantScoped).IsAssignableFrom(typeof(TEntity));

    // 编译期判定实体是否软删除(实现 ISoftDelete,即 BaseEntity 子类)。DeleteAsync 据此分流软删/物理删,
    // RestoreAsync 据此拒绝非软删实体。用反射而非 `default(TEntity) is ISoftDelete`——引用类型 default 为 null,恒 false。
    private static readonly bool IsSoftDelete = typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity));

    // 每个实体类型的唯一索引字符串列(运行时从 SugarIndex 元数据解析,缓存一次)
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> UniqueStringColumnsCache = new();

    /// <inheritdoc />
    public virtual ISugarQueryable<TEntity> AsQueryable() => db.Queryable<TEntity>();

    /// <summary>
    /// IOrgScoped / ITenantScoped 实体的写前范围守卫:经带全局范围/租户过滤器的查询确认目标行在当前范围内。
    /// 复用已注册的查询过滤器(<c>SqlSugarSetup</c> 的 <c>AddTableFilter&lt;IOrgScoped&gt;</c> /
    /// <c>AddTableFilter&lt;ITenantScoped&gt;</c>),不在范围内(或不存在)即返回 false,调用方据此拒写——
    /// 堵住按主键改删他机构行 / 他租户行的 IDOR。两者都不实现的普通 BaseEntity 恒真短路,无额外查询、行为不变。
    /// <para>ITenantScoped 的检查只在调用者已解出真实租户(<c>currentUser.TenantId != null</c>)时才启用:
    /// 全局租户过滤器对无租户上下文的调用者是"谁都看不见"的硬拒绝、没有逃逸开关(见 <c>SqlSugarSetup</c> 里
    /// 该过滤器的注释),原样套用到写路径会把登录 / MFA / 短信登录等预登录自助流程也一并挡住——那些流程本就
    /// 依赖显式 <c>ClearFilter&lt;ITenantScoped&gt;()</c> 读出目标行、再按主键写回同一行(<c>AuthService</c>
    /// / <c>MfaEnrollmentService</c> 里那些读取点),此时 <c>currentUser.TenantId</c> 必然是 null。
    /// 这类调用者跳过本检查不等于失去保护——目标行本就是各自审计过的显式查询解出来的,仓储层这道门从来
    /// 不是唯一防线。调用者持有真实租户时检查照常生效:目标行不属于该租户就查不到,正确拒绝跨租户 IDOR。
    /// </para>
    /// <para>查询显式 <c>ClearFilter&lt;ISoftDelete&gt;()</c>:范围/租户守卫要回答的是"这行是否属于调用者",
    /// 与"这行当前是否软删"是两件事——不清掉软删过滤器,已软删行会被判"不存在",回收站彻底删除
    /// (<c>RecycleBinType&lt;TEntity&gt;.PurgeAsync</c> → <c>HardDeleteAsync</c>,目标行此刻必然是软删态)
    /// 会被这道门误挡,0 行受影响。普通 BaseEntity(<c>IsOrgScoped</c>/<c>IsTenantScoped</c> 皆假)不受影响——
    /// 不是因为它不实现 <c>ISoftDelete</c>(<c>BaseEntity</c> 恰恰实现了),而是 <c>needsCheck</c> 恒假,
    /// 查询本身从不执行,<c>ClearFilter</c> 这一句自然也不会跑到。
    /// </para>
    /// </summary>
    protected virtual async Task<bool> InScopeAsync(long id)
    {
        var needsCheck = IsOrgScoped || (IsTenantScoped && currentUser?.TenantId != null);
        return !needsCheck || await db.Queryable<TEntity>().ClearFilter<ISoftDelete>().Where(e => e.Id == id).AnyAsync();
    }

    /// <inheritdoc />
    public virtual Task<TEntity?> GetByIdAsync(long id) =>
        db.Queryable<TEntity>().Where(e => e.Id == id).FirstAsync()!;

    /// <inheritdoc />
    public virtual Task<TEntity?> GetFirstAsync(Expression<Func<TEntity, bool>> predicate) =>
        db.Queryable<TEntity>().Where(predicate).FirstAsync()!;

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(Expression<Func<TEntity, bool>> predicate) =>
        db.Queryable<TEntity>().AnyAsync(predicate);

    /// <inheritdoc />
    public virtual Task<int> InsertAsync(TEntity entity) =>
        db.Insertable(entity).ExecuteCommandAsync();

    /// <inheritdoc />
    public virtual Task<int> InsertRangeAsync(List<TEntity> entities) =>
        db.Insertable(entities).ExecuteCommandAsync();

    /// <inheritdoc />
    public virtual async Task<int> UpdateAsync(TEntity entity)
    {
        if (!await InScopeAsync(entity.Id)) return 0;   // 越权改防护(IOrgScoped 恒触发;ITenantScoped 仅调用者有真实租户时触发)
        return await db.Updateable(entity).ExecuteCommandAsync();
    }

    /// <inheritdoc />
    public virtual async Task<int> DeleteAsync(long id)
    {
        if (!await InScopeAsync(id)) return 0;   // 越权删防护(IOrgScoped 恒触发;ITenantScoped 仅调用者有真实租户时触发)

        // 非软删实体(AuditEntity 系,如 OrgAuditEntity)→ 物理删除。已过 InScope 守卫,不再走 HardDeleteAsync 二次查询。
        // 物理删后行消失,唯一约束自然释放,无需软删那套 _del_{id} 占位释放。
        if (!IsSoftDelete)
            return await db.Deleteable<TEntity>().In(id).ExecuteCommandAsync();

        var uniqueCols = GetUniqueStringColumns();
        if (uniqueCols.Length > 0)
        {
            await db.RunInTransactionAsync(async () =>
            {
                await ReleaseUniqueColumnsAsync(id, uniqueCols);
                await SoftDeleteCoreAsync(id);
            });
            return 1;
        }

        return await SoftDeleteCoreAsync(id);
    }

    /// <inheritdoc />
    public virtual async Task<int> HardDeleteAsync(long id)
    {
        if (!await InScopeAsync(id)) return 0;
        return await db.Deleteable<TEntity>().In(id).ExecuteCommandAsync();
    }

    /// <inheritdoc />
    public virtual async Task<int> RestoreAsync(long id)
    {
        // 非软删实体(AuditEntity 系)物理删不可逆,无回收站可恢复 —— 显式报错而非静默返 0。
        if (!IsSoftDelete)
            throw new NotSupportedException($"{typeof(TEntity).Name} 非软删实体(未实现 ISoftDelete),物理删除不可恢复,无 RestoreAsync 语义。");

        // 按 Id 取回(清掉软删过滤器才能看到已删行),再在内存里经 ISoftDelete 判是否确为已软删行。
        // TEntity 仅约束 AuditEntity,编译期无 e.IsDelete 成员 → 不写进 SQL 谓词(裸标识符在 PG 会大小写折叠);
        // 上面的 guard 已确保运行时 entity 必是 ISoftDelete。
        var entity = await db.Queryable<TEntity>().ClearFilter<ISoftDelete>()
            .Where(e => e.Id == id).FirstAsync();
        if (entity is not ISoftDelete { IsDelete: true }) return 0;   // 不存在 / 未删 → 无可恢复

        var uniqueCols = GetUniqueStringColumns();
        var suffix = $"_del_{id}";

        if (uniqueCols.Length > 0)
        {
            // 逆转唯一列的 _del_{id} 后缀
            foreach (var prop in uniqueCols)
            {
                var val = (string?)prop.GetValue(entity) ?? "";
                if (val.EndsWith(suffix))
                    prop.SetValue(entity, val[..^suffix.Length]);
            }
            // 冲突检查:去后缀后的值是否已被现存记录占用
            foreach (var prop in uniqueCols)
            {
                var restored = (string?)prop.GetValue(entity) ?? "";
                var colName = prop.Name;
                var conflict = await db.Queryable<TEntity>()
                    .Where(e => e.Id != id)
                    .Where($"{colName} = @v", new { v = restored })
                    .AnyAsync();
                if (conflict)
                    throw new AdminException(Core.ErrorCode.RecycleUniqueConflict);
            }

            await db.RunInTransactionAsync(async () =>
            {
                await db.Updateable(entity)
                    .UpdateColumns(uniqueCols.Select(p => p.Name).ToArray())
                    .Where(e => e.Id == id)
                    .ExecuteCommandAsync();
                await db.Updateable<TEntity>()
                    .SetColumns(nameof(ISoftDelete.IsDelete), false)
                    .Where(e => e.Id == id)
                    .ExecuteCommandAsync();
            });
            return 1;
        }

        return await db.Updateable<TEntity>()
            .SetColumns(nameof(ISoftDelete.IsDelete), false)
            .Where(e => e.Id == id)
            .ExecuteCommandAsync();
    }

    /// <summary>
    /// 软删除核心:置 IsDelete 标记 + 审计时间/操作人。提取为独立方法以便 <see cref="DeleteAsync"/> 在
    /// 需要释放唯一列时将其与释放操作包进同一个事务,不需要时则直接调用(零事务开销)。
    /// </summary>
    protected virtual async Task<int> SoftDeleteCoreAsync(long id)
    {
        // 审计两列必须在这里显式置:删也是一次更新,"谁、什么时候删的"是审计的基本要求,
        // 而按列更新(SetColumns)走的不是整对象更新路径,SqlSugarSetup 里那个只认 UpdateByObject 的
        // 审计 AOP 根本不会触发 —— 不显式写,UpdateTime/UpdateUserId 就永远停在删除之前的值。
        // 删除时间同时是文件回收任务的保留期锚点,丢了它 GC 无从判断该不该收。
        var now = (time ?? TimeProvider.System).GetLocalNow().DateTime;   // 与审计 AOP 同一时间口径
        // IsDelete 按列名写:TEntity 仅约束 AuditEntity,编译期无 e.IsDelete 成员;SoftDeleteCoreAsync 仅在 IsSoftDelete 分支被调,运行时实体确有此列。
        var update = db.Updateable<TEntity>()
          .SetColumns(nameof(ISoftDelete.IsDelete), true)
          .SetColumns(e => e.UpdateTime == now);
        if (currentUser?.UserId is { } uid)                               // 无登录上下文(系统/后台任务)则不硬塞操作人
            update = update.SetColumns(e => e.UpdateUserId == uid);

        return await update.Where(e => e.Id == id).ExecuteCommandAsync();
    }

    /// <summary>
    /// 取当前实体类型中所有受唯一索引约束的 string 列(从 <see cref="SugarIndexAttribute"/> 元数据解析,按类型缓存)。
    /// 软删前需将这些列的值追加 <c>_del_{id}</c> 后缀,释放唯一约束占位,使原值可被新行复用。
    /// </summary>
    private PropertyInfo[] GetUniqueStringColumns()
    {
        return UniqueStringColumnsCache.GetOrAdd(typeof(TEntity), _ =>
        {
            var info = db.EntityMaintenance.GetEntityInfo<TEntity>();
            // SqlSugar 对无索引实体返回 Indexs == null(而非空集),不 guard 会 NRE
            var uniquePropNames = (info.Indexs ?? [])
                .Where(idx => idx.IsUnique)
                .SelectMany(idx => idx.IndexFields.Keys)
                .ToHashSet(StringComparer.Ordinal);

            return info.Columns
                .Where(c => uniquePropNames.Contains(c.PropertyName) && c.PropertyInfo.PropertyType == typeof(string))
                .Select(c => c.PropertyInfo)
                .ToArray();
        });
    }

    /// <summary>软删前释放唯一索引字符串列:读出当前值,追加 <c>_del_{id}</c>,按列更新回去。</summary>
    private async Task ReleaseUniqueColumnsAsync(long id, PropertyInfo[] uniqueCols)
    {
        var entity = await db.Queryable<TEntity>().InSingleAsync(id);
        if (entity is null) return;

        foreach (var prop in uniqueCols)
            prop.SetValue(entity, ((string?)prop.GetValue(entity) ?? "") + $"_del_{id}");

        await db.Updateable(entity)
            .UpdateColumns(uniqueCols.Select(p => p.Name).ToArray())
            .Where(e => e.Id == id)
            .ExecuteCommandAsync();
    }
}
