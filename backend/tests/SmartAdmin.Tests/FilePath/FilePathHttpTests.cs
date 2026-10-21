using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径的 HTTP 入口:上传前的角色判断、<c>viewUrl</c> 的签发条件、下载的响应头与 Range、
/// 签名直链的内联白名单与验签、分片流程,以及文件服务器密码不进操作日志、模块开关整体下线。
/// 文件服务器和文件路径经服务预置,HTTP 只测控制器本身;断言读信封里的 <c>code</c>,只有直链和下载的成功分支看状态码。
/// </summary>
public class FilePathHttpTests
{
    private const string DownloadBase = "/api/v1/sys/file-path/download/";

    private static string NewCode() => $"h-{Guid.NewGuid():N}";

    /// <summary>登记一条文件夹为 <c>Docs</c>、只收 <c>.pdf</c> 的文件路径;角色列表为 null = 所有登录用户。</summary>
    private static async Task<string> AddPathAsync(
        FilePathTestEnv env, IReadOnlyList<long>? upload = null, IReadOnlyList<long>? download = null)
    {
        var code = NewCode();
        await env.Service<IFilePathConfigService>().AddAsync(new FilePathAddInput(
            code, "订单附件", await env.AddServerAsync(), "Docs", FilePathNamingRule.YearMonthOriginal,
            FilePathConflictMode.Reject, false, ".pdf", 10, upload, download, true, null));
        return code;
    }

    /// <summary>直接把文件放进文件夹(下载不看后缀白名单,所以可以放 png、txt)。</summary>
    private static void PutFile(FilePathTestEnv env, string relative, string content)
    {
        var file = Path.Combine(env.Root, "Docs", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static Task<long> NewRoleAsync(FilePathTestEnv env) => GrantTestKit.CreateRoleAsync(env.Factory, []);

    private static async Task<HttpClient> UserWithRolesAsync(FilePathTestEnv env, params long[] roleIds)
    {
        var (_, account) = await GrantTestKit.CreateUserAsync(env.Factory, roleIds);
        return await GrantTestKit.LoginAsync(env.Factory, account);
    }

    private static async Task<JsonElement> UploadAsync(HttpClient c, string code, string fileName, string content)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName } };
        return await (await c.PostAsync($"/api/v1/sys/file-path/upload/{code}", form)).ReadEnvelope();
    }

    private static int Code(JsonElement envelope) => envelope.GetProperty("code").GetInt32();

    private static string DownloadUrl(string code, string path, string? name = null) =>
        $"{DownloadBase}{code}?path={Uri.EscapeDataString(path)}" + (name is null ? "" : $"&name={Uri.EscapeDataString(name)}");

    [Fact]
    public async Task Upload_denied_without_upload_role()
    {
        await using var env = FilePathTestEnv.Create();
        var (roleA, roleB) = (await NewRoleAsync(env), await NewRoleAsync(env));
        var code = await AddPathAsync(env, upload: [roleA]);
        var client = await UserWithRolesAsync(env, roleB);

        var result = await UploadAsync(client, code, "a.pdf", "x");

        Assert.Equal((int)ErrorCode.FilePathUploadDenied, Code(result));
    }

    [Fact]
    public async Task Chunk_steps_denied_without_upload_role()
    {
        await using var env = FilePathTestEnv.Create();
        var (roleA, roleB) = (await NewRoleAsync(env), await NewRoleAsync(env));
        var code = await AddPathAsync(env, upload: [roleA]);
        var bytes = Encoding.UTF8.GetBytes("x");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // 有上传角色的人建好会话:后面两步拿的是真实存在的 uploadId,拦下它们的只能是角色判断
        var owner = await UserWithRolesAsync(env, roleA);
        var created = await (await owner.PostJson($"/api/v1/sys/file-path/upload/{code}/chunk/init",
            new { fileName = "a.pdf", size = bytes.Length, sha256 })).ReadEnvelope();
        Assert.Equal(0, Code(created));
        var uploadId = created.GetProperty("data").GetProperty("uploadId").GetString()!;

        var client = await UserWithRolesAsync(env, roleB);
        var init = await (await client.PostJson($"/api/v1/sys/file-path/upload/{code}/chunk/init",
            new { fileName = "a.pdf", size = bytes.Length, sha256 })).ReadEnvelope();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(uploadId), "uploadId" },
            { new StringContent("0"), "index" },
            { new ByteArrayContent(bytes), "chunk", "chunk0" },
        };
        var chunk = await (await client.PostAsync($"/api/v1/sys/file-path/upload/{code}/chunk", form)).ReadEnvelope();
        var complete = await (await client.PostJson($"/api/v1/sys/file-path/upload/{code}/chunk/complete", new { uploadId })).ReadEnvelope();

        Assert.Equal((int)ErrorCode.FilePathUploadDenied, Code(init));
        Assert.Equal((int)ErrorCode.FilePathUploadDenied, Code(chunk));
        Assert.Equal((int)ErrorCode.FilePathUploadDenied, Code(complete));
        // 被拒的分片没写进会话
        Assert.Equal(new[] { "session.json" }, Directory.GetFiles(Path.Combine(env.Staging, uploadId)).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public async Task Upload_with_role_returns_path_and_null_view_url_without_download_role()
    {
        await using var env = FilePathTestEnv.Create();
        var (roleA, roleC) = (await NewRoleAsync(env), await NewRoleAsync(env));
        var code = await AddPathAsync(env, upload: [roleA], download: [roleC]);
        var client = await UserWithRolesAsync(env, roleA);

        var result = await UploadAsync(client, code, "a.pdf", "x");

        Assert.Equal(0, Code(result));
        var data = result.GetProperty("data");
        Assert.False(string.IsNullOrEmpty(data.GetProperty("path").GetString()));
        Assert.True(!data.TryGetProperty("viewUrl", out var viewUrl) || viewUrl.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Upload_view_url_present_with_download_role()
    {
        await using var env = FilePathTestEnv.Create();
        var (roleA, roleC) = (await NewRoleAsync(env), await NewRoleAsync(env));
        var code = await AddPathAsync(env, upload: [roleA], download: [roleC]);
        var client = await UserWithRolesAsync(env, roleA, roleC);

        var result = await UploadAsync(client, code, "a.pdf", "x");

        Assert.Equal(0, Code(result));
        Assert.StartsWith(DownloadBase, result.GetProperty("data").GetProperty("viewUrl").GetString());
    }

    [Fact]
    public async Task Empty_role_lists_allow_any_logged_in_user()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var client = await UserWithRolesAsync(env);   // 没有任何角色

        var upload = await UploadAsync(client, code, "a.pdf", "hello");
        Assert.Equal(0, Code(upload));

        var download = await client.GetAsync(DownloadUrl(code, upload.GetProperty("data").GetProperty("path").GetString()!));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("hello", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_denied_without_download_role()
    {
        await using var env = FilePathTestEnv.Create();
        var (roleC, roleB) = (await NewRoleAsync(env), await NewRoleAsync(env));
        var code = await AddPathAsync(env, download: [roleC]);
        PutFile(env, "a.pdf", "x");
        var client = await UserWithRolesAsync(env, roleB);

        var result = await (await client.GetAsync(DownloadUrl(code, "a.pdf"))).ReadEnvelope();

        Assert.Equal((int)ErrorCode.FilePathDownloadDenied, Code(result));
    }

    [Fact]
    public async Task Download_is_attachment_nosniff_and_ranged()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        PutFile(env, "2026/10/a.pdf", "0123456789");
        var client = await GrantTestKit.SuperAdminAsync(env.Factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl(code, "2026/10/a.pdf"));
        request.Headers.Range = new RangeHeaderValue(0, 3);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentRange);
        Assert.Equal("0123", await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Download_name_keeps_real_extension()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        PutFile(env, "a.pdf", "x");
        var client = await GrantTestKit.SuperAdminAsync(env.Factory);

        using var response = await client.GetAsync(DownloadUrl(code, "a.pdf", "报价.exe"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("报价.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task View_link_inline_for_png_attachment_for_txt()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        PutFile(env, "a.png", "not-really-a-png");
        PutFile(env, "a.txt", "text");
        var signer = env.Service<IFilePathLinkSigner>();
        var anonymous = env.Factory.CreateClient();   // 不带令牌:签名本身就是授权

        using var png = await anonymous.GetAsync(signer.BuildUrl(code, "a.png"));
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("attachment", png.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(png.Headers.GetValues("X-Content-Type-Options")));

        using var txt = await anonymous.GetAsync(signer.BuildUrl(code, "a.txt"));
        Assert.Equal(HttpStatusCode.OK, txt.StatusCode);
        Assert.Equal("application/octet-stream", txt.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", txt.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(txt.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("a.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("a.html", "<script>alert(1)</script>")]
    [InlineData("a.SVG", "<svg/>")]
    public async Task View_link_serves_script_capable_files_as_attachment(string fileName, string content)
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        PutFile(env, fileName, content);
        var anonymous = env.Factory.CreateClient();

        // 能带脚本的类型不按文件自己的类型内联:强制另存、禁嗅探,浏览器不会把它当页面执行
        using var response = await anonymous.GetAsync(env.Service<IFilePathLinkSigner>().BuildUrl(code, fileName));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task View_link_pdf_is_inline_and_sandboxed()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        PutFile(env, "a.pdf", "%PDF-1.4");
        var anonymous = env.Factory.CreateClient();

        using var pdf = await anonymous.GetAsync(env.Service<IFilePathLinkSigner>().BuildUrl(code, "a.pdf"));

        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("attachment", pdf.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(pdf.Headers.GetValues("X-Content-Type-Options")));
        // PDF 可以带脚本和表单:内联时再套一层沙箱,里面的脚本拿不到本站的源
        Assert.Equal("sandbox", Assert.Single(pdf.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task View_link_tampered_or_expired_is_403()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        PutFile(env, "a.png", "x");
        PutFile(env, "b.png", "y");
        var signer = env.Service<IFilePathLinkSigner>();
        var anonymous = env.Factory.CreateClient();
        var url = signer.BuildUrl(code, "a.png");
        var exp = long.Parse(System.Web.HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..])["exp"]!);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(url)).StatusCode);   // 对照:原链接可用
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(url.Replace("path=a.png", "path=b.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(url.Replace($"exp={exp}", $"exp={exp + 1}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(url[..url.IndexOf("&sig=")])).StatusCode);   // 缺签名
    }

    [Fact]
    public async Task View_link_hidden_segment_not_found()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var anonymous = env.Factory.CreateClient();

        // 链接合法(签得出来、验得过),但探针文件、覆盖用的临时文件这类点开头的段不对外
        var result = await (await anonymous.GetAsync(env.Service<IFilePathLinkSigner>().BuildUrl(code, ".smartadmin-probe-x"))).ReadEnvelope();

        Assert.Equal((int)ErrorCode.FilePathFileNotFound, Code(result));
    }

    [Fact]
    public async Task Chunk_flow_over_http()
    {
        await using var env = FilePathTestEnv.Create();
        var code = await AddPathAsync(env);
        var client = await GrantTestKit.SuperAdminAsync(env.Factory);
        var bytes = new byte[5 * 1024 * 1024 + 1000];   // 服务端分片 5 MB,多出的 1000 字节成第二片
        new Random(7).NextBytes(bytes);

        var init = await (await client.PostJson($"/api/v1/sys/file-path/upload/{code}/chunk/init",
            new { fileName = "big.pdf", size = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) })).ReadEnvelope();
        Assert.Equal(0, Code(init));
        var uploadId = init.GetProperty("data").GetProperty("uploadId").GetString()!;
        var chunkSize = init.GetProperty("data").GetProperty("chunkSize").GetInt32();
        Assert.Equal(2, init.GetProperty("data").GetProperty("chunkCount").GetInt32());

        for (var index = 0; index < 2; index++)
        {
            var part = bytes.AsMemory(index * chunkSize, Math.Min(chunkSize, bytes.Length - index * chunkSize)).ToArray();
            using var form = new MultipartFormDataContent
            {
                { new StringContent(uploadId), "uploadId" },
                { new StringContent(index.ToString()), "index" },
                { new ByteArrayContent(part), "chunk", $"chunk{index}" },
            };
            var chunk = await (await client.PostAsync($"/api/v1/sys/file-path/upload/{code}/chunk", form)).ReadEnvelope();
            Assert.Equal(0, Code(chunk));
        }

        var complete = await (await client.PostJson($"/api/v1/sys/file-path/upload/{code}/chunk/complete", new { uploadId })).ReadEnvelope();

        Assert.Equal(0, Code(complete));
        var path = complete.GetProperty("data").GetProperty("path").GetString()!;
        Assert.Equal(bytes.Length, complete.GetProperty("data").GetProperty("size").GetInt64());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(env.Root, "Docs", path)));
    }

    [Fact]
    public async Task Server_password_is_masked_in_operation_log()
    {
        await using var env = FilePathTestEnv.Create();
        var client = await GrantTestKit.SuperAdminAsync(env.Factory);
        const string Secret = "S3cret-Pass-9x!";

        var add = await (await client.PostJson("/api/v1/sys/file-server/add", new
        {
            name = $"srv-{Guid.NewGuid():N}", rootPath = env.Root, userName = "svc-files", password = Secret, enabled = true,
        })).ReadEnvelope();
        Assert.Equal(0, Code(add));

        var logs = (await (await client.GetAsync("/api/v1/sys/log/op/page?Current=1&Size=20")).ReadEnvelope())
            .GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        var entry = logs.First(l => l.GetProperty("path").GetString() == "/api/v1/sys/file-server/add");
        var paramJson = entry.GetProperty("paramJson").GetString()!;
        Assert.DoesNotContain(Secret, paramJson);
        Assert.Contains("***", paramJson);   // 确实记下了入参,只是打了码
    }

    /// <summary>四个控制器各请求一个接口,返回 HTTP 状态码;业务失败是 200 + 信封,所以 404 只会来自路由不存在。</summary>
    private static async Task<HttpStatusCode[]> ProbeFourControllersAsync(FilePathTestEnv env)
    {
        var client = await GrantTestKit.SuperAdminAsync(env.Factory);
        return
        [
            (await client.GetAsync("/api/v1/sys/file-server/page")).StatusCode,
            (await client.GetAsync("/api/v1/sys/file-path/page")).StatusCode,
            (await client.PostAsync("/api/v1/sys/file-path/upload/any", new MultipartFormDataContent())).StatusCode,
            (await client.GetAsync(DownloadUrl("any", "a.pdf"))).StatusCode,
        ];
    }

    [Fact]
    public async Task Disabled_module_returns_404()
    {
        await using (var enabled = FilePathTestEnv.Create())
            Assert.DoesNotContain(HttpStatusCode.NotFound, await ProbeFourControllersAsync(enabled));   // 对照:不禁用时四个路由都在

        await using var env = FilePathTestEnv.Create(disabledModules: ["FilePath"]);
        Assert.All(await ProbeFourControllersAsync(env), status => Assert.Equal(HttpStatusCode.NotFound, status));
    }
}
