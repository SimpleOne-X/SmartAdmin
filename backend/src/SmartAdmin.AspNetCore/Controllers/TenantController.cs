using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>租户注册表端点——业务门禁(仅平台管理员)在 TenantService 里,不在这层。</summary>
[ApiController]
[Route("api/v1/sys/tenant")]
public class TenantController(ITenantService tenantService) : ControllerBase
{
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysTenant>>> Page([FromQuery] TenantPageInput input) =>
        Result<PagedList<SysTenant>>.Ok(await tenantService.PageAsync(input));

    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<SysTenant>> Get(long id) =>
        Result<SysTenant>.Ok(await tenantService.GetAsync(id));

    [HttpPost("add")]
    [RolePermission]
    [OperationLog("新增租户")]
    public async Task<Result<long>> Add(TenantCreateInput input) =>
        Result<long>.Ok(await tenantService.AddAsync(input));

    [HttpPut("{id}")]
    [RolePermission]
    [OperationLog("更新租户")]
    public async Task<Result<bool>> Update(long id, TenantInput input)
    {
        await tenantService.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    [HttpDelete("{id}")]
    [RolePermission]
    [OperationLog("删除租户")]
    public async Task<Result<bool>> Delete(long id)
    {
        await tenantService.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }
}
