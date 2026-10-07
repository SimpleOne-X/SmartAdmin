using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;
using SqlSugar;
using static SmartAdmin.Tests.ExternalAuthProviderTestSupport;

namespace SmartAdmin.Tests;

/// <summary>
/// 第三方登录连接配置管理服务(<see cref="ExternalAuthProviderService"/>)的规则锁:机密加密入库且不外露、
/// 留空不改、首次必填、改了决定请求去向的字段要重输、临时主密钥拒绝保存、Code 规则、软删后可重加、清除不动绑定表。
/// </summary>
public class ExternalAuthProviderServiceTests
{
    private sealed class EphemeralKeyProvider : IDataProtectionKeyProvider
    {
        public bool IsEphemeral => true;
        public DataProtectionKeyMaterial GetCurrentKey() => throw new NotSupportedException("被拒绝的保存不应走到加密");
        public DataProtectionKeyMaterial GetKey(int version) => throw new NotSupportedException();
    }

    private static async Task<SysExternalAuthProvider> RowAsync(IServiceProvider sp, string code) =>
        (await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().GetFirstAsync(r => r.Code == code))!;

    private static async Task<AdminException> FailAsync(Func<Task> action) => await Assert.ThrowsAsync<AdminException>(action);

    // ── 加密往返 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Secrets_are_encrypted_at_rest_and_round_trip()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<IExternalAuthProviderService>().SaveAsync("acme", AcmeInput());

        var row = await RowAsync(sp, "acme");
        Assert.NotNull(row.SecretsProtected);
        Assert.DoesNotContain(AcmeSecret, row.SecretsProtected!);
        Assert.DoesNotContain(AcmeSecret, row.SettingsJson ?? "");
        Assert.DoesNotContain(AcmeSecret, row.SecretHints ?? "");
        Assert.Contains("app-1", row.SettingsJson!);   // 非机密明文,页面回显用

        var plain = sp.GetRequiredService<ISecretProtector>().Unprotect(row.SecretsProtected!);
        Assert.Equal(AcmeSecret, JsonSerializer.Deserialize<Dictionary<string, string>>(plain)!["appSecret"]);
    }

    [Fact]
    public async Task Hint_is_the_last_four_only_when_secret_is_long_enough()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        await service.SaveAsync("acme", AcmeInput(secret: "12345678"));   // 恰好 8 位:给尾四位
        var longOne = (await service.GetCatalogAsync()).Providers.Single(p => p.Code == "acme").Secrets["appSecret"];
        Assert.True(longOne.HasValue);
        Assert.Equal("5678", longOne.Hint);

        await service.SaveAsync("acme", AcmeInput(secret: "1234567"));    // 7 位:不给任何字符,免得短密钥被看去大半
        var shortOne = (await service.GetCatalogAsync()).Providers.Single(p => p.Code == "acme").Secrets["appSecret"];
        Assert.True(shortOne.HasValue);
        Assert.Null(shortOne.Hint);
    }

    [Fact]
    public async Task Catalog_never_contains_secret_plaintext()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("acme", AcmeInput());

        var json = JsonSerializer.Serialize(await service.GetCatalogAsync());

        Assert.DoesNotContain(AcmeSecret, json);
        Assert.Contains("app-1", json);
    }

    // ── 留空不改 / 首次必填 ──────────────────────────────────────────

    [Fact]
    public async Task Blank_secret_keeps_the_stored_one()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("acme", AcmeInput("app-1"));

        await service.SaveAsync("acme", AcmeInput("app-2", secret: null));   // 只改非机密字段

        var provider = (AcmeProvider)(await sp.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme"))!;
        Assert.Equal("app-2", provider.Config.Get("appId"));
        Assert.Equal(AcmeSecret, provider.Config.Get("appSecret"));
    }

    [Fact]
    public async Task Whitespace_only_secret_counts_as_blank()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("acme", AcmeInput());

        await service.SaveAsync("acme", AcmeInput(secret: "   "));

        var provider = (AcmeProvider)(await sp.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme"))!;
        Assert.Equal(AcmeSecret, provider.Config.Get("appSecret"));
    }

    [Fact]
    public async Task First_save_requires_the_secret()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("acme", AcmeInput(secret: null)));

        Assert.Equal(ErrorCode.ExternalAuthFieldMissing, ex.Code);
        Assert.Equal("appSecret", ex.Args!["field"]);
    }

    [Fact]
    public async Task Required_plain_field_is_enforced()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("acme", AcmeInput(appId: "  ")));

        Assert.Equal(ErrorCode.ExternalAuthFieldMissing, ex.Code);
        Assert.Equal("appId", ex.Args!["field"]);
    }

    [Fact]
    public async Task Keys_outside_the_type_field_list_are_ignored_and_secrets_never_come_from_values()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();

        // 把机密塞进 Values 不算数:机密只认 Secrets;多余的键直接丢弃
        var ex = await FailAsync(() => service.SaveAsync("acme", new ExternalAuthProviderSaveInput(
            "acme", null, null,
            new Dictionary<string, string?> { ["appId"] = "a", ["appSecret"] = AcmeSecret, ["junk"] = "x" },
            null)));
        Assert.Equal(ErrorCode.ExternalAuthFieldMissing, ex.Code);

        await service.SaveAsync("acme", new ExternalAuthProviderSaveInput(
            "acme", null, null,
            new Dictionary<string, string?> { ["appId"] = "a", ["junk"] = "x" },
            new Dictionary<string, string?> { ["appSecret"] = AcmeSecret, ["other"] = "y" }));
        var row = await RowAsync(sp, "acme");
        Assert.DoesNotContain("junk", row.SettingsJson!);
        Assert.DoesNotContain("other", sp.GetRequiredService<ISecretProtector>().Unprotect(row.SecretsProtected!));
    }

    // ── 改了决定请求去向的字段,机密必须重输 ─────────────────────────

    [Fact]
    public async Task Changing_authority_requires_re_entering_the_secret()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("keycloak", OidcInput("https://idp.example.com/realms/a", "old-client-secret"));

        // 换地址却不重输:否则有权限的人能把地址指到自己的服务器,登录时服务端带着已保存的密钥过去
        var ex = await FailAsync(() => service.SaveAsync("keycloak", OidcInput("https://evil.example.com/realms/a", null)));
        Assert.Equal(ErrorCode.ExternalAuthFieldMissing, ex.Code);
        Assert.Equal("clientSecret", ex.Args!["field"]);

        // 失败的保存什么也没改
        var unchanged = await RowAsync(sp, "keycloak");
        Assert.Contains("idp.example.com", unchanged.SettingsJson!);

        // 重输后放行
        await service.SaveAsync("keycloak", OidcInput("https://evil.example.com/realms/a", "new-client-secret"));
        Assert.Contains("evil.example.com", (await RowAsync(sp, "keycloak")).SettingsJson!);
    }

    [Fact]
    public async Task Trailing_slash_or_other_fields_do_not_count_as_changing_the_endpoint()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("keycloak", OidcInput("https://idp.example.com/realms/a", "old-client-secret"));

        await service.SaveAsync("keycloak", OidcInput("https://idp.example.com/realms/a/", null));               // 只多了末尾斜杠
        await service.SaveAsync("keycloak", OidcInput("https://idp.example.com/realms/a", null, clientId: "c2"));  // 改 clientId 不涉及去向
    }

    // ── 临时主密钥 ───────────────────────────────────────────────────

    [Fact]
    public async Task Ephemeral_master_key_rejects_saving()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme, more: s => s.Replace(ServiceDescriptor.Singleton<IDataProtectionKeyProvider>(new EphemeralKeyProvider())));
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("acme", AcmeInput()));

        Assert.Equal(ErrorCode.ExternalAuthDataProtectionKeyMissing, ex.Code);
        Assert.Null(await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().GetFirstAsync(r => r.Code == "acme"));
        Assert.True((await service.GetCatalogAsync()).DataProtectionEphemeral);
    }

    // ── Code 规则 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("A")]            // 太短
    [InlineData("a")]
    [InlineData("1abc")]         // 首字符必须是字母
    [InlineData("Key-Cloak")]    // 只许小写
    [InlineData("has_underscore")]
    [InlineData("this-code-is-way-too-long-for-the-limit")]
    public async Task Oidc_code_must_match_the_pattern(string code)
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync(code, OidcInput("https://idp.example.com", "secret-value-1")));

        Assert.Equal(ErrorCode.ExternalAuthCodeInvalid, ex.Code);
    }

    [Theory]
    [InlineData("wecom")]
    [InlineData("dingtalk")]
    [InlineData("github")]
    [InlineData("wechat")]
    [InlineData("acme")]         // 已安装的「只许一份」类型的类型名也保留
    public async Task Oidc_code_cannot_take_an_official_type_name(string code)
    {
        using var f = Factory(new AcmeAuthProviderType());
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync(code, OidcInput("https://idp.example.com", "secret-value-1")));

        Assert.Equal(ErrorCode.ExternalAuthCodeInvalid, ex.Code);
    }

    [Theory]
    [InlineData("my-idp")]
    [InlineData("keycloak")]
    [InlineData("oidc")]
    [InlineData("k2")]
    public async Task Oidc_accepts_valid_codes(string code)
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        await service.SaveAsync(code, OidcInput("https://idp.example.com", "secret-value-1"));
    }

    [Fact]
    public async Task Official_type_code_is_the_type_name()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("acme-2", AcmeInput()));   // 只能配一份,Code 固定

        Assert.Equal(ErrorCode.ExternalAuthCodeInvalid, ex.Code);
    }

    [Fact]
    public async Task Unknown_or_uninstalled_type_is_rejected()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("acme", AcmeInput()));   // acme 类型没注册

        Assert.Equal(ErrorCode.ExternalAuthTypeNotFound, ex.Code);
    }

    [Fact]
    public async Task Code_taken_by_a_code_registered_provider_is_rejected()
    {
        using var f = new AdminAppFactory
        {
            Overrides = s => s.AddSingleton<IExternalAuthProvider>(new CodeRegisteredProvider("corp-sso")),
        };
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("corp-sso", OidcInput("https://idp.example.com", "secret-value-1")));

        Assert.Equal(ErrorCode.ExternalAuthCodeExists, ex.Code);
    }

    [Fact]
    public async Task Existing_code_cannot_switch_type()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme, more: s => s.AddSingleton<IExternalAuthProviderType>(new RenamedOidcType()));
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("shared-code", OidcInput("https://idp.example.com", "secret-value-1"));

        // 同一个 Code 换个类型保存:类型保存后不可改
        var ex = await FailAsync(() => service.SaveAsync("shared-code", new ExternalAuthProviderSaveInput(
            "oidc2", null, null,
            new Dictionary<string, string?> { ["authority"] = "https://idp.example.com", ["clientId"] = "c" },
            new Dictionary<string, string?> { ["clientSecret"] = "secret-value-2" })));

        Assert.Equal(ErrorCode.ExternalAuthCodeExists, ex.Code);
    }

    /// <summary>另一个「允许多条」的类型,用来构造「同 Code 换类型」。</summary>
    private sealed class RenamedOidcType : IExternalAuthProviderType
    {
        public string Type => "oidc2";
        public string DefaultDisplayName => "SSO2";
        public string? DefaultIcon => null;
        public bool AllowMultiple => true;
        public IReadOnlyList<ExternalAuthField> Fields { get; } =
        [
            new("authority", DefinesEndpoint: true),
            new("clientId"),
            new("clientSecret", Secret: true),
        ];
        public IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services) => new AcmeProvider(config);
        public Task<ExternalAuthTestResult> TestAsync(ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalAuthTestResult([]));
    }

    // ── 出站围栏 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://169.254.169.254/latest")]   // 云元数据段
    [InlineData("http://127.0.0.1:8080/realms/x")]  // 回环
    [InlineData("ftp://idp.example.com")]           // 非 http(s)
    [InlineData("not a url")]
    public async Task Save_rejects_authority_blocked_by_the_fence(string authority)
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.SaveAsync("keycloak", OidcInput(authority, "secret-value-1")));

        Assert.Equal(ErrorCode.ExternalAuthEndpointBlocked, ex.Code);
        Assert.Null(await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().GetFirstAsync(r => r.Code == "keycloak"));
    }

    [Fact]
    public async Task Test_rejects_authority_blocked_by_the_fence_without_any_request()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.TestAsync(new ExternalAuthProviderTestInput(
            null, "oidc",
            new Dictionary<string, string?> { ["authority"] = "http://169.254.169.254", ["clientId"] = "c" },
            new Dictionary<string, string?> { ["clientSecret"] = "secret-value-1" })));

        Assert.Equal(ErrorCode.ExternalAuthEndpointBlocked, ex.Code);
    }

    [Fact]
    public async Task Fence_allowed_hosts_from_the_shared_fence_options_apply()
    {
        // 围栏配置复用 SmartAdmin:Ai:Http 那一份:配了白名单,不在里面的主机保存时被拒
        using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?> { ["SmartAdmin:Ai:Http:AllowedHosts:0"] = "sso.corp.example.com" },
        };
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        await service.SaveAsync("keycloak", OidcInput("https://sso.corp.example.com/realms/x", "secret-value-1"));
        var ex = await FailAsync(() => service.SaveAsync("other", OidcInput("https://elsewhere.example.com", "secret-value-1")));

        Assert.Equal(ErrorCode.ExternalAuthEndpointBlocked, ex.Code);
    }

    // ── 清除 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_then_add_the_same_code_again()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("acme", AcmeInput("app-1"));

        await service.DeleteAsync("acme");
        Assert.DoesNotContain((await service.GetCatalogAsync()).Providers, p => p.Code == "acme");

        await service.SaveAsync("acme", AcmeInput("app-2", secret: "another-secret-9999"));   // 软删后唯一位已释放,不撞唯一索引
        var view = (await service.GetCatalogAsync()).Providers.Single(p => p.Code == "acme");
        Assert.Equal("app-2", view.Values["appId"]);
        Assert.Equal("9999", view.Secrets["appSecret"].Hint);

        // 重加是一条新行:被清除的那一行仍是软删状态,Code 带着 _del_ 后缀,两行都在库里
        var all = await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().AsQueryable()
            .ClearFilter<ISoftDelete>().Where(r => r.Code.StartsWith("acme")).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, r => !r.IsDelete);
    }

    [Fact]
    public async Task Delete_unknown_code_reports_not_found()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.DeleteAsync("nope"));

        Assert.Equal(ErrorCode.ExternalAuthProviderNotFound, ex.Code);
    }

    [Fact]
    public async Task Delete_leaves_user_bindings_alone()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("acme", AcmeInput());

        var user = new SysUser { Account = "bound-user", Password = sp.GetRequiredService<IPasswordHasher>().Hash("x"), Name = "Bound", Enabled = true };
        await sp.GetRequiredService<IRepository<SysUser>>().InsertAsync(user);
        var bindings = sp.GetRequiredService<ISysUserExternalService>();
        await bindings.BindAsync(user.Id, new ExternalIdentity("acme", "sub-1", "Bound"));

        await service.DeleteAsync("acme");

        var kept = await bindings.ListByUserAsync(user.Id);
        Assert.Contains(kept, b => b.Provider == "acme");
    }

    // ── 连接测试 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Test_uses_saved_secret_when_blank_and_does_not_persist()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("acme", AcmeInput("app-1"));

        var result = await service.TestAsync(new ExternalAuthProviderTestInput(
            "acme", "acme",
            new Dictionary<string, string?> { ["appId"] = "app-unsaved" },
            null));

        Assert.Equal("app-unsaved", acme.LastTested!.Get("appId"));          // 用表单里的值
        Assert.Equal(AcmeSecret, acme.LastTested.Get("appSecret"));          // 机密留空用已保存的
        Assert.True(result.Ok);
        Assert.Contains("app-1", (await RowAsync(sp, "acme")).SettingsJson!);   // 不落库
    }

    [Fact]
    public async Task Test_redacts_secrets_that_a_type_echoes_in_details()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var result = await service.TestAsync(new ExternalAuthProviderTestInput(
            null, "acme",
            new Dictionary<string, string?> { ["appId"] = "app-1" },
            new Dictionary<string, string?> { ["appSecret"] = AcmeSecret }));

        Assert.DoesNotContain(AcmeSecret, JsonSerializer.Serialize(result));
        Assert.Contains("secret=***", result.Checks[0].Detail);
        Assert.Equal(["ok", "skipped"], result.Checks.Select(c => c.Status).ToArray());
    }

    [Fact]
    public async Task Test_of_a_new_provider_needs_the_secret_too()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var ex = await FailAsync(() => service.TestAsync(new ExternalAuthProviderTestInput(
            null, "acme", new Dictionary<string, string?> { ["appId"] = "app-1" }, null)));

        Assert.Equal(ErrorCode.ExternalAuthFieldMissing, ex.Code);
    }

    [Fact]
    public async Task Test_does_not_reuse_the_saved_secret_after_the_endpoint_changed()
    {
        using var f = new AdminAppFactory();
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();
        await service.SaveAsync("keycloak", OidcInput("https://idp.example.com/realms/a", "old-client-secret"));

        var ex = await FailAsync(() => service.TestAsync(new ExternalAuthProviderTestInput(
            "keycloak", "oidc",
            new Dictionary<string, string?> { ["authority"] = "https://other.example.com", ["clientId"] = "c" },
            null)));

        Assert.Equal(ErrorCode.ExternalAuthFieldMissing, ex.Code);
    }

    [Fact]
    public async Task A_type_that_throws_from_test_becomes_a_failed_check()
    {
        using var f = new AdminAppFactory
        {
            Overrides = s => s.AddSingleton<IExternalAuthProviderType>(new ThrowingType()),
        };
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>();

        var result = await service.TestAsync(new ExternalAuthProviderTestInput(
            null, "boom", new Dictionary<string, string?>(), new Dictionary<string, string?>()));

        Assert.False(result.Ok);
        Assert.Equal("failed", result.Checks.Single().Status);
    }

    private sealed class ThrowingType : IExternalAuthProviderType
    {
        public string Type => "boom";
        public string DefaultDisplayName => "Boom";
        public string? DefaultIcon => null;
        public bool AllowMultiple => false;
        public IReadOnlyList<ExternalAuthField> Fields => [];
        public IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services) => throw new NotSupportedException();
        public Task<ExternalAuthTestResult> TestAsync(ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("类型实现没有折成失败项");
    }
}
