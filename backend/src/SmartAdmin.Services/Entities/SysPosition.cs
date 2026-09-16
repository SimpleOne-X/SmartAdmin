using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 职位表——组织内岗位/职务的字典项(如"经理""专员"),供用户维度的职位归属使用。
/// <para>与角色(权限载体)是正交概念:职位描述"是什么岗位",角色描述"有什么权限",
/// 二者不互相派生,一名用户可挂一个职位、多个角色。</para>
/// </summary>
[SugarTable("sys_position", TableDescription = "职位")]
// 编码唯一性是**租户内**唯一,不是全局唯一:两个客户各建一个 Code="gm" 的职位是常态,
// 不是边界情况。索引名保持不变,旧形状(单列 Code 全局唯一)的库由 DatabaseInitializer 按名先删再重建。
[SugarIndex("idx_sys_position_code", nameof(TenantId), OrderByType.Asc, nameof(Code), OrderByType.Asc, IsUnique = true)]
public class SysPosition : TenantEntity
{
    [SugarColumn(Length = 64, ColumnDescription = "职位名称")]
    public string Name { get; set; } = "";

    /// <summary>职位编码(唯一,程序判职位用它而非名称)</summary>
    [SugarColumn(Length = 64, ColumnDescription = "职位编码(唯一)")]
    public string Code { get; set; } = "";

    [SugarColumn(ColumnDescription = "排序(小在前)")]
    public int Sort { get; set; }

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; } = true;
}
