using System.Net.Http.Headers;
using SmartAdmin.Testing;

namespace SmartAdmin.Tests;

public class UserShortcutTests
{
    private static async Task<HttpClient> LoggedIn(AdminAppFactory f, string account, string password)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken(account, password));
        return c;
    }

    [Fact]
    public async Task Shortcuts_empty_by_default()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        var body = await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope();
        Assert.Equal(0, body.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Pin_then_list_shows_pinned_item_with_menu_title()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");

        var pin = await (await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" })).ReadEnvelope();
        Assert.Equal(0, pin.GetProperty("code").GetInt32());

        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        var items = list.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("/system/user", items[0].GetProperty("menuPath").GetString());
        Assert.True(items[0].GetProperty("pinned").GetBoolean());
        Assert.Equal("用户管理", items[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Pin_is_idempotent()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });
        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Unpin_removes_pin_but_keeps_visit_history()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });
        var unpin = await (await admin.PutJson("/api/v1/personal/shortcuts/unpin", new { menuPath = "/system/user" })).ReadEnvelope();
        Assert.Equal(0, unpin.GetProperty("code").GetInt32());

        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        Assert.Empty(list.EnumerateArray());   // 未置顶且访问计数 0,不进 cap 列表
    }

    [Fact]
    public async Task Visit_recorded_items_show_up_unpinned_and_pinned_come_first()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");

        for (var i = 0; i < 3; i++)
            await admin.PostJson("/api/v1/personal/shortcuts/visit", new { menuPath = "/system/role" });
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });

        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        var items = list.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("/system/user", items[0].GetProperty("menuPath").GetString());   // 置顶的在前
        Assert.True(items[0].GetProperty("pinned").GetBoolean());
        Assert.Equal("/system/role", items[1].GetProperty("menuPath").GetString());
        Assert.False(items[1].GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task Shortcuts_are_per_user()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        await admin.PostJson("/api/v1/sys/user",
            new { account = "quinn", password = "InitPass123", name = "quinn", enabled = true, roleIds = Array.Empty<long>() });
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });

        var quinn = await LoggedIn(f, "quinn", "InitPass123");
        var list = (await (await quinn.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        Assert.Empty(list.EnumerateArray());
    }
}
