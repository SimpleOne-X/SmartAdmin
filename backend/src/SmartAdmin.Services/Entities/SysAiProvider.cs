using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// AI 厂商配置——运维在「AI 模型」页维护;业务方通过 <see cref="Core.IAiChatClient"/> 按
/// <see cref="Code"/> 或全局默认模型间接引用,不直接依赖本表结构。
/// </summary>
[SugarTable("sys_ai_provider", TableDescription = "AI 厂商配置")]
[SugarIndex("idx_sys_ai_provider_code", nameof(Code), OrderByType.Asc, IsUnique = true)]
public class SysAiProvider : BaseEntity
{
    /// <summary>厂商编码(唯一;业务方 ProviderCode 用它引用)。新增时默认取预设 code,可改;保存后不可改</summary>
    [SugarColumn(Length = 64, ColumnDescription = "厂商编码(唯一;保存后不可改)")]
    public string Code { get; set; } = "";

    [SugarColumn(Length = 64, ColumnDescription = "显示名")]
    public string Name { get; set; } = "";

    /// <summary>预设 code(见 AiProviderPresets);保存后不可改</summary>
    [SugarColumn(Length = 32, ColumnDescription = "预设 code(保存后不可改)")]
    public string Preset { get; set; } = "";

    /// <summary>协议标识("openai" / "anthropic"),由预设带出</summary>
    [SugarColumn(Length = 32, ColumnDescription = "协议标识(openai / anthropic)")]
    public string Protocol { get; set; } = "";

    /// <summary>调用基址;可改,保存与调用前都过 SSRF 围栏</summary>
    [SugarColumn(Length = 512, ColumnDescription = "调用基址")]
    public string BaseUrl { get; set; } = "";

    /// <summary>鉴权方式:"bearer" / "x-api-key" / "api-key" / "none";由预设带出</summary>
    [SugarColumn(Length = 16, ColumnDescription = "鉴权方式(bearer / x-api-key / api-key / none)")]
    public string AuthScheme { get; set; } = "";

    /// <summary>API Key 的 ISecretProtector 加密信封;null = 未配置</summary>
    [SugarColumn(Length = 2048, IsNullable = true, ColumnDescription = "API Key 加密信封(ISecretProtector)")]
    public string? ApiKeyProtected { get; set; }

    /// <summary>API Key 脱敏尾四位,仅展示用</summary>
    [SugarColumn(Length = 16, IsNullable = true, ColumnDescription = "API Key 脱敏尾四位")]
    public string? ApiKeyHint { get; set; }

    /// <summary>是否启用;AuthScheme != none 且未配置 Key 时不能为 true(49004)</summary>
    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; }

    [SugarColumn(ColumnDescription = "卡片顺序(小在前)")]
    public int Sort { get; set; }

    [SugarColumn(Length = 512, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }
}
