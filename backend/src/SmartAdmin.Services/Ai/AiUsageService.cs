using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <inheritdoc cref="IAiUsageService" />
public class AiUsageService(
    ISqlSugarClient db,
    IRepository<SysAiUsageLog> usageLogs,
    IRepository<SysUser> users) : IAiUsageService
{
    private const int ErrorMessageMaxLength = 512;

    /// <summary>失败行 <c>ErrorCode</c> 为 null 时,在 <see cref="AiUsageTotals.FailuresByErrorCode"/> 里归到的约定 key。</summary>
    private const int UnknownErrorCodeKey = 0;

    /// <inheritdoc />
    public virtual async Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        var errorMessage = record.ErrorMessage;
        if (errorMessage is { Length: > ErrorMessageMaxLength })
            errorMessage = errorMessage[..ErrorMessageMaxLength];

        var entity = new SysAiUsageLog
        {
            ProviderId = record.ProviderId,
            ProviderCode = record.ProviderCode,
            Model = record.Model,
            Scene = record.Scene,
            UserId = record.UserId,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            TotalTokens = record.InputTokens + record.OutputTokens,
            UsageSource = (int)record.UsageSource,
            LatencyMs = record.LatencyMs,
            Success = record.Success,
            ErrorCode = record.ErrorCode,
            ErrorMessage = errorMessage,
            Streamed = record.Streamed,
            RequestId = record.RequestId,
        };
        await db.Insertable(entity).ExecuteCommandAsync(cancellationToken);   // AOP 填雪花 Id/CreateTime
    }

    /// <inheritdoc />
    public virtual async Task<AiUsageSummary> SummaryAsync(AiUsageSummaryInput input, CancellationToken cancellationToken = default)
    {
        var callCount = await Filtered(input).CountAsync(cancellationToken);
        var failureCount = await Filtered(input).Where(x => !x.Success).CountAsync(cancellationToken);
        var inputTokens = await Filtered(input).SumAsync<long>(x => x.InputTokens);
        var outputTokens = await Filtered(input).SumAsync<long>(x => x.OutputTokens);
        var totalTokens = await Filtered(input).SumAsync<long>(x => x.TotalTokens);

        // 失败按错误码分类:失败行本就是全量的小子集,查出来在内存里分组比再研究一次可空列 SQL GroupBy 更稳妥。
        var failedErrorCodes = await Filtered(input).Where(x => !x.Success)
            .Select(x => x.ErrorCode)
            .ToListAsync(cancellationToken);
        var failuresByErrorCode = failedErrorCodes
            .GroupBy(code => code ?? UnknownErrorCodeKey)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var totals = new AiUsageTotals(totalTokens, inputTokens, outputTokens, callCount, failureCount, failuresByErrorCode);
        var groups = await GroupAsync(input, totals.TotalTokens, cancellationToken);
        return new AiUsageSummary(totals, groups);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<AiUsageTrendPoint>> TrendAsync(AiUsageTrendInput input, CancellationToken cancellationToken = default)
    {
        var rows = await Filtered(input.From, input.To, input.ProviderCode, input.Model, input.Scene, input.UserId)
            .Select(x => new { x.CreateTime, x.InputTokens, x.OutputTokens })
            .ToListAsync(cancellationToken);

        // 按天分桶用服务器本地时区(CreateTime 本就是审计 AOP 用本地时钟填的),不下推到 SQL——
        // 各方言的按天截断函数(DATE()/CONVERT/DATE_TRUNC)写法不一致,内存分桶更稳。
        var byDay = rows
            .GroupBy(r => DateOnly.FromDateTime(r.CreateTime))
            .ToDictionary(
                g => g.Key,
                g => (InputTokens: g.Sum(r => (long)r.InputTokens), OutputTokens: g.Sum(r => (long)r.OutputTokens), CallCount: (long)g.Count()));

        var points = new List<AiUsageTrendPoint>();
        var fromDate = DateOnly.FromDateTime(input.From.Date);
        var toDate = DateOnly.FromDateTime(input.To.Date);
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            points.Add(byDay.TryGetValue(date, out var agg)
                ? new AiUsageTrendPoint(date, agg.InputTokens, agg.OutputTokens, agg.CallCount)
                : new AiUsageTrendPoint(date, 0, 0, 0));
        }
        return points;
    }

    /// <inheritdoc />
    public virtual async Task<PagedList<SysAiUsageLog>> PageAsync(AiUsagePageInput input, CancellationToken cancellationToken = default)
    {
        var query = usageLogs.AsQueryable()
            .WhereIF(input.From.HasValue, x => x.CreateTime >= input.From!.Value)
            .WhereIF(input.To.HasValue, x => x.CreateTime <= input.To!.Value)
            .WhereIF(!string.IsNullOrEmpty(input.ProviderCode), x => x.ProviderCode == input.ProviderCode!)
            .WhereIF(!string.IsNullOrEmpty(input.Model), x => x.Model == input.Model!)
            .WhereIF(!string.IsNullOrEmpty(input.Scene), x => x.Scene == input.Scene!)
            .WhereIF(input.UserId.HasValue, x => x.UserId == input.UserId!.Value)
            .WhereIF(input.Success.HasValue, x => x.Success == input.Success!.Value)
            .OrderBy(x => x.CreateTime, OrderByType.Desc);
        return await query.ToPagedListAsync(input.Current, input.Size);
    }

    /// <summary>按 <see cref="AiUsageSummaryInput.GroupBy"/> 维度做 SQL 侧 GroupBy + SqlFunc.Aggregate* 聚合。</summary>
    protected virtual async Task<IReadOnlyList<AiUsageGroupRow>> GroupAsync(
        AiUsageSummaryInput input, long totalTokens, CancellationToken cancellationToken)
    {
        List<(string Key, long InputTokens, long OutputTokens, long CallCount, double AvgLatencyMs)> raw;
        switch (input.GroupBy)
        {
            case AiUsageGroupBy.Provider:
            {
                var agg = await Filtered(input).GroupBy(x => x.ProviderCode)
                    .Select(x => new
                    {
                        Key = x.ProviderCode,
                        InputTokens = SqlFunc.AggregateSum<long>(x.InputTokens),
                        OutputTokens = SqlFunc.AggregateSum<long>(x.OutputTokens),
                        CallCount = SqlFunc.AggregateCount(x.Id),
                        AvgLatencyMs = SqlFunc.AggregateAvg<double>(x.LatencyMs),
                    })
                    .ToListAsync(cancellationToken);
                raw = agg.Select(g => (g.Key ?? "", g.InputTokens, g.OutputTokens, (long)g.CallCount, g.AvgLatencyMs)).ToList();
                break;
            }
            case AiUsageGroupBy.Model:
            {
                var agg = await Filtered(input).GroupBy(x => x.Model)
                    .Select(x => new
                    {
                        Key = x.Model,
                        InputTokens = SqlFunc.AggregateSum<long>(x.InputTokens),
                        OutputTokens = SqlFunc.AggregateSum<long>(x.OutputTokens),
                        CallCount = SqlFunc.AggregateCount(x.Id),
                        AvgLatencyMs = SqlFunc.AggregateAvg<double>(x.LatencyMs),
                    })
                    .ToListAsync(cancellationToken);
                raw = agg.Select(g => (g.Key ?? "", g.InputTokens, g.OutputTokens, (long)g.CallCount, g.AvgLatencyMs)).ToList();
                break;
            }
            case AiUsageGroupBy.Scene:
            {
                var agg = await Filtered(input).GroupBy(x => x.Scene)
                    .Select(x => new
                    {
                        Key = x.Scene,
                        InputTokens = SqlFunc.AggregateSum<long>(x.InputTokens),
                        OutputTokens = SqlFunc.AggregateSum<long>(x.OutputTokens),
                        CallCount = SqlFunc.AggregateCount(x.Id),
                        AvgLatencyMs = SqlFunc.AggregateAvg<double>(x.LatencyMs),
                    })
                    .ToListAsync(cancellationToken);
                raw = agg.Select(g => (g.Key ?? "", g.InputTokens, g.OutputTokens, (long)g.CallCount, g.AvgLatencyMs)).ToList();
                break;
            }
            case AiUsageGroupBy.User:
            {
                var agg = await Filtered(input).GroupBy(x => x.UserId)
                    .Select(x => new
                    {
                        Key = x.UserId,
                        InputTokens = SqlFunc.AggregateSum<long>(x.InputTokens),
                        OutputTokens = SqlFunc.AggregateSum<long>(x.OutputTokens),
                        CallCount = SqlFunc.AggregateCount(x.Id),
                        AvgLatencyMs = SqlFunc.AggregateAvg<double>(x.LatencyMs),
                    })
                    .ToListAsync(cancellationToken);
                // 未登录/后台任务发起的调用 UserId 为 null,归到约定 key "0"(与 FailuresByErrorCode 的 0 同一约定)。
                raw = agg.Select(g => ((g.Key ?? UnknownErrorCodeKey).ToString(), g.InputTokens, g.OutputTokens, (long)g.CallCount, g.AvgLatencyMs)).ToList();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.GroupBy, "未知的用量分组维度");
        }

        var rows = raw
            .Select(g => new AiUsageGroupRow(
                g.Key,
                null,
                g.InputTokens,
                g.OutputTokens,
                g.CallCount,
                totalTokens > 0 ? Math.Round((g.InputTokens + g.OutputTokens) * 100.0 / totalTokens, 2) : 0,
                g.AvgLatencyMs))
            .OrderByDescending(r => r.InputTokens + r.OutputTokens)
            .ToList();

        if (input.GroupBy == AiUsageGroupBy.User)
            await FillUserLabelsAsync(rows, cancellationToken);

        return rows;
    }

    /// <summary>User 分组的 Key 是字符串化的 UserId;按本页出现的 Id 批量查一次姓名回填(N+1 规避,同 <c>LogService.FillUserNamesAsync</c>)。</summary>
    protected virtual async Task FillUserLabelsAsync(List<AiUsageGroupRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Key)
            .Where(key => key != UnknownErrorCodeKey.ToString())
            .Select(key => long.TryParse(key, out var id) ? id : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return;

        var rowsByName = await users.AsQueryable()
            .ClearFilter<ISoftDelete>()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Name })
            .ToListAsync(cancellationToken);
        var nameById = rowsByName.ToDictionary(u => u.Id, u => u.Name);

        for (var i = 0; i < rows.Count; i++)
        {
            if (long.TryParse(rows[i].Key, out var id) && nameById.TryGetValue(id, out var name))
                rows[i] = rows[i] with { Label = name };
        }
    }

    /// <summary>汇总/趋势共用的过滤查询——时间闭区间 + 厂商/模型/场景/用户精确过滤。每次调用起一个新查询,避免复用同一个查询构建器时的潜在状态串扰。</summary>
    private ISugarQueryable<SysAiUsageLog> Filtered(AiUsageSummaryInput input) =>
        Filtered(input.From, input.To, input.ProviderCode, input.Model, input.Scene, input.UserId);

    private ISugarQueryable<SysAiUsageLog> Filtered(
        DateTime from, DateTime to, string? providerCode, string? model, string? scene, long? userId) =>
        usageLogs.AsQueryable()
            .Where(x => x.CreateTime >= from && x.CreateTime <= to)
            .WhereIF(!string.IsNullOrEmpty(providerCode), x => x.ProviderCode == providerCode!)
            .WhereIF(!string.IsNullOrEmpty(model), x => x.Model == model!)
            .WhereIF(!string.IsNullOrEmpty(scene), x => x.Scene == scene!)
            .WhereIF(userId.HasValue, x => x.UserId == userId!.Value);
}
