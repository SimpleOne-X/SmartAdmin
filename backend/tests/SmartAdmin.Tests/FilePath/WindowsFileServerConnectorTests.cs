using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// Windows 连接器的凭据状态机。子类覆写两个 native 调用,返回预设的码并记录调用,时钟是可推进的假时钟,
/// 所以不碰真实的 <c>mpr.dll</c>,任何平台都能跑;真实的 <c>WNetAddConnection2W</c> 靠手工验证。
/// </summary>
public class WindowsFileServerConnectorTests
{
    private const string Share = @"\\nas01\files";

    /// <summary>
    /// 信封是「前缀 + 冒号 + 明文」,断言能看出连接器把解开的明文交给了 native 调用;
    /// 前缀可以随便换,用来造「同一个密码、不同的信封」(真实的信封每次加密都带随机数)。
    /// </summary>
    private sealed class PrefixProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string envelope) => envelope[(envelope.IndexOf(':') + 1)..];
    }

    private sealed class TestConnector(FakeTimeProvider time)
        : WindowsFileServerConnector(new PrefixProtector(), NullLogger<WindowsFileServerConnector>.Instance, time)
    {
        private readonly List<string> _calls = [];

        /// <summary>每次 <see cref="AddConnection"/> 依次取一个返回码,取完之后返回 0。</summary>
        public Queue<int> AddResults { get; } = new();

        public int CancelResult { get; set; }

        public TimeSpan AddDelay { get; set; }

        public TimeSpan CancelDelay { get; set; }

        public TimeSpan? Timeout { get; set; }

        /// <summary>最近一次 <see cref="AddConnection"/> 收到的密码,用来区分空串和 null。</summary>
        public string? LastPassword { get; private set; }

        /// <summary>第一次进入 <see cref="AddConnection"/> 时完成。</summary>
        public TaskCompletionSource AddStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        public int AddCount => Calls.Count(c => c.StartsWith("add ", StringComparison.Ordinal));

        public int CancelCount => Calls.Count(c => c.StartsWith("cancel ", StringComparison.Ordinal));

        protected override TimeSpan ConnectTimeout => Timeout ?? base.ConnectTimeout;

        protected override int AddConnection(string remoteName, string? userName, string? password)
        {
            int rc;
            lock (_calls)
            {
                _calls.Add($"add {remoteName} {userName} {password}");
                LastPassword = password;
                rc = AddResults.Count > 0 ? AddResults.Dequeue() : 0;
            }
            AddStarted.TrySetResult();
            if (AddDelay > TimeSpan.Zero) Thread.Sleep(AddDelay);
            return rc;
        }

        protected override int CancelConnection(string remoteName)
        {
            lock (_calls) _calls.Add($"cancel {remoteName}");
            if (CancelDelay > TimeSpan.Zero) Thread.Sleep(CancelDelay);
            return CancelResult;
        }
    }

    private static FileServerTarget Target(string? userName = "svc-files", string password = "p@ss") =>
        new(1, $@"{Share}\docs", userName, userName is null ? null : "enc:" + password);

    private static (TestConnector Connector, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new TestConnector(time), time);
    }

    private static async Task<FileServerConnectException> Fails(TestConnector connector, FileServerTarget target, bool retryFailed = false) =>
        await Assert.ThrowsAsync<FileServerConnectException>(() => connector.EnsureConnectedAsync(target, retryFailed));

    [Fact]
    public async Task No_user_name_never_calls_native()
    {
        var (connector, _) = Create();

        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: true);

        Assert.True(connector.SupportsCredentials);
        Assert.Empty(connector.Calls);
    }

    [Fact]
    public async Task Same_fingerprint_connects_once()
    {
        var (connector, _) = Create();

        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);

        // 远程名只取根路径里的 \\主机\共享,子目录不算;密码是解开信封后的明文
        Assert.Equal([$@"add {Share} svc-files p@ss"], connector.Calls);
    }

    [Fact]
    public async Task Changed_password_reconnects()
    {
        var (connector, _) = Create();
        await connector.EnsureConnectedAsync(Target(password: "old"), retryFailed: false);

        await connector.EnsureConnectedAsync(Target(password: "new"), retryFailed: false);

        Assert.Equal([$"add {Share} svc-files old", $"cancel {Share}", $"add {Share} svc-files new"], connector.Calls);
    }

    [Fact]
    public async Task Open_files_keep_old_connection()
    {
        var (connector, _) = Create();
        await connector.EnsureConnectedAsync(Target(password: "old"), retryFailed: false);
        connector.CancelResult = 2401;   // ERROR_OPEN_FILES

        await connector.EnsureConnectedAsync(Target(password: "new"), retryFailed: false);

        Assert.Equal([$"add {Share} svc-files old", $"cancel {Share}"], connector.Calls);

        // 旧连接还在用,下次访问再试着换
        connector.CancelResult = 0;
        await connector.EnsureConnectedAsync(Target(password: "new"), retryFailed: false);
        Assert.Equal($"add {Share} svc-files new", connector.Calls[^1]);
    }

    [Fact]
    public async Task Credential_conflict_retries_once()
    {
        var (connector, _) = Create();
        connector.AddResults.Enqueue(1219);   // ERROR_SESSION_CREDENTIAL_CONFLICT
        connector.AddResults.Enqueue(0);

        await connector.EnsureConnectedAsync(Target(), retryFailed: false);

        Assert.Equal([$"add {Share} svc-files p@ss", $"cancel {Share}", $"add {Share} svc-files p@ss"], connector.Calls);
        // 连上之后同一套凭据不再调 native
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        Assert.Equal(3, connector.Calls.Count);
    }

    [Fact]
    public async Task Credential_conflict_twice_reports_conflict()
    {
        var (connector, _) = Create();
        connector.AddResults.Enqueue(1219);
        connector.AddResults.Enqueue(1219);

        var e = await Fails(connector, Target());

        Assert.Equal((FileAccessFailure.CredentialConflict, 1219), (e.Reason, e.Win32Code));
        Assert.Equal(2, connector.AddCount);
    }

    [Fact]
    public async Task Bad_credentials_are_not_retried()
    {
        var (connector, time) = Create();
        connector.AddResults.Enqueue(1326);   // ERROR_LOGON_FAILURE

        var first = await Fails(connector, Target());
        Assert.Equal((FileAccessFailure.BadCredentials, 1326), (first.Reason, first.Win32Code));

        // 同一套凭据:不再拿错密码去试,过多久都一样
        time.Advance(TimeSpan.FromHours(1));
        var again = await Fails(connector, Target());
        Assert.Equal(FileAccessFailure.BadCredentials, again.Reason);
        Assert.Equal(1, connector.AddCount);

        // 有人点了测试连接:重新试一次
        connector.AddResults.Enqueue(1326);
        await Fails(connector, Target(), retryFailed: true);
        Assert.Equal(2, connector.AddCount);

        // 改了密码:按新凭据连
        await connector.EnsureConnectedAsync(Target(password: "fixed"), retryFailed: false);
        Assert.Equal(3, connector.AddCount);
        Assert.Equal($"add {Share} svc-files fixed", connector.Calls[^1]);
    }

    [Theory]
    [InlineData(86, "badCredentials")]    // ERROR_INVALID_PASSWORD
    [InlineData(1330, "badCredentials")]  // ERROR_PASSWORD_EXPIRED
    [InlineData(1909, "accountLocked")]   // ERROR_ACCOUNT_LOCKED_OUT
    [InlineData(1331, "accountLocked")]   // ERROR_ACCOUNT_DISABLED
    public async Task Every_credential_failure_is_sticky(int code, string reason)
    {
        var (connector, time) = Create();
        connector.AddResults.Enqueue(code);
        await Fails(connector, Target());

        time.Advance(TimeSpan.FromMinutes(5));
        var again = await Fails(connector, Target());

        Assert.Equal(reason, again.Reason);
        Assert.Equal(1, connector.AddCount);
    }

    [Fact]
    public async Task Network_failure_retries_after_30_seconds()
    {
        var (connector, time) = Create();
        connector.AddResults.Enqueue(53);   // ERROR_BAD_NETPATH
        var first = await Fails(connector, Target());
        Assert.Equal(FileAccessFailure.NetworkPathNotFound, first.Reason);

        time.Advance(TimeSpan.FromSeconds(29));
        var cached = await Fails(connector, Target());
        Assert.Equal(FileAccessFailure.NetworkPathNotFound, cached.Reason);
        Assert.Equal(1, connector.AddCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        Assert.Equal(2, connector.AddCount);
    }

    [Fact]
    public async Task Slow_native_call_times_out()
    {
        var (connector, _) = Create();
        connector.Timeout = TimeSpan.FromMilliseconds(100);
        connector.AddDelay = TimeSpan.FromSeconds(1);

        var e = await Fails(connector, Target());

        Assert.Equal((FileAccessFailure.Timeout, 0), (e.Reason, e.Win32Code));
    }

    [Fact]
    public async Task Concurrent_calls_on_one_share_connect_once()
    {
        var (connector, _) = Create();
        connector.AddDelay = TimeSpan.FromMilliseconds(200);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => connector.EnsureConnectedAsync(Target(), retryFailed: false)));

        Assert.Equal(1, connector.AddCount);
    }

    [Fact]
    public async Task Root_without_share_is_rejected()
    {
        var (connector, _) = Create();

        var e = await Fails(connector, new FileServerTarget(1, @"D:\files", "svc-files", "enc:p@ss"));

        Assert.Equal(FileAccessFailure.Unknown, e.Reason);
        Assert.Empty(connector.Calls);
    }

    [Fact]
    public async Task Missing_password_is_sent_as_empty_string()
    {
        var (connector, _) = Create();

        await connector.EnsureConnectedAsync(new FileServerTarget(1, Share, "svc-files", null), retryFailed: false);

        // NULL 表示「用这个账号的默认密码」,空串才是「不用密码」
        Assert.Equal(1, connector.AddCount);
        Assert.Equal("", connector.LastPassword);
    }

    [Fact]
    public async Task Same_password_in_another_envelope_does_not_reconnect()
    {
        var (connector, _) = Create();
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);

        // 同一个密码重新加密出来的信封不同,凭据并没有变
        await connector.EnsureConnectedAsync(Target() with { PasswordProtected = "nonce2:p@ss" }, retryFailed: false);

        Assert.Equal([$"add {Share} svc-files p@ss"], connector.Calls);
    }

    [Fact]
    public async Task Caller_giving_up_still_records_bad_credentials()
    {
        var (connector, _) = Create();
        connector.AddResults.Enqueue(1326);
        connector.AddDelay = TimeSpan.FromMilliseconds(300);
        using var cts = new CancellationTokenSource();

        var first = connector.EnsureConnectedAsync(Target(), retryFailed: false, cts.Token);
        await connector.AddStarted.Task;
        await cts.CancelAsync();

        // 拿到锁之后调用方的令牌不再起作用:native 的结果照样记下,下一个请求不再拿错密码去试
        var e = await Assert.ThrowsAsync<FileServerConnectException>(() => first);
        Assert.Equal(FileAccessFailure.BadCredentials, e.Reason);
        var again = await Fails(connector, Target());
        Assert.Equal(FileAccessFailure.BadCredentials, again.Reason);
        Assert.Equal(1, connector.AddCount);
    }

    [Fact]
    public async Task Timeout_backs_off_for_30_seconds()
    {
        var (connector, time) = Create();
        connector.Timeout = TimeSpan.FromMilliseconds(100);
        connector.AddDelay = TimeSpan.FromSeconds(1);
        await Fails(connector, Target());

        time.Advance(TimeSpan.FromSeconds(29));
        var cached = await Fails(connector, Target());
        Assert.Equal((FileAccessFailure.Timeout, 0), (cached.Reason, cached.Win32Code));
        Assert.Equal(1, connector.AddCount);

        connector.AddDelay = TimeSpan.Zero;
        time.Advance(TimeSpan.FromSeconds(2));
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        Assert.Equal(2, connector.AddCount);
    }

    [Fact]
    public async Task Slow_cancel_times_out_and_backs_off()
    {
        var (connector, _) = Create();
        await connector.EnsureConnectedAsync(Target(password: "old"), retryFailed: false);
        connector.Timeout = TimeSpan.FromMilliseconds(100);
        connector.CancelDelay = TimeSpan.FromSeconds(1);

        var e = await Fails(connector, Target(password: "new"));
        Assert.Equal(FileAccessFailure.Timeout, e.Reason);

        await Fails(connector, Target(password: "new"));
        Assert.Equal((1, 1), (connector.AddCount, connector.CancelCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2250)]   // ERROR_NOT_CONNECTED:旧连接本来就不在了
    public async Task Clearing_user_name_disconnects_old_connection(int cancelResult)
    {
        var (connector, _) = Create();
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        connector.CancelResult = cancelResult;

        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: false);
        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: false);

        Assert.Equal([$"add {Share} svc-files p@ss", $"cancel {Share}"], connector.Calls);
        // 旧连接已经忘掉:再填回账号要重新连
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        Assert.Equal(2, connector.AddCount);
    }

    [Fact]
    public async Task Clearing_user_name_keeps_old_connection_while_files_open()
    {
        var (connector, _) = Create();
        await connector.EnsureConnectedAsync(Target(), retryFailed: false);
        connector.CancelResult = 2401;   // ERROR_OPEN_FILES

        // 还有文件开着:继续用旧连接,每次访问都再试着断
        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: false);
        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: false);
        Assert.Equal(2, connector.CancelCount);

        connector.CancelResult = 0;
        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: false);
        await connector.EnsureConnectedAsync(Target(userName: null), retryFailed: false);
        Assert.Equal(3, connector.CancelCount);
    }
}
