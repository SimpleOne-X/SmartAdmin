using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// AI 用量查询端点。三个都是纯查询,全部 <c>[RolePermission]</c>——超管放行,普通用户需被授予对应路由权限码。
/// </summary>
[ApiController]
[Route("api/v1/sys/ai/usage")]
[Module("Ai")]   // 可经 Api:DisabledModules=["Ai"] 关闭
public class AiUsageController(IAiUsageService usage) : ControllerBase
{
    /// <summary>用量汇总:时间区间内的总计 + 按厂商/模型/场景/用户维度的分组聚合</summary>
    [HttpGet("summary")]
    [RolePermission]
    public async Task<Result<AiUsageSummary>> Summary([FromQuery] AiUsageSummaryInput input) =>
        Result<AiUsageSummary>.Ok(await usage.SummaryAsync(input));

    /// <summary>用量趋势:按天聚合的 Token/调用数,区间内无数据的日期补 0</summary>
    [HttpGet("trend")]
    [RolePermission]
    public async Task<Result<IReadOnlyList<AiUsageTrendPoint>>> Trend([FromQuery] AiUsageTrendInput input) =>
        Result<IReadOnlyList<AiUsageTrendPoint>>.Ok(await usage.TrendAsync(input));

    /// <summary>用量明细分页(不聚合)</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysAiUsageLog>>> Page([FromQuery] AiUsagePageInput input) =>
        Result<PagedList<SysAiUsageLog>>.Ok(await usage.PageAsync(input));
}
