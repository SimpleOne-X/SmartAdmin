using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;
using static SmartAdmin.Tests.ExternalAuthProviderTestSupport;

namespace SmartAdmin.Tests;

/// <summary>
/// 外部登录 provider 注册表(<see cref="ExternalAuthProviderRegistry"/>)的行为锁:代码注册的 ∪ 库里按类型现建的,
/// 同 Code 代码优先;实例跨请求复用、行变了才重建;配置不完整的行不出实例;写路径让缓存失效。
/// </summary>
public class ExternalAuthProviderRegistryTests
{
    [Fact]
    public async Task Result_is_the_union_of_code_registered_and_database_providers()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme, more: s => s.AddSingleton<IExternalAuthProvider>(new CodeRegisteredProvider("corp-sso")));
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IExternalAuthProviderService>().SaveAsync("acme", AcmeInput());
        await sp.GetRequiredService<IExternalAuthProviderService>().SaveAsync("keycloak", OidcInput("https://idp.example.com/realms/a", "client-secret-1"));
        var registry = sp.GetRequiredService<IExternalAuthProviderRegistry>();

        var codes = (await registry.ListAsync()).Select(p => p.Code).ToList();

        Assert.Equal(["corp-sso", "acme", "keycloak"], codes);
        Assert.NotNull(await registry.FindAsync("corp-sso"));
        Assert.NotNull(await registry.FindAsync("acme"));
        Assert.NotNull(await registry.FindAsync("keycloak"));
        Assert.Null(await registry.FindAsync("missing"));
    }

    [Fact]
    public async Task Code_registered_provider_wins_on_the_same_code()
    {
        var acme = new AcmeAuthProviderType();
        var code = new CodeRegisteredProvider("acme");
        using var f = Factory(acme, more: s => s.AddSingleton<IExternalAuthProvider>(code));
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        // 绕过管理服务(它会拒绝占用代码注册的 Code),直接插一行同 Code 的库配置
        await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().InsertAsync(new SysExternalAuthProvider
        {
            Code = "acme",
            Type = "acme",
            SettingsJson = """{"appId":"db-app"}""",
            SecretsProtected = sp.GetRequiredService<ISecretProtector>().Protect("""{"appSecret":"db-secret"}"""),
            SecretHints = """{"appSecret":""}""",
        });
        var registry = sp.GetRequiredService<IExternalAuthProviderRegistry>();

        Assert.Same(code, await registry.FindAsync("acme"));
        var listed = await registry.ListAsync();
        Assert.Same(code, listed.Single(p => p.Code == "acme"));
        Assert.Equal(0, acme.CreateCount);   // 库里那一行根本没造实例
    }

    [Fact]
    public async Task Instance_is_reused_across_requests_until_the_row_changes()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>().SaveAsync("acme", AcmeInput("app-1"));

        IExternalAuthProvider? first, second;
        using (var scope = f.Services.CreateScope())
            first = await scope.ServiceProvider.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme");
        using (var scope = f.Services.CreateScope())
            second = await scope.ServiceProvider.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme");

        Assert.Same(first, second);      // 实例内缓存(access_token / 发现文档)跨请求存活
        Assert.Equal(1, acme.CreateCount);

        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IExternalAuthProviderService>().SaveAsync("acme", AcmeInput("app-2", secret: null));

        IExternalAuthProvider? rebuilt;
        using (var scope = f.Services.CreateScope())
            rebuilt = await scope.ServiceProvider.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme");

        Assert.NotSame(first, rebuilt);   // 行变了 → 重建
        Assert.Equal("app-2", ((AcmeProvider)rebuilt!).Config.Get("appId"));
        Assert.Equal(2, acme.CreateCount);
    }

    [Fact]
    public async Task Back_to_back_saves_still_rebuild_the_instance()
    {
        // 部分数据库的时间精度是秒:重建不能只看 UpdateTime,连续两次保存都要生效
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        var registry = sp.GetRequiredService<IExternalAuthProviderRegistry>();

        for (var i = 1; i <= 3; i++)
        {
            await service.SaveAsync("acme", AcmeInput($"app-{i}", secret: $"secret-value-{i}"));
            var provider = (AcmeProvider)(await registry.FindAsync("acme"))!;
            Assert.Equal($"app-{i}", provider.Config.Get("appId"));
            Assert.Equal($"secret-value-{i}", provider.Config.Get("appSecret"));
        }
    }

    [Fact]
    public async Task Incomplete_rows_produce_no_instance()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IRepository<SysExternalAuthProvider>>();
        // 只有非机密字段:缺必填机密
        await repo.InsertAsync(new SysExternalAuthProvider { Code = "acme", Type = "acme", SettingsJson = """{"appId":"a"}""" });
        // 有机密缺必填非机密
        await repo.InsertAsync(new SysExternalAuthProvider
        {
            Code = "keycloak",
            Type = "oidc",
            SettingsJson = """{"clientId":"c"}""",
            SecretsProtected = sp.GetRequiredService<ISecretProtector>().Protect("""{"clientSecret":"s"}"""),
            SecretHints = """{"clientSecret":""}""",
        });
        var registry = sp.GetRequiredService<IExternalAuthProviderRegistry>();

        Assert.Null(await registry.FindAsync("acme"));
        Assert.Null(await registry.FindAsync("keycloak"));
        Assert.Empty(await registry.ListAsync());
        Assert.Equal(0, acme.CreateCount);

        // 目录里这些行仍在,状态是「未配置」
        var catalog = await sp.GetRequiredService<IExternalAuthProviderService>().GetCatalogAsync();
        Assert.All(catalog.Providers, p => Assert.False(p.Configured));
        Assert.Equal(2, catalog.Providers.Count);
    }

    [Fact]
    public async Task Row_whose_type_is_not_installed_produces_no_instance()
    {
        using var f = new AdminAppFactory();   // acme 类型没装
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().InsertAsync(new SysExternalAuthProvider
        {
            Code = "acme",
            Type = "acme",
            SettingsJson = """{"appId":"a"}""",
            SecretsProtected = sp.GetRequiredService<ISecretProtector>().Protect("""{"appSecret":"s"}"""),
            SecretHints = """{"appSecret":""}""",
        });

        Assert.Null(await sp.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme"));
        var view = (await sp.GetRequiredService<IExternalAuthProviderService>().GetCatalogAsync()).Providers.Single();
        Assert.False(view.Installed);
        Assert.False(view.Configured);
    }

    [Fact]
    public async Task Undecryptable_secrets_produce_no_instance_instead_of_throwing()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IRepository<SysExternalAuthProvider>>().InsertAsync(new SysExternalAuthProvider
        {
            Code = "acme",
            Type = "acme",
            SettingsJson = """{"appId":"a"}""",
            SecretsProtected = "not-a-valid-envelope",   // 主密钥换过 / 信封损坏
            SecretHints = """{"appSecret":""}""",
        });

        Assert.Null(await sp.GetRequiredService<IExternalAuthProviderRegistry>().FindAsync("acme"));
    }

    [Fact]
    public async Task Writes_invalidate_the_shared_cache()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cache = sp.GetRequiredService<ICacheProvider>();
        var service = sp.GetRequiredService<IExternalAuthProviderService>();
        var registry = sp.GetRequiredService<IExternalAuthProviderRegistry>();

        await service.SaveAsync("acme", AcmeInput());
        await registry.ListAsync();   // 读穿透:缓存被填上
        Assert.NotNull(await cache.GetAsync<List<ExternalAuthProviderRow>>(CacheKeys.ExternalAuthProviders()));

        await service.SaveAsync("acme", AcmeInput("app-2", secret: null));
        Assert.Null(await cache.GetAsync<List<ExternalAuthProviderRow>>(CacheKeys.ExternalAuthProviders()));   // 保存失效

        await registry.ListAsync();
        await service.DeleteAsync("acme");
        Assert.Null(await cache.GetAsync<List<ExternalAuthProviderRow>>(CacheKeys.ExternalAuthProviders()));   // 清除失效
        Assert.Null(await registry.FindAsync("acme"));
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Cached_rows_carry_only_the_encrypted_envelope()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IExternalAuthProviderService>().SaveAsync("acme", AcmeInput());
        await sp.GetRequiredService<IExternalAuthProviderRegistry>().ListAsync();

        var cached = await sp.GetRequiredService<ICacheProvider>().GetAsync<List<ExternalAuthProviderRow>>(CacheKeys.ExternalAuthProviders());

        Assert.DoesNotContain(AcmeSecret, System.Text.Json.JsonSerializer.Serialize(cached));   // Redis 里存的也不是明文
    }

    [Fact]
    public async Task Provider_configured_in_the_database_serves_the_login_flow()
    {
        var acme = new AcmeAuthProviderType();
        using var f = Factory(acme);
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IExternalAuthProviderService>().SaveAsync("acme", AcmeInput());

        // AuthService 从注册表取 provider:换身份成功,身份未绑定 → 40016(而不是「provider 不可用」40013)
        var ex = await Assert.ThrowsAsync<AdminException>(() => sp.GetRequiredService<IAuthService>().LoginByExternalAsync(new ExternalLoginInput
        {
            ProviderCode = "acme",
            Code = "c",
            CodeVerifier = "v",
            Nonce = "n",
            RedirectUri = "https://app/cb",
        }));
        Assert.Equal(ErrorCode.OAuthAccountNotBound, ex.Code);

        var unknown = await Assert.ThrowsAsync<AdminException>(() => sp.GetRequiredService<IAuthService>().LoginByExternalAsync(new ExternalLoginInput
        {
            ProviderCode = "not-configured",
            Code = "c",
            CodeVerifier = "v",
            Nonce = "n",
            RedirectUri = "https://app/cb",
        }));
        Assert.Equal(ErrorCode.OAuthProviderDisabled, unknown.Code);
    }
}
