using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Auth.WeCom;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>企业微信应用消息发送:请求形状、截断转义、token 刷新、错误分类、机密不外泄(假 HttpMessageHandler,不触网)。</summary>
public class WeComNoticeSenderTests
{
    private sealed record Call(HttpMethod Method, string Uri, string? Body);

    private sealed class SeqHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps = new();

        // 请求体在发送时读出来:调用方可能随后释放请求内容
        public List<Call> Calls { get; } = new();

        public void Enqueue(HttpStatusCode status, string json) =>
            _steps.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> step) => _steps.Enqueue(step);

        public int Count(string pathPart) => Calls.Count(c => c.Uri.Contains(pathPart, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!.ToString(), body));
            return _steps.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : _steps.Dequeue()(request);
        }
    }

    private sealed class CaptureLogger : ILogger<WeComExternalAuthProvider>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }

    private static readonly ExternalNoticeMessage Msg = new(1, "标题", "正文", "https://admin.example.com/notice/1");

    private static WeComExternalAuthProvider Make(
        SeqHandler h, ILogger<WeComExternalAuthProvider>? log = null, string agentId = "1000002", string secret = "sec") =>
        new(new WeComAuthOptions { CorpId = "wwcorp", AgentId = agentId, CorpSecret = secret },
            new HttpClient(h),
            log ?? NullLogger<WeComExternalAuthProvider>.Instance);

    private static string TokenJson(string token = "T") =>
        $$"""{"errcode":0,"access_token":"{{token}}","expires_in":7200}""";

    private static string Err(int errcode, string errmsg = "x") =>
        $$"""{"errcode":{{errcode}},"errmsg":"{{errmsg}}"}""";

    private static JsonElement Body(Call call) => JsonDocument.Parse(call.Body!).RootElement;

    [Fact]
    public async Task Send_posts_a_textcard_with_duplicate_check()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok"}""");

        var result = await Make(h).SendAsync(Msg, ["u1", "u2"]);

        Assert.Empty(result.Rejected);
        Assert.Equal(2, h.Calls.Count);
        var send = h.Calls[1];
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal("https://qyapi.weixin.qq.com/cgi-bin/message/send?access_token=T", send.Uri);
        var json = Body(send);
        Assert.Equal("u1|u2", json.GetProperty("touser").GetString());
        Assert.Equal("textcard", json.GetProperty("msgtype").GetString());
        Assert.Equal(JsonValueKind.Number, json.GetProperty("agentid").ValueKind);
        Assert.Equal(1000002, json.GetProperty("agentid").GetInt32());
        var card = json.GetProperty("textcard");
        Assert.Equal("标题", card.GetProperty("title").GetString());
        Assert.Equal("正文", card.GetProperty("description").GetString());
        Assert.Equal("https://admin.example.com/notice/1", card.GetProperty("url").GetString());
        Assert.Equal("详情", card.GetProperty("btntxt").GetString());
        Assert.Equal(1, json.GetProperty("enable_duplicate_check").GetInt32());
        Assert.Equal(600, json.GetProperty("duplicate_check_interval").GetInt32());
    }

    [Fact]
    public async Task Send_returns_invaliduser_and_unlicenseduser_as_rejected()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0,"errmsg":"ok","invaliduser":"u1|u2","unlicenseduser":"u3"}""");

        var result = await Make(h).SendAsync(Msg, ["u1", "u2", "u3", "u4"]);

        Assert.Equal(["u1", "u2", "u3"], result.Rejected);
    }

    [Fact]
    public async Task Send_truncates_title_to_128_and_escapes_description()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0}""");

        await Make(h).SendAsync(Msg with { Title = new string('标', 200), Text = "a<b&c>" }, ["u1"]);

        var card = Body(h.Calls[1]).GetProperty("textcard");
        Assert.Equal(new string('标', 128), card.GetProperty("title").GetString());
        Assert.Equal("a&lt;b&amp;c&gt;", card.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Send_truncated_description_never_splits_an_entity()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0}""");

        await Make(h).SendAsync(Msg with { Text = new string('<', 600) }, ["u1"]);

        var description = Body(h.Calls[1]).GetProperty("textcard").GetProperty("description").GetString()!;
        Assert.True(description.Length <= 512);
        Assert.EndsWith("…", description);
        var kept = description[..^1];
        Assert.Equal(0, kept.Length % 4);
        Assert.Equal(string.Concat(Enumerable.Repeat("&lt;", kept.Length / 4)), kept);
    }

    [Theory]
    [InlineData(40014)]
    [InlineData(42001)]
    public async Task Send_refreshes_token_once_on_invalid_token(int errcode)
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson("T1"));
        h.Enqueue(HttpStatusCode.OK, Err(errcode));
        h.Enqueue(HttpStatusCode.OK, TokenJson("T2"));
        h.Enqueue(HttpStatusCode.OK, """{"errcode":0}""");

        var result = await Make(h).SendAsync(Msg, ["u1"]);

        Assert.Empty(result.Rejected);
        Assert.Equal(2, h.Count("gettoken"));
        Assert.Equal(2, h.Count("message/send"));
        Assert.EndsWith("access_token=T1", h.Calls[1].Uri);
        Assert.EndsWith("access_token=T2", h.Calls[3].Uri);
    }

    [Fact]
    public async Task Send_gives_up_when_refreshed_token_is_still_invalid()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson("T1"));
        h.Enqueue(HttpStatusCode.OK, Err(42001));
        h.Enqueue(HttpStatusCode.OK, TokenJson("T2"));
        h.Enqueue(HttpStatusCode.OK, Err(42001));

        var ex = await Assert.ThrowsAsync<ExternalNoticeException>(() => Make(h).SendAsync(Msg, ["u1"]));

        Assert.False(ex.Retryable);
        Assert.Equal("42001", ex.ProviderCode);
        Assert.Equal(2, h.Count("message/send"));
        Assert.Equal(4, h.Calls.Count);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(60020, false)]
    [InlineData(81013, false)]
    [InlineData(40001, false)]
    public async Task Send_maps_errors_to_retryability(int errcode, bool retryable)
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Enqueue(HttpStatusCode.OK, Err(errcode, "boom"));

        var ex = await Assert.ThrowsAsync<ExternalNoticeException>(() => Make(h).SendAsync(Msg, ["u1"]));

        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal(errcode.ToString(), ex.ProviderCode);
        Assert.Contains($"errcode={errcode}", ex.Message);
        Assert.Equal(2, h.Calls.Count);
    }

    [Theory]
    [InlineData("send-throws")]
    [InlineData("send-502")]
    [InlineData("token-throws")]
    [InlineData("token-502")]
    public async Task Send_network_failure_and_5xx_are_retryable(string mode)
    {
        var h = new SeqHandler();
        if (mode.StartsWith("token", StringComparison.Ordinal))
        {
            if (mode == "token-throws") h.Enqueue(_ => throw new HttpRequestException("net"));
            else h.Enqueue(HttpStatusCode.BadGateway, "bad gateway");
        }
        else
        {
            h.Enqueue(HttpStatusCode.OK, TokenJson());
            if (mode == "send-throws") h.Enqueue(_ => throw new HttpRequestException("net"));
            else h.Enqueue(HttpStatusCode.BadGateway, "bad gateway");
        }

        var ex = await Assert.ThrowsAsync<ExternalNoticeException>(() => Make(h).SendAsync(Msg, ["u1"]));

        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Send_60020_message_mentions_trusted_ip()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, TokenJson());
        h.Enqueue(HttpStatusCode.OK, Err(60020, "not allow to access from your ip"));

        var ex = await Assert.ThrowsAsync<ExternalNoticeException>(() => Make(h).SendAsync(Msg, ["u1"]));

        Assert.Contains("企业可信 IP", ex.Message);
    }

    [Fact]
    public async Task Send_never_leaks_token_or_secret()
    {
        var token = "TOKEN-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var secret = "SECRET-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        // 每个场景覆盖一条失败路径;错误体、异常消息里都故意回显 token / secret / 完整请求 URL
        var scenarios = new Dictionary<string, Action<SeqHandler>>
        {
            ["gettoken errcode"] = h => h.Enqueue(HttpStatusCode.OK, Err(40001, $"invalid {secret}")),
            ["gettoken throws"] = h => h.Enqueue(r => throw new HttpRequestException($"failed {r.RequestUri}")),
            ["gettoken 502"] = h => h.Enqueue(HttpStatusCode.BadGateway, $"{secret}"),
            ["send 60020"] = h => { h.Enqueue(HttpStatusCode.OK, TokenJson(token)); h.Enqueue(HttpStatusCode.OK, Err(60020, $"ip denied {token} {secret}")); },
            ["send -1"] = h => { h.Enqueue(HttpStatusCode.OK, TokenJson(token)); h.Enqueue(HttpStatusCode.OK, Err(-1, $"busy {token}")); },
            ["send 42001 twice"] = h =>
            {
                h.Enqueue(HttpStatusCode.OK, TokenJson(token));
                h.Enqueue(HttpStatusCode.OK, Err(42001, $"expired {token}"));
                h.Enqueue(HttpStatusCode.OK, TokenJson(token));
                h.Enqueue(HttpStatusCode.OK, Err(42001, $"expired {token}"));
            },
            ["send throws"] = h => { h.Enqueue(HttpStatusCode.OK, TokenJson(token)); h.Enqueue(r => throw new HttpRequestException($"failed {r.RequestUri}")); },
            ["send 502"] = h => { h.Enqueue(HttpStatusCode.OK, TokenJson(token)); h.Enqueue(HttpStatusCode.BadGateway, $"{token}"); },
        };

        foreach (var (name, arrange) in scenarios)
        {
            var h = new SeqHandler();
            var log = new CaptureLogger();
            arrange(h);

            var ex = await Assert.ThrowsAsync<ExternalNoticeException>(() => Make(h, log, secret: secret).SendAsync(Msg, ["u1"]));

            Assert.Null(ex.InnerException);
            foreach (var text in new[] { ex.Message, ex.ToString(), string.Join('\n', log.Lines) })
            {
                Assert.False(text.Contains(token, StringComparison.Ordinal), $"{name}: token leaked");
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{name}: secret leaked");
                Assert.False(text.Contains("access_token=", StringComparison.Ordinal), $"{name}: url leaked");
            }
        }
    }

    [Fact]
    public async Task Send_rejects_non_numeric_agent_id()
    {
        var h = new SeqHandler();

        var ex = await Assert.ThrowsAsync<ExternalNoticeException>(() => Make(h, agentId: "abc").SendAsync(Msg, ["u1"]));

        Assert.False(ex.Retryable);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public void WeCom_type_supports_notice()
    {
        Assert.True(new WeComAuthProviderType().SupportsNotice);
        Assert.False(((IExternalAuthProviderType)new AcmeAuthProviderType()).SupportsNotice);
    }

    [Fact]
    public async Task Login_exchange_still_maps_errors_to_OAuthExchangeFailed()
    {
        var h = new SeqHandler();
        h.Enqueue(HttpStatusCode.OK, Err(40001, "invalid credential"));

        var ex = await Assert.ThrowsAnyAsync<AdminException>(() =>
            Make(h).ExchangeAsync(new ExternalExchangeRequest("code", "verifier", "https://app/cb", "nonce")));

        Assert.Equal(ErrorCode.OAuthExchangeFailed, ex.Code);
    }
}
