using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 授权相关集成测试的公共搭建:建角色、建用户、登录,读门户三件套(权限码 / 模块 / 菜单树)。
/// 编码与账号用随机 Guid 截断而不是 v7:v7 的前几位是时间戳,同一毫秒附近截出来会撞唯一索引。
/// </summary>
internal static class GrantTestKit
{
    public const string Password = "Grant@123456";

    public static HttpClient WithToken(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<HttpClient> LoginAsync(AdminAppFactory f, string account, string password = Password)
    {
        var c = f.CreateClient();
        return WithToken(c, await c.LoginToken(account, password));
    }

    public static Task<HttpClient> SuperAdminAsync(AdminAppFactory f) => LoginAsync(f, "superAdmin", "Test@123456");

    /// <summary>建一个启用的角色并授菜单;给了 <paramref name="scope"/> 就顺带配数据范围。</summary>
    public static async Task<long> CreateRoleAsync(
        AdminAppFactory f,
        IReadOnlyCollection<long> menuIds,
        DataScopeType? scope = null,
        IReadOnlyCollection<long>? customOrgIds = null)
    {
        using var s = f.Services.CreateScope();
        var sp = s.ServiceProvider;
        var role = new SysRole { Name = "授权测试角色", Code = "grant-" + Guid.NewGuid().ToString("N")[..10], Enabled = true };
        await sp.GetRequiredService<IRepository<SysRole>>().InsertAsync(role);
        var rbac = sp.GetRequiredService<IRbacService>();
        if (menuIds.Count > 0) await rbac.SetRoleMenusAsync(role.Id, menuIds);
        if (scope is { } type) await rbac.SetRoleDataScopeAsync(role.Id, type, customOrgIds);
        return role.Id;
    }

    /// <summary>建一个启用的用户;<paramref name="roleIds"/> 为空即「没有任何角色」。</summary>
    public static async Task<(long Id, string Account)> CreateUserAsync(
        AdminAppFactory f, IReadOnlyCollection<long> roleIds, long? orgId = null)
    {
        using var s = f.Services.CreateScope();
        var account = "grant-" + Guid.NewGuid().ToString("N")[..10];
        var output = await s.ServiceProvider.GetRequiredService<IUserService>().AddAsync(new AddUserInput
        {
            Account = account, Password = Password, Name = "授权测试用户", Enabled = true, OrgId = orgId, RoleIds = [.. roleIds],
        });
        return (output.Id, account);
    }

    /// <summary>当前登录用户的有效权限码(<c>/personal/permissions</c>)。</summary>
    public static async Task<string[]> CodesAsync(HttpClient c) =>
        [.. (await (await c.GetAsync("/api/v1/personal/permissions")).ReadEnvelope())
            .GetProperty("data").EnumerateArray().Select(x => x.GetString()!)];

    /// <summary>当前登录用户可进的模块 Id(<c>/personal/modules</c>)。</summary>
    public static async Task<long[]> ModuleIdsAsync(HttpClient c) =>
        [.. (await (await c.GetAsync("/api/v1/personal/modules")).ReadEnvelope())
            .GetProperty("data").GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetInt64())];

    /// <summary>某模块下的门户菜单树,拍平成「节点 Id → 子节点 Id」;键 0 是根节点列表。</summary>
    public static async Task<Dictionary<long, long[]>> MenuTreeAsync(HttpClient c, long moduleId)
    {
        var data = (await (await c.GetAsync($"/api/v1/personal/menu?moduleId={moduleId}")).ReadEnvelope()).GetProperty("data");
        var map = new Dictionary<long, long[]> { [0] = [.. data.EnumerateArray().Select(n => n.GetProperty("id").GetInt64())] };
        Walk(data);
        return map;

        void Walk(JsonElement nodes)
        {
            foreach (var n in nodes.EnumerateArray())
            {
                var children = n.GetProperty("children");
                map[n.GetProperty("id").GetInt64()] = [.. children.EnumerateArray().Select(x => x.GetProperty("id").GetInt64())];
                Walk(children);
            }
        }
    }

    /// <summary>绕过服务直接写一条单独授权,并失效该用户的权限码缓存与门户代际。</summary>
    public static async Task InsertGrantAsync(AdminAppFactory f, long userId, long menuId, UserMenuEffect effect, DateTime? expireTime = null)
    {
        using (var s = f.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>()
                .InsertAsync(new SysUserMenu { UserId = userId, MenuId = menuId, Effect = effect, ExpireTime = expireTime });
        await ResetUserCachesAsync(f, userId);
    }

    /// <summary>绕过服务直接删一条单独授权,并失效缓存。</summary>
    public static async Task DeleteGrantAsync(AdminAppFactory f, long userId, long menuId)
    {
        using (var s = f.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>().Db
                .Deleteable<SysUserMenu>().Where(g => g.UserId == userId && g.MenuId == menuId).ExecuteCommandAsync();
        await ResetUserCachesAsync(f, userId);
    }

    /// <summary>直接改库后补做服务层会做的失效:该用户的权限码缓存 + 门户代际。</summary>
    public static async Task ResetUserCachesAsync(AdminAppFactory f, long userId)
    {
        var cache = f.Services.GetRequiredService<ICacheProvider>();
        await cache.RemoveAsync(CacheKeys.UserPermissions(userId));
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
    }

    /// <summary>不经 HTTP,直接问权限提供者(用于替换了时钟、令牌可能随之过期的用例)。</summary>
    public static async Task<IReadOnlyCollection<string>> CodesOfAsync(AdminAppFactory f, long userId)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IPermissionProvider>().GetPermissionCodesAsync(userId);
    }

    /// <summary>本地时间,格式与前端提交的一致(不带时区后缀),免得序列化带上 offset。</summary>
    public static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>今天 + <paramref name="days"/> 那一天的 23:59:59,即前端日期选择器提交的形态。</summary>
    public static string EndOfDay(int days) => Local(DateTime.Today.AddDays(days + 1).AddSeconds(-1));

    public static object Allow(long menuId, string? expire = null, string? remark = null) => new { menuId, effect = 1, expireTime = expire, remark };

    public static object Deny(long menuId, string? expire = null, string? remark = null) => new { menuId, effect = 2, expireTime = expire, remark };

    /// <summary>按变更集保存,返回信封里的业务码(0 = 成功)。</summary>
    public static async Task<int> PutGrantsAsync(HttpClient c, long userId, object[] upserts, long[]? removes = null) =>
        (await (await c.PutJson("/api/v1/sys/user/menu", new { userId, upserts, removes = removes ?? Array.Empty<long>() })).ReadEnvelope())
            .GetProperty("code").GetInt32();

    public static async Task<List<SysUserMenu>> GrantRowsAsync(AdminAppFactory f, long userId)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>().AsQueryable().Where(g => g.UserId == userId).ToListAsync();
    }

    public static async Task<long> SuperAdminIdAsync(AdminAppFactory f)
    {
        using var s = f.Services.CreateScope();
        return (await s.ServiceProvider.GetRequiredService<IRepository<SysUser>>().GetFirstAsync(u => u.IsSuperAdmin == true))!.Id;
    }

    /// <summary>普通管理员:角色只授「用户-授权菜单」(239),数据范围默认全部。</summary>
    public static async Task<(HttpClient Client, long Id, long RoleId)> DelegatedAdminAsync(
        AdminAppFactory f, DataScopeType scope = DataScopeType.All, long[]? customOrgIds = null, long? orgId = null)
    {
        var role = await CreateRoleAsync(f, [239], scope, customOrgIds);
        var (id, account) = await CreateUserAsync(f, [role], orgId);
        return (await LoginAsync(f, account), id, role);
    }

    /// <summary>在指定模块下建一个顶级目录 + 其下一个页面。</summary>
    public static async Task<(long CatalogId, long PageId)> CreateCatalogWithPageAsync(AdminAppFactory f, long moduleId)
    {
        using var s = f.Services.CreateScope();
        var menus = s.ServiceProvider.GetRequiredService<IMenuService>();
        var catalog = await menus.CreateAsync(new MenuInput
        {
            ParentId = 0, Type = MenuType.Catalog, Title = "授权测试目录", Permission = "", Sort = 50, Enabled = true, ModuleId = moduleId, Visible = true,
        });
        var page = await menus.CreateAsync(new MenuInput
        {
            ParentId = catalog, Type = MenuType.Menu, Title = "授权测试页面", Permission = "", Sort = 1, Enabled = true,
            Path = "/grant-test/" + Guid.NewGuid().ToString("N")[..8], Component = "dashboard/biz", Visible = true,
        });
        return (catalog, page);
    }
}
