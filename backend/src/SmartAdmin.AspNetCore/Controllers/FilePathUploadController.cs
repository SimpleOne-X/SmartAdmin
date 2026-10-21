using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 文件路径上传端点——按编码把文件写进配置好的文件夹,单文件与分片两条路。
/// 不挂 <c>[RolePermission]</c>(没有按路由授权的权限码):任何登录用户都能进来,
/// 谁能往哪个文件路径上传由该路径配置的上传角色决定,每个动作的第一步就查(服务层不判角色)。
/// 规则校验(只读、后缀、大小、命名)与路径安全由 <see cref="IFilePathUploadService"/> 保证。
/// </summary>
[ApiController]
[Route("api/v1/sys/file-path/upload")]
[Module("FilePath")]   // 可经 Api:DisabledModules=["FilePath"] 关闭
[ActiveSession]
public class FilePathUploadController(
    IFilePathUploadService uploads,
    IFilePathAccessChecker access,
    IFilePathLinkSigner signer) : ControllerBase
{
    /// <summary>
    /// 上传单个文件,返回相对文件夹的路径(业务存它、之后凭它下载)。
    /// 大小只取 <c>file.Length</c>,不接受客户端另报。<c>subPath</c> 仅「业务指定路径」命名规则使用。
    /// </summary>
    [HttpPost("{code}")]
    [OperationLog("文件路径上传")]   // 入参含 IFormFile,脱敏器序列化不了会记占位串,不影响记录本次操作
    public async Task<Result<FilePathUploadOutput>> Upload(string code, IFormFile file, [FromForm] string? subPath)
    {
        await EnsureCanUploadAsync(code);
        // 控制器只负责把 IFormFile 拆成"流 + 元数据"喂给服务;IFormFile 不入 Services 层
        await using var stream = file.OpenReadStream();
        var output = await uploads.UploadAsync(
            code, new FilePathUploadInput(stream, file.FileName, file.Length, subPath), HttpContext.RequestAborted);
        return Result<FilePathUploadOutput>.Ok(await WithViewUrlAsync(code, output));
    }

    /// <summary>分片上传-初始化:按声明的文件名、大小拦截规则,通过才建暂存会话,返回分片大小与片数</summary>
    [HttpPost("{code}/chunk/init")]
    public async Task<Result<FilePathChunkInitOutput>> ChunkInit(string code, FilePathChunkInitInput input)
    {
        await EnsureCanUploadAsync(code);
        return Result<FilePathChunkInitOutput>.Ok(await uploads.ChunkInitAsync(code, input, HttpContext.RequestAborted));
    }

    /// <summary>分片上传-上传一个分片(multipart:uploadId + index + chunk 文件部件);分片长度由服务按应有长度核对</summary>
    [HttpPost("{code}/chunk")]
    public async Task<Result<bool>> Chunk(string code, [FromForm] string uploadId, [FromForm] int index, IFormFile chunk)
    {
        await EnsureCanUploadAsync(code);
        await using var stream = chunk.OpenReadStream();
        await uploads.ChunkAsync(code, uploadId, index, stream, HttpContext.RequestAborted);
        return Result<bool>.Ok(true);
    }

    /// <summary>分片上传-完成:合并 + 按实际大小和当前规则再校验 + 哈希核对 + 写入,返回相对文件夹的路径</summary>
    [HttpPost("{code}/chunk/complete")]
    [OperationLog("文件路径分片上传")]
    public async Task<Result<FilePathUploadOutput>> ChunkComplete(string code, FilePathChunkCompleteInput input)
    {
        await EnsureCanUploadAsync(code);
        var output = await uploads.ChunkCompleteAsync(code, input.UploadId, HttpContext.RequestAborted);
        return Result<FilePathUploadOutput>.Ok(await WithViewUrlAsync(code, output));
    }

    /// <summary>每个动作的第一步:当前用户没有这个文件路径的上传权限就拒绝(编码不存在或停用时由检查器抛未找到)。</summary>
    private async Task EnsureCanUploadAsync(string code) =>
        AdminException.ThrowIf(!await access.CanUploadAsync(code, HttpContext.RequestAborted), ErrorCode.FilePathUploadDenied);

    /// <summary>
    /// 给出参补上签名直链,条件是调用者对这个文件路径有下载权限——直链是匿名可读的能力链接,
    /// 不能发给不该读这里文件的人。URL 是 HTTP 层的事,Services 层不掺和路由。
    /// </summary>
    private async Task<FilePathUploadOutput> WithViewUrlAsync(string code, FilePathUploadOutput output) =>
        await access.CanDownloadAsync(code, HttpContext.RequestAborted)
            ? output with { ViewUrl = signer.BuildUrl(code, output.Path) }
            : output;
}
