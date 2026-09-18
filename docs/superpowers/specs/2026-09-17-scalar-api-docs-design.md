# 内核内置 Scalar API 文档 UI

日期:2026-09-17
范围:`backend/src/SmartAdmin.AspNetCore`(依赖、路由映射、生产鉴权、Options)、
`backend/src/SmartAdmin.Core`(Options 定义)、`backend/src/SmartAdmin.Services`(菜单种子)、
`backend/Directory.Packages.props`、`web/packages/admin`(新增一个入口页)、
约束类文档(CLAUDE.md 等多处)。
不涉及:多 OpenAPI 文档/AsyncAPI 聚合、Scalar 的 CDN bundle 切换选项、自定义路由前缀配置项、消费者自定义鉴权扩展点。

## 1. 背景与目标

`/openapi/v1.json` 已内置(`SmartAdminSetup.cs:299-306` 生成、`339-343` 映射),
但只生成契约 JSON,没有可视化 UI,且仅开发环境暴露。
目标:内核直接内置 [Scalar](https://github.com/scalar/scalar) 作为 API 文档可视化 UI,
消费端调用 `AddSmartAdmin`/`MapSmartAdmin` 即自动获得,开发环境零配置可用,
生产环境可显式开启并有完整 RBAC 网关。

## 2. 依赖:核心运行时依赖红线的例外

`Scalar.AspNetCore`(现版本 2.17.4)在 nuget.org 三个目标框架(net8.0/net9.0/net10.0)下均为
**"No dependencies"**——零传递依赖的 MIT 包,UI 资源默认内嵌在包内
(仅默认字体走 CDN,可用 `DisableDefaultFonts()` 关掉)。

`backend/Directory.Packages.props` 新增:

```xml
<PackageVersion Include="Scalar.AspNetCore" Version="2.17.4" />
```

`backend/src/SmartAdmin.AspNetCore/SmartAdmin.AspNetCore.csproj` 新增引用,注释写清例外范围:

```xml
<!-- 内置 Scalar API 文档 UI:核心包"只依赖 SqlSugarCore + Microsoft.*"这条红线目前唯一的第三方例外。
     零传递依赖、UI 资源本地内嵌、只做端点映射渲染,不参与序列化/存储/鉴权等核心逻辑。见 docs/adr/00xx-scalar-in-core.md -->
<PackageReference Include="Scalar.AspNetCore" />
```

这是该约束第一次对核心四包本身开例外(此前的例外都是
`SmartAdmin.Excel`/`SmartAdmin.Caching.Redis`/`SmartAdmin.Auth.*` 这类独立可选包,不碰核心)。
需要同步更新的文档:根 `CLAUDE.md`、`backend/CLAUDE.md`、`docs/rebuild-design.md` §2.3、
`docs/coding-standards.md`、`site/standard/backend.md`(中/英)、
`site/backend/architecture.md` 的 "Runtime dependency red line" 框、`site/public/llms.txt`。
按 `docs/adr/README.md` 的判据("将来有人会重新提出被否掉的那个方案"——大概率有人会问"为什么不做成可选包"),
这次决策应该配一份 ADR,编号顺延现有 0009 之后。

## 3. 路由映射与环境门控

`SmartAdminSetup.cs` 的 `MapSmartAdmin`,紧接现有 339-343 行的 OpenAPI 映射块:

```csharp
var env = endpoints.ServiceProvider.GetService<IHostEnvironment>();
var scalarOptions = endpoints.ServiceProvider.GetService<AdminScalarOptions>();
// env is null(裸容器/测试场景)沿用现状语义,按"非生产"兜底——与既有 339-343 行判断一致。
var isDevLike = env is null || env.IsDevelopment();
var exposeDocs = isDevLike || scalarOptions?.EnabledInProduction == true;

if (exposeDocs)
{
    // Scalar 壳页面:纯静态 JS/CSS 渲染器,不含契约数据,始终匿名——见第 4 节的鉴权边界划分。
    endpoints.MapScalarApiReference("/scalar").AllowAnonymous();

    // OpenAPI 契约 JSON:真正的敏感数据。开发环境(含 env is null 兜底)匿名不变;
    // 生产环境显式开启时收紧为 ScalarAccess 策略(见第 4 节)。
    var openApiBuilder = endpoints.MapOpenApi();
    if (isDevLike)
        openApiBuilder.AllowAnonymous();
    else
        openApiBuilder.RequireAuthorization(ScalarAccessRequirement.PolicyName);
}
```

路由固定用包默认值 `/scalar`(不做自定义前缀配置项,YAGNI)。`/openapi/v1.json` 路由不变。

## 4. 生产鉴权:为什么不能直接复用 `[RolePermission]`,以及不能用 query-string token

**两个已排除的方案,及排除理由(供以后不重新踩坑)**:

1. **直接挂 `[RolePermission]`**——它是 `IAsyncAuthorizationFilter`
(`RolePermissionAttribute.cs:23`),只在 MVC 控制器 action 管线里生效。
`MapScalarApiReference`/`MapOpenApi` 是 Minimal API 端点,走 ASP.NET Core 授权中间件的 Policy 机制,
两条管道不通用,属性挂不上去。
2. **仿照 SignalR 的 `?access_token=` query-string 兜底**
(`SmartAdminSetup.cs:209-216`,目前只对 `options.Realtime.HubPath` 生效)——
这套 JWT 认证是纯 Bearer Header(`AddJwtBearer`,`SmartAdminSetup.cs:188`),没有 Cookie 会话,
浏览器裸导航/`window.open` 新标签页不会带 SPA 里存的 Bearer token,直接 401。
query-string 方案能绕过这个问题,但会把长效 JWT 明文塞进 URL,
留痕浏览器历史与服务端访问日志,是新增的安全异味,予以排除。

**采用的方案**:把鉴权边界划在"壳"和"数据"之间——`/scalar` 壳页面本身没有契约数据,始终匿名;
只有 `/openapi/{documentname}.json`(真正的接口契约,即现有注释说的"侦察面")在生产开启时才收紧。
Scalar 自带 Authentication 面板支持配置 Bearer 认证(`options.AddHttpAuthentication(...)`),
管理员打开 `/scalar` 后手动粘贴自己当前的接口令牌,
Scalar 内部再拿这个令牌请求 `/openapi/{documentname}.json`——完全复用现有 Bearer Header 管线,不发明新机制。

新增一个 Minimal API 专用的授权 Policy,判定逻辑与 `RolePermissionAttribute` 一致
(已认证 → 会话仍活跃 → 超管放行 → 否则比对权限码),但走 `IAuthorizationHandler`:

```csharp
// SmartAdmin.AspNetCore/Security/ScalarAccessRequirement.cs
public sealed class ScalarAccessRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "ScalarAccess";
}

// SmartAdmin.AspNetCore/Security/ScalarAccessAuthorizationHandler.cs
public class ScalarAccessAuthorizationHandler : AuthorizationHandler<ScalarAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScalarAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext) return;
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true) return;

        var sessionId = user.FindFirstValue(TokenClaimNames.SESSION_ID);
        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessions = httpContext.RequestServices.GetRequiredService<ISessionService>();
            if (!await sessions.IsActiveAsync(sessionId)) return;
        }

        if (user.HasClaim(TokenClaimNames.SUPER_ADMIN, "true"))
        {
            context.Succeed(requirement);
            return;
        }

        if (!long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            return;

        var template = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var code = PermissionCode.Build(httpContext.Request.Method,
            template ?? httpContext.Request.Path.Value);
        var codes = await httpContext.RequestServices.GetRequiredService<IPermissionProvider>()
            .GetPermissionCodesAsync(userId, httpContext.RequestAborted);
        if (codes.Contains(code)) context.Succeed(requirement);
    }
}
```

`AddSmartAdmin` 里注册:

```csharp
services.AddAuthorizationBuilder()
    .AddPolicy(ScalarAccessRequirement.PolicyName,
        p => p.AddRequirements(new ScalarAccessRequirement()));
services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler,
    ScalarAccessAuthorizationHandler>());
```

实现时需核对 `PermissionCode.Build` 对 `RoutePattern.RawText` 这类 Minimal API 路由模板的
规范化结果是否与 `/openapi/{documentname}.json`(小写)一致——`PermissionCode.Build` 已知逻辑是
`{METHOD}:/{template.ToLowerInvariant()}`(`PermissionCode.cs:19-20`),
`RawText` 原样是 `/openapi/{documentName}.json`,小写化后应为 `/openapi/{documentname}.json`,
与种子里手写的码必须逐字符一致。

## 5. 权限码与菜单种子

`DefaultMenuSeed.cs` 在 300(系统运维)目录下新增一个 **Menu 类型**页面节点(不是 Button——
Scalar 壳页面匿名不需要权限码,但要走"角色-菜单"UI 才能被消费端管理员发现/授权,
权限码挂在指向真实数据的 `/openapi/{documentname}.json` 上):

```csharp
new SysMenu
{
    Id = 302, ParentId = 300, Type = MenuType.Menu, Title = "接口文档",
    Permission = "GET:/openapi/{documentname}.json",
    Path = "/system/api-docs", Component = "system/api-docs/index",
    Icon = "ph:book-open-text-duotone", Sort = 2, Enabled = true, Visible = true,
},
```

(具体 Id 取号在实现时核对 300 目录下现有子节点占用情况,301 已被"连通性探针"占用,
层级规则见 `MenuSeedIdLayoutTests.cs:28`:`Id % 10 != 0 且 Id / 10 == ParentId / 10`。)

**测试一致性缺口**:`PermissionCodeConsistencyTests.BuiltInEndpointCodes()`
(`backend/tests/SmartAdmin.Tests/PermissionCodeConsistencyTests.cs:43-67`)
目前只反射 MVC 控制器上的 `[RolePermission]`,不会发现这个走 `ScalarAccess` policy 的 Minimal API 端点。
直接加这颗种子会让 `Every_seeded_permission_code_maps_to_a_real_endpoint` 失败
(种子码找不到匹配的反射端点)。需要在 `BuiltInEndpointCodes()` 里补一条硬编码:

```csharp
// ScalarAccess policy 端点,非 [RolePermission],见 spec
codes.Add(PermissionCode.Build("GET", "/openapi/{documentname}.json"));
```

## 6. 前端:入口页与令牌复制

新增内核内置页面(照抄 `web/CLAUDE.md` 描述的内核页面组织方式):`views/system/api-docs/index.vue`,
`Component` 值即 `system/api-docs/index`,和第 5 节的菜单种子一一对应。

进入页面即执行:
- `window.open(`${apiBase}/scalar`, '_blank')`——`apiBase` 复用
`createSmartAdmin({ apiBase })` 已注入的现有配置
(`web/template/src/main.ts:9-15` 已有的 `VITE_API_BASE` 机制),不新增基址配置项。
- 页面正文:一句提示"接口文档已在新标签页打开"+ 手动兜底链接(浏览器拦截弹窗时用)。
- 一个"复制我的接口令牌"按钮:读当前登录态里已持有的 access token 写入剪贴板,
旁边一行说明"在新标签页里点右上角 Authentication → Bearer,粘贴这里复制的令牌"。
生产环境未开启 `EnabledInProduction` 时(即两个端点按第 3 节都没挂载),
新标签页打开的 `/scalar` 直接收到 404;这颗按钮和提示文案不需要因此做环境判断隐藏——
空跑一次点击不产生副作用,维护成本比让前端感知后端环境开关更低。

## 7. 配置项

`backend/src/SmartAdmin.Core/Options/AdminScalarOptions.cs`(照抄 `AdminRealtimeOptions` 的结构):

```csharp
public class AdminScalarOptions
{
    /// <summary>生产环境是否显式开启 Scalar/OpenAPI(默认关)。开发环境不受此项影响,始终暴露。</summary>
    public bool EnabledInProduction { get; set; }
}
```

`SmartAdminOptions` 新增 `public AdminScalarOptions Scalar { get; set; } = new();`,
对应配置节 `SmartAdmin:Scalar:EnabledInProduction`。

## 8. 测试策略

新增契约测试(照抄 `OpenApiContractTests.cs` 风格):
- 开发环境:`/scalar` 匿名 200;`/openapi/v1.json` 匿名 200(现状不变)。
- 生产环境 + `EnabledInProduction=false`(默认):两个端点均未映射(404)。
- 生产环境 + `EnabledInProduction=true`:`/scalar` 匿名 200;
`/openapi/v1.json` 未登录 401、登录但无权限码 403、有权限码或超管 200。
- `PermissionCodeConsistencyTests` 两个既有用例(种子↔反射双向一致)继续跑绿,验证第 5 节的补丁生效。

## 9. 不做的事(YAGNI)

不做自定义路由前缀配置项;不做多文档/AsyncAPI;
不碰 `WithBundleUrl` 之类的 CDN 切换选项(保持默认本地内嵌资源);
不新增令牌签发端点或 query-string token 机制(第 4 节已排除并说明理由);
不改 `[RolePermission]`/`RolePermissionAttribute` 本身去兼容 Minimal API
(新写一个独立的 Policy Handler,而不是硬拉两条管道合一)。
