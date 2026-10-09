namespace SmartAdmin.Services;

/// <summary>
/// 菜单树的两个共用运算:上溯根目录取所属模块、按 ParentId 展开子孙。
/// 门户、权限聚合与授权守卫都要用,收在这里只写一份。菜单表小,调用方整表载入内存再算。
/// </summary>
public static class MenuTree
{
    /// <summary>上溯 ParentId 链的最大步数(防断链/环)。菜单层级远小于此。</summary>
    public const int WalkGuard = 64;

    /// <summary>上溯 <paramref name="menuId"/> 的 ParentId 链到根目录,返回根目录的 ModuleId(未挂模块或断链为 null)。</summary>
    public static long? RootModuleId(long menuId, IReadOnlyDictionary<long, SysMenu> byId)
    {
        var cur = byId.GetValueOrDefault(menuId);
        var guard = 0;
        while (cur is not null && cur.ParentId != 0 && guard++ < WalkGuard)
        {
            if (!byId.TryGetValue(cur.ParentId, out var parent)) break;
            cur = parent;
        }
        return cur?.ModuleId;
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
