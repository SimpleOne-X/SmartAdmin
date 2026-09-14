namespace SmartAdmin.Core;

/// <summary>
/// AI 对话网关统一入口。业务代码(消费者的 AI 审批等)注入本接口调用大模型,
/// 厂商选择、Key 解密、默认模型解析、Token 记账、错误映射全部由内核处理,调用方只管对话内容。
/// </summary>
public interface IAiChatClient
{
    /// <summary>非流式对话,一次性返回完整结果</summary>
    Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>流式对话,逐块产出增量内容;<see cref="AiChatChunk.Usage"/> 只在最后一块出现</summary>
    IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken cancellationToken = default);
}

/// <summary>一次对话请求。</summary>
public sealed record AiChatRequest
{
    /// <summary>业务场景标签,用量统计按它拆账;必填,空串抛 49031</summary>
    public required string Scene { get; init; }

    /// <summary>对话消息序列</summary>
    public required IReadOnlyList<AiChatMessage> Messages { get; init; }

    /// <summary>显式指定厂商编码;不填则按 <see cref="Model"/> 或全局默认模型解析</summary>
    public string? ProviderCode { get; init; }

    /// <summary>显式指定模型名;不填则用全局默认模型</summary>
    public string? Model { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary>透传给日志扩展的自定义元数据;一期不落库</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>一条对话消息</summary>
public sealed record AiChatMessage(AiChatRole Role, string Content)
{
    public static AiChatMessage System(string content) => new(AiChatRole.System, content);
    public static AiChatMessage User(string content) => new(AiChatRole.User, content);
    public static AiChatMessage Assistant(string content) => new(AiChatRole.Assistant, content);
}

/// <summary>消息角色</summary>
public enum AiChatRole
{
    System = 1,
    User = 2,
    Assistant = 3,
}

/// <summary>非流式对话结果</summary>
public sealed record AiChatResponse(
    string Content,
    string ProviderCode,
    string Model,
    AiUsage Usage,
    string? FinishReason,
    int LatencyMs,
    string? RequestId);

/// <summary>流式对话的一个数据块;<see cref="Usage"/> 只在最后一块出现</summary>
public sealed record AiChatChunk(string Delta, AiUsage? Usage = null, string? FinishReason = null);

/// <summary>Token 用量</summary>
public sealed record AiUsage(int InputTokens, int OutputTokens, AiUsageSource Source)
{
    /// <summary>输入 + 输出</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>用量数据的来源</summary>
public enum AiUsageSource
{
    /// <summary>上游明确回报了 usage 字段</summary>
    Reported = 1,

    /// <summary>上游未回报(典型场景:流式且厂商不支持 include_usage);不做估算,Token 记 0</summary>
    Missing = 2,
}
