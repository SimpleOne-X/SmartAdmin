using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 文件服务器管理端点(文件路径的落盘位置所在的服务器:根路径 + 访问账号)。全部 <c>[RolePermission]</c>——超管放行,
/// 普通用户需被授予对应路由权限码。账号密码经手的写操作要求再认证;入参里的密码在操作日志中按字段名打码。
/// </summary>
[ApiController]
[Route("api/v1/sys/file-server")]
[Module("FilePath")]   // 可经 Api:DisabledModules=["FilePath"] 关闭
public class SysFileServerController(IFileServerService servers) : ControllerBase
{
    /// <summary>分页查询文件服务器</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<FileServerView>>> Page([FromQuery] FileServerPageInput input) =>
        Result<PagedList<FileServerView>>.Ok(await servers.PageAsync(input));

    /// <summary>文件服务器详情(不含密码,只有「是否已设置」标记)</summary>
    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<FileServerView>> Get(long id) =>
        Result<FileServerView>.Ok(await servers.GetAsync(id));

    /// <summary>批量在线状态:当前页各行能否访问根路径;不存在的 Id 不出现在结果里</summary>
    [HttpGet("status")]
    [RolePermission]
    public async Task<Result<IReadOnlyList<FileAccessStatus>>> Status([FromQuery] long[] ids) =>
        Result<IReadOnlyList<FileAccessStatus>>.Ok(await servers.StatusAsync(ids, HttpContext.RequestAborted));

    /// <summary>全部文件服务器(含停用的)的下拉选项,配置文件路径时选服务器用</summary>
    [HttpGet("options")]
    [RolePermission]
    public async Task<Result<IReadOnlyList<FileServerOption>>> Options() =>
        Result<IReadOnlyList<FileServerOption>>.Ok(await servers.OptionsAsync());

    /// <summary>新增文件服务器,返回新 Id</summary>
    [HttpPost("add")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("新增文件服务器")]
    public async Task<Result<long>> Add(FileServerSaveInput input) =>
        Result<long>.Ok(await servers.AddAsync(input));

    /// <summary>更新文件服务器(密码留空表示不改)</summary>
    [HttpPut("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("更新文件服务器")]
    public async Task<Result<bool>> Update(long id, FileServerSaveInput input)
    {
        await servers.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    /// <summary>软删除文件服务器(仍有文件路径引用时拒绝)</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("删除文件服务器")]
    public async Task<Result<bool>> Delete(long id)
    {
        await servers.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }

    /// <summary>测试连接:测弹窗里当前填的内容,不要求先保存;连不上的原因在返回体里,不抛异常</summary>
    [HttpPost("test")]
    [RolePermission]
    [OperationLog("测试文件服务器连接")]
    public async Task<Result<FileAccessTestResult>> Test(FileServerTestInput input) =>
        Result<FileAccessTestResult>.Ok(await servers.TestAsync(input, HttpContext.RequestAborted));
}
