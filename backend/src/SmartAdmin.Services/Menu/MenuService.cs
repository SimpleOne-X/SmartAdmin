using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IMenuService"/> 默认实现。菜单表很小,整表载入内存做树运算(上溯根目录取 ModuleId、
/// 授权叶子的祖先脚手架),避免多次递归查库。有效菜单与 <c>RbacPermissionProvider</c> 同一套规则:启用角色授予的菜单叠加用户单独授权。
/// </summary>
public class MenuService(
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysMenu> menus,
    IRepository<SysModule> modules,
    IRbacService rbac,
    ICacheProvider cache,
    AdminCacheOptions cacheOptions,
    // 可选尾参:DI 正常注入,消费者子类不传也能编译;单独授权是否到期按注入时钟判
    TimeProvider? time = null) : IMenuService
{
    /// <summary>上溯 ParentId 链到根目录的最大步数(防断链/环)。菜单层级远小于此。</summary>
    private const int WalkGuard = 64;

    /// <summary>
    /// 当前用户启用中的角色 Id。与 <see cref="RbacPermissionProvider"/> 同口径(停用角色不授码),
    /// 门户模块/菜单树必须同样过滤,否则停用角色后接口 403、侧栏仍显示入口。
    /// </summary>
    protected virtual async Task<List<long>> ResolveEnabledRoleIdsAsync(long userId) =>
        await userRoles.AsQueryable()
            .InnerJoin<SysRole>((ur, r) => ur.RoleId == r.Id && r.Enabled)
            .Where((ur, r) => ur.UserId == userId)
            .Select((ur, r) => ur.RoleId).ToListAsync();

    /// <summary>
    /// 用户的有效菜单 Id:启用角色授予的菜单,叠加用户单独授权。与 <see cref="RbacPermissionProvider"/> 同一套规则,
    /// 门户模块与菜单树都从它反推,侧栏与接口权限不会各说各话。
    /// </summary>
    protected virtual async Task<List<long>> ResolveGrantedMenuIdsAsync(long userId)
    {
        var roleIds = await ResolveEnabledRoleIdsAsync(userId);
        List<long> roleMenuIds = roleIds.Count == 0
            ? []
            : await roleMenus.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.MenuId).ToListAsync();
        return [.. await ApplyUserGrantsAsync(userId, roleMenuIds)];
    }

    /// <summary>在角色授予的菜单上叠加单独授权;没有记录时原样返回、不读菜单表。</summary>
    protected virtual async Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)
    {
        var grants = await UserMenuGrantQueries.ListByUserAsync(menus.Db, userId);
        if (grants.Count == 0) return roleMenuIds;
        var all = await menus.AsQueryable().ToListAsync();
        return UserMenuGrantRules.Compute(roleMenuIds, grants, all, Now).EffectiveMenuIds;
    }

    /// <summary>该用户最早一条尚未到期的单独授权的到期时间(没有为 null),给门户缓存过期封顶。</summary>
    protected virtual Task<DateTime?> GetNextGrantExpiryAsync(long userId) =>
        UserMenuGrantQueries.NextExpiryAsync(menus.Db, userId, Now);

    // 门户缓存 TTL:仅作孤儿回收 + 直连改库的兜底(正确性由 CUD 自增 PortalGeneration 保证);复用权限缓存过期配置。
    private TimeSpan? PortalTtl => cacheOptions.PermissionMinutes > 0 ? TimeSpan.FromMinutes(cacheOptions.PermissionMinutes) : null;

    /// <summary>当前本地时间,单独授权是否到期按它判。</summary>
    protected DateTime Now => (time ?? TimeProvider.System).GetLocalNow().DateTime;

    /// <summary>门户缓存 TTL:配置值;该用户有将到期的单独授权时按到期时刻封顶(超管不受单独授权影响,不查)。</summary>
    private async Task<TimeSpan?> PortalTtlForAsync(long userId, bool isSuperAdmin) =>
        isSuperAdmin ? PortalTtl : UserMenuGrantRules.CapTtl(PortalTtl, await GetNextGrantExpiryAsync(userId), Now);

    /// <summary>令门户缓存(模块列表 + 菜单树)整体惰性失效——自增代际,旧键不再被读到。菜单/角色-菜单/用户-角色变更后调用。</summary>
    private Task BumpPortalAsync() => cache.IncrementAsync(CacheKeys.PortalGeneration);

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<ModuleItem>> GetMyModulesAsync(long userId, bool isSuperAdmin)
    {
        var gen = await cache.GetAsync<long>(CacheKeys.PortalGeneration);   // 键缺失 → 0(首代)
        var key = CacheKeys.PortalModules(userId, gen);
        var cached = await cache.GetAsync<List<ModuleItem>>(key);
        if (cached is not null) return cached;                              // 命中(含缓存的空列表,与未缓存可区分)

        var result = await ComputeMyModulesAsync(userId, isSuperAdmin);
        await cache.SetAsync(key, result, await PortalTtlForAsync(userId, isSuperAdmin));
        return result;
    }

    /// <summary>聚合查库计算用户可访问模块(仅缓存未命中时执行)。</summary>
    protected virtual async Task<List<ModuleItem>> ComputeMyModulesAsync(long userId, bool isSuperAdmin)
    {
        var allModules = await modules.AsQueryable().Where(m => m.Enabled).OrderBy(m => m.Sort).OrderBy(m => m.Id).ToListAsync();
        if (isSuperAdmin) return allModules.Select(ToItem).ToList();

        var grantedMenuIds = await ResolveGrantedMenuIdsAsync(userId);
        if (grantedMenuIds.Count == 0) return [];

        // 只用启用菜单反推模块访问权:与 RbacPermissionProvider 的生效权限口径一致。
        // 若角色仍关联已停用菜单,它不应继续让用户在门户看到该模块(Permission 本身也不会授出)。
        var byId = (await menus.AsQueryable().Where(m => m.Enabled).ToListAsync()).ToDictionary(m => m.Id);
        var accessibleModuleIds = grantedMenuIds
            .Select(id => MenuTree.RootModuleId(id, byId))
            .Where(mid => mid is not null)
            .Select(mid => mid!.Value)
            .ToHashSet();

        return allModules.Where(m => accessibleModuleIds.Contains(m.Id)).Select(ToItem).ToList();
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<MenuNode>> GetMyMenuTreeAsync(long userId, bool isSuperAdmin, long moduleId)
    {
        var gen = await cache.GetAsync<long>(CacheKeys.PortalGeneration);
        var key = CacheKeys.PortalMenuTree(userId, moduleId, gen);
        var cached = await cache.GetAsync<List<MenuNode>>(key);
        if (cached is not null) return cached;

        var result = await ComputeMyMenuTreeAsync(userId, isSuperAdmin, moduleId);
        await cache.SetAsync(key, result, await PortalTtlForAsync(userId, isSuperAdmin));
        return result;
    }

    /// <summary>聚合查库计算用户在某模块下的菜单树(仅缓存未命中时执行)。</summary>
    protected virtual async Task<List<MenuNode>> ComputeMyMenuTreeAsync(long userId, bool isSuperAdmin, long moduleId)
    {
        var allMenus = await menus.AsQueryable().Where(m => m.Enabled).OrderBy(m => m.Sort).OrderBy(m => m.Id).ToListAsync();
        var byId = allMenus.ToDictionary(m => m.Id);

        // 该模块下的节点 = 其根目录 ModuleId == moduleId
        var moduleMenus = allMenus.Where(m => MenuTree.RootModuleId(m.Id, byId) == moduleId).ToList();

        IEnumerable<SysMenu> visible = moduleMenus;
        if (!isSuperAdmin)
        {
            var grantedMenuIds = await ResolveGrantedMenuIdsAsync(userId);

            // 授权叶子 ∪ 其祖先目录(脚手架:让被授权节点在树上有完整的父路径)
            var keep = new HashSet<long>();
            foreach (var id in grantedMenuIds)
            {
                var cur = byId.GetValueOrDefault(id);
                var guard = 0;
                while (cur is not null && guard++ < WalkGuard)
                {
                    if (!keep.Add(cur.Id)) break;          // 已收录,其祖先链亦已收录
                    if (cur.ParentId == 0) break;
                    cur = byId.GetValueOrDefault(cur.ParentId);
                }
            }
            visible = moduleMenus.Where(m => keep.Contains(m.Id));
        }

        // 只保留导航节点(目录/页面);按钮仅承载权限码,不进侧边栏。
        var navNodes = visible.Where(m => m.Type != MenuType.Button).ToList();
        return BuildForest(navNodes);
    }

    // ── 管理端 CRUD ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<MenuTreeNode>> GetTreeAsync()
    {
        // 管理端要看全量原始菜单(含停用、含按钮),不套门户的授权裁剪;软删仍由全局过滤器隐藏。
        var all = await menus.AsQueryable().OrderBy(m => m.Sort).OrderBy(m => m.Id).ToListAsync();
        return BuildAdminForest(all);
    }

    /// <inheritdoc />
    public virtual async Task<long> CreateAsync(MenuInput input)
    {
        await EnsureParentValidAsync(null, input.ParentId);
        var entity = ApplyInput(new SysMenu(), input);
        await menus.InsertAsync(entity);   // AOP 补雪花 Id/审计字段
        await BumpPortalAsync();            // 新菜单可能改变门户模块/树 → 门户缓存整体失效
        return entity.Id;
    }

    /// <inheritdoc />
    public virtual async Task UpdateAsync(long id, MenuInput input)
    {
        var entity = await menus.GetByIdAsync(id);
        AdminException.ThrowIf(entity is null, ErrorCode.MenuNotFound);
        await EnsureParentValidAsync(id, input.ParentId);
        await menus.UpdateAsync(ApplyInput(entity!, input));
        // 权限码/启用态可能已变 → 失效被授该菜单用户的权限缓存,授权改动即时生效(不等 TTL)
        await rbac.InvalidatePermissionsByMenuAsync(id);
        await BumpPortalAsync();   // 标题/启用/父级/所属模块变更也会改门户模块/树 → 门户缓存整体失效
    }

    /// <summary>
    /// 校验目标父节点合法:父必须存在(顶级 ParentId==0 除外);更新时父不得指向自身或自身子孙——
    /// 否则 <see cref="BuildAdminForest"/> 会把成环的节点挂到彼此下、永不成为根,整个子树从管理端树上消失且无从修复
    /// (还会被 <see cref="DeleteAsync"/> 的"有子节点"判据卡死)。菜单表小,整表载入内存上溯判环。
    /// </summary>
    protected virtual async Task EnsureParentValidAsync(long? selfId, long parentId)
    {
        if (parentId == 0) return;   // 顶级目录:无父

        var byId = (await menus.AsQueryable().ToListAsync()).ToDictionary(m => m.Id);
        AdminException.ThrowIf(!byId.ContainsKey(parentId), ErrorCode.MenuInvalidParent);   // 父必须存在
        if (selfId is null) return;   // 新增:节点尚不存在,不可能成环

        // 更新:从新父上溯根,若触达自身则成环(含 parentId==selfId 的自指)——拒绝。
        var cur = byId.GetValueOrDefault(parentId);
        var guard = 0;
        while (cur is not null && guard++ < WalkGuard)
        {
            AdminException.ThrowIf(cur.Id == selfId, ErrorCode.MenuInvalidParent);
            if (cur.ParentId == 0) break;
            cur = byId.GetValueOrDefault(cur.ParentId);
        }
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(long id)
    {
        AdminException.ThrowIf(await menus.GetByIdAsync(id) is null, ErrorCode.MenuNotFound);
        AdminException.ThrowIf(await menus.AsQueryable().AnyAsync(m => m.ParentId == id), ErrorCode.MenuHasChildren);
        // ponytail: 不级联清 sys_role_menu——软删后该菜单被全局过滤器隐藏,其权限码不再聚合、门户不再可见,
        //           悬空关联行无害;需要物理回收再加清理任务。
        await menus.DeleteAsync(id);
        // 悬空的 sys_role_menu 仍在,故删后扇出仍能定位受影响用户,失效其权限缓存(否则最长 TTL 内仍按旧权限)
        await rbac.InvalidatePermissionsByMenuAsync(id);
        await BumpPortalAsync();   // 删菜单会改门户模块/树 → 门户缓存整体失效
    }

    /// <summary>
    /// 把入参写入实体;<b>ModuleId 仅顶级目录(ParentId==0)保留,子节点强制置空</b>(归属靠上溯解析,不冗余存)。
    /// 权限码经 <see cref="PermissionCode.Split"/> 归一化后存(去空白、逐条规范化、去重),手敲的大小写差异到不了库里。
    /// </summary>
    protected virtual SysMenu ApplyInput(SysMenu e, MenuInput input)
    {
        e.ParentId = input.ParentId;
        e.Type = input.Type;
        e.Title = input.Title;
        e.Permission = PermissionCode.Join(PermissionCode.Split(input.Permission));
        e.Sort = input.Sort;
        e.Enabled = input.Enabled;
        e.ModuleId = input.ParentId == 0 ? input.ModuleId : null;
        e.Path = input.Path;
        e.Component = input.Component;
        e.Icon = input.Icon;
        e.Visible = input.Visible;
        return e;
    }

    /// <summary>按 ParentId 把平铺(已排序)节点拼成森林(管理端全字段节点);父不在集合内的升为根。</summary>
    private static IReadOnlyList<MenuTreeNode> BuildAdminForest(List<SysMenu> nodes)
    {
        var map = nodes.ToDictionary(m => m.Id, ToAdminNode);
        var roots = new List<MenuTreeNode>();
        foreach (var m in nodes)
        {
            var node = map[m.Id];
            if (m.ParentId != 0 && map.TryGetValue(m.ParentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        return roots;
    }

    private static MenuTreeNode ToAdminNode(SysMenu m) => new()
    {
        Id = m.Id, ParentId = m.ParentId, Type = m.Type, Title = m.Title, Permission = m.Permission,
        Sort = m.Sort, Enabled = m.Enabled, ModuleId = m.ModuleId,
        Path = m.Path, Component = m.Component, Icon = m.Icon, Visible = m.Visible,
    };

    /// <summary>按 ParentId 把平铺(已排序)节点拼成森林;父不在集合内的节点升为根。</summary>
    private static List<MenuNode> BuildForest(List<SysMenu> nodes)
    {
        var nodeMap = nodes.ToDictionary(m => m.Id, ToNode);
        var roots = new List<MenuNode>();
        foreach (var m in nodes)
        {
            var node = nodeMap[m.Id];
            if (m.ParentId != 0 && nodeMap.TryGetValue(m.ParentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        return roots;
    }

    private static ModuleItem ToItem(SysModule m) => new()
    {
        Id = m.Id, Code = m.Code, Title = m.Title, Icon = m.Icon, DefaultRoute = m.DefaultRoute, Sort = m.Sort,
    };

    private static MenuNode ToNode(SysMenu m) => new()
    {
        Id = m.Id, ParentId = m.ParentId, Type = m.Type, Title = m.Title,
        Path = m.Path, Component = m.Component, Icon = m.Icon, Sort = m.Sort, Visible = m.Visible,
    };
}
