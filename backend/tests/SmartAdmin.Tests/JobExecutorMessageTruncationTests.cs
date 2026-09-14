using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 执行记录 <c>MessageText</c>/<c>ErrorText</c> 的落库截断(GitHub #5/#6):旧实现硬编码 8192 字符、
/// 不可配、超限直接丢弃后续输出、从中间切断也不留标记——多步任务里最有价值的结尾汇总行反而先被挤没。
/// 本类锁住修复后的行为:<see cref="AdminJobsOptions.MaxMessageChars"/> 可配(≤0 不限),超限保留开头 +
/// 结尾并留下可读标记,异常信息走同一套规则。每个用例各起一个独立 <see cref="JobEngineHost"/>(不同用例要配不同
/// 的 <c>MaxMessageChars</c>,不能共享 <see cref="JobSchedulerTests"/> 那种单实例宿主)。
/// </summary>
public class JobExecutorMessageTruncationTests
{
    private sealed class FewLinesJob : IAdminJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < 5; i++) context.Log?.Invoke($"步骤 {i}:完成");
            context.Log?.Invoke("同步结束,共处理完毕");
            return Task.CompletedTask;
        }
    }

    /// <summary>模拟原始 issue 的现场形状:大量重复的逐行明细排在前面,真正有价值的结论排在最后。</summary>
    private sealed class ManyLinesJob : IAdminJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < 2000; i++)
                context.Log?.Invoke($"明细行 {i}:单位编号本地不存在,已按默认单位落库");
            context.Log?.Invoke("同步结束,共处理完毕");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingJob : IAdminJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(new string('x', 5000));
    }

    private static async Task<(JobEngineHost Host, SysJob Job)> RunAsync(string handlerName, Action<AdminJobsOptions>? tuneJobs = null)
    {
        var id = $"jobtrunc-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");
        var host = new JobEngineHost(id, dbFile, "node-a", new MutableTime(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)),
            configure: s =>
            {
                s.TryAddEnumerable(ServiceDescriptor.Scoped<IAdminJob, FewLinesJob>());
                s.TryAddEnumerable(ServiceDescriptor.Scoped<IAdminJob, ManyLinesJob>());
                s.TryAddEnumerable(ServiceDescriptor.Scoped<IAdminJob, ThrowingJob>());
            },
            tuneJobs: tuneJobs);
        host.InitTables();

        var job = await host.InsertJobAsync(handlerName);
        await host.NewScheduler().TickAsync();
        return (host, job);
    }

    [Fact]
    public async Task Output_within_the_limit_is_stored_verbatim()
    {
        var (host, job) = await RunAsync(typeof(FewLinesJob).FullName!);
        await using var _ = host;
        var logs = await host.WaitForLogsAsync(job.Id, l => l.Any(x => x.RunStatus == JobRunStatus.Success));
        var done = Assert.Single(logs);
        Assert.Contains("同步结束,共处理完毕", done.MessageText);
        Assert.DoesNotContain("省略", done.MessageText);
    }

    [Fact]
    public async Task Output_over_the_limit_keeps_head_and_tail_with_a_marker()
    {
        var (host, job) = await RunAsync(typeof(ManyLinesJob).FullName!, jobs => jobs.MaxMessageChars = 4000);
        await using var _ = host;
        var logs = await host.WaitForLogsAsync(job.Id, l => l.Any(x => x.RunStatus == JobRunStatus.Success));
        var done = Assert.Single(logs);

        Assert.NotNull(done.MessageText);
        Assert.True(done.MessageText!.Length <= 4000, $"实际长度 {done.MessageText.Length},预算 4000。");
        Assert.Contains("明细行 0", done.MessageText);            // 开头保留
        Assert.Contains("同步结束,共处理完毕", done.MessageText);   // 结尾保留——旧实现会把这行挤没
        Assert.Contains("中间省略", done.MessageText);
        Assert.Contains("原文共", done.MessageText);
    }

    [Fact]
    public async Task Zero_or_negative_max_message_chars_disables_truncation()
    {
        var (host, job) = await RunAsync(typeof(ManyLinesJob).FullName!, jobs => jobs.MaxMessageChars = 0);
        await using var _ = host;
        var logs = await host.WaitForLogsAsync(job.Id, l => l.Any(x => x.RunStatus == JobRunStatus.Success));
        var done = Assert.Single(logs);

        Assert.True(done.MessageText!.Length > 8192, "输出量远超旧的硬编码上限,新行为不应再截断。");
        Assert.Contains("同步结束,共处理完毕", done.MessageText);
        Assert.DoesNotContain("省略", done.MessageText);
    }

    [Fact]
    public async Task Exception_text_is_truncated_by_the_same_rule()
    {
        var (host, job) = await RunAsync(typeof(ThrowingJob).FullName!, jobs => jobs.MaxMessageChars = 1000);
        await using var _ = host;
        var logs = await host.WaitForLogsAsync(job.Id, l => l.Any(x => x.RunStatus == JobRunStatus.Failed));
        var done = Assert.Single(logs);

        Assert.NotNull(done.ErrorText);
        Assert.True(done.ErrorText!.Length <= 1000, $"实际长度 {done.ErrorText.Length},预算 1000。");
        Assert.Contains("中间省略", done.ErrorText);
    }
}
