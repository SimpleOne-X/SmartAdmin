using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 超管账号对其他管理员不可见,也不可经 Id 操作:即使对方持有「用户管理」的全部按钮、数据范围还是「全部」。
/// 列表、详情、导出只是看不见;更新、重置密码、启停、删除是能不能被接管,后者才是越权面——
/// 重置超管密码再以超管登录,就是把一个只有用户管理权限的账号变成最高权限。
/// 超管自己照常能看、能管;无登录上下文(种子、后台任务)视为可信,不受限(同数据范围守卫的约定)。
/// </summary>
public class SuperAdminHiddenFromAdminsTests
{
    private const string SuperPassword = "Test@123456";

    /// <summary>「组织管理」目录 + 「用户管理」页面 + 它的全部按钮(含重置密码、启停、授权菜单)。</summary>
    private static readonly long[] UserManagementMenus = [200, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239];

    /// <summary>普通管理员:持有用户管理的全部按钮,数据范围「全部」(最宽,正是超管最容易漏出去的那一档)。</summary>
    private static async Task<HttpClient> UserManagerAsync(AdminAppFactory f)
    {
        var role = await GrantTestKit.CreateRoleAsync(f, UserManagementMenus, DataScopeType.All);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        return await GrantTestKit.LoginAsync(f, account);
    }

    private static async Task<int> CodeAsync(HttpResponseMessage r) => (await r.ReadEnvelope()).GetProperty("code").GetInt32();

    private static async Task<long[]> ListedIdsAsync(HttpClient c)
    {
        var j = await (await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=100")).ReadEnvelope();
        Assert.Equal(0, j.GetProperty("code").GetInt32());
        return [.. j.GetProperty("data").GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt64())];
    }

    [Fact]
    public async Task User_list_hides_super_admin_from_non_super_admin_but_super_admin_sees_self()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var manager = await UserManagerAsync(f);
        var super = await GrantTestKit.SuperAdminAsync(f);

        Assert.DoesNotContain(superId, await ListedIdsAsync(manager));
        Assert.Contains(superId, await ListedIdsAsync(super));
    }

    /// <summary>按账号精确搜超管也搜不到:过滤在查询骨架里,不是前端的显示层。</summary>
    [Fact]
    public async Task Searching_by_super_admin_account_finds_nothing_for_non_super_admin()
    {
        using var f = new AdminAppFactory();
        var manager = await UserManagerAsync(f);

        var j = await (await manager.GetAsync("/api/v1/sys/user/page?Current=1&Size=20&Account=superAdmin")).ReadEnvelope();

        Assert.Equal(0, j.GetProperty("data").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task User_detail_of_super_admin_is_not_found_for_non_super_admin()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var manager = await UserManagerAsync(f);
        var super = await GrantTestKit.SuperAdminAsync(f);

        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(await manager.GetAsync($"/api/v1/sys/user/{superId}")));
        Assert.Equal(0, await CodeAsync(await super.GetAsync($"/api/v1/sys/user/{superId}")));
    }

    /// <summary>核心越权面:重置超管密码。被拒,而且超管原密码照常能登录。</summary>
    [Fact]
    public async Task Non_super_admin_cannot_reset_super_admin_password()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var manager = await UserManagerAsync(f);

        var r = await manager.PutJson($"/api/v1/sys/user/{superId}/password", new { newPassword = "Hijack@123456" });

        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(r));
        var login = await f.CreateClient().PostJson("/api/v1/auth/login", new { account = "superAdmin", password = "Hijack@123456" });
        Assert.NotEqual(0, await CodeAsync(login));   // 新口令没生效
        _ = await GrantTestKit.SuperAdminAsync(f);    // 原口令仍可登录(登录失败会抛)
    }

    [Fact]
    public async Task Non_super_admin_cannot_update_disable_or_delete_super_admin()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var manager = await UserManagerAsync(f);

        var update = await manager.PutJson($"/api/v1/sys/user/{superId}", new { name = "Hacked", enabled = true, roleIds = Array.Empty<long>() });
        var disable = await manager.PutJson($"/api/v1/sys/user/{superId}/enabled", new { enabled = false });
        var delete = await manager.DeleteAsync($"/api/v1/sys/user/{superId}");
        var batch = await manager.PostJson("/api/v1/sys/user/batch-delete", new { ids = new[] { superId } });

        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(update));
        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(disable));
        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(delete));
        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(batch));

        using var s = f.Services.CreateScope();
        var row = await s.ServiceProvider.GetRequiredService<IRepository<SysUser>>().GetByIdAsync(superId);
        Assert.NotNull(row);
        Assert.Equal("超级管理员", row!.Name);   // 一个字段都没被改
        Assert.True(row.Enabled);
    }

    /// <summary>批量删除里夹带超管:整批拒绝,同批里的普通用户也一个不删(与既有「含超管则整体拒绝」同语义)。</summary>
    [Fact]
    public async Task Batch_delete_containing_super_admin_deletes_nobody_for_non_super_admin()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var manager = await UserManagerAsync(f);
        var (victim, _) = await GrantTestKit.CreateUserAsync(f, []);

        var batch = await manager.PostJson("/api/v1/sys/user/batch-delete", new { ids = new[] { victim, superId } });

        Assert.Equal((int)ErrorCode.UserNotFound, await CodeAsync(batch));
        Assert.Contains(victim, await ListedIdsAsync(manager));
    }

    /// <summary>单独授权的读接口也不能绕过去:超管不在非超管的可见范围内。</summary>
    [Fact]
    public async Task User_menu_grant_reads_of_super_admin_are_out_of_scope_for_non_super_admin()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var manager = await UserManagerAsync(f);

        var effective = await manager.GetAsync($"/api/v1/sys/user/{superId}/menus/effective");
        var grants = await manager.GetAsync($"/api/v1/sys/user/{superId}/menus");

        Assert.Equal((int)ErrorCode.UserOutOfDataScope, await CodeAsync(effective));
        Assert.Equal((int)ErrorCode.UserOutOfDataScope, await CodeAsync(grants));
    }

    /// <summary>
    /// 强退的「非超管不得踢超管」不能因数据范围是「全部」而被跳过:否则「全部」范围的普通管理员能把超管踢下线。
    /// 目标会话当作不存在(<see cref="ErrorCode.SessionNotFound"/>),超管的会话原样在线。
    /// </summary>
    [Fact]
    public async Task Non_super_admin_with_all_scope_cannot_force_logout_super_admin()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        // 日志审计(500)→ 在线会话页(540)→ 查询(541)、强制下线(545)
        var role = await GrantTestKit.CreateRoleAsync(f, [500, 540, 541, 545], DataScopeType.All);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        var manager = await GrantTestKit.LoginAsync(f, account);
        async Task<string[]> OnlineSessionsOfSuperAdminAsync()
        {
            var j = await (await super.GetAsync("/api/v1/sys/session/online?Current=1&Size=50")).ReadEnvelope();
            return [.. j.GetProperty("data").GetProperty("items").EnumerateArray()
                .Where(x => x.GetProperty("account").GetString() == "superAdmin")
                .Select(x => x.GetProperty("sessionId").GetString()!)];
        }
        var superSession = Assert.Single(await OnlineSessionsOfSuperAdminAsync());

        var kick = await manager.DeleteAsync($"/api/v1/sys/session/{superSession}");

        Assert.Equal((int)ErrorCode.SessionNotFound, await CodeAsync(kick));
        Assert.Equal([superSession], await OnlineSessionsOfSuperAdminAsync());   // 超管的会话还在线
    }

    /// <summary>超管自己不受影响:能看、能改资料、能给别人重置密码。</summary>
    [Fact]
    public async Task Super_admin_can_still_manage_users_normally()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        var reset = await super.PutJson($"/api/v1/sys/user/{target}/password", new { newPassword = "Reset@123456" });

        Assert.Equal(0, await CodeAsync(reset));
    }

    /// <summary>无登录上下文(种子、后台任务)视为可信:服务层照常读得到超管。</summary>
    [Fact]
    public async Task Background_code_without_login_still_sees_super_admin()
    {
        using var f = new AdminAppFactory();
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        using var s = f.Services.CreateScope();

        var detail = await s.ServiceProvider.GetRequiredService<IUserService>().GetAsync(superId);

        Assert.True(detail.IsSuperAdmin);
    }
}
