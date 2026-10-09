using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>某用户的有效菜单计算结果。</summary>
/// <param name="EffectiveMenuIds">有效菜单(只含启用节点)</param>
/// <param name="DeniedMenuIds">被生效中的拒绝收回的节点:拒绝的节点连同其全部子孙</param>
/// <param name="NextExpiry">该用户全部单独授权里晚于当前时间的最早到期时间,没有为 null;缓存过期按它封顶</param>
public sealed record UserMenuGrantResult(IReadOnlySet<long> EffectiveMenuIds, IReadOnlySet<long> DeniedMenuIds, DateTime? NextExpiry);

/// <summary>
/// 用户单独授权的计算规则,唯一实现(纯函数,不碰库):
/// 有效菜单 =(启用角色授予 ∪ 生效中的允许)− 生效中的拒绝及其子孙,再只留启用节点。
/// <para>拒绝优先;拒绝扩展到子孙,所以拒掉页面就连它的按钮一起拒掉,不会出现页面没了、按钮接口还能调。
/// 拒绝作用在节点上而不是权限码上:被拒节点的某条码若还由另一个有效节点携带,这条码照样有效,
/// <see cref="LeakedCodes"/> 把这种情况找出来给界面提示。</para>
/// <para>权限聚合(<c>RbacPermissionProvider</c>)、门户(<c>MenuService</c>)与授权服务都调这里。</para>
/// </summary>
public static class UserMenuGrantRules
{
    /// <summary>生效中 = 没有到期时间,或到期时间晚于当前时间。</summary>
    public static bool IsActive(SysUserMenu grant, DateTime now) => grant.ExpireTime is null || grant.ExpireTime > now;

    /// <summary>
    /// 计算有效菜单。<paramref name="menus"/> 传全部未删除的菜单(含停用的):拒绝按完整的树展开子孙,
    /// 停用节点最后统一剔除。
    /// </summary>
    public static UserMenuGrantResult Compute(
        IEnumerable<long> roleMenuIds,
        IReadOnlyCollection<SysUserMenu> grants,
        IReadOnlyCollection<SysMenu> menus,
        DateTime now)
    {
        var active = grants.Where(g => IsActive(g, now)).ToList();
        var denied = MenuTree.WithDescendants(active.Where(g => g.Effect == UserMenuEffect.Deny).Select(g => g.MenuId), menus);
        var enabled = menus.Where(m => m.Enabled).Select(m => m.Id).ToHashSet();
        var effective = roleMenuIds
            .Concat(active.Where(g => g.Effect == UserMenuEffect.Allow).Select(g => g.MenuId))
            .Where(id => enabled.Contains(id) && !denied.Contains(id))
            .ToHashSet();
        var next = grants.Where(g => g.ExpireTime > now).Min(g => g.ExpireTime);
        return new UserMenuGrantResult(effective, denied, next);
    }

    /// <summary>
    /// 缓存过期时间:配置值与「到下一个到期时刻还剩多久」取小。配置为 null(永不过期)也按到期时刻封顶;
    /// 没有将到期的记录就是配置值。剩余时长至少 1 秒,不给出 0 或负数(缓存实现可能把它当成永不过期)。
    /// </summary>
    public static TimeSpan? CapTtl(TimeSpan? configured, DateTime? nextExpiry, DateTime now)
    {
        if (nextExpiry is not { } at) return configured;
        var remaining = at - now;
        if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);
        return configured is { } c && c < remaining ? c : remaining;
    }

    /// <summary>拒掉 <paramref name="deniedMenuId"/>(连同子孙)之后仍然有效的权限码,以及携带它们的有效节点。</summary>
    public static IReadOnlyList<UserMenuLeakedCode> LeakedCodes(
        long deniedMenuId, IReadOnlyCollection<SysMenu> menus, IReadOnlySet<long> effectiveMenuIds)
    {
        var subtree = MenuTree.WithDescendants([deniedMenuId], menus);
        var denied = menus.Where(m => subtree.Contains(m.Id))
            .SelectMany(m => PermissionCode.Split(m.Permission))
            .ToHashSet(StringComparer.Ordinal);
        if (denied.Count == 0) return [];

        return [.. menus
            .Where(m => effectiveMenuIds.Contains(m.Id) && !subtree.Contains(m.Id))
            .SelectMany(m => PermissionCode.Split(m.Permission).Select(code => (Code: code, MenuId: m.Id)))
            .Where(x => denied.Contains(x.Code))
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new UserMenuLeakedCode { Code = g.Key, CarrierMenuIds = [.. g.Select(x => x.MenuId).Distinct().Order()] })];
    }
}
