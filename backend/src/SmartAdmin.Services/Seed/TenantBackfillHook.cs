using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 升级期一次性回填:把已发版表补列后 TenantId 为 null 的存量行,统一置为默认租户(DefaultTenantSeed)。
/// 不这样做的话,升级后这些行会被 ITenantScoped 过滤器判定为"看不见"——等于老部署升级后数据消失。
/// 新行此后一律由插入 AOP 自动填充(见 SqlSugarSetup),不会再产生 null,所以这里只处理"存量"。
/// 每次启动都跑,只更新真正为 null 的行,第二次起是 no-op,不需要额外的"是否已回填过"标记。
/// <para>迁移更多实体到 TenantEntity 时(见 Task 9/10),回这个类加对应的 IRepository&lt;T&gt; 构造参数
/// 与一行 UpdateAsync 调用——这里刻意不用反射遍历所有 ITenantScoped 类型,保持每个实体显式可见。</para>
/// </summary>
public class TenantBackfillHook(
    IRepository<SysUser> users,
    IRepository<SysOrg> orgs,
    IRepository<SysRole> roles,
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysRoleDataScope> roleDataScopes,
    IRepository<SysPosition> positions,
    IRepository<SysNotice> notices,
    IRepository<SysNoticeReceiver> noticeReceivers,
    IRepository<SysNoticeRead> noticeReads,
    IRepository<SysFile> files,
    IRepository<SysLoginLog> loginLogs,
    IRepository<SysOpLog> opLogs,
    IRepository<SysExceptionLog> exceptionLogs,
    IRepository<SysSession> sessions) : IDatabaseReadyHook
{
    public virtual async Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        await users.Db.Updateable<SysUser>()
            .SetColumns(u => u.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(u => u.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await orgs.Db.Updateable<SysOrg>()
            .SetColumns(o => o.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(o => o.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await roles.Db.Updateable<SysRole>()
            .SetColumns(r => r.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(r => r.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await userRoles.Db.Updateable<SysUserRole>()
            .SetColumns(ur => ur.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(ur => ur.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await roleMenus.Db.Updateable<SysRoleMenu>()
            .SetColumns(rm => rm.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(rm => rm.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await roleDataScopes.Db.Updateable<SysRoleDataScope>()
            .SetColumns(rds => rds.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(rds => rds.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await positions.Db.Updateable<SysPosition>()
            .SetColumns(p => p.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(p => p.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await notices.Db.Updateable<SysNotice>()
            .SetColumns(n => n.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(n => n.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await noticeReceivers.Db.Updateable<SysNoticeReceiver>()
            .SetColumns(nr => nr.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(nr => nr.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await noticeReads.Db.Updateable<SysNoticeRead>()
            .SetColumns(nr => nr.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(nr => nr.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await files.Db.Updateable<SysFile>()
            .SetColumns(f => f.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(f => f.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await loginLogs.Db.Updateable<SysLoginLog>()
            .SetColumns(l => l.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(l => l.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await opLogs.Db.Updateable<SysOpLog>()
            .SetColumns(l => l.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(l => l.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await exceptionLogs.Db.Updateable<SysExceptionLog>()
            .SetColumns(l => l.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(l => l.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
        await sessions.Db.Updateable<SysSession>()
            .SetColumns(s => s.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(s => s.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
    }
}
