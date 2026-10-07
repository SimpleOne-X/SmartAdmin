using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// AI 调用记录——只增、物理删(对齐 SysJobLog),按 <see cref="AiConfigKeys.KEY_USAGE_RETENTION_DAYS"/> 定期清理。
/// <b>不记 prompt 与回复正文</b>:审批等业务内容属于业务数据,不进内核日志。
/// </summary>
[SugarTable("sys_ai_usage_log", TableDescription = "AI 调用记录")]
[SugarIndex("idx_sys_ai_usage_log_time", nameof(AuditEntity.CreateTime), OrderByType.Desc)]
[SugarIndex("idx_sys_ai_usage_log_scene", nameof(Scene), OrderByType.Asc, nameof(AuditEntity.CreateTime), OrderByType.Desc)]
[SugarIndex("idx_sys_ai_usage_log_provider", nameof(ProviderId), OrderByType.Asc, nameof(AuditEntity.CreateTime), OrderByType.Desc)]
[SugarIndex("idx_sys_ai_usage_log_user", nameof(UserId), OrderByType.Asc, nameof(AuditEntity.CreateTime), OrderByType.Desc)]
public class SysAiUsageLog : AuditEntity
{
    /// <summary>厂商 Id</summary>
    [SugarColumn(ColumnDescription = "厂商 Id")]
    public long ProviderId { get; set; }

    /// <summary>厂商编码快照(冗余;厂商删了也能读)</summary>
    [SugarColumn(Length = 64, ColumnDescription = "厂商编码快照")]
    public string ProviderCode { get; set; } = "";

    [SugarColumn(Length = 128, ColumnDescription = "模型名")]
    public string Model { get; set; } = "";

    /// <summary>业务场景标签,如 approval.summary;测试连接固定 system.test</summary>
    [SugarColumn(Length = 64, ColumnDescription = "业务场景标签")]
    public string Scene { get; set; } = "";

    /// <summary>发起用户;后台任务里可能为空</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "发起用户 Id")]
    public long? UserId { get; set; }

    [SugarColumn(ColumnDescription = "输入 Token 数")]
    public int InputTokens { get; set; }

    [SugarColumn(ColumnDescription = "输出 Token 数")]
    public int OutputTokens { get; set; }

    [SugarColumn(ColumnDescription = "总 Token 数")]
    public int TotalTokens { get; set; }

    /// <summary>用量来源:1=上游回报,2=上游未回报(流式且厂商不支持 include_usage)</summary>
    [SugarColumn(ColumnDescription = "用量来源:1=上游回报/2=上游未回报")]
    public int UsageSource { get; set; }

    [SugarColumn(ColumnDescription = "耗时(毫秒)")]
    public int LatencyMs { get; set; }

    [SugarColumn(ColumnDescription = "是否成功")]
    public bool Success { get; set; }

    /// <summary>失败时的内核错误码</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "失败时的内核错误码")]
    public int? ErrorCode { get; set; }

    /// <summary>上游错误摘要,截断;不含请求内容</summary>
    [SugarColumn(Length = 512, IsNullable = true, ColumnDescription = "上游错误摘要(截断,不含请求内容)")]
    public string? ErrorMessage { get; set; }

    [SugarColumn(ColumnDescription = "是否流式调用")]
    public bool Streamed { get; set; }

    /// <summary>上游返回的请求 id,排障用</summary>
    [SugarColumn(Length = 64, IsNullable = true, ColumnDescription = "上游请求 id(排障用)")]
    public string? RequestId { get; set; }
}
