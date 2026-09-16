using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 运维数据实体(通知/文件/日志/会话)迁入 TenantEntity 的表结构回归——不重复 Task 9 已经证明过的
/// HTTP 级跨租户隔离手法,只验证列存在 + 种子期产生的数据能被正确回填/归属默认租户。
/// </summary>
public class TenantOperationalEntitiesTests
{
    public static IEnumerable<object[]> MigratedTables() =>
    [
        ["sys_notice"], ["sys_notice_receiver"], ["sys_notice_read"], ["sys_file"],
        ["sys_login_log"], ["sys_op_log"], ["sys_exception_log"], ["sys_session"],
    ];

    [Theory]
    [MemberData(nameof(MigratedTables))]
    public async Task Table_has_nullable_TenantId_column(string tableName)
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

        var cols = db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        var tenantCol = Assert.Single(cols, c => c.DbColumnName.Equals("TenantId", StringComparison.OrdinalIgnoreCase));
        Assert.True(tenantCol.IsNullable);
    }

    [Fact]
    public async Task Login_creates_a_session_row_scoped_to_the_logging_in_users_tenant()
    {
        using var f = new AdminAppFactory();
        var c = f.CreateClient();
        _ = await c.LoginToken("superAdmin", "Test@123456");

        using var scope = f.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IRepository<SysSession>>();
        // 本用例读的是没有 HttpContext 的后台 DI 作用域,currentUser.TenantId 恒为 null;ITenantScoped
        // 过滤器据此恒零行(见 TestTenantContext.cs 的同款背景说明)。这里要检验的是"写进去的 TenantId
        // 是否正确",属于合法的系统级/跨租户验证读,清过滤器直读存量值,而不是模拟一次已认证请求。
        var mine = await sessions.AsQueryable().ClearFilter<ITenantScoped>()
            .Where(s => s.Account == "superAdmin").ToListAsync();

        Assert.NotEmpty(mine);
        Assert.All(mine, s => Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, s.TenantId));
    }

    // ── 追加:SysSession 迁入 TenantEntity 后闭合 Task 7 评审点名、留到 Task 10 处理的安全缺口 ──
    // ListOnlineAsync/ForceLogoutAsync 原本完全依赖 IDataScopeGuard(机构数据范围),而租户内超管的
    // scopeGuard.IsUnrestricted 恒为 true,导致这两个方法对超管完全不设防:任何租户的超管理论上能看到、
    // 强退别的租户的在线会话。ITenantScoped 过滤器接上后,sessions.AsQueryable()/GetFirstAsync 会自动按
    // currentUser.TenantId 收窄,不需要改 SessionService.cs 任何逻辑就能闭合——本类只验证这条边界真的生效。

    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    private static async Task<(HttpClient Client, string Account)> NewTenantAdminClient(AdminAppFactory f, HttpClient platform, string codePrefix)
    {
        var code = $"{codePrefix}{Guid.NewGuid():N}"[..16];
        var account = $"{code}_admin";
        const string password = "Test@123456";

        var add = await platform.PostJson("/api/v1/sys/tenant/add", new
        {
            code, name = $"{code}-Inc", isolationMode = 1, enabled = true,
            adminAccount = account, adminPassword = password,
        });
        Assert.Equal(0, (await add.ReadEnvelope()).GetProperty("code").GetInt32());

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await client.LoginToken(account, password));
        return (client, account);
    }

    [Fact]
    public async Task Tenant_admin_cannot_see_another_tenants_online_sessions()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var (_, accountA) = await NewTenantAdminClient(f, platform, "sessA");
        var (clientB, accountB) = await NewTenantAdminClient(f, platform, "sessB");

        // 租户 B 的超管在线列表里看不到租户 A 超管自己的会话(会话行落库时已 TenantId=A)
        var onlineB = await (await clientB.GetAsync("/api/v1/sys/session/online?Current=1&Size=100")).ReadEnvelope();
        var accountsB = onlineB.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("account").GetString()).ToList();
        Assert.DoesNotContain(accountA, accountsB);
        // 正向对照:B 自己的会话确实在列表里,证明上面的"看不到"不是接口本身返回空集
        Assert.Contains(accountB, accountsB);
    }

    [Fact]
    public async Task Tenant_admin_cannot_force_logout_another_tenants_session()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var (_, accountA) = await NewTenantAdminClient(f, platform, "kickA");
        var (clientB, _) = await NewTenantAdminClient(f, platform, "kickB");

        // 从数据库直接读出租户 A 超管那个会话的 SessionId(测试断言用,不代表业务上 B 能拿到)
        string sessionIdA;
        using (var scope = f.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<IRepository<SysSession>>();
            var rowA = await sessions.AsQueryable().ClearFilter<ITenantScoped>()
                .Where(s => s.RevokedAt == null && s.Account == accountA)
                .OrderByDescending(s => s.Id)
                .FirstAsync();
            Assert.NotNull(rowA);
            sessionIdA = rowA!.SessionId;
        }

        // 租户 B 的超管尝试强退租户 A 的会话:先处理可能的 [RequireReauth] 挑战
        async Task<System.Text.Json.JsonElement> TryForceLogoutAsync()
        {
            var resp = await clientB.DeleteAsync($"/api/v1/sys/session/{sessionIdA}");
            return await resp.ReadEnvelope();
        }

        var env = await TryForceLogoutAsync();
        if (env.GetProperty("code").GetInt32() == (int)ErrorCode.ReauthRequired)
        {
            var reauthEnv = await (await clientB.PostJson("/api/v1/auth/reauth", new
            {
                method = "password",
                password = "Test@123456",
            })).ReadEnvelope();
            Assert.Equal(0, reauthEnv.GetProperty("code").GetInt32());
            env = await TryForceLogoutAsync();
        }

        // 越权:会话存在但不在操作者(租户 B)的可见范围内 → SessionNotFound(与 ForceLogoutAsync 现有的
        // 越权守卫同一错误码,见 SessionService.ForceLogoutAsync 注释——不区分"不存在"与"存在但越界"）
        Assert.Equal((int)ErrorCode.SessionNotFound, env.GetProperty("code").GetInt32());

        // 且租户 A 的会话确实没有被强退掉(RevokedAt 仍为 null)
        using (var scope = f.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<IRepository<SysSession>>();
            var rowA = await sessions.AsQueryable().ClearFilter<ITenantScoped>()
                .Where(s => s.SessionId == sessionIdA).FirstAsync();
            Assert.NotNull(rowA);
            Assert.Null(rowA!.RevokedAt);
        }
    }
}
