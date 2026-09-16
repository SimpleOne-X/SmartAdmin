using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 升级回填契约:已发版表补列后 TenantId 为 null 的存量行,重启后必须被回填到默认租户,
/// 否则套上 ITenantScoped 过滤器后这些行会对所有人不可见——等同升级后数据"消失"。
/// 仿 CodeFirstNullableUpgradeTests 的"先有数据 → 退化成老库状态 → 二次启动补回"手法。
/// </summary>
public class TenantBackfillUpgradeTests
{
    [Fact]
    public async Task Legacy_null_TenantId_row_is_backfilled_to_default_tenant_on_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-tenant-backfill-{Guid.NewGuid():N}.db");

        try
        {
            using (var v1 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false, FreshDatabase = true })
            {
                _ = v1.CreateClient();
                using var scope = v1.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

                // 模拟"升级前的老库":把种子超管这一行的 TenantId 直接置空(绕过仓储,不经过滤器/AOP)
                await db.Updateable<SysUser>().SetColumns(u => u.TenantId == null).Where(u => u.IsSuperAdmin).ExecuteCommandAsync();
            }

            using var v2 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false };
            _ = v2.CreateClient();
            using var s2 = v2.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<ISqlSugarClient>();

            var admin = await db2.Queryable<SysUser>().ClearFilter<ITenantScoped>().Where(u => u.IsSuperAdmin).FirstAsync();
            Assert.NotNull(admin);
            Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, admin!.TenantId);
        }
        finally
        {
            TestDb.Cleanup(dbPath, dbPath);
        }
    }
}
