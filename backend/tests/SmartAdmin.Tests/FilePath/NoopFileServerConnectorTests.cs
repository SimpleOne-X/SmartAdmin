using Microsoft.Extensions.Logging;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>不支持账号连接的平台上的默认连接器:不声明支持账号、连接什么都不做、填了账号的服务器每个 Id 只警告一次。</summary>
public class NoopFileServerConnectorTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Connects_without_doing_anything_and_does_not_support_credentials()
    {
        var logger = new CapturingLogger<NoopFileServerConnector>();
        var connector = new NoopFileServerConnector(logger);

        Assert.False(connector.SupportsCredentials);
        await connector.EnsureConnectedAsync(new FileServerTarget(1, "/mnt/nas01/files", null, null), retryFailed: false);
        await connector.EnsureConnectedAsync(new FileServerTarget(2, "/mnt/nas01/scan", "", null), retryFailed: true);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Warns_once_per_server_that_has_an_account()
    {
        var logger = new CapturingLogger<NoopFileServerConnector>();
        var connector = new NoopFileServerConnector(logger);
        var files = new FileServerTarget(1, "/mnt/nas01/files", "svc-files", "envelope");
        var scan = new FileServerTarget(2, "/mnt/nas01/scan", "svc-files", "envelope");

        // 每次上传下载都会调到这里:同一台服务器只记一次,换一台再记一次
        for (var i = 0; i < 3; i++)
        {
            await connector.EnsureConnectedAsync(files, retryFailed: false);
            await connector.EnsureConnectedAsync(scan, retryFailed: i == 0);
        }

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.Contains(logger.Entries, e => e.Message.Contains("文件服务器 1 "));
        Assert.Contains(logger.Entries, e => e.Message.Contains("文件服务器 2 "));
        // 警告里只有服务器 Id,不带账号和密码信封
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("svc-files") || e.Message.Contains("envelope"));
    }
}
