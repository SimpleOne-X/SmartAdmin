using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IFilePathAccessChecker"/> 默认实现。只读缓存里的规则,不连文件服务器。
/// 角色按「启用中」算,与权限码、门户菜单同口径:停用角色不授权。
/// </summary>
public class FilePathAccessChecker(
    IFilePathResolver resolver,
    ICurrentUser currentUser,
    IRepository<SysUserRole> userRoles) : IFilePathAccessChecker
{
    /// <inheritdoc />
    public virtual async Task<bool> CanUploadAsync(string code, CancellationToken ct = default) =>
        await IsAllowedAsync((await resolver.GetRuleAsync(code, ct)).UploadRoleIds);

    /// <inheritdoc />
    public virtual async Task<bool> CanDownloadAsync(string code, CancellationToken ct = default) =>
        await IsAllowedAsync((await resolver.GetRuleAsync(code, ct)).DownloadRoleIds);

    /// <summary>当前用户是否被这份角色列表放行。空列表 = 所有登录用户。</summary>
    protected virtual async Task<bool> IsAllowedAsync(IReadOnlyList<long> allowedRoleIds)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId) return false;
        if (currentUser.IsSuperAdmin || allowedRoleIds.Count == 0) return true;
        return (await ResolveEnabledRoleIdsAsync(userId)).Any(allowedRoleIds.Contains);
    }

    /// <summary>
    /// 用户启用中的角色 Id。<c>GetUserRoleIdsAsync</c> 不过滤停用角色,所以这里和
    /// <see cref="MenuService"/> 一样 join 角色表取 <c>Enabled</c>。
    /// </summary>
    protected virtual async Task<List<long>> ResolveEnabledRoleIdsAsync(long userId) =>
        await userRoles.AsQueryable()
            .InnerJoin<SysRole>((ur, r) => ur.RoleId == r.Id && r.Enabled)
            .Where((ur, r) => ur.UserId == userId)
            .Select((ur, r) => ur.RoleId).ToListAsync();
}
