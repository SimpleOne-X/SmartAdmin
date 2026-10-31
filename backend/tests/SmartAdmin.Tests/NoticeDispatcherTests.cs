using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 通知后台分发:发布 → 入队 → 解析接收人 → 调用发送方。发送方用替身,不连任何真实渠道。
/// "没发"的断言一律靠哨兵:另注册一个总开着的 provider 排在后面,队列按序处理,
/// 哨兵收到了就说明前面的已经处理完,这时再断言前者没收到,不靠 Delay。
/// </summary>
public class NoticeDispatcherTests
{
    private const string BaseUrl = "https://admin.example.com/app/";

    private static readonly IReadOnlyDictionary<string, string?> WithBaseUrl =
        new Dictionary<string, string?> { ["SmartAdmin:ExternalAuth:CallbackBaseUrl"] = BaseUrl };

    /// <summary>替身 provider:登录那半是空壳,发送那半记录每次调用,并按尝试次数决定抛什么。</summary>
    internal sealed class FakeSenderProvider(string code) : IExternalAuthProvider, IExternalNoticeSender
    {
        public sealed record Call(ExternalNoticeMessage Message, IReadOnlyList<string> Subjects);

        private readonly ConcurrentQueue<Call> _calls = new();
        private readonly SemaphoreSlim _signal = new(0);
        private int _attempts;

        public string Code => code;
        public string DisplayName => code;
        public string? Icon => null;

        /// <summary>第 n 次调用(从 1 起)抛什么;返回 null 就正常返回 <see cref="Result"/>。</summary>
        public Func<int, Exception?> Fail { get; set; } = _ => null;

        public ExternalNoticeResult Result { get; set; } = new([]);

        public int CallCount => _calls.Count;
        public IReadOnlyList<Call> Calls => [.. _calls];

        public Task<string> BuildAuthorizeUrlAsync(ExternalAuthorizeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalIdentity> ExchangeAsync(ExternalExchangeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalNoticeResult> SendAsync(
            ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(new Call(message, [.. subjects]));
            var n = Interlocked.Increment(ref _attempts);
            _signal.Release();
            var ex = Fail(n);
            if (ex is not null) throw ex;
            return Task.FromResult(Result);
        }

        /// <summary>等到累计收到 <paramref name="count"/> 次调用(等的是"发生"这件事,超时只是兜底,不是断言)。</summary>
        public async Task WaitForCallsAsync(int count = 1)
        {
            for (var i = 0; i < count; i++)
                if (!await _signal.WaitAsync(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException($"{code} 在 30 秒内没有收到第 {i + 1} 次调用");
        }
    }

    /// <summary>只能登录、不能发送的 provider。</summary>
    private sealed class PlainProvider(string code) : IExternalAuthProvider
    {
        public string Code => code;
        public string DisplayName => code;
        public string? Icon => null;

        public Task<string> BuildAuthorizeUrlAsync(ExternalAuthorizeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalIdentity> ExchangeAsync(ExternalExchangeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>收集日志,并能等到某一条出现。</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(LogLevel Level, string Category, string Message);

        private readonly List<Entry> _entries = [];
        private readonly List<(Func<Entry, bool> Match, TaskCompletionSource<Entry> Done)> _waiters = [];

        public IReadOnlyList<Entry> Entries { get { lock (_entries) return [.. _entries]; } }

        public IEnumerable<Entry> Of(LogLevel level) => Entries.Where(e => e.Level == level && e.Category.EndsWith("NoticeDispatcher"));

        public Task<Entry> WaitAsync(Func<Entry, bool> match)
        {
            lock (_entries)
            {
                var hit = _entries.FirstOrDefault(match);
                if (hit is not null) return Task.FromResult(hit);
                var tcs = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((match, tcs));
                return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var entry = new Entry(logLevel, category, formatter(state, exception));
                lock (owner._entries)
                {
                    owner._entries.Add(entry);
                    foreach (var w in owner._waiters.Where(w => w.Match(entry)).ToList())
                    {
                        owner._waiters.Remove(w);
                        w.Done.TrySetResult(entry);
                    }
                }
            }
        }
    }

    /// <summary>可调的分发器子类:快速重试、固定的接收人、计数。</summary>
    internal sealed class TestDispatcher(IServiceScopeFactory scopes, AdminExternalAuthOptions options, ILogger<NoticeDispatcher> logger)
        : NoticeDispatcher(scopes, options, logger)
    {
        public bool FastRetry { get; init; } = true;
        public Func<string, List<string>>? Subjects { get; init; }
        public int ResolveCalls;

        public TimeSpan DefaultRetryDelay(int failedAttempts) => base.GetRetryDelay(failedAttempts);

        protected override Task<List<string>> ResolveSubjectsAsync(
            IServiceProvider services, NoticeDispatchItem item, string providerCode, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveCalls);
            return Subjects is null
                ? base.ResolveSubjectsAsync(services, item, providerCode, cancellationToken)
                : Task.FromResult(Subjects(providerCode));
        }

        protected override TimeSpan GetRetryDelay(int failedAttempts) => FastRetry ? TimeSpan.Zero : base.GetRetryDelay(failedAttempts);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("探测失败");
    }

    private static AdminAppFactory Host(
        CapturingLoggerProvider log,
        IReadOnlyList<IExternalAuthProvider> providers,
        bool baseUrl = true,
        Func<IServiceProvider, TestDispatcher>? dispatcher = null) => new()
    {
        Settings = baseUrl ? WithBaseUrl : null,
        Overrides = s =>
        {
            foreach (var p in providers) s.AddSingleton(p);
            s.AddSingleton<ILoggerProvider>(log);
            if (dispatcher is not null)
            {
                s.RemoveAll<NoticeDispatcher>();
                s.AddSingleton<NoticeDispatcher>(sp => dispatcher(sp));
            }
        },
    };

    private static TestDispatcher NewTestDispatcher(IServiceProvider sp, bool fastRetry = true, Func<string, List<string>>? subjects = null) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<AdminExternalAuthOptions>(),
            sp.GetRequiredService<ILogger<NoticeDispatcher>>())
        { FastRetry = fastRetry, Subjects = subjects };

    private static async Task<long> BoundUser(AdminAppFactory f, string provider, string subject)
    {
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var user = new SysUser { Account = $"u-{provider}-{subject}", Password = "x", Name = subject, Enabled = true };
        await sp.GetRequiredService<IRepository<SysUser>>().InsertAsync(user);
        await sp.GetRequiredService<ISysUserExternalService>().BindAsync(user.Id, new ExternalIdentity(provider, subject));
        return user.Id;
    }

    private static async Task<long> Publish(AdminAppFactory f, string title, string? content = null)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INoticeService>()
            .PublishAsync(new NoticePublishInput { Title = title, Content = content });
    }

    private static async Task SetConfig(AdminAppFactory f, string key, string value)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConfigService>()
            .AddAsync(new ConfigInput { ConfigKey = key, ConfigValue = value, Name = key });
    }

    private static NoticeDispatcher Direct(CapturingLoggerProvider log, IServiceScopeFactory scopes) =>
        new(scopes, new AdminExternalAuthOptions(), new LoggerFactory([log]).CreateLogger<NoticeDispatcher>());

    private static IServiceScopeFactory ScopesWith(params IExternalAuthProvider[] providers)
    {
        var s = new ServiceCollection();
        foreach (var p in providers) s.AddSingleton(p);
        return s.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static NoticeDispatchItem Item(long id) => new(id, "t", null, ReceiverType.All, []);

    // ── 消息内容 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_builds_message_with_absolute_link()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        await BoundUser(f, "fake", "s-1");

        var id = await Publish(f, "审批", "请处理\n[前往审批](/flow/todo?taskId=1)");
        await fake.WaitForCallsAsync();

        var call = Assert.Single(fake.Calls);
        Assert.Equal(id, call.Message.NoticeId);
        Assert.Equal("审批", call.Message.Title);
        Assert.Equal("请处理\n前往审批", call.Message.Text);
        Assert.Equal("https://admin.example.com/app/flow/todo?taskId=1", call.Message.Url);
        Assert.Equal(["s-1"], call.Subjects);
    }

    [Fact]
    public async Task Publish_without_link_lands_on_notice_page()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        await BoundUser(f, "fake", "s-1");

        await Publish(f, "公告", "今晚维护");
        await fake.WaitForCallsAsync();

        Assert.Equal("https://admin.example.com/app/personal/notice", Assert.Single(fake.Calls).Message.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[](/flow/todo?taskId=1)")]
    [InlineData("![](/a.png)")]
    public async Task Publish_with_link_only_or_empty_content_uses_title_as_text(string? content)
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        await BoundUser(f, "fake", "s-1");

        await Publish(f, "只有标题", content);
        await fake.WaitForCallsAsync();

        Assert.Equal("只有标题", Assert.Single(fake.Calls).Message.Text);
    }

    [Fact]
    public async Task Publish_encodes_non_ascii_link_and_falls_back_when_too_long()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        await BoundUser(f, "fake", "s-1");

        await Publish(f, "含中文", "[看](/a?name=张三)");
        await fake.WaitForCallsAsync();
        var encoded = fake.Calls[0].Message.Url;
        Assert.Contains("%E5%BC%A0", encoded);
        Assert.DoesNotContain("张", encoded);

        // 每个汉字编码成 9 个字符,400 个远超 2048 字节
        await Publish(f, "太长", $"[看](/a?name={new string('张', 400)})");
        await fake.WaitForCallsAsync();
        Assert.Equal("https://admin.example.com/app/personal/notice", fake.Calls[1].Message.Url);
    }

    [Fact]
    public async Task Publish_without_base_url_skips_send()
    {
        var log = new CapturingLoggerProvider();
        var fake = new FakeSenderProvider("fake");
        using var f = Host(log, [fake], baseUrl: false);
        await BoundUser(f, "fake", "s-1");

        await Publish(f, "没有基址");
        await log.WaitAsync(e => e.Level == LogLevel.Warning && e.Category.EndsWith("NoticeDispatcher"));

        Assert.Equal(0, fake.CallCount);
        Assert.Single(log.Of(LogLevel.Warning));
    }

    // ── 开关与分批 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("enabled")]
    [InlineData("notify")]
    public async Task Enabled_or_notify_off_skips_that_provider(string key)
    {
        var fake = new FakeSenderProvider("fake");
        var sentinel = new FakeSenderProvider("sentinel");
        using var f = Host(new CapturingLoggerProvider(), [fake, sentinel]);
        await BoundUser(f, "fake", "s-1");
        await BoundUser(f, "sentinel", "s-2");
        await SetConfig(f, $"sys.externalauth.fake.{key}", "false");

        await Publish(f, "关了");
        await sentinel.WaitForCallsAsync();

        Assert.Equal(0, fake.CallCount);
        Assert.Equal(1, sentinel.CallCount);
    }

    [Fact]
    public async Task More_than_1000_subjects_are_batched()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake],
            dispatcher: sp => NewTestDispatcher(sp, subjects: _ => [.. Enumerable.Range(0, 2500).Select(i => $"s-{i}")]));
        await BoundUser(f, "fake", "s-1");   // 让探测通过之外无其他作用,接收人由子类直接给出

        await Publish(f, "很多人");
        await fake.WaitForCallsAsync(3);

        Assert.Equal([1000, 1000, 500], fake.Calls.Select(c => c.Subjects.Count));
        Assert.Equal(2500, fake.Calls.SelectMany(c => c.Subjects).Distinct().Count());
    }

    [Fact]
    public void Default_retry_delays_are_5_then_30_seconds()
    {
        using var f = Host(new CapturingLoggerProvider(), [new FakeSenderProvider("fake")]);
        var d = NewTestDispatcher(f.Services, fastRetry: false);

        Assert.Equal(TimeSpan.FromSeconds(5), d.DefaultRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), d.DefaultRetryDelay(2));
    }

    // ── 重试与隔离 ───────────────────────────────────────────────────────

    [Fact]
    public async Task Retryable_failure_retries_up_to_three_attempts()
    {
        var log = new CapturingLoggerProvider();
        var fake = new FakeSenderProvider("fake") { Fail = _ => new ExternalNoticeException("繁忙", retryable: true, "-1") };
        var sentinel = new FakeSenderProvider("sentinel");
        using var f = Host(log, [fake, sentinel], dispatcher: sp => NewTestDispatcher(sp));
        await BoundUser(f, "fake", "s-1");
        await BoundUser(f, "sentinel", "s-2");

        await Publish(f, "重试");
        await sentinel.WaitForCallsAsync();

        Assert.Equal(3, fake.CallCount);
        var error = Assert.Single(log.Of(LogLevel.Error));
        Assert.Contains("fake", error.Message);
        Assert.Contains("-1", error.Message);
        Assert.Contains("繁忙", error.Message);
    }

    [Fact]
    public async Task Non_retryable_failure_sends_once()
    {
        var log = new CapturingLoggerProvider();
        var fake = new FakeSenderProvider("fake") { Fail = _ => new ExternalNoticeException("IP 不可信", retryable: false, "60020") };
        var sentinel = new FakeSenderProvider("sentinel");
        using var f = Host(log, [fake, sentinel], dispatcher: sp => NewTestDispatcher(sp));
        await BoundUser(f, "fake", "s-1");
        await BoundUser(f, "sentinel", "s-2");

        await Publish(f, "不重试");
        await sentinel.WaitForCallsAsync();

        Assert.Equal(1, fake.CallCount);
        Assert.Contains("60020", Assert.Single(log.Of(LogLevel.Error)).Message);
    }

    [Fact]
    public async Task Provider_failure_does_not_block_others_or_later_notices()
    {
        var fake = new FakeSenderProvider("fake") { Fail = _ => new InvalidOperationException("boom") };
        var sentinel = new FakeSenderProvider("sentinel");
        using var f = Host(new CapturingLoggerProvider(), [fake, sentinel], dispatcher: sp => NewTestDispatcher(sp));
        await BoundUser(f, "fake", "s-1");
        await BoundUser(f, "sentinel", "s-2");

        await Publish(f, "第一条");
        await sentinel.WaitForCallsAsync();
        await Publish(f, "第二条");
        await sentinel.WaitForCallsAsync();

        Assert.Equal(["第一条", "第二条"], sentinel.Calls.Select(c => c.Message.Title));
        Assert.Equal(2, fake.CallCount);   // 非 ExternalNoticeException 不重试:每条各一次
    }

    [Fact]
    public async Task Publish_succeeds_when_sender_throws()
    {
        var fake = new FakeSenderProvider("fake") { Fail = _ => new InvalidOperationException("boom") };
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        await BoundUser(f, "fake", "s-1");

        var id = await Publish(f, "发送方坏了");
        await fake.WaitForCallsAsync();

        using var scope = f.Services.CreateScope();
        var notice = await scope.ServiceProvider.GetRequiredService<IRepository<SysNotice>>().GetByIdAsync(id);
        Assert.Equal("发送方坏了", notice?.Title);
    }

    [Fact]
    public async Task Shutdown_during_retry_wait_ends_promptly()
    {
        var log = new CapturingLoggerProvider();
        var fake = new FakeSenderProvider("fake") { Fail = _ => new ExternalNoticeException("繁忙", retryable: true, "-1") };
        using var f = Host(log, [fake], dispatcher: sp => NewTestDispatcher(sp, fastRetry: false));
        await BoundUser(f, "fake", "s-1");

        await Publish(f, "停机");
        await fake.WaitForCallsAsync();   // 第一次失败后进入 5 秒的重试等待

        // 取消令牌要传进等待里:否则这里会等满 5 秒(之后还有 30 秒)
        await f.Services.GetRequiredService<NoticeDispatcher>().StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, fake.CallCount);
        Assert.Empty(log.Of(LogLevel.Error));
    }

    // ── 入队 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Full_queue_drops_without_throwing()
    {
        var log = new CapturingLoggerProvider();
        var dispatcher = Direct(log, ScopesWith(new FakeSenderProvider("fake")));   // 未启动:没人取队列

        for (var i = 1; i <= 10001; i++) dispatcher.Enqueue(Item(i));

        var error = Assert.Single(log.Of(LogLevel.Error));
        Assert.Contains("10001", error.Message);   // 带上被丢弃的 NoticeId
    }

    [Fact]
    public void No_sender_means_nothing_is_queued()
    {
        var log = new CapturingLoggerProvider();
        var dispatcher = Direct(log, ScopesWith(new PlainProvider("plain")));

        // 真入了队的话,第 10001 条会撑爆队列并记一条 Error
        for (var i = 1; i <= 10001; i++) dispatcher.Enqueue(Item(i));

        Assert.Empty(log.Of(LogLevel.Error));
    }

    [Fact]
    public async Task No_sender_means_recipients_are_never_resolved()
    {
        var plain = new PlainProvider("plain");
        TestDispatcher? dispatcher = null;
        using var f = Host(new CapturingLoggerProvider(), [plain], dispatcher: sp => dispatcher = NewTestDispatcher(sp));
        _ = f.Services;

        await Publish(f, "没人发");
        await Publish(f, "没人发 2");

        Assert.Equal(0, dispatcher!.ResolveCalls);
    }

    [Fact]
    public void Enqueue_never_throws_when_the_probe_fails()
    {
        var log = new CapturingLoggerProvider();
        var dispatcher = Direct(log, new ThrowingScopeFactory());

        dispatcher.Enqueue(Item(1));

        Assert.Single(log.Of(LogLevel.Error));
    }

    // ── 测试消息 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SendTest_unsupported_when_provider_missing_or_cannot_send()
    {
        using var f = Host(new CapturingLoggerProvider(), [new PlainProvider("plain")]);
        var dispatcher = f.Services.GetRequiredService<INoticeDispatcher>();

        Assert.Equal("unsupported", (await dispatcher.SendTestAsync("nope", 1)).Status);
        Assert.Equal("unsupported", (await dispatcher.SendTestAsync("plain", 1)).Status);
    }

    [Fact]
    public async Task SendTest_not_bound()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        await BoundUser(f, "fake", "s-1");   // 别人绑了,当前用户没绑

        var view = await f.Services.GetRequiredService<INoticeDispatcher>().SendTestAsync("fake", 424242);

        Assert.Equal("notBound", view.Status);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task SendTest_no_base_url()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake], baseUrl: false);
        var userId = await BoundUser(f, "fake", "s-1");

        var view = await f.Services.GetRequiredService<INoticeDispatcher>().SendTestAsync("fake", userId);

        Assert.Equal("noBaseUrl", view.Status);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task SendTest_ok_sends_one_message_with_time_in_text()
    {
        var fake = new FakeSenderProvider("fake");
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        var userId = await BoundUser(f, "fake", "s-1");

        var view = await f.Services.GetRequiredService<INoticeDispatcher>().SendTestAsync("fake", userId);

        Assert.Equal("ok", view.Status);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(["s-1"], call.Subjects);
        Assert.Equal("测试消息", call.Message.Title);
        Assert.Matches(@"发送时间 \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", call.Message.Text);
        Assert.Equal("https://admin.example.com/app/personal/notice", call.Message.Url);
    }

    [Fact]
    public async Task SendTest_rejected_when_the_channel_refuses_the_recipient()
    {
        var fake = new FakeSenderProvider("fake") { Result = new ExternalNoticeResult(["s-1"]) };
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        var userId = await BoundUser(f, "fake", "s-1");

        var view = await f.Services.GetRequiredService<INoticeDispatcher>().SendTestAsync("fake", userId);

        Assert.Equal("rejected", view.Status);
        Assert.Contains("s-1", view.Detail);
    }

    [Fact]
    public async Task SendTest_failed_reports_provider_code_and_does_not_retry()
    {
        var fake = new FakeSenderProvider("fake") { Fail = _ => new ExternalNoticeException("IP 不可信", retryable: true, "60020") };
        using var f = Host(new CapturingLoggerProvider(), [fake]);
        var userId = await BoundUser(f, "fake", "s-1");

        var view = await f.Services.GetRequiredService<INoticeDispatcher>().SendTestAsync("fake", userId);

        Assert.Equal("failed", view.Status);
        Assert.Equal("60020", view.ProviderCode);
        Assert.Equal("IP 不可信", view.Detail);
        Assert.Equal(1, fake.CallCount);   // 即便 Retryable 也只发一次
    }
}
