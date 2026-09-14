namespace SmartAdmin.Services;

/// <summary>AI 网关相关的配置中心键(GroupCode=ai)</summary>
public static class AiConfigKeys
{
    public const string GROUP = "ai";

    /// <summary>AI 用量记录保留天数;≤0 不清理(AiUsageLogCleanupJob 读取,批次 4 接入)</summary>
    public const string KEY_USAGE_RETENTION_DAYS = "sys.ai.usageRetentionDays";
}
