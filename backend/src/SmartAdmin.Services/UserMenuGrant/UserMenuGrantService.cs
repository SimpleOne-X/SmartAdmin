using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IUserMenuGrantService"/> 默认实现。保存按变更集而不是整份替换:整份替换会把没动的行也删了重插,
/// 授权人与授权时间被重置成本次保存的人;两个管理员先后编辑时,后保存的还会静默删掉对方刚加的记录。
/// </summary>
public class UserMenuGrantService(
    IRepository<SysUserMenu> grants,
    IRepository<SysUser> users,
    IRepository<SysMenu> menus,
    IUserMenuGrantPolicy policy,
    ICacheProvider cache,
    IEventBus events,
    TimeProvider time,
    ICurrentUser? currentUser = null) : IUserMenuGrantService
{
    /// <summary>授权理由最长字数,与 <see cref="SysUserMenu.Remark"/> 的列宽一致。</summary>
    protected const int RemarkMaxLength = 200;

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
