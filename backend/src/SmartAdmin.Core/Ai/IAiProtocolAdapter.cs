namespace SmartAdmin.Core;

/// <summary>
/// AI 协议适配器扩展点(多实现,<c>TryAddEnumerable</c>)。按协议而非厂商实现——一期内置 openai 兼容与
/// anthropic 两种协议即覆盖十家预置厂商;消费者要接私有协议时追加自己的实现即可,不需要改内核。
/// </summary>
public interface IAiProtocolAdapter
{
    /// <summary>本适配器处理的协议标识,如 "openai" / "anthropic",与 <c>sys_ai_provider.Protocol</c> 对应</summary>
    string Protocol { get; }

    Task<AiChatResponse> ChatAsync(AiEndpoint endpoint, AiChatRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<AiChatChunk> StreamAsync(AiEndpoint endpoint, AiChatRequest request, CancellationToken cancellationToken);
}

/// <summary>网关解析好的调用端点(厂商配置 + 目标模型),已解密 Key 只在内存经过一次,不落日志。
/// <see cref="SupportsJsonSchema"/> 是厂商预设是否支持 OpenAI 的 json_schema 严格结构化输出,来自
/// <c>AiProviderPreset.SupportsJsonSchema</c>;未知预设(如消费者自定义 Preset)按 true 处理,不影响现有行为</summary>
public sealed record AiEndpoint(
    string ProviderCode,
    string BaseUrl,
    string? ApiKey,
    string AuthScheme,
    string Model,
    bool SupportsJsonSchema = true);
