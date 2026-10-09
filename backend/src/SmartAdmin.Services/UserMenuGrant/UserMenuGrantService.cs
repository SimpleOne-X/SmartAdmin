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

        var existing = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync()).ToDictionary(g => g.MenuId);
        var added = new List<SysUserMenu>();
        var updated = new List<SysUserMenu>();
        foreach (var u in upserts)
        {
            if (existing.TryGetValue(u.MenuId, out var row))
            {
                row.Effect = u.Effect;
                row.ExpireTime = u.ExpireTime;
                row.Remark = NormalizeRemark(u.Remark);
                updated.Add(row);
            }
            else
            {
                added.Add(new SysUserMenu
                {
                    UserId = userId, MenuId = u.MenuId, Effect = u.Effect, ExpireTime = u.ExpireTime, Remark = NormalizeRemark(u.Remark),
                });
            }
        }
        List<long> removed = [.. removes.Where(existing.ContainsKey)];

        await grants.Db.RunInTransactionAsync(async () =>
        {
            if (added.Count > 0) await grants.InsertRangeAsync(added);
            foreach (var row in updated) await grants.UpdateAsync(row);
            // 物理删除:软删残行会撞 (UserId, MenuId) 唯一索引
            if (removed.Count > 0)
                await grants.Db.Deleteable<SysUserMenu>().Where(g => g.UserId == userId && removed.Contains(g.MenuId)).ExecuteCommandAsync();
        });

        await cache.RemoveAsync(CacheKeys.UserPermissions(userId));
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
        await events.PublishAsync(new UserMenuGrantsChangedEvent(
            userId, currentUser?.UserId, [.. added.Select(ToUpsert)], [.. updated.Select(ToUpsert)], removed));
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
