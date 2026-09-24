namespace SmartAdmin.Auth.DingTalk;

/// <summary>
/// 钉钉 provider 的运行时参数。连接配置由管理员在「系统配置 → 登录方式」填写,<see cref="AppSecret"/> 加密保存到数据库,
/// 由 <see cref="DingTalkAuthProviderType"/> 组装成本类;也可在代码里显式构造,经 <c>AddSmartAdminDingTalkAuth(options)</c> 注册。
/// </summary>
public class DingTalkAuthOptions
{
    /// <summary>provider 唯一码(登录按钮 / 回调路由 / 运营配置键都用它);库里配置的钉钉固定为 <c>dingtalk</c></summary>
    public string Code { get; set; } = "dingtalk";

    /// <summary>展示名(登录页按钮文案兜底)</summary>
    public string DisplayName { get; set; } = "钉钉";

    /// <summary>图标(iconify 名或图片 URL,可空)</summary>
    public string? Icon { get; set; } = "ph:qr-code-duotone";

    /// <summary>应用 AppKey(扫码登录应用的 client_id)</summary>
    public string AppKey { get; set; } = "";

    /// <summary>应用 AppSecret(client_secret;换取用户 access_token 用)</summary>
    public string AppSecret { get; set; } = "";
}
