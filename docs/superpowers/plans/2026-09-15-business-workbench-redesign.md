# 业务工作台(/business/workbench)重设计 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `dashboard/biz.vue`(`/business/workbench`)从"全量菜单铺开的快捷入口 + 无操作价值"改造成"在线状态 + 待办事项 + 可控快捷方式 + 精简通知"的可操作工作台。

**Architecture:** 后端按 Core(契约)→Services(实现)→AspNetCore(端点)分层加三样东西——一个薄扩展点 `IWorkbenchTodoProvider`(内核默认空)、一张新表 `sys_user_shortcut` 及其 `IUserShortcutService`、`IPersonalService` 补一个 `GetLastLoginAsync`——全部挂在已有的 `PersonalController`(`[ActiveSession]`,个人视角端点)下。前端重写 `dashboard/biz.vue`,新增两个同目录纯函数模块(`onlineDuration.ts`/`shortcutGroups.ts`,配 `.spec.ts`,因为这个包没有挂载 `.vue` 组件的测试设施)+ 一个 `ShortcutPicker.vue` 选择弹层,复用已有 `useMenuFlat()`/`uaSummary()`/`fmtDateTime()`。

**Tech Stack:** ASP.NET Core 10 + SqlSugarCore(后端);Vue 3 `<script setup>` + Naive UI + Pinia + vue-i18n(前端);xUnit v3 + `SmartAdmin.Testing`(后端测试);Vitest + happy-dom(前端测试)。

**Spec:** `docs/superpowers/specs/2026-09-15-business-workbench-redesign-design.md`

## Global Constraints

- 内核运行时依赖只能是 SqlSugarCore + Microsoft.*,不引入新三方包。
- 内置服务一律 `TryAddScoped`/`TryAddSingleton`/`TryAddEnumerable` 注册,消费者前置注册必须能顶替。
- 新登记的每个内核接口必须同步加进 `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs` 的 `Points` 列表——`ReplaceabilityTests.cs` 有一条反向自清测试(`EveryRegisteredKernelInterface_ShouldBeDeclaredAsExtensionPoint`),漏登记会直接把已有测试跑红。
- 错误码用 `ErrorCode` 数字枚举,不写本地化文案到后端。
- 全部真数据拼装,`dashboard/biz.vue` 不造假数字;`IWorkbenchTodoProvider` 内核默认实现必须返回真·空(`TotalCount=0`),不能编造示例数据。
- 前端包内路径用 `#/`,不用 `@/`;新类型手写在 `types/api.ts`,不建 schema 别名(该包现状如此)。
- `schema.d.ts` 只能靠 `npm run gen:api`(对着跑起来的后端)生成,不能手改。
- 该包 `views/` 下没有挂载 `.vue` 组件的测试设施(`@vue/test-utils` 不是依赖),新逻辑一律抽成同目录纯函数 `.ts` + `.spec.ts` 测试,这是仓库唯一在用的模式。
- Git commit 信息中文、conventional-commit 格式,不提消费方项目名字——**但本轮任务用户已明确要求全部完成后才由用户自己决定何时提交,执行阶段每个任务末尾的 `git add && git commit` 步骤全部跳过,只在本地工作区保留改动**。

---

## 文件结构总览

**后端新增:**
- `backend/src/SmartAdmin.Core/Workbench/IWorkbenchTodoProvider.cs` —— 待办扩展点契约 + DTO
- `backend/src/SmartAdmin.Services/Workbench/NullWorkbenchTodoProvider.cs` —— 内核默认空实现
- `backend/src/SmartAdmin.Core/Workbench/IUserShortcutService.cs` —— 快捷方式服务契约 + DTO
- `backend/src/SmartAdmin.Services/Entities/SysUserShortcut.cs` —— 快捷方式实体/表
- `backend/src/SmartAdmin.Services/Workbench/UserShortcutService.cs` —— 快捷方式服务实现
- `backend/tests/SmartAdmin.Tests/WorkbenchTodoTests.cs`
- `backend/tests/SmartAdmin.Tests/PersonalLastLoginTests.cs`
- `backend/tests/SmartAdmin.Tests/UserShortcutTests.cs`

**后端修改:**
- `backend/src/SmartAdmin.Services/Personal/IPersonalService.cs` —— 加 `GetLastLoginAsync`
- `backend/src/SmartAdmin.Services/Personal/PersonalService.cs` —— 实现它 + `LastLoginOutput` 记录
- `backend/src/SmartAdmin.Services/ServicesSetup.cs` —— 登记两个新服务
- `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs` —— `Points` 加两条
- `backend/src/SmartAdmin.AspNetCore/Controllers/PersonalController.cs` —— 加 5 个端点(last-login、workbench/todo、shortcuts 的 GET/PUT/PUT/POST)

**前端新增:**
- `web/packages/admin/src/views/dashboard/onlineDuration.ts` + `.spec.ts`
- `web/packages/admin/src/views/dashboard/shortcutGroups.ts` + `.spec.ts`
- `web/packages/admin/src/views/dashboard/ShortcutPicker.vue`

**前端修改:**
- `web/packages/admin/src/api/index.ts` —— `personalApi` 加 6 个方法
- `web/packages/admin/src/types/api.ts` —— 加 `LastLoginInfo`/`WorkbenchTodoItem`/`WorkbenchTodoSummary`/`UserShortcutItem`
- `web/packages/admin/src/router/index.ts` —— 加一个 `afterEach` 记访问
- `web/packages/admin/src/views/dashboard/biz.vue` —— 整页重写
- `web/packages/admin/src/locales/zh-CN.ts` / `en-US.ts` —— `biz` 命名空间改key

**偏离 spec 之处(计划阶段发现的必要修正,原样记录不再回头改 spec)：**
1. Spec 里 pin/unpin 写的是 `PUT`/`DELETE /personal/shortcuts/pin`,`DELETE` 带 body 在部分 HTTP 客户端下行为不一致;改成 `PUT /personal/shortcuts/pin` + `PUT /personal/shortcuts/unpin`,都走 body,语义仍是"设置置顶状态"。
2. Spec 的 `SysUserShortcut` 没有 `PinnedAt` 列,只灵魂拷问了"按置顶时间倒序"——但 `UpdateTime` 是任何字段更新(包括访问计数递增)都会碰的审计列,拿它排序会被后续访问计数更新污染顺序;加一个独立的可空 `PinnedAt` 列。

---

## 后端任务

### Task B1: `IWorkbenchTodoProvider` 扩展点 + 内核默认空实现

**Files:**
- Create: `backend/src/SmartAdmin.Core/Workbench/IWorkbenchTodoProvider.cs`
- Create: `backend/src/SmartAdmin.Services/Workbench/NullWorkbenchTodoProvider.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`
- Modify: `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs`
- Test: `backend/tests/SmartAdmin.Tests/WorkbenchTodoTests.cs`

**Interfaces:**
- Produces: `IWorkbenchTodoProvider.GetMineAsync(long userId) : Task<WorkbenchTodoSummary>`;`WorkbenchTodoSummary { int TotalCount; IReadOnlyList<WorkbenchTodoItem> Items }`;`WorkbenchTodoItem { long Id; string Title; string? Description; string? Url; DateTime CreateTime }`

- [ ] **Step 1: 写接口 + DTO**

```csharp
// backend/src/SmartAdmin.Core/Workbench/IWorkbenchTodoProvider.cs
namespace SmartAdmin.Core;

/// <summary>
/// 工作台待办数据源——内核不产生审批/工单数据,默认恒空;消费方接入真实业务系统后
/// 在 AddSmartAdmin() 之前注册自己的实现即可整体替换(替换性机制一,见 skills/replace-service.md)。
/// </summary>
public interface IWorkbenchTodoProvider
{
    Task<WorkbenchTodoSummary> GetMineAsync(long userId);
}

public record WorkbenchTodoSummary
{
    public int TotalCount { get; init; }
    public IReadOnlyList<WorkbenchTodoItem> Items { get; init; } = [];
}

public record WorkbenchTodoItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Url { get; init; }
    public DateTime CreateTime { get; init; }
}
```

- [ ] **Step 2: 写默认空实现**

```csharp
// backend/src/SmartAdmin.Services/Workbench/NullWorkbenchTodoProvider.cs
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>内核默认实现:恒空。消费方接入真实审批/工单系统后整体替换掉本类。</summary>
public class NullWorkbenchTodoProvider : IWorkbenchTodoProvider
{
    public Task<WorkbenchTodoSummary> GetMineAsync(long userId) =>
        Task.FromResult(new WorkbenchTodoSummary());
}
```

- [ ] **Step 3: 写失败的单测(先验证默认实现的形状)**

```csharp
// backend/tests/SmartAdmin.Tests/WorkbenchTodoTests.cs
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

public class WorkbenchTodoTests
{
    [Fact]
    public async Task NullProvider_returns_empty_summary()
    {
        IWorkbenchTodoProvider provider = new NullWorkbenchTodoProvider();
        var summary = await provider.GetMineAsync(1);
        Assert.Equal(0, summary.TotalCount);
        Assert.Empty(summary.Items);
    }
}
```

- [ ] **Step 4: 跑测试确认失败(类型还没接好前应该编译不过/或通过——此时先确认新文件能编译)**

Run: `dotnet build backend/SmartAdmin.slnx -c Release`
Expected: 编译通过(Step 1/2 已经把类型写全了,这一步主要是确认没有拼写错误)

- [ ] **Step 5: `ServicesSetup.cs` 登记**

在 `backend/src/SmartAdmin.Services/ServicesSetup.cs:223`(`IDashboardService` 登记行)之后插入:

```csharp
        // 工作台待办:内核不产生审批/工单数据,默认空实现,消费者接入真实业务系统后整体替换(机制一)
        services.TryAddScoped<IWorkbenchTodoProvider, NullWorkbenchTodoProvider>();
```

- [ ] **Step 6: `ReplaceabilityContract.cs` 加登记**

在 `Points` 数组里紧邻 `(typeof(IDashboardService), ServiceLifetime.Scoped),` 那一行之后加:

```csharp
        (typeof(IWorkbenchTodoProvider), ServiceLifetime.Scoped),
```

- [ ] **Step 7: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*WorkbenchTodoTests*" --filter-class "*ReplaceabilityTests*"`
Expected: 全部 PASS(`WorkbenchTodoTests` 新测试 + `ReplaceabilityTests` 的 `[Theory]` 自动多出一组针对 `IWorkbenchTodoProvider` 的用例,不用单独写替换性测试)

---

### Task B2: `PersonalController.GetWorkbenchTodo` 端点

**Files:**
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/PersonalController.cs`
- Test: `backend/tests/SmartAdmin.Tests/WorkbenchTodoTests.cs`

**Interfaces:**
- Consumes: `IWorkbenchTodoProvider.GetMineAsync(long)`(Task B1)
- Produces: `GET /api/v1/personal/workbench/todo` → `Result<WorkbenchTodoSummary>`

- [ ] **Step 1: 写集成测试**

```csharp
// WorkbenchTodoTests.cs 追加
[Fact]
public async Task GetWorkbenchTodo_default_is_empty()
{
    using var f = new AdminAppFactory();
    var c = f.CreateClient();
    c.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));

    var body = await (await c.GetAsync("/api/v1/personal/workbench/todo")).ReadEnvelope();
    Assert.Equal(0, body.GetProperty("code").GetInt32());
    var data = body.GetProperty("data");
    Assert.Equal(0, data.GetProperty("totalCount").GetInt32());
    Assert.Equal(0, data.GetProperty("items").GetArrayLength());
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-method "*GetWorkbenchTodo_default_is_empty*"`
Expected: FAIL(404,端点还不存在)

- [ ] **Step 3: 加端点**

`PersonalController` 构造函数参数列表加 `IWorkbenchTodoProvider todoProvider`,方法区(放在 `GetSessions` 之后)加:

```csharp
    /// <summary>看自己的工作台待办摘要。内核默认恒空,消费方接入真实审批/工单系统后有数据。</summary>
    [HttpGet("workbench/todo")]
    public async Task<Result<WorkbenchTodoSummary>> GetWorkbenchTodo() =>
        Result<WorkbenchTodoSummary>.Ok(await todoProvider.GetMineAsync(CurrentUserId));
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*WorkbenchTodoTests*"`
Expected: PASS

---

### Task B3: `IPersonalService.GetLastLoginAsync` + 端点

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Personal/IPersonalService.cs`
- Modify: `backend/src/SmartAdmin.Services/Personal/PersonalService.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/PersonalController.cs`
- Test: `backend/tests/SmartAdmin.Tests/PersonalLastLoginTests.cs`

**Interfaces:**
- Produces: `IPersonalService.GetLastLoginAsync(long userId) : Task<LastLoginOutput?>`;`LastLoginOutput { DateTime Time; string? Ip; string? UserAgent }`;`GET /api/v1/personal/last-login` → `Result<LastLoginOutput?>`

- [ ] **Step 1: 写集成测试(首次登录 = null,登录两次后能看到上一条)**

```csharp
// backend/tests/SmartAdmin.Tests/PersonalLastLoginTests.cs
using System.Net.Http.Headers;
using SmartAdmin.Testing;

namespace SmartAdmin.Tests;

public class PersonalLastLoginTests
{
    [Fact]
    public async Task GetLastLogin_first_login_is_null()
    {
        using var f = new AdminAppFactory();
        var admin = SuperAdminClient(f);
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await admin.LoginToken("superAdmin", "Test@123456"));
        var add = await (await admin.PostJson("/api/v1/sys/user",
            new { account = "olive", password = "InitPass123", name = "olive", enabled = true, roleIds = Array.Empty<long>() })).ReadEnvelope();
        Assert.Equal(0, add.GetProperty("code").GetInt32());

        var olive = f.CreateClient();
        olive.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await olive.LoginToken("olive", "InitPass123"));

        var body = await (await olive.GetAsync("/api/v1/personal/last-login")).ReadEnvelope();
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task GetLastLogin_second_login_shows_the_one_before()
    {
        using var f = new AdminAppFactory();
        var admin = SuperAdminClient(f);
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await admin.LoginToken("superAdmin", "Test@123456"));
        await admin.PostJson("/api/v1/sys/user",
            new { account = "pete", password = "InitPass123", name = "pete", enabled = true, roleIds = Array.Empty<long>() });

        var first = f.CreateClient();
        await first.LoginToken("pete", "InitPass123");   // 第一次登录,只为留一条登录日志

        var second = f.CreateClient();
        second.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await second.LoginToken("pete", "InitPass123"));

        var body = await (await second.GetAsync("/api/v1/personal/last-login")).ReadEnvelope();
        var data = body.GetProperty("data");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, data.ValueKind);
        Assert.True(data.TryGetProperty("time", out _));
    }

    private static HttpClient SuperAdminClient(AdminAppFactory f) => f.CreateClient();
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*PersonalLastLoginTests*"`
Expected: FAIL(404,接口/端点都还没写)

- [ ] **Step 3: 接口 + 实现**

`IPersonalService.cs` 接口方法列表加:

```csharp
    Task<LastLoginOutput?> GetLastLoginAsync(long userId);
```

在接口文件里(同文件,紧跟接口定义之后)加记录类型:

```csharp
public record LastLoginOutput
{
    public DateTime Time { get; init; }
    public string? Ip { get; init; }
    public string? UserAgent { get; init; }
}
```

`PersonalService.cs` 构造函数参数加 `IRepository<SysLoginLog> loginLogs`(必填参数,不走可选逃生舱——这是新功能不是兼容旧签名),方法区加:

```csharp
    /// <summary>
    /// 上一次成功登录的信息(排除本次)。取该用户最近 2 条成功登录记录里的第二条;
    /// 不足 2 条(首次登录)返回 null。索引 idx_sys_login_log_create 已按 CreateTime desc 建好。
    /// </summary>
    public virtual async Task<LastLoginOutput?> GetLastLoginAsync(long userId)
    {
        var recent = await loginLogs.AsQueryable()
            .Where(l => l.UserId == userId && l.Success)
            .OrderByDescending(l => l.CreateTime)
            .Take(2)
            .ToListAsync();
        if (recent.Count < 2) return null;
        var previous = recent[1];
        return new LastLoginOutput { Time = previous.CreateTime, Ip = previous.Ip, UserAgent = previous.UserAgent };
    }
```

- [ ] **Step 4: 端点**

`PersonalController` 方法区加(放在 `GetProfile` 附近):

```csharp
    /// <summary>看自己上一次成功登录的信息(排除本次);首次登录返回 null。</summary>
    [HttpGet("last-login")]
    public async Task<Result<LastLoginOutput?>> GetLastLogin() =>
        Result<LastLoginOutput?>.Ok(await personal.GetLastLoginAsync(CurrentUserId));
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*PersonalLastLoginTests*"`
Expected: PASS

---

### Task B4: `SysUserShortcut` 实体 + `IUserShortcutService` + 端点

**Files:**
- Create: `backend/src/SmartAdmin.Core/Workbench/IUserShortcutService.cs`
- Create: `backend/src/SmartAdmin.Services/Entities/SysUserShortcut.cs`
- Create: `backend/src/SmartAdmin.Services/Workbench/UserShortcutService.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`
- Modify: `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/PersonalController.cs`
- Test: `backend/tests/SmartAdmin.Tests/UserShortcutTests.cs`

**Interfaces:**
- Produces:
  - `IUserShortcutService.ListMineAsync(long userId, int cap = 8) : Task<IReadOnlyList<UserShortcutItem>>`
  - `IUserShortcutService.PinAsync(long userId, string menuPath) : Task`
  - `IUserShortcutService.UnpinAsync(long userId, string menuPath) : Task`
  - `IUserShortcutService.RecordVisitAsync(long userId, string menuPath) : Task`
  - `UserShortcutItem { string MenuPath; bool Pinned; string Title; string? Icon }`
  - `GET /api/v1/personal/shortcuts` / `PUT .../shortcuts/pin` / `PUT .../shortcuts/unpin` / `POST .../shortcuts/visit`(均 body `{ menuPath }`)

- [ ] **Step 1: 写集成测试(置顶排前 + 高频补位 + 幂等)**

```csharp
// backend/tests/SmartAdmin.Tests/UserShortcutTests.cs
using System.Net.Http.Headers;
using SmartAdmin.Testing;

namespace SmartAdmin.Tests;

public class UserShortcutTests
{
    private static async Task<HttpClient> LoggedIn(AdminAppFactory f, string account, string password)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken(account, password));
        return c;
    }

    [Fact]
    public async Task Shortcuts_empty_by_default()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        var body = await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope();
        Assert.Equal(0, body.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Pin_then_list_shows_pinned_item_with_menu_title()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");

        var pin = await (await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" })).ReadEnvelope();
        Assert.Equal(0, pin.GetProperty("code").GetInt32());

        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        var items = list.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("/system/user", items[0].GetProperty("menuPath").GetString());
        Assert.True(items[0].GetProperty("pinned").GetBoolean());
        Assert.Equal("用户管理", items[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Pin_is_idempotent()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });
        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Unpin_removes_pin_but_keeps_visit_history()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });
        var unpin = await (await admin.PutJson("/api/v1/personal/shortcuts/unpin", new { menuPath = "/system/user" })).ReadEnvelope();
        Assert.Equal(0, unpin.GetProperty("code").GetInt32());

        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        Assert.Empty(list.EnumerateArray());   // 未置顶且访问计数 0,不进 cap 列表
    }

    [Fact]
    public async Task Visit_recorded_items_show_up_unpinned_and_pinned_come_first()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");

        for (var i = 0; i < 3; i++)
            await admin.PostJson("/api/v1/personal/shortcuts/visit", new { menuPath = "/system/role" });
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });

        var list = (await (await admin.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        var items = list.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("/system/user", items[0].GetProperty("menuPath").GetString());   // 置顶的在前
        Assert.True(items[0].GetProperty("pinned").GetBoolean());
        Assert.Equal("/system/role", items[1].GetProperty("menuPath").GetString());
        Assert.False(items[1].GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task Shortcuts_are_per_user()
    {
        using var f = new AdminAppFactory();
        var admin = await LoggedIn(f, "superAdmin", "Test@123456");
        await admin.PostJson("/api/v1/sys/user",
            new { account = "quinn", password = "InitPass123", name = "quinn", enabled = true, roleIds = Array.Empty<long>() });
        await admin.PutJson("/api/v1/personal/shortcuts/pin", new { menuPath = "/system/user" });

        var quinn = await LoggedIn(f, "quinn", "InitPass123");
        var list = (await (await quinn.GetAsync("/api/v1/personal/shortcuts")).ReadEnvelope()).GetProperty("data");
        Assert.Empty(list.EnumerateArray());
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserShortcutTests*"`
Expected: FAIL(编译或 404 都算,端点/服务都还没写)

- [ ] **Step 3: 契约 + DTO**

```csharp
// backend/src/SmartAdmin.Core/Workbench/IUserShortcutService.cs
namespace SmartAdmin.Core;

/// <summary>
/// 工作台快捷方式:用户手动置顶 + 高频访问自动补位,跨设备持久化。
/// </summary>
public interface IUserShortcutService
{
    /// <summary>置顶的在前(按置顶时间倒序)+ 未置顶里访问计数最高的补齐到 cap 条;
    /// 对应菜单已被删除的行自动过滤掉,不返回。</summary>
    Task<IReadOnlyList<UserShortcutItem>> ListMineAsync(long userId, int cap = 8);

    /// <summary>置顶(幂等,已置顶则不动);没有历史行时新建一行。</summary>
    Task PinAsync(long userId, string menuPath);

    /// <summary>取消置顶(幂等,未置顶或没有历史行都不报错);访问计数行保留。</summary>
    Task UnpinAsync(long userId, string menuPath);

    /// <summary>记一次访问:没有历史行则新建(VisitCount=1),有则 +1。</summary>
    Task RecordVisitAsync(long userId, string menuPath);
}

public record UserShortcutItem
{
    public string MenuPath { get; init; } = "";
    public bool Pinned { get; init; }
    public string Title { get; init; } = "";
    public string? Icon { get; init; }
}
```

- [ ] **Step 4: 实体**

```csharp
// backend/src/SmartAdmin.Services/Entities/SysUserShortcut.cs
using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 用户工作台快捷方式——(UserId, MenuPath) 唯一,一行既记"是否手动置顶"也记"访问计数"。
/// <para>继承 BaseEntity 而非 DataEntity:这是用户个人偏好,不该被机构数据范围过滤器过滤掉。
/// PinnedAt 单独一列而不是复用 UpdateTime——UpdateTime 会被访问计数递增顺带碰到,拿它排"置顶时间"会被污染。</para>
/// </summary>
[SugarTable("sys_user_shortcut", TableDescription = "用户工作台快捷方式")]
[SugarIndex("idx_sys_user_shortcut", nameof(UserId), OrderByType.Asc, nameof(MenuPath), OrderByType.Asc, IsUnique = true)]
public class SysUserShortcut : BaseEntity
{
    [SugarColumn(ColumnDescription = "用户 Id")]
    public long UserId { get; set; }

    [SugarColumn(Length = 256, ColumnDescription = "菜单路由 path")]
    public string MenuPath { get; set; } = "";

    [SugarColumn(ColumnDescription = "是否手动置顶")]
    public bool Pinned { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "置顶时间(null=未置顶)")]
    public DateTime? PinnedAt { get; set; }

    [SugarColumn(ColumnDescription = "访问计数")]
    public int VisitCount { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "最近访问时间")]
    public DateTime? LastVisitAt { get; set; }
}
```

- [ ] **Step 5: 服务实现**

```csharp
// backend/src/SmartAdmin.Services/Workbench/UserShortcutService.cs
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

public class UserShortcutService(
    IRepository<SysUserShortcut> shortcuts,
    IRepository<SysMenu> menus,
    TimeProvider time) : IUserShortcutService
{
    public virtual async Task<IReadOnlyList<UserShortcutItem>> ListMineAsync(long userId, int cap = 8)
    {
        var rows = await shortcuts.AsQueryable().Where(s => s.UserId == userId).ToListAsync();
        var pinned = rows.Where(r => r.Pinned).OrderByDescending(r => r.PinnedAt).ToList();
        var suggestedCap = Math.Max(0, cap - pinned.Count);
        var suggested = rows.Where(r => !r.Pinned).OrderByDescending(r => r.VisitCount).Take(suggestedCap).ToList();
        var ordered = pinned.Concat(suggested).ToList();
        if (ordered.Count == 0) return [];

        var paths = ordered.Select(r => r.MenuPath).Distinct().ToList();
        var menuByPath = (await menus.AsQueryable().Where(m => paths.Contains(m.Path!)).ToListAsync())
            .ToDictionary(m => m.Path!, m => m);

        return [.. ordered
            .Where(r => menuByPath.ContainsKey(r.MenuPath))   // 菜单已被删除的行自动过滤掉
            .Select(r => new UserShortcutItem
            {
                MenuPath = r.MenuPath,
                Pinned = r.Pinned,
                Title = menuByPath[r.MenuPath].Title,
                Icon = menuByPath[r.MenuPath].Icon,
            })];
    }

    public virtual async Task PinAsync(long userId, string menuPath)
    {
        var row = await shortcuts.GetFirstAsync(s => s.UserId == userId && s.MenuPath == menuPath);
        var now = time.GetLocalNow().DateTime;
        if (row is null)
        {
            await shortcuts.InsertAsync(new SysUserShortcut { UserId = userId, MenuPath = menuPath, Pinned = true, PinnedAt = now });
            return;
        }
        if (row.Pinned) return;
        row.Pinned = true;
        row.PinnedAt = now;
        await shortcuts.UpdateAsync(row);
    }

    public virtual async Task UnpinAsync(long userId, string menuPath)
    {
        var row = await shortcuts.GetFirstAsync(s => s.UserId == userId && s.MenuPath == menuPath);
        if (row is null || !row.Pinned) return;
        row.Pinned = false;
        row.PinnedAt = null;
        await shortcuts.UpdateAsync(row);
    }

    public virtual async Task RecordVisitAsync(long userId, string menuPath)
    {
        var row = await shortcuts.GetFirstAsync(s => s.UserId == userId && s.MenuPath == menuPath);
        var now = time.GetLocalNow().DateTime;
        if (row is null)
        {
            await shortcuts.InsertAsync(new SysUserShortcut { UserId = userId, MenuPath = menuPath, VisitCount = 1, LastVisitAt = now });
            return;
        }
        row.VisitCount++;
        row.LastVisitAt = now;
        await shortcuts.UpdateAsync(row);
    }
}
```

- [ ] **Step 6: `ServicesSetup.cs` 登记**

紧跟 Task B1 加的那行之后:

```csharp
        // 工作台快捷方式:用户手动置顶 + 高频访问自动补位,持久化在 sys_user_shortcut
        services.TryAddScoped<IUserShortcutService, UserShortcutService>();
```

- [ ] **Step 7: `ReplaceabilityContract.cs` 加登记**

紧跟 Task B1 加的那行之后:

```csharp
        (typeof(IUserShortcutService), ServiceLifetime.Scoped),
```

- [ ] **Step 8: 端点**

`PersonalController` 构造函数参数加 `IUserShortcutService shortcuts`,方法区(放在 `GetWorkbenchTodo` 之后)加:

```csharp
    public record ShortcutMenuPathInput(string MenuPath);

    /// <summary>看自己的工作台快捷方式(置顶优先 + 高频自动补位)。</summary>
    [HttpGet("shortcuts")]
    public async Task<Result<IReadOnlyList<UserShortcutItem>>> GetShortcuts() =>
        Result<IReadOnlyList<UserShortcutItem>>.Ok(await shortcuts.ListMineAsync(CurrentUserId));

    /// <summary>置顶一个快捷方式(幂等)。</summary>
    [HttpPut("shortcuts/pin")]
    [OperationLog("置顶工作台快捷方式")]
    public async Task<Result<bool>> PinShortcut(ShortcutMenuPathInput input)
    {
        await shortcuts.PinAsync(CurrentUserId, input.MenuPath);
        return Result<bool>.Ok(true);
    }

    /// <summary>取消置顶(幂等)。</summary>
    [HttpPut("shortcuts/unpin")]
    [OperationLog("取消置顶工作台快捷方式")]
    public async Task<Result<bool>> UnpinShortcut(ShortcutMenuPathInput input)
    {
        await shortcuts.UnpinAsync(CurrentUserId, input.MenuPath);
        return Result<bool>.Ok(true);
    }

    /// <summary>记一次快捷方式访问(高频自动补位用)。前端静默调用,不挂操作日志——每次导航都会打,不是有意义的审计事件。</summary>
    [HttpPost("shortcuts/visit")]
    public async Task<Result<bool>> RecordShortcutVisit(ShortcutMenuPathInput input)
    {
        await shortcuts.RecordVisitAsync(CurrentUserId, input.MenuPath);
        return Result<bool>.Ok(true);
    }
```

- [ ] **Step 9: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserShortcutTests*" --filter-class "*ReplaceabilityTests*"`
Expected: PASS

---

### Task B5: 全量后端回归

**Files:** 无新文件,验证性任务。

- [ ] **Step 1: 全量 build**

Run: `dotnet build backend/SmartAdmin.slnx -c Release`
Expected: 0 error

- [ ] **Step 2: 全量测试(SQLite,本地默认)**

Run: `dotnet test backend/SmartAdmin.slnx`
Expected: 全部 PASS,无新增失败

---

## 前端任务

### Task F1: API 客户端 + 类型 + `gen:api`

**Files:**
- Modify: `web/packages/admin/src/types/api.ts`
- Modify: `web/packages/admin/src/api/index.ts`

**Interfaces:**
- Consumes: 后端 Task B1-B4 的 5 个端点
- Produces: `personalApi.lastLogin()`、`personalApi.workbenchTodo()`、`personalApi.shortcuts()`、`personalApi.pinShortcut(menuPath)`、`personalApi.unpinShortcut(menuPath)`、`personalApi.recordShortcutVisit(menuPath)`

- [ ] **Step 1: 起后端、重生成 `schema.d.ts`**

Run(仓库根目录):
```bash
dotnet run --project backend/samples/MinimalHost &
```
等 `http://localhost:5100/health` 有响应后:
```bash
cd web && npm run gen:api
```
Expected: `packages/admin/src/api/schema.d.ts` 里出现 `/api/v1/personal/last-login`、`/api/v1/personal/workbench/todo`、`/api/v1/personal/shortcuts`、`/api/v1/personal/shortcuts/pin`、`/api/v1/personal/shortcuts/unpin`、`/api/v1/personal/shortcuts/visit` 六条路径

- [ ] **Step 2: 加手写类型**

`types/api.ts` 追加(挨着 `MySessionItem` 定义):

```ts
export interface LastLoginInfo {
  time: string
  ip?: string | null
  userAgent?: string | null
}

export interface WorkbenchTodoItem {
  id: number
  title: string
  description?: string | null
  url?: string | null
  createTime: string
}

export interface WorkbenchTodoSummary {
  totalCount: number
  items: WorkbenchTodoItem[]
}

export interface UserShortcutItem {
  menuPath: string
  pinned: boolean
  title: string
  icon?: string | null
}
```

- [ ] **Step 3: `personalApi` 加方法**

`api/index.ts` 的 `personalApi` 对象里追加(挨着 `sessions`):

```ts
  lastLogin: () => client.GET('/api/v1/personal/last-login', {}).then(r => unwrap<LastLoginInfo | null>(r)),
  workbenchTodo: () =>
    client.GET('/api/v1/personal/workbench/todo', {}).then(r => unwrap<WorkbenchTodoSummary>(r)),
  shortcuts: () => client.GET('/api/v1/personal/shortcuts', {}).then(r => unwrap<UserShortcutItem[]>(r)),
  pinShortcut: (menuPath: string) =>
    client.PUT('/api/v1/personal/shortcuts/pin', { body: { menuPath } }).then(r => unwrap<boolean>(r)),
  unpinShortcut: (menuPath: string) =>
    client.PUT('/api/v1/personal/shortcuts/unpin', { body: { menuPath } }).then(r => unwrap<boolean>(r)),
  recordShortcutVisit: (menuPath: string) =>
    client.POST('/api/v1/personal/shortcuts/visit', { body: { menuPath } }).then(r => unwrap<boolean>(r)),
```

同文件顶部 import 加上 `LastLoginInfo`、`WorkbenchTodoSummary`、`UserShortcutItem`。

- [ ] **Step 4: 类型检查**

Run: `cd web && npm run typecheck`
Expected: 0 error(这一步只验证类型层接好了,`biz.vue` 还没消费这些方法)

---

### Task F2: `onlineDuration.ts` 纯函数

**Files:**
- Create: `web/packages/admin/src/views/dashboard/onlineDuration.ts`
- Test: `web/packages/admin/src/views/dashboard/onlineDuration.spec.ts`

**Interfaces:**
- Produces: `onlineDurationParts(loginTime: string, now?: Date): { hours: number; minutes: number }`

- [ ] **Step 1: 写失败的测试**

```ts
// onlineDuration.spec.ts
import { describe, expect, it } from 'vitest'
import { onlineDurationParts } from './onlineDuration'

describe('onlineDurationParts', () => {
  it('在线不到一小时只有分钟', () => {
    const login = '2026-09-15T08:00:00'
    const now = new Date('2026-09-15T08:25:00')
    expect(onlineDurationParts(login, now)).toEqual({ hours: 0, minutes: 25 })
  })

  it('在线超过一小时拆出小时和分钟', () => {
    const login = '2026-09-15T08:00:00'
    const now = new Date('2026-09-15T10:25:00')
    expect(onlineDurationParts(login, now)).toEqual({ hours: 2, minutes: 25 })
  })

  it('时钟异常(登录时间在未来)不返回负数', () => {
    const login = '2026-09-15T10:00:00'
    const now = new Date('2026-09-15T08:00:00')
    expect(onlineDurationParts(login, now)).toEqual({ hours: 0, minutes: 0 })
  })
})
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd web && npx vitest run src/views/dashboard/onlineDuration.spec.ts`
Expected: FAIL(模块不存在)

- [ ] **Step 3: 实现**

```ts
// onlineDuration.ts
/** 在线时长拆成小时/分钟,供 i18n 模板插值;不做负数(时钟回拨/服务器时间误差时钳到 0)。 */
export function onlineDurationParts(loginTime: string, now: Date = new Date()): { hours: number; minutes: number } {
  const start = new Date(loginTime)
  const ms = Math.max(0, now.getTime() - start.getTime())
  const totalMinutes = Math.floor(ms / 60000)
  return { hours: Math.floor(totalMinutes / 60), minutes: totalMinutes % 60 }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd web && npx vitest run src/views/dashboard/onlineDuration.spec.ts`
Expected: PASS

---

### Task F3: `shortcutGroups.ts` 纯函数

**Files:**
- Create: `web/packages/admin/src/views/dashboard/shortcutGroups.ts`
- Test: `web/packages/admin/src/views/dashboard/shortcutGroups.spec.ts`

**Interfaces:**
- Consumes: `UserShortcutItem`(Task F1)、`MenuLeaf`(`web/packages/admin/src/composables/useMenuFlat.ts` 现有类型)
- Produces: `buildShortcutGroups(shortcuts: UserShortcutItem[], menuLeaves: MenuLeaf[]): { pinned: ShortcutDisplayItem[]; suggested: ShortcutDisplayItem[] }`;`ShortcutDisplayItem { path: string; title: string; icon: string; pinned: boolean }`

- [ ] **Step 1: 写失败的测试**

```ts
// shortcutGroups.spec.ts
import { describe, expect, it } from 'vitest'
import { buildShortcutGroups } from './shortcutGroups'
import type { UserShortcutItem } from '#/types/api'
import type { MenuLeaf } from '#/composables/useMenuFlat'

const leaves: MenuLeaf[] = [
  { title: '用户管理', path: '/system/user', icon: 'ph:users-duotone', breadcrumb: [] },
  { title: '角色管理', path: '/system/role', icon: 'ph:shield-check-duotone', breadcrumb: [] },
]

describe('buildShortcutGroups', () => {
  it('置顶的进 pinned,未置顶的进 suggested,顺序保持后端返回的顺序', () => {
    const shortcuts: UserShortcutItem[] = [
      { menuPath: '/system/user', pinned: true, title: '用户管理', icon: 'ph:users-duotone' },
      { menuPath: '/system/role', pinned: false, title: '角色管理', icon: 'ph:shield-check-duotone' },
    ]
    const { pinned, suggested } = buildShortcutGroups(shortcuts, leaves)
    expect(pinned).toEqual([{ path: '/system/user', title: '用户管理', icon: 'ph:users-duotone', pinned: true }])
    expect(suggested).toEqual([{ path: '/system/role', title: '角色管理', icon: 'ph:shield-check-duotone', pinned: false }])
  })

  it('菜单在当前应用不可见(已被禁用/无权限)时,对应快捷方式被过滤掉', () => {
    const shortcuts: UserShortcutItem[] = [
      { menuPath: '/system/user', pinned: true, title: '用户管理', icon: 'ph:users-duotone' },
      { menuPath: '/system/gone', pinned: true, title: '已删除的页面', icon: null },
    ]
    const { pinned } = buildShortcutGroups(shortcuts, leaves)
    expect(pinned).toHaveLength(1)
    expect(pinned[0].path).toBe('/system/user')
  })

  it('icon 缺省兜底成通用图标', () => {
    const shortcuts: UserShortcutItem[] = [{ menuPath: '/system/user', pinned: false, title: '用户管理', icon: null }]
    const { suggested } = buildShortcutGroups(shortcuts, leaves)
    expect(suggested[0].icon).toBe('ph:squares-four')
  })
})
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd web && npx vitest run src/views/dashboard/shortcutGroups.spec.ts`
Expected: FAIL(模块不存在)

- [ ] **Step 3: 实现**

```ts
// shortcutGroups.ts
import type { UserShortcutItem } from '#/types/api'
import type { MenuLeaf } from '#/composables/useMenuFlat'

export interface ShortcutDisplayItem {
  path: string
  title: string
  icon: string
  pinned: boolean
}

/**
 * 后端 personalApi.shortcuts() 已经按"置顶优先 + 高频补位"排好序、按 cap 截断,
 * 这里只做两件事:按 pinned 拆成两组、用当前应用可见的菜单叶子交叉过滤——
 * 菜单被禁用/删除/无权限后,对应快捷方式跟着自动消失,不用后端联动清理。
 */
export function buildShortcutGroups(
  shortcuts: UserShortcutItem[],
  menuLeaves: MenuLeaf[],
): { pinned: ShortcutDisplayItem[]; suggested: ShortcutDisplayItem[] } {
  const visiblePaths = new Set(menuLeaves.map(l => l.path))
  const toDisplay = (s: UserShortcutItem): ShortcutDisplayItem => ({
    path: s.menuPath,
    title: s.title,
    icon: s.icon || 'ph:squares-four',
    pinned: s.pinned,
  })
  const visible = shortcuts.filter(s => visiblePaths.has(s.menuPath))
  return {
    pinned: visible.filter(s => s.pinned).map(toDisplay),
    suggested: visible.filter(s => !s.pinned).map(toDisplay),
  }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd web && npx vitest run src/views/dashboard/shortcutGroups.spec.ts`
Expected: PASS

---

### Task F4: `ShortcutPicker.vue` 选择弹层

**Files:**
- Create: `web/packages/admin/src/views/dashboard/ShortcutPicker.vue`

**Interfaces:**
- Consumes: `useMenuFlat()`(现有 composable,产出 `MenuLeaf[]`)
- Produces: `defineProps<{ show: boolean; excludePaths: string[] }>()` + `defineEmits<{ pick: [path: string]; 'update:show': [boolean] }>()`

- [ ] **Step 1: 实现(仓库没有组件挂载测试设施,这个任务不写 `.spec.ts`,靠 Task F6 的手动验证覆盖)**

```vue
<script setup lang="ts">
// 复用 MenuSearch.vue 的"搜索+高亮+列表"思路,但不是同一个组件——MenuSearch 是全局命令面板,
// 点了就跳转;这里点了是回调 pick,不导航。
import { computed, ref, watch } from 'vue'
import { NModal, NCard, NInput, NEmpty, NScrollbar } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useMenuFlat } from '#/composables/useMenuFlat'
import AppIcon from '#/components/AppIcon.vue'

const props = defineProps<{ show: boolean; excludePaths: string[] }>()
const emit = defineEmits<{ pick: [path: string]; 'update:show': [boolean] }>()

const { t } = useI18n()
const { leaves } = useMenuFlat()
const keyword = ref('')

watch(
  () => props.show,
  v => {
    if (v) keyword.value = ''
  },
)

const candidates = computed(() => {
  const excluded = new Set(props.excludePaths)
  const kw = keyword.value.trim().toLowerCase()
  return leaves.value.filter(l => !excluded.has(l.path) && (!kw || l.title.toLowerCase().includes(kw)))
})

function pick(path: string) {
  emit('pick', path)
  emit('update:show', false)
}
</script>

<template>
  <n-modal :show="show" @update:show="v => emit('update:show', v)">
    <n-card style="width: 420px" :title="t('biz.quickAdd')" :bordered="false" size="small" role="dialog">
      <n-input v-model:value="keyword" :placeholder="t('common.search')" clearable />
      <n-scrollbar style="max-height: 320px; margin-top: 12px">
        <div v-if="!candidates.length" class="empty"><n-empty /></div>
        <div
          v-for="c in candidates"
          :key="c.path"
          class="row"
          @click="pick(c.path)"
        >
          <AppIcon :icon="c.icon || 'ph:squares-four'" :size="16" />
          <span>{{ c.title }}</span>
        </div>
      </n-scrollbar>
    </n-card>
  </n-modal>
</template>

<style scoped>
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: var(--radius-md);
  cursor: pointer;
}
.row:hover {
  background: var(--color-fill-hover);
}
.empty {
  padding: 24px 0;
}
</style>
```

- [ ] **Step 2: 类型检查**

Run: `cd web && npm run typecheck`
Expected: 0 error

---

### Task F5: 路由 afterEach 记访问

**Files:**
- Modify: `web/packages/admin/src/router/index.ts`

- [ ] **Step 1: 在现有 `afterEach`(约 116-121 行)之后加一个新的**

```ts
// 记一次工作台快捷方式访问(高频自动补位用)。守卫条件与上面记标签页的那个一致,
// 失败静默——这不是用户能感知的操作,不值得为它弹错误提示。
router.afterEach(to => {
  if (to.meta.public) return
  if (['login', 'module', 'not-found', 'personal'].includes(to.name as string)) return
  if (!to.matched.some(r => r.name === 'layout')) return
  personalApi.recordShortcutVisit(to.path).catch(() => {})
})
```

同文件顶部按现有 import 风格加 `import { personalApi } from '#/api'`(如果 `#/api` 尚未被这个文件引入)。

- [ ] **Step 2: 类型检查**

Run: `cd web && npm run typecheck`
Expected: 0 error

---

### Task F6: `dashboard/biz.vue` 整页重写 + i18n

**Files:**
- Modify: `web/packages/admin/src/views/dashboard/biz.vue`
- Modify: `web/packages/admin/src/locales/zh-CN.ts`
- Modify: `web/packages/admin/src/locales/en-US.ts`

**Interfaces:**
- Consumes: Task F1-F5 的全部产出物 + 现有 `uaSummary`(`#/utils/ua`)、`fmtDateTime`(`#/utils/format`)、`useMenuFlat`(`#/composables/useMenuFlat`)

- [ ] **Step 1: locale 改键**

`zh-CN.ts` 的 `biz:` 命名空间(现状 329-336 行)整段替换成:

```ts
  biz: {
    welcome: '{name},欢迎回来',
    subtitle: '应用工作台',
    onlineDuration: '本次在线 {hours} 小时 {minutes} 分',
    onlineDurationShort: '本次在线 {minutes} 分钟',
    lastLogin: '上次登录 {time}',
    firstLogin: '首次登录',
    todo: '待办事项',
    todoEmpty: '暂无待办',
    quick: '快捷方式',
    quickPinned: '已置顶',
    quickSuggested: '常去 · 点星可固定',
    quickAdd: '添加快捷方式',
    quickPin: '固定',
    quickUnpin: '移除',
    notices: '我的通知',
    noticesEmpty: '暂无通知',
    noMenus: '当前应用暂无菜单',
  },
```

`en-US.ts` 对应段落(现状 333-340 行)替换成:

```ts
  biz: {
    welcome: 'Welcome back, {name}',
    subtitle: 'App workbench',
    onlineDuration: 'Online for {hours}h {minutes}m',
    onlineDurationShort: 'Online for {minutes}m',
    lastLogin: 'Last sign-in {time}',
    firstLogin: 'First sign-in',
    todo: 'To-dos',
    todoEmpty: 'Nothing pending',
    quick: 'Shortcuts',
    quickPinned: 'Pinned',
    quickSuggested: 'Frequent · click the star to pin',
    quickAdd: 'Add shortcut',
    quickPin: 'Pin',
    quickUnpin: 'Remove',
    notices: 'My notifications',
    noticesEmpty: 'No notifications',
    noMenus: 'No menus in this app yet',
  },
```

- [ ] **Step 2: 整页重写**

```vue
<script setup lang="ts">
// 业务系统的工作台。每个应用一个首页组件——这里和 dashboard/workbench.vue 是两个独立页面,
// 由各自应用的「工作台」菜单(component 字段)指向,切应用即换首页。
// 全部真数据拼装:欢迎横幅(user store + sessions + last-login)+ 待办事项(工作台待办扩展点,
// 内核默认空)+ 快捷方式(手动置顶 + 高频自动补位)+ 我的通知(notice/mine)。
// 应用要换成自己的业务统计,在自己的 views/ 下放同 key(dashboard/biz)的页面覆盖本页;别造写死的假数字。
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NList, NListItem, NEmpty, NTag } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore } from '#/stores/user'
import { useAuthStore } from '#/stores/auth'
import { useAppStore } from '#/stores/app'
import { btnGrad } from '#/theme/mix'
import { noticeApi, personalApi } from '#/api'
import type { NoticeMineItem, LastLoginInfo, WorkbenchTodoSummary, UserShortcutItem } from '#/types/api'
import { useMenuFlat } from '#/composables/useMenuFlat'
import { onlineDurationParts } from './onlineDuration'
import { buildShortcutGroups } from './shortcutGroups'
import ShortcutPicker from './ShortcutPicker.vue'
import AppIcon from '#/components/AppIcon.vue'
import { fmtDateTime } from '#/utils/format'
import { uaSummary } from '#/utils/ua'

const { t } = useI18n()
const user = useUserStore()
const auth = useAuthStore()
const app = useAppStore()
const route = useRoute()
const router = useRouter()
const { leaves } = useMenuFlat()

const avatarStyle = computed(() => ({ background: btnGrad(app.accent) }))

// ── 在线时长 + 上次登录 ─────────────────────────────────────
const currentLoginTime = ref<string | null>(null)
const lastLogin = ref<LastLoginInfo | null>(null)
const now = ref(new Date())
let tickTimer: ReturnType<typeof setInterval> | undefined

const onlineText = computed(() => {
  if (!currentLoginTime.value) return ''
  const { hours, minutes } = onlineDurationParts(currentLoginTime.value, now.value)
  return hours > 0 ? t('biz.onlineDuration', { hours, minutes }) : t('biz.onlineDurationShort', { minutes })
})
const lastLoginText = computed(() => {
  if (!lastLogin.value) return t('biz.firstLogin')
  const device = uaSummary(lastLogin.value.userAgent)
  const ip = lastLogin.value.ip ? ` · ${lastLogin.value.ip}` : ''
  return `${t('biz.lastLogin', { time: fmtDateTime(lastLogin.value.time, { seconds: false }) })}${ip}${device ? ` · ${device}` : ''}`
})

// ── 待办事项 ─────────────────────────────────────────────
const todo = ref<WorkbenchTodoSummary | null>(null)

// ── 快捷方式 ─────────────────────────────────────────────
const shortcuts = ref<UserShortcutItem[]>([])
const pickerShow = ref(false)
const groups = computed(() => buildShortcutGroups(shortcuts.value, leaves.value))
const pinnedPaths = computed(() => new Set(shortcuts.value.filter(s => s.pinned).map(s => s.menuPath)))

async function loadShortcuts() {
  try {
    shortcuts.value = await personalApi.shortcuts()
  } catch {
    shortcuts.value = []
  }
}
async function pin(path: string) {
  await personalApi.pinShortcut(path)
  await loadShortcuts()
}
async function unpin(path: string) {
  await personalApi.unpinShortcut(path)
  await loadShortcuts()
}

// ── 通知 ────────────────────────────────────────────────
const notices = ref<NoticeMineItem[]>([])

onMounted(async () => {
  tickTimer = setInterval(() => {
    now.value = new Date()
  }, 60_000)

  try {
    const sessions = await personalApi.sessions()
    currentLoginTime.value = sessions.find(s => s.isCurrent)?.loginTime ?? null
  } catch {
    // 在线时长拿不到就不显示这一段,不影响页面其余部分
  }
  try {
    lastLogin.value = await personalApi.lastLogin()
  } catch {
    lastLogin.value = null
  }
  try {
    todo.value = await personalApi.workbenchTodo()
  } catch {
    todo.value = null
  }
  await loadShortcuts()
  try {
    notices.value = (await noticeApi.mine({ page: 1, pageSize: 5 })).items
  } catch {
    // 首页不因一张卡挂了糊用户一脸红:通知取不到就空态,其余照常可用。
  }
})
onUnmounted(() => {
  if (tickTimer) clearInterval(tickTimer)
})
</script>

<template>
  <div class="view">
    <!-- 欢迎横幅:身份 + 在线状态,一行说完 -->
    <div class="banner">
      <div class="avatar" :style="avatarStyle">
        <Icon icon="ph:user" :width="26" color="#fff" />
      </div>
      <div class="banner-body">
        <div class="hi">
          {{ t('biz.welcome', { name: user.userInfo?.name ?? user.userInfo?.account ?? '' }) }}
        </div>
        <div class="tip">{{ t('biz.subtitle') }}</div>
        <div v-if="onlineText || lastLoginText" class="meta-row">
          <span v-if="onlineText">{{ onlineText }}</span>
          <span v-if="onlineText && lastLoginText" class="meta-dot" />
          <span>{{ lastLoginText }}</span>
        </div>
      </div>
    </div>

    <div class="row">
      <!-- 待办事项:内核默认空,消费方接入真实审批/工单系统后有内容 -->
      <n-card :title="t('biz.todo')" :bordered="true">
        <template v-if="todo && todo.totalCount > 0" #header-extra>
          <n-tag type="error" round size="small">{{ todo.totalCount }}</n-tag>
        </template>
        <div v-if="todo && todo.items.length" class="todo-list">
          <div
            v-for="i in todo.items"
            :key="i.id"
            class="todo-row"
            @click="i.url && router.push(i.url)"
          >
            <div class="todo-main">
              <div class="todo-title">{{ i.title }}</div>
              <div v-if="i.description" class="todo-sub">{{ i.description }}</div>
            </div>
            <span class="todo-time">{{ fmtDateTime(i.createTime, { seconds: false }) }}</span>
          </div>
        </div>
        <n-empty v-else :description="t('biz.todoEmpty')" />
      </n-card>

      <!-- 快捷方式:实心=手动置顶(可移除),虚线框=高频访问自动推荐(点固定即置顶) -->
      <n-card :title="t('biz.quick')" :bordered="true">
        <template #header-extra>
          <n-button size="small" quaternary circle @click="pickerShow = true">
            <template #icon><Icon icon="ph:plus" :width="16" /></template>
          </n-button>
        </template>
        <div v-if="groups.pinned.length || groups.suggested.length">
          <div v-if="groups.pinned.length" class="quick-group-label">{{ t('biz.quickPinned') }}</div>
          <div v-if="groups.pinned.length" class="quick-grid">
            <div v-for="s in groups.pinned" :key="s.path" class="quick-item">
              <button class="quick-star active" type="button" :aria-label="t('biz.quickUnpin')" @click.stop="unpin(s.path)">
                <Icon icon="ph:star-fill" :width="12" />
              </button>
              <div class="quick-body" @click="router.push(s.path)">
                <AppIcon :icon="s.icon" :size="17" />
                <span class="quick-name">{{ s.title }}</span>
              </div>
            </div>
          </div>
          <div v-if="groups.suggested.length" class="quick-group-label">{{ t('biz.quickSuggested') }}</div>
          <div v-if="groups.suggested.length" class="quick-grid">
            <div v-for="s in groups.suggested" :key="s.path" class="quick-item suggested">
              <button class="quick-star" type="button" :aria-label="t('biz.quickPin')" @click.stop="pin(s.path)">
                <Icon icon="ph:star" :width="12" />
              </button>
              <div class="quick-body" @click="router.push(s.path)">
                <AppIcon :icon="s.icon" :size="17" />
                <span class="quick-name">{{ s.title }}</span>
              </div>
            </div>
          </div>
        </div>
        <n-empty v-else :description="t('biz.noMenus')" />
      </n-card>
    </div>

    <!-- 通知:没有"查看全部"——卡片本身就是完整内容 -->
    <n-card :title="t('biz.notices')" :bordered="true">
      <n-list v-if="notices.length" :show-divider="false">
        <n-list-item v-for="n in notices" :key="n.id">
          <div class="notice-row" @click="router.push('/personal/notice')">
            <span class="notice-title">
              <span v-if="!n.isRead" class="dot" />
              {{ n.title }}
            </span>
            <span class="notice-time">{{ fmtDateTime(n.publishTime, { seconds: false }) }}</span>
          </div>
        </n-list-item>
      </n-list>
      <n-empty v-else :description="t('biz.noticesEmpty')" />
    </n-card>

    <ShortcutPicker
      v-model:show="pickerShow"
      :exclude-paths="[...pinnedPaths, route.path]"
      @pick="pin"
    />
  </div>
</template>

<style scoped>
.view {
  display: flex;
  flex-direction: column;
  gap: var(--gap-card);
}
.banner {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 24px;
  border-radius: var(--radius-lg);
  background: var(--color-primary-light);
}
.avatar {
  flex-shrink: 0;
  width: 52px;
  height: 52px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
}
.banner-body {
  min-width: 0;
}
.hi {
  font-size: var(--font-size-lg);
  font-weight: 600;
  color: var(--color-text-primary);
}
.tip {
  color: var(--color-text-secondary);
  margin-top: 4px;
}
.meta-row {
  margin-top: 8px;
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13px;
  color: var(--color-text-tertiary);
  font-variant-numeric: tabular-nums;
}
.meta-dot {
  width: 3px;
  height: 3px;
  border-radius: 50%;
  background: var(--color-text-disabled);
  flex-shrink: 0;
}
.row {
  display: grid;
  grid-template-columns: 1.35fr 1fr;
  gap: var(--gap-card);
  align-items: start;
}
@media (max-width: 760px) {
  .row {
    grid-template-columns: 1fr;
  }
}
.todo-list {
  display: flex;
  flex-direction: column;
}
.todo-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 0;
  border-bottom: 1px solid var(--color-border);
  cursor: pointer;
}
.todo-row:last-child {
  border-bottom: none;
}
.todo-title {
  color: var(--color-text-primary);
  font-weight: 500;
}
.todo-sub {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-tertiary);
}
.todo-time {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--color-text-tertiary);
}
.quick-group-label {
  font-size: 12px;
  color: var(--color-text-tertiary);
  margin: 4px 0 8px;
}
.quick-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin-bottom: 4px;
}
.quick-item {
  position: relative;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}
.quick-item.suggested {
  border-style: dashed;
}
.quick-body {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 14px 8px 10px;
  cursor: pointer;
}
.quick-name {
  font-size: 13px;
  color: var(--color-text-secondary);
}
.quick-star {
  position: absolute;
  top: -6px;
  right: -6px;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  border: 1px solid var(--color-border);
  background: var(--color-bg-elevated);
  color: var(--color-text-disabled);
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}
.quick-star.active {
  color: var(--color-warning);
}
.notice-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  cursor: pointer;
}
.notice-title {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--color-text-primary);
}
.dot {
  flex-shrink: 0;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--color-primary);
}
.notice-time {
  flex-shrink: 0;
  color: var(--color-text-secondary);
  font-size: 12px;
}
</style>
```

- [ ] **Step 3: lint + typecheck + 单测全量**

Run: `cd web && npm run lint && npm run typecheck && npm test`
Expected: 全部 PASS,无新增报错

---

### Task F7: 前端整体回归

- [ ] **Step 1**

Run: `cd web && npm run build`
Expected: package(`packages/admin`)+ template 均构建成功

- [ ] **Step 2**

Run: `cd web && npm run format:check`
Expected: 0 diff

---

## 收尾

- [ ] 停掉 Task F1 起的后台 `MinimalHost` 进程。
- [ ] 汇总一份执行报告:每个 Task 的命令 + 真实输出摘要,列出跳过的步骤(commit)和已知遗留项。
- [ ] **不执行 `git add`/`git commit`**——用户已明确要求先看效果再自己决定提交时机。
