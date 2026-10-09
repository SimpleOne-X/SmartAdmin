using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 菜单树的共用运算:上溯根目录取所属模块、判系统菜单、按 ParentId 展开子孙。
/// 门户、权限聚合与授权守卫都要用,收在这里只写一份。菜单表小,调用方整表载入内存再算。
/// </summary>
public static class MenuTree
{
    /// <summary>上溯 ParentId 链的最大步数(防断链/环)。菜单层级远小于此。</summary>
    public const int WalkGuard = 64;

    /// <summary>上溯 <paramref name="menuId"/> 的 ParentId 链到根目录,返回根目录的 ModuleId(未挂模块或断链为 null)。</summary>
    public static long? RootModuleId(long menuId, IReadOnlyDictionary<long, SysMenu> byId) =>
        RootOf(menuId, byId)?.ModuleId;

    /// <summary>
    /// <paramref name="menuId"/> 是不是系统菜单:上溯到根目录,根目录挂在内置「系统」应用,且根目录是内核种子
    /// (Id 不超过 <see cref="SmartSeedIds.KernelMax"/>)。整棵子树都算,所以消费者挂在内核目录下的页面仍是系统菜单;
    /// 消费者在「系统」应用下自建的根目录(Id ≥ <see cref="SmartSeedIds.ConsumerMin"/>)和它的子树不算。
    /// 系统菜单只能授给内置角色,这个口径只在这里写一份,升级清理与授权守卫共用。
    /// 上溯规则同 <see cref="RootModuleId"/>:断链停在断点处的节点,成环超过 <see cref="WalkGuard"/> 步就停。
    /// </summary>
    public static bool IsKernelSystemMenu(long menuId, IReadOnlyDictionary<long, SysMenu> byId) =>
        RootOf(menuId, byId) is { } root
        && root.ModuleId == DefaultModuleSeed.BUILTIN_MODULE_ID
        && root.Id <= SmartSeedIds.KernelMax;

    /// <summary>上溯 ParentId 链到根目录。菜单不存在返回 null;断链或成环时返回停下来的那个节点。</summary>
    private static SysMenu? RootOf(long menuId, IReadOnlyDictionary<long, SysMenu> byId)
    {
        var cur = byId.GetValueOrDefault(menuId);
        var guard = 0;
        while (cur is not null && cur.ParentId != 0 && guard++ < WalkGuard)
        {
            if (!byId.TryGetValue(cur.ParentId, out var parent)) break;
            cur = parent;
        }
        return cur;
    }

    /// <summary>
    /// <paramref name="menuId"/> 的祖先(不含自己)里有没有 <paramref name="ancestorIds"/> 中的节点。
    /// 沿 ParentId 上溯,断链或成环(超过 <see cref="WalkGuard"/> 步)就停。
    /// </summary>
    public static bool HasAncestorIn(long menuId, IReadOnlyDictionary<long, SysMenu> byId, IReadOnlySet<long> ancestorIds)
    {
        if (ancestorIds.Count == 0) return false;
        var cur = byId.GetValueOrDefault(menuId);
        var guard = 0;
        while (cur is not null && cur.ParentId != 0 && guard++ < WalkGuard)
        {
            if (ancestorIds.Contains(cur.ParentId)) return true;
            cur = byId.GetValueOrDefault(cur.ParentId);
        }
        return false;
    }

    /// <summary>根节点连同它们在 <paramref name="menus"/> 里的全部子孙。已收录的节点不再展开,所以成环的数据也会停下。</summary>
    public static HashSet<long> WithDescendants(IEnumerable<long> roots, IEnumerable<SysMenu> menus)
    {
        var children = menus.ToLookup(m => m.ParentId, m => m.Id);
        var result = new HashSet<long>();
        var pending = new Stack<long>(roots);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!result.Add(id)) continue;
            foreach (var child in children[id]) pending.Push(child);
        }
        return result;
    }
}
