using System.Net.Http.Headers;
using System.Text.Json;

namespace SmartAdmin.Tests;

/// <summary>
/// 水印设置随匿名站点信息端点下发:默认关;存了合法值原样透出;写错或越界的值收口成默认 / 边界,
/// 不让一个坏值把整个站点信息(登录页也靠它)拖垮。
/// </summary>
public class WatermarkConfigTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    private static async Task<JsonElement> Watermark(HttpClient anon) =>
        (await (await anon.GetAsync("/api/v1/sys/config/site")).ReadEnvelope())
            .GetProperty("data").GetProperty("watermark").Clone();

    private static async Task Save(HttpClient c, params (string Key, string Value)[] items)
    {
        var batch = await c.PutJson("/api/v1/sys/config/batch",
            items.Select(i => new { configKey = i.Key, configValue = i.Value }).ToArray());
        Assert.Equal(0, (await batch.ReadEnvelope()).GetProperty("code").GetInt32());
    }

    private static string[] Fields(JsonElement wm) =>
        wm.GetProperty("fields").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task Default_is_off_with_name_account_time()
    {
        using var f = new AdminAppFactory();
        var wm = await Watermark(f.CreateClient()); // 匿名

        Assert.False(wm.GetProperty("enabled").GetBoolean());
        Assert.Equal(["name", "account", "time"], Fields(wm));
        Assert.Equal("", wm.GetProperty("text").GetString());
        Assert.Equal("YYYY-MM-DD HH:mm", wm.GetProperty("timeFormat").GetString());
        Assert.Equal("single", wm.GetProperty("layout").GetString());
        Assert.Equal(14, wm.GetProperty("fontSize").GetInt32());
        Assert.Equal(8, wm.GetProperty("opacity").GetInt32());
        Assert.Equal(-20, wm.GetProperty("rotate").GetInt32());
        Assert.Equal("normal", wm.GetProperty("density").GetString());
        Assert.True(wm.GetProperty("cross").GetBoolean());
    }

    [Fact]
    public async Task Saved_values_are_exposed_and_fields_are_put_in_fixed_order()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        await Save(c,
            ("sys.watermark.enabled", "true"),
            ("sys.watermark.fields", "time, text ,name,unknown"), // 乱序、带空格、含未知项
            ("sys.watermark.text", "内部资料 严禁外传"),
            ("sys.watermark.timeFormat", "YYYY-MM-DD HH:mm:ss"),
            ("sys.watermark.layout", "multi"),
            ("sys.watermark.fontSize", "20"),
            ("sys.watermark.opacity", "15"),
            ("sys.watermark.rotate", "-30"),
            ("sys.watermark.density", "dense"),
            ("sys.watermark.cross", "false"));

        var wm = await Watermark(f.CreateClient());
        Assert.True(wm.GetProperty("enabled").GetBoolean());
        Assert.Equal(["name", "time", "text"], Fields(wm)); // 固定顺序,未知项丢掉
        Assert.Equal("内部资料 严禁外传", wm.GetProperty("text").GetString());
        Assert.Equal("YYYY-MM-DD HH:mm:ss", wm.GetProperty("timeFormat").GetString());
        Assert.Equal("multi", wm.GetProperty("layout").GetString());
        Assert.Equal(20, wm.GetProperty("fontSize").GetInt32());
        Assert.Equal(15, wm.GetProperty("opacity").GetInt32());
        Assert.Equal(-30, wm.GetProperty("rotate").GetInt32());
        Assert.Equal("dense", wm.GetProperty("density").GetString());
        Assert.False(wm.GetProperty("cross").GetBoolean());
    }

    [Fact]
    public async Task Out_of_range_or_malformed_values_are_clamped_not_failed()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        await Save(c,
            ("sys.watermark.enabled", "yes"), // 不是 bool → 关
            ("sys.watermark.text", new string('字', 60)),
            ("sys.watermark.timeFormat", "dd/MM/yyyy"),
            ("sys.watermark.layout", "diagonal"),
            ("sys.watermark.fontSize", "999"),
            ("sys.watermark.opacity", "0"),
            ("sys.watermark.rotate", "abc"),
            ("sys.watermark.density", "huge"),
            ("sys.watermark.cross", "maybe")); // 不是 bool → 默认 true

        var anon = f.CreateClient();
        var resp = await anon.GetAsync("/api/v1/sys/config/site");
        Assert.True(resp.IsSuccessStatusCode); // 站点信息本身不受影响
        var wm = (await resp.ReadEnvelope()).GetProperty("data").GetProperty("watermark");

        Assert.False(wm.GetProperty("enabled").GetBoolean());
        Assert.Equal(40, wm.GetProperty("text").GetString()!.Length);
        Assert.Equal("YYYY-MM-DD HH:mm", wm.GetProperty("timeFormat").GetString());
        Assert.Equal("single", wm.GetProperty("layout").GetString());
        Assert.Equal(28, wm.GetProperty("fontSize").GetInt32());
        Assert.Equal(2, wm.GetProperty("opacity").GetInt32());
        Assert.Equal(-20, wm.GetProperty("rotate").GetInt32());
        Assert.Equal("normal", wm.GetProperty("density").GetString());
        Assert.True(wm.GetProperty("cross").GetBoolean());
    }

    [Fact]
    public async Task Empty_fields_means_nothing_selected_and_is_respected()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        await Save(c, ("sys.watermark.fields", ""));

        Assert.Empty(Fields(await Watermark(f.CreateClient()))); // 空串 = 管理员一项都没勾,不回退默认
    }
}
