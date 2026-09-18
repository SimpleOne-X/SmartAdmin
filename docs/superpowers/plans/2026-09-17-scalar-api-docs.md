# 内核内置 Scalar API 文档 UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消费端调用 `AddSmartAdmin`/`MapSmartAdmin` 即自动获得 Scalar API 文档 UI——开发环境零配置可用，生产环境默认关闭、显式开启后有完整 RBAC 网关。

**Architecture:** `Scalar.AspNetCore`(零依赖包)直接进 `SmartAdmin.AspNetCore`；`/scalar` 壳页面始终匿名，真正敏感的 `/openapi/{documentName}.json` 走一个新写的 Minimal API 授权 Policy（复用现有 `ISessionService`/`IPermissionProvider`，判定逻辑与 `RolePermissionAttribute` 一致）。前端加一个薄入口页，`window.open` 新标签页打开壳页面，并提供"复制我的接口令牌"辅助按钮供管理员手动粘贴进 Scalar 自带的 Authentication 面板。

**Tech Stack:** ASP.NET Core 10 Minimal API + `Microsoft.AspNetCore.Authorization` Policy、Scalar.AspNetCore 2.17.4、Vue 3 `<script setup>` + Naive UI + Pinia + VueUse（`@vueuse/core` 的 `useClipboard`）。

**Spec:** `docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md`

## Global Constraints

- `Scalar.AspNetCore` 版本锁定 `2.17.4`（nuget.org 三个目标框架均为零传递依赖）。
- 路由固定用包默认值 `/scalar`，不做自定义前缀配置项（spec §3，YAGNI）。
- `/scalar` 壳页面**始终匿名**（开发/生产都一样）；只有 `/openapi/{documentName}.json` 在生产开启时收紧（spec §4）。
- 不做 query-string token 机制、不新增令牌签发端点（spec §4 已排除并说明理由）。
- `SmartAdmin:Scalar:EnabledInProduction` 默认 `false`（spec §7）。
- 本仓库测试不用任何 Mock 框架（无 Moq/NSubstitute），一律走 `AdminAppFactory` 真实集成测试或手写 fake（`backend/tests/SmartAdmin.Tests` 现有约定）。
- 菜单种子 Id 布局规则（`MenuSeedIdLayoutTests.cs:28`）：`MenuType.Menu` 挂在 `MenuType.Catalog` 下时，`Id % 10 == 0 && Id % 100 != 0 && Id / 100 == ParentId / 100`。
- 提交信息中文、conventional-commit 格式（`type(scope): 主题`），结尾带 `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`。

---

## Task 1: `AdminScalarOptions` 配置项

**Files:**
- Create: `backend/src/SmartAdmin.Core/Options/AdminScalarOptions.cs`
- Modify: `backend/src/SmartAdmin.Core/Options/SmartAdminOptions.cs:53`
- Modify: `backend/src/SmartAdmin.Services/SmartAdminOptionsSetup.cs:45`
- Test: `backend/tests/SmartAdmin.Tests/ScalarDocsTests.cs`(新建)

**Interfaces:**
- Produces:`AdminScalarOptions { bool EnabledInProduction }`（`SmartAdmin.Core` 命名空间），`SmartAdminOptions.Scalar` 属性，`AddSmartAdminOptions` 里以 `TryAddSingleton` 注册，配置节 `SmartAdmin:Scalar:EnabledInProduction`。后续任务据此读取生产开关。

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/SmartAdmin.Tests/ScalarDocsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

public class ScalarDocsTests
{
    [Fact]
    public void AdminScalarOptions_defaults_to_disabled_and_resolves_via_DI()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminOptions(new SmartAdminOptions());
        using var sp = services.BuildServiceProvider();

        var scalar = sp.GetRequiredService<AdminScalarOptions>();

        Assert.False(scalar.EnabledInProduction);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ScalarDocsTests*"`
Expected: 编译失败——`AdminScalarOptions` 类型不存在。

- [ ] **Step 3: 新建 Options 类**

创建 `backend/src/SmartAdmin.Core/Options/AdminScalarOptions.cs`（照抄 `AdminRealtimeOptions.cs` 结构）:

```csharp
namespace SmartAdmin.Core;

/// <summary>
/// Scalar API 文档 UI 配置(对应 <c>SmartAdmin:Scalar</c> 节)。
/// <para><c>/scalar</c> 壳页面始终匿名(无契约数据);<c>/openapi/{documentName}.json</c> 开发环境匿名不变,
/// 生产环境默认不挂载,<see cref="EnabledInProduction"/> 显式开启后收紧为 <c>ScalarAccess</c> 授权策略。</para>
/// </summary>
public class AdminScalarOptions
{
    /// <summary>生产环境是否显式开启 Scalar/OpenAPI(默认关)。开发环境不受此项影响,始终暴露。</summary>
    public bool EnabledInProduction { get; set; }
}
```

- [ ] **Step 4: `SmartAdminOptions` 加属性**

修改 `backend/src/SmartAdmin.Core/Options/SmartAdminOptions.cs`,在第 53 行(`Ai` 属性)之后插入:

```csharp
    /// <summary>Scalar API 文档 UI 配置(见 <see cref="AdminScalarOptions"/>;对应 <c>SmartAdmin:Scalar</c>)</summary>
    public AdminScalarOptions Scalar { get; set; } = new();
```

- [ ] **Step 5: 注册进容器**

修改 `backend/src/SmartAdmin.Services/SmartAdminOptionsSetup.cs`,在第 45 行(`services.TryAddSingleton(options.Ai);`)之后插入:

```csharp
        services.TryAddSingleton(options.Scalar);
```

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ScalarDocsTests*"`
Expected: PASS

- [ ] **Step 7: 提交**

```bash
git add backend/src/SmartAdmin.Core/Options/AdminScalarOptions.cs backend/src/SmartAdmin.Core/Options/SmartAdminOptions.cs backend/src/SmartAdmin.Services/SmartAdminOptionsSetup.cs backend/tests/SmartAdmin.Tests/ScalarDocsTests.cs
git commit -m "$(cat <<'EOF'
feat(backend): 新增 AdminScalarOptions 配置项

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: 依赖、生产鉴权 Policy 与路由映射

**Files:**
- Modify: `backend/Directory.Packages.props:16`(在 `Microsoft.OpenApi` 之后插入)
- Modify: `backend/src/SmartAdmin.AspNetCore/SmartAdmin.AspNetCore.csproj:18`(在 `Microsoft.OpenApi` 引用之后插入)
- Create: `backend/src/SmartAdmin.AspNetCore/Security/ScalarAccessAuthorization.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/SmartAdminSetup.cs:228`(`AddSmartAdmin` 里的 `services.AddAuthorization();`)
- Modify: `backend/src/SmartAdmin.AspNetCore/SmartAdminSetup.cs:339-343`(`MapSmartAdmin` 里的 OpenAPI 映射块)
- Test: `backend/tests/SmartAdmin.Tests/ScalarDocsTests.cs`(追加)

**Interfaces:**
- Consumes:Task 1 的 `AdminScalarOptions.EnabledInProduction`。
- Produces:`ScalarAccessRequirement.PolicyName`(常量 `"ScalarAccess"`)、`ScalarAccessAuthorizationHandler`;路由 `GET /scalar`(始终匿名)、`GET /openapi/{documentName}.json`(生产开启时走 `ScalarAccess` policy)。Task 3 的菜单种子权限码必须与这里的路由模板字符串逐字符一致。

- [ ] **Step 1: 写失败测试(先写,此时端点还没挂,预期全部落空)**

在 `backend/tests/SmartAdmin.Tests/ScalarDocsTests.cs` 追加:

```csharp
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;
```

(把这几个 using 加到文件顶部,与已有的 `Microsoft.Extensions.DependencyInjection`/`SmartAdmin.Core`/`SmartAdmin.Services` 合并、去重。)

```csharp
    private static HttpClient WithToken(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>在宿主内造一个挂了指定权限码的临时菜单按钮 + 角色 + 用户,返回其登录账号/密码。</summary>
    private static async Task<(string account, string password)> SeedUserWithPermission(AdminAppFactory f, string permissionCode)
    {
        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var menus = sp.GetRequiredService<IRepository<SysMenu>>();
        var roles = sp.GetRequiredService<IRepository<SysRole>>();
        var rbac = sp.GetRequiredService<IRbacService>();
        var users = sp.GetRequiredService<IUserService>();

        var menu = new SysMenu
        {
            ParentId = 300, Type = MenuType.Button, Title = "测试-" + permissionCode,
            Permission = permissionCode, Enabled = true, Visible = true,
        };
        await menus.InsertAsync(menu);

        var role = new SysRole { Name = "受限角色", Code = "limited-" + Guid.CreateVersion7().ToString("N")[..8], Enabled = true };
        await roles.InsertAsync(role);
        await rbac.SetRoleMenusAsync(role.Id, [menu.Id]);

        var account = "limited-" + Guid.CreateVersion7().ToString("N")[..8];
        const string password = "Limited@123456";
        await users.AddAsync(new AddUserInput { Account = account, Password = password, Name = "受限用户", Enabled = true, RoleIds = [role.Id] });
        return (account, password);
    }

    [Fact]
    public async Task Development_exposes_scalar_and_openapi_anonymously()
    {
        using var f = new AdminAppFactory();   // 默认 Development
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/scalar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Production_without_opt_in_maps_neither_endpoint()
    {
        using var f = new AdminAppFactory { EnvironmentName = "Production" };
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/scalar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Production_opt_in_gates_openapi_json_but_leaves_scalar_shell_anonymous()
    {
        var settings = new Dictionary<string, string?> { ["SmartAdmin:Scalar:EnabledInProduction"] = "true" };
        using var f = new AdminAppFactory { EnvironmentName = "Production", Settings = settings };

        // 壳页面始终匿名,不受下面的鉴权场景影响
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/scalar")).StatusCode);

        // 未登录 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/openapi/v1.json")).StatusCode);

        // 登录但权限码不对 → 403
        var wrongClient = f.CreateClient();
        var (wrongAccount, wrongPassword) = await SeedUserWithPermission(f, "GET:/api/v1/ping");
        WithToken(wrongClient, await wrongClient.LoginToken(wrongAccount, wrongPassword));
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongClient.GetAsync("/openapi/v1.json")).StatusCode);

        // 登录且权限码正确 → 200
        var rightClient = f.CreateClient();
        var (rightAccount, rightPassword) = await SeedUserWithPermission(f, "GET:/openapi/{documentname}.json");
        WithToken(rightClient, await rightClient.LoginToken(rightAccount, rightPassword));
        Assert.Equal(HttpStatusCode.OK, (await rightClient.GetAsync("/openapi/v1.json")).StatusCode);

        // 超管无视权限码 → 200
        var adminClient = f.CreateClient();
        WithToken(adminClient, await adminClient.LoginToken("superAdmin", AdminAppFactory.DefaultAdminPassword));
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/openapi/v1.json")).StatusCode);
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ScalarDocsTests*"`
Expected: `Development_exposes_scalar_and_openapi_anonymously` 失败(断言 `/scalar` 200,实际现状是 404——`/scalar` 端点还没挂)。`Production_without_opt_in_maps_neither_endpoint` 会意外通过(现状 `/openapi/v1.json` 在生产环境本就是 404,`/scalar` 更是压根没挂,两个断言凑巧都成立),这是本次改动前的真实状态,不代表实现已完成。`Production_opt_in_gates_openapi_json_but_leaves_scalar_shell_anonymous` 失败(`/scalar` 断言 200,实际 404)。Step 6 做完后三个用例应全部按设计变绿。

- [ ] **Step 3: 加依赖**

修改 `backend/Directory.Packages.props`,在第 16 行(`Microsoft.OpenApi`)之后插入:

```xml
    <PackageVersion Include="Scalar.AspNetCore" Version="2.17.4" />
```

修改 `backend/src/SmartAdmin.AspNetCore/SmartAdmin.AspNetCore.csproj`,在第 18 行(`Microsoft.OpenApi` 引用)之后插入:

```xml
    <!-- 内置 Scalar API 文档 UI:核心包"只依赖 SqlSugarCore + Microsoft.*"这条红线目前唯一的第三方例外。
         零传递依赖、UI 资源本地内嵌、只做端点映射渲染,不参与序列化/存储/鉴权等核心逻辑。
         见 docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md -->
    <PackageReference Include="Scalar.AspNetCore" />
```

- [ ] **Step 4: 新建授权 Requirement + Handler**

创建 `backend/src/SmartAdmin.AspNetCore/Security/ScalarAccessAuthorization.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// <c>/scalar</c>(壳页面,始终匿名)与 <c>/openapi/{documentName}.json</c>(契约数据,生产开启时收紧)
/// 的生产环境授权网关。
/// <para><see cref="RolePermissionAttribute"/> 是 MVC <c>IAsyncAuthorizationFilter</c>,只在控制器 action
/// 管线生效;这两个是 Minimal API 端点,走 ASP.NET Core 授权中间件的 Policy 机制,两条管道不通用,
/// 故另起一份判定逻辑(已认证 → 会话仍活跃 → 超管放行 → 否则比对权限码),规则与
/// <see cref="RolePermissionAttribute"/> 保持一致但各自独立实现。</para>
/// </summary>
public sealed class ScalarAccessRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "ScalarAccess";
}

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

        if (!long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)) return;

        var template = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var code = PermissionCode.Build(httpContext.Request.Method, template ?? httpContext.Request.Path.Value);
        var codes = await httpContext.RequestServices.GetRequiredService<IPermissionProvider>()
            .GetPermissionCodesAsync(userId, httpContext.RequestAborted);
        if (codes.Contains(code)) context.Succeed(requirement);
    }
}
```

- [ ] **Step 5: 在 `AddSmartAdmin` 里注册 Policy + Handler**

修改 `backend/src/SmartAdmin.AspNetCore/SmartAdminSetup.cs`,把第 226-228 行:

```csharp
        // 默认拒绝走 MapControllers().RequireAuthorization()(见 MapSmartAdmin),只作用于真实控制器端点、
        // 尊重 [AllowAnonymous],且不影响未匹配路由的 404(FallbackPolicy 会把 404 劫持成 401,故不用它)。
        services.AddAuthorization();
```

替换为:

```csharp
        // 默认拒绝走 MapControllers().RequireAuthorization()(见 MapSmartAdmin),只作用于真实控制器端点、
        // 尊重 [AllowAnonymous],且不影响未匹配路由的 404(FallbackPolicy 会把 404 劫持成 401,故不用它)。
        // ScalarAccess:生产环境显式开启时网关 /openapi/{documentName}.json(见 MapSmartAdmin、
        // ScalarAccessAuthorizationHandler)。
        services.AddAuthorizationBuilder()
            .AddPolicy(ScalarAccessRequirement.PolicyName, p => p.AddRequirements(new ScalarAccessRequirement()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, ScalarAccessAuthorizationHandler>());
```

(若文件顶部尚未 `using Microsoft.Extensions.DependencyInjection.Extensions;`,补上——`TryAddEnumerable` 需要它。)

- [ ] **Step 6: 改 `MapSmartAdmin` 的路由映射**

把第 339-343 行:

```csharp
        // OpenAPI 文档:仅开发环境暴露(生产匿名开放会泄露完整 API 契约作侦察面);
        // 匿名可访问(否则被上面的 FallbackPolicy 挡成 401)。契约源本就是开发期前端代码生成用。
        var env = endpoints.ServiceProvider.GetService<IHostEnvironment>();
        if (env is null || env.IsDevelopment())
            endpoints.MapOpenApi().AllowAnonymous();
```

替换为:

```csharp
        // OpenAPI 文档 UI(Scalar)与契约 JSON:开发环境(含 env is null 兜底)全部匿名暴露,契约源本就是
        // 开发期前端代码生成用。生产环境默认都不挂载(避免匿名开放泄露完整 API 契约作侦察面);显式开启
        // (SmartAdmin:Scalar:EnabledInProduction)时,/scalar 壳页面(无契约数据)仍匿名,只有
        // /openapi/{documentName}.json 收紧到 ScalarAccess 策略——鉴权边界划在"壳"与"数据"之间,见 spec。
        var env = endpoints.ServiceProvider.GetService<IHostEnvironment>();
        var scalarOptions = endpoints.ServiceProvider.GetService<AdminScalarOptions>();
        var isDevLike = env is null || env.IsDevelopment();
        if (isDevLike || scalarOptions?.EnabledInProduction == true)
        {
            endpoints.MapScalarApiReference("/scalar").AllowAnonymous();

            var openApiBuilder = endpoints.MapOpenApi();
            if (isDevLike)
                openApiBuilder.AllowAnonymous();
            else
                openApiBuilder.RequireAuthorization(ScalarAccessRequirement.PolicyName);
        }
```

(若文件顶部尚未 `using Scalar.AspNetCore;`,补上——`MapScalarApiReference` 是该命名空间的扩展方法。)

- [ ] **Step 7: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ScalarDocsTests*"`
Expected: PASS 全部四个用例。

- [ ] **Step 8: 跑一遍既有 OpenAPI 契约测试,确认没有回归**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*OpenApiContractTests*"`
Expected: PASS(开发环境 `/openapi/v1.json` 行为不变)。

- [ ] **Step 9: 提交**

```bash
git add backend/Directory.Packages.props backend/src/SmartAdmin.AspNetCore/SmartAdmin.AspNetCore.csproj backend/src/SmartAdmin.AspNetCore/Security/ScalarAccessAuthorization.cs backend/src/SmartAdmin.AspNetCore/SmartAdminSetup.cs backend/tests/SmartAdmin.Tests/ScalarDocsTests.cs
git commit -m "$(cat <<'EOF'
feat(backend): 内置 Scalar API 文档 UI,生产环境按权限码网关

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: 菜单种子 + 权限码一致性测试补丁

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs:163`(300 目录下,`380 缓存管理` 之后)
- Modify: `backend/tests/SmartAdmin.Tests/PermissionCodeConsistencyTests.cs:43-67`(`BuiltInEndpointCodes()`)

**Interfaces:**
- Consumes:Task 2 里 `/openapi/{documentName}.json` 端点实际挂载的路由模板(必须逐字符一致,否则种子码授了也匹配不上)。

- [ ] **Step 1: 加菜单种子行**

修改 `backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs`,在第 163 行(`Id = 380` 缓存管理)之后插入:

```csharp
        new SysMenu { Id = 390, ParentId = 300, Type = MenuType.Menu, Title = "接口文档", Permission = "GET:/openapi/{documentname}.json", Path = "/system/api-docs", Component = "system/api-docs/index", Icon = "ph:book-open-text-duotone", Sort = 9, Enabled = true, Visible = true },
```

- [ ] **Step 2: 跑菜单 Id 布局测试,确认新行合规**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*MenuSeedIdLayoutTests*"`
Expected: PASS(`390 % 10 == 0 && 390 % 100 != 0 && 390 / 100 == 300 / 100`,合规)。

- [ ] **Step 3: 跑权限码一致性测试,确认按预期失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*PermissionCodeConsistencyTests*"`
Expected: `Every_seeded_permission_code_maps_to_a_real_endpoint` 失败——种子里的 `GET:/openapi/{documentname}.json` 在 `BuiltInEndpointCodes()`(只反射 `[RolePermission]` 控制器)里找不到匹配项。

- [ ] **Step 4: 给 `BuiltInEndpointCodes()` 补一条硬编码**

修改 `backend/tests/SmartAdmin.Tests/PermissionCodeConsistencyTests.cs`,把第 65-67 行:

```csharp
                }
            }
        }
        return codes;
    }
```

替换为:

```csharp
                }
            }
        }

        // ScalarAccess policy 端点(Minimal API,不挂 [RolePermission],鉴权走独立的
        // ScalarAccessAuthorizationHandler)。见 docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md。
        codes.Add(PermissionCode.Build("GET", "/openapi/{documentname}.json"));

        return codes;
    }
```

- [ ] **Step 5: 跑权限码一致性测试,确认转绿**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*PermissionCodeConsistencyTests*"`
Expected: PASS。若 `Every_seeded_permission_code_maps_to_a_real_endpoint` 仍红,大概率是 `RoutePattern.RawText` 的大小写/格式与 `PermissionCode.Build` 的小写化结果不完全一致——把失败信息里打印出的实际差异码,原样改进 Step 1 的种子 `Permission` 字段和 Step 4 的硬编码,直至两边字符串逐字符相同。

- [ ] **Step 6: 跑一遍 Task 2 的 `ScalarDocsTests`,确认种子改动没有连带破坏**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ScalarDocsTests*"`
Expected: PASS(不应受菜单种子改动影响,纯粹回归确认)。

- [ ] **Step 7: 提交**

```bash
git add backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs backend/tests/SmartAdmin.Tests/PermissionCodeConsistencyTests.cs
git commit -m "$(cat <<'EOF'
feat(backend): 接口文档菜单种子,补权限码一致性测试白名单

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: 前端入口页

**Files:**
- Create: `web/packages/admin/src/views/system/api-docs/index.vue`
- Modify: `web/packages/admin/src/locales/zh-CN.ts`(新增 `apiDocs` 段)
- Modify: `web/packages/admin/src/locales/en-US.ts`(新增 `apiDocs` 段,与 zh-CN 键一一对应)
- Test: `web/packages/admin/src/views/system/api-docs/index.spec.ts`

**Interfaces:**
- Consumes:`runtime.apiBase`(`#/lib/runtime`)、`useUserStore().accessToken`(`#/stores/user`)——均为既有导出,不新增。菜单 `Component` 值 `system/api-docs/index` 必须与 Task 3 种子里的 `Component` 字段完全一致(视图注册表按这个字符串找组件)。

- [ ] **Step 1: 写失败测试**

创建 `web/packages/admin/src/views/system/api-docs/index.spec.ts`:

```typescript
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, type App } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { runtime } from '#/lib/runtime'
import { useUserStore } from '#/stores/user'
import ApiDocsPage from './index.vue'

let app: App<Element> | undefined

afterEach(() => {
  app?.unmount()
  app = undefined
  runtime.apiBase = ''
})

describe('ApiDocsPage', () => {
  it('挂载即在新标签页打开 /scalar,兜底链接指向同一地址', () => {
    runtime.apiBase = 'https://api.example.test'
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)

    const pinia = createPinia()
    setActivePinia(pinia)
    const userStore = useUserStore()
    userStore.accessToken = 'test-access-token'

    const i18n = createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: {
        'zh-CN': {
          apiDocs: {
            title: '接口文档',
            openedHint: '接口文档已在新标签页打开',
            fallbackLink: '没有自动打开?点此手动打开',
            copyToken: '复制我的接口令牌',
            copyTokenHint: '在新标签页里点右上角 Authentication → Bearer,粘贴这里复制的令牌',
            tokenCopied: '已复制',
          },
        },
      },
    })

    const host = document.createElement('div')
    app = createApp(ApiDocsPage)
    app.use(pinia)
    app.use(i18n)
    app.mount(host)

    expect(openSpy).toHaveBeenCalledWith('https://api.example.test/scalar', '_blank')
    expect(host.querySelector('a')?.getAttribute('href')).toBe('https://api.example.test/scalar')
    expect(host.textContent).toContain('接口文档已在新标签页打开')
  })
})
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd web/packages/admin && npx vitest run src/views/system/api-docs/index.spec.ts`
Expected: FAIL——`./index.vue` 不存在。

- [ ] **Step 3: 写页面组件**

创建 `web/packages/admin/src/views/system/api-docs/index.vue`:

```vue
<script setup lang="ts">
import { NButton, NCard, NSpace } from 'naive-ui'
import { useClipboard } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { runtime } from '#/lib/runtime'
import { useUserStore } from '#/stores/user'

const { t } = useI18n()
const userStore = useUserStore()
const { copy, copied } = useClipboard()

const scalarUrl = `${runtime.apiBase}/scalar`

function copyToken() {
  copy(userStore.accessToken)
}

window.open(scalarUrl, '_blank')
</script>

<template>
  <NCard :title="t('apiDocs.title')">
    <NSpace vertical size="large">
      <p>{{ t('apiDocs.openedHint') }}</p>
      <NButton tag="a" :href="scalarUrl" target="_blank">{{ t('apiDocs.fallbackLink') }}</NButton>
      <NSpace align="center">
        <NButton @click="copyToken">{{ t('apiDocs.copyToken') }}</NButton>
        <span v-if="copied">{{ t('apiDocs.tokenCopied') }}</span>
      </NSpace>
      <p>{{ t('apiDocs.copyTokenHint') }}</p>
    </NSpace>
  </NCard>
</template>
```

- [ ] **Step 4: 加中英文案(键必须一一对应,否则 `locales/parity.spec.ts` 会红)**

在 `web/packages/admin/src/locales/zh-CN.ts` 里(参照已有的 `recycle: { ... }` 顶层段落写法)新增:

```typescript
  apiDocs: {
    title: '接口文档',
    openedHint: '接口文档已在新标签页打开。',
    fallbackLink: '没有自动打开?点此手动打开',
    copyToken: '复制我的接口令牌',
    copyTokenHint: '在新标签页里点右上角 Authentication → Bearer,粘贴这里复制的令牌',
    tokenCopied: '已复制',
  },
```

在 `web/packages/admin/src/locales/en-US.ts` 里对应位置新增:

```typescript
  apiDocs: {
    title: 'API Docs',
    openedHint: 'API docs opened in a new tab.',
    fallbackLink: "Didn't open automatically? Click here",
    copyToken: 'Copy my API token',
    copyTokenHint: 'In the new tab, click Authentication → Bearer in the top right and paste the token you just copied',
    tokenCopied: 'Copied',
  },
```

- [ ] **Step 5: 跑测试确认通过**

Run: `cd web/packages/admin && npx vitest run src/views/system/api-docs/index.spec.ts`
Expected: PASS

- [ ] **Step 6: 跑 locale 一致性测试 + typecheck + lint,确认没有连带破坏**

Run: `cd web/packages/admin && npx vitest run src/locales/parity.spec.ts`
Run: `cd web && npm run typecheck && npm run lint`
Expected: 全部 PASS。

- [ ] **Step 7: 提交**

```bash
git add web/packages/admin/src/views/system/api-docs/index.vue web/packages/admin/src/views/system/api-docs/index.spec.ts web/packages/admin/src/locales/zh-CN.ts web/packages/admin/src/locales/en-US.ts
git commit -m "$(cat <<'EOF'
feat(web): 接口文档入口页(打开 Scalar 壳页面 + 复制接口令牌)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: 约束文档更新(核心包第三方依赖例外)

**Files:**
- Modify: `CLAUDE.md`(根)
- Modify: `.github/copilot-instructions.md:20`
- Modify: `docs/coding-standards.md:36`
- Modify: `docs/rebuild-design.md:97,112-113`
- Modify: `site/standard/backend.md:12`
- Modify: `site/zh/standard/backend.md:12`
- Modify: `site/backend/architecture.md:62-63`
- Modify: `site/public/llms.txt:6`

**Interfaces:**
- 无代码接口,纯文档;全部指向同一个来源 `docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md`。

- [ ] **Step 1: 根 `CLAUDE.md`**

把:

```
Runtime deps are **only SqlSugarCore + Microsoft.\*** — no other third-party frameworks in the core packages.
```

改为:

```
Runtime deps are **only SqlSugarCore + Microsoft.\*** — no other third-party frameworks in the core packages, with one narrow, documented exception: `Scalar.AspNetCore` (zero transitive dependencies, UI rendering only, no business logic) is built into `SmartAdmin.AspNetCore` for the kernel's built-in API docs UI — see `docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md`.
```

- [ ] **Step 2: `.github/copilot-instructions.md:20`**

把:

```
5. **包依赖只能向下**:Core → SqlSugar → Services → AspNetCore。核心四包的运行时依赖只允许 SqlSugarCore + `Microsoft.*`,引第三方就得下沉到可选包。实体住在 `SmartAdmin.Services`,实体基类住在 `SmartAdmin.SqlSugar`,**不在 Core**。
```

改为:

```
5. **包依赖只能向下**:Core → SqlSugar → Services → AspNetCore。核心四包的运行时依赖只允许 SqlSugarCore + `Microsoft.*`,引第三方就得下沉到可选包(唯一例外:`Scalar.AspNetCore`,零传递依赖的内置 API 文档 UI,直接进 `SmartAdmin.AspNetCore`,见 spec)。实体住在 `SmartAdmin.Services`,实体基类住在 `SmartAdmin.SqlSugar`,**不在 Core**。
```

- [ ] **Step 3: `docs/coding-standards.md:36`**

把:

```
- 运行时依赖**仅** SqlSugarCore + Microsoft.\*，核心包不得引入其它第三方框架。
```

改为:

```
- 运行时依赖**仅** SqlSugarCore + Microsoft.\*，核心包不得引入其它第三方框架(唯一例外:`Scalar.AspNetCore`，零依赖的内置 API 文档 UI，见 `docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md`)。
```

- [ ] **Step 4: `docs/rebuild-design.md`**

把第 97 行:

```
 └─→ SmartAdmin.AspNetCore
```

改为:

```
 └─→ SmartAdmin.AspNetCore ──→ Scalar.AspNetCore(零依赖,内置 API 文档 UI;核心包内唯一的第三方例外)
```

把第 112-113 行:

```
> **核心四包(Core / SqlSugar / Services / AspNetCore)运行时依赖只允许 `SqlSugarCore` + `Microsoft.*`。**
> 一切其余要么自写、要么拷源、要么下沉到可选包 —— 具体逐库处置见 §2.3。
```

改为:

```
> **核心四包(Core / SqlSugar / Services / AspNetCore)运行时依赖只允许 `SqlSugarCore` + `Microsoft.*`,外加一个显式例外:`Scalar.AspNetCore`(零传递依赖,只做 API 文档 UI 渲染)。**
> 一切其余要么自写、要么拷源、要么下沉到可选包 —— 具体逐库处置见 §2.3。例外理由与范围见 `docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md`。
```

- [ ] **Step 5: `site/standard/backend.md:12`(英文站)**

把:

```
- Runtime dependencies are **only** SqlSugarCore + Microsoft.\* — core packages pull in no other third-party framework.
```

改为:

```
- Runtime dependencies are **only** SqlSugarCore + Microsoft.\* — core packages pull in no other third-party framework, except `Scalar.AspNetCore` (zero dependencies, the built-in API docs UI).
```

- [ ] **Step 6: `site/zh/standard/backend.md:12`(中文站)**

把:

```
- 运行时依赖只有 SqlSugarCore + Microsoft.\*，核心包不引入其它第三方框架。
```

改为:

```
- 运行时依赖只有 SqlSugarCore + Microsoft.\*，核心包不引入其它第三方框架，唯一例外是零依赖的 `Scalar.AspNetCore`（内置 API 文档 UI）。
```

- [ ] **Step 7: `site/backend/architecture.md:62-63`**

把第 62-63 行:

```
::: warning Runtime dependency red line
The core packages' only third-party runtime dependencies are SqlSugarCore + Microsoft.*. Capabilities that are usually pulled from third-party libraries — logging, snowflake IDs (typically Serilog, Yitter.IdGenerator) — instead ship as single-file implementations inside the kernel (`FileLoggerProvider`, `SnowflakeIdGenerator`), precisely to hold this line.
```

改为:

```
::: warning Runtime dependency red line
The core packages' only third-party runtime dependencies are SqlSugarCore + Microsoft.*. Capabilities that are usually pulled from third-party libraries — logging, snowflake IDs (typically Serilog, Yitter.IdGenerator) — instead ship as single-file implementations inside the kernel (`FileLoggerProvider`, `SnowflakeIdGenerator`), precisely to hold this line. The one documented exception is `Scalar.AspNetCore` (zero transitive dependencies, UI rendering only) for the built-in API docs UI.
```

- [ ] **Step 8: `site/public/llms.txt:6`**

把:

```
Runtime dependencies of the core packages: SqlSugarCore plus Microsoft.* only.
```

改为:

```
Runtime dependencies of the core packages: SqlSugarCore plus Microsoft.* only (one documented exception: the zero-dependency Scalar.AspNetCore, used for the built-in API docs UI).
```

- [ ] **Step 9: 跑文档相关的本地检查**

Run: `cd site && npm run lint:prose -- backend/architecture.md standard/backend.md zh/standard/backend.md`
Run: `cd site && npm run check:llms`
Expected: 全部 PASS。

- [ ] **Step 10: 提交**

```bash
git add CLAUDE.md .github/copilot-instructions.md docs/coding-standards.md docs/rebuild-design.md site/standard/backend.md site/zh/standard/backend.md site/backend/architecture.md site/public/llms.txt
git commit -m "$(cat <<'EOF'
docs: 记录核心包第三方依赖红线的 Scalar.AspNetCore 例外

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## 验收(全部任务完成后)

Run: `ci.bat`(SQLite + web + docs + template + audit,不用 Docker)
Expected: 全绿。这一条覆盖了前四个任务分别验证过的用例,外加 `template-smoke`(消费端零配置跑起来确实带上了 Scalar)与 `web` 的完整 lint/typecheck/vitest/build——本 plan 没有单独给 `template-smoke` 写用例,因为它是既有的端到端冒烟,不需要为这个功能专门改。
