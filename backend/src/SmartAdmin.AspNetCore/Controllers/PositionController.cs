using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>职位管理端点——职位字典的标准增改删查,权限码即路由(挂 <c>[RolePermission]</c>)。</summary>
[ApiController]
[Route("api/v1/sys/position")]
public class PositionController(IPositionService positionService) : ControllerBase
{
    /// <summary>分页查询职位</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysPosition>>> Page([FromQuery] PositionPageInput input) =>
        Result<PagedList<SysPosition>>.Ok(await positionService.PageAsync(input));

    /// <summary>职位选择器数据源(id / name)。只回 Id 与名称,故 <c>[ActiveSession]</c>:没有职位页权限的角色也能在表单里选职位。</summary>
    [HttpGet("options")]
    [ActiveSession]
    public async Task<Result<IReadOnlyList<PositionOption>>> Options()
    {
        var data = await positionService.OptionsAsync();
        return Result<IReadOnlyList<PositionOption>>.Ok(data);
    }

    /// <summary>按 Id 取单条职位</summary>
    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<SysPosition>> Get(long id) =>
        Result<SysPosition>.Ok(await positionService.GetAsync(id));

    /// <summary>新增职位</summary>
    [HttpPost("add")]
    [RolePermission]
    public async Task<Result<long>> Add(PositionInput input) =>
        Result<long>.Ok(await positionService.AddAsync(input));

    /// <summary>更新职位</summary>
    [HttpPut("{id}")]
    [RolePermission]
    public async Task<Result<bool>> Update(long id, PositionInput input)
    {
        await positionService.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    /// <summary>删除职位</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    public async Task<Result<bool>> Delete(long id)
    {
        await positionService.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }

}
