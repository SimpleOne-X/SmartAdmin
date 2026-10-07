using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>用量汇总的分组维度。</summary>
public enum AiUsageGroupBy
{
    /// <summary>按厂商编码(<see cref="SysAiUsageLog.ProviderCode"/>)分组</summary>
    Provider = 1,

    /// <summary>按模型名(<see cref="SysAiUsageLog.Model"/>)分组</summary>
    Model = 2,

    /// <summary>按业务场景标签(<see cref="SysAiUsageLog.Scene"/>)分组</summary>
    Scene = 3,

    /// <summary>按发起用户(<see cref="SysAiUsageLog.UserId"/>)分组</summary>
    User = 4,
}

/// <summary>用量汇总(<see cref="IAiUsageService.SummaryAsync"/>)入参。</summary>
public sealed record AiUsageSummaryInput(
    DateTime From,
    DateTime To,
    string? ProviderCode,
    string? Model,
    string? Scene,
    long? UserId,
    AiUsageGroupBy GroupBy);

/// <summary>用量趋势(<see cref="IAiUsageService.TrendAsync"/>)入参。</summary>
public sealed record AiUsageTrendInput(
    DateTime From,
    DateTime To,
    string? ProviderCode,
    string? Model,
    string? Scene,
    long? UserId);

/// <summary>用量明细分页(<see cref="IAiUsageService.PageAsync"/>)入参。</summary>
public sealed record AiUsagePageInput : PageInputBase
{
    /// <summary>起始时间(含,按 <c>CreateTime</c> 过滤;可选)</summary>
    public DateTime? From { get; init; }

    /// <summary>截止时间(含,按 <c>CreateTime</c> 过滤;可选)</summary>
    public DateTime? To { get; init; }

    /// <summary>厂商编码(精确匹配,可选)</summary>
    public string? ProviderCode { get; init; }

    /// <summary>模型名(精确匹配,可选)</summary>
    public string? Model { get; init; }

    /// <summary>业务场景标签(精确匹配,可选)</summary>
    public string? Scene { get; init; }

    /// <summary>发起用户 Id(精确匹配,可选)</summary>
    public long? UserId { get; init; }

    /// <summary>是否成功(精确匹配,可选)</summary>
    public bool? Success { get; init; }
}

/// <summary>
/// 时间区间内的用量总计。<c>FailuresByErrorCode</c>:失败按内核错误码分类计数,
/// <see cref="SysAiUsageLog.ErrorCode"/> 为 null 的失败行归到约定 key <c>0</c>。
/// </summary>
public sealed record AiUsageTotals(
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CallCount,
    long FailureCount,
    IReadOnlyDictionary<int, long> FailuresByErrorCode);

/// <summary>
/// 某个分组维度下一行聚合数据。<c>Key</c> 是分组键的原始值(厂商编码/模型名/场景标签,或用户 Id 的字符串形式;
/// 用户为空时为 <c>"0"</c>);<c>Label</c> 是展示名,仅 <see cref="AiUsageGroupBy.User"/> 分组尝试回填用户名,查不到或其它维度为 null;
/// <c>SharePercent</c> 是该组 (InputTokens+OutputTokens) 占总 Token 的百分比,0~100,总量为 0 时给 0。
/// </summary>
public sealed record AiUsageGroupRow(
    string Key,
    string? Label,
    long InputTokens,
    long OutputTokens,
    long CallCount,
    double SharePercent,
    double AvgLatencyMs);

/// <summary>用量汇总结果:总计 + 按 <see cref="AiUsageSummaryInput.GroupBy"/> 维度的分组列表。</summary>
public sealed record AiUsageSummary(AiUsageTotals Totals, IReadOnlyList<AiUsageGroupRow> Groups);

/// <summary>按天聚合的一个趋势点。区间内没有数据的日期也会补 0(供前端画连续折线)。</summary>
public sealed record AiUsageTrendPoint(DateOnly Date, long InputTokens, long OutputTokens, long CallCount);
