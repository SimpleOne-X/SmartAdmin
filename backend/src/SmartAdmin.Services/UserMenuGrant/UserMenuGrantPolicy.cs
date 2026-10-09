using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IUserMenuGrantPolicy"/> 默认实现。<c>currentUser</c> 尾随可选:未注入或未认证(后台任务、启动期)
/// 视为可信系统上下文,与 <see cref="RoleGrantPolicy"/> 同一约定。
/// </summary>
public class UserMenuGrantPolicy(
    IRepository<SysUser> users,
    IRepository<SysMenu> menus,
    IRepository<SysModule> modules,
    IPermissionProvider permissions,
    IDataScopeGuard scopeGuard,
    AdminSecurityOptions security,
    TimeProvider time,
    ICurrentUser? currentUser = null) : IUserMenuGrantPolicy
{
    /// <summary>
    /// 「能做单独授权」的那条权限码。目标用户的有效权限码里有它就是管理员,只有超管能为其单独授权。
    /// 权限码就是规范化路由,与 <c>UserController</c> 上保存端点的路由一字不差。
    /// </summary>
    public const string GrantPermissionCode = "PUT:/api/v1/sys/user/menu";

    /// <summary>超管或系统 / 未认证上下文:不受委派约束。</summary>
    protected bool IsTrusted => currentUser is null || !currentUser.IsAuthenticated || currentUser.IsSuperAdmin;

    /// <summary>当前本地时间(与审计字段同口径)。</summary>
    protected DateTime Now => time.GetLocalNow().DateTime;

    /// <inheritdoc />
    public virtual int? DelegatedMaxDays => IsTrusted || security.DelegatedGrantMaxDays <= 0 ? null : security.DelegatedGrantMaxDays;

    /// <inheritdoc />
    public virtual DateOnly? DelegatedMaxDate => DelegatedMaxDays is { } days ? DateOnly.FromDateTime(Now).AddDays(days) : null;

    /// <inheritdoc />
    public virtual async Task<ErrorCode?> GetTargetBlockAsync(long targetUserId)
    {
        if (currentUser is { IsAuthenticated: true } && currentUser.UserId == targetUserId) return ErrorCode.CannotOperateSelf;
        var target = await users.GetByIdAsync(targetUserId);
        if (target is null) return ErrorCode.UserNotFound;
        if (target.IsSuperAdmin) return ErrorCode.SuperAdminProtected;
        if (IsTrusted) return null;
        if (!await scopeGuard.IsUserInScopeAsync(targetUserId)) return ErrorCode.UserOutOfDataScope;
        // 看目标当前的有效权限码:某人被分进管理员角色之后,原先给他做授权的普通管理员就不能再改他的记录
        var codes = await permissions.GetPermissionCodesAsync(targetUserId);
        return codes.Contains(GrantPermissionCode, StringComparer.OrdinalIgnoreCase) ? ErrorCode.TargetIsDelegatedAdmin : null;
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlySet<long>?> GetGrantableModuleIdsAsync()
    {
        if (IsTrusted) return null;
        var builtinModuleId = DefaultModuleSeed.BUILTIN_MODULE_ID;   // 内置系统模块固定不可转授,不看库里的列值
        // 布尔列写成比较式:SqlServer 的谓词上下文不接受裸布尔
        var ids = await modules.AsQueryable()
            .Where(m => m.IsDelegatable == true && m.Id != builtinModuleId)
            .Select(m => m.Id)
            .ToListAsync();
        return ids.ToHashSet();
    }

    /// <inheritdoc />
    public virtual async Task EnsureGrantableAsync(long targetUserId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        if (await GetTargetBlockAsync(targetUserId) is { } block) throw new AdminException(block);
        if (IsTrusted) return;
        EnsureDelegatedExpiry(upserts);
        await EnsureMenusGrantableAsync([.. upserts.Select(u => u.MenuId), .. removes]);
    }

    /// <summary>
    /// 每一条「允许」必须带到期时间,且到期日不晚于 <see cref="DelegatedMaxDate"/>(按日期判,与界面展示的上限是同一份计算)。
    /// 「拒绝」只会收紧权限,不要求限时。长期权限应当走角色,单独授权本就是临时例外。
    /// </summary>
    protected virtual void EnsureDelegatedExpiry(IReadOnlyCollection<UserMenuGrantUpsert> upserts)
    {
        if (DelegatedMaxDays is not { } maxDays) return;
        var lastDay = DelegatedMaxDate ?? DateOnly.FromDateTime(Now).AddDays(maxDays);
        foreach (var u in upserts.Where(u => u.Effect == UserMenuEffect.Allow))
            AdminException.ThrowIf(u.ExpireTime is not { } t || DateOnly.FromDateTime(t) > lastDay, ErrorCode.DelegatedGrantExpiryInvalid,
                new Dictionary<string, object?> { ["maxDays"] = maxDays });
    }

    /// <summary>
    /// 每一条的菜单都必须属于可转授模块(上溯到根目录取 ModuleId;不挂模块的视为不可转授)。
    /// 移除也要校验:删掉超管加的一条系统模块拒绝等于放大权限,删掉一条系统模块允许也是在改超管的决定。
    /// 菜单字典取<b>全表</b>(含停用节点):中间一层停用不能让上溯断链,否则可转授模块里的菜单会被误判成不属于任何模块。
    /// </summary>
    protected virtual async Task EnsureMenusGrantableAsync(IReadOnlyCollection<long> menuIds)
    {
        if (menuIds.Count == 0) return;
        var grantable = await GetGrantableModuleIdsAsync();
        if (grantable is null) return;
        var byId = (await menus.AsQueryable().ToListAsync()).ToDictionary(m => m.Id);
        foreach (var id in menuIds)
            AdminException.ThrowIf(MenuTree.RootModuleId(id, byId) is not { } moduleId || !grantable.Contains(moduleId),
                ErrorCode.MenuNotGrantable);
    }
}
