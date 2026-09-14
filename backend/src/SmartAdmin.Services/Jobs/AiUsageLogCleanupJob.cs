using SqlSugar;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 内置狗粮任务:清理过期 AI 用量记录(种子见 <c>DefaultJobSeed</c>)。
/// 保留天数走配置中心 <see cref="AiConfigKeys.KEY_USAGE_RETENTION_DAYS"/>(默认 90,≤0 不清理);
/// 分批 500 防长事务,照抄 <see cref="JobLogCleanupJob"/>。
/// </summary>
public class AiUsageLogCleanupJob(ISqlSugarClient db, IConfigService config, TimeProvider time) : IAdminJob
{
    /// <inheritdoc />
    public virtual async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var raw = await config.GetValueByKeyAsync(AiConfigKeys.KEY_USAGE_RETENTION_DAYS);
        var days = int.TryParse(raw, out var d) ? d : 90;

        var removed = 0;
        if (days > 0)
        {
            var cutoff = JobTime.Truncate(time.GetLocalNow().DateTime).AddDays(-days);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = await db.Queryable<SysAiUsageLog>()
                    .Where(l => l.CreateTime < cutoff)
                    .Take(500)
                    .Select(l => l.Id)
                    .ToListAsync();
                if (ids.Count == 0) break;
                removed += await db.Deleteable<SysAiUsageLog>().In(ids).ExecuteCommandAsync();
                if (ids.Count < 500) break;
            }
        }

        context.Log?.Invoke($"清理 AI 用量记录 {removed} 行(保留 {days} 天)。");
    }
}
