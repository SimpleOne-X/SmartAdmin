using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// SysTenant 的标准 CRUD,叠加两条本表独有的规则:只有平台管理员能操作(RequirePlatformAdmin,
/// 与 [RolePermission]/IsSuperAdmin 完全独立的第二道门,见 ICurrentUser.IsPlatformAdmin 注释);
/// 一期只接受 Shared 隔离模式(RequireSharedMode)。
/// </summary>
public class TenantService(
    IRepository<SysTenant> tenants,
    IRepository<SysUser> users,
    IPasswordHasher hasher,
    ICurrentUser currentUser) : ITenantService
{
    public virtual async Task<PagedList<SysTenant>> PageAsync(TenantPageInput input)
    {
        // 读也要过这道门:租户注册表不受 ITenantScoped 过滤器约束(SysTenant 是注册表本身,没有 TenantId),
        // 少了这一行,任何租户的初始管理员(租户内超管,天然绕过 [RolePermission])直接 GET 就能翻出
        // 全平台每一个租户的完整档案——跨客户信息泄露。
        RequirePlatformAdmin();
        return await tenants.AsQueryable()
            .WhereIF(!string.IsNullOrEmpty(input.Name), t => t.Name.Contains(input.Name!))
            .ToPagedListAsync(input, q => q.OrderBy(t => t.CreateTime));
    }

    public virtual async Task<SysTenant> GetAsync(long id)
    {
        RequirePlatformAdmin();   // 同 PageAsync:按 Id 单读同样是读,门禁一视同仁
        var tenant = await tenants.GetByIdAsync(id);
        AdminException.ThrowIf(tenant is null, ErrorCode.TenantNotFound);
        return tenant!;
    }

    public virtual async Task<long> AddAsync(TenantCreateInput input)
    {
        RequirePlatformAdmin();
        RequireSharedMode(input.IsolationMode);

        AdminException.ThrowIf(
            await tenants.AsQueryable().ClearFilter<ISoftDelete>().AnyAsync(t => t.Code == input.Code),
            ErrorCode.TenantCodeExists);
        // 双类型参数单次调用,不是链式两次 ClearFilter<A>().ClearFilter<B>()——后者只有最后一层真的生效
        // (ClearFilter 对内部状态是赋值不是追加),同 AuthService.GenerateProvisionAccountAsync /
        // UserService.AddAsync 处注释。
        AdminException.ThrowIf(
            await users.AsQueryable().ClearFilter<ISoftDelete, ITenantScoped>().AnyAsync(u => u.Account == input.AdminAccount),
            ErrorCode.AccountExists);

        var tenant = new SysTenant
        {
            Code = input.Code,
            Name = input.Name,
            ContactName = input.ContactName,
            ContactPhone = input.ContactPhone,
            ExpireTime = input.ExpireTime,
            IsolationMode = input.IsolationMode,
            Enabled = input.Enabled,
            Remark = input.Remark,
        };

        // 租户行与它的初始管理员必须一起有、一起没:半途失败(哈希异常/DB 抖动/唯一约束竞态)会留下
        // 没有管理员的孤儿租户,且孤儿租户的 Code 还占着唯一索引,朴素重试会先撞 TenantCodeExists——同一事务整体回滚。
        await tenants.Db.RunInTransactionAsync(async () =>
        {
            await tenants.InsertAsync(tenant);

            // 新租户的初始管理员——租户内超管,不是平台管理员。TenantId 必须显式指定:
            // 插入 AOP 会按"当前登录者"(平台管理员自己所在的默认租户)回填,那不是这个新用户该归属的租户。
            await users.InsertAsync(new SysUser
            {
                TenantId = tenant.Id,
                Account = input.AdminAccount,
                Password = hasher.Hash(input.AdminPassword),
                Name = input.Name + "管理员",
                IsSuperAdmin = true,
                MustChangePassword = true,
            });
        });

        return tenant.Id;
    }

    public virtual async Task UpdateAsync(long id, TenantInput input)
    {
        RequirePlatformAdmin();
        RequireSharedMode(input.IsolationMode);

        var tenant = await GetAsync(id);
        AdminException.ThrowIf(
            input.Code != tenant.Code &&
            await tenants.AsQueryable().ClearFilter<ISoftDelete>().AnyAsync(t => t.Code == input.Code && t.Id != id),
            ErrorCode.TenantCodeExists);

        tenant.Code = input.Code;
        tenant.Name = input.Name;
        tenant.ContactName = input.ContactName;
        tenant.ContactPhone = input.ContactPhone;
        tenant.ExpireTime = input.ExpireTime;
        tenant.IsolationMode = input.IsolationMode;
        tenant.Enabled = input.Enabled;
        tenant.Remark = input.Remark;
        await tenants.UpdateAsync(tenant);
    }

    public virtual async Task DeleteAsync(long id)
    {
        RequirePlatformAdmin();
        AdminException.ThrowIf(id == DefaultTenantSeed.DEFAULT_TENANT_ID, ErrorCode.TenantProtected);
        await GetAsync(id);
        await tenants.DeleteAsync(id);
    }

    /// <summary>只有平台管理员能管理租户注册表本身——不依赖角色授权,防止某个租户内部的超管账号
    /// (同样绕过 [RolePermission])顺带管到别的租户。</summary>
    protected virtual void RequirePlatformAdmin() =>
        AdminException.ThrowIf(!currentUser.IsPlatformAdmin, ErrorCode.PlatformAdminRequired);

    /// <summary>一期只支持共享库;写 Standalone 一律拒绝(二期解锁前的硬校验)。</summary>
    protected virtual void RequireSharedMode(TenantIsolationMode mode) =>
        AdminException.ThrowIf(mode != TenantIsolationMode.Shared, ErrorCode.TenantIsolationModeNotSupported);
}
