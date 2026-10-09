using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IRbacService"/> 默认实现。关联维护用"整删再插"全量替换语义:
/// 关联行是纯连接数据、无保留价值,故物理删除(走 <c>Db.Deleteable</c> 逃生舱口)而非软删——
/// 免得软删残行既撑大表又撞唯一索引。删+插包在事务里,失效缓存放在事务提交之后。
/// <para>
/// 角色<b>授权面</b>(菜单挂载、数据范围)是超管专属,越过路由权限之外的强约束——
/// 由 <see cref="EnsureSuperAdmin"/> 兜底;角色<b>指派面</b>(把角色关联到用户)经 <see cref="IRoleGrantPolicy"/>
/// 收口,非超管只能把"可转授"角色授予其数据范围内的用户。两个依赖均尾随可选:未注入(消费者精简子类/
/// 手工构造的实例)时视为可信系统上下文,不加限制。
/// 授权面另有一条:除「用户管理」「角色管理」外的系统菜单只能授给内置角色(见 <see cref="EnsureRoleMenusAssignableAsync"/>)。
/// </para>
/// </summary>
public class RbacService(
    IRepository<SysRole> roles,
    IRepository<SysUser> users,
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysRoleDataScope> roleScopes,
    ICacheProvider cache,
    IRoleGrantPolicy? grantPolicy = null,
    ICurrentUser? currentUser = null) : IRbacService
{
    /// <summary>超管专属操作守卫(角色授权面);系统/未认证上下文视为可信,不受限。</summary>
    protected virtual void EnsureSuperAdmin() =>
        AdminException.ThrowIf(
            currentUser is { IsAuthenticated: true, IsSuperAdmin: false },
            ErrorCode.SuperAdminRequired);

    /// <inheritdoc />
    public virtual async Task SetRoleMenusAsync(long roleId, IReadOnlyCollection<long> menuIds)
    {
        EnsureSuperAdmin();   // 角色菜单授权超管专属
        var role = await roles.GetByIdAsync(roleId);
        AdminException.ThrowIf(role is null, ErrorCode.RoleNotFound);
        await EnsureRoleMenusAssignableAsync(role!, menuIds);

        var links = menuIds.Distinct().Select(mid => new SysRoleMenu { RoleId = roleId, MenuId = mid }).ToList();
        await ReplaceAsync(
            deleteExisting: () => roleMenus.Db.Deleteable<SysRoleMenu>().Where(x => x.RoleId == roleId).ExecuteCommandAsync(),
            insertNew: links);

        // 授权变了 → 失效所有挂该角色用户的权限缓存(下次请求即按新授权重算)
        var affectedUsers = await userRoles.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.UserId).ToListAsync();
        await InvalidatePermissionsAsync(affectedUsers);
        await cache.IncrementAsync(CacheKeys.PortalGeneration);   // 授权变动改门户模块/菜单树 → 门户缓存整体失效
    }

    /// <summary>
    /// 超管专属的系统菜单只能授给内置角色(<see cref="SysRole.IsBuiltin"/>):它们是管理面,除超管自己外只由内置角色持有。
    /// 系统菜单指根目录是内核种子(Id ≤ <see cref="SmartSeedIds.KernelMax"/>)且挂在内置「系统」应用下的整棵子树,
    /// 其中「用户管理」「角色管理」两个页面(连同「组织管理」目录壳)放开,新建角色可以持有,
    /// 判定见 <see cref="MenuTree.IsSuperAdminOnlyMenu"/>;消费者在「系统」应用下自建的目录不算,新建角色照常可授。
    /// 判定读的是全表(含停用与软删的节点):中间隔着一个停用的目录时只读启用节点会断链,把下面的系统菜单误判成普通菜单;
    /// 软删的菜单在回收站里,恢复后会带着授权回来,同样算系统菜单。
    /// 系统 / 未认证上下文(种子、启动任务)视为可信,不受限,与 <see cref="EnsureSuperAdmin"/> 同一约定。
    /// 读菜单走已有仓储的 <c>Db</c> 逃生舱口,不加构造参数。
    /// </summary>
    protected virtual async Task EnsureRoleMenusAssignableAsync(SysRole role, IReadOnlyCollection<long> menuIds)
    {
        if (role.IsBuiltin || menuIds.Count == 0 || currentUser is not { IsAuthenticated: true }) return;
        var byId = (await roles.Db.Queryable<SysMenu>().ClearFilter<ISoftDelete>().ToListAsync()).ToDictionary(m => m.Id);
        AdminException.ThrowIf(
            menuIds.Any(id => MenuTree.IsSuperAdminOnlyMenu(id, byId)),
            ErrorCode.SystemMenuNotAssignable);
    }

    /// <inheritdoc />
    public virtual async Task SetUserRolesAsync(long userId, IReadOnlyCollection<long> roleIds)
    {
        var user = await users.GetByIdAsync(userId);
        AdminException.ThrowIf(user is null, ErrorCode.UserNotFound);

        // 只对"相对已有关联新增"的角色做转授校验——全量替换语义下,若连同被保留的既有关联一起校验,
        // 非超管重新提交一份"早先由超管授过某不可转授角色"的既有集合时,即便一个角色都没多加也会被误挡。
        var oldRoleIds = await userRoles.AsQueryable().Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync();
        var addedRoleIds = roleIds.Except(oldRoleIds).ToList();
        if (grantPolicy is not null)
            await grantPolicy.EnsureGrantableAsync(addedRoleIds, userId, user!.OrgId);

        var links = roleIds.Distinct().Select(rid => new SysUserRole { UserId = userId, RoleId = rid }).ToList();
        await ReplaceAsync(
            deleteExisting: () => userRoles.Db.Deleteable<SysUserRole>().Where(x => x.UserId == userId).ExecuteCommandAsync(),
            insertNew: links);

        // 角色变了 → 权限与数据范围都可能变,两者缓存都失效
        await InvalidatePermissionsAsync([userId]);
        await InvalidateScopesAsync([userId]);
        await cache.IncrementAsync(CacheKeys.PortalGeneration);   // 用户角色变动改其门户模块/菜单树 → 门户缓存整体失效
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<long>> GetRoleMenuIdsAsync(long roleId) =>
        await roleMenus.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.MenuId).ToListAsync();

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<long>> GetUserRoleIdsAsync(long userId) =>
        await userRoles.AsQueryable().Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync();

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<long>> GetRoleUserIdsAsync(long roleId) =>
        await userRoles.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.UserId).ToListAsync();

    /// <inheritdoc />
    public virtual async Task SetRoleUsersAsync(long roleId, IReadOnlyCollection<long> userIds)
    {
        AdminException.ThrowIf(!await roles.AnyAsync(r => r.Id == roleId), ErrorCode.RoleNotFound);

        var oldUserIds = await userRoles.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.UserId).ToListAsync();

        // 只对"新增"的用户做转授校验(同 SetUserRolesAsync 的理由);每个新增用户各自的机构决定是否在范围内。
        var addedUserIds = userIds.Except(oldUserIds).ToList();
        if (grantPolicy is not null && addedUserIds.Count > 0)
        {
            var addedUsers = await users.AsQueryable().Where(u => addedUserIds.Contains(u.Id)).Select(u => new { u.Id, u.OrgId }).ToListAsync();
            foreach (var u in addedUsers)
                await grantPolicy.EnsureGrantableAsync([roleId], u.Id, u.OrgId);
        }

        var links = userIds.Distinct().Select(uid => new SysUserRole { UserId = uid, RoleId = roleId }).ToList();
        await ReplaceAsync(
            deleteExisting: () => userRoles.Db.Deleteable<SysUserRole>().Where(x => x.RoleId == roleId).ExecuteCommandAsync(),
            insertNew: links);

        var affected = oldUserIds.Union(userIds).Distinct().ToList();
        await InvalidatePermissionsAsync(affected);
        await InvalidateScopesAsync(affected);
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
    }

    /// <inheritdoc />
    public virtual async Task SetRoleDataScopeAsync(long roleId, DataScopeType scopeType, IReadOnlyCollection<long>? customOrgIds = null)
    {
        EnsureSuperAdmin();   // 数据范围配置超管专属
        AdminException.ThrowIf(!await roles.AnyAsync(r => r.Id == roleId), ErrorCode.RoleNotFound);

        // 自定义机构仅 Custom 有意义;非 Custom 一律清空,避免残留误导
        var csv = scopeType == DataScopeType.Custom && customOrgIds is { Count: > 0 }
            ? string.Join(',', customOrgIds.Distinct())
            : "";

        var existing = await roleScopes.GetFirstAsync(x => x.RoleId == roleId);
        if (existing is null)
            await roleScopes.InsertAsync(new SysRoleDataScope { RoleId = roleId, ScopeType = scopeType, CustomOrgIds = csv });
        else
        {
            existing.ScopeType = scopeType;
            existing.CustomOrgIds = csv;
            await roleScopes.UpdateAsync(existing);
        }

        var affectedUsers = await userRoles.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.UserId).ToListAsync();
        await InvalidateScopesAsync(affectedUsers);
    }

    /// <inheritdoc />
    public virtual Task<SysRoleDataScope?> GetRoleDataScopeAsync(long roleId) =>
        roleScopes.GetFirstAsync(x => x.RoleId == roleId);

    /// <inheritdoc />
    public virtual async Task InvalidatePermissionsByMenuAsync(long menuId)
    {
        // 菜单 → 授它的角色(sys_role_menu) → 挂这些角色的用户(sys_user_role) → 失效其权限缓存。
        // 菜单 CRUD 低频,过量失效无害(下次请求按新授权重算);软删菜单不清 sys_role_menu,故删后此扇出仍能命中受影响用户。
        var roleIds = await roleMenus.AsQueryable().Where(x => x.MenuId == menuId).Select(x => x.RoleId).ToListAsync();
        List<long> affected = roleIds.Count == 0
            ? []
            : await userRoles.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.UserId).ToListAsync();

        // 再加上所有有单独授权记录的用户:拒绝会扩展到子孙,挪父节点会改变扩展结果,精确圈定代价高;
        // 单独授权是例外,这个集合小,过量失效无害。
        affected.AddRange(await roleMenus.Db.Queryable<SysUserMenu>().Select(g => g.UserId).Distinct().ToListAsync());

        if (affected.Count == 0) return;
        await InvalidatePermissionsAsync(affected.Distinct());
    }

    /// <inheritdoc />
    public virtual async Task InvalidateAllScopesAsync()
    {
        // 机构树结构变更影响"本机构及以下/自定义"范围的解析结果,受影响用户集难以精确圈定(跨角色、跨层级)。
        // ponytail: 机构增改删极低频,直接失效全体用户 scope 最简且正确。删除已是批量(一次多键 DEL),
        //           剩下的成本是那一次 Id 全表扫;真到了嫌它贵的规模,再收窄为"仅 OrgAndChildren/Custom 范围角色所属用户"。
        //           scope 代际键评估过不做:每请求多一次往返,换极低频机构变更的一次批量删,不划算。
        var allUserIds = await users.AsQueryable().Select(u => u.Id).ToListAsync();
        await InvalidateScopesAsync(allUserIds);
    }

    /// <inheritdoc />
    public virtual async Task InvalidateByRoleAsync(long roleId)
    {
        var affectedUsers = await userRoles.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.UserId).ToListAsync();
        await InvalidatePermissionsAsync(affectedUsers);
        await InvalidateScopesAsync(affectedUsers);
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
    }

    /// <inheritdoc />
    public virtual async Task OnRoleDeletedAsync(long roleId)
    {
        // 删前圈定受影响用户(下面要失效其权限/数据范围缓存);此刻关联行尚在。
        var affectedUsers = await userRoles.AsQueryable().Where(x => x.RoleId == roleId).Select(x => x.UserId).ToListAsync();

        // 事务内物理删三组关联(纯连接数据、无保留价值,走 Db.Deleteable 逃生舱口;同 ReplaceAsync 语义)。
        await roleMenus.Db.RunInTransactionAsync(async () =>
        {
            await roleMenus.Db.Deleteable<SysUserRole>().Where(x => x.RoleId == roleId).ExecuteCommandAsync();
            await roleMenus.Db.Deleteable<SysRoleMenu>().Where(x => x.RoleId == roleId).ExecuteCommandAsync();
            await roleMenus.Db.Deleteable<SysRoleDataScope>().Where(x => x.RoleId == roleId).ExecuteCommandAsync();
        });

        await InvalidatePermissionsAsync(affectedUsers);
        await InvalidateScopesAsync(affectedUsers);
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
    }

    /// <summary>事务内"整删再插"。任一步失败整体回滚,关联不会处于半更新状态。</summary>
    private async Task ReplaceAsync<TLink>(Func<Task<int>> deleteExisting, List<TLink> insertNew) where TLink : BaseEntity, new()
    {
        await roleMenus.Db.RunInTransactionAsync(async () =>
        {
            await deleteExisting();
            if (insertNew.Count > 0) await roleMenus.Db.Insertable(insertNew).ExecuteCommandAsync();
        });
    }

    // 一次批量删,不是逐个往返:改一个角色就可能牵动几千个用户,Redis 上那是几千个 RTT。
    private Task InvalidatePermissionsAsync(IEnumerable<long> userIds) =>
        cache.RemoveManyAsync(userIds.Select(CacheKeys.UserPermissions));

    private Task InvalidateScopesAsync(IEnumerable<long> userIds) =>
        cache.RemoveManyAsync(userIds.Select(CacheKeys.UserDataScope));
}
