using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// Task 10 收尾复审(评审员核实)发现的两个既有(Task 7/9 遗留,非本次多租户改造范围)缺陷的回归:
/// <list type="number">
///   <item><b>SqlSugarCore 的 <c>ClearFilter&lt;T&gt;()</c> 对内部状态是赋值不是追加</b>——链式
///   <c>.ClearFilter&lt;A&gt;().ClearFilter&lt;B&gt;()</c> 只有最后一次调用真的生效,前一次被覆盖。
///   受影响的三处生产代码(<c>AuthService.GenerateProvisionAccountAsync</c>/<c>UserService.AddAsync</c>/
///   <c>TenantService.AddAsync</c>)原写法是 <c>ClearFilter&lt;ISoftDelete&gt;().ClearFilter&lt;ITenantScoped&gt;()</c>,
///   被吞掉的是 <c>ISoftDelete</c>——软删但唯一列未改名的同名行会被误判为"不重复",查重放行后撞库唯一索引
///   直接抛原生异常(500),而不是这里本该给出的 <c>AccountExists</c>。已改成单次调用的双类型参数重载
///   <c>ClearFilter&lt;ISoftDelete, ITenantScoped&gt;()</c>。</item>
///   <item><b>这个 bug 得以现形的前提</b>:<c>SqlSugarRepository.ReleaseUniqueColumnsAsync</c>(软删前给唯一列
///   追加 <c>_del_{id}</c> 后缀释放唯一位)内部按主键查目标行本身受 <c>ITenantScoped</c> 过滤器约束——在没有
///   租户上下文(后台任务/系统作用域)软删一个 <c>ITenantScoped</c> 实体时,这一步查不到行,直接跳过改名,
///   但紧接着的裸 <c>Updateable</c> 照样把 <c>IsDelete</c> 置 1。净效果:任何在无租户上下文软删的行,
///   唯一列永远不会被改名,永久占着唯一索引位。已按 <see cref="SqlSugarRepository{TEntity}"/> 里已有的
///   <c>IsTenantScoped</c> 判据,在需要时对这一步查询显式 <c>ClearFilter&lt;ITenantScoped&gt;()</c>。</item>
/// </list>
/// </summary>
public class ClearFilterChainedCallRegressionTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    /// <summary>
    /// 直接造一条"软删但唯一列未改名"的用户行——绕过(已修复的)<c>IRepository&lt;T&gt;.DeleteAsync</c>
    /// 软删流程,直接以 <c>IsDelete=true</c> 落库。这不是凭空构造:任何绕过仓储软删路径直接置位
    /// <c>IsDelete</c> 的历史数据/迁移脚本/未来新缺口都会产生同样形状的行,用来单独验证"账号唯一性检查"
    /// 这一侧的修复,不依赖 <see cref="SqlSugarRepository{TEntity}.ReleaseUniqueColumnsAsync"/> 那一侧是否已修。
    /// </summary>
    private static async Task InsertUnrenamedSoftDeletedUserAsync(IServiceProvider sp, string account)
    {
        var users = sp.GetRequiredService<IRepository<SysUser>>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();
        await users.InsertAsync(new SysUser
        {
            Account = account,
            Password = hasher.Hash("Placeholder@123"),
            Name = "占位软删用户",
            Enabled = true,
            IsDelete = true,   // 直接置位,不经 DeleteAsync → 唯一列没有被改名,原样占着 Account
        });
    }

    // ── 1. SqlSugarRepository.ReleaseUniqueColumnsAsync:无租户上下文软删也要改名 ──

    [Fact]
    public async Task Soft_delete_without_tenant_context_still_releases_the_unique_account_column()
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();   // 确保宿主起来、表建好

        var account = "release-unique-" + Guid.NewGuid().ToString("N")[..8];
        long userId;

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var users = sp.GetRequiredService<IRepository<SysUser>>();
            var hasher = sp.GetRequiredService<IPasswordHasher>();

            // 没有 HttpContext 的后台 DI 作用域:插入 AOP 填不到 TenantId,显式给,与真实登录后的
            // tid=1 令牌一致(同 FileOwnerTests/SystemTableScopeTests 等既有先例)。
            var user = new SysUser
            {
                TenantId = DefaultTenantSeed.DEFAULT_TENANT_ID,
                Account = account,
                Password = hasher.Hash("Placeholder@123"),
                Name = "释放唯一位测试",
                Enabled = true,
            };
            await users.InsertAsync(user);
            userId = user.Id;

            // 关键:这一步软删同样发生在没有租户上下文的作用域里——修复前 ReleaseUniqueColumnsAsync
            // 会因为查不到行而跳过改名,IsDelete 却照样被置 1。
            var affected = await users.DeleteAsync(userId);
            Assert.Equal(1, affected);
        }

        using (var check = f.Services.CreateScope())
        {
            var users = check.ServiceProvider.GetRequiredService<IRepository<SysUser>>();
            var row = await users.AsQueryable().ClearFilter<ISoftDelete, ITenantScoped>()
                .Where(u => u.Id == userId).FirstAsync();
            Assert.NotNull(row);
            Assert.True(row!.IsDelete);
            // 唯一列必须已经被改名——不再是原值,而是带上了 _del_{id} 后缀,原值才算真正被释放
            Assert.NotEqual(account, row.Account);
            Assert.EndsWith($"_del_{userId}", row.Account);
        }
    }

    // ── 2. UserService.AddAsync 的账号唯一性检查 ──

    [Fact]
    public async Task UserService_AddAsync_rejects_duplicate_of_an_unrenamed_soft_deleted_account()
    {
        using var f = new AdminAppFactory();
        var admin = await SuperAdminClient(f);
        var account = "dup-user-" + Guid.NewGuid().ToString("N")[..8];

        using (var scope = f.Services.CreateScope())
            await InsertUnrenamedSoftDeletedUserAsync(scope.ServiceProvider, account);

        // 建同名账号:查重必须真的命中这条软删未改名的行,优雅返回 AccountExists——
        // 而不是让请求穿透到 InsertAsync,撞上 sys_user.Account 的真实唯一索引,抛原生异常变成 500。
        var resp = await admin.PostJson("/api/v1/sys/user", new
        {
            account, password = "NewPass@123456", name = "新建同名用户", enabled = true, roleIds = Array.Empty<long>(),
        });
        var env = await resp.ReadEnvelope();
        Assert.Equal((int)ErrorCode.AccountExists, env.GetProperty("code").GetInt32());
    }

    // ── 3. TenantService.AddAsync 的管理员账号唯一性检查(跨租户口径) ──

    [Fact]
    public async Task TenantService_AddAsync_rejects_duplicate_admin_account_of_an_unrenamed_soft_deleted_user()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);
        var account = "dup-tenant-admin-" + Guid.NewGuid().ToString("N")[..8];

        using (var scope = f.Services.CreateScope())
            await InsertUnrenamedSoftDeletedUserAsync(scope.ServiceProvider, account);

        var code = "dt" + Guid.NewGuid().ToString("N")[..14];
        var resp = await platform.PostJson("/api/v1/sys/tenant/add", new
        {
            code, name = $"{code}-Inc", isolationMode = 1, enabled = true,
            adminAccount = account, adminPassword = "NewPass@123456",
        });
        var env = await resp.ReadEnvelope();
        Assert.Equal((int)ErrorCode.AccountExists, env.GetProperty("code").GetInt32());
    }

    // ── 4. AuthService.GenerateProvisionAccountAsync 的自动开户查重(外部登录首次建号) ──

    private sealed class FakeExternalAuthProvider(ExternalIdentity identity) : IExternalAuthProvider
    {
        public string Code => identity.Provider;
        public string DisplayName => "Fake";
        public string? Icon => null;
        public Task<string> BuildAuthorizeUrlAsync(ExternalAuthorizeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://idp.test/authorize?state={Uri.EscapeDataString(request.State)}");
        public Task<ExternalIdentity> ExchangeAsync(ExternalExchangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(identity);
    }

    [Fact]
    public async Task External_login_provisioning_avoids_colliding_with_an_unrenamed_soft_deleted_account()
    {
        // GenerateProvisionAccountAsync 的候选账号是 "{provider}_{邮箱前缀清洗后的 slug}"
        const string provider = "provdup";
        const string emailSeed = "provseed";
        var baseAccount = $"{provider}_{emailSeed}";
        var identity = new ExternalIdentity(provider, "sub-provdup", Email: $"{emailSeed}@example.com");

        using var f = new AdminAppFactory
        {
            Overrides = s => s.AddSingleton<IExternalAuthProvider>(new FakeExternalAuthProvider(identity)),
        };
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // 预占第一候选账号:软删但未改名(同上,绕过仓储软删路径直接构造)
        await InsertUnrenamedSoftDeletedUserAsync(sp, baseAccount);

        // 打开该 provider 的自动开户
        await sp.GetRequiredService<IConfigService>().AddAsync(new ConfigInput
        {
            ConfigKey = $"sys.externalauth.{provider}.provisioning",
            ConfigValue = "provision",
            Name = "外部登录-provdup-未绑定策略",
        });

        // 查重修复前:GenerateProvisionAccountAsync 认为 baseAccount 可用(软删过滤器悄悄挡住了那一行),
        // ProvisionExternalUserAsync 的 InsertAsync 撞上真实唯一索引,非竞态失败原样 throw → 500。
        // 查重修复后:应正常登入(不抛异常),且新账号必须换了后缀,不能等于被占用的 baseAccount。
        var output = await sp.GetRequiredService<IAuthService>().LoginByExternalAsync(new ExternalLoginInput
        {
            ProviderCode = provider, Code = "auth-code", CodeVerifier = "verifier", Nonce = "nonce", RedirectUri = "https://app/cb",
        });
        Assert.False(string.IsNullOrEmpty(output.AccessToken));

        var binding = await sp.GetRequiredService<ISysUserExternalService>().FindByExternalAsync(provider, "sub-provdup");
        Assert.NotNull(binding);
        var provisioned = await sp.GetRequiredService<IRepository<SysUser>>()
            .AsQueryable().ClearFilter<ITenantScoped>().Where(u => u.Id == binding!.UserId).FirstAsync();
        Assert.NotNull(provisioned);
        Assert.NotEqual(baseAccount, provisioned!.Account);        // 撞了名,必须换后缀,不能是原候选
        Assert.StartsWith(baseAccount, provisioned.Account);       // 但仍是同一个种子派生出来的
    }
}
