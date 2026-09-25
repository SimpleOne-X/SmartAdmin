using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 第三方登录 provider 的连接配置:管理员在「系统配置 → 登录方式」里维护,业务代码通过
/// <see cref="Core.IExternalAuthProviderRegistry"/> 取用,不直接依赖本表结构。
/// <para>单独成表而不放 <c>sys_config</c>:配置值会出现在配置列表、导出与操作日志里,密钥放进去等于明文露出。
/// 启用开关、自动开户等运营项仍在 <c>sys_config</c>(<c>sys.externalauth.{code}.*</c>)。</para>
/// </summary>
[SugarTable("sys_external_auth_provider", TableDescription = "第三方登录连接配置")]
[SugarIndex("idx_sys_external_auth_provider_code", nameof(Code), OrderByType.Asc, IsUnique = true)]
public class SysExternalAuthProvider : BaseEntity
{
    /// <summary>provider 码(唯一;登录按钮、回调路由、运营配置键都用它)。官方厂商类型的 Code 等于类型名;保存后不可改</summary>
    [SugarColumn(Length = 64, ColumnDescription = "provider 码(唯一;保存后不可改)")]
    public string Code { get; set; } = "";

    /// <summary>类型码(<c>oidc</c> / <c>wecom</c> / <c>dingtalk</c> / <c>github</c> / <c>wechat</c> …);保存后不可改</summary>
    [SugarColumn(Length = 32, ColumnDescription = "类型码(保存后不可改)")]
    public string Type { get; set; } = "";

    /// <summary>登录按钮文案;null = 用类型的默认展示名</summary>
    [SugarColumn(Length = 64, IsNullable = true, ColumnDescription = "展示名")]
    public string? DisplayName { get; set; }

    /// <summary>图标(iconify 名或图片 URL);null = 用类型默认图标</summary>
    [SugarColumn(Length = 256, IsNullable = true, ColumnDescription = "图标")]
    public string? Icon { get; set; }

    /// <summary>非机密字段的 JSON(字段名 → 值),明文存放,管理页回显以便核对填的是哪个应用</summary>
    [SugarColumn(Length = 2048, IsNullable = true, ColumnDescription = "非机密字段 JSON")]
    public string? SettingsJson { get; set; }

    /// <summary>机密字段整体一个 JSON(字段名 → 值),经 <see cref="Core.ISecretProtector"/> 封成信封;null = 未配置任何机密</summary>
    [SugarColumn(Length = 4000, IsNullable = true, ColumnDescription = "机密字段加密信封(ISecretProtector)")]
    public string? SecretsProtected { get; set; }

    /// <summary>
    /// 各机密字段的展示提示 JSON(字段名 → 尾四位)。键存在即表示该机密已配置;
    /// 机密不足 8 位时值为空串,不露出任何字符,避免短密钥被整个看到。
    /// </summary>
    [SugarColumn(Length = 512, IsNullable = true, ColumnDescription = "机密字段脱敏提示 JSON")]
    public string? SecretHints { get; set; }
}
