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

    /// <inheritdoc />
    public virtual async Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        // 空库 PreviousSchemaVersion 为 null;解析不出整数的老版本号按「更早」处理
        if (!context.Upgraded || context.PreviousSchemaVersion is not { } previous) return;
        if (int.TryParse(previous, out var from) && from >= SinceSchemaVersion) return;

        var menus = await db.Queryable<SysMenu>().ClearFilter<ISoftDelete>().ToListAsync();
        var byId = menus.ToDictionary(m => m.Id);
        List<long> systemMenuIds = [.. menus.Where(m => MenuTree.RootModuleId(m.Id, byId) == DefaultModuleSeed.BUILTIN_MODULE_ID).Select(m => m.Id)];
        if (systemMenuIds.Count == 0) return;

        var links = await db.Queryable<SysRoleMenu>()
            .Where(x => x.RoleId > SmartSeedIds.KernelMax && systemMenuIds.Contains(x.MenuId))
            .OrderBy(x => x.RoleId).OrderBy(x => x.MenuId)
            .ToListAsync();
        if (links.Count == 0) return;

        List<long> roleIds = [.. links.Select(x => x.RoleId).Distinct()];
        var roleNames = (await db.Queryable<SysRole>().ClearFilter<ISoftDelete>().Where(r => roleIds.Contains(r.Id)).ToListAsync())
            .ToDictionary(r => r.Id, r => $"{r.Name}({r.Code})");

        await db.RunInTransactionAsync(async () =>
        {
            foreach (var batch in links.Select(x => x.Id).Chunk(DeleteBatchSize))
                await db.Deleteable<SysRoleMenu>().Where(x => batch.Contains(x.Id)).ExecuteCommandAsync();
        });

        // 删完再记:日志只陈述已经发生的事
        foreach (var link in links)
            logger.LogWarning("SmartAdmin: 系统模块的菜单只能授给内置角色,升级清理删除角色 {Role} 上的菜单 {Menu}(Id {MenuId})",
                roleNames.GetValueOrDefault(link.RoleId, link.RoleId.ToString()), byId[link.MenuId].Title, link.MenuId);
        logger.LogWarning("SmartAdmin: 升级清理共删除 {Count} 条非内置角色的系统模块菜单授权,涉及 {Roles} 个角色", links.Count, roleIds.Count);

        List<long> userIds = [.. (await db.Queryable<SysUserRole>().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.UserId).ToListAsync()).Distinct()];
        if (userIds.Count > 0)
            await cache.RemoveManyAsync(userIds.Select(CacheKeys.UserPermissions), cancellationToken);
        await cache.IncrementAsync(CacheKeys.PortalGeneration, cancellationToken: cancellationToken);
    }
}
