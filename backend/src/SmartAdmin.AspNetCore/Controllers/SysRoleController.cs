using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 角色端点。角色生命周期 CRUD(<see cref="IRoleService"/>)+ 角色授权/数据范围(<see cref="IRbacService"/>)。
/// <para>全部挂 <c>[RolePermission]</c>:超管直接放行;普通用户需被授予对应接口的权限码
/// (即种子里"角色-*"菜单)才能代管(安全默认拒绝)。</para>
/// </summary>
[ApiController]
[Route("api/v1/sys/role")]
public class SysRoleController(IRoleService roleService, IRbacService rbac) : ControllerBase
{
    /// <summary>分页查询角色</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysRole>>> Page([FromQuery] RolePageInput input) =>
        Result<PagedList<SysRole>>.Ok(await roleService.PageAsync(input));

    /// <summary>
    /// 角色选择器数据源(仅启用,id / name)。只回 Id 与名称,故 <c>[ActiveSession]</c>:
    /// 没有角色页权限的角色也能在用户表单里选角色;能否真的分配仍由用户更新接口的授权与转授校验把关。
    /// </summary>
    [HttpGet("options")]
    [ActiveSession]
    public async Task<Result<IReadOnlyList<RoleOption>>> Options()
    {
        var data = await roleService.OptionsAsync();
        return Result<IReadOnlyList<RoleOption>>.Ok(data);
    }

    /// <summary>按 Id 取单条角色</summary>
    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<SysRole>> Get(long id) =>
        Result<SysRole>.Ok(await roleService.GetAsync(id));

    /// <summary>新增角色</summary>
    [HttpPost("add")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<long>> Add(RoleInput input) =>
        Result<long>.Ok(await roleService.AddAsync(input));

    /// <summary>更新角色</summary>
    [HttpPut("{id}")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<bool>> Update(long id, RoleInput input)
    {
        await roleService.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    /// <summary>删除角色(级联清关联)</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<bool>> Delete(long id)
    {
        await roleService.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }

    /// <summary>批量删除角色</summary>
    [HttpPost("batch-delete")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<bool>> BatchDelete(BatchDeleteInput input)
    {
        await roleService.DeleteBatchAsync(input.Ids);
        return Result<bool>.Ok(true);
    }

    /// <summary>取某角色当前授予的菜单 Id 集合(授权抽屉回显用)。</summary>
    [HttpGet("{id}/menus")]
    [RolePermission]
    public async Task<Result<IReadOnlyCollection<long>>> GetMenus(long id) =>
        Result<IReadOnlyCollection<long>>.Ok(await rbac.GetRoleMenuIdsAsync(id));

    /// <summary>取某角色的数据范围配置(未配置返回 null;数据范围抽屉回显用)。</summary>
    [HttpGet("{id}/datascope")]
    [RolePermission]
    public async Task<Result<SysRoleDataScope?>> GetDataScope(long id) =>
        Result<SysRoleDataScope?>.Ok(await rbac.GetRoleDataScopeAsync(id));

    /// <summary>取某角色当前关联的用户 Id 集合(授权用户抽屉回显用)。</summary>
    [HttpGet("{id}/users")]
    [RolePermission]
    public async Task<Result<IReadOnlyCollection<long>>> GetUsers(long id) =>
        Result<IReadOnlyCollection<long>>.Ok(await rbac.GetRoleUserIdsAsync(id));

    /// <summary>全量设置某角色关联的用户;成功后受影响用户权限+数据范围缓存即时失效。</summary>
    [HttpPut("users")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<bool>> SetUsers(SetRoleUsersInput input)
    {
        await rbac.SetRoleUsersAsync(input.RoleId, input.UserIds);
        return Result<bool>.Ok(true);
    }

    /// <summary>全量设置某角色授予的菜单;成功后受影响用户权限缓存即时失效。</summary>
    [HttpPut("menu")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<bool>> SetMenus(SetRoleMenusInput input)
    {
        await rbac.SetRoleMenusAsync(input.RoleId, input.MenuIds);
        return Result<bool>.Ok(true);
    }

    /// <summary>设置某角色的数据范围(招牌能力);成功后受影响用户数据范围缓存即时失效。</summary>
    [HttpPut("datascope")]
    [RolePermission]
    [RequireReauth]
    public async Task<Result<bool>> SetDataScope(SetRoleDataScopeInput input)
    {
        await rbac.SetRoleDataScopeAsync(input.RoleId, input.ScopeType, input.CustomOrgIds);
        return Result<bool>.Ok(true);
    }
}
