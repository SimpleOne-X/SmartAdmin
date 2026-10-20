using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 连接器替身:记录每次调用,并按需模拟失败或卡死。经 <see cref="FilePathTestEnv.Create"/> 的服务覆盖换掉内置连接器。
/// </summary>
public sealed class FakeFileServerConnector : IFileServerConnector
{
    private readonly List<(FileServerTarget Target, bool RetryFailed)> _calls = [];

    public bool SupportsCredentials { get; set; }

    /// <summary>不为空时,每次调用在记录之后抛出它。</summary>
    public Exception? Fail { get; set; }

    /// <summary>为真时,每次调用在记录之后永不返回(模拟连不上又不报错的主机)。</summary>
    public bool Hang { get; set; }

    /// <summary>到目前为止的调用快照(并发调用下也安全)。</summary>
    public IReadOnlyList<(FileServerTarget Target, bool RetryFailed)> Calls
    {
        get { lock (_calls) return [.. _calls]; }
    }

    /// <summary>最近一次调用收到的取消令牌(看调用方到时限后有没有取消它)。</summary>
    public CancellationToken LastToken { get; private set; }

    public async Task EnsureConnectedAsync(FileServerTarget target, bool retryFailed, CancellationToken ct = default)
    {
        lock (_calls)
        {
            _calls.Add((target, retryFailed));
            LastToken = ct;
        }
        if (Fail is not null) throw Fail;
        if (Hang) await new TaskCompletionSource().Task;
    }
}
