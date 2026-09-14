namespace SmartAdmin.Services;

/// <summary>AI 厂商预置里的一个起步模型</summary>
public sealed record AiModelPreset(string Name, string DisplayName, int? ContextWindow = null);

/// <summary>
/// AI 厂商预置项:协议 / Base URL / 鉴权方式由预设带出,运维只填 Key(见 docs/plans/ai-management.md §6)。
/// <see cref="SupportsJsonSchema"/> 是否支持 OpenAI 的 json_schema 严格结构化输出(<c>response_format.type == "json_schema"</c>);
/// 只对 Protocol == "openai" 的厂商有意义,默认 true。标记为 false 的厂商收到该字段时不会报错,只会静默忽略约束按自由文本
/// 作答——AiChatClient 据此在请求携带 ResponseSchema 时提前拒绝,不把一个对方读不懂的字段透传过去。
/// </summary>
public sealed record AiProviderPreset(
    string Code,
    string Name,
    string Protocol,
    string BaseUrl,
    string AuthScheme,
    IReadOnlyList<AiModelPreset> Models,
    bool SupportsJsonSchema = true);

/// <summary>
/// 厂商预置静态表。模型名变动快,这里只是起步清单,界面上可增删。
/// 模型名核对时间 2026-09-13(逐家核对官方文档;部分厂商同期换代频繁,新增厂商或大版本更新时请重新核对再改本表)。
/// </summary>
public static class AiProviderPresets
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "openai", "https://api.openai.com/v1", "bearer",
        [
            new("gpt-6-astra", "GPT-6 Astra"),
            new("gpt-5.6-terra", "GPT-5.6 Terra"),
        ]),

        // 模型名 = 部署名,清单留空。api-key 头是最简接入;生产建议改用 Entra ID Bearer token(界面仍先给 api-key 头)。
        new("azure-openai", "Azure OpenAI", "openai", "https://{resource}.openai.azure.com/openai/v1", "api-key", []),

        new("anthropic", "Anthropic", "anthropic", "https://api.anthropic.com", "x-api-key",
        [
            new("claude-opus-5", "Claude Opus 5"),
            new("claude-sonnet-5", "Claude Sonnet 5"),
            new("claude-haiku-4-5-20251001", "Claude Haiku 4.5"),
        ]),

        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com/v1", "bearer",
        [
            new("deepseek-flash", "DeepSeek Flash"),
            new("deepseek-v4-pro", "DeepSeek V4 Pro"),
        ]),

        new("qwen", "通义千问", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1", "bearer",
        [
            new("qwen-plus", "Qwen Plus"),
            new("qwen-turbo", "Qwen Turbo"),
            new("qwen3.8-max", "Qwen3.8 Max"),
        ]),

        // 官方 response_format.type 枚举只有 text / json_object,没有 json_schema;传了也不报错,只是不生效
        new("zhipu", "智谱 GLM", "openai", "https://open.bigmodel.cn/api/paas/v4", "bearer",
        [
            new("glm-4.6", "GLM-4.6"),
            new("glm-4.7-flash", "GLM-4.7 Flash"),
        ], SupportsJsonSchema: false),

        new("moonshot", "Kimi", "openai", "https://api.moonshot.cn/v1", "bearer",
        [
            new("kimi-k2.6", "Kimi K2.6"),
            new("kimi-k3", "Kimi K3"),
        ]),

        // 模型名可直接调用(如下两个示例),或改填控制台创建的接入点 ep-xxx(仅支持 API Key 鉴权)。
        new("doubao", "豆包(火山方舟)", "openai", "https://ark.cn-beijing.volces.com/api/v3", "bearer",
        [
            new("doubao-seed-2-0-pro-260215", "豆包 Seed 2.0 Pro"),
            new("doubao-seed-2-0-lite-260215", "豆包 Seed 2.0 Lite"),
        ]),

        // 官方 OpenAI 兼容层文档路径末尾带斜杠;该兼容层文档仍标注 beta。
        new("gemini", "Google Gemini", "openai", "https://generativelanguage.googleapis.com/v1beta/openai/", "bearer",
        [
            new("gemini-3.8-flash", "Gemini 3.8 Flash"),
            new("gemini-3.5-flash-lite", "Gemini 3.5 Flash Lite"),
        ]),

        // 服务端不校验 Key;OpenAI 客户端 SDK 仍要求非空 api_key,调用时传占位字符串即可。
        new("ollama", "Ollama(本地)", "openai", "http://localhost:11434/v1", "none", []),

        new("custom", "自定义(OpenAI 兼容)", "openai", "", "bearer", []),
    ];

    /// <summary>按 code 查预置;大小写不敏感</summary>
    public static AiProviderPreset? Find(string code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}
