using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFilePathCircuit"/> 默认实现:状态只在本进程内存里,多副本各管各的。
/// 打开时长和在途上限是 <c>protected virtual</c> 属性,要调就子类化覆写,不设配置项。
/// </summary>
public class FilePathCircuit(TimeProvider time, ILogger<FilePathCircuit> logger) : IFilePathCircuit
{
    private readonly ConcurrentDictionary<long, ServerState> _servers = new();

    /// <summary>每台服务器同时在做的文件系统操作上限。</summary>
    protected virtual int MaxInFlight => 16;

    /// <summary>网络类失败后熔断打开多久。</summary>
    protected virtual TimeSpan OpenDuration => TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public virtual void ThrowIfOpen(long serverId)
    {
        if (!_servers.TryGetValue(serverId, out var state)) return;
        lock (state)
        {
            if (state.OpenUntil is { } until && time.GetUtcNow() < until) throw Unavailable(state.Reason!);
        }
    }

    /// <inheritdoc />
    public virtual async Task<T> RunAsync<T>(long serverId, Func<Task<T>> access, bool probe = false)
    {
        var state = State(serverId);
        if (!state.Slots.Wait(0)) throw Unavailable(FileAccessFailure.Busy);
        try
        {
            if (!probe) Admit(state);
            T result;
            try
            {
                result = await access();
            }
            catch (Exception e)
            {
                if (!probe) ReportFailure(serverId, e);
                // 文件不存在、撞名、拒绝访问这类失败说明服务器连得上,试探算成功;取消说明不了什么
                if (e is not OperationCanceledException && NetworkReason(e) is null) Close(serverId, state);
                throw;
            }

            Close(serverId, state);
            return result;
        }
        finally
        {
            state.Slots.Release();
        }
    }

    /// <inheritdoc />
    public virtual void ReportFailure(long serverId, Exception failure)
    {
        if (NetworkReason(failure) is not { } reason) return;

        var state = State(serverId);
        bool opened;
        lock (state)
        {
            opened = state.OpenUntil is null;
            state.OpenUntil = time.GetUtcNow() + OpenDuration;
            state.Reason = reason;
        }

        if (opened)
            logger.LogWarning(failure, "文件服务器 {ServerId} 出现网络类失败({Reason}),{Seconds} 秒内对它的上传、下载、删除直接报文件夹无法访问",
                serverId, reason, OpenDuration.TotalSeconds);
    }

    /// <summary>
    /// 失败里能读出的网络类原因;不是网络类返回 null。认原始的 IO 异常,也认已经转成 <see cref="ErrorCode.FilePathFolderUnavailable"/> 的业务异常。
    /// 取消、拒绝访问、账号密码错误、文件不存在等都不算:前一种不是服务器的问题,后几种说明服务器连得上。
    /// </summary>
    protected virtual string? NetworkReason(Exception failure)
    {
        if (failure is OperationCanceledException) return null;
        var reason = FileAccessFailure.FromException(failure);
        return reason is FileAccessFailure.NetworkPathNotFound or FileAccessFailure.Timeout ? reason : null;
    }

    /// <summary>
    /// 熔断打开且没满时长:直接报。满了:这个请求去试探,窗口往后推一段,它的结果出来之前其余请求照旧被挡。
    /// 试探放在真正碰文件系统的这一步而不是 <see cref="ThrowIfOpen"/>:只写暂存的分片这类请求不碰服务器,不能占掉试探名额。
    /// </summary>
    private void Admit(ServerState state)
    {
        lock (state)
        {
            if (state.OpenUntil is not { } until) return;
            var now = time.GetUtcNow();
            if (now < until) throw Unavailable(state.Reason!);
            state.OpenUntil = now + OpenDuration;
        }
    }

    private void Close(long serverId, ServerState state)
    {
        lock (state)
        {
            if (state.OpenUntil is null) return;
            state.OpenUntil = null;
            state.Reason = null;
        }
        logger.LogInformation("文件服务器 {ServerId} 访问恢复,熔断关闭", serverId);
    }

    private ServerState State(long serverId) => _servers.GetOrAdd(serverId, _ => new ServerState(MaxInFlight));

    private static AdminException Unavailable(string reason) =>
        new(ErrorCode.FilePathFolderUnavailable, new Dictionary<string, object?> { ["reason"] = reason });

    /// <summary>一台服务器的熔断状态;<see cref="OpenUntil"/> 和 <see cref="Reason"/> 只在锁住这个对象时读写。</summary>
    private sealed class ServerState(int maxInFlight)
    {
        public SemaphoreSlim Slots { get; } = new(maxInFlight, maxInFlight);

        /// <summary>熔断打开到什么时候;null 是关闭。</summary>
        public DateTimeOffset? OpenUntil { get; set; }

        public string? Reason { get; set; }
    }
}
