using SmartAdmin.Core;

namespace SmartAdmin.Services;

public interface ITenantService
{
    Task<PagedList<SysTenant>> PageAsync(TenantPageInput input);
    Task<SysTenant> GetAsync(long id);
    Task<long> AddAsync(TenantCreateInput input);
    Task UpdateAsync(long id, TenantInput input);
    Task DeleteAsync(long id);
}
