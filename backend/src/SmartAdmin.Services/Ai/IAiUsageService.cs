using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// AI 用量记录与查询。<see cref="RecordAsync"/> 由网关每次调用后写一行;
/// <see cref="SummaryAsync"/> / <see cref="TrendAsync"/> / <see cref="PageAsync"/> 服务批次 3 的用量统计页。
/// </summary>
public interface IAiUsageService
{
    /// <summary>写一行调用记录(成败都写);写入失败不应影响调用方的主流程,由实现自行决定是否吞异常并记日志。</summary>
    Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 时间区间(<see cref="AiUsageSummaryInput.From"/>~<see cref="AiUsageSummaryInput.To"/>,闭区间,按 <c>CreateTime</c>)内的用量总计,
    /// 以及按 <see cref="AiUsageSummaryInput.GroupBy"/> 维度的分组聚合。
    /// </summary>
    Task<AiUsageSummary> SummaryAsync(AiUsageSummaryInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按天(服务器本地时区)聚合的用量趋势,<see cref="AiUsageTrendInput.From"/>~<see cref="AiUsageTrendInput.To"/> 闭区间,
    /// 按日期升序返回;区间内没有数据的日期补 0,保证日期连续。
    /// </summary>
    Task<IReadOnlyList<AiUsageTrendPoint>> TrendAsync(AiUsageTrendInput input, CancellationToken cancellationToken = default);

    /// <summary>用量明细分页(不聚合),按 <c>CreateTime desc</c> 排序。</summary>
    Task<PagedList<SysAiUsageLog>> PageAsync(AiUsagePageInput input, CancellationToken cancellationToken = default);
}

/// <summary>一次 AI 调用的落库素材,对应 <see cref="SysAiUsageLog"/> 的业务字段。</summary>
public sealed record AiUsageRecord(
    long ProviderId,
    string ProviderCode,
    string Model,
    string Scene,
    long? UserId,
    int InputTokens,
    int OutputTokens,
    AiUsageSource UsageSource,
    int LatencyMs,
    bool Success,
    int? ErrorCode,
    string? ErrorMessage,
    bool Streamed,
    string? RequestId);
