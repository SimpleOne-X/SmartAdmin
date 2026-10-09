using SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 读 <c>sys_user_menu</c> 的两条共用查询。权限聚合与门户不给主构造器加仓储参数
/// (对继承它们的消费者是源码破坏性变更),经已有仓储的 <c>Db</c> 逃生舱口调这里。
/// </summary>
public static class UserMenuGrantQueries
{
    /// <summary>该用户的全部单独授权记录(含已过期的,规则函数自己按时间筛)。</summary>
    public static Task<List<SysUserMenu>> ListByUserAsync(ISqlSugarClient db, long userId) =>
        db.Queryable<SysUserMenu>().Where(g => g.UserId == userId).ToListAsync();

    /// <summary>该用户晚于 <paramref name="now"/> 的最早到期时间,没有为 null。取回来在内存里求最小值,四种库写法一致。</summary>
    public static async Task<DateTime?> NextExpiryAsync(ISqlSugarClient db, long userId, DateTime now)
    {
        var times = await db.Queryable<SysUserMenu>()
            .Where(g => g.UserId == userId && g.ExpireTime != null && g.ExpireTime > now)
            .Select(g => g.ExpireTime)
            .ToListAsync();
        return times.Min();
    }
}
