using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 角色授权的现有口径:权限码、门户模块、菜单树三处一致。加入用户单独授权之后,
/// 没有任何单独授权记录的用户必须得到与这里完全相同的结果。
/// </summary>
public class RoleGrantBaselineTests
{
    private const long OrgCatalog = 200, PositionPage = 220, PositionQuery = 221;

    private static readonly string[] PositionQueryCodes = ["GET:/api/v1/sys/position/page", "GET:/api/v1/sys/position/{id}"];

    [Fact]
    public async Task Granted_button_yields_its_codes_and_scaffolds_ancestors_in_tree()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionQuery]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        var c = await GrantTestKit.LoginAsync(f, account);

        Assert.Equal(PositionQueryCodes.Order(StringComparer.Ordinal), (await GrantTestKit.CodesAsync(c)).Order(StringComparer.Ordinal));
        var modules = await GrantTestKit.ModuleIdsAsync(c);
        Assert.Equal([1L], modules);

        var tree = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([OrgCatalog], tree[0]);              // 授的是按钮,目录由脚手架补上
        Assert.Equal([PositionPage], tree[OrgCatalog]);   // 页面由脚手架补上
        Assert.Empty(tree[PositionPage]);                 // 按钮不进侧栏
    }

    [Fact]
    public async Task Disabled_role_yields_no_codes_and_empty_tree()
    {
        using var f = new AdminAppFactory();
        var roleId = await GrantTestKit.CreateRoleAsync(f, [PositionPage, PositionQuery]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [roleId]);

        using (var scope = f.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var role = await roles.GetAsync(roleId);
            await roles.UpdateAsync(roleId, new RoleInput { Name = role.Name, Code = role.Code, Sort = role.Sort, Enabled = false, Remark = role.Remark });
        }

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Empty(await GrantTestKit.CodesAsync(c));
        Assert.Empty((await GrantTestKit.MenuTreeAsync(c, 1))[0]);
    }

    [Fact]
    public async Task User_without_roles_gets_no_codes_modules_or_tree()
    {
        using var f = new AdminAppFactory();
        var (_, account) = await GrantTestKit.CreateUserAsync(f, []);
        var c = await GrantTestKit.LoginAsync(f, account);

        Assert.Empty(await GrantTestKit.CodesAsync(c));
        Assert.Empty(await GrantTestKit.ModuleIdsAsync(c));
        Assert.Empty((await GrantTestKit.MenuTreeAsync(c, 1))[0]);
    }

    [Fact]
    public async Task Disabled_button_drops_its_codes_but_granted_page_stays_in_tree()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionPage, PositionQuery]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var menu = (await sp.GetRequiredService<IRepository<SysMenu>>().GetByIdAsync(PositionQuery))!;
            await sp.GetRequiredService<IMenuService>().UpdateAsync(PositionQuery, new MenuInput
            {
                ParentId = menu.ParentId, Type = menu.Type, Title = menu.Title, Permission = menu.Permission,
                Sort = menu.Sort, Enabled = false, ModuleId = menu.ModuleId,
                Path = menu.Path, Component = menu.Component, Icon = menu.Icon, Visible = menu.Visible,
            });
        }

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Empty(await GrantTestKit.CodesAsync(c));
        var tree = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([OrgCatalog], tree[0]);
        Assert.Equal([PositionPage], tree[OrgCatalog]);
    }
}
