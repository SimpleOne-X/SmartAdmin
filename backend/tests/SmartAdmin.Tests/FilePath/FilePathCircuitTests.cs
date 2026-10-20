using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件服务器熔断:网络类失败后 30 秒内直接报「文件夹无法访问」、不再碰文件系统,30 秒后放一个请求试探;
/// 在途满了立即报「繁忙」;测试连接、在线状态这类探针不打开熔断,成功时关闭熔断。
/// </summary>
public class FilePathCircuitTests
{
    /// <summary>在途上限调小,免得测试要开 16 个并发。</summary>
    private sealed class SmallCircuit(TimeProvider time) : FilePathCircuit(time, NullLogger<FilePathCircuit>.Instance)
    {
        protected override int MaxInFlight => 2;
    }

    private static (FilePathCircuit Circuit, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (new SmallCircuit(time), time);
    }

    /// <summary>ERROR_NETNAME_DELETED:已经建立的 SMB 会话断了,Windows 常这样报。</summary>
    private static IOException NetworkGone() => new("网络名不再可用", unchecked((int)0x80070040));

    private static AdminException Rejected(Action action)
    {
        var e = Assert.Throws<AdminException>(action);
        Assert.Equal(ErrorCode.FilePathFolderUnavailable, e.Code);
        return e;
    }

    [Fact]
    public async Task Network_failure_opens_the_circuit_for_30_seconds_then_lets_one_request_through()
    {
        var (circuit, time) = Create();
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => circuit.RunAsync<int>(1, () => { calls++; throw NetworkGone(); }));

        // 打开期间同一台服务器直接报上次失败的原因;别的服务器不受影响
        Assert.Equal(FileAccessFailure.NetworkPathNotFound, Rejected(() => circuit.ThrowIfOpen(1)).Args!["reason"]);
        circuit.ThrowIfOpen(2);
        time.Advance(TimeSpan.FromSeconds(29));
        Rejected(() => circuit.ThrowIfOpen(1));

        // 满 30 秒放一个请求过去试探,它的结果出来之前其余请求照旧直接报
        time.Advance(TimeSpan.FromSeconds(1));
        circuit.ThrowIfOpen(1);
        Rejected(() => circuit.ThrowIfOpen(1));

        // 试探成功,熔断关闭
        Assert.Equal(7, await circuit.RunAsync(1, () => Task.FromResult(7)));
        circuit.ThrowIfOpen(1);
        circuit.ThrowIfOpen(1);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failed_trial_opens_another_30_seconds()
    {
        var (circuit, time) = Create();
        circuit.ReportFailure(1, new TimeoutException());
        time.Advance(TimeSpan.FromSeconds(30));
        circuit.ThrowIfOpen(1);

        await Assert.ThrowsAsync<IOException>(() => circuit.RunAsync<int>(1, () => throw NetworkGone()));

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(FileAccessFailure.NetworkPathNotFound, Rejected(() => circuit.ThrowIfOpen(1)).Args!["reason"]);
    }

    [Fact]
    public async Task Folder_unavailable_error_with_a_network_reason_opens_the_circuit()
    {
        var (circuit, _) = Create();

        await Assert.ThrowsAsync<AdminException>(() =>
            circuit.RunAsync<int>(1, () => throw FileAccessFailure.ToError(new TimeoutException())));

        Assert.Equal(FileAccessFailure.Timeout, Rejected(() => circuit.ThrowIfOpen(1)).Args!["reason"]);
    }

    [Fact]
    public async Task Other_failures_leave_the_circuit_closed()
    {
        var (circuit, _) = Create();

        // 拒绝访问、文件不存在、账号密码错误都说明服务器连得上,不该把整台服务器挡掉
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => circuit.RunAsync<int>(1, () => throw new UnauthorizedAccessException()));
        await Assert.ThrowsAsync<AdminException>(() => circuit.RunAsync<int>(1, () => throw new AdminException(ErrorCode.FilePathFileNotFound)));
        circuit.ReportFailure(1, new FileServerConnectException(FileAccessFailure.BadCredentials, 1326));
        await Assert.ThrowsAsync<OperationCanceledException>(() => circuit.RunAsync<int>(1, () => throw new OperationCanceledException()));

        circuit.ThrowIfOpen(1);
    }

    [Fact]
    public async Task Full_in_flight_rejects_immediately_without_running_the_access()
    {
        var (circuit, _) = Create();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = circuit.RunAsync(1, () => gate.Task);
        var second = circuit.RunAsync(1, () => gate.Task);

        var calls = 0;
        var busy = await Assert.ThrowsAsync<AdminException>(() => circuit.RunAsync(1, () => { calls++; return Task.FromResult(0); }));
        Assert.Equal((ErrorCode.FilePathFolderUnavailable, FileAccessFailure.Busy), (busy.Code, busy.Args!["reason"]));
        Assert.Equal(0, calls);
        // 繁忙不是服务器的问题,不打开熔断;别的服务器有自己的名额
        circuit.ThrowIfOpen(1);
        Assert.Equal(3, await circuit.RunAsync(2, () => Task.FromResult(3)));

        gate.SetResult(1);
        await Task.WhenAll(first, second);
        Assert.Equal(4, await circuit.RunAsync(1, () => Task.FromResult(4)));
    }

    [Fact]
    public async Task Probe_never_opens_the_circuit()
    {
        var (circuit, _) = Create();

        await Assert.ThrowsAsync<IOException>(() => circuit.RunAsync<int>(1, () => throw NetworkGone(), probe: true));

        circuit.ThrowIfOpen(1);
    }

    [Fact]
    public async Task Successful_probe_closes_the_circuit()
    {
        var (circuit, _) = Create();
        circuit.ReportFailure(1, NetworkGone());
        Rejected(() => circuit.ThrowIfOpen(1));

        await circuit.RunAsync(1, () => Task.FromResult(0), probe: true);

        circuit.ThrowIfOpen(1);
    }

    [Fact]
    public async Task Probe_is_subject_to_the_in_flight_limit()
    {
        var (circuit, _) = Create();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new[] { circuit.RunAsync(1, () => gate.Task), circuit.RunAsync(1, () => gate.Task, probe: true) };

        var busy = await Assert.ThrowsAsync<AdminException>(() => circuit.RunAsync(1, () => Task.FromResult(0), probe: true));

        Assert.Equal(FileAccessFailure.Busy, busy.Args!["reason"]);
        gate.SetResult(0);
        await Task.WhenAll(running);
    }
}
