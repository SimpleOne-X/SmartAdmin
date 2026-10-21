using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 文件路径下载端点——按编码和相对路径读文件夹里的文件。
/// 不挂 <c>[RolePermission]</c>:谁能下载由该路径配置的下载角色决定(服务层不判角色,这里先查)。
/// 后缀白名单只约束上传,下载不看它。路径原样交给服务和签名器,规范化与路径安全都在那边做。
/// </summary>
[ApiController]
[Route("api/v1/sys/file-path/download")]
[Module("FilePath")]   // 可经 Api:DisabledModules=["FilePath"] 关闭
public class FilePathDownloadController(
    IFilePathDownloadService downloads,
    IFilePathAccessChecker access,
    IFilePathLinkSigner signer) : ControllerBase
{
    /// <summary>
    /// 下载文件(以附件返回,支持 Range)。<c>name</c> 可指定下载时给用户看的文件名,
    /// 后缀始终沿用实际文件的(见 <see cref="FilePathRules.DownloadName"/>)。
    /// </summary>
    [HttpGet("{code}")]
    [ActiveSession]
    public async Task<IActionResult> Download(string code, [FromQuery] string path, [FromQuery] string? name)
    {
        AdminException.ThrowIf(!await access.CanDownloadAsync(code, HttpContext.RequestAborted), ErrorCode.FilePathDownloadDenied);
        var download = await downloads.OpenReadAsync(code, path, HttpContext.RequestAborted);
        return Attachment(download, name);
    }

    /// <summary>
    /// 签名直链(匿名)——给 <c>&lt;img src&gt;</c> 这类带不了 Authorization 头的场景用,链接由上传出参的 <c>viewUrl</c> 给出。
    /// 匿名是<b>刻意的</b>,但不可伪造:签名编的是编码、规范化后的路径和过期时刻(见 <see cref="IFilePathLinkSigner"/>),
    /// 拿得到链接才拿得到文件,签名本身就是授权,所以这里不做角色判断。
    /// <para>验签先于读文件:参数不全、签名不对、过期都是 403,不让人拿它探测"这个文件存不存在"。验过之后服务抛的业务错误走正常信封。</para>
    /// <para><b>不信文件夹里文件的类型</b>:匿名 + inline 若按文件自带的类型回吐,<c>text/html</c> / <c>image/svg+xml</c> 会被浏览器当脚本渲染,
    /// 与前端同源 → 存储型 XSS。因此按<b>后缀</b>在白名单里取服务端权威类型:是"可安全内联"的图片/PDF 才 inline,
    /// 其余一律 <c>application/octet-stream</c> + 强制另存,再叠一层 <c>nosniff</c> 禁嗅探;内联的再加 <c>Content-Security-Policy: sandbox</c>。</para>
    /// </summary>
    [HttpGet("{code}/view")]
    [AllowAnonymous]
    public async Task<IActionResult> View(
        string code, [FromQuery] string? path, [FromQuery] long? exp, [FromQuery] string? sig, [FromQuery] string? name)
    {
        if (string.IsNullOrEmpty(path) || exp is not { } expiresAt || string.IsNullOrEmpty(sig) || !signer.Verify(code, path, expiresAt, sig))
            return StatusCode(StatusCodes.Status403Forbidden);

        var download = await downloads.OpenReadAsync(code, path, HttpContext.RequestAborted);
        if (!InlineSafeTypes.TryGetValue(download.Extension, out var safeType))
            return Attachment(download, name);   // 含 svg/html/office/zip 及一切未知后缀:浏览器不会把它当页面执行

        Response.Headers.XContentTypeOptions = "nosniff";
        // 纵深防御:PDF 能带脚本和表单,内联打开时放进沙箱(不透明的源),里面的东西拿不到本站的 Cookie 和存储;
        // 浏览器自带的 PDF 查看器在沙箱里照常显示,图片不受影响,所以内联的一律加
        Response.Headers.ContentSecurityPolicy = "sandbox";
        return File(download.Content, safeType, enableRangeProcessing: true);   // 已知安全的图片/PDF:inline 渲染
    }

    // 可安全 inline 渲染的后缀 → 服务端权威媒体类型。刻意排除 svg(可内嵌脚本)与 html/xml;不在表内的一律走 octet-stream + 另存。
    private static readonly Dictionary<string, string> InlineSafeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
    };

    /// <summary>以附件返回:强制另存 + 禁嗅探 + 支持 Range。File(...) 会在写完响应后释放流。</summary>
    private FileStreamResult Attachment(FilePathDownload download, string? name)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(download.Content, "application/octet-stream", FilePathRules.DownloadName(name, download.FileName), enableRangeProcessing: true);
    }
}
