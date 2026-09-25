using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// 内置 OIDC 类型描述(<see cref="OidcExternalAuthProviderType"/>):字段清单、造实例、连接测试(用假 handler 覆盖
/// 通过 / issuer 不一致 / 缺端点 / HTTP 错误 / 网络错误 / 响应畸形),以及 OIDC 出站请求确实走 SSRF 围栏的命名客户端。
/// </summary>
public class OidcExternalAuthProviderTypeTests
{
    private const string Authority = "https://idp.example.com/realms/main";

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Discovery(string issuer = Authority, string? authorize = "https://idp.example.com/auth", string? token = "https://idp.example.com/token")
    {
        var parts = new List<string> { $"\"issuer\":\"{issuer}\"" };
        if (authorize is not null) parts.Add($"\"authorization_endpoint\":\"{authorize}\"");
        if (token is not null) parts.Add($"\"token_endpoint\":\"{token}\"");
        return "{" + string.Join(',', parts) + "}";
    }

    private static IServiceProvider Services(FakeHandler handler, string environment = "Development")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeEnv(environment));
        services.AddHttpClient(ExternalAuthHttpClient.Name).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static ExternalAuthProviderConfig Config(string authority = Authority) => new(
        "keycloak", "Keycloak", null,
        new Dictionary<string, string> { ["authority"] = authority, ["clientId"] = "c", ["clientSecret"] = "the-client-secret" });

    private static readonly OidcExternalAuthProviderType Type = new();

    private static string[] Statuses(ExternalAuthTestResult r) => [.. r.Checks.Select(c => $"{c.Key}:{c.Status}")];

    // ── 字段清单 ─────────────────────────────────────────────────────

    [Fact]
    public void Field_list_marks_authority_as_endpoint_defining_and_only_the_client_secret_as_secret()
    {
        Assert.Equal("oidc", Type.Type);
        Assert.True(Type.AllowMultiple);

        var byName = Type.Fields.ToDictionary(f => f.Name);
        Assert.Equal(["authority", "clientId", "clientSecret", "scopes", "usePkce"], Type.Fields.Select(f => f.Name).ToArray());
        Assert.True(byName["authority"].DefinesEndpoint);
        Assert.True(byName["authority"].Required);
        Assert.True(byName["clientSecret"].Secret);
        Assert.Equal(["clientSecret"], Type.Fields.Where(f => f.Secret).Select(f => f.Name).ToArray());
        Assert.Equal("openid profile email", byName["scopes"].Default);
        Assert.False(byName["scopes"].Required);
        Assert.Equal("true", byName["usePkce"].Default);
        Assert.False(byName["usePkce"].Required);
    }

    [Fact]
    public async Task Create_builds_a_provider_from_the_config_with_defaults()
    {
        var sp = Services(new FakeHandler(_ => Json("{}")));

        var provider = Type.Create(Config(), sp);

        Assert.IsType<OidcExternalAuthProvider>(provider);
        Assert.Equal("keycloak", provider.Code);
        Assert.Equal("Keycloak", provider.DisplayName);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Created_provider_fetches_discovery_through_the_fenced_client()
    {
        var handler = new FakeHandler(_ => Json(Discovery()));
        var provider = Type.Create(Config(), Services(handler));

        var url = await provider.BuildAuthorizeUrlAsync(new ExternalAuthorizeRequest("state1", "nonce1", "challenge1", "https://app/cb"));

        Assert.StartsWith("https://idp.example.com/auth?", url);
        Assert.Contains("client_id=c", url);
        Assert.Contains("code_challenge=challenge1", url);   // usePkce 缺省为开
        Assert.Contains(handler.Requests, u => u.ToString() == Authority + "/.well-known/openid-configuration");
    }

    [Fact]
    public async Task Pkce_can_be_switched_off_and_scopes_default_when_blank()
    {
        var handler = new FakeHandler(_ => Json(Discovery()));
        var config = new ExternalAuthProviderConfig("keycloak", "K", null, new Dictionary<string, string>
        {
            ["authority"] = Authority, ["clientId"] = "c", ["clientSecret"] = "s", ["usePkce"] = "false", ["scopes"] = "  ",
        });
        var provider = Type.Create(config, Services(handler));

        var url = await provider.BuildAuthorizeUrlAsync(new ExternalAuthorizeRequest("s", "n", "challenge1", "https://app/cb"));

        Assert.DoesNotContain("code_challenge", url);
        Assert.Contains("scope=openid%20profile%20email", url);
    }

    // ── 连接测试 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Test_passes_and_marks_the_client_secret_as_unverified()
    {
        var handler = new FakeHandler(_ => Json(Discovery()));

        var result = await Type.TestAsync(Config(), Services(handler));

        Assert.True(result.Ok);
        Assert.Equal(["discovery:Ok", "issuer:Ok", "endpoints:Ok", "clientSecret:Skipped"], Statuses(result));
        Assert.Equal(Authority + "/.well-known/openid-configuration", Assert.Single(handler.Requests).ToString());
    }

    [Fact]
    public async Task Test_tolerates_a_trailing_slash_on_either_side_of_the_issuer()
    {
        var handler = new FakeHandler(_ => Json(Discovery(issuer: Authority + "/")));

        var result = await Type.TestAsync(Config(Authority + "/"), Services(handler));

        Assert.True(result.Ok);
        Assert.Equal(Authority + "/.well-known/openid-configuration", handler.Requests.Single().ToString());
    }

    [Fact]
    public async Task Test_fails_when_issuer_differs_from_authority()
    {
        var handler = new FakeHandler(_ => Json(Discovery(issuer: "https://other.example.com/realms/main")));

        var result = await Type.TestAsync(Config(), Services(handler));

        Assert.False(result.Ok);
        Assert.Equal(["discovery:Ok", "issuer:Failed", "endpoints:Ok", "clientSecret:Skipped"], Statuses(result));
        Assert.Contains("other.example.com", result.Checks.Single(c => c.Key == "issuer").Detail);
    }

    [Theory]
    [InlineData(null, "https://idp.example.com/token", "authorization_endpoint")]
    [InlineData("https://idp.example.com/auth", null, "token_endpoint")]
    public async Task Test_fails_when_an_endpoint_is_missing(string? authorize, string? token, string missing)
    {
        var handler = new FakeHandler(_ => Json(Discovery(authorize: authorize, token: token)));

        var result = await Type.TestAsync(Config(), Services(handler));

        Assert.False(result.Ok);
        var endpoints = result.Checks.Single(c => c.Key == "endpoints");
        Assert.Equal(ExternalAuthCheckStatus.Failed, endpoints.Status);
        Assert.Contains(missing, endpoints.Detail);
    }

    [Fact]
    public async Task Test_reports_http_errors_as_a_failed_discovery_check()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Type.TestAsync(Config(), Services(handler));

        Assert.False(result.Ok);
        Assert.Equal(["discovery:Failed", "clientSecret:Skipped"], Statuses(result));
        Assert.Contains("404", result.Checks[0].Detail);
    }

    [Fact]
    public async Task Test_folds_network_errors_into_a_failed_check_instead_of_throwing()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("连接被拒绝"));

        var result = await Type.TestAsync(Config(), Services(handler));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, result.Checks[0].Status);
        Assert.Contains("连接被拒绝", result.Checks[0].Detail);
    }

    [Theory]
    [InlineData("<html>not json</html>")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    public async Task Test_treats_malformed_discovery_documents_as_failures(string body)
    {
        var handler = new FakeHandler(_ => Json(body));

        var result = await Type.TestAsync(Config(), Services(handler));

        Assert.False(result.Ok);
        Assert.Equal(["discovery:Failed", "clientSecret:Skipped"], Statuses(result));
    }

    [Fact]
    public async Task Test_refuses_plain_http_authority_outside_development_without_sending_a_request()
    {
        var handler = new FakeHandler(_ => Json(Discovery("http://idp.example.com")));

        var result = await Type.TestAsync(Config("http://idp.example.com"), Services(handler, environment: "Production"));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, result.Checks[0].Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Test_allows_plain_http_authority_in_development()
    {
        var handler = new FakeHandler(_ => Json(Discovery("http://idp.local/realms/x", "http://idp.local/auth", "http://idp.local/token")));

        var result = await Type.TestAsync(Config("http://idp.local/realms/x"), Services(handler));

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_check()
    {
        var handler = new FakeHandler(_ => Json(Discovery()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Type.TestAsync(Config(), Services(handler), cts.Token));
    }

    // ── 出站围栏 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fenced_document_retriever_uses_the_named_client_and_enforces_https()
    {
        var handler = new FakeHandler(_ => Json("{\"ok\":true}"));
        var factory = Services(handler).GetRequiredService<IHttpClientFactory>();

        var doc = await new FencedDocumentRetriever(factory, requireHttps: true)
            .GetDocumentAsync("https://idp.example.com/jwks", CancellationToken.None);

        Assert.Contains("ok", doc);
        Assert.Single(handler.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FencedDocumentRetriever(factory, requireHttps: true)
            .GetDocumentAsync("http://idp.example.com/jwks", CancellationToken.None));
        Assert.Single(handler.Requests);   // 被拒的那次没有发出请求
    }

    [Theory]
    [InlineData("http://127.0.0.1:1/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public async Task Named_client_registered_by_the_kernel_refuses_blocklisted_addresses(string url)
    {
        using var f = new AdminAppFactory();
        var http = f.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ExternalAuthHttpClient.Name);

        // 连接期对解析后的 IP 复检黑名单(防 DNS rebinding):命中即拒连,请求根本发不出去
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
        Assert.Contains("围栏", ex.ToString());
    }

    [Fact]
    public void Kernel_registers_the_oidc_type_exactly_once()
    {
        using var f = new AdminAppFactory();

        var types = f.Services.GetServices<IExternalAuthProviderType>().ToList();

        Assert.Single(types, t => t.Type == "oidc");
    }

    // ── 启动告警 ─────────────────────────────────────────────────────

    private static IConfigurationSection ExternalAuthSection(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("SmartAdmin:ExternalAuth");

    [Fact]
    public void Unused_config_sections_are_found_by_name_regardless_of_case()
    {
        var section = ExternalAuthSection(new()
        {
            ["SmartAdmin:ExternalAuth:CallbackBaseUrl"] = "https://admin.example.com",
            ["SmartAdmin:ExternalAuth:Oidc:0:Authority"] = "https://idp.example.com",
            ["SmartAdmin:ExternalAuth:wecom:CorpSecret"] = "legacy-corp-secret",
            ["SmartAdmin:ExternalAuth:GitHub:ClientSecret"] = "legacy-github-secret",
        });

        var found = UnusedExternalAuthConfigWarning.Find(section);

        Assert.Equal(
            ["SmartAdmin:ExternalAuth:GitHub", "SmartAdmin:ExternalAuth:Oidc", "SmartAdmin:ExternalAuth:wecom"],
            found.Order(StringComparer.Ordinal).ToArray());   // 配置提供程序不保证枚举顺序
    }

    [Fact]
    public void Deployment_level_keys_alone_produce_no_warning()
    {
        var section = ExternalAuthSection(new()
        {
            ["SmartAdmin:ExternalAuth:CallbackBaseUrl"] = "https://admin.example.com",
            ["SmartAdmin:ExternalAuth:FrontendResultPath"] = "/oauth/callback",
        });

        Assert.Empty(UnusedExternalAuthConfigWarning.Find(section));
    }

    [Fact]
    public async Task Warning_is_one_log_entry_that_points_at_the_admin_page()
    {
        var log = new CapturingLogger<UnusedExternalAuthConfigWarning>();
        var warning = new UnusedExternalAuthConfigWarning(
            ["SmartAdmin:ExternalAuth:WeCom", "SmartAdmin:ExternalAuth:GitHub"], log);

        await warning.StartAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SmartAdmin:ExternalAuth:WeCom", entry.Message);
        Assert.Contains("SmartAdmin:ExternalAuth:GitHub", entry.Message);
        Assert.Contains("系统配置 → 登录方式", entry.Message);
    }

    [Fact]
    public async Task No_sections_means_no_log_entry()
    {
        var log = new CapturingLogger<UnusedExternalAuthConfigWarning>();

        await new UnusedExternalAuthConfigWarning([], log).StartAsync(CancellationToken.None);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Startup_registers_the_warning_only_when_such_a_section_exists()
    {
        using var with = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?> { ["SmartAdmin:ExternalAuth:WeCom:CorpSecret"] = "legacy-corp-secret" },
        };
        using var without = new AdminAppFactory();

        Assert.Single(with.Services.GetServices<IHostedService>().OfType<UnusedExternalAuthConfigWarning>());
        Assert.Empty(without.Services.GetServices<IHostedService>().OfType<UnusedExternalAuthConfigWarning>());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
