using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 编码唯一性是<b>租户内</b>唯一,不是全局唯一:sys_org / sys_role / sys_position 的
/// <c>idx_sys_*_code</c> 由单列 <c>Code</c> 改成复合 <c>(TenantId, Code)</c>。
/// <para>起因是一条真实的跨租户 500:租户 A 建了 Code="HQ" 的机构,租户 B 再建同码机构时,
/// 应用层查重是租户内的(过滤器生效)→ 放行 → 撞上库里<b>全局</b>唯一索引 → 裸 .NET 异常栈。
/// 两个客户各有一个 "HQ" 是常态,不是边界情况。</para>
/// <para>本文件两组用例:<br/>
/// (1) 升级路径 —— 老库(旧索引形状 + 已有数据)二次启动后必须真的换成复合索引。光改
/// <c>[SugarIndex]</c> 属性不够:<c>InitTables</c> 只按索引名判存在,同名索引已在库里就整个跳过,
/// 对已建过表的库是静默空操作。真正干活的是
/// <c>DatabaseInitializer.DropReshapedIndexes</c> 的"按名先删再让 InitTables 重建"。<br/>
/// (2) HTTP 级往返 —— 三个实体各跑一遍"两租户同码都成功 + 同租户同码仍是业务错误",
/// 证明修掉跨租户那条的同时没有放松租户内那条。</para>
/// </summary>
public class TenantScopedCodeUniquenessTests
{
    // ── (1) 升级路径:老库换索引形状 ────────────────────────────────────────────────────────────

    /// <summary>租户 A 的租户号(库里不必真有这个租户行,唯一索引只看列值)。</summary>
    private const long LegacyTenantA = 900_001L;

    /// <summary>租户 B 的租户号。</summary>
    private const long LegacyTenantB = 900_002L;

    /// <summary>两个租户都想用的那个普通编码。</summary>
    private const string SharedCode = "HQ";

    /// <summary>
    /// 两次启动:第一次建好库后把 sys_org 退回<b>修复前</b>的索引形状(单列 Code 全局唯一)并写入一行真实数据,
    /// 第二次启动跑迁移。判据是行为而不是元数据:换租户同码要能插进去,同租户同码仍要被库拦住。
    /// <para>本次修复落地后,代码里已经没有"旧形状"的实体可以启动了,所以旧形状只能在第一次启动之后用
    /// DbMaintenance 手工还原——与 <see cref="CodeFirstNullableUpgradeTests"/> 用 DropColumn 还原"补列前老库"同一个成法。</para>
    /// </summary>
    [Fact]
    public async Task Legacy_global_unique_code_index_is_reshaped_to_tenant_scoped_on_upgrade()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-tenant-code-idx-{Guid.NewGuid():N}.db");

        try
        {
            // 1) 正常首启(从零建表,不拿模板副本):此时 sys_org 上已经是新的复合索引
            using (var v1 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false, FreshDatabase = true })
            {
                _ = v1.CreateClient();
                using var scope = v1.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

                // 2) 退化成「本次修复之前的老库」:单列 Code 全局唯一
                //    (种子机构的 Code 本就互不相同,这一步不会撞约束)
                db.DbMaintenance.DropIndex("idx_sys_org_code", "sys_org");
                db.DbMaintenance.CreateIndex("sys_org", ["Code"], "idx_sys_org_code", true);

                // 3) 老库上已有一行真实数据:租户 A 的 "HQ"。迁移必须在非空表上成立,不是空库上的建表
                //    (TenantId 显式给值,插入 AOP 只在它为 null 时才回填,这里不会被覆盖)
                await db.Insertable(new SysOrg
                {
                    ParentId = 0, Name = "A 的总部", Code = SharedCode, Sort = 1, Enabled = true, TenantId = LegacyTenantA,
                }).ExecuteCommandAsync();
            }

            // 4) 同库二次启动:DropReshapedIndexes 按名删掉旧索引 → InitTables 按复合形状重建
            using var v2 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false };
            _ = v2.CreateClient();
            using var s2 = v2.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<ISqlSugarClient>();

            // 5a) 换个租户、同一个编码 → 必须插得进去(旧的全局唯一约束确实没了)
            var crossTenant = await db2.Insertable(new SysOrg
            {
                ParentId = 0, Name = "B 的总部", Code = SharedCode, Sort = 1, Enabled = true, TenantId = LegacyTenantB,
            }).ExecuteCommandAsync();
            Assert.Equal(1, crossTenant);

            // 5b) 同一个租户、同一个编码 → 仍必须被库拦住(复合唯一索引真的建上了,不是把约束整个丢了)
            var sameTenant = await Record.ExceptionAsync(() => db2.Insertable(new SysOrg
            {
                ParentId = 0, Name = "A 的第二个总部", Code = SharedCode, Sort = 2, Enabled = true, TenantId = LegacyTenantA,
            }).ExecuteCommandAsync());
            Assert.NotNull(sameTenant);
        }
        finally
        {
            TestDb.Cleanup(dbPath, dbPath);
        }
    }

    // ── (2) HTTP 级往返:两租户同码 ────────────────────────────────────────────────────────────

    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    /// <summary>建一个新租户,返回它已登录的租户内超管 client(该租户的初始管理员 IsSuperAdmin=true)。</summary>
    private static async Task<HttpClient> NewTenantAdminClient(AdminAppFactory f, HttpClient platform, string codePrefix)
    {
        var code = $"{codePrefix}{Guid.NewGuid():N}"[..16];
        var account = $"{code}_admin";
        const string password = "Test@123456";

        var add = await (await platform.PostJson("/api/v1/sys/tenant/add", new
        {
            code, name = $"{code}-Inc", isolationMode = 1, enabled = true,
            adminAccount = account, adminPassword = password,
        })).ReadEnvelope();
        Assert.Equal(0, add.GetProperty("code").GetInt32());

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await client.LoginToken(account, password));
        return client;
    }

    [Fact]
    public async Task Two_tenants_can_create_orgs_with_the_same_code()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);
        var a = await NewTenantAdminClient(f, platform, "orgDupA");
        var b = await NewTenantAdminClient(f, platform, "orgDupB");

        object Body(string name) => new { name, code = SharedCode, parentId = 0, sort = 1, enabled = true };

        var addA = await (await a.PostJson("/api/v1/sys/org/add", Body("A 的总部"))).ReadEnvelope();
        Assert.Equal(0, addA.GetProperty("code").GetInt32());

        // 修复前这一发就是裸 500(唯一约束冲突冒到 ExceptionFilter 之外)
        var addB = await (await b.PostJson("/api/v1/sys/org/add", Body("B 的总部"))).ReadEnvelope();
        Assert.Equal(0, addB.GetProperty("code").GetInt32());

        // 反向对照:同租户内同码仍是原来的业务错误,不是裸异常,也不是"静默成功"
        var dup = await (await a.PostJson("/api/v1/sys/org/add", Body("A 的第二个总部"))).ReadEnvelope();
        Assert.Equal((int)ErrorCode.OrgCodeExists, dup.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Two_tenants_can_create_roles_with_the_same_code()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);
        var a = await NewTenantAdminClient(f, platform, "roleDupA");
        var b = await NewTenantAdminClient(f, platform, "roleDupB");

        object Body(string name) => new { name, code = "manager", sort = 1, enabled = true };

        var addA = await (await a.PostJson("/api/v1/sys/role/add", Body("A 的经理"))).ReadEnvelope();
        Assert.Equal(0, addA.GetProperty("code").GetInt32());

        var addB = await (await b.PostJson("/api/v1/sys/role/add", Body("B 的经理"))).ReadEnvelope();
        Assert.Equal(0, addB.GetProperty("code").GetInt32());

        var dup = await (await a.PostJson("/api/v1/sys/role/add", Body("A 的第二个经理"))).ReadEnvelope();
        Assert.Equal((int)ErrorCode.RoleCodeExists, dup.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Two_tenants_can_create_positions_with_the_same_code()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);
        var a = await NewTenantAdminClient(f, platform, "posDupA");
        var b = await NewTenantAdminClient(f, platform, "posDupB");

        // 注意别撞种子职位的编码(gm / vp / dm …):那是同租户内的既有行,会命中租户内查重
        object Body(string name) => new { name, code = "shift_lead", sort = 1, enabled = true };

        var addA = await (await a.PostJson("/api/v1/sys/position/add", Body("A 的值班组长"))).ReadEnvelope();
        Assert.Equal(0, addA.GetProperty("code").GetInt32());

        var addB = await (await b.PostJson("/api/v1/sys/position/add", Body("B 的值班组长"))).ReadEnvelope();
        Assert.Equal(0, addB.GetProperty("code").GetInt32());

        var dup = await (await a.PostJson("/api/v1/sys/position/add", Body("A 的第二个值班组长"))).ReadEnvelope();
        Assert.Equal((int)ErrorCode.PositionCodeExists, dup.GetProperty("code").GetInt32());
    }
}
