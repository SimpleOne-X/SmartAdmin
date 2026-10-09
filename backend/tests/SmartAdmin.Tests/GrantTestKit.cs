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
}
