using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 租户表——隔离边界的根,自己不实现 ITenantScoped(不自我引用)。新表,首次建表可直接 NOT NULL,
/// 不受"已有表加列必须可空"约束。
/// </summary>
[SugarTable("sys_tenant", TableDescription = "租户")]
[SugarIndex("idx_sys_tenant_code", nameof(Code), OrderByType.Asc, IsUnique = true)]
public class SysTenant : BaseEntity
{
    [SugarColumn(Length = 32, ColumnDescription = "租户编码(唯一)")]
    public string Code { get; set; } = "";

    [SugarColumn(Length = 128, ColumnDescription = "租户名称")]
    public string Name { get; set; } = "";

    [SugarColumn(Length = 64, IsNullable = true, ColumnDescription = "联系人")]
    public string? ContactName { get; set; }

    [SugarColumn(Length = 32, IsNullable = true, ColumnDescription = "联系电话")]
    public string? ContactPhone { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "到期时间")]
    public DateTime? ExpireTime { get; set; }

    /// <summary>二期预留,一期恒为 Shared;TenantService 对 Standalone 写入一律拒绝。</summary>
    [SugarColumn(ColumnDescription = "隔离模式")]
    public TenantIsolationMode IsolationMode { get; set; } = TenantIsolationMode.Shared;

    /// <summary>二期预留:对应 SqlSugar ConfigId,只路由消费方业务库连接,不影响内核自身数据。一期恒为空。</summary>
    [SugarColumn(Length = 64, IsNullable = true, ColumnDescription = "业务库连接配置Id(二期预留)")]
    public string? ConnectionConfigId { get; set; }

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; } = true;

    [SugarColumn(Length = 256, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }
}
