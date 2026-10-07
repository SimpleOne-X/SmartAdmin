using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Auth.GitHub;
using SmartAdmin.Auth.WeChat;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// GitHub / WeChat 可选包的类型描述:字段清单、<c>Create</c> 造出的 provider、<c>TestAsync</c> 的检查项归类,
/// 以及无参 Setup 的注册语义。HTTP 全部走假 handler,不触网。
/// </summary>
public class GitHubWeChatTypeTests
{
    private const string GitHubSecret = "gh-secret-VALUE-123";
    private const string WeChatSecret = "wx-secret-VALUE-456";

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return await respond(request);
        }
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Json(HttpStatusCode status, string json) =>
        _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    private static ServiceProvider GitHubServices(FakeHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSmartAdminGitHubAuth();
        services.AddHttpClient(GitHubExternalAuthProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider WeChatServices(FakeHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSmartAdminWeChatAuth();
        services.AddHttpClient(WeChatExternalAuthProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static ExternalAuthProviderConfig GitHubConfig(string displayName = "", string? icon = null) =>
        new("github", displayName, icon, new Dictionary<string, string>
        {
            ["clientId"] = "cid-123",
            ["clientSecret"] = GitHubSecret,
        });

    private static ExternalAuthProviderConfig WeChatConfig(string displayName = "", string? icon = null) =>
        new("wechat", displayName, icon, new Dictionary<string, string>
        {
            ["appId"] = "wx-app-1",
            ["appSecret"] = WeChatSecret,
        });

    private static ExternalAuthCheck Check(ExternalAuthTestResult r, string key) =>
        Assert.Single(r.Checks, c => c.Key == key);

    private static void AssertNoSecret(ExternalAuthTestResult r, string secret)
    {
        foreach (var c in r.Checks)
            Assert.DoesNotContain(secret, c.Detail ?? "", StringComparison.Ordinal);
    }

    // ───────────────────────── GitHub:字段与 Create ─────────────────────────

    [Fact]
    public void GitHub_type_describes_fields_and_defaults()
    {
        IExternalAuthProviderType type = new GitHubAuthProviderType();
        Assert.Equal("github", type.Type);
        Assert.Equal("GitHub", type.DefaultDisplayName);
        Assert.False(type.AllowMultiple);

        Assert.Collection(type.Fields,
            f =>
            {
                Assert.Equal("clientId", f.Name);
                Assert.False(f.Secret);
                Assert.True(f.Required);
                Assert.False(f.DefinesEndpoint);
            },
            f =>
            {
                Assert.Equal("clientSecret", f.Name);
                Assert.True(f.Secret);
                Assert.True(f.Required);
                Assert.False(f.DefinesEndpoint);
            });
    }

    [Fact]
    public async Task GitHub_create_builds_provider_from_config_values()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK, "{}")));
        var provider = new GitHubAuthProviderType().Create(GitHubConfig("公司 GitHub", "ph:github-logo"), sp);

        Assert.IsType<GitHubExternalAuthProvider>(provider);
        Assert.Equal("github", provider.Code);
        Assert.Equal("公司 GitHub", provider.DisplayName);
        Assert.Equal("ph:github-logo", provider.Icon);
        var url = await provider.BuildAuthorizeUrlAsync(new ExternalAuthorizeRequest("st", "no", "ch", "https://app/cb"));
        Assert.Contains("client_id=cid-123", url);
        Assert.DoesNotContain(GitHubSecret, url);
    }

    [Fact]
    public void GitHub_create_falls_back_to_default_display_name_when_blank()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK, "{}")));
        var provider = new GitHubAuthProviderType().Create(GitHubConfig("   "), sp);
        Assert.Equal("GitHub", provider.DisplayName);
        Assert.Null(provider.Icon);
    }

    // ───────────────────────── GitHub:TestAsync ─────────────────────────

    [Fact]
    public async Task GitHub_test_ok_when_code_is_rejected_but_credentials_accepted()
    {
        var handler = new FakeHandler(Json(HttpStatusCode.OK,
            """{"error":"bad_verification_code","error_description":"The code passed is incorrect or expired."}"""));
        using var sp = GitHubServices(handler);

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        Assert.True(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "credentials").Status);
        AssertNoSecret(result, GitHubSecret);

        var (req, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://github.com/login/oauth/access_token", req.RequestUri!.ToString());
        Assert.DoesNotContain("client_secret", req.RequestUri.Query);
        Assert.Contains("client_id=cid-123", body);
        Assert.Contains("code=smartadmin-connection-test", body);
        Assert.Contains(GitHubExternalAuthProvider.UserAgentValue, string.Join(" ", req.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task GitHub_test_fails_credentials_on_incorrect_client_credentials()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK,
            """{"error":"incorrect_client_credentials","error_description":"The client_id and/or client_secret passed are incorrect."}""")));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        var cred = Check(result, "credentials");
        Assert.Equal(ExternalAuthCheckStatus.Failed, cred.Status);
        Assert.Equal("incorrect_client_credentials", cred.Detail);
    }

    [Fact]
    public async Task GitHub_test_fails_credentials_when_client_id_is_unknown()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.NotFound, """{"error":"Not Found"}""")));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        var cred = Check(result, "credentials");
        Assert.Equal(ExternalAuthCheckStatus.Failed, cred.Status);
        Assert.Equal("HTTP 404", cred.Detail);
    }

    [Fact]
    public async Task GitHub_test_skips_credentials_on_unrelated_error()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK, """{"error":"redirect_uri_mismatch"}""")));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        Assert.True(result.Ok);
        var cred = Check(result, "credentials");
        Assert.Equal(ExternalAuthCheckStatus.Skipped, cred.Status);
        Assert.Equal("redirect_uri_mismatch", cred.Detail);
    }

    [Fact]
    public async Task GitHub_test_reports_network_error_as_failed_check_without_throwing()
    {
        using var sp = GitHubServices(new FakeHandler(_ => throw new HttpRequestException($"boom {GitHubSecret}")));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, Check(result, "reachable").Status);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "credentials").Status);
        AssertNoSecret(result, GitHubSecret);
    }

    [Fact]
    public async Task GitHub_test_reports_server_error_as_unreachable()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.ServiceUnavailable, "")));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        var reach = Check(result, "reachable");
        Assert.Equal(ExternalAuthCheckStatus.Failed, reach.Status);
        Assert.Equal("HTTP 503", reach.Detail);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "credentials").Status);
    }

    [Theory]
    [InlineData("<html>oops</html>")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"unexpected":true}""")]
    public async Task GitHub_test_reports_malformed_response_as_failed_credentials(string body)
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK, body)));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        var cred = Check(result, "credentials");
        Assert.Equal(ExternalAuthCheckStatus.Failed, cred.Status);
        Assert.StartsWith("invalid response", cred.Detail);
    }

    [Fact]
    public async Task GitHub_test_detail_never_contains_secret_even_if_vendor_echoes_it()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK,
            $$"""{"error":"weird_{{GitHubSecret}}_error"}""")));

        var result = await new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp);

        AssertNoSecret(result, GitHubSecret);
        Assert.Contains("***", Check(result, "credentials").Detail);
    }

    [Fact]
    public async Task GitHub_test_propagates_caller_cancellation()
    {
        using var sp = GitHubServices(new FakeHandler(Json(HttpStatusCode.OK, "{}")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GitHubAuthProviderType().TestAsync(GitHubConfig(), sp, cts.Token));
    }

    // ───────────────────────── WeChat:字段与 Create ─────────────────────────

    [Fact]
    public void WeChat_type_describes_fields_and_defaults()
    {
        IExternalAuthProviderType type = new WeChatAuthProviderType();
        Assert.Equal("wechat", type.Type);
        Assert.Equal("微信", type.DefaultDisplayName);
        Assert.False(type.AllowMultiple);

        Assert.Collection(type.Fields,
            f =>
            {
                Assert.Equal("appId", f.Name);
                Assert.False(f.Secret);
                Assert.True(f.Required);
            },
            f =>
            {
                Assert.Equal("appSecret", f.Name);
                Assert.True(f.Secret);
                Assert.True(f.Required);
            });
    }

    [Fact]
    public async Task WeChat_create_builds_provider_from_config_values()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, "{}")));
        var provider = new WeChatAuthProviderType().Create(WeChatConfig("扫码登录", "ph:wechat-logo"), sp);

        Assert.IsType<WeChatExternalAuthProvider>(provider);
        Assert.Equal("wechat", provider.Code);
        Assert.Equal("扫码登录", provider.DisplayName);
        Assert.Equal("ph:wechat-logo", provider.Icon);
        var url = await provider.BuildAuthorizeUrlAsync(new ExternalAuthorizeRequest("st", "no", "ch", "https://app/cb"));
        Assert.Contains("appid=wx-app-1", url);
        Assert.DoesNotContain(WeChatSecret, url);
    }

    [Fact]
    public void WeChat_create_falls_back_to_default_display_name_when_blank()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, "{}")));
        var provider = new WeChatAuthProviderType().Create(WeChatConfig(""), sp);
        Assert.Equal("微信", provider.DisplayName);
        Assert.Null(provider.Icon);
    }

    // ───────────────────────── WeChat:TestAsync ─────────────────────────

    [Fact]
    public async Task WeChat_test_accepts_app_id_and_leaves_secret_unverified_when_code_is_rejected()
    {
        var handler = new FakeHandler(Json(HttpStatusCode.OK, """{"errcode":40029,"errmsg":"invalid code, rid: 6ab499e5-63eafdbb-7e7c6958"}"""));
        using var sp = WeChatServices(handler);

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.True(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        var appId = Check(result, "appId");
        Assert.Equal(ExternalAuthCheckStatus.Ok, appId.Status);
        Assert.Equal("errcode 40029: invalid code", appId.Detail);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "secret").Status);
        AssertNoSecret(result, WeChatSecret);

        var (req, _) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("api.weixin.qq.com", req.RequestUri!.Host);
        Assert.Contains("appid=wx-app-1", req.RequestUri.Query);
        Assert.Contains("code=smartadmin-connection-test", req.RequestUri.Query);
    }

    [Fact]
    public async Task WeChat_test_accepts_app_id_when_code_was_already_used()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, """{"errcode":40163,"errmsg":"code been used"}""")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "appId").Status);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "secret").Status);
    }

    [Fact]
    public async Task WeChat_test_fails_app_id_on_invalid_appid()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, """{"errcode":40013,"errmsg":"invalid appid, rid: 6ab499b8-63c0e063-004181da"}""")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        var appId = Check(result, "appId");
        Assert.Equal(ExternalAuthCheckStatus.Failed, appId.Status);
        Assert.Equal("errcode 40013: invalid appid", appId.Detail);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "secret").Status);
    }

    [Fact]
    public async Task WeChat_test_fails_secret_on_invalid_appsecret()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, """{"errcode":40125,"errmsg":"invalid appsecret, rid: abc"}""")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "appId").Status);
        var secret = Check(result, "secret");
        Assert.Equal(ExternalAuthCheckStatus.Failed, secret.Status);
        Assert.Equal("errcode 40125: invalid appsecret", secret.Detail);
    }

    [Fact]
    public async Task WeChat_test_skips_app_id_and_secret_on_unrelated_errcode()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, """{"errcode":45011,"errmsg":"api minute-quota reach limit"}""")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.True(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "appId").Status);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "secret").Status);
    }

    [Fact]
    public async Task WeChat_test_reports_network_error_as_failed_check_without_throwing()
    {
        // 真实的 HttpRequestException 消息可能带上含 secret 的请求 URL,这里模拟这种最坏情形
        using var sp = WeChatServices(new FakeHandler(req => throw new HttpRequestException($"failed: {req.RequestUri}")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, Check(result, "reachable").Status);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "appId").Status);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "secret").Status);
        AssertNoSecret(result, WeChatSecret);
    }

    [Fact]
    public async Task WeChat_test_reports_server_error_as_unreachable()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.BadGateway, "")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        var reach = Check(result, "reachable");
        Assert.Equal(ExternalAuthCheckStatus.Failed, reach.Status);
        Assert.Equal("HTTP 502", reach.Detail);
    }

    [Theory]
    [InlineData("<html>oops</html>")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"unexpected":true}""")]
    [InlineData("""{"errcode":"not-a-number"}""")]
    public async Task WeChat_test_reports_malformed_response_as_failed_app_id(string body)
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, body)));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Ok, Check(result, "reachable").Status);
        var appId = Check(result, "appId");
        Assert.Equal(ExternalAuthCheckStatus.Failed, appId.Status);
        Assert.StartsWith("invalid response", appId.Detail);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, Check(result, "secret").Status);
    }

    [Fact]
    public async Task WeChat_test_detail_never_contains_secret_even_if_vendor_echoes_it()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK,
            $$"""{"errcode":40125,"errmsg":"bad {{WeChatSecret}}"}""")));

        var result = await new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp);

        AssertNoSecret(result, WeChatSecret);
        Assert.Contains("***", Check(result, "secret").Detail);
    }

    [Fact]
    public async Task WeChat_test_propagates_caller_cancellation()
    {
        using var sp = WeChatServices(new FakeHandler(Json(HttpStatusCode.OK, "{}")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WeChatAuthProviderType().TestAsync(WeChatConfig(), sp, cts.Token));
    }

    // ───────────────────────── Setup ─────────────────────────

    [Fact]
    public void Parameterless_setup_registers_type_descriptors_and_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSmartAdminGitHubAuth();
        services.AddSmartAdminGitHubAuth();
        services.AddSmartAdminWeChatAuth();
        services.AddSmartAdminWeChatAuth();

        using var sp = services.BuildServiceProvider();
        var types = sp.GetServices<IExternalAuthProviderType>().ToList();
        Assert.Equal(2, types.Count);
        Assert.Single(types, t => t is GitHubAuthProviderType && t.Type == "github");
        Assert.Single(types, t => t is WeChatAuthProviderType && t.Type == "wechat");
        // 类型描述本身不带 provider:provider 由注册表按库里的配置现建
        Assert.Empty(sp.GetServices<IExternalAuthProvider>());
        Assert.NotNull(sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubExternalAuthProvider.HttpClientName));
        Assert.NotNull(sp.GetRequiredService<IHttpClientFactory>().CreateClient(WeChatExternalAuthProvider.HttpClientName));
    }

    [Fact]
    public void Parameterless_setup_keeps_a_preregistered_replacement_type()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IExternalAuthProviderType, CustomGitHubType>();
        services.AddSmartAdminGitHubAuth();

        using var sp = services.BuildServiceProvider();
        var types = sp.GetServices<IExternalAuthProviderType>().ToList();
        Assert.Contains(types, t => t is CustomGitHubType);
        Assert.Contains(types, t => t is GitHubAuthProviderType);
    }

    private sealed class CustomGitHubType : GitHubAuthProviderType;

}
