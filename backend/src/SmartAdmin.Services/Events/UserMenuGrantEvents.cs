namespace SmartAdmin.Services;

/// <summary>
/// 某用户的单独授权已保存(事务提交、缓存失效之后发布)。内核自身不订阅,是留给消费者的扩展点:接通知、外部审计或告警。
/// </summary>
/// <param name="UserId">目标用户</param>
/// <param name="OperatorId">操作人;系统上下文为 null</param>
/// <param name="Added">新增的记录</param>
/// <param name="Updated">修改的记录(保存后的值)</param>
/// <param name="RemovedMenuIds">被移除记录的菜单 Id</param>
public record UserMenuGrantsChangedEvent(
    long UserId,
    long? OperatorId,
    IReadOnlyList<UserMenuGrantUpsert> Added,
    IReadOnlyList<UserMenuGrantUpsert> Updated,
    IReadOnlyList<long> RemovedMenuIds);
