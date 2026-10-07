namespace SmartAdmin.Services;

/// <summary>
/// AI 网关命名 HttpClient 的登记入口。<c>Timeout</c> 故意设为 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>——
/// 超时由网关(AiChatClient)的 CancellationTokenSource 统一控制,流式调用需要按整段计时而非逐次请求计时。
/// <para>主处理器挂了 <see cref="HttpFence.CreateHandler"/>(<c>ServicesSetup.AddSmartAdminServices</c> 里
/// <c>ConfigurePrimaryHttpMessageHandler</c>),SSRF 围栏配置读 <c>AdminAiOptions.Http</c>。</para>
/// </summary>
public static class AiHttpClient
{
    /// <summary>命名 HttpClient 常量,<c>IHttpClientFactory.CreateClient(AiHttpClient.Name)</c> 取用</summary>
    public const string Name = "SmartAdmin.Ai";
}
