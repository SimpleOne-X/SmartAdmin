using System.Text.Json;

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

    /// <summary>约束模型输出必须匹配的 JSON Schema;不填是现在的自由文本行为。协议适配器用它拼各自厂商的结构化输出字段
    /// (OpenAI 兼容:response_format.json_schema;Anthropic:output_config.format);厂商/模型不支持时上游报错,
    /// 按现有 49020 系列错误码映射,网关不做静默降级或估算解析。</summary>
    public JsonElement? ResponseSchema { get; init; }
}

/// <summary>一条对话消息</summary>
public sealed record AiChatMessage(AiChatRole Role, string Content)
{
    /// <summary>多段内容(文本/图片混排);不填时用 <see cref="Content"/> 走纯文本,填了 Parts 时 Content 被忽略。
    /// System 角色不支持多段内容(两个协议的 system 都只接受纯文本),填了抛 49032。</summary>
    public IReadOnlyList<AiChatContentPart>? Parts { get; init; }

    public static AiChatMessage System(string content) => new(AiChatRole.System, content);
    public static AiChatMessage User(string content) => new(AiChatRole.User, content);

    /// <summary>多模态消息(文本/图片混排)</summary>
    public static AiChatMessage User(IReadOnlyList<AiChatContentPart> parts) => new(AiChatRole.User, string.Empty) { Parts = parts };

    public static AiChatMessage Assistant(string content) => new(AiChatRole.Assistant, content);
}

/// <summary>消息内容段(封闭继承——只有 Text/Image 两种,私有构造禁止外部再派生)</summary>
public abstract record AiChatContentPart
{
    private AiChatContentPart()
    {
    }

    /// <summary>文本段</summary>
    public sealed record Text(string Content) : AiChatContentPart;

    /// <summary>图片段</summary>
    public sealed record Image(AiImageSource Source) : AiChatContentPart;
}

/// <summary>图片来源(封闭继承)</summary>
public abstract record AiImageSource
{
    private AiImageSource()
    {
    }

    /// <summary>base64 编码的图片数据;MediaType 如 "image/jpeg"</summary>
    public sealed record Base64(string MediaType, string Data) : AiImageSource;

    /// <summary>图片的 URL 引用</summary>
    public sealed record Url(string Value) : AiImageSource;
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
