using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// <see cref="AiUsageLogCleanupJob"/> 的保留期回归(照抄 JobLogCleanupJob 的清理语义:分批删、≤0 不清理)。
/// 行的 <c>CreateTime</c> 靠插入前显式赋非默认值绕过审计 AOP 自动填充(AOP 只在 <c>CreateTime == default</c> 时才兜底,
/// 见 SqlSugarSetup.DataExecuting)。
/// </summary>
public class AiUsageLogCleanupJobTests
{
    private static JobExecutionContext Context() => new()
    {
        JobId = 2,
        JobCode = "sys-ai-usage-cleanup",
        JobName = "AI 用量记录清理",
        FireInstanceId = 1,
        ScheduledTime = DateTime.Now,
        FireTime = DateTime.Now,
    };

    private static SysAiUsageLog UsageLog(DateTime createTime) => new()
    {
        ProviderId = 1,
        ProviderCode = "deepseek",
        Model = "deepseek-chat",
        Scene = "system.test",
        InputTokens = 1,
        OutputTokens = 1,
        TotalTokens = 2,
        UsageSource = 1,
        LatencyMs = 10,
        Success = true,
        Streamed = false,
        CreateTime = createTime,
    };

    [Fact]
    public async Task Removes_rows_older_than_retention_and_keeps_the_rest()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ISqlSugarClient>();
        var config = sp.GetRequiredService<IConfigService>();
        var logs = sp.GetRequiredService<IRepository<SysAiUsageLog>>();

        await config.SaveValuesAsync([new ConfigBatchItem { ConfigKey = AiConfigKeys.KEY_USAGE_RETENTION_DAYS, ConfigValue = "30" }]);

        var oldRow = UsageLog(DateTime.Now.AddDays(-31));
        var freshRow = UsageLog(DateTime.Now.AddDays(-1));
        await logs.InsertAsync(oldRow);
        await logs.InsertAsync(freshRow);

        var job = new AiUsageLogCleanupJob(db, config, TimeProvider.System);
        await job.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Null(await db.Queryable<SysAiUsageLog>().Where(l => l.Id == oldRow.Id).FirstAsync());
        Assert.NotNull(await db.Queryable<SysAiUsageLog>().Where(l => l.Id == freshRow.Id).FirstAsync());
    }

    [Fact]
    public async Task Non_positive_retention_skips_cleanup()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ISqlSugarClient>();
        var config = sp.GetRequiredService<IConfigService>();
        var logs = sp.GetRequiredService<IRepository<SysAiUsageLog>>();

        await config.SaveValuesAsync([new ConfigBatchItem { ConfigKey = AiConfigKeys.KEY_USAGE_RETENTION_DAYS, ConfigValue = "0" }]);

        var ancientRow = UsageLog(DateTime.Now.AddYears(-1));
        await logs.InsertAsync(ancientRow);

        var job = new AiUsageLogCleanupJob(db, config, TimeProvider.System);
        await job.ExecuteAsync(Context(), CancellationToken.None);

        Assert.NotNull(await db.Queryable<SysAiUsageLog>().Where(l => l.Id == ancientRow.Id).FirstAsync());
    }
}
