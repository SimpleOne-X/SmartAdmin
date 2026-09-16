namespace SmartAdmin.Core;

/// <summary>
/// 租户的数据隔离模式。一期只支持 <see cref="Shared"/>;<see cref="Standalone"/> 是二期"混合模式"的预留值,
/// 一期 <c>TenantService</c> 会拒绝写入(见 <c>ErrorCode.TenantIsolationModeNotSupported</c>)。
/// 二期即便某租户选了 Standalone,内核自身的机构/用户/角色/菜单授权数据仍然留在共享主库,只有消费方
/// 自己的业务表会路由到独立连接串——对应 SqlSugar 官方"基础信息库 + 业务库"的划分,见设计文档 §8。
/// </summary>
public enum TenantIsolationMode
{
    /// <summary>共享库:与其它租户共用主库,按 TenantId 过滤隔离。一期唯一可选项。</summary>
    Shared = 1,

    /// <summary>独立库(二期):消费方业务数据路由到独立连接串。一期写入即拒绝。</summary>
    Standalone = 2,
}
