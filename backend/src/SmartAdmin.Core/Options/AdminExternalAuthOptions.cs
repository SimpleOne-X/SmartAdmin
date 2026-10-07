namespace SmartAdmin.Core;

/// <summary>
/// 外部登录 / SSO 配置(对应 <c>SmartAdmin:ExternalAuth</c> 节):只有回调基址与前端结果页两项部署级参数。
/// <para>各登录方式的连接与密钥由管理员在「系统配置 → 登录方式」填写,加密入库(<c>sys_external_auth_provider</c>),
/// 运行时由 <see cref="IExternalAuthProviderType"/>(类型描述)与 <see cref="IExternalAuthProviderRegistry"/>(注册表)装配;
/// 启用开关、未绑定策略、开户默认角色/机构等运营项走 <c>sys_config</c>
/// 键 <c>sys.externalauth.{code}.{enabled|provisioning|linkByAccount|defaultRoleIds|defaultOrgId}</c>(运行时可改、无需重启)。</para>
/// <para><see cref="CallbackBaseUrl"/> 留在配置里:它不是机密,而且启动时的格式校验能在部署阶段就拦下填错的值。</para>
/// </summary>
public class AdminExternalAuthOptions
{
    /// <summary>
    /// 后端对外可达的公网基址(拼 <c>redirect_uri</c> 用,须与 IdP 注册的回调前缀一致,如 <c>https://admin.example.com</c>)。
    /// <para>为空则从当前请求推断(<c>scheme://host</c>);反向代理下请显式配,否则推断出内网地址致回调对不上。</para>
    /// </summary>
    public string? CallbackBaseUrl { get; set; }

    /// <summary>
    /// 登录成功后带一次性票据重定向到的<b>前端</b>页路径(SPA 在此页用 ticket 调 <c>POST /auth/external/exchange</c> 换令牌)。
    /// 默认 <c>/oauth/callback</c>。令牌<b>不进</b>重定向 URL,只带票据(见 <see cref="CacheKeys.OAuthTicket"/>)。
    /// </summary>
    public string FrontendResultPath { get; set; } = "/oauth/callback";
}

/// <summary>单个 OIDC provider 的运行时连接配置,由 <c>OidcExternalAuthProviderType</c> 用库里解密后的字段装配。</summary>
public class OidcProviderOptions
{
    /// <summary>provider 唯一码(登录按钮 / 回调路由 / 运营配置键都用它;多 IdP 时须互不相同,如 <c>keycloak</c>、<c>entra</c>)</summary>
    public string Code { get; set; } = "oidc";

    /// <summary>展示名(登录页按钮文案兜底;前端可用 i18n 覆盖)</summary>
    public string DisplayName { get; set; } = "SSO";

    /// <summary>图标(iconify 名或图片 URL,可空)</summary>
    public string? Icon { get; set; }

    /// <summary>OIDC 颁发者基址(取 <c>{Authority}/.well-known/openid-configuration</c> 发现文档,拿 authorization/token 端点与 JWKS)</summary>
    public string Authority { get; set; } = "";

    /// <summary>客户端 Id(IdP 注册的 client_id)</summary>
    public string ClientId { get; set; } = "";

    /// <summary>客户端密钥(机密客户端;全程服务端持有,不下发浏览器)</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>申请的 scope(空格分隔,默认 <c>openid profile email</c>;<c>openid</c> 必带)</summary>
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>是否启用 PKCE(S256,默认启用;机密客户端也建议开,防授权码注入)</summary>
    public bool UsePkce { get; set; } = true;
}
