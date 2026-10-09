using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>用户授权计算规则(纯函数)。树:1 目录 → 10 页面 → 11/12 按钮;1 → 20 页面 → 21 按钮;13 是停用按钮。</summary>
public class UserMenuGrantRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 10, 0, 0);

    private static SysMenu M(long id, long parent, string permission = "", bool enabled = true, long? moduleId = null) =>
        new() { Id = id, ParentId = parent, Permission = permission, Enabled = enabled, ModuleId = moduleId };

    private static readonly SysMenu[] Menus =
    [
        M(1, 0, moduleId: 2),
        M(10, 1), M(11, 10, "GET:/a;GET:/shared"), M(12, 10, "GET:/b"), M(13, 10, "GET:/c", enabled: false),
        M(20, 1), M(21, 20, "GET:/shared"),
    ];

    private static SysUserMenu G(long menuId, UserMenuEffect effect, DateTime? expire = null) =>
        new() { MenuId = menuId, Effect = effect, ExpireTime = expire };

    private static long[] Effective(long[] roleMenus, params SysUserMenu[] grants) =>
        [.. UserMenuGrantRules.Compute(roleMenus, grants, Menus, Now).EffectiveMenuIds.Order()];

    [Fact] public void No_grants_keeps_role_result() => Assert.Equal([10L, 11], Effective([10, 11]));

    [Fact] public void Allow_unions_with_roles() => Assert.Equal([11L, 12], Effective([11], G(12, UserMenuEffect.Allow)));

    [Fact] public void Deny_beats_role_grant() => Assert.Empty(Effective([11], G(11, UserMenuEffect.Deny)));

    [Fact] public void Deny_beats_allow_on_same_node() => Assert.Empty(Effective([], G(12, UserMenuEffect.Allow), G(12, UserMenuEffect.Deny)));

    [Fact]
    public void Deny_expands_to_descendants()
    {
        var result = UserMenuGrantRules.Compute([1, 10, 11, 12, 20, 21], [G(10, UserMenuEffect.Deny)], Menus, Now);
        Assert.Equal([1L, 20, 21], result.EffectiveMenuIds.Order());
        Assert.Equal([10L, 11, 12, 13], result.DeniedMenuIds.Order());
    }

    [Fact] public void Deny_on_catalog_removes_whole_subtree() => Assert.Empty(Effective([1, 10, 11, 20, 21], G(1, UserMenuEffect.Deny)));

    [Fact]
    public void Expired_grants_are_ignored()
    {
        Assert.Empty(Effective([], G(12, UserMenuEffect.Allow, Now.AddMinutes(-1))));
        Assert.Equal([11L], Effective([11], G(11, UserMenuEffect.Deny, Now)));            // 到期时刻本身即失效
        Assert.Equal([12L], Effective([], G(12, UserMenuEffect.Allow, Now.AddSeconds(1))));
    }

    [Fact] public void Disabled_menu_is_never_effective() => Assert.Empty(Effective([13], G(13, UserMenuEffect.Allow)));

    [Fact]
    public void Next_expiry_is_earliest_future_one()
    {
        SysUserMenu[] grants =
        [
            G(11, UserMenuEffect.Allow, Now.AddHours(2)), G(12, UserMenuEffect.Deny, Now.AddHours(1)),
            G(20, UserMenuEffect.Allow, Now.AddHours(-1)), G(21, UserMenuEffect.Allow),
        ];
        Assert.Equal(Now.AddHours(1), UserMenuGrantRules.Compute([], grants, Menus, Now).NextExpiry);
        Assert.Null(UserMenuGrantRules.Compute([], [G(21, UserMenuEffect.Allow), G(20, UserMenuEffect.Allow, Now)], Menus, Now).NextExpiry);
    }

    [Fact]
    public void Leaked_codes_list_codes_still_carried_by_other_effective_nodes()
    {
        var leaked = UserMenuGrantRules.LeakedCodes(10, Menus, new HashSet<long> { 20, 21 });
        var only = Assert.Single(leaked);
        Assert.Equal("GET:/shared", only.Code);
        Assert.Equal([21L], only.CarrierMenuIds);

        Assert.Empty(UserMenuGrantRules.LeakedCodes(10, Menus, new HashSet<long> { 20 }));   // 21 不有效,就没有漏网
    }

    [Fact]
    public void Ttl_is_capped_by_next_expiry()
    {
        var twenty = TimeSpan.FromMinutes(20);
        Assert.Equal(TimeSpan.FromMinutes(5), UserMenuGrantRules.CapTtl(twenty, Now.AddMinutes(5), Now));
        Assert.Equal(twenty, UserMenuGrantRules.CapTtl(twenty, Now.AddHours(1), Now));
        Assert.Equal(TimeSpan.FromMinutes(5), UserMenuGrantRules.CapTtl(null, Now.AddMinutes(5), Now));   // 配置为永不过期也封顶
        Assert.Equal(twenty, UserMenuGrantRules.CapTtl(twenty, null, Now));
        Assert.Equal(TimeSpan.FromSeconds(1), UserMenuGrantRules.CapTtl(twenty, Now, Now));               // 不给出 0 或负数
    }

    [Fact]
    public void Descendant_walk_stops_on_cycles()
    {
        SysMenu[] cyclic = [M(1, 2), M(2, 1)];
        Assert.Equal([1L, 2], MenuTree.WithDescendants([1], cyclic).Order());
    }

    [Fact]
    public void Root_module_is_taken_from_top_catalog()
    {
        var byId = Menus.ToDictionary(m => m.Id);
        Assert.Equal(2L, MenuTree.RootModuleId(21, byId));
        Assert.Null(MenuTree.RootModuleId(999, byId));
    }
}
