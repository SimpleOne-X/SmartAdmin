using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// 配置中心的 Logo 上传(<c>POST /sys/config/logo</c>)与站点信息里 Logo 直链的续签。
/// Logo 走专用端点而不是通用上传:只收按文件头认出来的位图、1 MB 以内,且不受全局上传白名单影响;
/// 存进 <c>sys.site.logo</c> 的签名直链在下发时现签,开了直链寿命也不会过期。
/// </summary>
public class SiteLogoTests
{
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    private static byte[] Png(int bodyBytes = 64) => [.. PngHead, .. new byte[bodyBytes]];

    private static async Task<System.Text.Json.JsonElement> UploadLogo(HttpClient c, byte[] bytes, string name)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
        return await (await c.PostAsync("/api/v1/sys/config/logo", form)).ReadEnvelope();
    }

    private static async Task SaveLogo(HttpClient c, string value)
    {
        var batch = await c.PutJson("/api/v1/sys/config/batch", new object[] { new { configKey = "sys.site.logo", configValue = value } });
        Assert.Equal(0, (await batch.ReadEnvelope()).GetProperty("code").GetInt32());
    }

    private static async Task<string?> SiteLogo(HttpClient anon) =>
        (await (await anon.GetAsync("/api/v1/sys/config/site")).ReadEnvelope())
            .GetProperty("data").GetProperty("logo").GetString();

    [Fact]
    public async Task Png_upload_returns_a_signed_url_that_renders_inline_anonymously()
    {
        using var f = new AdminAppFactory();
        var env = await UploadLogo(await SuperAdminClient(f), Png(), "brand.png");
        Assert.Equal(0, env.GetProperty("code").GetInt32());

        var viewUrl = env.GetProperty("data").GetProperty("viewUrl").GetString()!;
        var resp = await f.CreateClient().GetAsync(viewUrl);   // 登录页是匿名的,Logo 必须不带令牌也能取
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Type_comes_from_file_header_not_file_name()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        // 改了后缀的文本、SVG(可内嵌脚本)一律拒收
        var fake = await UploadLogo(c, Encoding.UTF8.GetBytes("<html>not an image</html>"), "brand.png");
        Assert.Equal((int)ErrorCode.FileExtNotAllowed, fake.GetProperty("code").GetInt32());
        var svg = await UploadLogo(c, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"), "brand.svg");
        Assert.Equal((int)ErrorCode.FileExtNotAllowed, svg.GetProperty("code").GetInt32());

        // 反过来:真 PNG 起了个 .jpg 的名字,存下来的后缀按内容走,/view 下发的也是 image/png
        var renamed = await UploadLogo(c, Png(), "brand.jpg");
        Assert.Equal(0, renamed.GetProperty("code").GetInt32());
        var resp = await f.CreateClient().GetAsync(renamed.GetProperty("data").GetProperty("viewUrl").GetString());
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Over_one_megabyte_is_rejected()
    {
        using var f = new AdminAppFactory();
        var env = await UploadLogo(await SuperAdminClient(f), Png(1024 * 1024), "big.png");
        Assert.Equal((int)ErrorCode.FileTooLarge, env.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Global_upload_whitelist_does_not_block_logo()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);
        var batch = await c.PutJson("/api/v1/sys/config/batch", new object[]
        {
            new { configKey = "sys.upload.allowedExtensions", configValue = ".pdf" },
        });
        Assert.Equal(0, (await batch.ReadEnvelope()).GetProperty("code").GetInt32());

        Assert.Equal(0, (await UploadLogo(c, Png(), "brand.png")).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Expired_signed_logo_url_is_resigned_on_site_info()
    {
        using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?> { ["SmartAdmin:Upload:SignedUrlTtlMinutes"] = "5" },
        };
        var c = await SuperAdminClient(f);
        var anon = f.CreateClient();
        var id = (await UploadLogo(c, Png(), "brand.png")).GetProperty("data").GetProperty("id").GetInt64();

        // 模拟"几天前存的 Logo":签名合法,但 exp 已过
        var signer = f.Services.GetRequiredService<IFileUrlSigner>();
        var past = DateTimeOffset.UtcNow.AddDays(-1);
        var stale = $"/api/v1/sys/file/{id}/view?sig={signer.Sign(id, past)}&exp={past.ToUnixTimeSeconds()}";
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync(stale)).StatusCode);
        await SaveLogo(c, stale);

        var logo = await SiteLogo(anon);
        Assert.NotEqual(stale, logo);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(logo)).StatusCode);
        // 第二次命中缓存,依然是新签的
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(await SiteLogo(anon))).StatusCode);
    }

    [Fact]
    public async Task Forged_or_external_logo_urls_are_returned_unchanged()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);
        var anon = f.CreateClient();

        // 签名对不上的本地直链原样返回:匿名端点不能替人签出任意文件的直链
        const string forged = "/api/v1/sys/file/123/view?sig=forged";
        await SaveLogo(c, forged);
        Assert.Equal(forged, await SiteLogo(anon));

        const string external = "https://cdn.example.com/brand.png";
        await SaveLogo(c, external);
        Assert.Equal(external, await SiteLogo(anon));
    }

    [Fact]
    public async Task Page_can_exclude_keys_claimed_by_structured_forms()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var page = await (await c.GetAsync(
            "/api/v1/sys/config/page?Current=1&Size=1&GroupCode=sys&ExcludedKeys=sys.site.title&ExcludedKeys=sys.site.logo"))
            .ReadEnvelope();
        var all = await (await c.GetAsync("/api/v1/sys/config/page?Current=1&Size=1&GroupCode=sys")).ReadEnvelope();

        // 总数在库里就少了两条(不是前端过滤后页数对不上)
        Assert.Equal(
            all.GetProperty("data").GetProperty("total").GetInt64() - 2,
            page.GetProperty("data").GetProperty("total").GetInt64());
    }
}
