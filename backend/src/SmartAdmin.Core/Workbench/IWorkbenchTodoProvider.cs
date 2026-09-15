namespace SmartAdmin.Core;

/// <summary>
/// 工作台待办数据源——内核不产生审批/工单数据,默认恒空;消费方接入真实业务系统后
/// 在 AddSmartAdmin() 之前注册自己的实现即可整体替换(替换性机制一,见 skills/replace-service.md)。
/// </summary>
public interface IWorkbenchTodoProvider
{
    Task<WorkbenchTodoSummary> GetMineAsync(long userId);
}

public record WorkbenchTodoSummary
{
    public int TotalCount { get; init; }
    public IReadOnlyList<WorkbenchTodoItem> Items { get; init; } = [];
}

public record WorkbenchTodoItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Url { get; init; }
    public DateTime CreateTime { get; init; }
}
