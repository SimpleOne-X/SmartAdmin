using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>被拒节点里仍然有效的一条权限码,以及携带它的有效节点。</summary>
public record UserMenuLeakedCode
{
    /// <summary>权限码(规范化路由)</summary>
    public string Code { get; init; } = "";

    /// <summary>仍携带这条码的有效菜单节点 Id</summary>
    public IReadOnlyList<long> CarrierMenuIds { get; init; } = [];
}

/// <summary>单独授权变更集里的一条新增或修改。</summary>
public record UserMenuGrantUpsert
{
    /// <summary>菜单节点</summary>
    public long MenuId { get; init; }

    /// <summary>允许 / 拒绝</summary>
    public UserMenuEffect Effect { get; init; }

    /// <summary>到期时间(本地时间);为空即长期。非超管授「允许」必填。</summary>
    public DateTime? ExpireTime { get; init; }

    /// <summary>授权理由,最长 200</summary>
    public string? Remark { get; init; }
}

/// <summary>
/// 按变更集保存某用户的单独授权:新增与修改走 <see cref="Upserts"/>,移除走 <see cref="Removes"/>(按菜单 Id),
/// 没提到的记录原样保留。
/// </summary>
public record SetUserMenuGrantsInput
{
    /// <summary>目标用户</summary>
    public long UserId { get; init; }

    /// <summary>新增或修改</summary>
    public IReadOnlyList<UserMenuGrantUpsert> Upserts { get; init; } = [];

    /// <summary>要移除记录的菜单 Id</summary>
    public IReadOnlyList<long> Removes { get; init; } = [];
}

/// <summary>一条单独授权记录(授权菜单弹窗回显)。</summary>
public record UserMenuGrantItem
{
    /// <summary>菜单节点</summary>
    public long MenuId { get; init; }

    /// <summary>允许 / 拒绝</summary>
    public UserMenuEffect Effect { get; init; }

    /// <summary>到期时间;为空即长期</summary>
    public DateTime? ExpireTime { get; init; }

    /// <summary>授权理由</summary>
    public string? Remark { get; init; }

    /// <summary>授权人(建这一行的人;系统写入为空)</summary>
    public long? GrantorId { get; init; }

    public string? GrantorName { get; init; }

    /// <summary>授权时间</summary>
    public DateTime GrantTime { get; init; }

    /// <summary>最后修改人</summary>
    public long? UpdaterId { get; init; }

    public string? UpdaterName { get; init; }

    public DateTime? UpdateTime { get; init; }
}

/// <summary>授权弹窗用的模块清单项:授权弹窗不调模块列表接口(那个要「模块-查询」权限)。</summary>
public record UserMenuModuleItem
{
    public long Id { get; init; }

    public string Title { get; init; } = "";

    /// <summary>可转授(内置 system 模块恒为 false)</summary>
    public bool Delegatable { get; init; }
}

/// <summary>一个菜单节点对目标用户是否有效、为什么。</summary>
public record UserMenuEffectiveNode
{
    public long MenuId { get; init; }

    /// <summary>所属模块(上溯到根目录取 ModuleId)</summary>
    public long? ModuleId { get; init; }

    /// <summary>最终是否有效</summary>
    public bool Effective { get; init; }

    /// <summary>授予该节点的启用角色名</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>该节点上的单独授权效果(含已过期的);没有记录为 null</summary>
    public UserMenuEffect? Grant { get; init; }

    public DateTime? ExpireTime { get; init; }

    /// <summary>该节点上的单独授权已过期</summary>
    public bool Expired { get; init; }

    /// <summary>被某个祖先节点的拒绝连带收回</summary>
    public bool DeniedByAncestor { get; init; }

    /// <summary>当前授权人能否改这个节点(超管恒为 true;普通管理员看菜单所属模块是否可转授)</summary>
    public bool Grantable { get; init; }

    /// <summary>该节点被拒时仍然有效的权限码及携带它们的节点(只在生效中的拒绝上给出)</summary>
    public IReadOnlyList<UserMenuLeakedCode> LeakedCodes { get; init; } = [];
}

/// <summary>授权弹窗需要的全部数据,一次取齐。</summary>
public record UserMenuEffectiveOutput
{
    public long UserId { get; init; }

    /// <summary>目标用户有没有启用中的角色(没有时数据范围为「仅本人」,界面据此提示)</summary>
    public bool HasRoles { get; init; }

    /// <summary>当前授权人能否编辑这个目标用户</summary>
    public bool TargetEditable { get; init; }

    /// <summary>不能编辑的原因:42029 自己 / 42007 超管 / 41005 范围外 / 41007 对方是管理员</summary>
    public ErrorCode? ReadOnlyReason { get; init; }

    /// <summary>委派授权最长天数;超管、配置为 0 时为空</summary>
    public int? DelegatedMaxDays { get; init; }

    public IReadOnlyList<UserMenuModuleItem> Modules { get; init; } = [];

    /// <summary>全部未删除菜单节点(含停用的)各一项</summary>
    public IReadOnlyList<UserMenuEffectiveNode> Nodes { get; init; } = [];
}

/// <summary>单独授权的状态(一览筛选与展示)。</summary>
public enum UserMenuGrantStatus
{
    /// <summary>生效中(作筛选条件时含 7 天内到期的)</summary>
    Active = 1,

    /// <summary>7 天内到期</summary>
    Expiring = 2,

    /// <summary>已过期</summary>
    Expired = 3,
}

/// <summary>单独授权一览的查询条件。</summary>
public record UserMenuGrantPageInput : PageInputBase
{
    /// <summary>目标用户账号或姓名,模糊</summary>
    public string? User { get; init; }

    /// <summary>授权人账号或姓名,模糊</summary>
    public string? Grantor { get; init; }

    public long? MenuId { get; init; }

    public UserMenuEffect? Effect { get; init; }

    public UserMenuGrantStatus? Status { get; init; }
}

/// <summary>单独授权一览的一行。</summary>
public record UserMenuGrantPageItem
{
    public long Id { get; init; }

    public long UserId { get; init; }

    public string UserAccount { get; init; } = "";

    public string UserName { get; init; } = "";

    public long MenuId { get; init; }

    public string MenuTitle { get; init; } = "";

    public long? ModuleId { get; init; }

    public string? ModuleTitle { get; init; }

    public UserMenuEffect Effect { get; init; }

    public DateTime? ExpireTime { get; init; }

    public UserMenuGrantStatus Status { get; init; }

    public long? GrantorId { get; init; }

    public string? GrantorName { get; init; }

    public DateTime GrantTime { get; init; }

    public string? Remark { get; init; }
}
