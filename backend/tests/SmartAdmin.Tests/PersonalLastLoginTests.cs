using System.Net.Http.Headers;
using SmartAdmin.Testing;

namespace SmartAdmin.Tests;

public class PersonalLastLoginTests
{
    [Fact]
    public async Task GetLastLogin_first_login_is_null()
    {
        using var f = new AdminAppFactory();
        var admin = f.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await admin.LoginToken("superAdmin", "Test@123456"));
        var add = await (await admin.PostJson("/api/v1/sys/user",
            new { account = "olive", password = "InitPass123", name = "olive", enabled = true, roleIds = Array.Empty<long>() })).ReadEnvelope();
        Assert.Equal(0, add.GetProperty("code").GetInt32());

        var olive = f.CreateClient();
        olive.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await olive.LoginToken("olive", "InitPass123"));

        var body = await (await olive.GetAsync("/api/v1/personal/last-login")).ReadEnvelope();
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task GetLastLogin_second_login_shows_the_one_before()
    {
        using var f = new AdminAppFactory();
        var admin = f.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await admin.LoginToken("superAdmin", "Test@123456"));
        await admin.PostJson("/api/v1/sys/user",
            new { account = "pete", password = "InitPass123", name = "pete", enabled = true, roleIds = Array.Empty<long>() });

        var first = f.CreateClient();
        await first.LoginToken("pete", "InitPass123");   // 第一次登录,只为留一条登录日志

        var second = f.CreateClient();
        second.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await second.LoginToken("pete", "InitPass123"));

        var body = await (await second.GetAsync("/api/v1/personal/last-login")).ReadEnvelope();
        var data = body.GetProperty("data");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, data.ValueKind);
        Assert.True(data.TryGetProperty("time", out _));
    }
}
