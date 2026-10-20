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

    /// <summary>信封就是 <c>enc:</c> 加明文,断言能看出连接器把解开的明文交给了 native 调用。</summary>
    private sealed class PrefixProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string envelope) => envelope["enc:".Length..];
    }

    private sealed class TestConnector(FakeTimeProvider time)
        : WindowsFileServerConnector(new PrefixProtector(), NullLogger<WindowsFileServerConnector>.Instance, time)
    {
        private readonly List<string> _calls = [];

        /// <summary>每次 <see cref="AddConnection"/> 依次取一个返回码,取完之后返回 0。</summary>
        public Queue<int> AddResults { get; } = new();

        public int CancelResult { get; set; }

        public TimeSpan AddDelay { get; set; }

        public TimeSpan? Timeout { get; set; }

        public IReadOnlyList<string> Calls
        {
            get { lock (_calls) return [.. _calls]; }
        }

        public int AddCount => Calls.Count(c => c.StartsWith("add ", StringComparison.Ordinal));

        protected override TimeSpan ConnectTimeout => Timeout ?? base.ConnectTimeout;

        protected override int AddConnection(string remoteName, string? userName, string? password)
        {
            int rc;
            lock (_calls)
            {
                _calls.Add($"add {remoteName} {userName} {password}");
                rc = AddResults.Count > 0 ? AddResults.Dequeue() : 0;
            }
            if (AddDelay > TimeSpan.Zero) Thread.Sleep(AddDelay);
            return rc;
        }

        protected override int CancelConnection(string remoteName)
        {
            lock (_calls) _calls.Add($"cancel {remoteName}");
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
}
