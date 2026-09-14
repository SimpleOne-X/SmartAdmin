namespace SmartAdmin.Services;

/// <summary>
/// AI 网关命名 HttpClient 的登记入口。<c>Timeout</c> 故意设为 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>——
/// 超时由网关(AiChatClient)的 CancellationTokenSource 统一控制,流式调用需要按整段计时而非逐次请求计时。
/// <para>批次 4 会给这个命名客户端接上 SSRF 围栏 handler(<c>HttpFence.CreateHandler</c>);批次 2 先只登记名字与超时。</para>
/// </summary>
public static class AiHttpClient
{
    /// <summary>命名 HttpClient 常量,<c>IHttpClientFactory.CreateClient(AiHttpClient.Name)</c> 取用</summary>
    public const string Name = "SmartAdmin.Ai";
}
