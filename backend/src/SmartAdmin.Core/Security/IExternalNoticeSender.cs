namespace SmartAdmin.Core;

/// <summary>
/// 把站内通知推到外部渠道(企业微信应用消息等)的发送方。由 <see cref="IExternalAuthProvider"/> 的实现顺带实现:
/// 渠道的凭据与 access_token 缓存只在 provider 里保留一份。
/// </summary>
public interface IExternalNoticeSender
{
    /// <summary>
    /// 向一批接收人发送一条消息。<paramref name="subjects"/> 是渠道里的接收人标识(与外部身份的 <c>Subject</c> 同一个值)。
    /// 渠道认为无法投递的接收人放进 <see cref="ExternalNoticeResult.Rejected"/>;整次调用失败抛 <see cref="ExternalNoticeException"/>。
    /// </summary>
    Task<ExternalNoticeResult> SendAsync(
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default);
}

/// <summary>要发送的消息:标题、纯文本正文和点击后打开的链接。</summary>
/// <param name="NoticeId">站内通知 Id(渠道侧不使用,供发送方记录与排查)</param>
/// <param name="Title">标题</param>
/// <param name="Text">纯文本正文(发送方负责转义与按渠道上限截断)</param>
/// <param name="Url">点击消息后打开的链接</param>
public sealed record ExternalNoticeMessage(long NoticeId, string Title, string Text, string Url);

/// <summary>一次发送的结果。</summary>
/// <param name="Rejected">渠道明确拒收的接收人标识(不存在、未授权等),调用方不应再重试这些人</param>
public sealed record ExternalNoticeResult(IReadOnlyList<string> Rejected);

/// <summary>
/// 整次发送失败。<see cref="Retryable"/> 为 <c>true</c> 表示稍后重试可能成功(渠道繁忙、网络抖动、5xx),
/// <c>false</c> 表示重试也没用(配置错误、凭据无效、接收人全部无效)。消息不含凭据与请求地址。
/// </summary>
/// <param name="message">面向管理员的失败说明</param>
/// <param name="retryable">是否值得重试</param>
/// <param name="providerCode">渠道返回的错误码(没有则为空)</param>
/// <param name="inner">内部异常</param>
public sealed class ExternalNoticeException(
    string message, bool retryable, string? providerCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>稍后重试是否可能成功。</summary>
    public bool Retryable { get; } = retryable;

    /// <summary>渠道返回的错误码,没有则为空。</summary>
    public string? ProviderCode { get; } = providerCode;
}
