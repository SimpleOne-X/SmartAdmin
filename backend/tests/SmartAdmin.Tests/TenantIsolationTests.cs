using System.Net.Http.Headers;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// 跨租户 HTTP 级隔离回归:两个租户各自建机构,互相看不到——即使操作者是租户内超管(数据范围天然
/// "全部")也看不穿,证明 ITenantScoped 过滤器不受机构数据范围/超管身份影响(spec §3)。
/// </summary>
public class TenantIsolationTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    private static async Task<HttpClient> NewTenantAdminClient(AdminAppFactory f, HttpClient platform, string codePrefix) =>
        (await NewTenant(f, platform, codePrefix)).Client;

    /// <summary>建一个新租户,返回它的 Id 与已登录的租户内超管 client(该租户的初始管理员 IsSuperAdmin=true)。</summary>
    private static async Task<(long TenantId, HttpClient Client)> NewTenant(AdminAppFactory f, HttpClient platform, string codePrefix)
    {
        var code = $"{codePrefix}{Guid.NewGuid():N}"[..16];
        var account = $"{code}_admin";
        const string password = "Test@123456";

        var add = await platform.PostJson("/api/v1/sys/tenant/add", new
        {
            code, name = $"{code}-Inc", isolationMode = 1, enabled = true,
            adminAccount = account, adminPassword = password,
        });
        var addEnv = await add.ReadEnvelope();
        Assert.Equal(0, addEnv.GetProperty("code").GetInt32());

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await client.LoginToken(account, password));
        return (addEnv.GetProperty("data").GetInt64(), client);
    }

    [Fact]
    public async Task Two_tenants_cannot_see_each_others_orgs()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var clientA = await NewTenantAdminClient(f, platform, "tnA");
        var clientB = await NewTenantAdminClient(f, platform, "tnB");

        var addOrg = await (await clientA.PostJson("/api/v1/sys/org/add",
            new { name = "A的机构", code = $"ORG_A_{Guid.NewGuid():N}"[..16], parentId = 0, sort = 1, enabled = true })).ReadEnvelope();
        Assert.Equal(0, addOrg.GetProperty("code").GetInt32());
        var orgAId = addOrg.GetProperty("data").GetInt64();

        // 租户 B 的超管列表里看不到租户 A 建的机构
        var listB = (await (await clientB.GetAsync("/api/v1/sys/org/list")).ReadEnvelope()).GetProperty("data");
        var idsB = listB.EnumerateArray().Select(o => o.GetProperty("id").GetInt64()).ToList();
        Assert.DoesNotContain(orgAId, idsB);

        // 直接按 Id 越权查询 → 404(OrgNotFound),不暴露"存在但无权"
        var crossGet = await (await clientB.GetAsync($"/api/v1/sys/org/{orgAId}")).ReadEnvelope();
        Assert.Equal((int)ErrorCode.OrgNotFound, crossGet.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Two_tenants_cannot_see_each_others_roles()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var clientA = await NewTenantAdminClient(f, platform, "roleA");
        var clientB = await NewTenantAdminClient(f, platform, "roleB");

        var addRole = await (await clientA.PostJson("/api/v1/sys/role/add",
            new { name = "A的角色", code = $"ROLE_A_{Guid.NewGuid():N}"[..16], sort = 1, enabled = true })).ReadEnvelope();
        Assert.Equal(0, addRole.GetProperty("code").GetInt32());
        var roleAId = addRole.GetProperty("data").GetInt64();

        var pageB = (await (await clientB.GetAsync("/api/v1/sys/role/page?Current=1&Size=100")).ReadEnvelope()).GetProperty("data");
        var idsB = pageB.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
        Assert.DoesNotContain(roleAId, idsB);
    }

    /// <summary>
    /// 租户注册表的<b>读</b>接口同样只对平台管理员开放。每个新租户的初始管理员都是租户内超管
    /// (IsSuperAdmin=true,天然绕过 [RolePermission]),门禁若只挂在写接口上,它直接 GET 就能读到
    /// 全平台每一个租户的完整信息(名称、联系人、联系电话、到期时间)——跨客户信息泄露。
    /// </summary>
    [Fact]
    public async Task Tenant_admin_cannot_read_the_tenant_registry()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var (victimTenantId, _) = await NewTenant(f, platform, "victim");   // 被窥探的那个租户
        var (_, peeker) = await NewTenant(f, platform, "peeker");           // 发起窥探的租户内超管

        var page = await (await peeker.GetAsync("/api/v1/sys/tenant/page?Current=1&Size=100")).ReadEnvelope();
        Assert.Equal((int)ErrorCode.PlatformAdminRequired, page.GetProperty("code").GetInt32());

        var get = await (await peeker.GetAsync($"/api/v1/sys/tenant/{victimTenantId}")).ReadEnvelope();
        Assert.Equal((int)ErrorCode.PlatformAdminRequired, get.GetProperty("code").GetInt32());

        // 正向对照:平台管理员照常读得到,证明上面的拒绝是门禁生效而不是接口本身坏了
        var byPlatform = await (await platform.GetAsync($"/api/v1/sys/tenant/{victimTenantId}")).ReadEnvelope();
        Assert.Equal(0, byPlatform.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// 跨租户篡改 Id 的<b>写</b>路径:租户 B 拿着租户 A 的机构 Id 直接发 PUT / DELETE。
    /// 这是本次改造最核心的攻击面——全局租户过滤器只作用于查询,按主键的 Update/Delete 靠
    /// <c>SqlSugarRepository.InScopeAsync</c> 那道写前守卫兜底;这条把它锁进 CI。
    /// <para>两个动词都必须报错而不是"成功但零行":DELETE 曾经因为服务层不看仓储返回值而回
    /// <c>{"code":0,"data":true}</c>——数据没损坏(守卫挡住了),但调用方以为删掉了,审计日志还留下一条假的
    /// "删除成功"。</para>
    /// </summary>
    [Fact]
    public async Task Tenant_admin_cannot_update_or_delete_another_tenants_org_by_id()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var clientA = await NewTenantAdminClient(f, platform, "idorA");
        var clientB = await NewTenantAdminClient(f, platform, "idorB");

        const string orgName = "A的机构";
        var addOrg = await (await clientA.PostJson("/api/v1/sys/org/add",
            new { name = orgName, code = $"ORGIDOR_{Guid.NewGuid():N}"[..16], parentId = 0, sort = 1, enabled = true })).ReadEnvelope();
        Assert.Equal(0, addOrg.GetProperty("code").GetInt32());
        var orgAId = addOrg.GetProperty("data").GetInt64();

        // 越权改:租户 B 拿着 A 的机构 Id 发 PUT(code 留空 = 不改编码)
        var put = await (await clientB.PutJson($"/api/v1/sys/org/{orgAId}",
            new { name = "被B改掉的名字", code = "", parentId = 0, sort = 99, enabled = false })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.OrgNotFound, put.GetProperty("code").GetInt32());

        // 越权删:同一个 Id 发 DELETE —— 必须同样报"查不到",不能是假成功
        var del = await (await clientB.DeleteAsync($"/api/v1/sys/org/{orgAId}")).ReadEnvelope();
        Assert.Equal((int)ErrorCode.OrgNotFound, del.GetProperty("code").GetInt32());

        // 租户 A 那一行原样健在:名字没被改、行也没被删
        var still = await (await clientA.GetAsync($"/api/v1/sys/org/{orgAId}")).ReadEnvelope();
        Assert.Equal(0, still.GetProperty("code").GetInt32());
        Assert.Equal(orgName, still.GetProperty("data").GetProperty("name").GetString());
    }
}
