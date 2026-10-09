namespace SmartAdmin.Services;

/// <summary>被拒节点里仍然有效的一条权限码,以及携带它的有效节点。</summary>
public record UserMenuLeakedCode
{
    /// <summary>权限码(规范化路由)</summary>
    public string Code { get; init; } = "";

    /// <summary>仍携带这条码的有效菜单节点 Id</summary>
    public IReadOnlyList<long> CarrierMenuIds { get; init; } = [];
}
