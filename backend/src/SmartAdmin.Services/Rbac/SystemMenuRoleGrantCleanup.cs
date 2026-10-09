using Microsoft.Extensions.Logging;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 从「新建角色也能持有系统模块菜单」的老版本升级上来时,清一次存量:物理删掉非内置角色上授的系统模块菜单,
/// 每一行写一条 Warning 日志留痕,再失效受影响用户的权限码缓存与门户代际(装了 Redis 时缓存跨重启存活)。
/// <para>只在种子版本从低于 <see cref="SinceSchemaVersion"/> 升上来的那一次执行;空库、平时重启、
/// 关了种子(不走版本闸门)都不执行。删除不可恢复,所以日志要逐条列出删了哪个角色的哪个菜单。</para>
/// <para>判「菜单属于系统模块」读的是全表(含停用与软删的节点),理由同 <c>RbacService.EnsureRoleMenusAssignableAsync</c>:
/// 只读启用节点会在停用的中间目录处断链而漏删,软删的菜单恢复后会带着授权回来。</para>
/// <para>流程拆成「找出待删授权 → 记待删汇总 → 删除 → 记日志 → 失效缓存」几步,各自 <c>protected virtual</c>,可单独覆写。</para>
/// <para>版本行先于钩子写成当前版本,所以这里不会重试:钩子失败后下次启动不再当作升级。因此失败要留下运维看得见的痕迹——
/// 删除前先记一条待删汇总;删除事务失败写 Error(说明已回滚、没删任何授权、如何补救)并照常抛出让启动失败;
/// 授权删完之后缓存失效失败也写 Error,但<b>不再抛出</b>:授权已经删了,抛出只会让启动失败而无从重试,
/// 缓存本就有过期时间,最坏是旧权限多留一阵,日志里给出缓存管理页的补救办法。</para>
/// </summary>
public class SystemMenuRoleGrantCleanup(
    ISqlSugarClient db,
    ICacheProvider cache,
    ILogger<SystemMenuRoleGrantCleanup> logger) : IDatabaseReadyHook
{
    /// <summary>从这个种子版本起,系统模块的菜单只能授给内置角色。</summary>
    public const int SinceSchemaVersion = 7;

    /// <summary>分批删除时每批的行 Id 数,免得一条 <c>IN</c> 列表长到 SQL Server 吃不消。</summary>
    private const int DeleteBatchSize = 1000;

    /// <summary>日志里最多列出的角色个数,免得几百个角色把一行日志撑爆。</summary>
    private const int MaxRolesListed = 20;

    /// <summary>待清理的授权:要删的 <c>sys_role_menu</c> 行,以及写日志要用的菜单标题与角色名。</summary>
    protected sealed record StaleGrants(
        IReadOnlyList<SysRoleMenu> Links,
        IReadOnlyDictionary<long, string> MenuTitles,
        IReadOnlyDictionary<long, string> RoleNames)
    {
        /// <summary>没有任何待清理的授权。</summary>
        public static StaleGrants None { get; } = new([], new Dictionary<long, string>(), new Dictionary<long, string>());

        /// <summary>涉及的角色 Id(去重)。</summary>
        public IReadOnlyList<long> RoleIds { get; } = [.. Links.Select(x => x.RoleId).Distinct()];
    }

    /// <inheritdoc />
    public virtual async Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        // 空库 PreviousSchemaVersion 为 null;解析不出整数的老版本号按「更早」处理
        if (!context.Upgraded || context.PreviousSchemaVersion is not { } previous) return;
        if (int.TryParse(previous, out var from) && from >= SinceSchemaVersion) return;

        var stale = await FindStaleGrantsAsync();
        if (stale.Links.Count == 0) return;

        LogPendingGrants(stale);

        int deleted;
        try
        {
            deleted = await DeleteGrantsAsync(stale);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "SmartAdmin: 升级清理删除失败:事务已回滚,没有删除任何授权。种子版本已前进,不会自动重试,"
                + "请超管在角色授权页对涉及角色重存一次授权(系统模块的菜单不会再显示,保存即收回):{Roles}",
                DescribeRoles(stale));
            throw;
        }

        LogDeletedGrants(stale, deleted);

        try
        {
            await InvalidateCachesAsync(stale, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "SmartAdmin: 升级清理的授权已删除,但权限码缓存与门户代际失效失败(缓存未失效):"
                + "受影响用户在缓存过期前可能仍看到旧权限,可在缓存管理页分别清除「权限缓存」与「门户菜单缓存」补救");
        }
    }

    /// <summary>找出非内置角色(Id &gt; 999)上授的系统模块菜单。菜单读全表(含停用与软删),角色名连软删的一起查。</summary>
    protected virtual async Task<StaleGrants> FindStaleGrantsAsync()
    {
        var menus = await db.Queryable<SysMenu>().ClearFilter<ISoftDelete>().ToListAsync();
        var byId = menus.ToDictionary(m => m.Id);
        List<long> systemMenuIds = [.. menus.Where(m => MenuTree.RootModuleId(m.Id, byId) == DefaultModuleSeed.BUILTIN_MODULE_ID).Select(m => m.Id)];
        if (systemMenuIds.Count == 0) return StaleGrants.None;

        var links = await db.Queryable<SysRoleMenu>()
            .Where(x => x.RoleId > SmartSeedIds.KernelMax && systemMenuIds.Contains(x.MenuId))
            .OrderBy(x => x.RoleId).OrderBy(x => x.MenuId)
            .ToListAsync();
        if (links.Count == 0) return StaleGrants.None;

        List<long> roleIds = [.. links.Select(x => x.RoleId).Distinct()];
        var roleNames = (await db.Queryable<SysRole>().ClearFilter<ISoftDelete>().Where(r => roleIds.Contains(r.Id)).ToListAsync())
            .ToDictionary(r => r.Id, r => $"{r.Name}({r.Code})");
        var menuTitles = links.Select(x => x.MenuId).Distinct().ToDictionary(id => id, id => byId[id].Title);
        return new StaleGrants(links, menuTitles, roleNames);
    }

    /// <summary>删除前先记一条汇总:待删多少条、涉及哪些角色。删除失败时,这是日志里唯一说明「本来要删什么」的地方。</summary>
    protected virtual void LogPendingGrants(StaleGrants stale) =>
        logger.LogWarning("SmartAdmin: 升级清理待删除 {Count} 条非内置角色的系统模块菜单授权,涉及 {Roles} 个角色:{RoleNames}",
            stale.Links.Count, stale.RoleIds.Count, DescribeRoles(stale));

    /// <summary>涉及角色的「名称(编码)」清单;角色已不存在的回落成 Id,太多时只列前 <see cref="MaxRolesListed"/> 个。</summary>
    private static string DescribeRoles(StaleGrants stale)
    {
        var names = stale.RoleIds.Take(MaxRolesListed).Select(id => stale.RoleNames.GetValueOrDefault(id, id.ToString()));
        var listed = string.Join("、", names);
        return stale.RoleIds.Count > MaxRolesListed ? $"{listed} 等共 {stale.RoleIds.Count} 个角色" : listed;
    }

    /// <summary>在一个事务里分批物理删除待清理的授权行,返回各批删除语句实际影响的行数之和。</summary>
    protected virtual async Task<int> DeleteGrantsAsync(StaleGrants stale)
    {
        var deleted = 0;
        await db.RunInTransactionAsync(async () =>
        {
            foreach (var batch in stale.Links.Select(x => x.Id).Chunk(DeleteBatchSize))
                deleted += await DeleteBatchAsync(batch);
        });
        return deleted;
    }

    /// <summary>删除一批授权行(<paramref name="linkIds"/> 不超过一批的行数),返回实际影响的行数。</summary>
    protected virtual async Task<int> DeleteBatchAsync(IReadOnlyCollection<long> linkIds)
    {
        long[] ids = [.. linkIds];
        return await db.Deleteable<SysRoleMenu>().Where(x => ids.Contains(x.Id)).ExecuteCommandAsync();
    }

    /// <summary>删完再记:日志只陈述已经发生的事。每删一行一条 Warning,末尾一条汇总,条数取实际删除的行数。</summary>
    protected virtual void LogDeletedGrants(StaleGrants stale, int deleted)
    {
        foreach (var link in stale.Links)
            logger.LogWarning("SmartAdmin: 系统模块的菜单只能授给内置角色,升级清理删除角色 {Role} 上的菜单 {Menu}(Id {MenuId})",
                stale.RoleNames.GetValueOrDefault(link.RoleId, link.RoleId.ToString()), stale.MenuTitles[link.MenuId], link.MenuId);
        logger.LogWarning("SmartAdmin: 升级清理共删除 {Count} 条非内置角色的系统模块菜单授权,涉及 {Roles} 个角色", deleted, stale.RoleIds.Count);
    }

    /// <summary>失效受影响用户的权限码缓存,并让门户代际自增(模块列表与菜单树随之重算)。</summary>
    protected virtual async Task InvalidateCachesAsync(StaleGrants stale, CancellationToken cancellationToken)
    {
        var roleIds = stale.RoleIds;
        List<long> userIds = [.. (await db.Queryable<SysUserRole>().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.UserId).ToListAsync()).Distinct()];
        if (userIds.Count > 0)
            await cache.RemoveManyAsync(userIds.Select(CacheKeys.UserPermissions), cancellationToken);
        await cache.IncrementAsync(CacheKeys.PortalGeneration, cancellationToken: cancellationToken);
    }
}
