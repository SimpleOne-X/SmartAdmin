using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>provider 的来源:库里的配置行,或消费者用代码注册的实例(只读)。</summary>
public static class ExternalAuthProviderSource
{
    /// <summary>库里的配置行,可在管理页编辑</summary>
    public const string Db = "db";

    /// <summary>消费者用 <see cref="IExternalAuthProvider"/> 代码注册,管理页只读</summary>
    public const string Code = "code";
}

/// <summary>类型描述里的一个字段(出参)。字段含义见 <see cref="ExternalAuthField"/>。</summary>
public sealed record ExternalAuthFieldView(string Name, bool Secret, bool Required, string? Default, bool DefinesEndpoint);

/// <summary>已安装的 provider 类型(出参):管理页据此渲染「添加」入口与设置面板的字段。</summary>
public sealed record ExternalAuthTypeView(
    string Type,
    string DefaultDisplayName,
    string? DefaultIcon,
    bool AllowMultiple,
    IReadOnlyList<ExternalAuthFieldView> Fields);

/// <summary>一个机密字段的状态:只回「是否已配置」与尾四位提示,永远没有明文。</summary>
/// <param name="HasValue">已配置</param>
/// <param name="Hint">尾四位;机密不足 8 位或未配置时为 <c>null</c></param>
public sealed record ExternalAuthSecretState(bool HasValue, string? Hint);

/// <summary>
/// 一个 provider 的配置视图(出参)。<b>不含机密明文</b>:<see cref="Secrets"/> 只有状态。
/// </summary>
/// <param name="Code">provider 码</param>
/// <param name="Type">类型码;代码注册的 provider 无类型,为空串</param>
/// <param name="DisplayName">展示名(已回退到类型默认)</param>
/// <param name="Icon">图标(已回退到类型默认)</param>
/// <param name="Source"><see cref="ExternalAuthProviderSource"/></param>
/// <param name="Installed">对应类型已安装;库里的行对应的可选包被卸载后为 <c>false</c>,此时不出实例</param>
/// <param name="Configured">必填项与必填机密都齐全,登录可用</param>
/// <param name="Values">非机密字段值</param>
/// <param name="Secrets">机密字段状态,键为字段名</param>
/// <param name="CallbackUri">要填到厂商后台的回调地址;由端点层按当前请求与 <c>CallbackBaseUrl</c> 算出</param>
public sealed record ExternalAuthProviderView(
    string Code,
    string Type,
    string DisplayName,
    string? Icon,
    string Source,
    bool Installed,
    bool Configured,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, ExternalAuthSecretState> Secrets,
    string CallbackUri = "");

/// <summary>「登录方式」页的目录:已装类型 + 全部 provider + 主密钥状态。</summary>
/// <param name="DataProtectionEphemeral">数据保护主密钥是进程内临时密钥,此时拒绝保存(40034)</param>
/// <param name="Types">已安装的类型</param>
/// <param name="Providers">全部 provider(库里的行与代码注册的实例)</param>
public sealed record ExternalAuthCatalog(
    bool DataProtectionEphemeral,
    IReadOnlyList<ExternalAuthTypeView> Types,
    IReadOnlyList<ExternalAuthProviderView> Providers);

/// <summary>
/// 保存入参。<see cref="Secrets"/> 的属性名必须叫 <c>Secrets</c>:操作日志的脱敏器按名字子串匹配 <c>secret</c>,
/// 命中即整包打码,机密不会落进日志。
/// </summary>
/// <param name="Type">类型码;新增时决定类型,更新时必须与已存类型一致</param>
/// <param name="DisplayName">展示名;空 = 用类型默认</param>
/// <param name="Icon">图标;空 = 用类型默认</param>
/// <param name="Values">非机密字段值(字段名 → 值)</param>
/// <param name="Secrets">机密字段值(字段名 → 明文);留空 = 不修改,首次保存必填</param>
public sealed record ExternalAuthProviderSaveInput(
    string Type,
    string? DisplayName,
    string? Icon,
    IReadOnlyDictionary<string, string?>? Values,
    IReadOnlyDictionary<string, string?>? Secrets);

/// <summary>
/// 连接测试入参:用表单里的值测,不落库。<see cref="Secrets"/> 留空的机密字段取 <see cref="Code"/> 对应的已保存值。
/// </summary>
/// <param name="Code">已保存 provider 的码;新增未保存时可空,此时机密必须全部在 <see cref="Secrets"/> 里给出</param>
/// <param name="Type">类型码</param>
/// <param name="Values">非机密字段值</param>
/// <param name="Secrets">机密字段值</param>
public sealed record ExternalAuthProviderTestInput(
    string? Code,
    string Type,
    IReadOnlyDictionary<string, string?>? Values,
    IReadOnlyDictionary<string, string?>? Secrets);

/// <summary>连接测试的一项检查(出参)。<see cref="Status"/> 为 <c>ok</c> / <c>failed</c> / <c>skipped</c>。</summary>
public sealed record ExternalAuthCheckView(string Key, string Status, string? Detail);

/// <summary>连接测试结果(出参):逐项列出;任何一项 <c>failed</c> 则 <see cref="Ok"/> 为 <c>false</c>。</summary>
public sealed record ExternalAuthTestView(bool Ok, IReadOnlyList<ExternalAuthCheckView> Checks);
