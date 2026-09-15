# /business/workbench 重设计

日期:2026-09-15
范围:`web/packages/admin/src/views/dashboard/biz.vue`(内核内置页,菜单 `component=dashboard/biz`,由 `SysMenu.Id=110` 挂在 `/business/workbench` 下)。
不涉及:`dashboard/workbench.vue`(系统 app 首页,没有"快捷方式"这块,本次不动)。
可视化设计稿(已确认整体方向):<https://claude.ai/artifact/Gjc6wEdj3jvjGF5VJrkvWE>

## 1. 目标

`dashboard/biz.vue` 现状是"欢迎横幅 + 全量菜单铺开的快捷入口 + 通知列表(带查看全部)",信息密度低、无操作价值。本次改造目标:

1. 加入用户可操作的"待办事项"——但内核本身没有审批/工单概念,所以是加一个可插拔的数据入口,内核默认空,消费方接入真实业务数据后才有内容。
2. 快捷方式从"全量铺开"改为"用户可控(手动置顶)+ 高频访问自动补位",可跨设备同步。
3. 通知卡片去掉"查看全部"跳转,卡片本身即完整内容。
4. 欢迎横幅补充"本次在线时长 / 上次登录信息",让用户一进页面就看到和自己相关的真实状态。
5. 视觉上收敛信息密度、复用现有 design tokens,不引入新色系。

约束(继承自现状 `biz.vue` 头部注释,不可违反):**全部真数据拼装,不造假数字**;应用可用同 key(`dashboard/biz`)页面整体覆盖本页。

## 2. 信息架构

```
欢迎横幅(姓名 + 副标题 + 在线时长/上次登录 元信息行)
┌─────────────────────┬───────────────┐
│ 待办事项(1.35fr)      │ 快捷方式(1fr)   │
└─────────────────────┴───────────────┘
我的通知(整行)
```

移动端(≤760px)三块纵向堆叠。

## 3. 各模块设计

### 3.1 欢迎横幅:在线时长 / 上次登录

- **本次在线时长**:不新增后端接口。复用已有 `GET /api/v1/personal/sessions`(`PersonalController.GetSessions`,`backend/src/SmartAdmin.AspNetCore/Controllers/PersonalController.cs:67`)返回的当前会话(`IsCurrent=true`)`LoginTime`,前端本地起计时器渲染"X 小时 Y 分"。
- **上次登录信息**(时间 + IP + 设备):现状没有对应端点。新增:

  ```csharp
  // PersonalController
  [HttpGet("last-login")]
  public async Task<Result<LastLoginOutput?>> GetLastLogin() =>
      Result<LastLoginOutput?>.Ok(await personal.GetLastLoginAsync(CurrentUserId));
  ```

  `IPersonalService.GetLastLoginAsync(long userId)`:查 `sys_login_log` 里该用户 `Success=true` 的最近 2 条(按 `CreateTime desc`),取**第二条**(跳过本次登录那条)的 `CreateTime`/`Ip`/`UserAgent`;不足 2 条(首次登录)返回 `null`,前端显示"首次登录"或直接不渲染这一段。`LastLoginOutput { DateTime Time; string? Ip; string? UserAgent }`。
  索引 `idx_sys_login_log_create` 已按 `CreateTime desc` 建好,加 `UserId` 过滤走的是现有索引前缀查询,不需要新索引。

### 3.2 待办事项

新增一个薄扩展点,不新建实体、不建表——内核不产生待办数据,只定义形状:

```csharp
// SmartAdmin.Core/Workbench/IWorkbenchTodoProvider.cs
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

```csharp
// SmartAdmin.Services/Workbench/NullWorkbenchTodoProvider.cs —— 内核默认实现,恒空
public class NullWorkbenchTodoProvider : IWorkbenchTodoProvider
{
    public Task<WorkbenchTodoSummary> GetMineAsync(long userId) =>
        Task.FromResult(new WorkbenchTodoSummary());
}
```

`ServicesSetup.cs` 里 `TryAddScoped<IWorkbenchTodoProvider, NullWorkbenchTodoProvider>()`。消费方接入真实审批/工单系统时,走 `skills/replace-service.md` 机制一(DI 整体替换,`AddSmartAdmin()` 之前注册自己的实现),不用子类化——这是全新概念而非"改一步"。

端点:`PersonalController` 新增

```csharp
[HttpGet("workbench/todo")]
public async Task<Result<WorkbenchTodoSummary>> GetWorkbenchTodo() =>
    Result<WorkbenchTodoSummary>.Ok(await todoProvider.GetMineAsync(CurrentUserId));
```

前端:卡片标题旁数量角标(`TotalCount`,0 或未来数据未接入时不显示角标);列表最多渲染 5 条(`Title` + `Description` 灰字 + 相对时间);点击整行按 `Url` 跳转,`Url` 为空则不可点;`TotalCount=0` 时渲染空状态"暂无待办"。内核默认状态下(未替换 `NullWorkbenchTodoProvider`)这张卡永远是空状态,这是预期行为,不是 bug。

### 3.3 快捷方式

新增实体 + 表:

```csharp
// SmartAdmin.Services/Entities/SysUserShortcut.cs
[SugarTable("sys_user_shortcut", TableDescription = "用户工作台快捷方式")]
[SugarIndex("idx_sys_user_shortcut_user_path", nameof(UserId), OrderByType.Asc, nameof(MenuPath), OrderByType.Asc, IsUnique = true)]
public class SysUserShortcut : BaseEntity
{
    public long UserId { get; set; }

    [SugarColumn(Length = 256, ColumnDescription = "菜单路由 path")]
    public string MenuPath { get; set; } = "";

    [SugarColumn(ColumnDescription = "是否手动置顶")]
    public bool Pinned { get; set; }

    [SugarColumn(ColumnDescription = "访问计数")]
    public int VisitCount { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "最近访问时间")]
    public DateTime? LastVisitAt { get; set; }
}
```

`MenuPath` 存路由 path(全局唯一,不需要额外挂 `ModuleId`)。`IUserShortcutService`:

```csharp
public interface IUserShortcutService
{
    /// 置顶的在前(按置顶时间倒序)+ 未置顶里访问计数最高的补齐到 cap(8) 条
    Task<IReadOnlyList<UserShortcutItem>> ListMineAsync(long userId, int cap = 8);
    Task PinAsync(long userId, string menuPath);
    Task UnpinAsync(long userId, string menuPath);
    /// upsert:命中已有行 VisitCount+1、LastVisitAt=now;没有则新建 Pinned=false, VisitCount=1
    Task RecordVisitAsync(long userId, string menuPath);
}
```

`UserShortcutItem { string MenuPath; bool Pinned; string Title; string? Icon; }` —— `Title`/`Icon` 现查 `SysMenu` 表按 `Path` 关联回填(菜单标题/图标改了,快捷方式跟着变,不用同步维护冗余字段)。前端渲染前再用当前应用 `auth.menuTree` 的可见叶子交叉过滤一遍(同现状 `leaves()` 逻辑)——菜单被禁用/删除或用户无权限时,快捷方式自动跟着消失,不需要后端联动清理。

端点(`PersonalController`):

```
GET    /api/v1/personal/shortcuts              → IReadOnlyList<UserShortcutItem>
PUT    /api/v1/personal/shortcuts/pin          body: { menuPath }
DELETE /api/v1/personal/shortcuts/pin          body: { menuPath }
POST   /api/v1/personal/shortcuts/visit        body: { menuPath }   （fire-and-forget,前端不等待/不提示错误）
```

前端:
- `router.afterEach` 命中当前应用菜单叶子路径时调用 `visit`(不阻塞导航,失败静默)。
- 卡片分两组渲染:"已置顶"(`Pinned=true`,可点掉星移除)+ "常去"(`Pinned=false` 里 `VisitCount` 最高的几条,虚线框,点星即 `pin`)。
- 标题旁"+"按钮打开一个菜单选择器(复用 `auth.menuTree`,列出未置顶的可见叶子供勾选),选中即调 `pin`。

### 3.4 我的通知

不改后端。去掉 `header-extra` 里的"查看全部"按钮(`web/packages/admin/src/views/dashboard/biz.vue:78-82`),`n-list` 内嵌展示保留,单条仍可点击跳转 `/personal/notice`。

## 4. 视觉规范

沿用 `web/packages/admin/src/styles/tokens.css` 既有 token,不新增变量、不引入新色系:
- 卡片:`--color-bg-container` + `--radius-lg` + `--shadow-1`,与现状一致。
- 待办卡片类型标签(审批/签署/复核)复用既有语义色:`--color-warning`/`--color-info`/`--color-danger` 的 `-bg` 浅底 + 本色文字,不新造标签色板。
- 快捷方式"已置顶"实心星 `--color-warning`、"常去"虚线框区分自动推荐,点击后过渡到实心态。
- 数字类内容(在线时长、待办角标、IP)用 `--font-family-mono` + `tabular-nums`,与 `workbench.vue` 现有 `.tabular` 手法一致。
- 明暗双主题按 `tokens.css` 既有 `[data-theme="dark"]` 覆盖层自动适配,不需要新写暗色分支。

设计稿里待办卡片的类型标签配色、快捷方式虚线框的区分强弱、两栏 1.35:1 的宽度比例已在可视化稿中呈现,视为已确认的视觉基线;实现阶段做像素级微调不需要重新走设计确认。

## 5. 错误处理

延续现状 `biz.vue` 的"一张卡挂了不糊用户一脸红"原则:待办、快捷方式、通知三张卡各自独立 `try/catch`,单个接口失败只影响对应卡片展示空态,不影响其余卡片和横幅。`visit` 上报接口失败静默忽略(不是用户能感知的操作)。

## 6. 测试

新增的都是全新能力,不是重构,直接补测试,不需要先补回归测试锁行为:

- 后端:
  - `IWorkbenchTodoProvider` 默认实现返回空——纳入 `ReplaceabilityContract.cs` 扩展点清单 + `ReplaceabilityTests`(验证消费者前置注册可整体替换,参照现有 DI 替换类扩展点的写法)。
  - `SysUserShortcut` 的 Service 单测:置顶/取消置顶幂等、`RecordVisitAsync` 的 upsert 行为、`ListMineAsync` 的排序与 cap 截断。
  - `PersonalController` 新端点的集成测试(`AdminAppFactory`):`last-login` 首次登录返回 null、有历史时返回上一条;`workbench/todo` 默认空;`shortcuts` 的增删查。
- 前端:`dashboard/biz.vue` 的 `*.spec.ts`(Vitest)覆盖三张卡片的空态/有数据两种渲染分支,以及"查看全部"按钮确实已移除。

## 7. 范围之外

- `dashboard/workbench.vue`(系统 app 首页)不改动。
- 不实现任何真实审批/工单业务逻辑——`IWorkbenchTodoProvider` 只是内核侧的空默认实现。
- 不做快捷方式拖拽排序;置顶顺序固定按"置顶时间倒序"。
- 不做跨应用(多业务 app)的快捷方式聚合视图。
