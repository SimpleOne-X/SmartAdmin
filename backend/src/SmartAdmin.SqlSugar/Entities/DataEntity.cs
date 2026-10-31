using SqlSugar;

namespace SmartAdmin.SqlSugar;

/// <summary>
/// 数据范围标记接口——全局过滤器按<b>接口</b>而非基类匹配实体
/// (SqlSugar 的 <c>AddTableFilter&lt;T&gt;</c> 匹配接口/精确类型,不匹配基类;软删过滤器同理走 <see cref="ISoftDelete"/>)。
/// 暴露锚点字段 <see cref="CreateOrgId"/>(归属机构)与 <see cref="CreateUserId"/>(创建者,供"仅本人")。
/// </summary>
public interface IOrgScoped
{
    /// <summary>归属机构 Id(数据范围锚点)</summary>
    long? CreateOrgId { get; }

    /// <summary>创建人用户 Id(“仅本人”范围据此比对;定义在 <see cref="BaseEntity"/>)</summary>
    long? CreateUserId { get; }
}

/// <summary>
/// 可按行共享给所有机构的数据范围标记接口,由 <see cref="OrgAuditEntity"/> 实现。
/// 独立于 <see cref="IOrgScoped"/>(不继承、不同时实现):同一实体上的全局过滤器是 AND 关系,
/// 若两个接口并存,共享行会先被机构过滤器挡掉;SqlSugar 按 <c>GetInterfaces()</c> 匹配,继承来的接口同样算匹配。
/// 过滤器在机构范围之外多放行 <see cref="IsShared"/> 为 true 的行,<c>null</c> 与 <c>false</c> 都表示不共享。
/// </summary>
public interface IOrgShared
{
    /// <summary>归属机构 Id(数据范围锚点)</summary>
    long? CreateOrgId { get; }

    /// <summary>创建人用户 Id(“仅本人”范围据此比对)</summary>
    long? CreateUserId { get; }

    /// <summary>是否共享给所有机构;true 时不管归属机构是否为空,所有机构都能查到</summary>
    bool? IsShared { get; }
}

// ponytail: 与 BaseEntity 一样,概念上属于 Core,但携带 Sugar 特性,所以放在本层;改成无特性 POCO 才有条件挪进 Core。
/// <summary>
/// 带机构数据范围的实体基类。业务表继承它即获得<b>数据范围锚点</b>
/// <see cref="CreateOrgId"/>,配合 <c>IDataScopeProvider</c> 全局过滤器(按 <see cref="IOrgScoped"/> 匹配),
/// 实现"本机构/本机构及以下/仅本人/自定义"隔离。
/// <para>不需要机构隔离的表(如全局字典、机构树自身)继续用 <see cref="BaseEntity"/>。</para>
/// <para><b>写路径守卫</b>:数据范围全局过滤器只作用于<b>查询(SELECT)</b>,不作用于按主键的
/// <c>Updateable</c>/<c>Deleteable</c>。为此 <see cref="SqlSugarRepository{TEntity}"/> 对 IOrgScoped 实体的
/// <c>UpdateAsync</c>/<c>DeleteAsync</c> <b>已内置写路径范围守卫</b>:写前经带范围过滤器的查询确认目标行在当前
/// 数据范围内,越权改删他机构行会被拒(返回 0 行),<b>默认安全</b>。仍建议业务服务改/删前先 <c>GetByIdAsync</c>
/// (经范围过滤)校验存在,以返回准确的"未找到/无权"(内置服务均如此)。绕过仓储直接走 <c>Db.Updateable/Deleteable</c>
/// 逃生舱口的写不受此守卫,属显式例外,需自行校验归属。</para>
/// </summary>
public abstract class DataEntity : BaseEntity, IOrgScoped
{
    /// <summary>
    /// 归属机构 Id(数据范围锚点)= 创建者当时所属机构。插入时由审计 AOP 从 <c>ICurrentUser.OrgId</c>(令牌 org claim)自动填充,
    /// 数据范围过滤器按它 + 当前用户的范围规则决定行可见性。为 null 表示行没有归属机构(系统内建数据、或创建者无归属机构):
    /// 受限用户看不到这些行,只有范围含“仅本人”的创建人和不受限用户能看到。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "归属机构 Id(数据范围锚点)")]
    public long? CreateOrgId { get; set; }
}

/// <summary>
/// 带机构数据范围的<b>审计实体</b>:<see cref="AuditEntity"/> + <see cref="IOrgShared"/>,<b>不含软删除</b>——
/// <see cref="DataEntity"/> 的“物理删版”。业务表继承它即获得数据范围锚点 <see cref="CreateOrgId"/>(同 <c>DataEntity</c>)
/// 和按行共享标记 <see cref="IsShared"/>,全局过滤器按 <see cref="IOrgShared"/> 接口生效,但仓储 <c>DeleteAsync</c> 是<b>物理删除</b>、无回收站。
/// <para>适用于确需真删、又要机构隔离 + 审计的业务表。需要软删除/回收站的机构表继续用 <see cref="DataEntity"/>(不带共享能力)。</para>
/// <para>它<b>不实现</b> <see cref="IOrgScoped"/>:全局过滤器按接口匹配,<c>ClearFilter&lt;IOrgScoped&gt;()</c> 清不掉它的过滤器,
/// 要写 <c>ClearFilter&lt;IOrgScoped, IOrgShared&gt;()</c>。</para>
/// <para><b>写路径守卫</b>:<see cref="SqlSugarRepository{TEntity}"/> 对机构实体的
/// <c>UpdateAsync</c>/<c>DeleteAsync</c>/<c>HardDeleteAsync</c> 已内置范围守卫:私有行被非归属方写入返回 0 行(对他机构不可见),
/// <b>共享行被非归属方写入抛 <c>ErrorCode.OrgOutOfScope</c></b>(共享行能读到,静默返回 0 会让忽略返回值的代码以为保存成功)。
/// 归属以 <c>DataScopeResult.Owns</c> 判断,业务服务也可以用它给 DTO 算“能否编辑”。
/// 守卫要读当前数据范围,所以仓储必须带 <c>IDataScopeContext</c>(DI 注入的仓储自带;自己 <c>new</c> 或
/// <c>: SqlSugarRepository&lt;T&gt;(db)</c> 的子类要补传),缺省时写操作抛 <see cref="InvalidOperationException"/>。</para>
/// </summary>
public abstract class OrgAuditEntity : AuditEntity, IOrgShared
{
    /// <summary>
    /// 归属机构 Id(数据范围锚点)= 创建者当时所属机构。插入时由审计 AOP 从 <c>ICurrentUser.OrgId</c> 自动填充(按 <see cref="IOrgShared"/> 匹配),
    /// 数据范围过滤器据它决定行可见性。为 null 表示行没有归属机构:受限用户看不到(除非 <see cref="IsShared"/> 为 true),
    /// 只有范围含“仅本人”的创建人和不受限用户能看到。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "归属机构 Id(数据范围锚点)")]
    public long? CreateOrgId { get; set; }

    /// <summary>
    /// 是否共享给所有机构。true 时所有机构的用户都能查到这行(不管 <see cref="CreateOrgId"/> 是否为空);
    /// null 与 false 都表示不共享,可见性与普通机构数据一致。列必须可空:给已有数据的表加列只能可空。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "是否共享给所有机构")]
    public bool? IsShared { get; set; }
}
