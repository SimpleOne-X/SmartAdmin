using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IPermissionProvider"/> 的 RBAC 实现。
/// <para>热路径优先走缓存:命中直接返回;未命中才聚合查库(用户 → 启用角色 → 菜单,叠加用户单独授权,
/// 再取有效菜单的权限码),结果回填缓存。授权变更由 <see cref="RbacService"/> 与单独授权服务精确失效对应用户的缓存键。</para>
/// <para>已缓存的<b>空集合</b>与"未缓存"可区分(见 <see cref="ICacheProvider.GetAsync{T}"/>),
/// 无权限用户也只查一次库,不会每请求穿透。缓存过期不超过该用户最近一条单独授权的到期时刻。</para>
/// </summary>
public class RbacPermissionProvider(
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysMenu> menus,
    ICacheProvider cache,
    AdminCacheOptions cacheOptions,
    // 可选尾参:DI 正常注入,消费者子类不传也能编译;单独授权是否到期按注入时钟判(与审计字段同一本地时间口径)
    TimeProvider? time = null) : IPermissionProvider
{
    /// <summary>当前本地时间,单独授权是否到期按它判。</summary>
    protected DateTime Now => (time ?? TimeProvider.System).GetLocalNow().DateTime;

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<string>> GetPermissionCodesAsync(long userId, CancellationToken cancellationToken = default)
    {
        var key = CacheKeys.UserPermissions(userId);
        var cached = await cache.GetAsync<string[]>(key, cancellationToken);
        if (cached is not null) return cached;

        // 有将到期的单独授权时,缓存不能活过那一刻,否则到期后最长还按旧权限放行一个 TTL。
        // 最早到期时刻要先于聚合取:聚合计入的授权到期时间都不早于它,封到它就不会活过任何一条被计入的授权;
        // 先聚合再取的话,授权恰在两步之间到期,封顶查询看不到它,缓存会按整段配置 TTL 留着含已到期授权的结果
        var nextExpiry = await GetNextGrantExpiryAsync(userId);
        var codes = await LoadFromDatabaseAsync(userId);
        var configured = cacheOptions.PermissionMinutes > 0 ? TimeSpan.FromMinutes(cacheOptions.PermissionMinutes) : (TimeSpan?)null;
        var ttl = UserMenuGrantRules.CapTtl(configured, nextExpiry, Now);
        await cache.SetAsync(key, codes, ttl, cancellationToken);
        return codes;
    }

    /// <summary>
    /// 聚合查库:用户的启用角色 → 角色的菜单 → 叠加单独授权得到有效菜单 → 有效菜单里带路由码且启用的节点的 Permission 展开去重
    /// (一个按钮节点可挂多条码,见 <see cref="PermissionCode.Split"/>)。
    /// 没有角色时不再直接返回空:单独授权的「允许」可以让没有角色的用户也有权限。仅在缓存未命中时执行。
    /// </summary>
    protected virtual async Task<string[]> LoadFromDatabaseAsync(long userId)
    {
        var roleIds = await userRoles.AsQueryable()
            .InnerJoin<SysRole>((ur, r) => ur.RoleId == r.Id && r.Enabled)
            .Where((ur, r) => ur.UserId == userId)
            .Select((ur, r) => ur.RoleId).ToListAsync();
        List<long> roleMenuIds = roleIds.Count == 0
            ? []
            : await roleMenus.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.MenuId).ToListAsync();

        var menuIds = (await ApplyUserGrantsAsync(userId, roleMenuIds)).ToList();
        if (menuIds.Count == 0) return [];

        var permissions = await menus.AsQueryable()
            .Where(m => menuIds.Contains(m.Id) && m.Enabled && m.Permission != "")
            .Select(m => m.Permission)
            .ToListAsync();
        return permissions.SelectMany(PermissionCode.Split).Distinct().ToArray();
    }

    /// <summary>
    /// 在角色授予的菜单上叠加该用户的单独授权(规则见 <see cref="UserMenuGrantRules"/>)。
    /// 没有任何单独授权记录时原样返回、不读菜单表——绝大多数用户走的就是这条路。
    /// </summary>
    protected virtual async Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)
    {
        var grants = await UserMenuGrantQueries.ListByUserAsync(menus.Db, userId);
        if (grants.Count == 0) return roleMenuIds;
        var all = await menus.AsQueryable().ToListAsync();
        return UserMenuGrantRules.Compute(roleMenuIds, grants, all, Now).EffectiveMenuIds;
    }

    /// <summary>该用户最早一条尚未到期的单独授权的到期时间(没有为 null),给缓存过期封顶。</summary>
    protected virtual Task<DateTime?> GetNextGrantExpiryAsync(long userId) =>
        UserMenuGrantQueries.NextExpiryAsync(menus.Db, userId, Now);
}
