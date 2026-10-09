using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 单独授权的委派守卫——「当前用户能否对这个用户做这些单独授权」的唯一判定出口。
/// 保存(<see cref="IUserMenuGrantService.ApplyChangesAsync"/>)与读接口里的「能否编辑 / 能否授」都经它,不各写一套。
/// <para>规则依次:目标是自己(42029)→ 目标是超管(42007)→ 授权人是超管或系统上下文则放行 →
/// 目标不在数据范围内(41005)→ 目标也是管理员(41007)→ 「允许」必须限时(41008)→
/// 变更集里每一条(新增、修改、移除)的菜单都必须属于可转授模块(41006)。不要求授权人自己持有这些菜单。</para>
/// <para>类 public、方法 virtual,注册用 TryAdd:消费者可接入外部治理系统整体替换判定规则。</para>
/// </summary>
public interface IUserMenuGrantPolicy
{
    /// <summary>当前授权人受委派限时约束时的最长天数;超管、系统上下文、配置为 0 时为 null(不限)。</summary>
    int? DelegatedMaxDays { get; }

    /// <summary>
    /// 当前授权人能授出的「允许」的最晚到期日:服务器当前本地日期加 <see cref="DelegatedMaxDays"/>;不受限时为 null。
    /// 校验与界面展示都用它,日期由服务端给出,不让浏览器按自己的时区再算一遍
    /// (服务器在 UTC、用户在东八区时,每天凌晨浏览器的日期会领先服务器一天)。
    /// </summary>
    DateOnly? DelegatedMaxDate { get; }

    /// <summary>当前授权人不能编辑该目标用户的原因(规则 1–5 的错误码);能编辑返回 null。</summary>
    Task<ErrorCode?> GetTargetBlockAsync(long targetUserId);

    /// <summary>当前授权人可授的模块 Id;超管或系统上下文返回 null(全部可授)。</summary>
    Task<IReadOnlySet<long>?> GetGrantableModuleIdsAsync();

    /// <summary>校验整份变更集,不满足直接抛 <see cref="AdminException"/>。变更集之外的记录不校验。</summary>
    Task EnsureGrantableAsync(long targetUserId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes);
}
