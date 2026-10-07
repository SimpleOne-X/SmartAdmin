using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;
using static SmartAdmin.Tests.ExternalAuthProviderTestSupport;

namespace SmartAdmin.Tests;

/// <summary>
/// 「登录方式」管理端点(<see cref="ExternalAuthProviderController"/>)的 HTTP 级锁:权限码、再认证门槛、
/// 响应里任何地方都没有机密明文、操作日志里机密被打码、种子按钮真能授出这些端点。
/// </summary>
public class ExternalAuthProviderEndpointTests
{
    private const string Route = "/api/v1/sys/external-auth/providers";

    private static object AcmeBody(string appId = "app-1", string? secret = AcmeSecret) => new
    {
        type = "acme",
        displayName = "Acme 登录",
        values = new { appId },
        secrets = secret is null ? null : new { appSecret = secret },
    };

    // ── 声明式:权限与再认证 ─────────────────────────────────────────

    [Fact]
    public void Endpoints_declare_permission_and_reauth_as_designed()
    {
        var type = typeof(ExternalAuthProviderController);
        Assert.Equal("ExternalAuth", type.GetCustomAttribute<ModuleAttribute>()!.Name);
        Assert.Equal("api/v1/sys/external-auth/providers", type.GetCustomAttribute<RouteAttribute>()!.Template);

        MethodInfo M(string name) => type.GetMethod(name)!;
        foreach (var name in new[] { "Get", "Save", "Delete", "Test" })
            Assert.NotNull(M(name).GetCustomAttribute<RolePermissionAttribute>());

        // 写操作(决定谁能登录系统)要近期重新验证身份;连接测试不落库,不要求
        Assert.NotNull(M("Save").GetCustomAttribute<RequireReauthAttribute>());
        Assert.NotNull(M("Delete").GetCustomAttribute<RequireReauthAttribute>());
        Assert.Null(M("Test").GetCustomAttribute<RequireReauthAttribute>());
        Assert.Null(M("Get").GetCustomAttribute<RequireReauthAttribute>());

        Assert.NotNull(M("Save").GetCustomAttribute<OperationLogAttribute>());
        Assert.NotNull(M("Delete").GetCustomAttribute<OperationLogAttribute>());
        Assert.NotNull(M("Test").GetCustomAttribute<OperationLogAttribute>());

        Assert.Equal("{code}", M("Save").GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.Equal("{code}", M("Delete").GetCustomAttribute<HttpDeleteAttribute>()!.Template);
        Assert.Equal("test", M("Test").GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(M("Get").GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public void Request_bodies_name_the_secret_dictionary_so_the_log_masker_catches_it()
    {
        // 脱敏器按属性名子串匹配 secret:名字改了,整包机密就会明文落进操作日志
        foreach (var t in new[] { typeof(ExternalAuthProviderSaveInput), typeof(ExternalAuthProviderTestInput) })
        {
            var prop = t.GetProperty("Secrets");
            Assert.NotNull(prop);
            Assert.True(SensitiveKeys.IsSensitive(prop!.Name));
        }
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        using var f = Factory(new AcmeAuthProviderType());
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PutJson($"{Route}/acme", AcmeBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.DeleteAsync($"{Route}/acme")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostJson($"{Route}/test", AcmeBody())).StatusCode);
    }

    // ── 响应形状与机密不外露 ─────────────────────────────────────────

    [Fact]
    public async Task Save_then_catalog_reports_state_without_any_secret_plaintext()
    {
        using var f = Factory(new AcmeAuthProviderType());
        var admin = await SuperAdminClientAsync(f);

        var put = await admin.PutJson($"{Route}/acme", AcmeBody());
        var putRaw = await put.Content.ReadAsStringAsync();
        Assert.Equal(0, JsonDocument.Parse(putRaw).RootElement.GetProperty("code").GetInt32());

        var get = await admin.GetAsync(Route);
        var getRaw = await get.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(getRaw).RootElement.GetProperty("data");

        // 页面级告警
        Assert.False(data.GetProperty("dataProtectionEphemeral").GetBoolean());
        Assert.False(data.GetProperty("callbackBaseUrlMissing").GetBoolean());

        // 已装类型:官方 acme + 内置 oidc,字段清单带机密/必填/是否决定去向
        var types = data.GetProperty("types").EnumerateArray().ToList();
        var acmeType = types.Single(t => t.GetProperty("type").GetString() == "acme");
        Assert.False(acmeType.GetProperty("allowMultiple").GetBoolean());
        Assert.Equal("Acme", acmeType.GetProperty("defaultDisplayName").GetString());
        var secretField = acmeType.GetProperty("fields").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "appSecret");
        Assert.True(secretField.GetProperty("secret").GetBoolean());
        Assert.True(secretField.GetProperty("required").GetBoolean());
        Assert.False(secretField.GetProperty("definesEndpoint").GetBoolean());
        var oidcType = types.Single(t => t.GetProperty("type").GetString() == "oidc");
        Assert.True(oidcType.GetProperty("allowMultiple").GetBoolean());
        Assert.True(oidcType.GetProperty("fields").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == "authority").GetProperty("definesEndpoint").GetBoolean());

        // 已存 provider:非机密回显、机密只有 hasValue + 尾四位、回调地址、来源
        var acme = data.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "acme");
        Assert.Equal("acme", acme.GetProperty("type").GetString());
        Assert.Equal("Acme 登录", acme.GetProperty("displayName").GetString());
        Assert.Equal("db", acme.GetProperty("source").GetString());
        Assert.True(acme.GetProperty("installed").GetBoolean());
        Assert.True(acme.GetProperty("configured").GetBoolean());
        Assert.Equal("app-1", acme.GetProperty("values").GetProperty("appId").GetString());
        var state = acme.GetProperty("secrets").GetProperty("appSecret");
        Assert.True(state.GetProperty("hasValue").GetBoolean());
        Assert.Equal(AcmeSecret[^4..], state.GetProperty("hint").GetString());
        Assert.EndsWith("/api/v1/auth/external/acme/callback", acme.GetProperty("callbackUri").GetString());
        // 模板:新增时标识还没定,前端把 {code} 换成输入的标识
        Assert.EndsWith("/api/v1/auth/external/{code}/callback", data.GetProperty("callbackUriTemplate").GetString());

        // 整段响应(含保存的响应)序列化后不含任何机密明文
        Assert.DoesNotContain(AcmeSecret, getRaw);
        Assert.DoesNotContain(AcmeSecret, putRaw);
    }

    [Fact]
    public async Task Code_registered_providers_are_listed_read_only()
    {
        using var f = Factory(new AcmeAuthProviderType(), more: s => s.AddSingleton<IExternalAuthProvider>(new CodeRegisteredProvider("corp-sso")));
        var admin = await SuperAdminClientAsync(f);

        var data = (await (await admin.GetAsync(Route)).ReadEnvelope()).GetProperty("data");
        var item = data.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "corp-sso");

        Assert.Equal("code", item.GetProperty("source").GetString());
        Assert.True(item.GetProperty("configured").GetBoolean());

        // 想覆盖它:Code 被占用
        var put = await admin.PutJson($"{Route}/corp-sso", new
        {
            type = "oidc",
            values = new { authority = "https://idp.example.com", clientId = "c" },
            secrets = new { clientSecret = "secret-value-1" },
        });
        Assert.Equal((int)ErrorCode.ExternalAuthCodeExists, (await put.ReadEnvelope()).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Test_endpoint_returns_checks_and_never_the_secret()
    {
        using var f = Factory(new AcmeAuthProviderType());
        var admin = await SuperAdminClientAsync(f);

        var resp = await admin.PostJson($"{Route}/test", AcmeBody());
        var raw = await resp.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");

        Assert.True(data.GetProperty("ok").GetBoolean());
        var checks = data.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal("credential", checks[0].GetProperty("key").GetString());
        Assert.Equal("ok", checks[0].GetProperty("status").GetString());
        Assert.Equal("skipped", checks[1].GetProperty("status").GetString());
        Assert.DoesNotContain(AcmeSecret, raw);   // 类型实现把机密写进了 detail,服务层打码了

        // 测试不落库
        var after = await (await admin.GetAsync(Route)).ReadEnvelope();
        Assert.DoesNotContain(after.GetProperty("data").GetProperty("providers").EnumerateArray(), p => p.GetProperty("code").GetString() == "acme");
    }

    [Fact]
    public async Task Business_errors_carry_codes_and_field_args()
    {
        using var f = Factory(new AcmeAuthProviderType());
        var admin = await SuperAdminClientAsync(f);

        var missing = await (await admin.PutJson($"{Route}/acme", AcmeBody(secret: null))).ReadEnvelope();
        Assert.Equal((int)ErrorCode.ExternalAuthFieldMissing, missing.GetProperty("code").GetInt32());
        Assert.Equal("appSecret", missing.GetProperty("args").GetProperty("field").GetString());

        var invalid = await (await admin.PutJson($"{Route}/wrong-code", AcmeBody())).ReadEnvelope();
        Assert.Equal((int)ErrorCode.ExternalAuthCodeInvalid, invalid.GetProperty("code").GetInt32());

        var blocked = await (await admin.PutJson($"{Route}/keycloak", new
        {
            type = "oidc",
            values = new { authority = "http://169.254.169.254", clientId = "c" },
            secrets = new { clientSecret = "secret-value-1" },
        })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.ExternalAuthEndpointBlocked, blocked.GetProperty("code").GetInt32());

        var notFound = await (await admin.DeleteAsync($"{Route}/acme")).ReadEnvelope();
        Assert.Equal((int)ErrorCode.ExternalAuthProviderNotFound, notFound.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Delete_clears_the_provider_and_releases_the_login_button()
    {
        using var f = Factory(new AcmeAuthProviderType());
        var admin = await SuperAdminClientAsync(f);
        await admin.PutJson($"{Route}/acme", AcmeBody());

        // 配好即出现在登录页方式清单与管理端「全部 provider」(运营开关默认启用)
        var login = await (await f.CreateClient().GetAsync("/api/v1/auth/external/providers")).ReadEnvelope();
        Assert.Contains(login.GetProperty("data").EnumerateArray(), p => p.GetProperty("code").GetString() == "acme");
        var all = await (await admin.GetAsync("/api/v1/auth/external/providers/all")).ReadEnvelope();
        Assert.Contains(all.GetProperty("data").EnumerateArray(), p => p.GetProperty("code").GetString() == "acme");

        var del = await (await admin.DeleteAsync($"{Route}/acme")).ReadEnvelope();
        Assert.Equal(0, del.GetProperty("code").GetInt32());

        login = await (await f.CreateClient().GetAsync("/api/v1/auth/external/providers")).ReadEnvelope();
        Assert.DoesNotContain(login.GetProperty("data").EnumerateArray(), p => p.GetProperty("code").GetString() == "acme");
    }

    // ── 操作日志 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Operation_log_masks_the_secrets_of_save_and_test()
    {
        using var f = Factory(new AcmeAuthProviderType());
        var admin = await SuperAdminClientAsync(f);
        await admin.PutJson($"{Route}/acme", AcmeBody());
        await admin.PostJson($"{Route}/test", AcmeBody());

        var page = await (await admin.GetAsync("/api/v1/sys/log/op/page?Current=1&Size=100")).ReadEnvelope();
        var logs = page.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        var mine = logs.Where(l => (l.GetProperty("path").GetString() ?? "").StartsWith(Route)).ToList();

        Assert.Equal(2, mine.Count);   // 保存与测试各留一条
        foreach (var entry in mine)
        {
            var param = entry.GetProperty("paramJson").GetString()!;
            Assert.DoesNotContain(AcmeSecret, param);
            Assert.Contains("***", param);
            Assert.Contains("app-1", param);   // 非机密字段照常留痕
        }
    }

    // ── 再认证 ───────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_demand_reauth_when_totp_is_on_but_the_connection_test_does_not()
    {
        using var f = Factory(new AcmeAuthProviderType(), new Dictionary<string, string?>
        {
            ["SmartAdmin:Security:Totp:Enabled"] = "true",
            ["SmartAdmin:Security:DataProtection:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });
        var admin = await SuperAdminClientAsync(f);

        var put = await admin.PutJson($"{Route}/acme", AcmeBody());
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal((int)ErrorCode.ReauthRequired, (await put.ReadEnvelope()).GetProperty("code").GetInt32());

        var del = await admin.DeleteAsync($"{Route}/acme");
        Assert.Equal((int)ErrorCode.ReauthRequired, (await del.ReadEnvelope()).GetProperty("code").GetInt32());

        var test = await admin.PostJson($"{Route}/test", AcmeBody());
        Assert.Equal(0, (await test.ReadEnvelope()).GetProperty("code").GetInt32());

        // 通过再认证后写操作放行
        var reauth = await (await admin.PostJson("/api/v1/auth/reauth", new { method = "password", password = "Test@123456" })).ReadEnvelope();
        Assert.Equal(0, reauth.GetProperty("code").GetInt32());
        var saved = await (await admin.PutJson($"{Route}/acme", AcmeBody())).ReadEnvelope();
        Assert.Equal(0, saved.GetProperty("code").GetInt32());
    }

    // ── 种子按钮 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(311, "GET")]      // 配置-查询:目录
    [InlineData(312, "TEST")]     // 配置-新增:连接测试
    [InlineData(313, "PUT")]      // 配置-更新:保存
    [InlineData(314, "DELETE")]   // 配置-删除:清除
    public async Task Seeded_config_buttons_grant_the_matching_endpoint_only(int menuId, string granted)
    {
        using var f = Factory(new AcmeAuthProviderType());
        var (account, password) = await SeedUserWithPermissionAsync(f, menuId);
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken(account, password));

        var attempts = new Dictionary<string, Func<Task<HttpResponseMessage>>>
        {
            ["GET"] = () => c.GetAsync(Route),
            ["TEST"] = () => c.PostJson($"{Route}/test", AcmeBody()),
            ["PUT"] = () => c.PutJson($"{Route}/acme", AcmeBody()),
            ["DELETE"] = () => c.DeleteAsync($"{Route}/acme"),
        };
        foreach (var (name, call) in attempts)
        {
            var resp = await call();
            if (name == granted) Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
            else Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    private static async Task<(string account, string password)> SeedUserWithPermissionAsync(AdminAppFactory f, long menuId)
    {
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var role = new SysRole { Name = "登录方式配置", Code = "extauth-" + Guid.CreateVersion7().ToString("N")[..8], Enabled = true };
        await sp.GetRequiredService<IRepository<SysRole>>().InsertAsync(role);
        await sp.GetRequiredService<IRbacService>().SetRoleMenusAsync(role.Id, [menuId]);

        var account = "extauth-" + Guid.CreateVersion7().ToString("N")[..8];
        const string password = "Limited@123456";
        await sp.GetRequiredService<IUserService>().AddAsync(new AddUserInput
        {
            Account = account, Password = password, Name = "受限用户", Enabled = true, RoleIds = [role.Id],
        });
        return (account, password);
    }
}

/// <summary>
/// 回调地址算法(<see cref="ExternalAuthCallback"/>):登录控制器拼 <c>redirect_uri</c> 与管理页显示的回调地址共用这一份。
/// </summary>
public class ExternalAuthCallbackTests
{
    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static Microsoft.AspNetCore.Http.HttpRequest Request(string pathBase = "")
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost", 5100);
        ctx.Request.PathBase = pathBase;
        return ctx.Request;
    }

    [Fact]
    public void Configured_base_url_is_used_as_is()
    {
        var options = new AdminExternalAuthOptions { CallbackBaseUrl = "https://gw.example.com/admin/" };

        Assert.False(ExternalAuthCallback.BaseUrlMissing(options, new Env("Production")));
        Assert.Equal("https://gw.example.com/admin/api/v1/auth/external/github/callback",
            ExternalAuthCallback.BuildUri(options, new Env("Production"), Request(), "github"));
        Assert.Equal("/admin", ExternalAuthCallback.PathBase(options, Request()));
    }

    [Fact]
    public void Development_falls_back_to_the_request_host_and_path_base()
    {
        var options = new AdminExternalAuthOptions();

        Assert.False(ExternalAuthCallback.BaseUrlMissing(options, new Env("Development")));
        Assert.Equal("http://localhost:5100/sub/api/v1/auth/external/wecom/callback",
            ExternalAuthCallback.BuildUri(options, new Env("Development"), Request("/sub"), "wecom"));
    }

    [Fact]
    public void Production_without_a_base_url_is_reported_and_refuses_to_build()
    {
        var options = new AdminExternalAuthOptions { CallbackBaseUrl = "  " };

        Assert.True(ExternalAuthCallback.BaseUrlMissing(options, new Env("Production")));
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAuthCallback.BuildUri(options, new Env("Production"), Request(), "github"));   // 不能靠可伪造的 Host 头推断
    }
}
