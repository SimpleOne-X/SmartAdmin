using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IUserMenuGrantService"/> 默认实现。保存按变更集而不是整份替换:整份替换会把没动的行也删了重插,
/// 授权人与授权时间被重置成本次保存的人;两个管理员先后编辑时,后保存的还会静默删掉对方刚加的记录。
/// <para>读接口都按数据范围收口:一个人的权限构成本身是敏感信息。菜单的所属模块、可授与否一律用<b>全表</b>菜单(含停用节点)上溯,
/// 中间一层停用不能让上溯断链。</para>
/// </summary>
public class UserMenuGrantService(
    IRepository<SysUserMenu> grants,
    IRepository<SysUser> users,
    IRepository<SysMenu> menus,
    IRepository<SysModule> modules,
    IRepository<SysRole> roles,
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IUserMenuGrantPolicy policy,
    IDataScopeGuard scopeGuard,
    ICacheProvider cache,
    IEventBus events,
    TimeProvider time,
    ICurrentUser? currentUser = null) : IUserMenuGrantService
{
    /// <summary>授权理由最长字数,与 <see cref="SysUserMenu.Remark"/> 的列宽一致。</summary>
    protected const int RemarkMaxLength = 200;

    /// <summary>「7 天内到期」的天数。</summary>
    protected const int ExpiringDays = 7;

    /// <summary>当前本地时间(与审计字段同口径)。</summary>
    protected DateTime Now => time.GetLocalNow().DateTime;

    /// <inheritdoc />
    public virtual async Task ApplyChangesAsync(long userId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        AdminException.ThrowIf(!await users.AnyAsync(u => u.Id == userId), ErrorCode.UserNotFound);
        await ValidateChangeSetAsync(upserts, removes);
        await policy.EnsureGrantableAsync(userId, upserts, removes);

        var plan = await PlanChangesAsync(userId, upserts, removes);
        // 没有任何实际变化就什么都不做:门户代际一动,所有人的门户缓存都作废,空保存不值得这个代价,也不该惊动订阅者
        if (plan.IsEmpty) return;

        await PersistAsync(userId, plan);
        await AfterCommitAsync(userId, plan);
    }

    /// <summary>
    /// 一次保存里实际发生的变化:库里没有的才算新增,内容有差异的才算修改,库里确有其行的才算移除。
    /// 内容与库里一致的提交、移除本来就没有的记录,都不计入。
    /// </summary>
    protected sealed record GrantChangePlan(IReadOnlyList<SysUserMenu> Added, IReadOnlyList<SysUserMenu> Updated, IReadOnlyList<long> Removed)
    {
        /// <summary>三组都为空,即没有任何需要落库的变化。</summary>
        public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Removed.Count == 0;
    }

    /// <summary>对照库里现状算出实际变化。已有记录的修改直接落在读出来的行对象上,交给 <see cref="PersistAsync"/> 更新。</summary>
    protected virtual async Task<GrantChangePlan> PlanChangesAsync(
        long userId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        var existing = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync()).ToDictionary(g => g.MenuId);
        var added = new List<SysUserMenu>();
        var updated = new List<SysUserMenu>();
        foreach (var u in upserts)
        {
            var remark = NormalizeRemark(u.Remark);
            if (existing.TryGetValue(u.MenuId, out var row))
            {
                if (row.Effect == u.Effect && row.ExpireTime == u.ExpireTime && row.Remark == remark) continue;
                row.Effect = u.Effect;
                row.ExpireTime = u.ExpireTime;
                row.Remark = remark;
                updated.Add(row);
            }
            else
            {
                added.Add(new SysUserMenu { UserId = userId, MenuId = u.MenuId, Effect = u.Effect, ExpireTime = u.ExpireTime, Remark = remark });
            }
        }
        return new GrantChangePlan(added, updated, [.. removes.Where(existing.ContainsKey)]);
    }

    /// <summary>事务内写库:新增插入、修改更新、移除物理删除。任何一步失败整体回滚。</summary>
    protected virtual async Task PersistAsync(long userId, GrantChangePlan plan)
    {
        List<long> removed = [.. plan.Removed];
        await grants.Db.RunInTransactionAsync(async () =>
        {
            if (plan.Added.Count > 0) await grants.InsertRangeAsync([.. plan.Added]);
            foreach (var row in plan.Updated) await grants.UpdateAsync(row);
            // 物理删除:软删残行会撞 (UserId, MenuId) 唯一索引
            if (removed.Count > 0)
                await grants.Db.Deleteable<SysUserMenu>().Where(g => g.UserId == userId && removed.Contains(g.MenuId)).ExecuteCommandAsync();
        });
    }

    /// <summary>提交之后的副作用:失效该用户的权限码缓存与门户代际,发布变更事件。事务回滚时不会走到这里。</summary>
    protected virtual async Task AfterCommitAsync(long userId, GrantChangePlan plan)
    {
        await cache.RemoveAsync(CacheKeys.UserPermissions(userId));
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
        await events.PublishAsync(new UserMenuGrantsChangedEvent(
            userId, currentUser?.UserId, [.. plan.Added.Select(ToUpsert)], [.. plan.Updated.Select(ToUpsert)], plan.Removed));
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<UserMenuGrantItem>> GetGrantsAsync(long userId)
    {
        await EnsureReadableAsync(userId);
        var menuIds = (await menus.AsQueryable().Select(m => m.Id).ToListAsync()).ToHashSet();
        var rows = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync())
            .Where(g => menuIds.Contains(g.MenuId))
            .OrderBy(g => g.MenuId)
            .ToList();
        var people = await UsersByIdAsync(rows.SelectMany(g => new[] { g.CreateUserId, g.UpdateUserId }));
        return [.. rows.Select(g => new UserMenuGrantItem
        {
            MenuId = g.MenuId, Effect = g.Effect, ExpireTime = g.ExpireTime, Remark = g.Remark,
            GrantorId = g.CreateUserId, GrantorName = NameOf(people, g.CreateUserId), GrantTime = g.CreateTime,
            UpdaterId = g.UpdateUserId, UpdaterName = NameOf(people, g.UpdateUserId), UpdateTime = g.UpdateTime,
        })];
    }

    /// <inheritdoc />
    public virtual async Task<UserMenuEffectiveOutput> GetEffectiveAsync(long userId)
    {
        await EnsureReadableAsync(userId);
        var now = Now;
        var target = (await users.GetByIdAsync(userId))!;
        // 全表菜单(含停用):拒绝按完整的树展开子孙,模块归属沿 ParentId 上溯也不会被停用的中间节点断链
        var allMenus = await menus.AsQueryable().ToListAsync();
        var byId = allMenus.ToDictionary(m => m.Id);
        var rows = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync()).Where(g => byId.ContainsKey(g.MenuId)).ToList();

        // 启用角色与权限聚合同一口径:停用的角色既不贡献菜单,也不当作「来源」列出
        var roleIds = await userRoles.AsQueryable()
            .InnerJoin<SysRole>((ur, r) => ur.RoleId == r.Id && r.Enabled)
            .Where((ur, r) => ur.UserId == userId)
            .Select((ur, r) => ur.RoleId).ToListAsync();
        var roleNames = roleIds.Count == 0
            ? new Dictionary<long, string>()
            : (await roles.AsQueryable().Where(r => roleIds.Contains(r.Id)).ToListAsync()).ToDictionary(r => r.Id, r => r.Name);
        List<SysRoleMenu> links = roleIds.Count == 0 ? [] : await roleMenus.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).ToListAsync();
        var rolesByMenu = links.GroupBy(l => l.MenuId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.Select(l => roleNames.GetValueOrDefault(l.RoleId)).OfType<string>().Distinct().Order(StringComparer.Ordinal)]);

        // 超管照旧全量放行,单独授权不参与计算
        var result = target.IsSuperAdmin
            ? new UserMenuGrantResult(allMenus.Where(m => m.Enabled).Select(m => m.Id).ToHashSet(), new HashSet<long>(), null)
            : UserMenuGrantRules.Compute(links.Select(l => l.MenuId), rows, allMenus, now);

        var grantable = await policy.GetGrantableModuleIdsAsync();
        var block = await policy.GetTargetBlockAsync(userId);
        var grantByMenu = rows.ToDictionary(g => g.MenuId);
        // 生效中的拒绝节点。超管不走单独授权的计算,名下即使留着记录也不产生拒绝
        HashSet<long> activeDenyIds = target.IsSuperAdmin
            ? []
            : [.. rows.Where(g => g.Effect == UserMenuEffect.Deny && UserMenuGrantRules.IsActive(g, now)).Select(g => g.MenuId)];
        var nodes = allMenus.Select(m =>
        {
            var moduleId = MenuTree.RootModuleId(m.Id, byId);
            grantByMenu.TryGetValue(m.Id, out var g);
            var activeDeny = activeDenyIds.Contains(m.Id);
            return new UserMenuEffectiveNode
            {
                MenuId = m.Id,
                ModuleId = moduleId,
                Effective = result.EffectiveMenuIds.Contains(m.Id),
                Roles = rolesByMenu.GetValueOrDefault(m.Id) ?? [],
                Grant = g?.Effect,
                ExpireTime = g?.ExpireTime,
                Expired = g is not null && !UserMenuGrantRules.IsActive(g, now),
                // 只看祖先,和节点自己有没有拒绝无关:自己与祖先都被拒时,撤掉自己的拒绝并不能恢复它
                DeniedByAncestor = MenuTree.HasAncestorIn(m.Id, byId, activeDenyIds),
                Grantable = grantable is null || (moduleId is { } mid && grantable.Contains(mid)),
                LeakedCodes = activeDeny ? UserMenuGrantRules.LeakedCodes(m.Id, allMenus, result.EffectiveMenuIds) : [],
            };
        }).ToList();

        var allModules = await modules.AsQueryable().OrderBy(m => m.Sort).OrderBy(m => m.Id).ToListAsync();
        return new UserMenuEffectiveOutput
        {
            UserId = userId,
            HasRoles = roleIds.Count > 0,
            TargetEditable = block is null,
            ReadOnlyReason = block,
            DelegatedMaxDays = policy.DelegatedMaxDays,
            Modules = [.. allModules.Select(x => new UserMenuModuleItem
            {
                // 内置系统模块在代码里固定不可转授,不看库里的列值;只有 true 算可转授
                Id = x.Id, Title = x.Title, Delegatable = x.Id != DefaultModuleSeed.BUILTIN_MODULE_ID && x.IsDelegatable == true,
            })],
            Nodes = nodes,
        };
    }

    /// <inheritdoc />
    public virtual async Task<PagedList<UserMenuGrantPageItem>> GetGrantPageAsync(UserMenuGrantPageInput input)
    {
        var now = Now;
        var query = grants.AsQueryable();

        var scoped = await scopeGuard.ResolveScopedUserIdsAsync();
        if (scoped is not null)
        {
            if (scoped.Count == 0) return Empty(input);
            query = query.Where(g => scoped.Contains(g.UserId));
        }
        if (!string.IsNullOrWhiteSpace(input.User))
        {
            var ids = await MatchUserIdsAsync(input.User.Trim());
            if (ids.Count == 0) return Empty(input);
            query = query.Where(g => ids.Contains(g.UserId));
        }
        if (!string.IsNullOrWhiteSpace(input.Grantor))
        {
            var ids = await MatchUserIdsAsync(input.Grantor.Trim());
            if (ids.Count == 0) return Empty(input);
            query = query.Where(g => g.CreateUserId != null && ids.Contains(g.CreateUserId.Value));
        }
        if (input.MenuId is { } menuId) query = query.Where(g => g.MenuId == menuId);
        if (input.Effect is { } effect) query = query.Where(g => g.Effect == effect);
        var soon = now.AddDays(ExpiringDays);
        query = input.Status switch
        {
            UserMenuGrantStatus.Active => query.Where(g => g.ExpireTime == null || g.ExpireTime > now),
            UserMenuGrantStatus.Expiring => query.Where(g => g.ExpireTime != null && g.ExpireTime > now && g.ExpireTime <= soon),
            UserMenuGrantStatus.Expired => query.Where(g => g.ExpireTime != null && g.ExpireTime <= now),
            _ => query,
        };

        // 菜单已不存在(软删)的记录不显示:预取现存菜单 Id,不写跨表子查询
        var allMenus = await menus.AsQueryable().ToListAsync();
        if (allMenus.Count == 0) return Empty(input);
        List<long> menuIds = [.. allMenus.Select(m => m.Id)];
        query = query.Where(g => menuIds.Contains(g.MenuId));

        var page = await query
            .OrderBySafe(input, q => q.OrderByDescending(g => g.CreateTime).OrderBy(g => g.Id))
            .ToPagedListAsync(input.Current, input.Size);

        var byId = allMenus.ToDictionary(m => m.Id);
        var moduleTitles = (await modules.AsQueryable().ToListAsync()).ToDictionary(m => m.Id, m => m.Title);
        var people = await UsersByIdAsync(page.Items.SelectMany(g => new long?[] { g.UserId, g.CreateUserId }));
        return new PagedList<UserMenuGrantPageItem>
        {
            Current = page.Current,
            Size = page.Size,
            Total = page.Total,
            Items = [.. page.Items.Select(g =>
            {
                var moduleId = MenuTree.RootModuleId(g.MenuId, byId);
                var user = people.GetValueOrDefault(g.UserId);
                return new UserMenuGrantPageItem
                {
                    Id = g.Id, UserId = g.UserId, UserAccount = user?.Account ?? "", UserName = user?.Name ?? "",
                    MenuId = g.MenuId, MenuTitle = byId.GetValueOrDefault(g.MenuId)?.Title ?? "",
                    ModuleId = moduleId, ModuleTitle = moduleId is { } mid ? moduleTitles.GetValueOrDefault(mid) : null,
                    Effect = g.Effect, ExpireTime = g.ExpireTime, Status = StatusOf(g, now),
                    GrantorId = g.CreateUserId, GrantorName = NameOf(people, g.CreateUserId), GrantTime = g.CreateTime, Remark = g.Remark,
                };
            })],
        };
    }

    /// <summary>读接口的数据范围收口:一个人的权限构成本身是敏感信息,不能靠猜 Id 读到范围外用户的。</summary>
    protected virtual async Task EnsureReadableAsync(long userId)
    {
        AdminException.ThrowIf(!await scopeGuard.IsUserInScopeAsync(userId), ErrorCode.UserOutOfDataScope);
        AdminException.ThrowIf(!await users.AnyAsync(u => u.Id == userId), ErrorCode.UserNotFound);
    }

    /// <summary>账号或姓名模糊匹配的用户 Id(含已软删的:授权人可能已离职)。</summary>
    private async Task<List<long>> MatchUserIdsAsync(string keyword) =>
        await users.AsQueryable().ClearFilter<ISoftDelete>()
            .Where(u => u.Account.Contains(keyword) || u.Name.Contains(keyword))
            .Select(u => u.Id).ToListAsync();

    /// <summary>按 Id 取用户(含已软删的),给授权人、修改人、目标用户配名字。</summary>
    private async Task<Dictionary<long, SysUser>> UsersByIdAsync(IEnumerable<long?> ids)
    {
        List<long> list = [.. ids.Where(x => x is not null).Select(x => x!.Value).Distinct()];
        if (list.Count == 0) return [];
        return (await users.AsQueryable().ClearFilter<ISoftDelete>().Where(u => list.Contains(u.Id)).ToListAsync()).ToDictionary(u => u.Id);
    }

    private static string? NameOf(Dictionary<long, SysUser> people, long? id) =>
        id is { } v && people.TryGetValue(v, out var u) ? u.Name : null;

    private static UserMenuGrantStatus StatusOf(SysUserMenu g, DateTime now) =>
        !UserMenuGrantRules.IsActive(g, now) ? UserMenuGrantStatus.Expired
        : g.ExpireTime is { } t && t <= now.AddDays(ExpiringDays) ? UserMenuGrantStatus.Expiring
        : UserMenuGrantStatus.Active;

    /// <summary>查询落空时的短路结果:页码、页大小照分页同一套规整与校验,超限的请求不会因为没有数据而"成功"。</summary>
    private static PagedList<UserMenuGrantPageItem> Empty(PageInputBase input) =>
        PagedListExtensions.EmptyPage<UserMenuGrantPageItem>(input.Current, input.Size);

    /// <summary>变更集自身的合法性:同一菜单只出现一次、效果取值有效、到期时间晚于当前时间、备注不超长、菜单存在。</summary>
    protected virtual async Task ValidateChangeSetAsync(IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        var ids = upserts.Select(u => u.MenuId).Concat(removes).ToList();
        AdminException.ThrowIf(ids.Count != ids.Distinct().Count(), ErrorCode.UserMenuGrantInvalid);

        var now = Now;
        AdminException.ThrowIf(upserts.Any(u =>
                u.Effect is not (UserMenuEffect.Allow or UserMenuEffect.Deny)
                || u.ExpireTime is { } t && t <= now
                || u.Remark is { Length: > RemarkMaxLength }),
            ErrorCode.UserMenuGrantInvalid);

        List<long> upsertIds = [.. upserts.Select(u => u.MenuId)];
        if (upsertIds.Count == 0) return;
        var found = await menus.AsQueryable().Where(m => upsertIds.Contains(m.Id)).Select(m => m.Id).ToListAsync();
        AdminException.ThrowIf(found.Count != upsertIds.Count, ErrorCode.MenuNotFound);
    }

    private static string? NormalizeRemark(string? remark) => string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();

    private static UserMenuGrantUpsert ToUpsert(SysUserMenu g) =>
        new() { MenuId = g.MenuId, Effect = g.Effect, ExpireTime = g.ExpireTime, Remark = g.Remark };
}
