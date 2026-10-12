using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 机构 / 岗位 / 角色 / 用户的 <c>options</c> 只读端点:选择器的数据源挂 <c>[ActiveSession]</c>,
/// 没有任何系统菜单的角色也能用,同时完整的 list/page 仍锁在各页的查询权限后面。
/// 用户 options 只回 id / 姓名 / 账号三个字段,范围与用户分页一致(含超管对非超管不可见)。
/// </summary>
public class SysOptionsEndpointTests
{
    private static readonly string[] OptionUrls =
    [
        "/api/v1/sys/org/options",
        "/api/v1/sys/position/options",
        "/api/v1/sys/role/options",
        "/api/v1/sys/user/options",
    ];

    private static readonly string[] FullListUrls =
    [
        "/api/v1/sys/org/list",
        "/api/v1/sys/position/page?Current=1&Size=10",
        "/api/v1/sys/role/page?Current=1&Size=10",
        "/api/v1/sys/user/page?Current=1&Size=10",
    ];

    /// <summary>没有任何菜单的普通用户,数据范围「全部」(最宽,方便专门盯住 options 本身的行为)。</summary>
    private static async Task<HttpClient> NoMenuUserAsync(AdminAppFactory f)
    {
        var role = await GrantTestKit.CreateRoleAsync(f, [], DataScopeType.All);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        return await GrantTestKit.LoginAsync(f, account);
    }

    private static async Task<long> AddUserAsync(
        AdminAppFactory f, string account, string name, long? orgId = null, bool enabled = true)
    {
        using var s = f.Services.CreateScope();
        var output = await s.ServiceProvider.GetRequiredService<IUserService>().AddAsync(new AddUserInput
        {
            Account = account, Password = GrantTestKit.Password, Name = name, Enabled = enabled, OrgId = orgId,
        });
        return output.Id;
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> DataAsync(HttpClient c, string url)
    {
        var env = await (await c.GetAsync(url)).ReadEnvelope();
        Assert.Equal(0, env.GetProperty("code").GetInt32());
        return env.GetProperty("data");
    }

    private static async Task<List<JsonElement>> UserItemsAsync(HttpClient c, string query = "")
    {
        var data = await DataAsync(c, "/api/v1/sys/user/options?" + query);
        return [.. data.GetProperty("items").EnumerateArray()];
    }

    private static string[] Accounts(IEnumerable<JsonElement> items) =>
        [.. items.Select(x => x.GetProperty("account").GetString()!)];

    [Theory]
    [InlineData("/api/v1/sys/org/options")]
    [InlineData("/api/v1/sys/position/options")]
    [InlineData("/api/v1/sys/role/options")]
    [InlineData("/api/v1/sys/user/options")]
    public async Task Anonymous_calls_are_401(string url)
    {
        using var f = new AdminAppFactory();

        var r = await f.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    /// <summary>核心场景:无任何系统菜单的角色,四个 options 全通,四个完整查询仍是 403 + 41001。</summary>
    [Fact]
    public async Task Role_without_menus_reads_options_but_not_the_full_lists()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);

        foreach (var url in OptionUrls)
        {
            var r = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(0, (await r.ReadEnvelope()).GetProperty("code").GetInt32());
        }

        foreach (var url in FullListUrls)
        {
            var r = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal(41001, (await r.ReadEnvelope()).GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task Options_routes_are_not_in_the_grantable_route_list()
    {
        using var f = new AdminAppFactory();
        var admin = await GrantTestKit.SuperAdminAsync(f);

        var codes = (await DataAsync(admin, "/api/v1/sys/menu/routes")).EnumerateArray()
            .Select(x => x.GetProperty("code").GetString()!).ToList();

        foreach (var url in OptionUrls)
            Assert.DoesNotContain("GET:" + url, codes);
    }

    [Fact]
    public async Task User_options_returns_only_id_name_and_account()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        await AddUserAsync(f, Unique("opt-f-"), "字段检查");

        var env = await (await c.GetAsync("/api/v1/sys/user/options?Current=1&Size=100")).ReadEnvelope();
        var items = env.GetProperty("data").GetProperty("items").EnumerateArray().ToList();

        Assert.NotEmpty(items);
        foreach (var item in items)
            Assert.Equal(["account", "id", "name"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());

        var raw = env.GetRawText().ToLowerInvariant();
        foreach (var leaked in new[] { "phone", "email", "avatar", "password", "issuperadmin", "orgid", "nickname" })
            Assert.DoesNotContain(leaked, raw);
    }

    [Fact]
    public async Task User_options_keyword_matches_name_or_account()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        var token = Unique("kw");
        var byName = Unique("opt-n-");
        var byAccount = $"opt-{token}-a";
        var other = Unique("opt-o-");
        await AddUserAsync(f, byName, "姓名含" + token);
        await AddUserAsync(f, byAccount, "账号命中者");
        await AddUserAsync(f, other, "无关者");

        var accounts = Accounts(await UserItemsAsync(c, $"Keyword={token}&Current=1&Size=100"));

        Assert.Contains(byName, accounts);
        Assert.Contains(byAccount, accounts);
        Assert.DoesNotContain(other, accounts);
    }

    [Fact]
    public async Task User_options_filters_by_org_and_pages()
    {
        const long org = 940101;
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        var inOrg = new[] { Unique("opt-i1-"), Unique("opt-i2-"), Unique("opt-i3-") };
        foreach (var account in inOrg) await AddUserAsync(f, account, "同机构", org);
        var outside = Unique("opt-x-");
        await AddUserAsync(f, outside, "别的机构", org + 1);

        var data = await DataAsync(c, $"/api/v1/sys/user/options?OrgId={org}&Current=2&Size=2");

        Assert.Equal(3, data.GetProperty("total").GetInt32());
        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Contains(Accounts(items).Single(), inOrg);
        Assert.DoesNotContain(outside, Accounts(await UserItemsAsync(c, $"OrgId={org}&Current=1&Size=100")));
    }

    [Fact]
    public async Task User_options_excludes_disabled_users()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        var token = Unique("dis");
        var enabled = $"opt-{token}-on";
        var disabled = $"opt-{token}-off";
        await AddUserAsync(f, enabled, "启用者");
        await AddUserAsync(f, disabled, "停用者", enabled: false);

        var accounts = Accounts(await UserItemsAsync(c, $"Keyword={token}&Current=1&Size=100"));

        Assert.Contains(enabled, accounts);
        Assert.DoesNotContain(disabled, accounts);
    }

    /// <summary>超限抛 48001 而不是静默截断(与内核其余分页一致)。</summary>
    [Fact]
    public async Task User_options_page_size_over_the_limit_is_rejected()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);

        var env = await (await c.GetAsync("/api/v1/sys/user/options?Current=1&Size=10001")).ReadEnvelope();

        Assert.Equal((int)ErrorCode.PageSizeExceeded, env.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// options 不接受客户端排序:分页入参里没有 SortField,否则无权限的人可以按手机号、密码哈希这类列排序,
    /// 从排序结果反推出它们。多传 SortField 要被忽略,不报错也不生效。
    /// </summary>
    [Fact]
    public async Task User_options_ignores_client_sort_fields()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);

        var plain = Accounts(await UserItemsAsync(c, "Current=1&Size=100"));
        var sorted = Accounts(await UserItemsAsync(c, "Current=1&Size=100&SortField=password&SortOrder=desc"));

        Assert.Equal(plain, sorted);
    }

    [Fact]
    public async Task User_options_hides_super_admin_from_non_super_admin_but_not_from_super_admin()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        var admin = await GrantTestKit.SuperAdminAsync(f);

        Assert.DoesNotContain("superAdmin", Accounts(await UserItemsAsync(c, "Keyword=superAdmin&Current=1&Size=100")));
        Assert.Contains("superAdmin", Accounts(await UserItemsAsync(admin, "Keyword=superAdmin&Current=1&Size=100")));
    }

    /// <summary>数据范围与用户分页同一套:「本机构」只看得到本机构的人,keyword 与范围是「且」的关系。</summary>
    [Fact]
    public async Task User_options_follow_the_callers_data_scope()
    {
        const long orgTech = 3;   // 技术部(种子机构)
        const long orgHr = 7;     // 人事部(种子机构)
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [], DataScopeType.Org);
        var (_, callerAccount) = await GrantTestKit.CreateUserAsync(f, [role], orgTech);
        var token = Unique("sc");
        var tech = $"opt-{token}-tech";
        var hr = $"opt-{token}-hr";
        await AddUserAsync(f, tech, "技术部同事", orgTech);
        await AddUserAsync(f, hr, "人事部同事", orgHr);
        var c = await GrantTestKit.LoginAsync(f, callerAccount);
        var admin = await GrantTestKit.SuperAdminAsync(f);

        var scoped = Accounts(await UserItemsAsync(c, $"Keyword={token}&Current=1&Size=100"));
        var all = Accounts(await UserItemsAsync(admin, $"Keyword={token}&Current=1&Size=100"));

        Assert.Equal([tech], scoped);
        Assert.Contains(tech, all);
        Assert.Contains(hr, all);
    }

    [Fact]
    public async Task Org_options_are_flat_id_parentId_name_in_list_order()
    {
        using var f = new AdminAppFactory();
        var admin = await GrantTestKit.SuperAdminAsync(f);

        var options = (await DataAsync(admin, "/api/v1/sys/org/options")).EnumerateArray().ToList();
        var list = (await DataAsync(admin, "/api/v1/sys/org/list")).EnumerateArray().ToList();

        Assert.NotEmpty(options);
        foreach (var o in options)
            Assert.Equal(["id", "name", "parentId"], o.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(
            list.Select(o => o.GetProperty("id").GetInt64()),
            options.Select(o => o.GetProperty("id").GetInt64()));
    }

    /// <summary>范围与 org/list 完全一致:范围内机构加祖先链,同级的其它机构不出现。</summary>
    [Fact]
    public async Task Org_options_follow_the_callers_data_scope_like_the_list()
    {
        const long orgTech = 3;
        const long orgHr = 7;
        using var f = new AdminAppFactory();
        // 211 = 机构-查询:同一个用户既能调 list 又能调 options,便于逐项对比
        var role = await GrantTestKit.CreateRoleAsync(f, [211], DataScopeType.Org);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role], orgTech);
        var c = await GrantTestKit.LoginAsync(f, account);

        var options = (await DataAsync(c, "/api/v1/sys/org/options")).EnumerateArray()
            .Select(o => o.GetProperty("id").GetInt64()).ToList();
        var list = (await DataAsync(c, "/api/v1/sys/org/list")).EnumerateArray()
            .Select(o => o.GetProperty("id").GetInt64()).ToList();

        Assert.Equal(list, options);
        Assert.Contains(orgTech, options);
        Assert.Contains(1L, options);
        Assert.DoesNotContain(orgHr, options);
    }

    [Fact]
    public async Task Position_options_return_id_and_name_in_sort_order()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        string first = Unique("先"), second = Unique("后");
        using (var s = f.Services.CreateScope())
        {
            var positions = s.ServiceProvider.GetRequiredService<IPositionService>();
            await positions.AddAsync(new PositionInput { Name = second, Code = Unique("p-"), Sort = -900 });
            await positions.AddAsync(new PositionInput { Name = first, Code = Unique("p-"), Sort = -1000 });
        }

        var items = (await DataAsync(c, "/api/v1/sys/position/options")).EnumerateArray().ToList();

        foreach (var item in items)
            Assert.Equal(["id", "name"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var names = items.Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal(first, names[0]);
        Assert.Equal(second, names[1]);
    }

    [Fact]
    public async Task Role_options_return_id_and_name_of_enabled_roles_only()
    {
        using var f = new AdminAppFactory();
        var c = await NoMenuUserAsync(f);
        string on = Unique("启用角色"), off = Unique("停用角色");
        using (var s = f.Services.CreateScope())
        {
            var roles = s.ServiceProvider.GetRequiredService<IRepository<SysRole>>();
            await roles.InsertAsync(new SysRole { Name = on, Code = Unique("r-"), Enabled = true });
            await roles.InsertAsync(new SysRole { Name = off, Code = Unique("r-"), Enabled = false });
        }

        var items = (await DataAsync(c, "/api/v1/sys/role/options")).EnumerateArray().ToList();

        foreach (var item in items)
            Assert.Equal(["id", "name"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var names = items.Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains(on, names);
        Assert.DoesNotContain(off, names);
    }
}
