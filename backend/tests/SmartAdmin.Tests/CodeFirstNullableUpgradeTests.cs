using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 演进列补列契约:已有数据的表上 CodeFirst 只能安全 ADD <b>可空</b>列。
/// <para>根因:MSSQL 对「有数据的表 + ADD NOT NULL 且无 DEFAULT」直接失败;把演进列标成可空
/// (<c>IsNullable=true</c>)后,InitTables 补列应在四方言(尤其 SqlServer)上成功,
/// 旧行数据库 NULL 物化为 CLR 默认值,业务读侧按默认 false / 回退 ExpiresAt 处理。</para>
/// <para>空库全量建表 CI 绿证明不了这条路径——这里刻意:先有数据 → 砍列 → 再补列。</para>
/// </summary>
public class CodeFirstNullableUpgradeTests
{
    private const string LegacySessionId = "nullable-upgrade-legacy-session";

    [Fact]
    public async Task NonEmptyTable_CodeFirst_readds_nullable_upgrade_columns()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-nullable-upg-{Guid.NewGuid():N}.db");

        try
        {
            // 1) 正常首启:建表 + 种子 → sys_user 已有行(超管等);前提是从零建表,不拿模板副本
            using (var v1 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false, FreshDatabase = true })
            {
                _ = v1.CreateClient();
                using var scope = v1.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
                var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
                var sessions = scope.ServiceProvider.GetRequiredService<IRepository<SysSession>>();

                // 这里是脱离 HTTP 请求的后台 DI 作用域,没有租户上下文(currentUser.TenantId 为 null):
                // 直接查种子超管须跨租户查找,否则租户过滤器退化为找不到任何已归属租户的行。
                var seedAdmin = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.IsSuperAdmin).FirstAsync();
                Assert.NotNull(seedAdmin);
                await sessions.InsertAsync(new SysSession
                {
                    SessionId = LegacySessionId,
                    UserId = seedAdmin!.Id,
                    Account = seedAdmin.Account,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                    AbsoluteExpiresAt = DateTime.UtcNow.AddMinutes(10),
                });

                // 2) 退化成「补列前的老库」:砍掉演进列(模拟升级前结构)
                db.DbMaintenance.DropColumn("sys_user", "ForceTotp");
                db.DbMaintenance.DropColumn("sys_user", "TotpEnabled");
                db.DbMaintenance.DropColumn("sys_session", "AbsoluteExpiresAt");
            }

            // 3) 同库二次启动:Development 默认开 CodeFirst → InitTables 补回可空列
            using var v2 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false };
            _ = v2.CreateClient();
            using var s2 = v2.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<ISqlSugarClient>();
            var users2 = s2.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
            var sessions2 = s2.ServiceProvider.GetRequiredService<IRepository<SysSession>>();
            var sessionService = s2.ServiceProvider.GetRequiredService<ISessionService>();

            var userCols = db2.DbMaintenance.GetColumnInfosByTableName("sys_user", false)
                .Select(c => c.DbColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("ForceTotp", userCols);
            Assert.Contains("TotpEnabled", userCols);

            var sessionCols = db2.DbMaintenance.GetColumnInfosByTableName("sys_session", false)
                .Select(c => c.DbColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("AbsoluteExpiresAt", sessionCols);

            // 4) 存量行:补列后应为 null(可空语义),读侧按 false
            // 同上:后台 DI 作用域没有租户上下文,须跨租户查找。
            var admin = await users2.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.IsSuperAdmin).FirstAsync();
            Assert.NotNull(admin);
            Assert.False(admin!.ForceTotp);
            Assert.False(admin.TotpEnabled);

            // 同上:后台 DI 作用域没有租户上下文,须跨租户查找(该行的 TenantId 在 v2 启动时已由
            // TenantBackfillHook 回填成默认租户,但这里是没有 HttpContext 的验证读,不能依赖 currentUser.TenantId)。
            var legacySession = await sessions2.AsQueryable().ClearFilter<ITenantScoped>()
                .Where(s => s.SessionId == LegacySessionId).FirstAsync();
            Assert.NotNull(legacySession);
            Assert.Equal(default, legacySession!.AbsoluteExpiresAt);
            Assert.True(await sessionService.IsActiveAsync(LegacySessionId));
        }
        finally
        {
            TestDb.Cleanup(dbPath, dbPath);
        }
    }
}
