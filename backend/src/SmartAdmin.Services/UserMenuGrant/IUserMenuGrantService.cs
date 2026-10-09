using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 用户单独授权的读写服务。越权判定全部经 <see cref="IUserMenuGrantPolicy"/>。
/// <para>类 public、方法 virtual,注册用 TryAdd:消费者可继承覆写单步或整体替换。</para>
/// </summary>
public interface IUserMenuGrantService
{
    /// <summary>
    /// 按变更集保存某用户的单独授权:新增插入、修改更新、移除物理删除,没提到的记录原样保留。
    /// 事务内执行,提交后失效该用户的权限码缓存与门户代际,并发布 <see cref="UserMenuGrantsChangedEvent"/>。
    /// 没有任何实际变化(变更集为空,或内容与库里一致、要移除的记录本来就没有)时不写库、不失效缓存、不发事件。
    /// </summary>
    Task ApplyChangesAsync(long userId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes);

    /// <summary>目标用户的单独授权记录,带授权人、授权时间、最后修改人。菜单已不存在的记录不返回。非超管只能读数据范围内的用户。</summary>
    Task<IReadOnlyList<UserMenuGrantItem>> GetGrantsAsync(long userId);

    /// <summary>授权弹窗需要的全部数据:每个节点是否有效与来源、能否授、漏网的权限码、能否编辑及原因、委派最长天数、模块清单。</summary>
    Task<UserMenuEffectiveOutput> GetEffectiveAsync(long userId);

    /// <summary>单独授权一览,分页。非超管只看得到数据范围内用户的记录;菜单已不存在的记录不显示。</summary>
    Task<PagedList<UserMenuGrantPageItem>> GetGrantPageAsync(UserMenuGrantPageInput input);
}
