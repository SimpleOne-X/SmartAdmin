using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 文件路径配置端点(业务按编码引用的落盘位置:文件服务器 + 子文件夹 + 命名、后缀、大小、角色规则)。
/// 全部 <c>[RolePermission]</c>——超管放行,普通用户需被授予对应路由权限码。
/// 上传、下载本身见 <see cref="FilePathUploadController"/> 与 <see cref="FilePathDownloadController"/>。
/// </summary>
[ApiController]
[Route("api/v1/sys/file-path")]
[Module("FilePath")]   // 可经 Api:DisabledModules=["FilePath"] 关闭
public class SysFilePathController(IFilePathConfigService paths) : ControllerBase
{
    /// <summary>分页查询文件路径</summary>
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<FilePathView>>> Page([FromQuery] FilePathPageInput input) =>
        Result<PagedList<FilePathView>>.Ok(await paths.PageAsync(input));

    /// <summary>文件路径详情</summary>
    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<FilePathView>> Get(long id) =>
        Result<FilePathView>.Ok(await paths.GetAsync(id));

    /// <summary>批量在线状态:当前页各行文件夹能否访问;不存在的 Id 不出现在结果里</summary>
    [HttpGet("status")]
    [RolePermission]
    public async Task<Result<IReadOnlyList<FileAccessStatus>>> Status([FromQuery] long[] ids) =>
        Result<IReadOnlyList<FileAccessStatus>>.Ok(await paths.StatusAsync(ids, HttpContext.RequestAborted));

    /// <summary>白名单根目录与当前平台是否支持账号连接,配置页用它给提示和候选</summary>
    [HttpGet("environment")]
    [RolePermission]
    public Result<FilePathEnvironment> Environment() =>
        Result<FilePathEnvironment>.Ok(paths.GetEnvironment());

    /// <summary>新增文件路径,返回新 Id</summary>
    [HttpPost("add")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("新增文件路径")]
    public async Task<Result<long>> Add(FilePathAddInput input) =>
        Result<long>.Ok(await paths.AddAsync(input));

    /// <summary>更新文件路径(编码保存后不可改)</summary>
    [HttpPut("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("更新文件路径")]
    public async Task<Result<bool>> Update(long id, FilePathUpdateInput input)
    {
        await paths.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    /// <summary>软删除文件路径,编码随之释放</summary>
    [HttpDelete("{id}")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("删除文件路径")]
    public async Task<Result<bool>> Delete(long id)
    {
        await paths.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }

    /// <summary>测试连接:测弹窗里当前填的服务器与子文件夹,不要求先保存;可写的行会在文件夹里写入并删除一个探针文件</summary>
    [HttpPost("test")]
    [RolePermission]
    [OperationLog("测试文件路径连接")]
    public async Task<Result<FileAccessTestResult>> Test(FilePathTestInput input) =>
        Result<FileAccessTestResult>.Ok(await paths.TestAsync(input, HttpContext.RequestAborted));
}
