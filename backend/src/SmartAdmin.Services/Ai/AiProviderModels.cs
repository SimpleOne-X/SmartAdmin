using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>AI 模型出参(挂在某个厂商下)。</summary>
public sealed record AiModelView(
    long Id, string Name, string DisplayName, bool Enabled, bool IsDefault,
    int? ContextWindow, decimal? InputPrice, decimal? OutputPrice);

/// <summary>
/// AI 厂商出参。<b>不含</b>明文 Key——只有 <see cref="ApiKeyHint"/>(尾四位)+ <see cref="HasApiKey"/>,
/// 明文只在保存那一次经过内存(见 <see cref="AiProviderService"/>)。
/// </summary>
public sealed record AiProviderView(
    long Id, string Code, string Name, string Preset, string Protocol, string BaseUrl,
    string AuthScheme, string? ApiKeyHint, bool HasApiKey, bool Enabled, int Sort,
    string? Remark, IReadOnlyList<AiModelView> Models);

/// <summary>
/// AI 模型入参(厂商新增/编辑时随模型清单整体提交)。<see cref="Id"/> 为 <c>null</c> 表示新增该行;
/// 有值表示更新该行;厂商原有模型行中不在入参列表里的按软删处理(见 <see cref="AiProviderService"/>)。
/// </summary>
public sealed record AiModelInput(
    long? Id, string Name, string DisplayName, bool Enabled, bool IsDefault,
    int? ContextWindow, decimal? InputPrice, decimal? OutputPrice);

/// <summary>AI 厂商新增入参。<see cref="ApiKey"/> 为空表示不配置 Key(<see cref="AuthScheme"/> != none 时不能启用)。</summary>
public sealed record AiProviderAddInput(
    string Code, string Name, string Preset, string BaseUrl, string AuthScheme,
    string? ApiKey, int Sort, string? Remark, IReadOnlyList<AiModelInput> Models);

/// <summary>
/// AI 厂商编辑入参。<b>没有</b> <c>Preset</c>/<c>Protocol</c> 字段——这两项创建后不可改,入参类型层面就不给改的机会。
/// <see cref="ApiKey"/> 为 <c>null</c>/空串表示不改动原有 Key;非空则重新加密覆盖。
/// </summary>
public sealed record AiProviderUpdateInput(
    string Code, string Name, string BaseUrl, string AuthScheme,
    string? ApiKey, int Sort, string? Remark, IReadOnlyList<AiModelInput> Models);

/// <summary>AI 厂商分页查询入参。</summary>
public sealed record AiProviderPageInput : PageInputBase
{
    /// <summary>关键字(模糊匹配 Code 或 Name,可选)</summary>
    public string? Keyword { get; init; }

    /// <summary>启用状态精确过滤(可选)</summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// 测试连接结果。<see cref="Ok"/> 为 <c>false</c> 时 <see cref="Error"/> 携带排障文案——
/// 这是给运维在「测试连接」按钮下直接展示的诊断信息,不是走 <see cref="ErrorCode"/> 信封的业务错误,
/// 所以不受"错误一律用数字 ErrorCode"的约束(<see cref="AiProviderService.TestAsync"/> 从不让异常冒泡出这个方法)。
/// </summary>
public sealed record AiTestResult(bool Ok, string? Model, int LatencyMs, AiUsage? Usage, string? Error);
