using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// 让一段代码在"看起来像是已认证、且租户为指定 Id"的 HttpContext 下运行。
/// <para>
/// 背景:Task 7 给 <c>ITenantScoped</c> 挂了全局查询过滤器后,本仓库大量既有测试(不只是租户相关的测试——
/// RBAC/数据范围/导入导出/文件/模块门户/任务调度……几乎任何直接在没有 HttpContext 的后台 DI 作用域
/// (<c>f.Services.CreateScope()</c>)里建/查 <c>SysUser</c>/<c>SysRole</c> 等租户实体的测试都受影响)
/// 原本靠"currentUser.TenantId 与新建行的 TenantId 碰巧都是 null"侥幸互相匹配而继续通过。
/// 过滤器硬化为"currentUser.TenantId 为 null 时恒零行"(见 <c>SqlSugarSetup.AttachHooks</c> 的安全修复,
/// 修的是过滤器谓词本身依赖 ORM 把 <c>x == null</c> 翻成 <c>IS NULL</c> 这一不可靠假设)之后,
/// 这条侥幸路径彻底断了。这些测试原本压根不是在测租户隔离,只是被新增的强制要求波及——
/// 比逐个查询点补 <c>ClearFilter&lt;ITenantScoped&gt;()</c> 或给每个实体手工加 <c>TenantId</c> 字段
/// 更贴近真实场景:真实调用永远是"某个已认证租户下的请求",不是无租户系统上下文,
/// 让测试夹具的建库/查库过程也这样跑,才是与生产一致的默认,而不是一次性特例。
/// </para>
/// </summary>
internal static class TestTenantContext
{
    /// <summary>
    /// 在解析自 <paramref name="services"/> 的 <see cref="IHttpContextAccessor"/> 上设一个带 sub/tid/sadm claim
    /// 的假 <see cref="HttpContext"/>,让期间经同一 DI 容器解析的 <c>ICurrentUser</c>
    /// (<c>HttpContextCurrentUser</c>)读到 <c>UserId</c>/<c>TenantId</c>/<c>IsSuperAdmin</c>。
    /// <para>带上 <c>sadm=true</c> 是必须的,不只是图方便:<c>RbacService.EnsureSuperAdmin</c>/
    /// <c>RoleGrantPolicy.EnsureGrantableAsync</c> 这类守卫把"<c>currentUser</c> 为 null 或未认证"
    /// 同等看待为"可信系统上下文,不加限制"(与 <c>IDataScopeContext</c>"未显式设置=可信"同一约定,
    /// 两处文档注释都这么写)。这些测试夹具在 Task 7 之前一直是靠"没有 HttpContext ⇒ 未认证 ⇒ 免检"
    /// 这条路径跑通的;真做成"已认证但非超管"反而会在这些完全不相关的守卫上新炸出
    /// SuperAdminRequired/UserOutOfDataScope——本类只想解决 ITenantScoped 过滤器这一件事,
    /// 不该顺带收紧这些既有测试从未打算测的其它权限维度,故用超管身份维持原来的"不受限"效果。</para>
    /// <para><see cref="IHttpContextAccessor"/> 是 Singleton(<c>AddHttpContextAccessor</c> 注册),
    /// 内部用 <c>AsyncLocal</c> 存 HttpContext——设置只沿"当前异步调用链"传播,不会泄漏到其它并发测试,
    /// 也不会残留到测试自身后续真实发起的 HTTP 请求(TestServer 处理每个请求时会设自己的 HttpContext)。
    /// 仍应只在建/查夹具这一小段代码内 <c>using</c>,用完即释放——不要跨 <c>await c.PostJson(...)</c> 持有。</para>
    /// </summary>
    public static IDisposable Use(IServiceProvider services, long tenantId, long userId = 1)
    {
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        var identity = new ClaimsIdentity(
        [
            new Claim("sub", userId.ToString()),
            new Claim(TokenClaimNames.TENANT_ID, tenantId.ToString()),
            new Claim(TokenClaimNames.SUPER_ADMIN, "true"),
        ], authenticationType: "TestTenantContext");
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        return new Restorer(accessor, previous);
    }

    private sealed class Restorer(IHttpContextAccessor accessor, HttpContext? previous) : IDisposable
    {
        public void Dispose() => accessor.HttpContext = previous;
    }
}
