using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 默认租户种子——唯一一条固定 Id 的保留行,身兼两职:
/// 1) 全新安装:初始超管(SuperAdminSeed)挂在这个租户下,零配置启动不强制"先建租户再登录"。
/// 2) 老库升级:TenantBackfillHook 把补列后 TenantId 为 null 的存量行统一回填到这个租户,
///    升级前的单租户部署行为完全不变。
/// TenantService 对 DEFAULT_TENANT_ID 的删除/禁用请求一律拒绝(受保护,仿 SysModule 内置模块的保护写法)。
/// </summary>
public class DefaultTenantSeed : ISeedData<SysTenant>
{
    public const long DEFAULT_TENANT_ID = 1;

    public virtual IEnumerable<SysTenant> HasData() =>
    [
        new SysTenant
        {
            Id = DEFAULT_TENANT_ID,
            Code = "default",
            Name = "默认租户",
            IsolationMode = TenantIsolationMode.Shared,
            Enabled = true,
            Remark = "内置默认租户,承载初始超管账号与老库升级回填的存量数据,不可删除",
        },
    ];
}
