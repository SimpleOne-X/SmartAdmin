namespace SmartAdmin.Core;

/// <summary>
/// AI 网关配置(对应 <c>SmartAdmin:Ai</c> 节)。厂商、Key、模型走后台「AI 管理」维护(落库),
/// 这里只放部署级参数——运维改配置文件、不进数据库。
/// </summary>
public class AdminAiOptions
{
    /// <summary>单次调用超时(秒),默认 120;流式按整段计(从建连到枚举结束,不是逐块计时)</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>HTTP 出站围栏(SSRF 防护)。批次 4 从 <c>JobHttpFence</c> 抽出公共 <c>HttpFence</c> 后接入 AI 网关的 HttpClient</summary>
    public HttpFenceOptions Http { get; set; } = new();
}

/// <summary>
/// HTTP 出站围栏通用配置(SSRF 防护)。批次 4 从 <c>JobHttpFence</c> 抽公共 <c>HttpFence</c> 时,
/// 定时任务与 AI 网关共用本类型;<c>AdminJobsHttpOptions</c> 暂不改动,<c>JobHttpFence</c> 保留原签名转发。
/// </summary>
public class HttpFenceOptions
{
    /// <summary>目标主机白名单;null/空 = 不限(配了则 Base URL 主机必须命中)</summary>
    public string[]? AllowedHosts { get; set; }

    /// <summary>目标 IP 黑名单(CIDR),默认与 <see cref="AdminJobsHttpOptions.BlockedCidrs"/> 一致(云元数据段 + 回环)</summary>
    public string[] BlockedCidrs { get; set; } = ["169.254.0.0/16", "fd00:ec2::/32", "fe80::/10", "127.0.0.0/8", "::1/128"];

    /// <summary>
    /// 显式代理地址;空 = 不用代理。配了代理后不再对目标 IP 做解析后复检(DNS rebinding 防护对代理链天然失效),
    /// 只做 URL 级校验——这是运维的主动选择、风险自担,界面不开放本项,只能在 appsettings 配。
    /// </summary>
    public string? Proxy { get; set; }
}
