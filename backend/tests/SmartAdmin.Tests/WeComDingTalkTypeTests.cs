using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Auth.DingTalk;
using SmartAdmin.Auth.WeCom;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>企微 / 钉钉的类型描述:字段清单、Create、TestAsync 四类结果(通过 / 凭据错 / 网络错 / 响应畸形)、无参 Setup 注册。假 HttpMessageHandler,不触网。</summary>
public class WeComDingTalkTypeTests
{
    private const string Secret = "S3cr3t-value-0123456789";

    private sealed class StepHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps = new();

        public List<string> Urls { get; } = [];

        public List<string> Bodies { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) =>
            _steps.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public void EnqueueThrow(Exception ex) => _steps.Enqueue(() => throw ex);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _steps.Count == 0 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : _steps.Dequeue()();
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IServiceProvider Services(StepHandler handler) =>
        new ServiceCollection().AddSingleton<IHttpClientFactory>(new FakeFactory(handler)).BuildServiceProvider();

    private static ExternalAuthProviderConfig WeComConfig(string displayName = "", string? icon = null) => new(
        "whatever", displayName, icon,
        new Dictionary<string, string> { ["corpId"] = "wwcorp", ["agentId"] = "1000002", ["corpSecret"] = Secret });

    private static ExternalAuthProviderConfig DingTalkConfig(string displayName = "", string? icon = null) => new(
        "whatever", displayName, icon,
        new Dictionary<string, string> { ["appKey"] = "dingkey", ["appSecret"] = Secret });

    private static void AssertNoSecret(ExternalAuthTestResult result, params string[] secrets)
    {
        foreach (var check in result.Checks)
            foreach (var s in secrets)
                Assert.DoesNotContain(s, check.Detail ?? "");
    }

    private static IReadOnlyDictionary<string, ExternalAuthCheckStatus> ByKey(ExternalAuthTestResult r) =>
        r.Checks.ToDictionary(c => c.Key, c => c.Status);

    // ───────── 企业微信:类型描述 ─────────

    [Fact]
    public void WeCom_fields_mark_only_corpSecret_as_secret()
    {
        var type = new WeComAuthProviderType();

        Assert.Equal("wecom", type.Type);
        Assert.False(type.AllowMultiple);
        Assert.Equal(["corpId", "agentId", "corpSecret"], type.Fields.Select(f => f.Name));
        Assert.Equal([false, false, true], type.Fields.Select(f => f.Secret));
        Assert.All(type.Fields, f => Assert.True(f.Required));
        Assert.All(type.Fields, f => Assert.False(f.DefinesEndpoint));
        Assert.Equal("企业微信", type.DefaultDisplayName);
        Assert.Equal("ph:qr-code-duotone", type.DefaultIcon);
    }

    [Fact]
    public async Task WeCom_create_falls_back_to_defaults_and_pins_code()
    {
        var provider = new WeComAuthProviderType().Create(WeComConfig(), Services(new StepHandler()));

        Assert.Equal("wecom", provider.Code);
        Assert.Equal("企业微信", provider.DisplayName);
        Assert.Equal("ph:qr-code-duotone", provider.Icon);

        // 配置里的值确实进了 provider
        var url = await provider.BuildAuthorizeUrlAsync(new ExternalAuthorizeRequest("st", "n", "ch", "https://app/cb"));
        Assert.Contains("appid=wwcorp", url);
        Assert.Contains("agentid=1000002", url);
    }

    [Fact]
    public void WeCom_create_uses_configured_display_name_and_icon()
    {
        var provider = new WeComAuthProviderType().Create(WeComConfig(" 公司企微 ", "ph:buildings"), Services(new StepHandler()));

        Assert.Equal("wecom", provider.Code);
        Assert.Equal("公司企微", provider.DisplayName);
        Assert.Equal("ph:buildings", provider.Icon);
    }

    // ───────── 企业微信:TestAsync ─────────

    [Fact]
    public async Task WeCom_test_all_ok()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok","access_token":"tok-abc","expires_in":7200}""");
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok","agentid":1000002,"name":"SmartAdmin","close":0}""");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.True(result.Ok);
        Assert.Equal(["reachable", "credentials", "agent"], result.Checks.Select(c => c.Key));
        Assert.All(result.Checks, c => Assert.Equal(ExternalAuthCheckStatus.Ok, c.Status));
        Assert.Contains("gettoken?corpid=wwcorp", h.Urls[0]);
        Assert.Contains("agent/get?", h.Urls[1]);
        Assert.Contains("agentid=1000002", h.Urls[1]);
        AssertNoSecret(result, Secret, "tok-abc");
    }

    [Fact]
    public async Task WeCom_test_wrong_secret_fails_credentials_and_skips_agent()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"errcode":40001,"errmsg":"invalid credential"}""");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Ok, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["credentials"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, status["agent"]);
        Assert.Contains("40001", result.Checks.Single(c => c.Key == "credentials").Detail);
        Assert.Single(h.Urls);
    }

    [Fact]
    public async Task WeCom_test_unknown_agent_fails_only_agent()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"access_token":"tok-abc","expires_in":7200}""");
        h.Enqueue(HttpStatusCode.OK, """{"errcode":40056,"errmsg":"invalid agentid"}""");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Ok, status["credentials"]);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["agent"]);
        Assert.Contains("40056", result.Checks.Single(c => c.Key == "agent").Detail);
        AssertNoSecret(result, Secret, "tok-abc");
    }

    [Fact]
    public async Task WeCom_test_disabled_agent_fails()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"access_token":"tok-abc","expires_in":7200}""");
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"agentid":1000002,"close":1}""");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, ByKey(result)["agent"]);
    }

    [Fact]
    public async Task WeCom_test_network_error_is_a_failed_item_not_an_exception()
    {
        var h = new StepHandler();
        h.EnqueueThrow(new HttpRequestException($"connect failed https://qyapi.weixin.qq.com/cgi-bin/gettoken?corpsecret={Secret}"));

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, status["credentials"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, status["agent"]);
        AssertNoSecret(result, Secret);
    }

    [Fact]
    public async Task WeCom_test_network_error_on_the_agent_call_keeps_earlier_results()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"access_token":"tok-abc","expires_in":7200}""");
        h.EnqueueThrow(new HttpRequestException("boom"));

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        var status = ByKey(result);
        Assert.Equal(3, result.Checks.Count);
        Assert.Equal(ExternalAuthCheckStatus.Ok, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Ok, status["credentials"]);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["agent"]);
    }

    [Fact]
    public async Task WeCom_test_timeout_is_a_failed_item()
    {
        var h = new StepHandler();
        h.EnqueueThrow(new TaskCanceledException("timeout"));

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, ByKey(result)["reachable"]);
    }

    [Fact]
    public async Task WeCom_test_caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var h = new StepHandler();
        h.EnqueueThrow(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, "[1,2,3]")]
    [InlineData(HttpStatusCode.OK, """{"errcode":"oops"}""")]
    [InlineData(HttpStatusCode.OK, """{"errcode":0}""")]
    public async Task WeCom_test_malformed_token_response_fails_credentials(HttpStatusCode status, string body)
    {
        var h = new StepHandler();
        h.Enqueue(status, body);

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        var byKey = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Failed, byKey["credentials"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, byKey["agent"]);
    }

    [Fact]
    public async Task WeCom_test_malformed_agent_response_fails_agent()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"access_token":"tok-abc","expires_in":7200}""");
        h.Enqueue(HttpStatusCode.OK, "not json");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, ByKey(result)["agent"]);
    }

    [Fact]
    public async Task WeCom_test_server_error_fails_reachable()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.BadGateway, "<html>502</html>");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, status["credentials"]);
    }

    [Fact]
    public async Task WeCom_test_detail_masks_a_secret_echoed_by_the_vendor()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, $$"""{"errcode":40001,"errmsg":"invalid secret {{Secret}}"}""");

        var result = await new WeComAuthProviderType().TestAsync(WeComConfig(), Services(h));

        AssertNoSecret(result, Secret);
        Assert.Contains("40001", result.Checks.Single(c => c.Key == "credentials").Detail);
    }

    // ───────── 企业微信:Setup ─────────

    [Fact]
    public void WeCom_parameterless_setup_registers_the_type_once_and_configures_the_client()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminWeComAuth();
        services.AddSmartAdminWeComAuth();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<WeComAuthProviderType>(Assert.Single(sp.GetServices<IExternalAuthProviderType>()));
        Assert.Empty(sp.GetServices<IExternalAuthProvider>());
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WeComExternalAuthProvider.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }


    [Fact]
    public void WeCom_options_overload_registers_a_code_provider_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminWeComAuth(new WeComAuthOptions { CorpId = "c", AgentId = "1", CorpSecret = "s" });
        using var sp = services.BuildServiceProvider();

        Assert.Equal("wecom", Assert.Single(sp.GetServices<IExternalAuthProvider>()).Code);
        Assert.Empty(sp.GetServices<IExternalAuthProviderType>());
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSmartAdminWeComAuth(new WeComAuthOptions { CorpId = "c" }));
    }

    // ───────── 钉钉:类型描述 ─────────

    [Fact]
    public void DingTalk_fields_mark_only_appSecret_as_secret()
    {
        var type = new DingTalkAuthProviderType();

        Assert.Equal("dingtalk", type.Type);
        Assert.False(type.AllowMultiple);
        Assert.Equal(["appKey", "appSecret"], type.Fields.Select(f => f.Name));
        Assert.Equal([false, true], type.Fields.Select(f => f.Secret));
        Assert.All(type.Fields, f => Assert.True(f.Required));
        Assert.All(type.Fields, f => Assert.False(f.DefinesEndpoint));
        Assert.Equal("钉钉", type.DefaultDisplayName);
        Assert.Equal("ph:qr-code-duotone", type.DefaultIcon);
    }

    [Fact]
    public async Task DingTalk_create_falls_back_to_defaults_and_pins_code()
    {
        var provider = new DingTalkAuthProviderType().Create(DingTalkConfig(), Services(new StepHandler()));

        Assert.Equal("dingtalk", provider.Code);
        Assert.Equal("钉钉", provider.DisplayName);
        Assert.Equal("ph:qr-code-duotone", provider.Icon);

        var url = await provider.BuildAuthorizeUrlAsync(new ExternalAuthorizeRequest("st", "n", "ch", "https://app/cb"));
        Assert.Contains("client_id=dingkey", url);
    }

    [Fact]
    public void DingTalk_create_uses_configured_display_name_and_icon()
    {
        var provider = new DingTalkAuthProviderType().Create(DingTalkConfig("公司钉钉", "ph:chat"), Services(new StepHandler()));

        Assert.Equal("dingtalk", provider.Code);
        Assert.Equal("公司钉钉", provider.DisplayName);
        Assert.Equal("ph:chat", provider.Icon);
    }

    // ───────── 钉钉:TestAsync ─────────

    [Fact]
    public async Task DingTalk_test_ok_sends_credentials_in_the_body_not_the_url()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.OK, """{"accessToken":"tok-abc","expireIn":7200}""");

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        Assert.True(result.Ok);
        Assert.Equal(["reachable", "credentials"], result.Checks.Select(c => c.Key));
        Assert.All(result.Checks, c => Assert.Equal(ExternalAuthCheckStatus.Ok, c.Status));
        Assert.Equal("https://api.dingtalk.com/v1.0/oauth2/accessToken", h.Urls[0]);
        Assert.Contains("\"appKey\":\"dingkey\"", h.Bodies[0]);
        Assert.Contains(Secret, h.Bodies[0]);
        AssertNoSecret(result, Secret, "tok-abc");
    }

    [Fact]
    public async Task DingTalk_test_wrong_credentials_fails_credentials()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.BadRequest, """{"code":"invalidClientId.notFound","message":"client not found","requestid":"r1"}""");

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Ok, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["credentials"]);
        var detail = result.Checks.Single(c => c.Key == "credentials").Detail;
        Assert.Contains("invalidClientId.notFound", detail);
        Assert.Contains("400", detail);
    }

    [Fact]
    public async Task DingTalk_test_network_error_is_a_failed_item_not_an_exception()
    {
        var h = new StepHandler();
        h.EnqueueThrow(new HttpRequestException($"connect failed {Secret}"));

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, status["credentials"]);
        AssertNoSecret(result, Secret);
    }

    [Fact]
    public async Task DingTalk_test_timeout_is_a_failed_item()
    {
        var h = new StepHandler();
        h.EnqueueThrow(new TaskCanceledException("timeout"));

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, ByKey(result)["reachable"]);
    }

    [Fact]
    public async Task DingTalk_test_caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var h = new StepHandler();
        h.EnqueueThrow(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, "[1,2,3]")]
    [InlineData(HttpStatusCode.OK, """{"expireIn":7200}""")]
    [InlineData(HttpStatusCode.OK, """{"accessToken":123}""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task DingTalk_test_malformed_response_fails_credentials(HttpStatusCode status, string body)
    {
        var h = new StepHandler();
        h.Enqueue(status, body);

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        Assert.False(result.Ok);
        Assert.Equal(ExternalAuthCheckStatus.Failed, ByKey(result)["credentials"]);
    }

    [Fact]
    public async Task DingTalk_test_server_error_fails_reachable()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.ServiceUnavailable, "");

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        Assert.False(result.Ok);
        var status = ByKey(result);
        Assert.Equal(ExternalAuthCheckStatus.Failed, status["reachable"]);
        Assert.Equal(ExternalAuthCheckStatus.Skipped, status["credentials"]);
    }

    [Fact]
    public async Task DingTalk_test_detail_masks_a_secret_echoed_by_the_vendor()
    {
        var h = new StepHandler();
        h.Enqueue(HttpStatusCode.BadRequest, $$"""{"code":"authenticationFailed","message":"bad secret {{Secret}}"}""");

        var result = await new DingTalkAuthProviderType().TestAsync(DingTalkConfig(), Services(h));

        AssertNoSecret(result, Secret);
        Assert.Contains("authenticationFailed", result.Checks.Single(c => c.Key == "credentials").Detail);
    }

    // ───────── 钉钉:Setup ─────────

    [Fact]
    public void DingTalk_parameterless_setup_registers_the_type_once_and_configures_the_client()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminDingTalkAuth();
        services.AddSmartAdminDingTalkAuth();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<DingTalkAuthProviderType>(Assert.Single(sp.GetServices<IExternalAuthProviderType>()));
        Assert.Empty(sp.GetServices<IExternalAuthProvider>());
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(DingTalkExternalAuthProvider.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }


    [Fact]
    public void DingTalk_options_overload_registers_a_code_provider_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminDingTalkAuth(new DingTalkAuthOptions { AppKey = "k", AppSecret = "s" });
        using var sp = services.BuildServiceProvider();

        Assert.Equal("dingtalk", Assert.Single(sp.GetServices<IExternalAuthProvider>()).Code);
        Assert.Empty(sp.GetServices<IExternalAuthProviderType>());
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSmartAdminDingTalkAuth(new DingTalkAuthOptions { AppKey = "k" }));
    }
}
