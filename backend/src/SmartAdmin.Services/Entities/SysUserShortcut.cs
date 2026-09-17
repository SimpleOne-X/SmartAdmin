using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 用户工作台快捷方式——(UserId, MenuPath) 唯一,一行既记"是否手动置顶"也记"访问计数"。
/// <para>继承 <see cref="BaseEntity"/> 而非 <see cref="DataEntity"/>:这是用户个人偏好,不该被机构数据范围
/// 过滤器过滤掉。<see cref="PinnedAt"/> 单独一列而不是复用 <c>UpdateTime</c>——<c>UpdateTime</c> 会被访问计数
/// 递增顺带碰到,拿它排"置顶时间"会被污染。</para>
/// </summary>
[SugarTable("sys_user_shortcut", TableDescription = "用户工作台快捷方式")]
[SugarIndex("idx_sys_user_shortcut", nameof(UserId), OrderByType.Asc, nameof(MenuPath), OrderByType.Asc, IsUnique = true)]
public class SysUserShortcut : BaseEntity
{
    [SugarColumn(ColumnDescription = "用户 Id")]
    public long UserId { get; set; }

    [SugarColumn(Length = 256, ColumnDescription = "菜单路由 path")]
    public string MenuPath { get; set; } = "";

    [SugarColumn(ColumnDescription = "是否手动置顶")]
    public bool Pinned { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "置顶时间(null=未置顶)")]
    public DateTime? PinnedAt { get; set; }

    [SugarColumn(ColumnDescription = "访问计数")]
    public int VisitCount { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "最近访问时间")]
    public DateTime? LastVisitAt { get; set; }
}
