using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// AI 厂商管理端点。全部 <c>[RolePermission]</c>——超管放行,普通用户需被授予对应路由权限码。
/// </summary>
[ApiController]
[Route("api/v1/sys/ai/provider")]
[Module("Ai")]   // 可经 Api:DisabledModules=["Ai"] 关闭
public class AiProviderController(IAiProviderService providerService) : ControllerBase
{
    /// <summary>分页查询 AI 厂商(含模型清单)</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<AiProviderView>>> Page([FromQuery] AiProviderPageInput input) =>
        Result<PagedList<AiProviderView>>.Ok(await providerService.PageAsync(input));

    /// <summary>厂商详情(含模型清单)</summary>
    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<AiProviderView>> Get(long id) =>
        Result<AiProviderView>.Ok(await providerService.GetAsync(id));

    /// <summary>厂商预置清单(协议/BaseUrl/鉴权方式/起步模型),新增页选预设时展示</summary>
    [HttpGet("presets")]
    [RolePermission]
    public async Task<Result<IReadOnlyList<AiProviderPreset>>> Presets() =>
        Result<IReadOnlyList<AiProviderPreset>>.Ok(await providerService.PresetsAsync());

    /// <summary>新增 AI 厂商,返回新 Id</summary>
    [HttpPost("add")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("新增 AI 厂商")]
    public async Task<Result<long>> Add(AiProviderAddInput input) =>
        Result<long>.Ok(await providerService.AddAsync(input));

    /// <summary>更新 AI 厂商(不含厂商预设/协议,创建后不可改)</summary>
    [HttpPut("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("更新 AI 厂商")]
    public async Task<Result<bool>> Update(long id, AiProviderUpdateInput input)
    {
        await providerService.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    /// <summary>启停 AI 厂商(启用时若未配置 Key 会被拒绝)</summary>
    [HttpPut("{id}/enabled")]
    [RolePermission]
    [OperationLog("启停 AI 厂商")]
    public async Task<Result<bool>> SetEnabled(long id, [FromQuery] bool enabled)
    {
        await providerService.SetEnabledAsync(id, enabled);
        return Result<bool>.Ok(true);
    }

    /// <summary>软删除 AI 厂商(级联软删其下模型)</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    [OperationLog("删除 AI 厂商")]
    public async Task<Result<bool>> Delete(long id)
    {
        await providerService.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }

    /// <summary>测试连接:取该厂商默认/首个可用模型发一条极短对话,失败原因在返回体里展示,不抛异常</summary>
    [HttpPost("{id}/test")]
    [RolePermission]
    [OperationLog("测试 AI 连接")]
    public async Task<Result<AiTestResult>> Test(long id) =>
        Result<AiTestResult>.Ok(await providerService.TestAsync(id));
}
