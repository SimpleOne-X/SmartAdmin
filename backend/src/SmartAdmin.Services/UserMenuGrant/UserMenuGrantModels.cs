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
