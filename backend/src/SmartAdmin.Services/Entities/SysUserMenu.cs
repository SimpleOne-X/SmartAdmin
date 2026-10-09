using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>用户单独授权的效果。</summary>
public enum UserMenuEffect
{
    /// <summary>允许:在角色授予之外额外给这个节点(只作用于该节点本身)</summary>
    Allow = 1,

    /// <summary>拒绝:收回这个节点及其全部子孙,优先于任何允许</summary>
    Deny = 2,
}

/// <summary>
/// 用户单独授权:对一个用户的一个菜单节点做「允许」或「拒绝」,可带到期时间。
/// <para>有效菜单 =(启用角色授予 ∪ 生效中的允许)− 生效中的拒绝及其子孙,再只留启用节点,
/// 规则的唯一实现在 <see cref="UserMenuGrantRules"/>。</para>
/// <para>一个用户对一个节点只有一行。保存按变更集增改删,移除走物理删除——软删残行会撞唯一索引。
/// 所以 <c>CreateUserId</c> / <c>CreateTime</c> 就是授权人与授权时间,<c>UpdateUserId</c> / <c>UpdateTime</c> 是最后修改人与时间。</para>
/// </summary>
[SugarTable("sys_user_menu", TableDescription = "用户菜单单独授权")]
[SugarIndex("idx_sys_user_menu", nameof(UserId), OrderByType.Asc, nameof(MenuId), OrderByType.Asc, IsUnique = true)]
public class SysUserMenu : BaseEntity
{
    [SugarColumn(ColumnDescription = "目标用户 Id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnDescription = "菜单 Id")]
    public long MenuId { get; set; }

    [SugarColumn(ColumnDescription = "效果(1 允许 / 2 拒绝)")]
    public UserMenuEffect Effect { get; set; }

    /// <summary>到期时间(本地时间,与审计字段同口径);为空即长期,到了这个时刻起失效。</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "到期时间(空=长期)")]
    public DateTime? ExpireTime { get; set; }

    [SugarColumn(Length = 200, IsNullable = true, ColumnDescription = "授权理由")]
    public string? Remark { get; set; }
}
