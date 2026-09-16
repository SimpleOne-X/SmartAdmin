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

    /// <summary>
    /// 账号<b>确实存在</b>的登录失败(密码错),审计日志必须归属到该账号所属的租户。
    /// <para><c>ValidateUserAsync</c> 早已按账号跨租户把用户行解出来了,"没有已知用户可归属"只对
    /// "账号根本不存在"那一支成立;把这类失败一律留成 <c>TenantId=null</c>,等于全平台所有租户的
    /// 爆破痕迹堆在同一个无主分区里——租户管理员在自己的登录日志页一条都看不到针对自家账号的失败尝试。</para>
    /// </summary>
    [Fact]
    public async Task Failed_login_of_an_existing_account_is_attributed_to_that_accounts_tenant()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);
        var (tenantAdmin, account) = await NewTenantAdminClient(f, platform, "failog");

        // 故意用错密码登一次(一次不触发锁定阈值)
        var anonymous = f.CreateClient();
        var bad = await (await anonymous.PostJson("/api/v1/auth/login",
            new { account, password = "Wrong@000000" })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.PasswordWrong, bad.GetProperty("code").GetInt32());

        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
        var logs = scope.ServiceProvider.GetRequiredService<IRepository<SysLoginLog>>();

        var user = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Account == account).FirstAsync();
        Assert.NotNull(user);
        Assert.NotNull(user!.TenantId);

        // 后台 DI 作用域没有租户上下文 → 清过滤器直读存量值(验证读,同上面几条)。
        // 布尔列写成 `== false` 而非 `!x`:SqlServer 的谓词上下文不接受裸标量。
        var failed = await logs.AsQueryable().ClearFilter<ITenantScoped>()
            .Where(l => l.Account == account && l.Success == false).ToListAsync();
        var row = Assert.Single(failed);
        Assert.Equal(user.TenantId, row.TenantId);

        // HTTP 侧的等价事实:这条失败记录落在该租户名下,所以租户自己的管理员在登录日志页看得到它
        var page = await (await tenantAdmin.GetAsync(
            $"/api/v1/sys/log/login/page?Current=1&Size=100&Account={account}&Success=false")).ReadEnvelope();
        Assert.Equal(0, page.GetProperty("code").GetInt32());
        var accounts = page.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("account").GetString()).ToList();
        Assert.Contains(account, accounts);
    }

    // ── 系统/未认证上下文下的 TenantId 归属(C3 修复轮) ────────────────────────────────
    // 共同背景:ITenantScoped 全局过滤器对 currentUser.TenantId 为 null 的调用者恒零行(见 SqlSugarSetup),
    // 插入 AOP 同理只在有租户上下文时才回填。于是"没有登录态的写入点"如果不显式定租户,写出来的行
    // 在过滤器眼里谁都看不见——要么白写,要么(存在性校验那种)在插入之前就把整条路径判死。

    /// <summary>
    /// 账号<b>根本不存在</b>的登录失败:无人可归属,兜底到默认租户而不是留 null。
    /// <para>留 null 的行被租户过滤器挡在所有人视线之外,等于写了没写;代价(探测流量混进默认租户日志)
    /// 是已裁定接受的已知局限,与 <c>TenantBackfillHook</c> 把无主存量行回填到默认租户同一类取舍。</para>
    /// </summary>
    [Fact]
    public async Task Failed_login_of_an_unknown_account_falls_back_to_the_default_tenant()
    {
        using var f = new AdminAppFactory();
        var anonymous = f.CreateClient();
        var account = $"ghost{Guid.NewGuid():N}"[..20];

        var env = await (await anonymous.PostJson("/api/v1/auth/login",
            new { account, password = "Wrong@000000" })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.PasswordWrong, env.GetProperty("code").GetInt32());

        using var scope = f.Services.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<IRepository<SysLoginLog>>();
        // 后台 DI 作用域没有租户上下文 → 清过滤器直读存量值(同本类其它几条验证读)
        var row = Assert.Single(await logs.AsQueryable().ClearFilter<ITenantScoped>()
            .Where(l => l.Account == account).ToListAsync());
        Assert.False(row.Success);
        Assert.Null(row.UserId);                                                   // 确实是"账号不存在"那一支
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, row.TenantId);
    }

    /// <summary>无登录态的操作日志(未绑定用户的 API Key / 后台任务 / 无 HttpContext 的系统调用)兜底默认租户。</summary>
    [Fact]
    public async Task System_context_operation_log_falls_back_to_the_default_tenant()
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();   // 触发建库 + 种子

        var path = $"/diag/system-context-oplog/{Guid.NewGuid():N}";
        using var scope = f.Services.CreateScope();   // 无 HttpContext ⇒ currentUser.TenantId 为 null
        await scope.ServiceProvider.GetRequiredService<ILogService>().RecordOperationAsync(new OperationLogEntry
        {
            Title = "后台写入", HttpMethod = "POST", Path = path, ResultCode = 0,
        });

        var logs = scope.ServiceProvider.GetRequiredService<IRepository<SysOpLog>>();
        var row = Assert.Single(await logs.AsQueryable().ClearFilter<ITenantScoped>()
            .Where(x => x.Path == path).ToListAsync());
        Assert.Null(row.OperatorId);                                               // 确实没有登录态
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, row.TenantId);
    }

    /// <summary>
    /// 无登录态的异常日志同样兜底默认租户——匿名端点/后台任务崩掉恰恰是最需要看见的那批,
    /// 留 null 会让它们整批消失在过滤器后面。
    /// </summary>
    [Fact]
    public async Task System_context_exception_log_falls_back_to_the_default_tenant()
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();

        var path = $"/diag/system-context-exlog/{Guid.NewGuid():N}";
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILogService>().RecordExceptionAsync(new ExceptionLogEntry
        {
            HttpMethod = "GET", Path = path,
            ExceptionType = "System.InvalidOperationException", Message = "boom-system-context",
        });

        var logs = scope.ServiceProvider.GetRequiredService<IRepository<SysExceptionLog>>();
        var row = Assert.Single(await logs.AsQueryable().ClearFilter<ITenantScoped>()
            .Where(x => x.Path == path).ToListAsync());
        Assert.Null(row.OperatorId);
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, row.TenantId);
    }

    /// <summary>
    /// 系统上下文定向发通知(JobExecutor 的 Panic 告警就是这条路径):接收目标存在性校验必须看得见真实用户,
    /// 通知本体必须真的落库并归属默认租户。
    /// <para>修复前这里是真丢数据:校验在插入<b>之前</b>,过滤器让它查到零行 → 抛 45003 →
    /// 被 <c>SendPanicAlertAsync</c> 的 try/catch 吞成一条警告,那条 INSERT 根本没执行到。</para>
    /// </summary>
    [Fact]
    public async Task System_context_targeted_publish_sees_the_real_user_and_lands_in_the_default_tenant()
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();

        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
        var superAdmin = await users.AsQueryable().ClearFilter<ITenantScoped>()
            .Where(u => u.Account == "superAdmin").FirstAsync();
        Assert.NotNull(superAdmin);

        var title = $"系统上下文定向通知-{Guid.NewGuid():N}";
        var id = await scope.ServiceProvider.GetRequiredService<INoticeService>().PublishAsync(new NoticePublishInput
        {
            Title = title,
            Content = "x",
            ReceiverType = ReceiverType.User,
            ReceiverIds = [superAdmin!.Id],
        });
        Assert.True(id > 0);

        var notices = scope.ServiceProvider.GetRequiredService<IRepository<SysNotice>>();
        var row = await notices.AsQueryable().ClearFilter<ITenantScoped>().Where(n => n.Id == id).FirstAsync();
        Assert.NotNull(row);
        Assert.Equal(title, row!.Title);
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, row.TenantId);
    }

    /// <summary>
    /// 上一条的反面守卫:存在性校验清租户过滤器<b>只许</b>发生在没有租户上下文时。
    /// <para>已认证的租户 A 管理员拿租户 B 的真实用户 Id 当定向目标,必须仍然被判成"目标不存在"——
    /// 这层校验是唯一的把关处(插入接收目标行时不再查库),无条件清过滤器就等于开一个跨租户 IDOR:
    /// 既能给别的租户的人发通知,又能拿错误码当探针问出"B 租户有没有这个 Id"。</para>
    /// </summary>
    [Fact]
    public async Task Tenant_admin_cannot_target_another_tenants_user_in_a_targeted_notice()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var (clientA, accountA) = await NewTenantAdminClient(f, platform, "noticA");
        var (_, accountB) = await NewTenantAdminClient(f, platform, "noticB");

        long userIdA, userIdB;
        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
            var rowA = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Account == accountA).FirstAsync();
            var rowB = await users.AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Account == accountB).FirstAsync();
            Assert.NotNull(rowA);
            Assert.NotNull(rowB);
            Assert.NotEqual(rowA!.TenantId, rowB!.TenantId);   // 确实是两个不同租户的真实用户
            (userIdA, userIdB) = (rowA.Id, rowB.Id);
        }

        // 正向对照:发给自己租户的人是通的(证明下面那条不是"发通知这条路整体坏了")
        var ok = await (await clientA.PostJson("/api/v1/sys/notice",
            new { title = "自家人", type = 1, receiverType = 2, receiverIds = new[] { userIdA } })).ReadEnvelope();
        Assert.Equal(0, ok.GetProperty("code").GetInt32());

        // 越权:目标是租户 B 的真实用户 → 仍按"看不见就是不存在"拒绝
        const string crossTitle = "越租户";
        var denied = await (await clientA.PostJson("/api/v1/sys/notice",
            new { title = crossTitle, type = 1, receiverType = 2, receiverIds = new[] { userIdB } })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.NoticeReceiverNotFound, denied.GetProperty("code").GetInt32());

        // 且整体拒绝发生在插入之前:这条通知一行都没落库(跨租户也查不到)
        using (var scope = f.Services.CreateScope())
        {
            var notices = scope.ServiceProvider.GetRequiredService<IRepository<SysNotice>>();
            Assert.Empty(await notices.AsQueryable().ClearFilter<ITenantScoped>()
                .Where(n => n.Title == crossTitle).ToListAsync());
        }
    }
}
