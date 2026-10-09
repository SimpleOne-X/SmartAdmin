using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="AdminSecurityOptions"/> 的启动期校验——API 宿主与独立 Worker 共用同一入口,
/// 写法与 <see cref="AdminJobsOptionsValidation"/> 一致。
/// </summary>
public static class AdminSecurityOptionsValidation
{
    /// <summary><see cref="AdminSecurityOptions.DelegatedGrantMaxDays"/> 的上限(约十年)。再大的值在算到期上限时会让 <c>AddDays</c> 越界抛异常。</summary>
    public const int MaxDelegatedGrantDays = 3650;

    /// <summary>校验 Security 选项;任一不合法即抛 <see cref="InvalidOperationException"/>,拒绝启动。</summary>
    public static void Validate(AdminSecurityOptions security)
    {
        ArgumentNullException.ThrowIfNull(security);

        // 负数若当成「不限」会在配错时放行;过大则每次保存算到期上限都抛异常、普通管理员的保存全部 500。两头都在启动时拒掉。
        if (security.DelegatedGrantMaxDays is < 0 or > MaxDelegatedGrantDays)
            throw new InvalidOperationException(
                $"SmartAdmin:Security 配置无效:DelegatedGrantMaxDays({security.DelegatedGrantMaxDays})必须在 0 到 {MaxDelegatedGrantDays} 之间(0 表示不限)。");
    }
}
