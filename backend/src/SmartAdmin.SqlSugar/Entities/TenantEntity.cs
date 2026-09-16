using SqlSugar;

namespace SmartAdmin.SqlSugar;

/// <summary>
/// 租户隔离标记。与 <see cref="IOrgScoped"/> 的区别:这是硬安全边界,配套的全局过滤器
/// (见 SqlSugarSetup.AttachHooks)不设"数据范围=全部"式逃逸条件——机构数据范围回答
/// "同租户内我能看多少",租户隔离回答"我根本不该看见别的租户",语义不同,不能共用同一个开关。
/// </summary>
public interface ITenantScoped
{
    long? TenantId { get; }
}

/// <summary>
/// 审计 + 软删 + 租户隔离,不含机构数据范围。多数内置 Sys* 表(用户/角色/菜单授权等)用这个。
/// <para><c>TenantId</c> 是 <c>long?</c> 而非 <c>long</c>:已发版表补列必须可空(见
/// <c>CodeFirstNullableUpgradeTests</c>),不能假设这一列总有值。新行由插入 AOP 自动填充;
/// 老库升级后的存量 null 行由 <c>TenantBackfillHook</c> 统一回填。</para>
/// </summary>
public abstract class TenantEntity : BaseEntity, ITenantScoped
{
    public long? TenantId { get; set; }
}

/// <summary>
/// 审计 + 软删 + 机构数据范围 + 租户隔离。需要"同租户内还要分机构可见范围"的实体用这个。
/// </summary>
public abstract class TenantDataEntity : DataEntity, ITenantScoped
{
    public long? TenantId { get; set; }
}
