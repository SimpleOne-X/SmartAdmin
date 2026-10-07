using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IUserShortcutService"/> 默认实现。仓储层没有 upsert,置顶/取消置顶/记访问都是
/// "先查后写"两步(个人快捷方式规模天然小,不值得为原子递增下推 SQL)。
/// </summary>
public class UserShortcutService(
    IRepository<SysUserShortcut> shortcuts,
    IRepository<SysMenu> menus,
    TimeProvider time) : IUserShortcutService
{
    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<UserShortcutItem>> ListMineAsync(long userId, int cap = 8)
    {
        var rows = await shortcuts.AsQueryable().Where(s => s.UserId == userId).ToListAsync();
        var pinned = rows.Where(r => r.Pinned).OrderByDescending(r => r.PinnedAt).ToList();
        var suggestedCap = Math.Max(0, cap - pinned.Count);
        // VisitCount > 0:没访问过就不该出现在"常去"里(取消置顶后留下的空行同理不冒出来凑数)
        var suggested = rows.Where(r => !r.Pinned && r.VisitCount > 0)
            .OrderByDescending(r => r.VisitCount).Take(suggestedCap).ToList();
        var ordered = pinned.Concat(suggested).ToList();
        if (ordered.Count == 0) return [];

        var paths = ordered.Select(r => r.MenuPath).Distinct().ToList();
        var menuByPath = (await menus.AsQueryable().Where(m => paths.Contains(m.Path!)).ToListAsync())
            .ToDictionary(m => m.Path!, m => m);

        return [.. ordered
            .Where(r => menuByPath.ContainsKey(r.MenuPath))   // 菜单已被删除的行自动过滤掉
            .Select(r => new UserShortcutItem
            {
                MenuPath = r.MenuPath,
                Pinned = r.Pinned,
                Title = menuByPath[r.MenuPath].Title,
                Icon = menuByPath[r.MenuPath].Icon,
            })];
    }

    /// <inheritdoc />
    public virtual async Task PinAsync(long userId, string menuPath)
    {
        var row = await shortcuts.GetFirstAsync(s => s.UserId == userId && s.MenuPath == menuPath);
        var now = time.GetLocalNow().DateTime;
        if (row is null)
        {
            await shortcuts.InsertAsync(new SysUserShortcut { UserId = userId, MenuPath = menuPath, Pinned = true, PinnedAt = now });
            return;
        }
        if (row.Pinned) return;
        row.Pinned = true;
        row.PinnedAt = now;
        await shortcuts.UpdateAsync(row);
    }

    /// <inheritdoc />
    public virtual async Task UnpinAsync(long userId, string menuPath)
    {
        var row = await shortcuts.GetFirstAsync(s => s.UserId == userId && s.MenuPath == menuPath);
        if (row is null || !row.Pinned) return;
        row.Pinned = false;
        row.PinnedAt = null;
        await shortcuts.UpdateAsync(row);
    }

    /// <inheritdoc />
    public virtual async Task RecordVisitAsync(long userId, string menuPath)
    {
        var row = await shortcuts.GetFirstAsync(s => s.UserId == userId && s.MenuPath == menuPath);
        var now = time.GetLocalNow().DateTime;
        if (row is null)
        {
            await shortcuts.InsertAsync(new SysUserShortcut { UserId = userId, MenuPath = menuPath, VisitCount = 1, LastVisitAt = now });
            return;
        }
        row.VisitCount++;
        row.LastVisitAt = now;
        await shortcuts.UpdateAsync(row);
    }
}
