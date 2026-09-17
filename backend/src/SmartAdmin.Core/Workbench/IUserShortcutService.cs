namespace SmartAdmin.Core;

/// <summary>
/// 工作台快捷方式:用户手动置顶 + 高频访问自动补位,跨设备持久化。
/// </summary>
public interface IUserShortcutService
{
    /// <summary>置顶的在前(按置顶时间倒序)+ 未置顶里访问计数最高的补齐到 cap 条;
    /// 对应菜单已被删除的行自动过滤掉,不返回。</summary>
    Task<IReadOnlyList<UserShortcutItem>> ListMineAsync(long userId, int cap = 8);

    /// <summary>置顶(幂等,已置顶则不动);没有历史行时新建一行。</summary>
    Task PinAsync(long userId, string menuPath);

    /// <summary>取消置顶(幂等,未置顶或没有历史行都不报错);访问计数行保留。</summary>
    Task UnpinAsync(long userId, string menuPath);

    /// <summary>记一次访问:没有历史行则新建(VisitCount=1),有则 +1。</summary>
    Task RecordVisitAsync(long userId, string menuPath);
}

public record UserShortcutItem
{
    public string MenuPath { get; init; } = "";
    public bool Pinned { get; init; }
    public string Title { get; init; } = "";
    public string? Icon { get; init; }
}
