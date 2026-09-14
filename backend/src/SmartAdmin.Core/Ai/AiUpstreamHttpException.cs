namespace SmartAdmin.Core;

/// <summary>
/// 协议适配器收到上游非 2xx 响应时抛出;网关(AiChatClient)按状态码映射到具体 ErrorCode(49020–49024)。
/// 消费者自写的 <see cref="IAiProtocolAdapter"/> 也可以抛它以获得同等细分映射,不抛则网关按通用 HttpRequestException 兜底到 49020。
/// </summary>
public sealed class AiUpstreamHttpException(int? statusCode, string bodySnippet)
    : Exception($"AI 上游返回 {(statusCode.HasValue ? statusCode.Value.ToString() : "未知状态码")}: {bodySnippet}")
{
    /// <summary>上游 HTTP 状态码;网络层异常(连接失败等)可能拿不到,为 null</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>响应体前 512 字截断,不含请求内容</summary>
    public string BodySnippet { get; } = bodySnippet;
}
