using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 建表被跳过时(生产闸门关 / EnableCodeFirst=false)确认 <c>sys_user_menu</c> 在库里。
/// <para>启动时的缺列检查只看已存在的表,缺整张表它不报;这张表又在每个非超管请求的鉴权路径上,
/// 缺了它进程照样起得来,随后每个非超管请求都炸在驱动层的「表不存在」上。所以库就绪时点名拦下。</para>
/// </summary>
public class UserMenuGrantTableGuard(ISqlSugarClient db) : IDatabaseReadyHook
{
    /// <inheritdoc />
    public virtual Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        if (context.CodeFirstRan) return Task.CompletedTask;

        var table = db.EntityMaintenance.GetTableName<SysUserMenu>();
        if (db.DbMaintenance.IsAnyTable(table, false)) return Task.CompletedTask;

        throw new InvalidOperationException(
            $"SmartAdmin 启动失败:库里缺少表 {table}(用户单独授权,每个非超管请求的鉴权都要读它)。" +
            "CodeFirst 自动建表已跳过,没人替库建这张表。二选一:" +
            "(1) 本次启动配置 SmartAdmin:Database:EnableCodeFirstInProduction=true,由应用建表补列" +
            "(会丢数据的变更另有一道闸门默认拒绝,不会顺手执行);" +
            "(2) 由 DBA 先建好这张表再启动,表结构可在预发库上开闸门启动一次后从 SQL 日志照抄。详见文档站「部署」一节。");
    }
}
