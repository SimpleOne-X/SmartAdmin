using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>内核默认实现:恒空。消费方接入真实审批/工单系统后整体替换掉本类。</summary>
public class NullWorkbenchTodoProvider : IWorkbenchTodoProvider
{
    public Task<WorkbenchTodoSummary> GetMineAsync(long userId) =>
        Task.FromResult(new WorkbenchTodoSummary());
}
