using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 内置模块种子(多应用门户)。播两个应用:内置 <c>system</c>(默认工作区,现有系统管理/组织/字典/文件等
/// 顶级目录都挂靠它,见 <c>DefaultMenuSeed</c>,受 <c>ModuleService.DeleteAsync</c> 保护不可删)+ 一个示例
/// <c>business</c>「业务中心」(仅一条工作台菜单,演示多应用门户,可删)。固定 Id 作幂等锚点。
/// 「业务中心」种子为可转授(新库);老库升级时它保持 NULL(不可转授),由超管在模块管理页决定。
/// </summary>
public class DefaultModuleSeed : ISeedData<SysModule>
{
    /// <summary>内置模块固定主键(种子幂等锚点 + 删除保护锚点)</summary>
    internal const long BUILTIN_MODULE_ID = 1;

    /// <summary>内置模块编码</summary>
    internal const string BUILTIN_MODULE_CODE = "system";

    /// <summary>示例业务模块固定主键(挂工作台演示菜单,可删)</summary>
    internal const long BUSINESS_MODULE_ID = 2;

    /// <summary>模块表的<b>结构</b>是内核拥有的,随内核升级同步;只刷 <see cref="SyncColumns"/> 里那几列(见 <see cref="ISeedData{T}.SyncOnUpgrade"/>)。</summary>
    public virtual bool SyncOnUpgrade => true;

    /// <summary>
    /// 升级时只刷这几列:编码、图标、落地路由、路由前缀——它们决定"这个应用是什么、指向哪",由内核定义。
    /// 标题、排序、启用、备注、可转授是超管在模块管理页的设置,不在内:整行刷回会把超管关掉的「业务中心」可转授重新打开。
    /// </summary>
    public virtual string[]? SyncColumns =>
    [
        nameof(SysModule.Code), nameof(SysModule.Icon), nameof(SysModule.DefaultRoute), nameof(SysModule.ApiPrefix),
    ];

    /// <inheritdoc />
    public virtual IEnumerable<SysModule> HasData() =>
    [
        new SysModule { Id = BUILTIN_MODULE_ID, Code = BUILTIN_MODULE_CODE, Title = "系统", Icon = "lucide:settings", DefaultRoute = "", ApiPrefix = "sys", Sort = 1, Enabled = true, IsDelegatable = false, Remark = "内置系统应用,不可删除" },
        new SysModule { Id = BUSINESS_MODULE_ID, Code = "business", Title = "业务中心", Icon = "lucide:briefcase-business", DefaultRoute = "", ApiPrefix = "biz", Sort = 2, Enabled = true, IsDelegatable = true, Remark = "示例业务应用(可删除)" },
    ];
}
