namespace SmartAdmin.Core;

/// <summary>
/// Scalar API 文档 UI 配置(对应 <c>SmartAdmin:Scalar</c> 节)。
/// <para><c>/scalar</c> 壳页面始终匿名(无契约数据);<c>/openapi/{documentName}.json</c> 开发环境匿名不变,
/// 生产环境默认不挂载,<see cref="EnabledInProduction"/> 显式开启后收紧为 <c>ScalarAccess</c> 授权策略。</para>
/// </summary>
public class AdminScalarOptions
{
    /// <summary>生产环境是否显式开启 Scalar/OpenAPI(默认关)。开发环境不受此项影响,始终暴露。</summary>
    public bool EnabledInProduction { get; set; }
}
