namespace SmartAdmin.Auth.WeCom;

/// <summary>
/// 企业微信 provider 的运行时参数。连接配置由管理员在「系统配置 → 登录方式」填写,<see cref="CorpSecret"/> 加密保存到数据库,
/// 由 <see cref="WeComAuthProviderType"/> 组装成本类;也可在代码里显式构造,经 <c>AddSmartAdminWeComAuth(options)</c> 注册。
/// </summary>
public class WeComAuthOptions
{
    /// <summary>provider 唯一码(登录按钮 / 回调路由 / 运营配置键都用它);库里配置的企业微信固定为 <c>wecom</c></summary>
    public string Code { get; set; } = "wecom";

    /// <summary>展示名(登录页按钮文案兜底)</summary>
    public string DisplayName { get; set; } = "企业微信";

    /// <summary>图标(iconify 名或图片 URL,可空)</summary>
    public string? Icon { get; set; } = "ph:qr-code-duotone";

    /// <summary>企业 Id(corpid)</summary>
    public string CorpId { get; set; } = "";

    /// <summary>应用 AgentId(网页/扫码登录归属的自建应用)</summary>
    public string AgentId { get; set; } = "";

    /// <summary>应用 Secret(corpsecret;换取 access_token 用)</summary>
    public string CorpSecret { get; set; } = "";
}
