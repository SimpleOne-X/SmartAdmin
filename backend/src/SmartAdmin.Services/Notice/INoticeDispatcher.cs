namespace SmartAdmin.Services;

/// <summary>
/// 把已发布的站内通知推到外部渠道(企业微信等)。默认实现 <see cref="NoticeDispatcher"/> 是后台服务,
/// 自带内存队列:发布请求只入队就返回,外部渠道慢或挂了都不影响发布。
/// </summary>
public interface INoticeDispatcher
{
    /// <summary>入队;不阻塞、不抛异常。队列满或没有任何渠道能发时静默丢弃(前者记 Error)。</summary>
    void Enqueue(NoticeDispatchItem item);

    /// <summary>
    /// 向 <paramref name="userId"/> 在 <paramref name="providerCode"/> 下绑定的账号发一条测试消息。
    /// 不入队、不重试;各种失败都折成 <see cref="ExternalNoticeTestView.Status"/> 返回,不抛异常。
    /// </summary>
    Task<ExternalNoticeTestView> SendTestAsync(string providerCode, long userId, CancellationToken cancellationToken = default);
}

/// <summary>入队时的通知快照。后台处理时通知可能已被改动或删除,以这份快照为准。</summary>
/// <param name="NoticeId">站内通知 Id</param>
/// <param name="Title">标题</param>
/// <param name="Content">正文(Markdown)</param>
/// <param name="ReceiverType">接收范围</param>
/// <param name="TargetIds">接收目标:角色 Id 或用户 Id,<see cref="ReceiverType.All"/> 时为空</param>
public sealed record NoticeDispatchItem(
    long NoticeId, string Title, string? Content, ReceiverType ReceiverType, IReadOnlyList<long> TargetIds);

/// <summary>测试消息的结果。</summary>
/// <param name="Status">ok / rejected / failed / notBound / noBaseUrl / unsupported</param>
/// <param name="ProviderCode">渠道返回的错误码(只有 failed 时可能有)</param>
/// <param name="Detail">给管理员看的说明:failed 是失败原因,rejected 是被拒收的接收人</param>
public sealed record ExternalNoticeTestView(string Status, string? ProviderCode = null, string? Detail = null);
