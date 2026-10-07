namespace SmartAdmin.Core;

/// <summary>
/// 外部登录 provider「类型」的一个配置字段。字段清单决定管理页上要填什么、哪些要加密存储。
/// </summary>
/// <param name="Name">字段名(camelCase,同时是存库 JSON 的键与接口入参的键)</param>
/// <param name="Secret">机密字段:加密入库,接口只回「是否已配置 + 尾四位」,编辑时留空表示不改</param>
/// <param name="Required">必填(机密字段首次保存必填)</param>
/// <param name="Default">缺省值(新增时预填;为 <c>null</c> 则无)</param>
/// <param name="DefinesEndpoint">
/// 这个字段决定「请求发往哪里」(如 OIDC 的 Authority)。改了它就必须重新输入机密:
/// 否则有权限的人能把地址改到自己控制的服务器,登录时服务端会带着已保存的密钥过去。
/// </param>
public sealed record ExternalAuthField(
    string Name,
    bool Secret = false,
    bool Required = true,
    string? Default = null,
    bool DefinesEndpoint = false);

/// <summary>
/// 一个 provider 的运行时配置(已解密):<see cref="Values"/> 同时含非机密字段与机密字段,键为
/// <see cref="ExternalAuthField.Name"/>。只存在于内存,不落日志。
/// </summary>
public sealed record ExternalAuthProviderConfig(
    string Code,
    string DisplayName,
    string? Icon,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>取字段值,不存在返回空串。</summary>
    public string Get(string name) => Values.TryGetValue(name, out var v) ? v : "";
}

/// <summary>连接测试的单项检查结果状态。</summary>
public enum ExternalAuthCheckStatus
{
    /// <summary>通过</summary>
    Ok,

    /// <summary>失败</summary>
    Failed,

    /// <summary>无法验证(如标准 OIDC 无法在没有用户参与时验证 Client Secret);不算失败也不算通过</summary>
    Skipped,
}

/// <summary>连接测试的一项检查。<see cref="Key"/> 供前端查文案;<see cref="Detail"/> 是厂商返回的技术细节(不含机密)。</summary>
public sealed record ExternalAuthCheck(string Key, ExternalAuthCheckStatus Status, string? Detail = null);

/// <summary>连接测试结果:逐项列出,不是一个笼统的「成功」。任何一项 <see cref="ExternalAuthCheckStatus.Failed"/> 即整体不通过。</summary>
public sealed record ExternalAuthTestResult(IReadOnlyList<ExternalAuthCheck> Checks)
{
    /// <summary>没有失败项。</summary>
    public bool Ok => Checks.All(c => c.Status != ExternalAuthCheckStatus.Failed);
}

/// <summary>
/// 外部登录 provider 的「类型描述」:一种身份源(企业微信、GitHub、标准 OIDC…)需要填哪些字段、
/// 怎么用这些字段造出 <see cref="IExternalAuthProvider"/>、怎么测连接。
/// <para>可选包只需注册一个本接口的实现(<c>TryAddEnumerable</c>),连接配置由管理员在「登录方式」页填写、加密入库,
/// 不读 appsettings。装了包才有对应类型;没装的类型在管理页显示「未安装」。</para>
/// <para>本层零运行时依赖,故 <c>Create</c>/<c>TestAsync</c> 用 <see cref="IServiceProvider"/> 取
/// <c>IHttpClientFactory</c>、<c>ILoggerFactory</c> 等(可选包本就引用 Microsoft.Extensions.*)。</para>
/// </summary>
public interface IExternalAuthProviderType
{
    /// <summary>类型码:<c>oidc</c> / <c>wecom</c> / <c>dingtalk</c> / <c>github</c> / <c>wechat</c>。</summary>
    string Type { get; }

    /// <summary>默认展示名(管理员没填时用)</summary>
    string DefaultDisplayName { get; }

    /// <summary>默认图标(iconify 名,可空;前端优先按 code 命中品牌图标)</summary>
    string? DefaultIcon { get; }

    /// <summary>
    /// 是否允许配置多条。<c>false</c>(官方厂商包):Code 就是 <see cref="Type"/>,只能有一份;
    /// <c>true</c>(标准 OIDC):Code 由管理员起。
    /// </summary>
    bool AllowMultiple { get; }

    /// <summary>字段清单</summary>
    IReadOnlyList<ExternalAuthField> Fields { get; }

    /// <summary>按配置造出 provider 实例(配置已通过必填校验)。</summary>
    IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services);

    /// <summary>
    /// 连接测试。能验证到哪一步就检查到哪一步,验证不了的返回 <see cref="ExternalAuthCheckStatus.Skipped"/>,
    /// 不要假装通过。不写库、不带用户登录态。网络/解析异常也应折成失败项返回,而不是抛出。
    /// </summary>
    Task<ExternalAuthTestResult> TestAsync(ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default);
}

/// <summary>
/// provider 注册表:登录、回调、绑定统一从这里取 <see cref="IExternalAuthProvider"/>。
/// 结果 = 消费者用 <see cref="IExternalAuthProvider"/> 代码注册的实例 ∪ 库里按类型现建的实例;
/// 同 Code 冲突时代码注册的优先(与仓库一贯的「消费者胜」一致)。
/// </summary>
public interface IExternalAuthProviderRegistry
{
    /// <summary>按 code 取;不存在或配置不完整返回 <c>null</c>。</summary>
    Task<IExternalAuthProvider?> FindAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>全部可用 provider(未配置完整的不含)。</summary>
    Task<IReadOnlyList<IExternalAuthProvider>> ListAsync(CancellationToken cancellationToken = default);
}
