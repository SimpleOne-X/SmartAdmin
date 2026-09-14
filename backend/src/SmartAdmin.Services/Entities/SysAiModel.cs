using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>AI 模型——挂在某个厂商下;<see cref="IsDefault"/> 全库只允许一个 true(全局默认模型)。</summary>
[SugarTable("sys_ai_model", TableDescription = "AI 模型")]
[SugarIndex("idx_sys_ai_model_provider", nameof(ProviderId), OrderByType.Asc)]
public class SysAiModel : BaseEntity
{
    /// <summary>所属厂商 Id;厂商删除时级联软删</summary>
    [SugarColumn(ColumnDescription = "所属厂商 Id")]
    public long ProviderId { get; set; }

    /// <summary>调用用的模型名(如 deepseek-chat);同厂商内唯一(服务层校验,含软删行)</summary>
    [SugarColumn(Length = 128, ColumnDescription = "调用用的模型名")]
    public string Name { get; set; } = "";

    [SugarColumn(Length = 128, ColumnDescription = "展示名")]
    public string DisplayName { get; set; } = "";

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; } = true;

    /// <summary>是否全局默认模型;全库只允许一个 true,设默认时服务层先清掉其它行</summary>
    [SugarColumn(ColumnDescription = "是否全局默认模型(全库唯一)")]
    public bool IsDefault { get; set; }

    /// <summary>上下文窗口(Token 数),预设带出,展示用</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "上下文窗口(Token 数)")]
    public int? ContextWindow { get; set; }

    /// <summary>每百万 Token 输入单价;一期只建列不做界面</summary>
    [SugarColumn(IsNullable = true, Length = 18, DecimalDigits = 4, ColumnDescription = "每百万 Token 输入单价")]
    public decimal? InputPrice { get; set; }

    /// <summary>每百万 Token 输出单价;一期只建列不做界面</summary>
    [SugarColumn(IsNullable = true, Length = 18, DecimalDigits = 4, ColumnDescription = "每百万 Token 输出单价")]
    public decimal? OutputPrice { get; set; }
}
