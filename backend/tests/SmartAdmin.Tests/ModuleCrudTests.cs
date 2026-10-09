using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 模块/应用 CRUD 的 HTTP 级回归(多应用门户)。业务失败走统一信封 HTTP 200 + 业务码
/// (见 AdminExceptionFilter),故断言落在信封 code 上。
/// </summary>
public class ModuleCrudTests
{
    private static HttpClient WithToken(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        return WithToken(c, await c.LoginToken("superAdmin", "Test@123456"));
    }

    [Fact]
    public async Task Builtin_modules_include_the_configured_portal_metadata()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var modules = (await (await c.GetAsync("/api/v1/sys/module/list")).ReadEnvelope())
            .GetProperty("data")
            .EnumerateArray()
            .ToDictionary(module => module.GetProperty("code").GetString()!);

        var system = modules["system"];
        Assert.Equal("lucide:settings", system.GetProperty("icon").GetString());
        Assert.Equal("", system.GetProperty("defaultRoute").GetString());

        var business = modules["business"];
        Assert.Equal("lucide:briefcase-business", business.GetProperty("icon").GetString());
        Assert.Equal("", business.GetProperty("defaultRoute").GetString());
    }

    [Fact]
    public async Task SuperAdmin_can_crud_module()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        // 新增
        var add = await c.PostJson("/api/v1/sys/module/add", new { code = "crm", title = "客户管理", sort = 2, enabled = true });
        var addEnv = await add.ReadEnvelope();
        Assert.Equal(0, addEnv.GetProperty("code").GetInt32());
        var newId = addEnv.GetProperty("data").GetInt64();

        // 列表含新模块 + 内置 system
        var list = (await (await c.GetAsync("/api/v1/sys/module/list")).ReadEnvelope()).GetProperty("data");
        var ids = list.EnumerateArray().Select(m => m.GetProperty("id").GetInt64()).ToList();
        Assert.Contains(newId, ids);
        Assert.Contains(1L, ids);   // 内置 system 模块

        // 取单个
        var get = await (await c.GetAsync($"/api/v1/sys/module/{newId}")).ReadEnvelope();
        Assert.Equal("crm", get.GetProperty("data").GetProperty("code").GetString());

        // 更新
        var upd = await c.PutJson($"/api/v1/sys/module/{newId}", new { code = "crm", title = "客户管理V2", sort = 3, enabled = true });
        Assert.Equal(0, (await upd.ReadEnvelope()).GetProperty("code").GetInt32());
        var reGet = await (await c.GetAsync($"/api/v1/sys/module/{newId}")).ReadEnvelope();
        Assert.Equal("客户管理V2", reGet.GetProperty("data").GetProperty("title").GetString());

        // 删除
        Assert.Equal(0, (await (await c.DeleteAsync($"/api/v1/sys/module/{newId}")).ReadEnvelope()).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Delete_builtin_system_module_is_protected()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var del = await c.DeleteAsync("/api/v1/sys/module/1");   // 内置 system 模块
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);        // 业务失败仍是 200 信封
        Assert.Equal(42013, (await del.ReadEnvelope()).GetProperty("code").GetInt32());  // ModuleProtected
    }

    [Fact]
    public async Task Duplicate_code_is_rejected()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        Assert.Equal(0, (await (await c.PostJson("/api/v1/sys/module/add", new { code = "dup", title = "A", sort = 1, enabled = true })).ReadEnvelope()).GetProperty("code").GetInt32());
        var second = await c.PostJson("/api/v1/sys/module/add", new { code = "dup", title = "B", sort = 2, enabled = true });
        Assert.Equal(42012, (await second.ReadEnvelope()).GetProperty("code").GetInt32());  // ModuleCodeExists
    }

    /// <summary>内置 system 模块固定不可转授:传 true 也存不进去,读出来恒为 false。</summary>
    [Fact]
    public async Task Builtin_system_module_is_never_delegatable()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        var update = await c.PutJson("/api/v1/sys/module/1", new
        {
            code = "system", title = "系统", icon = "lucide:settings", defaultRoute = "", apiPrefix = "sys",
            sort = 1, enabled = true, remark = "内置系统应用,不可删除", isDelegatable = true,
        });
        Assert.Equal(0, (await update.ReadEnvelope()).GetProperty("code").GetInt32());

        var data = (await (await c.GetAsync("/api/v1/sys/module/1")).ReadEnvelope()).GetProperty("data");
        Assert.False(data.GetProperty("isDelegatable").GetBoolean());

        // 读侧会把 system 模块归一成 false,上面的读回挡不住写侧;直接查库才能证明存进去的就是显式 false
        // (Assert.False 对 bool? 的 null 同样失败)。后续按模块开关做判定的代码可能绕过服务层读侧、直接查库。
        Assert.False(await StoredDelegatableAsync(f, 1));
    }

    /// <summary>
    /// 库里 system 模块即便被直接写成 true,经服务读出来(单取与列表)仍是 false:
    /// 读侧归一是写侧之外的第二道锁,覆盖绕过 API 写库的情形。
    /// </summary>
    [Fact]
    public async Task Builtin_system_module_reads_false_even_when_database_says_true()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
            await db.Updateable<SysModule>()
                .SetColumns(x => new SysModule { IsDelegatable = true })
                .Where(x => x.Id == 1)
                .ExecuteCommandAsync();
        }
        Assert.True(await StoredDelegatableAsync(f, 1));   // 前提:库里确实是 true

        var single = (await (await c.GetAsync("/api/v1/sys/module/1")).ReadEnvelope()).GetProperty("data");
        Assert.False(single.GetProperty("isDelegatable").GetBoolean());

        var list = (await (await c.GetAsync("/api/v1/sys/module/list")).ReadEnvelope()).GetProperty("data").EnumerateArray().ToList();
        Assert.False(list.Single(m => m.GetProperty("id").GetInt64() == 1).GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>新库的种子:「系统」不可转授,「业务中心」可转授。</summary>
    [Fact]
    public async Task Fresh_seed_marks_business_module_delegatable()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        var list = (await (await c.GetAsync("/api/v1/sys/module/list")).ReadEnvelope()).GetProperty("data").EnumerateArray().ToList();
        Assert.False(list.Single(m => m.GetProperty("id").GetInt64() == 1).GetProperty("isDelegatable").GetBoolean());
        Assert.True(list.Single(m => m.GetProperty("id").GetInt64() == 2).GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>新建时不带开关 = 不可转授(null);之后能打开。</summary>
    [Fact]
    public async Task New_module_without_flag_is_not_delegatable_until_turned_on()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        var id = (await (await c.PostJson("/api/v1/sys/module/add", new { code = "crm", title = "客户", sort = 5, enabled = true })).ReadEnvelope())
            .GetProperty("data").GetInt64();
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            (await (await c.GetAsync($"/api/v1/sys/module/{id}")).ReadEnvelope()).GetProperty("data").GetProperty("isDelegatable").ValueKind);

        await c.PutJson($"/api/v1/sys/module/{id}", new { code = "crm", title = "客户", sort = 5, enabled = true, isDelegatable = true });
        Assert.True((await (await c.GetAsync($"/api/v1/sys/module/{id}")).ReadEnvelope()).GetProperty("data").GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>更新请求不带 isDelegatable(老前端 / 自建管理页)时保持原值,不把它清成不可转授。</summary>
    [Fact]
    public async Task Update_without_flag_keeps_existing_value()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        await c.PutJson("/api/v1/sys/module/2", new
        {
            code = "business", title = "业务中心(改)", icon = "lucide:briefcase-business", defaultRoute = "", apiPrefix = "biz",
            sort = 2, enabled = true, remark = "示例业务应用(可删除)",
        });
        var data = (await (await c.GetAsync("/api/v1/sys/module/2")).ReadEnvelope()).GetProperty("data");
        Assert.Equal("业务中心(改)", data.GetProperty("title").GetString());
        Assert.True(data.GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>
    /// 显式 false 走更新路径能存进去(把可转授关掉),之后不带该字段的更新也不会把它重新打开;
    /// 只有显式 true 才放行,null 只表示「保持原值」。
    /// </summary>
    [Fact]
    public async Task Explicit_false_is_saved_and_later_update_without_flag_keeps_it()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);

        var turnOff = await c.PutJson("/api/v1/sys/module/2", new
        {
            code = "business", title = "业务中心", icon = "lucide:briefcase-business", defaultRoute = "", apiPrefix = "biz",
            sort = 2, enabled = true, remark = "示例业务应用(可删除)", isDelegatable = false,
        });
        Assert.Equal(0, (await turnOff.ReadEnvelope()).GetProperty("code").GetInt32());
        var afterOff = (await (await c.GetAsync("/api/v1/sys/module/2")).ReadEnvelope()).GetProperty("data");
        Assert.False(afterOff.GetProperty("isDelegatable").GetBoolean());
        Assert.False(await StoredDelegatableAsync(f, 2));

        var withoutFlag = await c.PutJson("/api/v1/sys/module/2", new
        {
            code = "business", title = "业务中心(改)", icon = "lucide:briefcase-business", defaultRoute = "", apiPrefix = "biz",
            sort = 2, enabled = true, remark = "示例业务应用(可删除)",
        });
        Assert.Equal(0, (await withoutFlag.ReadEnvelope()).GetProperty("code").GetInt32());
        var afterKeep = (await (await c.GetAsync("/api/v1/sys/module/2")).ReadEnvelope()).GetProperty("data");
        Assert.Equal("业务中心(改)", afterKeep.GetProperty("title").GetString());   // 这次更新确实生效了
        Assert.False(afterKeep.GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>直接查库里某模块的 IsDelegatable 原值,不经 <c>ModuleService</c> 的读侧归一。</summary>
    private static async Task<bool?> StoredDelegatableAsync(AdminAppFactory f, long moduleId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        return (await db.Queryable<SysModule>().FirstAsync(x => x.Id == moduleId)).IsDelegatable;
    }
}
