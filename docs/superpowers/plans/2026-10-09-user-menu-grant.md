# 用户单独授权（角色授权 + 用户允许 / 拒绝）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在角色授权之外，给单个用户额外「允许」或「拒绝」菜单节点，授权可限时、来源可解释、委派边界按模块划分，并给已上线的下游项目一份完整的变更说明。

**Architecture:** 计算规则只在纯函数 `UserMenuGrantRules` 里实现一次，
`RbacPermissionProvider`（权限码）与 `MenuService`（门户模块 / 菜单树）两处聚合点在角色授予的菜单上叠加用户授权，
缓存过期按最近一条到期时间封顶。
写入经 `IUserMenuGrantService`（变更集保存）与 `IUserMenuGrantPolicy`（委派守卫，唯一判定出口），
新表 `sys_user_menu`，`sys_module` 加可空列 `IsDelegatable`。
前端在用户页加「授权菜单」弹窗与「单独授权一览」抽屉，复用角色授权的分组函数，不改角色页组件。

**Tech Stack:** .NET 10 / SqlSugarCore / xUnit v3（Microsoft.Testing.Platform）/ WebApplicationFactory；Vue 3 + Naive UI + smart-naive-table / Vitest / Playwright。

**Spec:** `docs/superpowers/specs/2026-10-09-user-menu-grant-design.md`（执行者先通读规格，再读本计划）。

---

## 变更说明（面向已上线的下游项目）

SmartAdmin 已在正式项目里使用，下面这份清单最终写进 `CHANGELOG.md` 的「升级说明」（Task 15），这里先列全，执行中有出入就同步改这里。

**本版含破坏性变更（升级前必读）**

- 系统模块的菜单只能授给内置角色（内核种子里固定 Id 1–999 的角色，如「系统管理员」）。
  **从种子版本 6 升级上来的那一次启动，会物理删除「界面上新建的角色 / 消费者种子角色（Id ≥ 1000）」上授的全部系统模块菜单**，
  组织管理、系统运维、任务调度、日志审计、文件管理、AI 网关管理、系统工作台都算。删除不可恢复，每一条写一行 Warning 日志。
  - 影响：靠新建角色进系统模块页面的用户，升级后失去这些入口。普通管理员改用内置「系统管理员」角色。
  - 升级前：备份 `sys_role_menu` 表；在测试库上先升级一次，从启动日志里核对将被删的「角色 × 菜单」清单。
  - 关了种子（`EnableSeed=false`）的部署不走版本闸门，不会自动清理：这些授权暂时仍有效，
    超管在角色页逐个打开这类角色的「授权菜单」保存一次即收回（弹窗不再显示系统模块）。
  - 之后超管在角色页给新建角色授系统菜单会被拒（`41009`）；后台代码在无登录上下文里调 `IRbacService.SetRoleMenusAsync` 不受限。

**数据库**

- 新表 `sys_user_menu`（用户单独授权），CodeFirst 自动建。
- `sys_module` 加可空列 `IsDelegatable`，CodeFirst 自动补。老库升级后所有存量模块都是 `NULL`，按「不可转授」处理。
- `SysSchemaVersion.Current` 由 `6` 升到 `7`：升级后第一次启动会同步一次结构性种子（菜单、模块、配置的结构列），钉了 `CodeFirstVersion` 的项目也会因此重扫一次建表。
- **关着建表闸门的生产库**（`EnableCodeFirstInProduction=false`）：启动时缺列检查会点名 `sys_module(IsDelegatable)`；
  缺 `sys_user_menu` 表由新加的库就绪守卫点名拦下（Task 3）。
  两条路：本次启动临时打开 `EnableCodeFirstInProduction=true` 让应用补列建表（破坏性变更另有闸门拦截，不会顺手执行）；
  或由 DBA 在预发库上用新版本开闸门启动一次，把 SQL 日志里的 DDL 照抄到生产库再启动。

**种子**

- 新按钮「用户-授权菜单」`Id = 239`（挂在「用户管理」页下），随种子进老库，但**不会自动授给任何角色**：要让普通管理员使用，超管在角色页给他的角色勾上。
- 新库的种子：「系统」模块不可转授，「业务中心」可转授。老库里「业务中心」保持 `NULL`（不可转授），要开由超管在模块管理页打开。
- 模块种子改为列白名单：升级只刷编码、图标、落地路由、路由前缀。
  **行为变化**：超管改过的内置模块标题、排序、启用、备注、可转授，升级后不再被刷回。

**接口与权限码**

- 新增四个端点：`GET /api/v1/sys/user/{id}/menus`、`GET /api/v1/sys/user/{id}/menus/effective`、`PUT /api/v1/sys/user/menu`、`GET /api/v1/sys/user/menu-grants/page`，权限码都挂在按钮 239 上。
- `PUT:/api/v1/sys/user/menu` 进内核高敏清单：开了 TOTP 时要再认证。
- `POST /sys/module/add`、`PUT /sys/module/{id}` 的入参与 `GET /sys/module/list|{id}` 的出参多一个 `isDelegatable`。
  更新时**不带这个字段 = 保持原值**，老前端与消费方自建的模块页不会把它清掉。
- 新错误码：`41006` MenuNotGrantable、`41007` TargetIsDelegatedAdmin、`41008` DelegatedGrantExpiryInvalid、`41009` SystemMenuNotAssignable、`42031` UserMenuGrantInvalid。
- 角色出参（`GET /sys/role/page|{id}`）多一个只读字段 `isBuiltin`。
  `41005` UserOutOfDataScope 语义从「授予角色」放宽为泛指「授权」，码与 msgKey 不变。

**后端 API 表面**

- 新接口 `IUserMenuGrantService`、`IUserMenuGrantPolicy`（TryAdd 注册，可前置替换），新事件 `UserMenuGrantsChangedEvent`（内核不订阅）。
- `RbacPermissionProvider`、`MenuService` 主构造器末尾追加可选参数 `TimeProvider? time = null`：
  继承它们的子类**源码不用改**，但已编译的子类程序集要随包升级重新编译（升级 NuGet 本来就会重编）。
- 两个类新增 `protected virtual` 步骤 `ApplyUserGrantsAsync` / `GetNextGrantExpiryAsync`（`MenuService` 另有 `ResolveGrantedMenuIdsAsync`），可单独覆写。
- `IRbacService.InvalidatePermissionsByMenuAsync` 的默认实现除「经角色被授予该菜单的用户」外，再失效所有有单独授权记录的用户。
- 菜单回收站类型改为 `MenuRecycleBinType` 类（彻底删除前清掉指向它的单独授权）；路由段仍是 `menu`。
- `SysRole` 新增只读计算属性 `IsBuiltin`（不建列）；`RbacService` 新增 `protected virtual` 步骤 `EnsureRoleMenusAssignableAsync`，主构造器不变。
- 新库就绪钩子 `SystemMenuRoleGrantCleanup`（升级清理）与 `UserMenuGrantTableGuard`（缺表守卫）。
- 消费者如果**整体替换**了 `IPermissionProvider` 或 `IMenuService`，他们的实现不认新表，单独授权不会生效。

**配置**

- `SmartAdmin:Security:DelegatedGrantMaxDays`（默认 `90`）：普通管理员授出的「允许」最长天数，`0` = 不限（不要求到期时间、也无上限）。

**前端（`smart-admin-web`）**

- 用户页行「更多」新增「授权菜单」，工具栏「更多」新增「单独授权一览」；模块管理页多一个「可转授」开关。
- 角色页「授权菜单」：非内置角色不再显示「系统」模块。
- 类型 `ModuleRow` / `ModuleInput` 多可选字段 `isDelegatable`，`SysRole` 多 `isBuiltin`；新增 `UserMenuEffect` 等类型与 `userApi` 的四个方法；
  `GrantMenuSheet` 多一个可选属性 `hint`。没有删除或改名任何导出。

**回滚**

- 新表与可空列都是加法，老版本代码不读它们。
- 但老版本在 MySQL / SqlServer / PostgreSQL 上启动时，破坏性变更闸门会因为 `sys_module.IsDelegatable`「实体没声明」而拒绝启动：
  回滚前由 DBA 删掉这一列，或回滚那一次配置 `AllowDestructiveSchemaChange=true`（会删掉该列）。

**已知限制**（与角色变更现状一致）：授权改动在接口层立即生效，但目标用户已打开的页面不会实时刷新侧栏与按钮，要刷新页面或重新登录。

---

## Global Constraints

- 运行时依赖只有 SqlSugarCore + Microsoft.\*；前端不加新依赖。引入任何新依赖前先停下来问用户。
- 内置服务一律 `TryAdd*` 注册；新增的接口登记进 `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs` 的 `Points`。
- 默认实现类 `public`、方法 `virtual`，长流程拆成 `protected virtual` 步骤。
- 不改 `IRbacService`、`IPermissionProvider`、`IMenuService` 的成员；`RbacService` 主构造器不动。
- `RbacPermissionProvider`、`MenuService` 主构造器**只追加**可选尾参 `TimeProvider? time = null`（用户已确认，沿用 `UserService` 的成法）。
- 读 `sys_user_menu` 时，这两个类经已有仓储的 `Db` 逃生舱口查，不加仓储参数。
- 跨表条件一律「预取 Id + Contains」，不写子查询；Id 集合为空先短路返回，不发空 `IN`。
- 布尔谓词写成比较式（`m.IsDelegatable == true`），不写裸布尔（SqlServer 谓词上下文不接受）。
- 已有表加列必须 `IsNullable = true`；内核给自己的表加列要 bump `SysSchemaVersion.Current`（本计划 `"6"` → `"7"`，只 bump 一次）。
- 时间口径：本地时间，取 `TimeProvider.GetLocalNow().DateTime`，与审计字段 `CreateTime` 同口径（已核对 `SqlSugarSetup` 的审计 AOP 用的就是它）。
- 委派限时按**日期粒度**判：到期日 ≤ 今天 + 最长天数（规格「不晚于当前时间 + 最长天数」的落地口径，与前端日期选择器一致）。前端提交到期日当天的 `23:59:59`。
- `DelegatedGrantMaxDays` 默认 `90`；`0` = 不限：不要求到期时间，也不设上限。
- 系统模块菜单只能授给内置角色：内置 = `SysRole.Id` 在 1–999（`SmartSeedIds.KernelMax`）；判定菜单属于系统模块 = `MenuTree.RootModuleId(...) == DefaultModuleSeed.BUILTIN_MODULE_ID`。
  守卫只拦有登录上下文的调用（与 `EnsureSuperAdmin` 同一约定），所以测试里经服务直接给新建角色配系统菜单仍然可以。
- 错误码：`MenuNotGrantable = 41006`、`TargetIsDelegatedAdmin = 41007`、`DelegatedGrantExpiryInvalid = 41008`、`SystemMenuNotAssignable = 41009`；
  另加 `UserMenuGrantInvalid = 42031`（规格的保存校验需要一个码，规格未指定，按最近的 `JobTriggerInvalid` 成法定）。
- 权限码就是规范化路由，代码里不写权限字符串常量（守卫判定「对方是管理员」用的 `PUT:/api/v1/sys/user/menu` 是唯一例外，集中成一个常量）。
- 新按钮 `Id = 239`，`ParentId = 230`，标题「用户-授权菜单」，`Sort = 9`。
- 事件命名沿用仓库的 `*Event` 后缀：`UserMenuGrantsChangedEvent`（规格里写作 `UserMenuGrantsChanged`）。
- 注释与文档只写现在做什么、为什么，不写历史、日期、issue 号、设计文档章节号。
- Markdown 文档的正文段落按语义断点拆成多行。
- 前端包内别名 `#/`，不用 `@/`；不手改 `schema.d.ts`，用 `npm run gen:api`；模板里用到的 Naive 组件都要在 `<script setup>` 里 import。
- 测试失败时不改断言、不加 skip、不放宽阈值，先定位原因。
- 提交信息：中文 conventional commit（`type(scope): 主题`），结尾带一行 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 后端测试命令：`dotnet test backend/SmartAdmin.slnx -- --filter-class "*Xxx*"`（MTP，不是 `--filter`）。

## Review Focus

规格没写、但最可能咬到使用者的五类输入，测试已分别加进负责的任务：

1. 关着建表闸门的生产库升级、缺 `sys_user_menu` 表：期望启动当场失败并点名表，而不是起来以后每个非超管请求都 500（Task 3 Step 9）。
2. 模块更新请求不带 `isDelegatable`（老前端、消费方自建页面）：期望原值保留，不被清成「不可转授」（Task 4 Step 1）。
3. 到期日恰好是「今天 + 最长天数」：期望接受；再晚一天被拒（Task 5 Step 1）。
4. 菜单被挪到不可转授模块下之后，普通管理员再改 / 删该节点上自己授过的记录：期望 `41006`，超管仍能改（Task 5 Step 1）。
5. 用户被软删再从回收站恢复：期望单独授权原样生效（Task 7 Step 1）。

---

## 文件结构

后端（`backend/src`）

| 文件 | 职责 |
|---|---|
| `SmartAdmin.Services/Entities/SysUserMenu.cs` | 新实体 + `UserMenuEffect` 枚举 |
| `SmartAdmin.Services/Menu/MenuTree.cs` | 菜单树共用运算：上溯根模块、展开子孙（从 `MenuService` 抽出） |
| `SmartAdmin.Services/UserMenuGrant/UserMenuGrantRules.cs` | 计算规则唯一实现（纯函数） |
| `SmartAdmin.Services/UserMenuGrant/UserMenuGrantQueries.cs` | 读 `sys_user_menu` 的两条共用查询 |
| `SmartAdmin.Services/UserMenuGrant/UserMenuGrantModels.cs` | 入参 / 出参 DTO |
| `SmartAdmin.Services/UserMenuGrant/IUserMenuGrantPolicy.cs` + `UserMenuGrantPolicy.cs` | 委派守卫 |
| `SmartAdmin.Services/UserMenuGrant/IUserMenuGrantService.cs` + `UserMenuGrantService.cs` | 读写服务 |
| `SmartAdmin.Services/UserMenuGrant/UserMenuGrantTableGuard.cs` | 建表被跳过时确认新表存在 |
| `SmartAdmin.Services/Events/UserMenuGrantEvents.cs` | `UserMenuGrantsChangedEvent` |
| `SmartAdmin.Services/Rbac/SystemMenuRoleGrantCleanup.cs` | 升级清理：删除非内置角色上的系统模块菜单 |
| 修改：`Security/RbacPermissionProvider.cs`、`Menu/MenuService.cs`、`Rbac/RbacService.cs`、`Entities/SysModule.cs`、`Seed/DefaultModuleSeed.cs`、`Seed/DefaultMenuSeed.cs`、`Module/ModuleModels.cs`、`Module/ModuleService.cs`、`Mfa/HighSensitivityPermissions.cs`、`RecycleBin/KernelRecycleBinTypes.cs`、`ServicesSetup.cs` | 接入 |
| 修改：`SmartAdmin.Core/ErrorCode.cs`、`SmartAdmin.Core/Options/AdminSecurityOptions.cs`、`SmartAdmin.SqlSugar/Entities/SysSchemaVersion.cs`、`SmartAdmin.AspNetCore/Controllers/UserController.cs` | 错误码、配置、版本、端点 |

后端测试（`backend/tests/SmartAdmin.Tests`）

| 文件 | 内容 |
|---|---|
| `GrantTestKit.cs` | 授权相关集成测试的公共搭建（建角色 / 用户、登录、读门户、写授权） |
| `RoleGrantBaselineTests.cs` | 回归：锁住角色授权的现有口径 |
| `UserMenuGrantRulesTests.cs` | 纯函数单测 |
| `UserMenuGrantEffectTests.cs` | 聚合点：权限码、门户、缓存、菜单变更 |
| `UserMenuGrantPolicyTests.cs` | 写路径与越权守卫 |
| `UserMenuGrantQueryTests.cs` | 读路径：记录、有效权限、一览 |
| `UserMenuGrantLifecycleTests.cs` | 关联清理与软删恢复 |
| `UserMenuGrantDialectTests.cs` | 方言敏感的查询形态（进 SqlServer 子集） |
| `SystemMenuRoleGrantTests.cs` | 系统模块菜单只授内置角色（接口守卫 + `isBuiltin`） |
| 修改：`ModuleCrudTests.cs`、`SeedUpgradeTests.cs`、`CodeFirstNullableUpgradeTests.cs`、`ProductionBootstrapTests.cs`、`ReplaceabilityContract.cs` | |

前端（`web/packages/admin/src`）

| 文件 | 职责 |
|---|---|
| `views/system/user/components/userGrantState.ts` + `.spec.ts` | 草稿三态、变更集、到期校验、漏网接口（纯函数） |
| `views/system/user/components/UserGrantNodeRow.vue` | 一个节点一行：来源、是否有效、三态、到期与备注、漏网提示 |
| `views/system/user/components/UserGrantMenuTable.vue` | 按模块 / 目录分组的节点列表（复用角色页的 `grantMenuGroups.ts`） |
| `views/system/user/components/UserGrantMenuSheet.vue` | 弹窗壳：两个页签、只读说明、底栏 |
| `views/system/user/components/UserGrantEffectiveTable.vue` | 「有效权限」页签里的只读表 |
| `views/system/user/components/UserGrantOverviewDrawer.vue` | 单独授权一览抽屉 |
| 修改：`types/api.ts`、`api/index.ts`、`api/schema.d.ts`（生成）、`views/system/user/index.vue`、`views/system/module/index.vue`、`locales/zh-CN.ts`、`locales/en-US.ts`、`locales/kernelMenuTitles.ts`、`views/listSearch.spec.ts`、`assets/icons/ph-subset.json`（生成） | |
| 修改：`views/system/role/index.vue`、`views/system/role/components/GrantMenuSheet.vue`（可选 `hint`）、`views/system/role/components/grantMenuGroups.ts` + `.spec.ts`（`treeForRole` / `modulesForRole`） | 非内置角色隐藏系统模块；`GrantMenuTable.vue` 不改 |
| `web/e2e/user-menu-grant.spec.ts` | 主流程 e2e |

文档：`docs/permission-design-guide.md`、`site/zh/backend/auth-security.md` + `site/backend/auth-security.md`、`site/zh/guide/deployment/index.md` + `site/guide/deployment/index.md`、`CONTEXT.md`、`CHANGELOG.md`、`.github/workflows/ci.yml`、`scripts/ci-local.ps1`。

---

### Task 0: 开分支

**Files:** 无

- [ ] **Step 1: 从 `dev` 开功能分支**

```bash
git switch dev
git pull --ff-only
git switch -c feat/user-menu-grant
```

规格与本计划在 `docs/superpowers/` 下，未入库，执行时直接读工作区里的文件；用 worktree 执行的话，把 `docs/superpowers/` 整个拷进 worktree（不提交）。

---

### Task 1: 回归测试锁住角色授权的现有口径

改聚合点之前，先把「角色授权 → 权限码 / 门户模块 / 菜单树」的现状钉住。
`ModulePortalTests` 已覆盖停用角色、停用菜单对门户模块的影响；这里补上它没覆盖的：非超管的菜单树（祖先脚手架、按钮不进侧栏）、没有角色的用户、停用角色与停用菜单对菜单树的影响。

**Files:**
- Create: `backend/tests/SmartAdmin.Tests/GrantTestKit.cs`
- Create: `backend/tests/SmartAdmin.Tests/RoleGrantBaselineTests.cs`

**Interfaces:**
- Produces（后续任务的测试都用）：
  - `GrantTestKit.Password`（`"Grant@123456"`）
  - `Task<HttpClient> LoginAsync(AdminAppFactory f, string account, string password = Password)`
  - `Task<HttpClient> SuperAdminAsync(AdminAppFactory f)`
  - `Task<long> CreateRoleAsync(AdminAppFactory f, IReadOnlyCollection<long> menuIds, DataScopeType? scope = null, IReadOnlyCollection<long>? customOrgIds = null)`
  - `Task<(long Id, string Account)> CreateUserAsync(AdminAppFactory f, IReadOnlyCollection<long> roleIds, long? orgId = null)`
  - `Task<string[]> CodesAsync(HttpClient c)`、`Task<long[]> ModuleIdsAsync(HttpClient c)`、`Task<Dictionary<long, long[]>> MenuTreeAsync(HttpClient c, long moduleId)`（键 `0` 是根节点列表）

- [ ] **Step 1: 写公共搭建 `GrantTestKit`**

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 授权相关集成测试的公共搭建:建角色、建用户、登录,读门户三件套(权限码 / 模块 / 菜单树)。
/// 编码与账号用随机 Guid 截断而不是 v7:v7 的前几位是时间戳,同一毫秒附近截出来会撞唯一索引。
/// </summary>
internal static class GrantTestKit
{
    public const string Password = "Grant@123456";

    public static HttpClient WithToken(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<HttpClient> LoginAsync(AdminAppFactory f, string account, string password = Password)
    {
        var c = f.CreateClient();
        return WithToken(c, await c.LoginToken(account, password));
    }

    public static Task<HttpClient> SuperAdminAsync(AdminAppFactory f) => LoginAsync(f, "superAdmin", "Test@123456");

    /// <summary>建一个启用的角色并授菜单;给了 <paramref name="scope"/> 就顺带配数据范围。</summary>
    public static async Task<long> CreateRoleAsync(
        AdminAppFactory f,
        IReadOnlyCollection<long> menuIds,
        DataScopeType? scope = null,
        IReadOnlyCollection<long>? customOrgIds = null)
    {
        using var s = f.Services.CreateScope();
        var sp = s.ServiceProvider;
        var role = new SysRole { Name = "授权测试角色", Code = "grant-" + Guid.NewGuid().ToString("N")[..10], Enabled = true };
        await sp.GetRequiredService<IRepository<SysRole>>().InsertAsync(role);
        var rbac = sp.GetRequiredService<IRbacService>();
        if (menuIds.Count > 0) await rbac.SetRoleMenusAsync(role.Id, menuIds);
        if (scope is { } type) await rbac.SetRoleDataScopeAsync(role.Id, type, customOrgIds);
        return role.Id;
    }

    /// <summary>建一个启用的用户;<paramref name="roleIds"/> 为空即「没有任何角色」。</summary>
    public static async Task<(long Id, string Account)> CreateUserAsync(
        AdminAppFactory f, IReadOnlyCollection<long> roleIds, long? orgId = null)
    {
        using var s = f.Services.CreateScope();
        var account = "grant-" + Guid.NewGuid().ToString("N")[..10];
        var output = await s.ServiceProvider.GetRequiredService<IUserService>().AddAsync(new AddUserInput
        {
            Account = account, Password = Password, Name = "授权测试用户", Enabled = true, OrgId = orgId, RoleIds = [.. roleIds],
        });
        return (output.Id, account);
    }

    /// <summary>当前登录用户的有效权限码(<c>/personal/permissions</c>)。</summary>
    public static async Task<string[]> CodesAsync(HttpClient c) =>
        [.. (await (await c.GetAsync("/api/v1/personal/permissions")).ReadEnvelope())
            .GetProperty("data").EnumerateArray().Select(x => x.GetString()!)];

    /// <summary>当前登录用户可进的模块 Id(<c>/personal/modules</c>)。</summary>
    public static async Task<long[]> ModuleIdsAsync(HttpClient c) =>
        [.. (await (await c.GetAsync("/api/v1/personal/modules")).ReadEnvelope())
            .GetProperty("data").GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetInt64())];

    /// <summary>某模块下的门户菜单树,拍平成「节点 Id → 子节点 Id」;键 0 是根节点列表。</summary>
    public static async Task<Dictionary<long, long[]>> MenuTreeAsync(HttpClient c, long moduleId)
    {
        var data = (await (await c.GetAsync($"/api/v1/personal/menu?moduleId={moduleId}")).ReadEnvelope()).GetProperty("data");
        var map = new Dictionary<long, long[]> { [0] = [.. data.EnumerateArray().Select(n => n.GetProperty("id").GetInt64())] };
        Walk(data);
        return map;

        void Walk(JsonElement nodes)
        {
            foreach (var n in nodes.EnumerateArray())
            {
                var children = n.GetProperty("children");
                map[n.GetProperty("id").GetInt64()] = [.. children.EnumerateArray().Select(x => x.GetProperty("id").GetInt64())];
                Walk(children);
            }
        }
    }
}
```

`AddUserInput.RoleIds` 的实际类型以 `backend/src/SmartAdmin.Services/User/UserModels.cs` 为准，集合表达式 `[.. roleIds]` 对 `List<long>` 与 `IReadOnlyCollection<long>` 都成立。

- [ ] **Step 2: 写回归测试**

```csharp
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 角色授权的现有口径:权限码、门户模块、菜单树三处一致。加入用户单独授权之后,
/// 没有任何单独授权记录的用户必须得到与这里完全相同的结果。
/// </summary>
public class RoleGrantBaselineTests
{
    private const long OrgCatalog = 200, PositionPage = 220, PositionQuery = 221;

    private static readonly string[] PositionQueryCodes = ["GET:/api/v1/sys/position/page", "GET:/api/v1/sys/position/{id}"];

    [Fact]
    public async Task Granted_button_yields_its_codes_and_scaffolds_ancestors_in_tree()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionQuery]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        var c = await GrantTestKit.LoginAsync(f, account);

        Assert.Equal(PositionQueryCodes.Order(StringComparer.Ordinal), (await GrantTestKit.CodesAsync(c)).Order(StringComparer.Ordinal));
        Assert.Equal([1L], await GrantTestKit.ModuleIdsAsync(c));

        var tree = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([OrgCatalog], tree[0]);              // 授的是按钮,目录由脚手架补上
        Assert.Equal([PositionPage], tree[OrgCatalog]);   // 页面由脚手架补上
        Assert.Empty(tree[PositionPage]);                 // 按钮不进侧栏
    }

    [Fact]
    public async Task Disabled_role_yields_no_codes_and_empty_tree()
    {
        using var f = new AdminAppFactory();
        var roleId = await GrantTestKit.CreateRoleAsync(f, [PositionPage, PositionQuery]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [roleId]);

        using (var scope = f.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var role = await roles.GetAsync(roleId);
            await roles.UpdateAsync(roleId, new RoleInput { Name = role.Name, Code = role.Code, Sort = role.Sort, Enabled = false, Remark = role.Remark });
        }

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Empty(await GrantTestKit.CodesAsync(c));
        Assert.Empty((await GrantTestKit.MenuTreeAsync(c, 1))[0]);
    }

    [Fact]
    public async Task User_without_roles_gets_no_codes_modules_or_tree()
    {
        using var f = new AdminAppFactory();
        var (_, account) = await GrantTestKit.CreateUserAsync(f, []);
        var c = await GrantTestKit.LoginAsync(f, account);

        Assert.Empty(await GrantTestKit.CodesAsync(c));
        Assert.Empty(await GrantTestKit.ModuleIdsAsync(c));
        Assert.Empty((await GrantTestKit.MenuTreeAsync(c, 1))[0]);
    }

    [Fact]
    public async Task Disabled_button_drops_its_codes_but_granted_page_stays_in_tree()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionPage, PositionQuery]);
        var (_, account) = await GrantTestKit.CreateUserAsync(f, [role]);

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var menu = (await sp.GetRequiredService<IRepository<SysMenu>>().GetByIdAsync(PositionQuery))!;
            await sp.GetRequiredService<IMenuService>().UpdateAsync(PositionQuery, new MenuInput
            {
                ParentId = menu.ParentId, Type = menu.Type, Title = menu.Title, Permission = menu.Permission,
                Sort = menu.Sort, Enabled = false, ModuleId = menu.ModuleId,
                Path = menu.Path, Component = menu.Component, Icon = menu.Icon, Visible = menu.Visible,
            });
        }

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Empty(await GrantTestKit.CodesAsync(c));
        var tree = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([OrgCatalog], tree[0]);
        Assert.Equal([PositionPage], tree[OrgCatalog]);
    }
}
```

- [ ] **Step 3: 跑测试，确认全绿（锁的是现状，应当一次通过）**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*RoleGrantBaselineTests*"`
Expected: 4 passed。有失败就是对现状理解错了：停下来读 `MenuService.ComputeMyMenuTreeAsync` 修正用例的预期（是预期写错，不是改产品代码）。

- [ ] **Step 4: 提交**

```bash
git add backend/tests/SmartAdmin.Tests/GrantTestKit.cs backend/tests/SmartAdmin.Tests/RoleGrantBaselineTests.cs
git commit -m "test(rbac): 锁住角色授权到权限码、门户模块、菜单树的现有口径" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: 计算规则纯函数、`sys_user_menu` 实体、菜单树工具

**Files:**
- Create: `backend/src/SmartAdmin.Services/Entities/SysUserMenu.cs`
- Create: `backend/src/SmartAdmin.Services/Menu/MenuTree.cs`
- Create: `backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantRules.cs`
- Create: `backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantModels.cs`
- Modify: `backend/src/SmartAdmin.Services/Menu/MenuService.cs`（删私有 `RootModuleId`，两处调用改成 `MenuTree.RootModuleId`）
- Test: `backend/tests/SmartAdmin.Tests/UserMenuGrantRulesTests.cs`

**Interfaces:**
- Produces:
  - `enum UserMenuEffect { Allow = 1, Deny = 2 }`；实体 `SysUserMenu { long UserId; long MenuId; UserMenuEffect Effect; DateTime? ExpireTime; string? Remark }`（继承 `BaseEntity`）
  - `static class MenuTree { const int WalkGuard = 64; long? RootModuleId(long menuId, IReadOnlyDictionary<long, SysMenu> byId); HashSet<long> WithDescendants(IEnumerable<long> roots, IEnumerable<SysMenu> menus) }`
  - `sealed record UserMenuGrantResult(IReadOnlySet<long> EffectiveMenuIds, IReadOnlySet<long> DeniedMenuIds, DateTime? NextExpiry)`
  - `static class UserMenuGrantRules`：`bool IsActive(SysUserMenu, DateTime now)`、`UserMenuGrantResult Compute(IEnumerable<long> roleMenuIds, IReadOnlyCollection<SysUserMenu> grants, IReadOnlyCollection<SysMenu> menus, DateTime now)`、`TimeSpan? CapTtl(TimeSpan? configured, DateTime? nextExpiry, DateTime now)`、`IReadOnlyList<UserMenuLeakedCode> LeakedCodes(long deniedMenuId, IReadOnlyCollection<SysMenu> menus, IReadOnlySet<long> effectiveMenuIds)`
  - `record UserMenuLeakedCode { string Code; IReadOnlyList<long> CarrierMenuIds }`

- [ ] **Step 1: 写失败的单测**

```csharp
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>用户授权计算规则(纯函数)。树:1 目录 → 10 页面 → 11/12 按钮;1 → 20 页面 → 21 按钮;13 是停用按钮。</summary>
public class UserMenuGrantRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 10, 0, 0);

    private static SysMenu M(long id, long parent, string permission = "", bool enabled = true, long? moduleId = null) =>
        new() { Id = id, ParentId = parent, Permission = permission, Enabled = enabled, ModuleId = moduleId };

    private static readonly SysMenu[] Menus =
    [
        M(1, 0, moduleId: 2),
        M(10, 1), M(11, 10, "GET:/a;GET:/shared"), M(12, 10, "GET:/b"), M(13, 10, "GET:/c", enabled: false),
        M(20, 1), M(21, 20, "GET:/shared"),
    ];

    private static SysUserMenu G(long menuId, UserMenuEffect effect, DateTime? expire = null) =>
        new() { MenuId = menuId, Effect = effect, ExpireTime = expire };

    private static long[] Effective(long[] roleMenus, params SysUserMenu[] grants) =>
        [.. UserMenuGrantRules.Compute(roleMenus, grants, Menus, Now).EffectiveMenuIds.Order()];

    [Fact] public void No_grants_keeps_role_result() => Assert.Equal([10L, 11], Effective([10, 11]));

    [Fact] public void Allow_unions_with_roles() => Assert.Equal([11L, 12], Effective([11], G(12, UserMenuEffect.Allow)));

    [Fact] public void Deny_beats_role_grant() => Assert.Empty(Effective([11], G(11, UserMenuEffect.Deny)));

    [Fact] public void Deny_beats_allow_on_same_node() => Assert.Empty(Effective([], G(12, UserMenuEffect.Allow), G(12, UserMenuEffect.Deny)));

    [Fact]
    public void Deny_expands_to_descendants()
    {
        var result = UserMenuGrantRules.Compute([1, 10, 11, 12, 20, 21], [G(10, UserMenuEffect.Deny)], Menus, Now);
        Assert.Equal([1L, 20, 21], result.EffectiveMenuIds.Order());
        Assert.Equal([10L, 11, 12, 13], result.DeniedMenuIds.Order());
    }

    [Fact] public void Deny_on_catalog_removes_whole_subtree() => Assert.Empty(Effective([1, 10, 11, 20, 21], G(1, UserMenuEffect.Deny)));

    [Fact]
    public void Expired_grants_are_ignored()
    {
        Assert.Empty(Effective([], G(12, UserMenuEffect.Allow, Now.AddMinutes(-1))));
        Assert.Equal([11L], Effective([11], G(11, UserMenuEffect.Deny, Now)));            // 到期时刻本身即失效
        Assert.Equal([12L], Effective([], G(12, UserMenuEffect.Allow, Now.AddSeconds(1))));
    }

    [Fact] public void Disabled_menu_is_never_effective() => Assert.Empty(Effective([13], G(13, UserMenuEffect.Allow)));

    [Fact]
    public void Next_expiry_is_earliest_future_one()
    {
        SysUserMenu[] grants =
        [
            G(11, UserMenuEffect.Allow, Now.AddHours(2)), G(12, UserMenuEffect.Deny, Now.AddHours(1)),
            G(20, UserMenuEffect.Allow, Now.AddHours(-1)), G(21, UserMenuEffect.Allow),
        ];
        Assert.Equal(Now.AddHours(1), UserMenuGrantRules.Compute([], grants, Menus, Now).NextExpiry);
        Assert.Null(UserMenuGrantRules.Compute([], [G(21, UserMenuEffect.Allow), G(20, UserMenuEffect.Allow, Now)], Menus, Now).NextExpiry);
    }

    [Fact]
    public void Leaked_codes_list_codes_still_carried_by_other_effective_nodes()
    {
        var leaked = UserMenuGrantRules.LeakedCodes(10, Menus, new HashSet<long> { 20, 21 });
        var only = Assert.Single(leaked);
        Assert.Equal("GET:/shared", only.Code);
        Assert.Equal([21L], only.CarrierMenuIds);

        Assert.Empty(UserMenuGrantRules.LeakedCodes(10, Menus, new HashSet<long> { 20 }));   // 21 不有效,就没有漏网
    }

    [Fact]
    public void Ttl_is_capped_by_next_expiry()
    {
        var twenty = TimeSpan.FromMinutes(20);
        Assert.Equal(TimeSpan.FromMinutes(5), UserMenuGrantRules.CapTtl(twenty, Now.AddMinutes(5), Now));
        Assert.Equal(twenty, UserMenuGrantRules.CapTtl(twenty, Now.AddHours(1), Now));
        Assert.Equal(TimeSpan.FromMinutes(5), UserMenuGrantRules.CapTtl(null, Now.AddMinutes(5), Now));   // 配置为永不过期也封顶
        Assert.Equal(twenty, UserMenuGrantRules.CapTtl(twenty, null, Now));
        Assert.Equal(TimeSpan.FromSeconds(1), UserMenuGrantRules.CapTtl(twenty, Now, Now));               // 不给出 0 或负数
    }

    [Fact]
    public void Descendant_walk_stops_on_cycles()
    {
        SysMenu[] cyclic = [M(1, 2), M(2, 1)];
        Assert.Equal([1L, 2], MenuTree.WithDescendants([1], cyclic).Order());
    }

    [Fact]
    public void Root_module_is_taken_from_top_catalog()
    {
        var byId = Menus.ToDictionary(m => m.Id);
        Assert.Equal(2L, MenuTree.RootModuleId(21, byId));
        Assert.Null(MenuTree.RootModuleId(999, byId));
    }
}
```

- [ ] **Step 2: 跑测试确认编译失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantRulesTests*"`
Expected: 编译错误，`SysUserMenu`、`UserMenuEffect`、`UserMenuGrantRules`、`MenuTree` 不存在。

- [ ] **Step 3: 写实体**

`backend/src/SmartAdmin.Services/Entities/SysUserMenu.cs`：

```csharp
using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>用户单独授权的效果。</summary>
public enum UserMenuEffect
{
    /// <summary>允许:在角色授予之外额外给这个节点(只作用于该节点本身)</summary>
    Allow = 1,

    /// <summary>拒绝:收回这个节点及其全部子孙,优先于任何允许</summary>
    Deny = 2,
}

/// <summary>
/// 用户单独授权:对一个用户的一个菜单节点做「允许」或「拒绝」,可带到期时间。
/// <para>有效菜单 =(启用角色授予 ∪ 生效中的允许)− 生效中的拒绝及其子孙,再只留启用节点,
/// 规则的唯一实现在 <see cref="UserMenuGrantRules"/>。</para>
/// <para>一个用户对一个节点只有一行。保存按变更集增改删,移除走物理删除——软删残行会撞唯一索引。
/// 所以 <c>CreateUserId</c> / <c>CreateTime</c> 就是授权人与授权时间,<c>UpdateUserId</c> / <c>UpdateTime</c> 是最后修改人与时间。</para>
/// </summary>
[SugarTable("sys_user_menu", TableDescription = "用户菜单单独授权")]
[SugarIndex("idx_sys_user_menu", nameof(UserId), OrderByType.Asc, nameof(MenuId), OrderByType.Asc, IsUnique = true)]
public class SysUserMenu : BaseEntity
{
    [SugarColumn(ColumnDescription = "目标用户 Id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnDescription = "菜单 Id")]
    public long MenuId { get; set; }

    [SugarColumn(ColumnDescription = "效果(1 允许 / 2 拒绝)")]
    public UserMenuEffect Effect { get; set; }

    /// <summary>到期时间(本地时间,与审计字段同口径);为空即长期,到了这个时刻起失效。</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "到期时间(空=长期)")]
    public DateTime? ExpireTime { get; set; }

    [SugarColumn(Length = 200, IsNullable = true, ColumnDescription = "授权理由")]
    public string? Remark { get; set; }
}
```

- [ ] **Step 4: 写菜单树工具**

`backend/src/SmartAdmin.Services/Menu/MenuTree.cs`：

```csharp
namespace SmartAdmin.Services;

/// <summary>
/// 菜单树的两个共用运算:上溯根目录取所属模块、按 ParentId 展开子孙。
/// 门户、权限聚合与授权守卫都要用,收在这里只写一份。菜单表小,调用方整表载入内存再算。
/// </summary>
public static class MenuTree
{
    /// <summary>上溯 ParentId 链的最大步数(防断链/环)。菜单层级远小于此。</summary>
    public const int WalkGuard = 64;

    /// <summary>上溯 <paramref name="menuId"/> 的 ParentId 链到根目录,返回根目录的 ModuleId(未挂模块或断链为 null)。</summary>
    public static long? RootModuleId(long menuId, IReadOnlyDictionary<long, SysMenu> byId)
    {
        var cur = byId.GetValueOrDefault(menuId);
        var guard = 0;
        while (cur is not null && cur.ParentId != 0 && guard++ < WalkGuard)
        {
            if (!byId.TryGetValue(cur.ParentId, out var parent)) break;
            cur = parent;
        }
        return cur?.ModuleId;
    }

    /// <summary>根节点连同它们在 <paramref name="menus"/> 里的全部子孙。已收录的节点不再展开,所以成环的数据也会停下。</summary>
    public static HashSet<long> WithDescendants(IEnumerable<long> roots, IEnumerable<SysMenu> menus)
    {
        var children = menus.ToLookup(m => m.ParentId, m => m.Id);
        var result = new HashSet<long>();
        var pending = new Stack<long>(roots);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!result.Add(id)) continue;
            foreach (var child in children[id]) pending.Push(child);
        }
        return result;
    }
}
```

`MenuService.cs`：删掉文件末尾的 `private static long? RootModuleId(...)`，`ComputeMyModulesAsync` 与 `ComputeMyMenuTreeAsync` 里的 `RootModuleId(` 改成 `MenuTree.RootModuleId(`。私有常量 `WalkGuard` 仍被 `EnsureParentValidAsync` 与脚手架循环使用，保留。

- [ ] **Step 5: 写规则与模型**

`backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantModels.cs`（后续任务往这个文件追加 DTO）：

```csharp
namespace SmartAdmin.Services;

/// <summary>被拒节点里仍然有效的一条权限码,以及携带它的有效节点。</summary>
public record UserMenuLeakedCode
{
    /// <summary>权限码(规范化路由)</summary>
    public string Code { get; init; } = "";

    /// <summary>仍携带这条码的有效菜单节点 Id</summary>
    public IReadOnlyList<long> CarrierMenuIds { get; init; } = [];
}
```

`backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantRules.cs`：

```csharp
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>某用户的有效菜单计算结果。</summary>
/// <param name="EffectiveMenuIds">有效菜单(只含启用节点)</param>
/// <param name="DeniedMenuIds">被生效中的拒绝收回的节点:拒绝的节点连同其全部子孙</param>
/// <param name="NextExpiry">该用户全部单独授权里晚于当前时间的最早到期时间,没有为 null;缓存过期按它封顶</param>
public sealed record UserMenuGrantResult(IReadOnlySet<long> EffectiveMenuIds, IReadOnlySet<long> DeniedMenuIds, DateTime? NextExpiry);

/// <summary>
/// 用户单独授权的计算规则,唯一实现(纯函数,不碰库):
/// 有效菜单 =(启用角色授予 ∪ 生效中的允许)− 生效中的拒绝及其子孙,再只留启用节点。
/// <para>拒绝优先;拒绝扩展到子孙,所以拒掉页面就连它的按钮一起拒掉,不会出现页面没了、按钮接口还能调。
/// 拒绝作用在节点上而不是权限码上:被拒节点的某条码若还由另一个有效节点携带,这条码照样有效,
/// <see cref="LeakedCodes"/> 把这种情况找出来给界面提示。</para>
/// <para>权限聚合(<c>RbacPermissionProvider</c>)、门户(<c>MenuService</c>)与授权服务都调这里。</para>
/// </summary>
public static class UserMenuGrantRules
{
    /// <summary>生效中 = 没有到期时间,或到期时间晚于当前时间。</summary>
    public static bool IsActive(SysUserMenu grant, DateTime now) => grant.ExpireTime is null || grant.ExpireTime > now;

    /// <summary>
    /// 计算有效菜单。<paramref name="menus"/> 传全部未删除的菜单(含停用的):拒绝按完整的树展开子孙,
    /// 停用节点最后统一剔除。
    /// </summary>
    public static UserMenuGrantResult Compute(
        IEnumerable<long> roleMenuIds,
        IReadOnlyCollection<SysUserMenu> grants,
        IReadOnlyCollection<SysMenu> menus,
        DateTime now)
    {
        var active = grants.Where(g => IsActive(g, now)).ToList();
        var denied = MenuTree.WithDescendants(active.Where(g => g.Effect == UserMenuEffect.Deny).Select(g => g.MenuId), menus);
        var enabled = menus.Where(m => m.Enabled).Select(m => m.Id).ToHashSet();
        var effective = roleMenuIds
            .Concat(active.Where(g => g.Effect == UserMenuEffect.Allow).Select(g => g.MenuId))
            .Where(id => enabled.Contains(id) && !denied.Contains(id))
            .ToHashSet();
        var next = grants.Where(g => g.ExpireTime > now).Min(g => g.ExpireTime);
        return new UserMenuGrantResult(effective, denied, next);
    }

    /// <summary>
    /// 缓存过期时间:配置值与「到下一个到期时刻还剩多久」取小。配置为 null(永不过期)也按到期时刻封顶;
    /// 没有将到期的记录就是配置值。剩余时长至少 1 秒,不给出 0 或负数(缓存实现可能把它当成永不过期)。
    /// </summary>
    public static TimeSpan? CapTtl(TimeSpan? configured, DateTime? nextExpiry, DateTime now)
    {
        if (nextExpiry is not { } at) return configured;
        var remaining = at - now;
        if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);
        return configured is { } c && c < remaining ? c : remaining;
    }

    /// <summary>拒掉 <paramref name="deniedMenuId"/>(连同子孙)之后仍然有效的权限码,以及携带它们的有效节点。</summary>
    public static IReadOnlyList<UserMenuLeakedCode> LeakedCodes(
        long deniedMenuId, IReadOnlyCollection<SysMenu> menus, IReadOnlySet<long> effectiveMenuIds)
    {
        var subtree = MenuTree.WithDescendants([deniedMenuId], menus);
        var denied = menus.Where(m => subtree.Contains(m.Id))
            .SelectMany(m => PermissionCode.Split(m.Permission))
            .ToHashSet(StringComparer.Ordinal);
        if (denied.Count == 0) return [];

        return [.. menus
            .Where(m => effectiveMenuIds.Contains(m.Id) && !subtree.Contains(m.Id))
            .SelectMany(m => PermissionCode.Split(m.Permission).Select(code => (Code: code, MenuId: m.Id)))
            .Where(x => denied.Contains(x.Code))
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new UserMenuLeakedCode { Code = g.Key, CarrierMenuIds = [.. g.Select(x => x.MenuId).Distinct().Order()] })];
    }
}
```

- [ ] **Step 6: 跑单测确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantRulesTests*"`
Expected: 13 passed。

- [ ] **Step 7: 跑门户与菜单回归（`MenuService` 换了 `RootModuleId` 的出处）**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*RoleGrantBaselineTests*" --filter-class "*ModulePortalTests*" --filter-class "*MenuCrudTests*"`
Expected: 全部通过。

- [ ] **Step 8: 提交**

```bash
git add backend/src/SmartAdmin.Services/Entities/SysUserMenu.cs backend/src/SmartAdmin.Services/Menu backend/src/SmartAdmin.Services/UserMenuGrant backend/tests/SmartAdmin.Tests/UserMenuGrantRulesTests.cs
git commit -m "feat(rbac): 用户单独授权的计算规则与 sys_user_menu 实体" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: 两处聚合点接入单独授权，缓存按到期封顶，菜单变更失效，缺表守卫

**Files:**
- Create: `backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantQueries.cs`
- Create: `backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantTableGuard.cs`
- Modify: `backend/src/SmartAdmin.Services/Security/RbacPermissionProvider.cs`
- Modify: `backend/src/SmartAdmin.Services/Menu/MenuService.cs`
- Modify: `backend/src/SmartAdmin.Services/Rbac/RbacService.cs:143-151`（`InvalidatePermissionsByMenuAsync`）
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`（登记库就绪守卫）
- Modify: `backend/tests/SmartAdmin.Tests/GrantTestKit.cs`（追加直插授权、失效缓存、按服务读权限码）
- Test: `backend/tests/SmartAdmin.Tests/UserMenuGrantEffectTests.cs`
- Test: `backend/tests/SmartAdmin.Tests/ProductionBootstrapTests.cs`（追加一条）

**Interfaces:**
- Consumes: `UserMenuGrantRules.Compute`、`UserMenuGrantRules.CapTtl`、`SysUserMenu`、`UserMenuEffect`（Task 2）
- Produces:
  - `static class UserMenuGrantQueries { Task<List<SysUserMenu>> ListByUserAsync(ISqlSugarClient db, long userId); Task<DateTime?> NextExpiryAsync(ISqlSugarClient db, long userId, DateTime now) }`
  - `RbacPermissionProvider`：可选尾参 `TimeProvider? time = null`；`protected DateTime Now`；`protected virtual Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)`；`protected virtual Task<DateTime?> GetNextGrantExpiryAsync(long userId)`
  - `MenuService`：可选尾参 `TimeProvider? time = null`；`protected virtual Task<List<long>> ResolveGrantedMenuIdsAsync(long userId)`，以及与上面同名同签名的 `ApplyUserGrantsAsync` / `GetNextGrantExpiryAsync`
  - `GrantTestKit.InsertGrantAsync(AdminAppFactory f, long userId, long menuId, UserMenuEffect effect, DateTime? expireTime = null)`、`ResetUserCachesAsync(AdminAppFactory f, long userId)`、`Task<IReadOnlyCollection<string>> CodesOfAsync(AdminAppFactory f, long userId)`、`DeleteGrantAsync(AdminAppFactory f, long userId, long menuId)`

- [ ] **Step 1: 给 `GrantTestKit` 追加直插授权的工具**

（服务层保存在 Task 5 才有，这一步的测试绕过服务直接写表，所以要自己失效缓存。）追加到类里，文件头补 `using SmartAdmin.Core;` 已在：

```csharp
    /// <summary>绕过服务直接写一条单独授权,并失效该用户的权限码缓存与门户代际。</summary>
    public static async Task InsertGrantAsync(AdminAppFactory f, long userId, long menuId, UserMenuEffect effect, DateTime? expireTime = null)
    {
        using (var s = f.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>()
                .InsertAsync(new SysUserMenu { UserId = userId, MenuId = menuId, Effect = effect, ExpireTime = expireTime });
        await ResetUserCachesAsync(f, userId);
    }

    /// <summary>绕过服务直接删一条单独授权,并失效缓存。</summary>
    public static async Task DeleteGrantAsync(AdminAppFactory f, long userId, long menuId)
    {
        using (var s = f.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>().Db
                .Deleteable<SysUserMenu>().Where(g => g.UserId == userId && g.MenuId == menuId).ExecuteCommandAsync();
        await ResetUserCachesAsync(f, userId);
    }

    /// <summary>直接改库后补做服务层会做的失效:该用户的权限码缓存 + 门户代际。</summary>
    public static async Task ResetUserCachesAsync(AdminAppFactory f, long userId)
    {
        var cache = f.Services.GetRequiredService<ICacheProvider>();
        await cache.RemoveAsync(CacheKeys.UserPermissions(userId));
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
    }

    /// <summary>不经 HTTP,直接问权限提供者(用于替换了时钟、令牌可能随之过期的用例)。</summary>
    public static async Task<IReadOnlyCollection<string>> CodesOfAsync(AdminAppFactory f, long userId)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IPermissionProvider>().GetPermissionCodesAsync(userId);
    }
```

- [ ] **Step 2: 写失败的集成测试**

`backend/tests/SmartAdmin.Tests/UserMenuGrantEffectTests.cs`：

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 单独授权接入两处聚合点:权限码(RbacPermissionProvider)与门户(MenuService)同口径,
/// 缓存不活过授权到期,菜单挪动后拒绝的展开结果随之变化。
/// </summary>
public class UserMenuGrantEffectTests
{
    private const long OrgCatalog = 200, PositionPage = 220, PositionQuery = 221;
    private const long OpsCatalog = 300, Ping = 301, RoleGrantMenus = 245, MenuQuery = 331;
    private const string PingCode = "GET:/api/v1/ping";

    [Fact]
    public async Task Allow_gives_codes_module_and_tree_to_user_without_roles()
    {
        using var f = new AdminAppFactory();
        var (uid, account) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, PositionQuery, UserMenuEffect.Allow);

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Contains("GET:/api/v1/sys/position/page", await GrantTestKit.CodesAsync(c));
        Assert.Equal([1L], await GrantTestKit.ModuleIdsAsync(c));
        var tree = await GrantTestKit.MenuTreeAsync(c, 1);
        Assert.Equal([OrgCatalog], tree[0]);
        Assert.Equal([PositionPage], tree[OrgCatalog]);
    }

    [Fact]
    public async Task Deny_on_page_revokes_page_and_buttons_and_deleting_it_restores()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionPage, PositionQuery]);
        var (uid, account) = await GrantTestKit.CreateUserAsync(f, [role]);
        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.NotEmpty(await GrantTestKit.CodesAsync(c));   // 预热缓存

        await GrantTestKit.InsertGrantAsync(f, uid, PositionPage, UserMenuEffect.Deny);
        Assert.Empty(await GrantTestKit.CodesAsync(c));                     // 拒绝扩展到按钮
        Assert.Empty(await GrantTestKit.ModuleIdsAsync(c));
        Assert.Empty((await GrantTestKit.MenuTreeAsync(c, 1))[0]);          // 被拒目录不会被脚手架加回来

        await GrantTestKit.DeleteGrantAsync(f, uid, PositionPage);
        Assert.NotEmpty(await GrantTestKit.CodesAsync(c));
        Assert.Equal([PositionPage], (await GrantTestKit.MenuTreeAsync(c, 1))[OrgCatalog]);
    }

    [Fact]
    public async Task Deny_is_per_node_so_shared_code_survives_on_other_node()
    {
        // GET:/api/v1/sys/menu/tree 同时挂在「角色-授权菜单」与「菜单-查询」上
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [RoleGrantMenus, MenuQuery]);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        await GrantTestKit.InsertGrantAsync(f, uid, RoleGrantMenus, UserMenuEffect.Deny);

        var codes = await GrantTestKit.CodesOfAsync(f, uid);
        Assert.Contains("GET:/api/v1/sys/menu/tree", codes);        // 仍由「菜单-查询」携带
        Assert.DoesNotContain("PUT:/api/v1/sys/role/menu", codes);  // 只在被拒节点上的码收回了
    }

    [Fact]
    public async Task Expired_grant_is_ignored()
    {
        using var f = new AdminAppFactory();
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, DateTime.Now.AddMinutes(-1));
        Assert.DoesNotContain(PingCode, await GrantTestKit.CodesOfAsync(f, uid));
    }

    [Fact]
    public async Task Expiry_is_judged_by_injected_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var f = new AdminAppFactory
        {
            Overrides = s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
            },
        };
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, clock.GetLocalNow().DateTime.AddHours(1));
        Assert.Contains(PingCode, await GrantTestKit.CodesOfAsync(f, uid));

        clock.Advance(TimeSpan.FromHours(2));
        await GrantTestKit.ResetUserCachesAsync(f, uid);
        Assert.DoesNotContain(PingCode, await GrantTestKit.CodesOfAsync(f, uid));
    }

    [Fact]
    public async Task Permission_cache_does_not_outlive_grant_expiry()
    {
        // 真实时钟:配置的权限缓存是 20 分钟,不封顶的话到期后 ping 仍放行
        using var f = new AdminAppFactory();
        var (uid, account) = await GrantTestKit.CreateUserAsync(f, []);
        var c = await GrantTestKit.LoginAsync(f, account);

        var expire = DateTime.Now.AddSeconds(5);
        await GrantTestKit.InsertGrantAsync(f, uid, Ping, UserMenuEffect.Allow, expire);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ping")).StatusCode);   // 这一次把缓存写进去

        var wait = expire - DateTime.Now + TimeSpan.FromSeconds(1.5);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/ping")).StatusCode);
    }

    [Fact]
    public async Task Moving_a_page_under_denied_catalog_revokes_it_immediately()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [PositionQuery]);
        var (uid, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        await GrantTestKit.InsertGrantAsync(f, uid, OpsCatalog, UserMenuEffect.Deny);
        Assert.Contains("GET:/api/v1/sys/position/page", await GrantTestKit.CodesOfAsync(f, uid));   // 预热缓存

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var page = (await sp.GetRequiredService<IRepository<SysMenu>>().GetByIdAsync(PositionPage))!;
            await sp.GetRequiredService<IMenuService>().UpdateAsync(PositionPage, new MenuInput
            {
                ParentId = OpsCatalog, Type = page.Type, Title = page.Title, Permission = page.Permission,
                Sort = page.Sort, Enabled = page.Enabled, ModuleId = null,
                Path = page.Path, Component = page.Component, Icon = page.Icon, Visible = page.Visible,
            });
        }

        // 挪进被拒目录后,按钮成了被拒目录的子孙;缓存必须已被菜单更新失效
        Assert.DoesNotContain("GET:/api/v1/sys/position/page", await GrantTestKit.CodesOfAsync(f, uid));
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantEffectTests*"`
Expected: 除 `Expired_grant_is_ignored` 外全部 FAIL（聚合点还不认 `sys_user_menu`；这一条现在恰好通过，接入后它必须仍然通过）。

- [ ] **Step 4: 写共用查询**

`backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantQueries.cs`：

```csharp
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
```

- [ ] **Step 5: 改 `RbacPermissionProvider`**

整个文件替换为：

```csharp
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IPermissionProvider"/> 的 RBAC 实现。
/// <para>热路径优先走缓存:命中直接返回;未命中才聚合查库(用户 → 启用角色 → 菜单,叠加用户单独授权,
/// 再取有效菜单的权限码),结果回填缓存。授权变更由 <see cref="RbacService"/> 与单独授权服务精确失效对应用户的缓存键。</para>
/// <para>已缓存的<b>空集合</b>与"未缓存"可区分(见 <see cref="ICacheProvider.GetAsync{T}"/>),
/// 无权限用户也只查一次库,不会每请求穿透。缓存过期不超过该用户最近一条单独授权的到期时刻。</para>
/// </summary>
public class RbacPermissionProvider(
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysMenu> menus,
    ICacheProvider cache,
    AdminCacheOptions cacheOptions,
    // 可选尾参:DI 正常注入,消费者子类不传也能编译;单独授权是否到期按注入时钟判(与审计字段同一本地时间口径)
    TimeProvider? time = null) : IPermissionProvider
{
    /// <summary>当前本地时间,单独授权是否到期按它判。</summary>
    protected DateTime Now => (time ?? TimeProvider.System).GetLocalNow().DateTime;

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<string>> GetPermissionCodesAsync(long userId, CancellationToken cancellationToken = default)
    {
        var key = CacheKeys.UserPermissions(userId);
        var cached = await cache.GetAsync<string[]>(key, cancellationToken);
        if (cached is not null) return cached;

        var codes = await LoadFromDatabaseAsync(userId);
        var configured = cacheOptions.PermissionMinutes > 0 ? TimeSpan.FromMinutes(cacheOptions.PermissionMinutes) : (TimeSpan?)null;
        // 有将到期的单独授权时,缓存不能活过那一刻,否则到期后最长还按旧权限放行一个 TTL
        var ttl = UserMenuGrantRules.CapTtl(configured, await GetNextGrantExpiryAsync(userId), Now);
        await cache.SetAsync(key, codes, ttl, cancellationToken);
        return codes;
    }

    /// <summary>
    /// 聚合查库:用户的启用角色 → 角色的菜单 → 叠加单独授权得到有效菜单 → 有效菜单里带路由码且启用的节点的 Permission 展开去重
    /// (一个按钮节点可挂多条码,见 <see cref="PermissionCode.Split"/>)。
    /// 没有角色时不再直接返回空:单独授权的「允许」可以让没有角色的用户也有权限。仅在缓存未命中时执行。
    /// </summary>
    protected virtual async Task<string[]> LoadFromDatabaseAsync(long userId)
    {
        var roleIds = await userRoles.AsQueryable()
            .InnerJoin<SysRole>((ur, r) => ur.RoleId == r.Id && r.Enabled)
            .Where((ur, r) => ur.UserId == userId)
            .Select((ur, r) => ur.RoleId).ToListAsync();
        List<long> roleMenuIds = roleIds.Count == 0
            ? []
            : await roleMenus.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.MenuId).ToListAsync();

        var menuIds = (await ApplyUserGrantsAsync(userId, roleMenuIds)).ToList();
        if (menuIds.Count == 0) return [];

        var permissions = await menus.AsQueryable()
            .Where(m => menuIds.Contains(m.Id) && m.Enabled && m.Permission != "")
            .Select(m => m.Permission)
            .ToListAsync();
        return permissions.SelectMany(PermissionCode.Split).Distinct().ToArray();
    }

    /// <summary>
    /// 在角色授予的菜单上叠加该用户的单独授权(规则见 <see cref="UserMenuGrantRules"/>)。
    /// 没有任何单独授权记录时原样返回、不读菜单表——绝大多数用户走的就是这条路。
    /// </summary>
    protected virtual async Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)
    {
        var grants = await UserMenuGrantQueries.ListByUserAsync(menus.Db, userId);
        if (grants.Count == 0) return roleMenuIds;
        var all = await menus.AsQueryable().ToListAsync();
        return UserMenuGrantRules.Compute(roleMenuIds, grants, all, Now).EffectiveMenuIds;
    }

    /// <summary>该用户最早一条尚未到期的单独授权的到期时间(没有为 null),给缓存过期封顶。</summary>
    protected virtual Task<DateTime?> GetNextGrantExpiryAsync(long userId) =>
        UserMenuGrantQueries.NextExpiryAsync(menus.Db, userId, Now);
}
```

- [ ] **Step 6: 改 `MenuService`**

1. 主构造器末尾追加可选尾参（类注释里「授权链复用 `RbacPermissionProvider` 同款三步短路」改成「有效菜单与 `RbacPermissionProvider` 同一套规则：启用角色授予的菜单叠加用户单独授权」）：

```csharp
public class MenuService(
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysMenu> menus,
    IRepository<SysModule> modules,
    IRbacService rbac,
    ICacheProvider cache,
    AdminCacheOptions cacheOptions,
    // 可选尾参:DI 正常注入,消费者子类不传也能编译;单独授权是否到期按注入时钟判
    TimeProvider? time = null) : IMenuService
```

2. 在 `PortalTtl` 属性下面加：

```csharp
    /// <summary>当前本地时间,单独授权是否到期按它判。</summary>
    protected DateTime Now => (time ?? TimeProvider.System).GetLocalNow().DateTime;

    /// <summary>门户缓存 TTL:配置值;该用户有将到期的单独授权时按到期时刻封顶(超管不受单独授权影响,不查)。</summary>
    private async Task<TimeSpan?> PortalTtlForAsync(long userId, bool isSuperAdmin) =>
        isSuperAdmin ? PortalTtl : UserMenuGrantRules.CapTtl(PortalTtl, await GetNextGrantExpiryAsync(userId), Now);
```

3. `GetMyModulesAsync` 与 `GetMyMenuTreeAsync` 里的 `await cache.SetAsync(key, result, PortalTtl);` 分别改成 `await cache.SetAsync(key, result, await PortalTtlForAsync(userId, isSuperAdmin));`。

4. `ComputeMyModulesAsync` 中间三行（`ResolveEnabledRoleIdsAsync` → `roleIds.Count == 0` 短路 → 查 `roleMenus`）替换为：

```csharp
        var grantedMenuIds = await ResolveGrantedMenuIdsAsync(userId);
        if (grantedMenuIds.Count == 0) return [];
```

5. `ComputeMyMenuTreeAsync` 里 `if (!isSuperAdmin)` 块的前两条语句（`roleIds` 与三元 `grantedMenuIds`）替换为：

```csharp
            var grantedMenuIds = await ResolveGrantedMenuIdsAsync(userId);
```

6. 在 `ResolveEnabledRoleIdsAsync` 下面加三个步骤：

```csharp
    /// <summary>
    /// 用户的有效菜单 Id:启用角色授予的菜单,叠加用户单独授权。与 <see cref="RbacPermissionProvider"/> 同一套规则,
    /// 门户模块与菜单树都从它反推,侧栏与接口权限不会各说各话。
    /// </summary>
    protected virtual async Task<List<long>> ResolveGrantedMenuIdsAsync(long userId)
    {
        var roleIds = await ResolveEnabledRoleIdsAsync(userId);
        List<long> roleMenuIds = roleIds.Count == 0
            ? []
            : await roleMenus.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.MenuId).ToListAsync();
        return [.. await ApplyUserGrantsAsync(userId, roleMenuIds)];
    }

    /// <summary>在角色授予的菜单上叠加单独授权;没有记录时原样返回、不读菜单表。</summary>
    protected virtual async Task<IReadOnlyCollection<long>> ApplyUserGrantsAsync(long userId, IReadOnlyCollection<long> roleMenuIds)
    {
        var grants = await UserMenuGrantQueries.ListByUserAsync(menus.Db, userId);
        if (grants.Count == 0) return roleMenuIds;
        var all = await menus.AsQueryable().ToListAsync();
        return UserMenuGrantRules.Compute(roleMenuIds, grants, all, Now).EffectiveMenuIds;
    }

    /// <summary>该用户最早一条尚未到期的单独授权的到期时间(没有为 null),给门户缓存过期封顶。</summary>
    protected virtual Task<DateTime?> GetNextGrantExpiryAsync(long userId) =>
        UserMenuGrantQueries.NextExpiryAsync(menus.Db, userId, Now);
```

- [ ] **Step 7: 改 `RbacService.InvalidatePermissionsByMenuAsync`**

替换整个方法：

```csharp
    /// <inheritdoc />
    public virtual async Task InvalidatePermissionsByMenuAsync(long menuId)
    {
        // 菜单 → 授它的角色(sys_role_menu) → 挂这些角色的用户(sys_user_role) → 失效其权限缓存。
        // 菜单 CRUD 低频,过量失效无害(下次请求按新授权重算);软删菜单不清 sys_role_menu,故删后此扇出仍能命中受影响用户。
        var roleIds = await roleMenus.AsQueryable().Where(x => x.MenuId == menuId).Select(x => x.RoleId).ToListAsync();
        List<long> affected = roleIds.Count == 0
            ? []
            : await userRoles.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.UserId).ToListAsync();

        // 再加上所有有单独授权记录的用户:拒绝会扩展到子孙,挪父节点会改变扩展结果,精确圈定代价高;
        // 单独授权是例外,这个集合小,过量失效无害。
        affected.AddRange(await roleMenus.Db.Queryable<SysUserMenu>().Select(g => g.UserId).Distinct().ToListAsync());

        if (affected.Count == 0) return;
        await InvalidatePermissionsAsync(affected.Distinct());
    }
```

`IRbacService.InvalidatePermissionsByMenuAsync` 的接口注释改成「……失效"经某角色被授予该菜单"的用户与所有有单独授权记录的用户的权限缓存」。

- [ ] **Step 8: 跑聚合点测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantEffectTests*" --filter-class "*RoleGrantBaselineTests*" --filter-class "*ModulePortalTests*" --filter-class "*CacheInvalidationTests*" --filter-class "*MultiCodeMenuTests*"`
Expected: 全部通过（`Permission_cache_does_not_outlive_grant_expiry` 约耗时 7 秒，正常）。

- [ ] **Step 9: 写缺表守卫的失败测试（Review Focus 1）**

追加到 `ProductionBootstrapTests`：

```csharp
    /// <summary>
    /// 关着建表闸门升级、库里缺 sys_user_menu:缺列检查只看已存在的表,缺整张表它不报;
    /// 而这张表在每个非超管请求的鉴权路径上。必须启动时点名拦下,不能起来以后每个非超管请求都 500。
    /// </summary>
    [Fact]
    public void Production_with_missing_user_menu_table_fails_at_startup_naming_the_table()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-nogrant-{Guid.NewGuid():N}.db");
        var noSeed = new Dictionary<string, string?> { ["SmartAdmin:Database:EnableSeed"] = "false" };

        using (var v1 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false, FreshDatabase = true, Settings = noSeed })
        {
            _ = v1.CreateClient();
            using var scope = v1.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISqlSugarClient>().DbMaintenance.DropTable("sys_user_menu");
        }

        using var f = new AdminAppFactory { DbPath = dbPath, EnvironmentName = "Production", FreshDatabase = true, Settings = noSeed };
        var ex = Assert.Throws<InvalidOperationException>(() => f.CreateClient());

        Assert.Contains("sys_user_menu", ex.Message);
        Assert.Contains("EnableCodeFirstInProduction", ex.Message);
    }
```

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ProductionBootstrapTests*"`
Expected: 新用例 FAIL（宿主正常起来，没有抛异常）。

- [ ] **Step 10: 写库就绪守卫并登记**

`backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantTableGuard.cs`：

```csharp
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
```

`ServicesSetup.cs`：在 `services.TryAddScoped<IRbacService, RbacService>();` 下面加：

```csharp
        // 单独授权表在鉴权热路径上:建表被跳过时库就绪即确认它在,缺了点名拦下而不是放进程起来再 500
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDatabaseReadyHook, UserMenuGrantTableGuard>());
```

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ProductionBootstrapTests*" --filter-class "*ReplaceabilityTests*" --filter-class "*StartupContractTests*"`
Expected: 全部通过（`IDatabaseReadyHook` 已在可替换性契约的多实现清单里）。

- [ ] **Step 11: 跑后端全量**

Run: `dotnet test backend/SmartAdmin.slnx`
Expected: 全部通过。

- [ ] **Step 12: 提交**

```bash
git add backend/src backend/tests
git commit -m "feat(rbac): 权限码与门户菜单叠加用户单独授权,缓存不活过授权到期" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 模块「可转授」开关，模块种子改列白名单，版本号 7

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Entities/SysModule.cs`
- Modify: `backend/src/SmartAdmin.Services/Seed/DefaultModuleSeed.cs`
- Modify: `backend/src/SmartAdmin.Services/Module/ModuleModels.cs`
- Modify: `backend/src/SmartAdmin.Services/Module/ModuleService.cs`
- Modify: `backend/src/SmartAdmin.SqlSugar/Entities/SysSchemaVersion.cs:17`
- Modify: `backend/src/SmartAdmin.SqlSugar/Seed/ISeedData.cs`（`SyncOnUpgrade` 注释）
- Test: `backend/tests/SmartAdmin.Tests/ModuleCrudTests.cs`、`SeedUpgradeTests.cs`、`CodeFirstNullableUpgradeTests.cs`

**Interfaces:**
- Produces: `SysModule.IsDelegatable`（`bool?`）；`ModuleInput.IsDelegatable`（`bool?`，更新时 null = 保持原值）；`DefaultModuleSeed.SyncColumns`（`virtual string[]?`）；`SysSchemaVersion.Current == "7"`

- [ ] **Step 1: 写失败的测试**

追加到 `ModuleCrudTests`（类里已有的 helper 不够用就直接用 `GrantTestKit.SuperAdminAsync`）：

```csharp
    /// <summary>内置 system 模块固定不可转授:传 true 也存不进去,读出来恒为 false。</summary>
    [Fact]
    public async Task Builtin_system_module_is_never_delegatable()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        var update = await c.PutJson("/api/v1/sys/module/1", new
        {
            code = "system", title = "系统", icon = "lucide:settings", defaultRoute = "", apiPrefix = "sys",
            sort = 1, enabled = true, remark = "内置系统应用,不可删除", isDelegatable = true,
        });
        Assert.Equal(0, (await update.ReadEnvelope()).GetProperty("code").GetInt32());

        var data = (await (await c.GetAsync("/api/v1/sys/module/1")).ReadEnvelope()).GetProperty("data");
        Assert.False(data.GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>新库的种子:「系统」不可转授,「业务中心」可转授。</summary>
    [Fact]
    public async Task Fresh_seed_marks_business_module_delegatable()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        var list = (await (await c.GetAsync("/api/v1/sys/module/list")).ReadEnvelope()).GetProperty("data").EnumerateArray().ToList();
        Assert.False(list.Single(m => m.GetProperty("id").GetInt64() == 1).GetProperty("isDelegatable").GetBoolean());
        Assert.True(list.Single(m => m.GetProperty("id").GetInt64() == 2).GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>新建时不带开关 = 不可转授(null);之后能打开。</summary>
    [Fact]
    public async Task New_module_without_flag_is_not_delegatable_until_turned_on()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        var id = (await (await c.PostJson("/api/v1/sys/module/add", new { code = "crm", title = "客户", sort = 5, enabled = true })).ReadEnvelope())
            .GetProperty("data").GetInt64();
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            (await (await c.GetAsync($"/api/v1/sys/module/{id}")).ReadEnvelope()).GetProperty("data").GetProperty("isDelegatable").ValueKind);

        await c.PutJson($"/api/v1/sys/module/{id}", new { code = "crm", title = "客户", sort = 5, enabled = true, isDelegatable = true });
        Assert.True((await (await c.GetAsync($"/api/v1/sys/module/{id}")).ReadEnvelope()).GetProperty("data").GetProperty("isDelegatable").GetBoolean());
    }

    /// <summary>更新请求不带 isDelegatable(老前端 / 自建管理页)时保持原值,不把它清成不可转授。</summary>
    [Fact]
    public async Task Update_without_flag_keeps_existing_value()
    {
        using var f = new AdminAppFactory();
        var c = await GrantTestKit.SuperAdminAsync(f);
        await c.PutJson("/api/v1/sys/module/2", new
        {
            code = "business", title = "业务中心(改)", icon = "lucide:briefcase-business", defaultRoute = "", apiPrefix = "biz",
            sort = 2, enabled = true, remark = "示例业务应用(可删除)",
        });
        var data = (await (await c.GetAsync("/api/v1/sys/module/2")).ReadEnvelope()).GetProperty("data");
        Assert.Equal("业务中心(改)", data.GetProperty("title").GetString());
        Assert.True(data.GetProperty("isDelegatable").GetBoolean());
    }
```

追加到 `SeedUpgradeTests`（类顶部加常量 `private const long BusinessModuleId = 2;`）：

```csharp
    /// <summary>
    /// 升级时模块种子只刷结构列(编码、图标、落地路由、路由前缀);标题、排序、启用、备注、可转授是超管的设置,留着。
    /// 整行刷回的话,超管关掉的「业务中心」可转授会在每次升级时被重新打开。
    /// </summary>
    [Fact]
    public async Task Upgrade_syncs_module_structure_but_keeps_admin_settings()
    {
        await RestartWithAsync(
            async db =>
            {
                await DowngradeVersionAsync(db);
                await db.Updateable<SysModule>()
                    .SetColumns(x => new SysModule
                    {
                        Icon = "ph:old", ApiPrefix = "old",
                        Title = "我的业务", Sort = 9, Enabled = false, Remark = "改过", IsDelegatable = false,
                    })
                    .Where(x => x.Id == BusinessModuleId)
                    .ExecuteCommandAsync();
            },
            async db =>
            {
                var m = await db.Queryable<SysModule>().FirstAsync(x => x.Id == BusinessModuleId);
                Assert.Equal("lucide:briefcase-business", m.Icon);   // 结构列刷回
                Assert.Equal("biz", m.ApiPrefix);
                Assert.Equal("我的业务", m.Title);                    // 超管的设置留着
                Assert.Equal(9, m.Sort);
                Assert.False(m.Enabled);
                Assert.Equal("改过", m.Remark);
                Assert.False(m.IsDelegatable);
            });
    }
```

追加到 `CodeFirstNullableUpgradeTests`：

```csharp
    /// <summary>
    /// 老库升级:sys_module 已有数据时补可空列 IsDelegatable;存量模块是 NULL(不可转授),
    /// 种子的列白名单不含它,升级同步也不会把「业务中心」改成可转授。
    /// </summary>
    [Fact]
    public async Task NonEmpty_sys_module_gets_nullable_IsDelegatable_and_stays_null_after_upgrade()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-module-upg-{Guid.NewGuid():N}.db");
        try
        {
            using (var v1 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false, FreshDatabase = true })
            {
                _ = v1.CreateClient();
                using var scope = v1.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
                db.DbMaintenance.DropColumn("sys_module", "IsDelegatable");
                await db.Updateable<SysSchemaVersion>().SetColumns(x => new SysSchemaVersion { Version = "6" }).Where(x => x.Id == 1).ExecuteCommandAsync();
            }

            using var v2 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false };
            _ = v2.CreateClient();
            using var s2 = v2.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<ISqlSugarClient>();

            var cols = db2.DbMaintenance.GetColumnInfosByTableName("sys_module", false).Select(c => c.DbColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("IsDelegatable", cols);
            var business = await db2.Queryable<SysModule>().FirstAsync(x => x.Id == 2);
            Assert.Null(business.IsDelegatable);
        }
        finally
        {
            TestDb.Cleanup(dbPath, dbPath);
        }
    }
```

（`CodeFirstNullableUpgradeTests` 文件头需要 `using SmartAdmin.Services;`、`using SqlSugar;`、`using SmartAdmin.SqlSugar;`，已有。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ModuleCrudTests*" --filter-class "*SeedUpgradeTests*" --filter-class "*CodeFirstNullableUpgradeTests*"`
Expected: 新用例编译失败（`SysModule.IsDelegatable` 不存在）。

- [ ] **Step 3: 实体加列**

`SysModule.cs` 在 `Remark` 之后加：

```csharp
    /// <summary>
    /// 可转授:非超管(普通管理员)能否把本模块里的菜单单独授给或拒给用户。可空,存量库补列时无损:
    /// 数据库 NULL(存量模块)与显式 <c>false</c> 同判定为不可转授,只有显式 <c>true</c> 才放行——
    /// 默认收紧,补列不会静默放宽。内置 system 模块固定不可转授(<c>ModuleService</c> 读写都按 false)。
    /// 演进列必须可空:MSSQL 无法对有数据的表 ADD 无 DEFAULT 的 NOT NULL 列(同 <see cref="SysRole.IsDelegatable"/>)。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "是否可转授(普通管理员可单独授权本模块菜单)")]
    public bool? IsDelegatable { get; set; }
```

类注释里「模块不是独立权限轴」一段后补一句：`<para>模块同时是委派授权的边界:见 <see cref="IsDelegatable"/>。</para>`

- [ ] **Step 4: 种子改值与列白名单**

`DefaultModuleSeed.cs`：

```csharp
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
```

类注释末尾补：`「业务中心」种子为可转授(新库);老库升级时它保持 NULL(不可转授),由超管在模块管理页决定。`

`backend/src/SmartAdmin.SqlSugar/Seed/ISeedData.cs` 的 `SyncOnUpgrade` 注释同步一句：
「配置中心(`sys_config`)与菜单树都靠 `SyncColumns`……」改成「配置中心(`sys_config`)、菜单树与模块表都靠 `SyncColumns`……」，
并在「菜单只刷……」之后补「模块只刷编码 / 图标 / 落地路由 / 路由前缀,标题、排序、启用、备注、可转授留给超管」。

- [ ] **Step 5: 入参与服务**

`ModuleModels.cs` 的 `ModuleInput` 末尾加：

```csharp
    /// <summary>可转授(普通管理员能否单独授权本模块菜单)。更新时为 null = 保持原值;内置 system 模块传什么都按 false。</summary>
    public bool? IsDelegatable { get; init; }
```

`ModuleService.cs`：

```csharp
    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<SysModule>> ListAsync()
    {
        var list = await modules.AsQueryable().OrderBy(m => m.Sort).OrderBy(m => m.Id).ToListAsync();
        foreach (var m in list) NormalizeDelegatable(m);
        return list;
    }

    /// <inheritdoc />
    public virtual async Task<SysModule> GetAsync(long id)
    {
        var module = await modules.GetByIdAsync(id);
        AdminException.ThrowIf(module is null, ErrorCode.ModuleNotFound);
        NormalizeDelegatable(module!);
        return module!;
    }
```

`AddAsync` 的对象初始化器加 `IsDelegatable = input.IsDelegatable,`；`UpdateAsync` 在 `entity.Remark = input.Remark;` 之后加：

```csharp
        // 内置 system 模块承载全部管理页,固定不可转授;其余模块入参不带这个字段(老前端 / 自建管理页)时保持原值,不被清成不可转授
        if (id == DefaultModuleSeed.BUILTIN_MODULE_ID) entity.IsDelegatable = false;
        else if (input.IsDelegatable is not null) entity.IsDelegatable = input.IsDelegatable;
```

类末尾加：

```csharp
    /// <summary>内置 system 模块固定不可转授:库里存了什么都按 false 读。</summary>
    private static void NormalizeDelegatable(SysModule m)
    {
        if (m.Id == DefaultModuleSeed.BUILTIN_MODULE_ID) m.IsDelegatable = false;
    }
```

类注释补一句：`内置 system 模块固定不可转授(读写都按 false)。`

- [ ] **Step 6: bump 版本号**

`SysSchemaVersion.cs`：`public const string Current = "7";`

- [ ] **Step 7: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ModuleCrudTests*" --filter-class "*SeedUpgradeTests*" --filter-class "*CodeFirstNullableUpgradeTests*" --filter-class "*CodeFirstVersionTests*" --filter-class "*ModuleProtectionTests*" --filter-class "*ModulePortalTests*"`
Expected: 全部通过。

- [ ] **Step 8: 提交**

```bash
git add backend/src backend/tests
git commit -m "feat(module): 模块新增「可转授」开关,模块种子升级只刷结构列" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: 写路径——错误码、配置、守卫、变更集保存、端点、按钮 239

**Files:**
- Modify: `backend/src/SmartAdmin.Core/ErrorCode.cs`（41005 注释、新增 41006–41008、42031）
- Modify: `backend/src/SmartAdmin.Core/Options/AdminSecurityOptions.cs`（`DelegatedGrantMaxDays`）
- Modify: `web/packages/admin/src/locales/zh-CN.ts`、`web/packages/admin/src/locales/en-US.ts`（错误文案 + 按钮标题译名）
- Modify: `web/packages/admin/src/locales/kernelMenuTitles.ts`
- Modify: `backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantModels.cs`
- Create: `backend/src/SmartAdmin.Services/Events/UserMenuGrantEvents.cs`
- Create: `backend/src/SmartAdmin.Services/UserMenuGrant/IUserMenuGrantPolicy.cs`、`UserMenuGrantPolicy.cs`
- Create: `backend/src/SmartAdmin.Services/UserMenuGrant/IUserMenuGrantService.cs`、`UserMenuGrantService.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/UserController.cs`
- Modify: `backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs`（按钮 239）
- Modify: `backend/src/SmartAdmin.Services/Mfa/HighSensitivityPermissions.cs`
- Modify: `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs`
- Modify: `backend/tests/SmartAdmin.Tests/GrantTestKit.cs`
- Test: `backend/tests/SmartAdmin.Tests/UserMenuGrantPolicyTests.cs`

**Interfaces:**
- Consumes: `UserMenuGrantRules`、`MenuTree`、`SysUserMenu`（Task 2）；`SysModule.IsDelegatable`（Task 4）
- Produces:
  - `ErrorCode.MenuNotGrantable (41006)`、`TargetIsDelegatedAdmin (41007)`、`DelegatedGrantExpiryInvalid (41008)`、`UserMenuGrantInvalid (42031)`
  - `AdminSecurityOptions.DelegatedGrantMaxDays`（`int`，默认 90）
  - `record UserMenuGrantUpsert { long MenuId; UserMenuEffect Effect; DateTime? ExpireTime; string? Remark }`
  - `record SetUserMenuGrantsInput { long UserId; IReadOnlyList<UserMenuGrantUpsert> Upserts; IReadOnlyList<long> Removes }`
  - `record UserMenuGrantsChangedEvent(long UserId, long? OperatorId, IReadOnlyList<UserMenuGrantUpsert> Added, IReadOnlyList<UserMenuGrantUpsert> Updated, IReadOnlyList<long> RemovedMenuIds)`
  - `interface IUserMenuGrantPolicy { int? DelegatedMaxDays { get; } Task<ErrorCode?> GetTargetBlockAsync(long targetUserId); Task<IReadOnlySet<long>?> GetGrantableModuleIdsAsync(); Task EnsureGrantableAsync(long targetUserId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes); }`
  - `UserMenuGrantPolicy.GrantPermissionCode == "PUT:/api/v1/sys/user/menu"`
  - `interface IUserMenuGrantService { Task ApplyChangesAsync(long userId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes); }`（Task 6 再加三个读方法）
  - 端点 `PUT /api/v1/sys/user/menu`（请求体 `SetUserMenuGrantsInput`）
  - `GrantTestKit`：`Local(DateTime)`、`EndOfDay(int days)`、`Allow(...)`、`Deny(...)`、`PutGrantsAsync(HttpClient, long userId, object[] upserts, long[]? removes = null)`（返回业务码）、`GrantRowsAsync(f, userId)`、`SuperAdminIdAsync(f)`、`DelegatedAdminAsync(f, scope, customOrgIds, orgId)`（返回 `(HttpClient Client, long Id, long RoleId)`）、`CreateCatalogWithPageAsync(f, moduleId)`（返回 `(long CatalogId, long PageId)`）

- [ ] **Step 1: 给 `GrantTestKit` 追加写路径工具，写失败的测试**

`GrantTestKit.cs` 文件头补 `using System.Globalization;`，类里追加：

```csharp
    /// <summary>本地时间,格式与前端提交的一致(不带时区后缀),免得序列化带上 offset。</summary>
    public static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>今天 + <paramref name="days"/> 那一天的 23:59:59,即前端日期选择器提交的形态。</summary>
    public static string EndOfDay(int days) => Local(DateTime.Today.AddDays(days + 1).AddSeconds(-1));

    public static object Allow(long menuId, string? expire = null, string? remark = null) => new { menuId, effect = 1, expireTime = expire, remark };

    public static object Deny(long menuId, string? expire = null, string? remark = null) => new { menuId, effect = 2, expireTime = expire, remark };

    /// <summary>按变更集保存,返回信封里的业务码(0 = 成功)。</summary>
    public static async Task<int> PutGrantsAsync(HttpClient c, long userId, object[] upserts, long[]? removes = null) =>
        (await (await c.PutJson("/api/v1/sys/user/menu", new { userId, upserts, removes = removes ?? Array.Empty<long>() })).ReadEnvelope())
            .GetProperty("code").GetInt32();

    public static async Task<List<SysUserMenu>> GrantRowsAsync(AdminAppFactory f, long userId)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IRepository<SysUserMenu>>().AsQueryable().Where(g => g.UserId == userId).ToListAsync();
    }

    public static async Task<long> SuperAdminIdAsync(AdminAppFactory f)
    {
        using var s = f.Services.CreateScope();
        return (await s.ServiceProvider.GetRequiredService<IRepository<SysUser>>().GetFirstAsync(u => u.IsSuperAdmin))!.Id;
    }

    /// <summary>普通管理员:角色只授「用户-授权菜单」(239),数据范围默认全部。</summary>
    public static async Task<(HttpClient Client, long Id, long RoleId)> DelegatedAdminAsync(
        AdminAppFactory f, DataScopeType scope = DataScopeType.All, long[]? customOrgIds = null, long? orgId = null)
    {
        var role = await CreateRoleAsync(f, [239], scope, customOrgIds);
        var (id, account) = await CreateUserAsync(f, [role], orgId);
        return (await LoginAsync(f, account), id, role);
    }

    /// <summary>在指定模块下建一个顶级目录 + 其下一个页面。</summary>
    public static async Task<(long CatalogId, long PageId)> CreateCatalogWithPageAsync(AdminAppFactory f, long moduleId)
    {
        using var s = f.Services.CreateScope();
        var menus = s.ServiceProvider.GetRequiredService<IMenuService>();
        var catalog = await menus.CreateAsync(new MenuInput
        {
            ParentId = 0, Type = MenuType.Catalog, Title = "授权测试目录", Permission = "", Sort = 50, Enabled = true, ModuleId = moduleId, Visible = true,
        });
        var page = await menus.CreateAsync(new MenuInput
        {
            ParentId = catalog, Type = MenuType.Menu, Title = "授权测试页面", Permission = "", Sort = 1, Enabled = true,
            Path = "/grant-test/" + Guid.NewGuid().ToString("N")[..8], Component = "dashboard/biz", Visible = true,
        });
        return (catalog, page);
    }
```

`backend/tests/SmartAdmin.Tests/UserMenuGrantPolicyTests.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 单独授权的写路径与委派守卫:对自己 / 对超管 / 范围外 / 对方是管理员 / 委派限时 / 不可转授模块,
/// 变更集语义(没提到的记录不动、先后提交互不覆盖)、保存校验与事件。
/// </summary>
public class UserMenuGrantPolicyTests
{
    private const long BizWorkbench = 110;    // 业务中心(新库种子为可转授)的工作台
    private const long BusinessModule = 2;
    private const long OrgQuery = 211, PositionQuery = 221, Ping = 301;   // 都在系统模块(不可转授)
    private const string PingCode = "GET:/api/v1/ping";

    private static object[] None => [];

    [Fact]
    public async Task Super_admin_grants_system_menu_without_expiry_and_cache_follows()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.DoesNotContain(PingCode, await GrantTestKit.CodesOfAsync(f, target));   // 预热缓存

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));

        var row = Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));
        Assert.Null(row.ExpireTime);
        Assert.Equal(await GrantTestKit.SuperAdminIdAsync(f), row.CreateUserId);
        Assert.Contains(PingCode, await GrantTestKit.CodesOfAsync(f, target));          // 保存即失效缓存
    }

    [Fact]
    public async Task Nobody_can_grant_to_themselves()
    {
        using var f = new AdminAppFactory();
        var (admin, adminId, _) = await GrantTestKit.DelegatedAdminAsync(f);
        Assert.Equal(42029, await GrantTestKit.PutGrantsAsync(admin, adminId, [GrantTestKit.Deny(BizWorkbench)]));

        var super = await GrantTestKit.SuperAdminAsync(f);
        Assert.Equal(42029, await GrantTestKit.PutGrantsAsync(super, await GrantTestKit.SuperAdminIdAsync(f), [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Super_admin_target_is_protected()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        Assert.Equal(42007, await GrantTestKit.PutGrantsAsync(admin, await GrantTestKit.SuperAdminIdAsync(f), [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Delegated_admin_cannot_grant_user_outside_data_scope()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);   // 只管技术部
        var (target, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 6);                              // 产品部
        Assert.Equal(41005, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Only_super_admin_can_grant_to_another_admin()
    {
        using var f = new AdminAppFactory();
        var (admin, _, adminRole) = await GrantTestKit.DelegatedAdminAsync(f);
        var (otherAdmin, _) = await GrantTestKit.CreateUserAsync(f, [adminRole]);

        Assert.Equal(41007, await GrantTestKit.PutGrantsAsync(admin, otherAdmin, [GrantTestKit.Deny(BizWorkbench)]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(await GrantTestKit.SuperAdminAsync(f), otherAdmin, [GrantTestKit.Deny(BizWorkbench)]));
    }

    [Fact]
    public async Task Target_promoted_to_admin_locks_out_original_grantor()
    {
        using var f = new AdminAppFactory();
        var (admin, _, adminRole) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(10))]));

        using (var scope = f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRbacService>().SetUserRolesAsync(target, [adminRole]);

        Assert.Equal(41007, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(10), "续期")]));
    }

    [Fact]
    public async Task Delegated_allow_needs_expiry_no_later_than_max_days()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(41008, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench)]));
        Assert.Equal(41008, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(91))]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(90))]));   // 上限当天可以
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(BizWorkbench)]));                                // 拒绝只会收紧,不要求限时
    }

    [Fact]
    public async Task Zero_max_days_lifts_the_expiry_rule()
    {
        using var f = new AdminAppFactory
        {
            Settings = new Dictionary<string, string?> { ["SmartAdmin:Security:DelegatedGrantMaxDays"] = "0" },
        };
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench)]));
    }

    [Fact]
    public async Task Delegated_admin_cannot_add_modify_or_remove_system_module_records()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping), GrantTestKit.Deny(PositionQuery)]));

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(OrgQuery)]));                                // 新增
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(Ping, GrantTestKit.EndOfDay(5), "改备注")]));   // 修改
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, None, [PositionQuery]));                                        // 移除
        Assert.Equal(2, (await GrantTestKit.GrantRowsAsync(f, target)).Count);
    }

    [Fact]
    public async Task Delegated_admin_grants_business_menu_he_does_not_hold()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);   // 角色只有「用户-授权菜单」
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(30))]));

        Assert.Equal([BusinessModule], await GrantTestKit.ModuleIdsAsync(await GrantTestKit.LoginAsync(f, account)));
    }

    [Fact]
    public async Task Business_change_saves_while_super_admin_system_record_stays_untouched()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var superId = await GrantTestKit.SuperAdminIdAsync(f);
        var (admin, adminId, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Deny(PositionQuery)]));
        var before = Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5))]));

        var rows = (await GrantTestKit.GrantRowsAsync(f, target)).ToDictionary(g => g.MenuId);
        Assert.Equal(superId, rows[PositionQuery].CreateUserId);          // 变更集之外的记录原样
        Assert.Equal(before.CreateTime, rows[PositionQuery].CreateTime);
        Assert.Equal(before.UpdateTime, rows[PositionQuery].UpdateTime);
        Assert.Equal(adminId, rows[BizWorkbench].CreateUserId);           // 授权人就是建这一行的人
    }

    [Fact]
    public async Task Two_admins_saving_in_turn_do_not_overwrite_each_other()
    {
        using var f = new AdminAppFactory();
        var (admin1, id1, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (admin2, id2, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        var (_, bizPage) = await GrantTestKit.CreateCatalogWithPageAsync(f, BusinessModule);

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin1, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5))]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin2, target, [GrantTestKit.Deny(bizPage)]));

        var rows = (await GrantTestKit.GrantRowsAsync(f, target)).ToDictionary(g => g.MenuId);
        Assert.Equal(id1, rows[BizWorkbench].CreateUserId);
        Assert.Equal(id2, rows[bizPage].CreateUserId);
    }

    [Fact]
    public async Task Module_without_flag_is_not_delegatable()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var moduleId = (await (await super.PostJson("/api/v1/sys/module/add", new { code = "crm", title = "客户", sort = 5, enabled = true })).ReadEnvelope())
            .GetProperty("data").GetInt64();
        var (catalog, _) = await GrantTestKit.CreateCatalogWithPageAsync(f, moduleId);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Deny(catalog)]));
    }

    [Fact]
    public async Task Closing_module_flag_keeps_existing_grants_but_blocks_edits()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5))]));

        await super.PutJson("/api/v1/sys/module/2", new
        {
            code = "business", title = "业务中心", icon = "lucide:briefcase-business", defaultRoute = "", apiPrefix = "biz",
            sort = 2, enabled = true, remark = "示例业务应用(可删除)", isDelegatable = false,
        });

        Assert.Equal([BusinessModule], await GrantTestKit.ModuleIdsAsync(await GrantTestKit.LoginAsync(f, account)));   // 关开关不是撤销
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(5), "续期")]));
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, None, [BizWorkbench]));
    }

    [Fact]
    public async Task Menu_moved_into_non_delegatable_module_locks_delegated_record()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (catalog, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, BusinessModule);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(page, GrantTestKit.EndOfDay(5))]));

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var c = (await sp.GetRequiredService<IRepository<SysMenu>>().GetByIdAsync(catalog))!;
            await sp.GetRequiredService<IMenuService>().UpdateAsync(catalog, new MenuInput
            {
                ParentId = 0, Type = c.Type, Title = c.Title, Permission = c.Permission, Sort = c.Sort, Enabled = c.Enabled,
                ModuleId = 1, Path = c.Path, Component = c.Component, Icon = c.Icon, Visible = c.Visible,
            });
        }

        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, [GrantTestKit.Allow(page, GrantTestKit.EndOfDay(5), "续期")]));
        Assert.Equal(41006, await GrantTestKit.PutGrantsAsync(admin, target, None, [page]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, None, [page]));   // 超管照样能收拾
    }

    [Fact]
    public async Task Invalid_change_sets_are_rejected()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);

        Assert.Equal(42031, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping, GrantTestKit.Local(DateTime.Now.AddMinutes(-5)))]));
        Assert.Equal(42031, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)], [Ping]));   // 同一菜单出现两次
        Assert.Equal(42015, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(999_999)]));        // 菜单不存在
        Assert.Equal(42001, await GrantTestKit.PutGrantsAsync(super, 999_999, [GrantTestKit.Allow(Ping)]));          // 用户不存在
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }

    [Fact]
    public async Task Empty_change_set_is_a_no_op()
    {
        using var f = new AdminAppFactory();
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(await GrantTestKit.SuperAdminAsync(f), target, None));
    }

    [Fact]
    public async Task Saving_publishes_change_event_with_details()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, Ping, UserMenuEffect.Allow);
        await GrantTestKit.InsertGrantAsync(f, target, PositionQuery, UserMenuEffect.Deny);

        var received = new TaskCompletionSource<UserMenuGrantsChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = f.Services.GetRequiredService<IEventBus>().Subscribe<UserMenuGrantsChangedEvent>((e, ct) =>
        {
            if (e.UserId == target) received.TrySetResult(e);
            return Task.CompletedTask;
        });

        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target,
            [GrantTestKit.Deny(BizWorkbench), GrantTestKit.Allow(Ping, remark: "排障")], [PositionQuery]));

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(await GrantTestKit.SuperAdminIdAsync(f), evt.OperatorId);
        Assert.Equal([BizWorkbench], evt.Added.Select(x => x.MenuId));
        Assert.Equal([Ping], evt.Updated.Select(x => x.MenuId));
        Assert.Equal("排障", evt.Updated[0].Remark);
        Assert.Equal([PositionQuery], evt.RemovedMenuIds);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantPolicyTests*"`
Expected: 编译失败（`UserMenuGrantsChangedEvent`、`IEventBus` 订阅的事件类型不存在）。

- [ ] **Step 3: 错误码与配置**

`ErrorCode.cs` 的 41xxx 段：

```csharp
    /// <summary>目标用户超出当前用户的数据范围,不能对其授权(授予角色或单独授权)</summary>
    [MsgKey("error.user.outOfDataScope")]
    UserOutOfDataScope = 41005,

    /// <summary>菜单所属模块不可转授:非超管只能对「可转授」模块里的菜单做单独授权(新增、修改、移除都算)</summary>
    [MsgKey("error.perm.menuNotGrantable")]
    MenuNotGrantable = 41006,

    /// <summary>目标用户也是管理员(能做单独授权):管理员之间的单独授权只由超管调整</summary>
    [MsgKey("error.perm.targetIsDelegatedAdmin")]
    TargetIsDelegatedAdmin = 41007,

    /// <summary>非超管授出的「允许」必须设到期时间,且到期日不晚于今天加最长天数;args 携带 maxDays</summary>
    [MsgKey("error.perm.delegatedGrantExpiryInvalid")]
    DelegatedGrantExpiryInvalid = 41008,
```

42xxx 段末尾（`OrgOutOfScope = 42030` 之后）：

```csharp
    /// <summary>单独授权的变更集不合法:同一菜单出现多次、效果取值不对、到期时间不晚于当前时间或备注超长</summary>
    [MsgKey("error.user.menuGrantInvalid")]
    UserMenuGrantInvalid = 42031,
```

`AdminSecurityOptions` 在 `DefaultInitialPassword` 之后加：

```csharp
    /// <summary>
    /// 非超管做用户单独授权时,「允许」最长可授多少天,默认 90。普通管理员授出的允许必须带到期时间,
    /// 到期日不晚于今天加这个天数;<b>0 = 不限</b>(不要求到期时间,也不设上限)。
    /// 「拒绝」只会收紧权限,不受此限;超管不受此限。长期权限应当走角色,单独授权是临时例外。
    /// </summary>
    public int DelegatedGrantMaxDays { get; set; } = 90;
```

- [ ] **Step 4: 前端文案（`ErrorCodeLocaleConsistencyTests` 与 `kernelMenuTitles.spec.ts` 会读它们）**

`zh-CN.ts` 的 `error.perm` 块加：

```ts
      menuNotGrantable: '该菜单所属模块不允许转授',
      targetIsDelegatedAdmin: '对方是管理员,只有超管能为其单独授权',
      delegatedGrantExpiryInvalid: '允许必须设置到期日,且最长 {maxDays} 天',
```

`error.user` 块加 `menuGrantInvalid: '授权变更不合法:同一菜单只能出现一次,到期时间须晚于当前时间',`。

`en-US.ts` 对应位置：

```ts
      menuNotGrantable: "This menu's module does not allow delegated grants",
      targetIsDelegatedAdmin: 'The user is an administrator; only a super admin can grant them menus individually',
      delegatedGrantExpiryInvalid: 'An allow grant needs an expiry date at most {maxDays} days away',
```

与 `menuGrantInvalid: 'Invalid grant changes: each menu at most once, and the expiry must be in the future',`。

`menuTitle.button` 块在 `userExport` 之后：zh `userGrantMenus: '用户-授权菜单',`，en `userGrantMenus: 'User - Grant Menus',`。
`kernelMenuTitles.ts` 在 `'用户-导出'` 那行之后加 `'用户-授权菜单': 'menuTitle.button.userGrantMenus',`。

- [ ] **Step 5: 模型与事件**

追加到 `UserMenuGrantModels.cs`：

```csharp
/// <summary>单独授权变更集里的一条新增或修改。</summary>
public record UserMenuGrantUpsert
{
    /// <summary>菜单节点</summary>
    public long MenuId { get; init; }

    /// <summary>允许 / 拒绝</summary>
    public UserMenuEffect Effect { get; init; }

    /// <summary>到期时间(本地时间);为空即长期。非超管授「允许」必填。</summary>
    public DateTime? ExpireTime { get; init; }

    /// <summary>授权理由,最长 200</summary>
    public string? Remark { get; init; }
}

/// <summary>
/// 按变更集保存某用户的单独授权:新增与修改走 <see cref="Upserts"/>,移除走 <see cref="Removes"/>(按菜单 Id),
/// 没提到的记录原样保留。
/// </summary>
public record SetUserMenuGrantsInput
{
    /// <summary>目标用户</summary>
    public long UserId { get; init; }

    /// <summary>新增或修改</summary>
    public IReadOnlyList<UserMenuGrantUpsert> Upserts { get; init; } = [];

    /// <summary>要移除记录的菜单 Id</summary>
    public IReadOnlyList<long> Removes { get; init; } = [];
}
```

`backend/src/SmartAdmin.Services/Events/UserMenuGrantEvents.cs`：

```csharp
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
```

- [ ] **Step 6: 守卫**

`IUserMenuGrantPolicy.cs`：

```csharp
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 单独授权的委派守卫——「当前用户能否对这个用户做这些单独授权」的唯一判定出口。
/// 保存(<see cref="IUserMenuGrantService.ApplyChangesAsync"/>)与读接口里的「能否编辑 / 能否授」都经它,不各写一套。
/// <para>规则依次:目标是自己(42029)→ 目标是超管(42007)→ 授权人是超管或系统上下文则放行 →
/// 目标不在数据范围内(41005)→ 目标也是管理员(41007)→ 「允许」必须限时(41008)→
/// 变更集里每一条(新增、修改、移除)的菜单都必须属于可转授模块(41006)。不要求授权人自己持有这些菜单。</para>
/// <para>类 public、方法 virtual,注册用 TryAdd:消费者可接入外部治理系统整体替换判定规则。</para>
/// </summary>
public interface IUserMenuGrantPolicy
{
    /// <summary>当前授权人受委派限时约束时的最长天数;超管、系统上下文、配置为 0 时为 null(不限)。</summary>
    int? DelegatedMaxDays { get; }

    /// <summary>当前授权人不能编辑该目标用户的原因(规则 1–5 的错误码);能编辑返回 null。</summary>
    Task<ErrorCode?> GetTargetBlockAsync(long targetUserId);

    /// <summary>当前授权人可授的模块 Id;超管或系统上下文返回 null(全部可授)。</summary>
    Task<IReadOnlySet<long>?> GetGrantableModuleIdsAsync();

    /// <summary>校验整份变更集,不满足直接抛 <see cref="AdminException"/>。变更集之外的记录不校验。</summary>
    Task EnsureGrantableAsync(long targetUserId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes);
}
```

`UserMenuGrantPolicy.cs`：

```csharp
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IUserMenuGrantPolicy"/> 默认实现。<c>currentUser</c> 尾随可选:未注入或未认证(后台任务、启动期)
/// 视为可信系统上下文,与 <see cref="RoleGrantPolicy"/> 同一约定。
/// </summary>
public class UserMenuGrantPolicy(
    IRepository<SysUser> users,
    IRepository<SysMenu> menus,
    IRepository<SysModule> modules,
    IPermissionProvider permissions,
    IDataScopeGuard scopeGuard,
    AdminSecurityOptions security,
    TimeProvider time,
    ICurrentUser? currentUser = null) : IUserMenuGrantPolicy
{
    /// <summary>
    /// 「能做单独授权」的那条权限码。目标用户的有效权限码里有它就是管理员,只有超管能为其单独授权。
    /// 权限码就是规范化路由,与 <c>UserController</c> 上保存端点的路由一字不差。
    /// </summary>
    public const string GrantPermissionCode = "PUT:/api/v1/sys/user/menu";

    /// <summary>超管或系统 / 未认证上下文:不受委派约束。</summary>
    protected bool IsTrusted => currentUser is null || !currentUser.IsAuthenticated || currentUser.IsSuperAdmin;

    /// <summary>当前本地时间(与审计字段同口径)。</summary>
    protected DateTime Now => time.GetLocalNow().DateTime;

    /// <inheritdoc />
    public virtual int? DelegatedMaxDays => IsTrusted || security.DelegatedGrantMaxDays <= 0 ? null : security.DelegatedGrantMaxDays;

    /// <inheritdoc />
    public virtual async Task<ErrorCode?> GetTargetBlockAsync(long targetUserId)
    {
        if (currentUser is { IsAuthenticated: true } && currentUser.UserId == targetUserId) return ErrorCode.CannotOperateSelf;
        var target = await users.GetByIdAsync(targetUserId);
        if (target is null) return ErrorCode.UserNotFound;
        if (target.IsSuperAdmin) return ErrorCode.SuperAdminProtected;
        if (IsTrusted) return null;
        if (!await scopeGuard.IsUserInScopeAsync(targetUserId)) return ErrorCode.UserOutOfDataScope;
        // 看目标当前的有效权限码:某人被分进管理员角色之后,原先给他做授权的普通管理员就不能再改他的记录
        var codes = await permissions.GetPermissionCodesAsync(targetUserId);
        return codes.Contains(GrantPermissionCode, StringComparer.OrdinalIgnoreCase) ? ErrorCode.TargetIsDelegatedAdmin : null;
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlySet<long>?> GetGrantableModuleIdsAsync()
    {
        if (IsTrusted) return null;
        var ids = await modules.AsQueryable()
            .Where(m => m.IsDelegatable == true && m.Id != DefaultModuleSeed.BUILTIN_MODULE_ID)
            .Select(m => m.Id)
            .ToListAsync();
        return ids.ToHashSet();
    }

    /// <inheritdoc />
    public virtual async Task EnsureGrantableAsync(long targetUserId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        if (await GetTargetBlockAsync(targetUserId) is { } block) throw new AdminException(block);
        if (IsTrusted) return;
        EnsureDelegatedExpiry(upserts);
        await EnsureMenusGrantableAsync([.. upserts.Select(u => u.MenuId), .. removes]);
    }

    /// <summary>
    /// 每一条「允许」必须带到期时间,且到期日不晚于今天加最长天数(按日期判,与前端日期选择器一致)。
    /// 「拒绝」只会收紧权限,不要求限时。长期权限应当走角色,单独授权本就是临时例外。
    /// </summary>
    protected virtual void EnsureDelegatedExpiry(IReadOnlyCollection<UserMenuGrantUpsert> upserts)
    {
        if (DelegatedMaxDays is not { } maxDays) return;
        var lastDay = Now.Date.AddDays(maxDays);
        foreach (var u in upserts.Where(u => u.Effect == UserMenuEffect.Allow))
            AdminException.ThrowIf(u.ExpireTime is not { } t || t.Date > lastDay, ErrorCode.DelegatedGrantExpiryInvalid,
                new Dictionary<string, object?> { ["maxDays"] = maxDays });
    }

    /// <summary>
    /// 每一条的菜单都必须属于可转授模块(上溯到根目录取 ModuleId;不挂模块的视为不可转授)。
    /// 移除也要校验:删掉超管加的一条系统模块拒绝等于放大权限,删掉一条系统模块允许也是在改超管的决定。
    /// </summary>
    protected virtual async Task EnsureMenusGrantableAsync(IReadOnlyCollection<long> menuIds)
    {
        if (menuIds.Count == 0) return;
        var grantable = await GetGrantableModuleIdsAsync();
        if (grantable is null) return;
        var byId = (await menus.AsQueryable().ToListAsync()).ToDictionary(m => m.Id);
        foreach (var id in menuIds)
            AdminException.ThrowIf(MenuTree.RootModuleId(id, byId) is not { } moduleId || !grantable.Contains(moduleId),
                ErrorCode.MenuNotGrantable);
    }
}
```

- [ ] **Step 7: 服务**

`IUserMenuGrantService.cs`：

```csharp
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
    /// </summary>
    Task ApplyChangesAsync(long userId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes);
}
```

`UserMenuGrantService.cs`：

```csharp
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IUserMenuGrantService"/> 默认实现。保存按变更集而不是整份替换:整份替换会把没动的行也删了重插,
/// 授权人与授权时间被重置成本次保存的人;两个管理员先后编辑时,后保存的还会静默删掉对方刚加的记录。
/// </summary>
public class UserMenuGrantService(
    IRepository<SysUserMenu> grants,
    IRepository<SysUser> users,
    IRepository<SysMenu> menus,
    IUserMenuGrantPolicy policy,
    ICacheProvider cache,
    IEventBus events,
    TimeProvider time,
    ICurrentUser? currentUser = null) : IUserMenuGrantService
{
    /// <summary>授权理由最长字数,与 <see cref="SysUserMenu.Remark"/> 的列宽一致。</summary>
    protected const int RemarkMaxLength = 200;

    /// <summary>当前本地时间(与审计字段同口径)。</summary>
    protected DateTime Now => time.GetLocalNow().DateTime;

    /// <inheritdoc />
    public virtual async Task ApplyChangesAsync(long userId, IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        AdminException.ThrowIf(!await users.AnyAsync(u => u.Id == userId), ErrorCode.UserNotFound);
        await ValidateChangeSetAsync(upserts, removes);
        await policy.EnsureGrantableAsync(userId, upserts, removes);

        var existing = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync()).ToDictionary(g => g.MenuId);
        var added = new List<SysUserMenu>();
        var updated = new List<SysUserMenu>();
        foreach (var u in upserts)
        {
            if (existing.TryGetValue(u.MenuId, out var row))
            {
                row.Effect = u.Effect;
                row.ExpireTime = u.ExpireTime;
                row.Remark = NormalizeRemark(u.Remark);
                updated.Add(row);
            }
            else
            {
                added.Add(new SysUserMenu
                {
                    UserId = userId, MenuId = u.MenuId, Effect = u.Effect, ExpireTime = u.ExpireTime, Remark = NormalizeRemark(u.Remark),
                });
            }
        }
        List<long> removed = [.. removes.Where(existing.ContainsKey)];

        await grants.Db.RunInTransactionAsync(async () =>
        {
            if (added.Count > 0) await grants.InsertRangeAsync(added);
            foreach (var row in updated) await grants.UpdateAsync(row);
            // 物理删除:软删残行会撞 (UserId, MenuId) 唯一索引
            if (removed.Count > 0)
                await grants.Db.Deleteable<SysUserMenu>().Where(g => g.UserId == userId && removed.Contains(g.MenuId)).ExecuteCommandAsync();
        });

        await cache.RemoveAsync(CacheKeys.UserPermissions(userId));
        await cache.IncrementAsync(CacheKeys.PortalGeneration);
        await events.PublishAsync(new UserMenuGrantsChangedEvent(
            userId, currentUser?.UserId, [.. added.Select(ToUpsert)], [.. updated.Select(ToUpsert)], removed));
    }

    /// <summary>变更集自身的合法性:同一菜单只出现一次、效果取值有效、到期时间晚于当前时间、备注不超长、菜单存在。</summary>
    protected virtual async Task ValidateChangeSetAsync(IReadOnlyCollection<UserMenuGrantUpsert> upserts, IReadOnlyCollection<long> removes)
    {
        var ids = upserts.Select(u => u.MenuId).Concat(removes).ToList();
        AdminException.ThrowIf(ids.Count != ids.Distinct().Count(), ErrorCode.UserMenuGrantInvalid);

        var now = Now;
        AdminException.ThrowIf(upserts.Any(u =>
                u.Effect is not (UserMenuEffect.Allow or UserMenuEffect.Deny)
                || u.ExpireTime is { } t && t <= now
                || u.Remark is { Length: > RemarkMaxLength }),
            ErrorCode.UserMenuGrantInvalid);

        List<long> upsertIds = [.. upserts.Select(u => u.MenuId)];
        if (upsertIds.Count == 0) return;
        var found = await menus.AsQueryable().Where(m => upsertIds.Contains(m.Id)).Select(m => m.Id).ToListAsync();
        AdminException.ThrowIf(found.Count != upsertIds.Count, ErrorCode.MenuNotFound);
    }

    private static string? NormalizeRemark(string? remark) => string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();

    private static UserMenuGrantUpsert ToUpsert(SysUserMenu g) =>
        new() { MenuId = g.MenuId, Effect = g.Effect, ExpireTime = g.ExpireTime, Remark = g.Remark };
}
```

- [ ] **Step 8: 注册、端点、按钮、高敏、可替换性契约**

`ServicesSetup.cs`，在 `services.TryAddScoped<IRoleService, RoleService>();` 下面：

```csharp
        // 用户单独授权:守卫是委派授权的唯一判定出口,服务的保存与读接口都经它
        services.TryAddScoped<IUserMenuGrantPolicy, UserMenuGrantPolicy>();
        services.TryAddScoped<IUserMenuGrantService, UserMenuGrantService>();
```

`UserController.cs`，在「导入 / 导出」分节之前加一个分节（用动作参数注入服务，不改控制器主构造器——继承它的消费者不受影响）：

```csharp
    // ── 单独授权 ──────────────────────────────────

    /// <summary>
    /// 按变更集保存某用户的单独授权(允许 / 拒绝 / 到期 / 备注),没提到的记录原样保留。
    /// 操作日志记下的请求体就是这次的增改删明细。越权判定见 <see cref="IUserMenuGrantPolicy"/>。
    /// </summary>
    [HttpPut("menu")]
    [RolePermission]
    [RequireReauth]
    [OperationLog("用户授权菜单")]
    public async Task<Result<bool>> SetMenuGrants(SetUserMenuGrantsInput input, [FromServices] IUserMenuGrantService grants)
    {
        await grants.ApplyChangesAsync(input.UserId, input.Upserts, input.Removes);
        return Result<bool>.Ok(true);
    }
```

`DefaultMenuSeed.cs`，在 Id 238 那行之后：

```csharp
        // 单独授权:弹窗要读菜单树;回显、提交、一览拆开授没有意义,同「角色-授权菜单」的归法。
        new SysMenu { Id = 239, ParentId = 230, Type = MenuType.Button, Title = "用户-授权菜单", Permission = Codes("GET:/api/v1/sys/menu/tree", "PUT:/api/v1/sys/user/menu"), Sort = 9, Enabled = true },
```

（三条读端点的码在 Task 6 端点建好时补进这颗按钮；`PermissionCodeConsistencyTests` 要求种子里每条码都对应真实端点。）

`HighSensitivityPermissions.Default` 在 `"PUT:/api/v1/sys/user/{id}/enabled",` 之后加：

```csharp
        // 单独授权:能放大或收回别人的权限
        "PUT:/api/v1/sys/user/menu",
```

`ReplaceabilityContract.Points` 的「RBAC 与数据范围」分组末尾加：

```csharp
        (typeof(IUserMenuGrantPolicy), ServiceLifetime.Scoped),
        (typeof(IUserMenuGrantService), ServiceLifetime.Scoped),
```

- [ ] **Step 9: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantPolicyTests*" --filter-class "*ReplaceabilityTests*" --filter-class "*PermissionCodeConsistencyTests*" --filter-class "*HighSensitivityConsistencyTests*" --filter-class "*ErrorCodeLocaleConsistencyTests*" --filter-class "*ErrorCodeRegistryTests*" --filter-class "*MenuSeedIdLayoutTests*" --filter-class "*SeedIdRangeTests*" --filter-class "*OpenApiContractTests*"`
Expected: 全部通过。

Run（前端译名对账，读后端种子文件）：`cd web/packages/admin && npx vitest run src/locales/kernelMenuTitles.spec.ts`
Expected: 通过。

- [ ] **Step 10: 提交**

```bash
git add backend web/packages/admin/src/locales
git commit -m "feat(rbac): 用户单独授权的变更集保存与委派守卫" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: 读路径——授权记录、有效权限、单独授权一览，方言子集

**Files:**
- Modify: `backend/src/SmartAdmin.Services/UserMenuGrant/UserMenuGrantModels.cs`
- Modify: `backend/src/SmartAdmin.Services/UserMenuGrant/IUserMenuGrantService.cs`、`UserMenuGrantService.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/UserController.cs`
- Modify: `backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs`（239 补三条读码）
- Modify: `.github/workflows/ci.yml:175`、`scripts/ci-local.ps1:98-103`（SqlServer 子集加 `UserMenuGrantDialectTests`）
- Test: `backend/tests/SmartAdmin.Tests/UserMenuGrantQueryTests.cs`、`UserMenuGrantDialectTests.cs`

**Interfaces:**
- Consumes: `IUserMenuGrantPolicy.GetTargetBlockAsync / GetGrantableModuleIdsAsync / DelegatedMaxDays`（Task 5）；`UserMenuGrantRules.Compute / LeakedCodes / IsActive`、`MenuTree.RootModuleId`（Task 2）
- Produces（前端 Task 8 照这些字段写类型）：
  - `record UserMenuGrantItem { long MenuId; UserMenuEffect Effect; DateTime? ExpireTime; string? Remark; long? GrantorId; string? GrantorName; DateTime GrantTime; long? UpdaterId; string? UpdaterName; DateTime? UpdateTime }`
  - `record UserMenuModuleItem { long Id; string Title; bool Delegatable }`
  - `record UserMenuEffectiveNode { long MenuId; long? ModuleId; bool Effective; IReadOnlyList<string> Roles; UserMenuEffect? Grant; DateTime? ExpireTime; bool Expired; bool DeniedByAncestor; bool Grantable; IReadOnlyList<UserMenuLeakedCode> LeakedCodes }`
  - `record UserMenuEffectiveOutput { long UserId; bool HasRoles; bool TargetEditable; ErrorCode? ReadOnlyReason; int? DelegatedMaxDays; IReadOnlyList<UserMenuModuleItem> Modules; IReadOnlyList<UserMenuEffectiveNode> Nodes }`
  - `enum UserMenuGrantStatus { Active = 1, Expiring = 2, Expired = 3 }`
  - `record UserMenuGrantPageInput : PageInputBase { string? User; string? Grantor; long? MenuId; UserMenuEffect? Effect; UserMenuGrantStatus? Status }`
  - `record UserMenuGrantPageItem { long Id; long UserId; string UserAccount; string UserName; long MenuId; string MenuTitle; long? ModuleId; string? ModuleTitle; UserMenuEffect Effect; DateTime? ExpireTime; UserMenuGrantStatus Status; long? GrantorId; string? GrantorName; DateTime GrantTime; string? Remark }`
  - `IUserMenuGrantService` 新增：`Task<IReadOnlyList<UserMenuGrantItem>> GetGrantsAsync(long userId)`、`Task<UserMenuEffectiveOutput> GetEffectiveAsync(long userId)`、`Task<PagedList<UserMenuGrantPageItem>> GetGrantPageAsync(UserMenuGrantPageInput input)`
  - 端点：`GET /api/v1/sys/user/{id}/menus`、`GET /api/v1/sys/user/{id}/menus/effective`、`GET /api/v1/sys/user/menu-grants/page`

- [ ] **Step 1: 写失败的测试**

`backend/tests/SmartAdmin.Tests/UserMenuGrantQueryTests.cs`：

```csharp
using System.Net;
using System.Text.Json;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>单独授权的读路径:授权记录、有效权限与来源、单独授权一览,以及读接口的数据范围收口。</summary>
public class UserMenuGrantQueryTests
{
    private const long OrgCatalog = 200, PositionQuery = 221, RoleGrantMenus = 245, Ping = 301, MenuQuery = 331, BizWorkbench = 110;

    private static async Task<JsonElement> DataAsync(HttpClient c, string url)
    {
        var env = await (await c.GetAsync(url)).ReadEnvelope();
        Assert.Equal(0, env.GetProperty("code").GetInt32());
        return env.GetProperty("data");
    }

    private static async Task<int> CodeAsync(HttpClient c, string url) =>
        (await (await c.GetAsync(url)).ReadEnvelope()).GetProperty("code").GetInt32();

    [Fact]
    public async Task Grants_endpoint_lists_records_with_grantor()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping, remark: "排障")]));

        var item = Assert.Single((await DataAsync(super, $"/api/v1/sys/user/{target}/menus")).EnumerateArray());
        Assert.Equal(Ping, item.GetProperty("menuId").GetInt64());
        Assert.Equal(1, item.GetProperty("effect").GetInt32());
        Assert.Equal("排障", item.GetProperty("remark").GetString());
        Assert.Equal(await GrantTestKit.SuperAdminIdAsync(f), item.GetProperty("grantorId").GetInt64());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("grantorName").GetString()));
    }

    [Fact]
    public async Task Records_of_deleted_menu_are_hidden_everywhere()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(page)]));
        Assert.Equal(0, (await (await super.DeleteAsync($"/api/v1/sys/menu/{page}")).ReadEnvelope()).GetProperty("code").GetInt32());

        Assert.Empty((await DataAsync(super, $"/api/v1/sys/user/{target}/menus")).EnumerateArray());
        Assert.DoesNotContain((await DataAsync(super, $"/api/v1/sys/user/{target}/menus/effective")).GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("menuId").GetInt64() == page);
        Assert.Equal(0, (await DataAsync(super, $"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Effective_explains_sources_denials_and_leaked_codes()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, [MenuQuery, PositionQuery]);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, [role]);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target,
            [GrantTestKit.Allow(Ping), GrantTestKit.Deny(RoleGrantMenus), GrantTestKit.Deny(OrgCatalog)]));

        var data = await DataAsync(super, $"/api/v1/sys/user/{target}/menus/effective");
        var nodes = data.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("menuId").GetInt64());

        Assert.True(data.GetProperty("hasRoles").GetBoolean());
        Assert.True(nodes[MenuQuery].GetProperty("effective").GetBoolean());
        Assert.Single(nodes[MenuQuery].GetProperty("roles").EnumerateArray());            // 来自角色
        Assert.True(nodes[Ping].GetProperty("effective").GetBoolean());
        Assert.Equal(1, nodes[Ping].GetProperty("grant").GetInt32());                      // 来自允许
        Assert.False(nodes[PositionQuery].GetProperty("effective").GetBoolean());          // 角色授了,但目录被拒
        Assert.True(nodes[PositionQuery].GetProperty("deniedByAncestor").GetBoolean());

        // 「角色-授权菜单」被拒,但菜单树接口还由「菜单-查询」携带
        var leaked = Assert.Single(nodes[RoleGrantMenus].GetProperty("leakedCodes").EnumerateArray());
        Assert.Equal("GET:/api/v1/sys/menu/tree", leaked.GetProperty("code").GetString());
        Assert.Equal([MenuQuery], leaked.GetProperty("carrierMenuIds").EnumerateArray().Select(x => x.GetInt64()));
    }

    [Fact]
    public async Task Effective_carries_modules_and_grantability_without_module_permission()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/sys/module/list")).StatusCode);   // 没有模块查询权限

        var data = await DataAsync(admin, $"/api/v1/sys/user/{target}/menus/effective");
        var modules = data.GetProperty("modules").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetInt64());
        Assert.False(modules[1].GetProperty("delegatable").GetBoolean());
        Assert.True(modules[2].GetProperty("delegatable").GetBoolean());
        var nodes = data.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("menuId").GetInt64());
        Assert.True(nodes[BizWorkbench].GetProperty("grantable").GetBoolean());
        Assert.False(nodes[Ping].GetProperty("grantable").GetBoolean());
        Assert.Equal(90, data.GetProperty("delegatedMaxDays").GetInt32());
        Assert.True(data.GetProperty("targetEditable").GetBoolean());
        Assert.False(data.GetProperty("hasRoles").GetBoolean());

        var asSuper = await DataAsync(await GrantTestKit.SuperAdminAsync(f), $"/api/v1/sys/user/{target}/menus/effective");
        Assert.Equal(JsonValueKind.Null, asSuper.GetProperty("delegatedMaxDays").ValueKind);
        Assert.All(asSuper.GetProperty("nodes").EnumerateArray(), n => Assert.True(n.GetProperty("grantable").GetBoolean()));
    }

    [Fact]
    public async Task Effective_marks_target_read_only_with_reason()
    {
        using var f = new AdminAppFactory();
        var (admin, adminId, adminRole) = await GrantTestKit.DelegatedAdminAsync(f);
        var (otherAdmin, _) = await GrantTestKit.CreateUserAsync(f, [adminRole]);

        var other = await DataAsync(admin, $"/api/v1/sys/user/{otherAdmin}/menus/effective");
        Assert.False(other.GetProperty("targetEditable").GetBoolean());
        Assert.Equal((int)ErrorCode.TargetIsDelegatedAdmin, other.GetProperty("readOnlyReason").GetInt32());

        var self = await DataAsync(admin, $"/api/v1/sys/user/{adminId}/menus/effective");
        Assert.Equal((int)ErrorCode.CannotOperateSelf, self.GetProperty("readOnlyReason").GetInt32());
    }

    [Fact]
    public async Task Reads_reject_users_outside_data_scope()
    {
        using var f = new AdminAppFactory();
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);
        var (outside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 6);
        Assert.Equal(41005, await CodeAsync(admin, $"/api/v1/sys/user/{outside}/menus"));
        Assert.Equal(41005, await CodeAsync(admin, $"/api/v1/sys/user/{outside}/menus/effective"));
    }

    [Fact]
    public async Task Grant_page_filters_by_status_effect_grantor_and_user()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target,
            [GrantTestKit.Allow(Ping), GrantTestKit.Allow(BizWorkbench, GrantTestKit.EndOfDay(3))]));
        await GrantTestKit.InsertGrantAsync(f, target, PositionQuery, UserMenuEffect.Deny, DateTime.Now.AddDays(-1));   // 已过期,授权人为空

        async Task<int> Total(string query) =>
            (await DataAsync(super, $"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}{query}")).GetProperty("total").GetInt32();

        Assert.Equal(3, await Total(""));
        Assert.Equal(2, await Total("&Status=1"));   // 生效中,含 7 天内到期
        Assert.Equal(1, await Total("&Status=2"));   // 7 天内到期
        Assert.Equal(1, await Total("&Status=3"));   // 已过期
        Assert.Equal(1, await Total("&Effect=2"));
        Assert.Equal(2, await Total("&Grantor=superAdmin"));
        Assert.Equal(0, (await DataAsync(super, "/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User=no-such-user")).GetProperty("total").GetInt32());

        var item = Assert.Single((await DataAsync(super, $"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}&Status=2"))
            .GetProperty("items").EnumerateArray());
        Assert.Equal(BizWorkbench, item.GetProperty("menuId").GetInt64());
        Assert.Equal(2, item.GetProperty("status").GetInt32());
        Assert.Equal(2, item.GetProperty("moduleId").GetInt64());
        Assert.Equal(account, item.GetProperty("userAccount").GetString());
    }

    [Fact]
    public async Task Grant_page_shows_delegated_admin_only_users_in_scope()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (admin, _, _) = await GrantTestKit.DelegatedAdminAsync(f, DataScopeType.Custom, [3], orgId: 3);
        var (inside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 3);
        var (outside, _) = await GrantTestKit.CreateUserAsync(f, [], orgId: 6);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, inside, [GrantTestKit.Deny(BizWorkbench)]));
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, outside, [GrantTestKit.Deny(BizWorkbench)]));

        static List<long> UserIds(JsonElement data) => [.. data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetInt64())];
        var mine = UserIds(await DataAsync(admin, "/api/v1/sys/user/menu-grants/page?Current=1&Size=50"));
        Assert.Contains(inside, mine);
        Assert.DoesNotContain(outside, mine);

        var all = UserIds(await DataAsync(super, "/api/v1/sys/user/menu-grants/page?Current=1&Size=50"));
        Assert.Contains(inside, all);
        Assert.Contains(outside, all);
    }
}
```

`backend/tests/SmartAdmin.Tests/UserMenuGrantDialectTests.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 单独授权里方言敏感的查询形态:可空时间比较、Contains、SELECT DISTINCT、排序分页。
/// 进 SqlServer 子集(ci.yml 与 scripts/ci-local.ps1 同一份 filter);与库无关的逻辑由其余用例在另外三种库上全量覆盖。
/// </summary>
public class UserMenuGrantDialectTests
{
    [Fact]
    public async Task Status_filters_and_expiry_queries_run_on_this_dialect()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, 301, UserMenuEffect.Allow);                             // 长期
        await GrantTestKit.InsertGrantAsync(f, target, 110, UserMenuEffect.Allow, DateTime.Now.AddDays(2));    // 将到期
        await GrantTestKit.InsertGrantAsync(f, target, 221, UserMenuEffect.Deny, DateTime.Now.AddDays(-2));    // 已过期

        Assert.Contains("GET:/api/v1/ping", await GrantTestKit.CodesOfAsync(f, target));   // 走单独授权叠加与最近到期查询
        foreach (var (status, expected) in new[] { (1, 2), (2, 1), (3, 1) })
        {
            var env = await (await super.GetAsync($"/api/v1/sys/user/menu-grants/page?Current=1&Size=20&User={account}&Status={status}")).ReadEnvelope();
            Assert.Equal(expected, env.GetProperty("data").GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task Menu_change_fans_out_to_every_user_with_grants()
    {
        using var f = new AdminAppFactory();
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        await GrantTestKit.InsertGrantAsync(f, target, 301, UserMenuEffect.Allow);
        await GrantTestKit.InsertGrantAsync(f, target, 110, UserMenuEffect.Deny);
        Assert.Contains("GET:/api/v1/ping", await GrantTestKit.CodesOfAsync(f, target));   // 预热缓存

        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            // 绕过服务删掉允许那一行、不失效缓存:之后只有菜单变更的扇出能让缓存失效
            await sp.GetRequiredService<IRepository<SysUserMenu>>().Db.Deleteable<SysUserMenu>()
                .Where(g => g.UserId == target && g.MenuId == 301).ExecuteCommandAsync();
            await sp.GetRequiredService<IRbacService>().InvalidatePermissionsByMenuAsync(500);   // 一个跟它无关的菜单
        }

        Assert.DoesNotContain("GET:/api/v1/ping", await GrantTestKit.CodesOfAsync(f, target));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantQueryTests*" --filter-class "*UserMenuGrantDialectTests*"`
Expected: `UserMenuGrantQueryTests` 与 `Status_filters_...` 失败（端点不存在，`ReadEnvelope` 报 404）；`Menu_change_fans_out_...` 通过（Task 3 已实现，它是锁方言的）。

- [ ] **Step 3: 模型**

追加到 `UserMenuGrantModels.cs`（文件头补 `using SmartAdmin.Core;`）：

```csharp
/// <summary>一条单独授权记录(授权菜单弹窗回显)。</summary>
public record UserMenuGrantItem
{
    public long MenuId { get; init; }
    public UserMenuEffect Effect { get; init; }
    /// <summary>到期时间;为空即长期</summary>
    public DateTime? ExpireTime { get; init; }
    public string? Remark { get; init; }
    /// <summary>授权人(建这一行的人;系统写入为空)</summary>
    public long? GrantorId { get; init; }
    public string? GrantorName { get; init; }
    /// <summary>授权时间</summary>
    public DateTime GrantTime { get; init; }
    /// <summary>最后修改人</summary>
    public long? UpdaterId { get; init; }
    public string? UpdaterName { get; init; }
    public DateTime? UpdateTime { get; init; }
}

/// <summary>授权弹窗用的模块清单项:授权弹窗不调模块列表接口(那个要「模块-查询」权限)。</summary>
public record UserMenuModuleItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    /// <summary>可转授(内置 system 模块恒为 false)</summary>
    public bool Delegatable { get; init; }
}

/// <summary>一个菜单节点对目标用户是否有效、为什么。</summary>
public record UserMenuEffectiveNode
{
    public long MenuId { get; init; }
    /// <summary>所属模块(上溯到根目录取 ModuleId)</summary>
    public long? ModuleId { get; init; }
    /// <summary>最终是否有效</summary>
    public bool Effective { get; init; }
    /// <summary>授予该节点的启用角色名</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
    /// <summary>该节点上的单独授权效果(含已过期的);没有记录为 null</summary>
    public UserMenuEffect? Grant { get; init; }
    public DateTime? ExpireTime { get; init; }
    /// <summary>该节点上的单独授权已过期</summary>
    public bool Expired { get; init; }
    /// <summary>被某个祖先节点的拒绝连带收回</summary>
    public bool DeniedByAncestor { get; init; }
    /// <summary>当前授权人能否改这个节点(超管恒为 true;普通管理员看菜单所属模块是否可转授)</summary>
    public bool Grantable { get; init; }
    /// <summary>该节点被拒时仍然有效的权限码及携带它们的节点(只在生效中的拒绝上给出)</summary>
    public IReadOnlyList<UserMenuLeakedCode> LeakedCodes { get; init; } = [];
}

/// <summary>授权弹窗需要的全部数据,一次取齐。</summary>
public record UserMenuEffectiveOutput
{
    public long UserId { get; init; }
    /// <summary>目标用户有没有启用中的角色(没有时数据范围为「仅本人」,界面据此提示)</summary>
    public bool HasRoles { get; init; }
    /// <summary>当前授权人能否编辑这个目标用户</summary>
    public bool TargetEditable { get; init; }
    /// <summary>不能编辑的原因:42029 自己 / 42007 超管 / 41005 范围外 / 41007 对方是管理员</summary>
    public ErrorCode? ReadOnlyReason { get; init; }
    /// <summary>委派授权最长天数;超管、配置为 0 时为空</summary>
    public int? DelegatedMaxDays { get; init; }
    public IReadOnlyList<UserMenuModuleItem> Modules { get; init; } = [];
    /// <summary>全部未删除菜单节点(含停用的)各一项</summary>
    public IReadOnlyList<UserMenuEffectiveNode> Nodes { get; init; } = [];
}

/// <summary>单独授权的状态(一览筛选与展示)。</summary>
public enum UserMenuGrantStatus
{
    /// <summary>生效中(作筛选条件时含 7 天内到期的)</summary>
    Active = 1,

    /// <summary>7 天内到期</summary>
    Expiring = 2,

    /// <summary>已过期</summary>
    Expired = 3,
}

/// <summary>单独授权一览的查询条件。</summary>
public record UserMenuGrantPageInput : PageInputBase
{
    /// <summary>目标用户账号或姓名,模糊</summary>
    public string? User { get; init; }
    /// <summary>授权人账号或姓名,模糊</summary>
    public string? Grantor { get; init; }
    public long? MenuId { get; init; }
    public UserMenuEffect? Effect { get; init; }
    public UserMenuGrantStatus? Status { get; init; }
}

/// <summary>单独授权一览的一行。</summary>
public record UserMenuGrantPageItem
{
    public long Id { get; init; }
    public long UserId { get; init; }
    public string UserAccount { get; init; } = "";
    public string UserName { get; init; } = "";
    public long MenuId { get; init; }
    public string MenuTitle { get; init; } = "";
    public long? ModuleId { get; init; }
    public string? ModuleTitle { get; init; }
    public UserMenuEffect Effect { get; init; }
    public DateTime? ExpireTime { get; init; }
    public UserMenuGrantStatus Status { get; init; }
    public long? GrantorId { get; init; }
    public string? GrantorName { get; init; }
    public DateTime GrantTime { get; init; }
    public string? Remark { get; init; }
}
```

- [ ] **Step 4: 接口加读方法**

`IUserMenuGrantService` 加：

```csharp
    /// <summary>目标用户的单独授权记录,带授权人、授权时间、最后修改人。菜单已不存在的记录不返回。非超管只能读数据范围内的用户。</summary>
    Task<IReadOnlyList<UserMenuGrantItem>> GetGrantsAsync(long userId);

    /// <summary>授权弹窗需要的全部数据:每个节点是否有效与来源、能否授、漏网的权限码、能否编辑及原因、委派最长天数、模块清单。</summary>
    Task<UserMenuEffectiveOutput> GetEffectiveAsync(long userId);

    /// <summary>单独授权一览,分页。非超管只看得到数据范围内用户的记录;菜单已不存在的记录不显示。</summary>
    Task<PagedList<UserMenuGrantPageItem>> GetGrantPageAsync(UserMenuGrantPageInput input);
```

- [ ] **Step 5: 服务实现读方法**

主构造器换成（追加 `modules`、`roles`、`userRoles`、`roleMenus`、`scopeGuard`）：

```csharp
public class UserMenuGrantService(
    IRepository<SysUserMenu> grants,
    IRepository<SysUser> users,
    IRepository<SysMenu> menus,
    IRepository<SysModule> modules,
    IRepository<SysRole> roles,
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IUserMenuGrantPolicy policy,
    IDataScopeGuard scopeGuard,
    ICacheProvider cache,
    IEventBus events,
    TimeProvider time,
    ICurrentUser? currentUser = null) : IUserMenuGrantService
```

在 `ValidateChangeSetAsync` 之前加：

```csharp
    /// <summary>「7 天内到期」的天数。</summary>
    protected const int ExpiringDays = 7;

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<UserMenuGrantItem>> GetGrantsAsync(long userId)
    {
        await EnsureReadableAsync(userId);
        var menuIds = (await menus.AsQueryable().Select(m => m.Id).ToListAsync()).ToHashSet();
        var rows = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync())
            .Where(g => menuIds.Contains(g.MenuId))
            .OrderBy(g => g.MenuId)
            .ToList();
        var people = await UsersByIdAsync(rows.SelectMany(g => new[] { g.CreateUserId, g.UpdateUserId }));
        return [.. rows.Select(g => new UserMenuGrantItem
        {
            MenuId = g.MenuId, Effect = g.Effect, ExpireTime = g.ExpireTime, Remark = g.Remark,
            GrantorId = g.CreateUserId, GrantorName = NameOf(people, g.CreateUserId), GrantTime = g.CreateTime,
            UpdaterId = g.UpdateUserId, UpdaterName = NameOf(people, g.UpdateUserId), UpdateTime = g.UpdateTime,
        })];
    }

    /// <inheritdoc />
    public virtual async Task<UserMenuEffectiveOutput> GetEffectiveAsync(long userId)
    {
        await EnsureReadableAsync(userId);
        var now = Now;
        var target = (await users.GetByIdAsync(userId))!;
        var allMenus = await menus.AsQueryable().ToListAsync();
        var byId = allMenus.ToDictionary(m => m.Id);
        var rows = (await grants.AsQueryable().Where(g => g.UserId == userId).ToListAsync()).Where(g => byId.ContainsKey(g.MenuId)).ToList();

        var roleIds = await userRoles.AsQueryable()
            .InnerJoin<SysRole>((ur, r) => ur.RoleId == r.Id && r.Enabled)
            .Where((ur, r) => ur.UserId == userId)
            .Select((ur, r) => ur.RoleId).ToListAsync();
        var roleNames = roleIds.Count == 0
            ? new Dictionary<long, string>()
            : (await roles.AsQueryable().Where(r => roleIds.Contains(r.Id)).ToListAsync()).ToDictionary(r => r.Id, r => r.Name);
        List<SysRoleMenu> links = roleIds.Count == 0 ? [] : await roleMenus.AsQueryable().Where(x => roleIds.Contains(x.RoleId)).ToListAsync();
        var rolesByMenu = links.GroupBy(l => l.MenuId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(l => roleNames[l.RoleId]).Distinct().Order()]);

        // 超管照旧全量放行,单独授权不参与计算
        var result = target.IsSuperAdmin
            ? new UserMenuGrantResult(allMenus.Where(m => m.Enabled).Select(m => m.Id).ToHashSet(), new HashSet<long>(), null)
            : UserMenuGrantRules.Compute(links.Select(l => l.MenuId), rows, allMenus, now);

        var grantable = await policy.GetGrantableModuleIdsAsync();
        var block = await policy.GetTargetBlockAsync(userId);
        var grantByMenu = rows.ToDictionary(g => g.MenuId);
        var nodes = allMenus.Select(m =>
        {
            var moduleId = MenuTree.RootModuleId(m.Id, byId);
            grantByMenu.TryGetValue(m.Id, out var g);
            var activeDeny = g is { Effect: UserMenuEffect.Deny } && UserMenuGrantRules.IsActive(g, now);
            return new UserMenuEffectiveNode
            {
                MenuId = m.Id,
                ModuleId = moduleId,
                Effective = result.EffectiveMenuIds.Contains(m.Id),
                Roles = rolesByMenu.GetValueOrDefault(m.Id) ?? [],
                Grant = g?.Effect,
                ExpireTime = g?.ExpireTime,
                Expired = g is not null && !UserMenuGrantRules.IsActive(g, now),
                DeniedByAncestor = result.DeniedMenuIds.Contains(m.Id) && !activeDeny,
                Grantable = grantable is null || (moduleId is { } mid && grantable.Contains(mid)),
                LeakedCodes = activeDeny ? UserMenuGrantRules.LeakedCodes(m.Id, allMenus, result.EffectiveMenuIds) : [],
            };
        }).ToList();

        var allModules = await modules.AsQueryable().OrderBy(m => m.Sort).OrderBy(m => m.Id).ToListAsync();
        return new UserMenuEffectiveOutput
        {
            UserId = userId,
            HasRoles = roleIds.Count > 0,
            TargetEditable = block is null,
            ReadOnlyReason = block,
            DelegatedMaxDays = policy.DelegatedMaxDays,
            Modules = [.. allModules.Select(x => new UserMenuModuleItem
            {
                Id = x.Id, Title = x.Title, Delegatable = x.Id != DefaultModuleSeed.BUILTIN_MODULE_ID && x.IsDelegatable == true,
            })],
            Nodes = nodes,
        };
    }

    /// <inheritdoc />
    public virtual async Task<PagedList<UserMenuGrantPageItem>> GetGrantPageAsync(UserMenuGrantPageInput input)
    {
        var now = Now;
        var query = grants.AsQueryable();

        var scoped = await scopeGuard.ResolveScopedUserIdsAsync();
        if (scoped is not null)
        {
            if (scoped.Count == 0) return Empty(input);
            query = query.Where(g => scoped.Contains(g.UserId));
        }
        if (!string.IsNullOrWhiteSpace(input.User))
        {
            var ids = await MatchUserIdsAsync(input.User.Trim());
            if (ids.Count == 0) return Empty(input);
            query = query.Where(g => ids.Contains(g.UserId));
        }
        if (!string.IsNullOrWhiteSpace(input.Grantor))
        {
            var ids = await MatchUserIdsAsync(input.Grantor.Trim());
            if (ids.Count == 0) return Empty(input);
            query = query.Where(g => g.CreateUserId != null && ids.Contains(g.CreateUserId.Value));
        }
        if (input.MenuId is { } menuId) query = query.Where(g => g.MenuId == menuId);
        if (input.Effect is { } effect) query = query.Where(g => g.Effect == effect);
        var soon = now.AddDays(ExpiringDays);
        query = input.Status switch
        {
            UserMenuGrantStatus.Active => query.Where(g => g.ExpireTime == null || g.ExpireTime > now),
            UserMenuGrantStatus.Expiring => query.Where(g => g.ExpireTime != null && g.ExpireTime > now && g.ExpireTime <= soon),
            UserMenuGrantStatus.Expired => query.Where(g => g.ExpireTime != null && g.ExpireTime <= now),
            _ => query,
        };

        // 菜单已不存在(软删)的记录不显示:预取现存菜单 Id,不写跨表子查询
        var allMenus = await menus.AsQueryable().ToListAsync();
        if (allMenus.Count == 0) return Empty(input);
        List<long> menuIds = [.. allMenus.Select(m => m.Id)];
        query = query.Where(g => menuIds.Contains(g.MenuId));

        var page = await query.OrderByDescending(g => g.CreateTime).OrderBy(g => g.Id).ToPagedListAsync(input.Current, input.Size);

        var byId = allMenus.ToDictionary(m => m.Id);
        var moduleTitles = (await modules.AsQueryable().ToListAsync()).ToDictionary(m => m.Id, m => m.Title);
        var people = await UsersByIdAsync(page.Items.SelectMany(g => new long?[] { g.UserId, g.CreateUserId }));
        return new PagedList<UserMenuGrantPageItem>
        {
            Current = page.Current,
            Size = page.Size,
            Total = page.Total,
            Items = [.. page.Items.Select(g =>
            {
                var moduleId = MenuTree.RootModuleId(g.MenuId, byId);
                var user = people.GetValueOrDefault(g.UserId);
                return new UserMenuGrantPageItem
                {
                    Id = g.Id, UserId = g.UserId, UserAccount = user?.Account ?? "", UserName = user?.Name ?? "",
                    MenuId = g.MenuId, MenuTitle = byId.GetValueOrDefault(g.MenuId)?.Title ?? "",
                    ModuleId = moduleId, ModuleTitle = moduleId is { } mid ? moduleTitles.GetValueOrDefault(mid) : null,
                    Effect = g.Effect, ExpireTime = g.ExpireTime, Status = StatusOf(g, now),
                    GrantorId = g.CreateUserId, GrantorName = NameOf(people, g.CreateUserId), GrantTime = g.CreateTime, Remark = g.Remark,
                };
            })],
        };
    }

    /// <summary>读接口的数据范围收口:一个人的权限构成本身是敏感信息,不能靠猜 Id 读到范围外用户的。</summary>
    protected virtual async Task EnsureReadableAsync(long userId)
    {
        AdminException.ThrowIf(!await scopeGuard.IsUserInScopeAsync(userId), ErrorCode.UserOutOfDataScope);
        AdminException.ThrowIf(!await users.AnyAsync(u => u.Id == userId), ErrorCode.UserNotFound);
    }

    /// <summary>账号或姓名模糊匹配的用户 Id(含已软删的:授权人可能已离职)。</summary>
    private async Task<List<long>> MatchUserIdsAsync(string keyword) =>
        await users.AsQueryable().ClearFilter<ISoftDelete>()
            .Where(u => u.Account.Contains(keyword) || u.Name.Contains(keyword))
            .Select(u => u.Id).ToListAsync();

    private async Task<Dictionary<long, SysUser>> UsersByIdAsync(IEnumerable<long?> ids)
    {
        List<long> list = [.. ids.Where(x => x is not null).Select(x => x!.Value).Distinct()];
        if (list.Count == 0) return [];
        return (await users.AsQueryable().ClearFilter<ISoftDelete>().Where(u => list.Contains(u.Id)).ToListAsync()).ToDictionary(u => u.Id);
    }

    private static string? NameOf(Dictionary<long, SysUser> people, long? id) =>
        id is { } v && people.TryGetValue(v, out var u) ? u.Name : null;

    private static UserMenuGrantStatus StatusOf(SysUserMenu g, DateTime now) =>
        !UserMenuGrantRules.IsActive(g, now) ? UserMenuGrantStatus.Expired
        : g.ExpireTime is { } t && t <= now.AddDays(ExpiringDays) ? UserMenuGrantStatus.Expiring
        : UserMenuGrantStatus.Active;

    private static PagedList<UserMenuGrantPageItem> Empty(PageInputBase input) => new() { Current = input.Current, Size = input.Size, Total = 0 };
```

文件头补 `using SqlSugar;`（`ClearFilter<ISoftDelete>` 的扩展所在）。`ISoftDelete` 在 `SmartAdmin.SqlSugar` 命名空间。

- [ ] **Step 6: 三个读端点，按钮 239 补码**

`UserController` 的「单独授权」分节里加：

```csharp
    /// <summary>取某用户的单独授权记录(授权菜单弹窗回显)</summary>
    [HttpGet("{id}/menus")]
    [RolePermission]
    public async Task<Result<IReadOnlyList<UserMenuGrantItem>>> GetMenuGrants(long id, [FromServices] IUserMenuGrantService grants) =>
        Result<IReadOnlyList<UserMenuGrantItem>>.Ok(await grants.GetGrantsAsync(id));

    /// <summary>取某用户的有效权限与来源(授权菜单弹窗一次取齐:能否授、能否编辑、模块清单)</summary>
    [HttpGet("{id}/menus/effective")]
    [RolePermission]
    public async Task<Result<UserMenuEffectiveOutput>> GetEffectiveMenus(long id, [FromServices] IUserMenuGrantService grants) =>
        Result<UserMenuEffectiveOutput>.Ok(await grants.GetEffectiveAsync(id));

    /// <summary>单独授权一览(分页 + 筛选)</summary>
    [HttpGet("menu-grants/page")]
    [RolePermission]
    public async Task<Result<PagedList<UserMenuGrantPageItem>>> MenuGrantPage([FromQuery] UserMenuGrantPageInput input, [FromServices] IUserMenuGrantService grants) =>
        Result<PagedList<UserMenuGrantPageItem>>.Ok(await grants.GetGrantPageAsync(input));
```

`DefaultMenuSeed` 的 239 改成：

```csharp
        new SysMenu { Id = 239, ParentId = 230, Type = MenuType.Button, Title = "用户-授权菜单", Permission = Codes("GET:/api/v1/sys/menu/tree", "GET:/api/v1/sys/user/{id}/menus", "GET:/api/v1/sys/user/{id}/menus/effective", "PUT:/api/v1/sys/user/menu", "GET:/api/v1/sys/user/menu-grants/page"), Sort = 9, Enabled = true },
```

- [ ] **Step 7: SqlServer 子集登记方言用例**

`.github/workflows/ci.yml:175` 的 filter 串末尾（`--filter-class "*MultiConfigIdTests*"` 之后、收尾单引号之前）加 ` --filter-class "*UserMenuGrantDialectTests*"`。
`scripts/ci-local.ps1` 的 `$sqlServerSubset` 末尾加 `, '--filter-class', '*UserMenuGrantDialectTests*'`（保持与 ci.yml 逐字一致；这个文件**必须保留 UTF-8 BOM**，用 Edit 工具改不会动 BOM，别用会重写编码的工具）。

- [ ] **Step 8: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantQueryTests*" --filter-class "*UserMenuGrantDialectTests*" --filter-class "*UserMenuGrantPolicyTests*" --filter-class "*PermissionCodeConsistencyTests*" --filter-class "*OpenApiContractTests*" --filter-class "*PermissionRoutesEndpointTests*" --filter-class "*PagingContractTests*"`
Expected: 全部通过。

- [ ] **Step 9: 后端全量**

Run: `dotnet test backend/SmartAdmin.slnx`
Expected: 全部通过。

- [ ] **Step 10: 提交**

```bash
git add backend .github/workflows/ci.yml scripts/ci-local.ps1
git commit -m "feat(rbac): 单独授权的记录回显、有效权限来源与授权一览" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: 关联清理——用户、菜单彻底删除时清掉单独授权

**Files:**
- Modify: `backend/src/SmartAdmin.Services/RecycleBin/KernelRecycleBinTypes.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs:224`
- Test: `backend/tests/SmartAdmin.Tests/UserMenuGrantLifecycleTests.cs`

**Interfaces:**
- Produces: `public class MenuRecycleBinType() : RecycleBinType<SysMenu>("menu", e => e.Title, e => e.Permission)`（覆写 `BeforePurgeAsync`）

- [ ] **Step 1: 写失败的测试**

```csharp
using System.Net;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>单独授权的关联清理:软删保留、恢复照旧生效;彻底删除用户或菜单时清掉指向它们的记录。</summary>
public class UserMenuGrantLifecycleTests
{
    private const long Ping = 301;

    private static async Task<int> CodeOf(Task<HttpResponseMessage> call) => (await (await call).ReadEnvelope()).GetProperty("code").GetInt32();

    [Fact]
    public async Task Soft_deleted_user_keeps_grants_and_restore_brings_them_back()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, account) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/user/{target}")));
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));                                   // 软删不清
        Assert.Equal(0, await CodeOf(super.PostAsync($"/api/v1/sys/recycle/user/{target}/restore", null)));

        var c = await GrantTestKit.LoginAsync(f, account);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ping")).StatusCode);
    }

    [Fact]
    public async Task Purging_user_removes_their_grants()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Allow(Ping)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/user/{target}")));
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/user/{target}")));
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }

    [Fact]
    public async Task Purging_menu_removes_grants_pointing_at_it()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var (_, page) = await GrantTestKit.CreateCatalogWithPageAsync(f, 2);
        var (target, _) = await GrantTestKit.CreateUserAsync(f, []);
        Assert.Equal(0, await GrantTestKit.PutGrantsAsync(super, target, [GrantTestKit.Deny(page)]));

        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/menu/{page}")));
        Assert.Single(await GrantTestKit.GrantRowsAsync(f, target));                                   // 软删菜单不清
        Assert.Equal(0, await CodeOf(super.DeleteAsync($"/api/v1/sys/recycle/menu/{page}")));
        Assert.Empty(await GrantTestKit.GrantRowsAsync(f, target));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantLifecycleTests*"`
Expected: `Soft_deleted_user_...` 通过（已有行为，钉住）；两条 `Purging_...` FAIL（记录还在）。

- [ ] **Step 3: 实现**

`KernelRecycleBinTypes.cs` 的 `UserRecycleBinType`：类注释改成「……彻底删除前清角色关联、单独授权与外部身份绑定。」，`BeforePurgeAsync` 改成：

```csharp
    /// <inheritdoc />
    protected override async Task BeforePurgeAsync(IServiceProvider sp, long id)
    {
        var db = sp.GetRequiredService<ISqlSugarClient>();
        await db.Deleteable<SysUserRole>().Where(ur => ur.UserId == id).ExecuteCommandAsync();
        await db.Deleteable<SysUserMenu>().Where(g => g.UserId == id).ExecuteCommandAsync();
        if (sp.GetService<ISysUserExternalService>() is { } bindings) await bindings.UnbindAllAsync(id);
    }
```

文件末尾加：

```csharp
/// <summary>
/// 已删菜单:彻底删除前物理删掉指向它的单独授权。读取单独授权时也会过滤掉菜单已不存在的行,这里是另一道保险。
/// </summary>
public class MenuRecycleBinType() : RecycleBinType<SysMenu>("menu", e => e.Title, e => e.Permission)
{
    /// <inheritdoc />
    protected override Task BeforePurgeAsync(IServiceProvider sp, long id) =>
        sp.GetRequiredService<ISqlSugarClient>().Deleteable<SysUserMenu>().Where(g => g.MenuId == id).ExecuteCommandAsync();
}
```

`ServicesSetup.cs`：删掉 `services.AddRecycleBinType<SysMenu>("menu", e => e.Title, e => e.Permission);`，在 `JobRecycleBinType` 那行之后加：

```csharp
        services.TryAddEnumerable(ServiceDescriptor.Singleton<RecycleBinType, MenuRecycleBinType>());
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserMenuGrantLifecycleTests*" --filter-class "*RecycleBinTests*" --filter-class "*ReplaceabilityTests*"`
Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add backend
git commit -m "feat(rbac): 用户与菜单彻底删除时清掉指向它们的单独授权" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: 系统模块菜单只授内置角色，升级时清掉存量

用户确认的追加需求（规格 6.7）：超管给界面上新建的角色授权时看不到也授不了「系统」模块的菜单；种子里的内置角色（Id 1–999）不受限；
从种子版本 6 升级上来的那一次启动，物理删除非内置角色上的系统模块菜单。

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Entities/SysRole.cs`（`IsBuiltin`）
- Modify: `backend/src/SmartAdmin.Core/ErrorCode.cs`（41009）
- Modify: `web/packages/admin/src/locales/zh-CN.ts`、`en-US.ts`（`error.role.systemMenuNotAssignable`）
- Modify: `backend/src/SmartAdmin.Services/Rbac/RbacService.cs:34-48`
- Create: `backend/src/SmartAdmin.Services/Rbac/SystemMenuRoleGrantCleanup.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`
- Modify: `backend/tests/SmartAdmin.Tests/GrantTestKit.cs`（`SetRoleMenusAsync`）
- Modify（只改搭建、不改断言）: `MultiCodeMenuTests.cs:31`、`RoleCrudTests.cs:77`、`PermissionRoutesEndpointTests.cs:75`、`MenuCrudTests.cs:56`、`ApiKeyAuthTests.cs:147,153`
- Test: `backend/tests/SmartAdmin.Tests/SystemMenuRoleGrantTests.cs`、`SeedUpgradeTests.cs`

**Interfaces:**
- Consumes: `MenuTree.RootModuleId`（Task 2）；`SysSchemaVersion.Current == "7"`（Task 4）
- Produces:
  - `SysRole.IsBuiltin`（`bool`，只读计算属性，`[SugarColumn(IsIgnore = true)]`，JSON 里是 `isBuiltin`）
  - `ErrorCode.SystemMenuNotAssignable = 41009`（msgKey `error.role.systemMenuNotAssignable`）
  - `RbacService`：`protected virtual Task EnsureRoleMenusAssignableAsync(SysRole role, IReadOnlyCollection<long> menuIds)`
  - `public class SystemMenuRoleGrantCleanup : IDatabaseReadyHook`（`public const int SinceSchemaVersion = 7`）
  - `GrantTestKit.SetRoleMenusAsync(AdminAppFactory f, long roleId, IReadOnlyCollection<long> menuIds)`（经服务、无登录上下文）

- [ ] **Step 1: 写失败的测试**

先给 `GrantTestKit` 加一个经服务配角色菜单的工具：

```csharp
    /// <summary>
    /// 经服务给角色配菜单。无登录上下文 = 可信系统上下文,不受「系统模块菜单只授内置角色」限制;
    /// 给那些锁别的行为、只是顺手要给新建角色配系统菜单的用例用。要测这条限制本身,走 HTTP。
    /// </summary>
    public static async Task SetRoleMenusAsync(AdminAppFactory f, long roleId, IReadOnlyCollection<long> menuIds)
    {
        using var s = f.Services.CreateScope();
        await s.ServiceProvider.GetRequiredService<IRbacService>().SetRoleMenusAsync(roleId, menuIds);
    }
```

`backend/tests/SmartAdmin.Tests/SystemMenuRoleGrantTests.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 系统模块的菜单只能授给内置角色(种子里固定 Id 1–999 的角色)。界面上新建的角色授不了;
/// 后台代码在无登录上下文里调服务不受限(与超管专属守卫同一约定)。
/// </summary>
public class SystemMenuRoleGrantTests
{
    private const long BuiltinRole = 1, BizWorkbench = 110, Ping = 301;

    private static async Task<int> PutRoleMenusAsync(HttpClient c, long roleId, long[] menuIds) =>
        (await (await c.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds })).ReadEnvelope()).GetProperty("code").GetInt32();

    [Fact]
    public async Task New_role_cannot_get_system_menus_but_builtin_role_can()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, []);

        Assert.Equal(41009, await PutRoleMenusAsync(super, role, [BizWorkbench, Ping]));
        Assert.Equal(0, await PutRoleMenusAsync(super, role, [BizWorkbench]));
        Assert.Equal(0, await PutRoleMenusAsync(super, BuiltinRole, [Ping]));
    }

    [Fact]
    public async Task Roles_expose_builtin_flag()
    {
        using var f = new AdminAppFactory();
        var super = await GrantTestKit.SuperAdminAsync(f);
        var role = await GrantTestKit.CreateRoleAsync(f, []);

        Assert.True((await (await super.GetAsync($"/api/v1/sys/role/{BuiltinRole}")).ReadEnvelope()).GetProperty("data").GetProperty("isBuiltin").GetBoolean());
        Assert.False((await (await super.GetAsync($"/api/v1/sys/role/{role}")).ReadEnvelope()).GetProperty("data").GetProperty("isBuiltin").GetBoolean());
    }

    [Fact]
    public async Task Background_code_without_login_is_not_restricted()
    {
        using var f = new AdminAppFactory();
        var role = await GrantTestKit.CreateRoleAsync(f, [Ping]);   // 种子、启动任务同一约定:无登录上下文视为可信
        using var scope = f.Services.CreateScope();
        Assert.Contains(Ping, await scope.ServiceProvider.GetRequiredService<IRbacService>().GetRoleMenuIdsAsync(role));
    }
}
```

追加到 `SeedUpgradeTests`（这个类在 SqlServer 子集里，清理用到的删除语句顺带在四种库上跑到）：

```csharp
    /// <summary>非内置角色(雪花号段的 Id,模拟界面上新建的角色),直接插库造存量。</summary>
    private const long NewRoleId = 900_000_000_001;

    private static async Task SeedNewRoleWithMenusAsync(ISqlSugarClient db)
    {
        await db.Insertable(new SysRole { Id = NewRoleId, Name = "运维助理", Code = "ops-helper", Enabled = true }).ExecuteCommandAsync();
        await db.Insertable(new List<SysRoleMenu>
        {
            new() { RoleId = NewRoleId, MenuId = 301 },   // 系统模块
            new() { RoleId = NewRoleId, MenuId = 110 },   // 业务中心
            new() { RoleId = 1, MenuId = 301 },           // 内置「系统管理员」
        }).ExecuteCommandAsync();
    }

    private static async Task<long[]> MenusOfRoleAsync(ISqlSugarClient db, long roleId) =>
        [.. (await db.Queryable<SysRoleMenu>().Where(x => x.RoleId == roleId).Select(x => x.MenuId).ToListAsync()).Order()];

    /// <summary>升级那一次:非内置角色上的系统模块菜单被删,业务菜单留着;内置角色不受影响。</summary>
    [Fact]
    public async Task Upgrade_removes_system_menus_from_non_builtin_roles_only()
    {
        await RestartWithAsync(
            async db =>
            {
                await DowngradeVersionAsync(db);
                await SeedNewRoleWithMenusAsync(db);
            },
            async db =>
            {
                Assert.Equal([110L], await MenusOfRoleAsync(db, NewRoleId));
                Assert.Contains(301L, await MenusOfRoleAsync(db, 1));
            });
    }

    /// <summary>平时重启(版本没变)不清理:清理只在从老版本升上来的那一次执行。</summary>
    [Fact]
    public async Task Restart_without_upgrade_keeps_role_menus()
    {
        await RestartWithAsync(
            SeedNewRoleWithMenusAsync,
            async db => Assert.Equal([110L, 301L], await MenusOfRoleAsync(db, NewRoleId)));
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*SystemMenuRoleGrantTests*" --filter-class "*SeedUpgradeTests*"`
Expected: `New_role_cannot_...`、`Roles_expose_builtin_flag`、`Upgrade_removes_...` FAIL；其余通过。

- [ ] **Step 3: 内置角色标记与错误码**

`SysRole.cs` 文件头补 `using SmartAdmin.Core;`，类末尾加：

```csharp
    /// <summary>
    /// 内置角色:内核种子播的固定 Id(1–999)。系统模块的菜单只能授给内置角色,界面上新建的角色(雪花 Id)
    /// 与消费者种子里的角色(Id ≥ 1000)都授不了。只读计算属性,不建列。
    /// </summary>
    [SugarColumn(IsIgnore = true)]
    public bool IsBuiltin => Id is >= 1 and <= SmartSeedIds.KernelMax;
```

`ErrorCode.cs` 在 `DelegatedGrantExpiryInvalid = 41008` 之后：

```csharp
    /// <summary>系统模块的菜单只能授给内置角色(种子里固定 Id 1–999 的角色),新建的角色授不了</summary>
    [MsgKey("error.role.systemMenuNotAssignable")]
    SystemMenuNotAssignable = 41009,
```

`zh-CN.ts` 的 `error.role` 块加 `systemMenuNotAssignable: '系统模块的菜单只能授给内置角色',`；
`en-US.ts` 对应位置加 `systemMenuNotAssignable: 'System module menus can only be granted to built-in roles',`。

- [ ] **Step 4: 接口守卫**

`RbacService.SetRoleMenusAsync` 开头两行换成：

```csharp
        EnsureSuperAdmin();   // 角色菜单授权超管专属
        var role = await roles.GetByIdAsync(roleId);
        AdminException.ThrowIf(role is null, ErrorCode.RoleNotFound);
        await EnsureRoleMenusAssignableAsync(role!, menuIds);
```

在 `SetRoleMenusAsync` 之后加：

```csharp
    /// <summary>
    /// 系统模块的菜单只能授给内置角色(<see cref="SysRole.IsBuiltin"/>):系统模块是管理面,除超管自己外只由内置角色持有。
    /// 菜单所属模块按 ParentId 上溯到根目录取 ModuleId。系统 / 未认证上下文(种子、启动任务)视为可信,不受限,
    /// 与 <see cref="EnsureSuperAdmin"/> 同一约定。读菜单走已有仓储的 <c>Db</c> 逃生舱口,不加构造参数。
    /// </summary>
    protected virtual async Task EnsureRoleMenusAssignableAsync(SysRole role, IReadOnlyCollection<long> menuIds)
    {
        if (role.IsBuiltin || menuIds.Count == 0 || currentUser is not { IsAuthenticated: true }) return;
        var byId = (await roles.Db.Queryable<SysMenu>().ToListAsync()).ToDictionary(m => m.Id);
        AdminException.ThrowIf(
            menuIds.Any(id => MenuTree.RootModuleId(id, byId) == DefaultModuleSeed.BUILTIN_MODULE_ID),
            ErrorCode.SystemMenuNotAssignable);
    }
```

类注释「角色授权面(菜单挂载、数据范围)是超管专属」那段末尾补一句：`系统模块的菜单只能授给内置角色(见 <see cref="EnsureRoleMenusAssignableAsync"/>)。`

- [ ] **Step 5: 升级清理钩子**

`backend/src/SmartAdmin.Services/Rbac/SystemMenuRoleGrantCleanup.cs`：

```csharp
using Microsoft.Extensions.Logging;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 从「新建角色也能持有系统模块菜单」的老版本升级上来时,清一次存量:物理删掉非内置角色上授的系统模块菜单,
/// 每一行写一条 Warning 日志留痕,再失效受影响用户的权限码缓存与门户代际(装了 Redis 时缓存跨重启存活)。
/// <para>只在种子版本从低于 <see cref="SinceSchemaVersion"/> 升上来的那一次执行;空库、平时重启、
/// 关了种子(不走版本闸门)都不执行。删除不可恢复,所以日志要逐条列出删了哪个角色的哪个菜单。</para>
/// </summary>
public class SystemMenuRoleGrantCleanup(
    ISqlSugarClient db,
    ICacheProvider cache,
    ILogger<SystemMenuRoleGrantCleanup> logger) : IDatabaseReadyHook
{
    /// <summary>从这个种子版本起,系统模块的菜单只能授给内置角色。</summary>
    public const int SinceSchemaVersion = 7;

    /// <inheritdoc />
    public virtual async Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        // 空库 PreviousSchemaVersion 为 null;解析不出整数的老版本号按「更早」处理
        if (!context.Upgraded || context.PreviousSchemaVersion is not { } previous) return;
        if (int.TryParse(previous, out var from) && from >= SinceSchemaVersion) return;

        var menus = await db.Queryable<SysMenu>().ToListAsync();
        var byId = menus.ToDictionary(m => m.Id);
        List<long> systemMenuIds = [.. menus.Where(m => MenuTree.RootModuleId(m.Id, byId) == DefaultModuleSeed.BUILTIN_MODULE_ID).Select(m => m.Id)];
        if (systemMenuIds.Count == 0) return;

        var links = await db.Queryable<SysRoleMenu>()
            .Where(x => x.RoleId > SmartSeedIds.KernelMax && systemMenuIds.Contains(x.MenuId))
            .ToListAsync();
        if (links.Count == 0) return;

        List<long> roleIds = [.. links.Select(x => x.RoleId).Distinct()];
        var roleNames = (await db.Queryable<SysRole>().ClearFilter<ISoftDelete>().Where(r => roleIds.Contains(r.Id)).ToListAsync())
            .ToDictionary(r => r.Id, r => $"{r.Name}({r.Code})");
        foreach (var link in links)
            logger.LogWarning("SmartAdmin: 系统模块的菜单只能授给内置角色,升级清理删除角色 {Role} 上的菜单 {Menu}(Id {MenuId})",
                roleNames.GetValueOrDefault(link.RoleId, link.RoleId.ToString()), byId[link.MenuId].Title, link.MenuId);

        List<long> linkIds = [.. links.Select(x => x.Id)];
        await db.Deleteable<SysRoleMenu>().Where(x => linkIds.Contains(x.Id)).ExecuteCommandAsync();

        var users = await db.Queryable<SysUserRole>().Where(x => roleIds.Contains(x.RoleId)).Select(x => x.UserId).ToListAsync();
        await cache.RemoveManyAsync(users.Distinct().Select(CacheKeys.UserPermissions), cancellationToken);
        await cache.IncrementAsync(CacheKeys.PortalGeneration, cancellationToken: cancellationToken);
        logger.LogWarning("SmartAdmin: 升级清理共删除 {Count} 条非内置角色的系统模块菜单授权,涉及 {Roles} 个角色", links.Count, roleIds.Count);
    }
}
```

`ServicesSetup.cs` 在 Task 3 登记的缺表守卫下面加：

```csharp
        // 系统模块菜单只授内置角色:从老版本升级上来的那一次清掉非内置角色上的存量
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDatabaseReadyHook, SystemMenuRoleGrantCleanup>());
```

- [ ] **Step 6: 调整既有用例的搭建方式（断言一条不动）**

下面五处用例经接口让超管给**新建角色**授系统模块菜单，测的却是别的行为（多码按钮展开、角色级联清理、路由清单与授权同源、无页面按钮、机器主体授权即时生效）。
新规则上线后这一步会被 `41009` 拒掉、后面的断言随之失败。它们要锁的行为与「菜单在哪个模块」无关，所以只把**授菜单这一步**换成经服务（可信上下文），断言与其余步骤原样保留：

| 文件:行 | 原来 | 改成 |
|---|---|---|
| `MultiCodeMenuTests.cs:31` | `await admin.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds });` | `await GrantTestKit.SetRoleMenusAsync(f, roleId, menuIds);` |
| `RoleCrudTests.cs:77` | `await c.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds = new[] { 200L } });` | `await GrantTestKit.SetRoleMenusAsync(f, roleId, [200L]);` |
| `PermissionRoutesEndpointTests.cs:75` | `await admin.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds = new[] { menuId!.Value } });` | `await GrantTestKit.SetRoleMenusAsync(f, roleId, [menuId!.Value]);` |
| `MenuCrudTests.cs:56` | `await admin.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds = new[] { buttonId } });` | `await GrantTestKit.SetRoleMenusAsync(f, roleId, [buttonId]);` |
| `ApiKeyAuthTests.cs:147` | `await admin.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds = new[] { buttonId } });` | `await GrantTestKit.SetRoleMenusAsync(f, roleId, [buttonId]);` |
| `ApiKeyAuthTests.cs:153` | `await admin.PutJson("/api/v1/sys/role/menu", new { roleId, menuIds = Array.Empty<long>() });` | `await GrantTestKit.SetRoleMenusAsync(f, roleId, []);` |

（`RoleDelegationTests` 里授的菜单 Id 1 不存在、不属于系统模块，不受影响，不改。执行时若全量测试里还有别的用例因 `41009` 失败，先确认它是不是同一类「搭建用到了被禁的路径」：是就照上表改搭建并在提交说明里列出；不是就停下来查原因。）

- [ ] **Step 7: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*SystemMenuRoleGrantTests*" --filter-class "*SeedUpgradeTests*" --filter-class "*RoleDelegationTests*" --filter-class "*RbacSuperAdminGuardTests*" --filter-class "*ErrorCodeLocaleConsistencyTests*" --filter-class "*MultiCodeMenuTests*" --filter-class "*RoleCrudTests*" --filter-class "*PermissionRoutesEndpointTests*" --filter-class "*MenuCrudTests*" --filter-class "*ApiKeyAuthTests*"`
Expected: 全部通过。

Run: `dotnet test backend/SmartAdmin.slnx`
Expected: 全部通过。

- [ ] **Step 8: 提交**

```bash
git add backend web/packages/admin/src/locales
git commit -m "feat(rbac): 系统模块的菜单只能授给内置角色,升级时清掉新建角色上的存量" -m "从种子版本 6 升级上来的那一次启动,物理删除非内置角色(Id > 999)上授的系统模块菜单,逐条写 Warning 日志。五个既有用例的搭建改为经服务配角色菜单,断言不变。" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: 前端契约与草稿纯函数

**Files:**
- Modify（生成）: `web/packages/admin/src/api/schema.d.ts`
- Modify: `web/packages/admin/src/types/api.ts`
- Modify: `web/packages/admin/src/api/index.ts`（`userApi` 加四个方法，文件头的类型 import 补上新类型）
- Create: `web/packages/admin/src/views/system/user/components/userGrantState.ts`
- Test: `web/packages/admin/src/views/system/user/components/userGrantState.spec.ts`

**Interfaces:**
- Consumes: Task 6 的出参字段（camelCase）、Task 8 的 `isBuiltin`
- Produces:
  - 类型：`enum UserMenuEffect { Allow = 1, Deny = 2 }`、`enum UserMenuGrantStatus { Active = 1, Expiring = 2, Expired = 3 }`、`UserMenuGrantItem`、`UserMenuGrantUpsert`、`UserMenuLeakedCode`、`UserMenuEffectiveNode`、`UserMenuModuleItem`、`UserMenuEffective`、`UserMenuGrantPageItem`；`ModuleRow.isDelegatable?`、`ModuleInput.isDelegatable?`、`SysRole.isBuiltin?`
  - `userApi.getMenuGrants(id)`、`userApi.getEffectiveMenus(id)`、`userApi.setMenuGrants(userId, upserts, removes)`、`userApi.menuGrantPage(params)`
  - `userGrantState.ts`：`type TriState = 'follow' | 'allow' | 'deny'`、`interface DraftEntry { effect; expireDate: string | null; remark: string | null }`、`type Draft = Map<number, DraftEntry>`、`toExpireDate`、`toExpireTime`、`formatDate(d: Date)`、`draftFromGrants`、`triStateOf`、`setTriState(draft, menuId, next, defaultExpireDate)`、`diffDraft(baseline, draft)`、`countDraft(draft)`、`maxExpireDate(maxDays, today)`、`invalidExpiry(baseline, draft, maxDays, today)`、`leakedCodes(tree, effective, menuId)`、`READONLY_REASON_KEYS`

- [ ] **Step 1: 起后端，重新生成契约**

后台起 MinimalHost（`run_in_background`），**指向会话 scratchpad 里的一个临时 SQLite 库**：
本分支把种子版本升到了 7，直接用 MinimalHost 自带的本地开发库启动，会把那个库升级一次，并触发 Task 8 的升级清理（删掉你本地新建角色上的系统菜单）。

```bash
SmartAdmin__Database__ConnectionString="DataSource=<scratchpad>/gen-api.db" dotnet run --project backend/samples/MinimalHost
```

（PowerShell 写法：`$env:SmartAdmin__Database__ConnectionString = 'DataSource=<scratchpad>\gen-api.db'; dotnet run --project backend/samples/MinimalHost`。）
等 `http://localhost:5100/health` 返回 200 再继续（用 Monitor 或一条带超时的检查命令，不要 sleep 轮询）。

Run: `cd web && npm run gen:api`
Expected: `packages/admin/src/api/schema.d.ts` 里出现 `"/api/v1/sys/user/{id}/menus/effective"`、`"/api/v1/sys/user/menu-grants/page"`、`UserMenuEffectiveOutput`、`SysModule` 的 `isDelegatable` 与 `SysRole` 的 `isBuiltin`。完成后停掉 MinimalHost。

- [ ] **Step 2: 写类型**

`types/api.ts`：`ModuleRow` 与 `ModuleInput` 各加 `/** 可转授:普通管理员能否单独授权本应用的菜单(内置 system 恒为 false)。 */ isDelegatable?: boolean | null`；
`SysRole` 加 `/** 内置角色(种子里固定 Id 1–999):只有它们能被授系统模块的菜单。 */ isBuiltin?: boolean`。
在 `ModuleInput` 之后追加：

```ts
/** 用户单独授权的效果(后端 UserMenuEffect)。 */
export enum UserMenuEffect {
  Allow = 1,
  Deny = 2,
}

/** 单独授权的有效期状态(后端 UserMenuGrantStatus;作筛选条件时 Active 含 7 天内到期)。 */
export enum UserMenuGrantStatus {
  Active = 1,
  Expiring = 2,
  Expired = 3,
}

/** 一条单独授权记录(后端 UserMenuGrantItem)。时间是本地时间串 yyyy-MM-ddTHH:mm:ss。 */
export interface UserMenuGrantItem {
  menuId: number
  effect: UserMenuEffect
  expireTime?: string | null
  remark?: string | null
  grantorId?: number | null
  grantorName?: string | null
  grantTime: string
  updaterId?: number | null
  updaterName?: string | null
  updateTime?: string | null
}

/** 变更集里的一条新增或修改(后端 UserMenuGrantUpsert)。 */
export interface UserMenuGrantUpsert {
  menuId: number
  effect: UserMenuEffect
  expireTime?: string | null
  remark?: string | null
}

/** 被拒节点里仍然有效的权限码与携带它的有效节点(后端 UserMenuLeakedCode)。 */
export interface UserMenuLeakedCode {
  code: string
  carrierMenuIds: number[]
}

/** 一个菜单节点对目标用户是否有效、为什么(后端 UserMenuEffectiveNode)。 */
export interface UserMenuEffectiveNode {
  menuId: number
  moduleId?: number | null
  effective: boolean
  roles: string[]
  grant?: UserMenuEffect | null
  expireTime?: string | null
  expired: boolean
  deniedByAncestor: boolean
  grantable: boolean
  leakedCodes: UserMenuLeakedCode[]
}

/** 授权弹窗的模块清单项(后端 UserMenuModuleItem)。 */
export interface UserMenuModuleItem {
  id: number
  title: string
  delegatable: boolean
}

/** 授权弹窗一次取齐的数据(后端 UserMenuEffectiveOutput)。readOnlyReason 是错误码。 */
export interface UserMenuEffective {
  userId: number
  hasRoles: boolean
  targetEditable: boolean
  readOnlyReason?: number | null
  delegatedMaxDays?: number | null
  modules: UserMenuModuleItem[]
  nodes: UserMenuEffectiveNode[]
}

/** 单独授权一览的一行(后端 UserMenuGrantPageItem)。 */
export interface UserMenuGrantPageItem {
  id: number
  userId: number
  userAccount: string
  userName: string
  menuId: number
  menuTitle: string
  moduleId?: number | null
  moduleTitle?: string | null
  effect: UserMenuEffect
  expireTime?: string | null
  status: UserMenuGrantStatus
  grantorId?: number | null
  grantorName?: string | null
  grantTime: string
  remark?: string | null
}
```

- [ ] **Step 3: 写 API**

`api/index.ts` 的 `userApi` 在 `setEnabled` 之后加（文件头 `from '#/types/api'` 的 import 里补 `UserMenuEffect`、`UserMenuGrantStatus`、`UserMenuGrantItem`、`UserMenuGrantUpsert`、`UserMenuEffective`、`UserMenuGrantPageItem`）：

```ts
  // ── 单独授权 ──

  /** 某用户的单独授权记录(授权菜单弹窗回显)。 */
  getMenuGrants: (id: number) =>
    client
      .GET('/api/v1/sys/user/{id}/menus', { params: { path: { id } } })
      .then(r => unwrap<UserMenuGrantItem[]>(r)),
  /** 某用户的有效权限与来源:能否授、能否编辑及原因、委派最长天数、模块清单,一次取齐。 */
  getEffectiveMenus: (id: number) =>
    client
      .GET('/api/v1/sys/user/{id}/menus/effective', { params: { path: { id } } })
      .then(r => unwrap<UserMenuEffective>(r)),
  /** 按变更集保存单独授权:没提到的记录原样保留。 */
  setMenuGrants: (userId: number, upserts: UserMenuGrantUpsert[], removes: number[]) =>
    client
      .PUT('/api/v1/sys/user/menu', { body: { userId, upserts, removes } })
      .then(r => unwrap<boolean>(r)),
  /** 单独授权一览:分页 + 筛选(用户 / 授权人模糊,效果、状态精确)。 */
  menuGrantPage: (params: {
    page: number
    pageSize: number
    user?: string
    grantor?: string
    effect?: UserMenuEffect
    status?: UserMenuGrantStatus
  }) =>
    client
      .GET('/api/v1/sys/user/menu-grants/page', {
        params: {
          query: {
            ...pageParams(params),
            User: params.user,
            Grantor: params.grantor,
            Effect: params.effect,
            Status: params.status,
          },
        },
      })
      .then(r => toPage<UserMenuGrantPageItem>(r)),
```

- [ ] **Step 4: 写失败的纯函数单测**

`views/system/user/components/userGrantState.spec.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { MenuType, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect } from '#/types/api'
import {
  countDraft,
  diffDraft,
  draftFromGrants,
  invalidExpiry,
  leakedCodes,
  maxExpireDate,
  setTriState,
  triStateOf,
  type Draft,
} from './userGrantState'

const TODAY = new Date(2026, 9, 9, 10, 0, 0) // 2026-10-09

const node = (id: number, type: MenuType, permission = '', children: MenuTreeNode[] = []): MenuTreeNode => ({
  id,
  parentId: 0,
  type,
  title: `n${id}`,
  permission,
  sort: 0,
  enabled: true,
  visible: true,
  children,
})

describe('草稿三态', () => {
  it('从授权记录建草稿;没有记录就是跟随角色', () => {
    const draft = draftFromGrants([
      { menuId: 1, effect: UserMenuEffect.Allow, expireTime: '2026-10-20T23:59:59', remark: 'x', grantTime: '2026-10-09T10:00:00' },
    ])
    expect(triStateOf(draft, 1)).toBe('allow')
    expect(draft.get(1)?.expireDate).toBe('2026-10-20')
    expect(triStateOf(draft, 2)).toBe('follow')
  })

  it('进入允许带默认到期日;切到拒绝保留已填的;回到跟随即删除', () => {
    const draft: Draft = new Map()
    setTriState(draft, 5, 'allow', '2027-01-07')
    expect(draft.get(5)).toEqual({ effect: UserMenuEffect.Allow, expireDate: '2027-01-07', remark: null })
    setTriState(draft, 5, 'deny', '2027-01-07')
    expect(draft.get(5)?.effect).toBe(UserMenuEffect.Deny)
    expect(draft.get(5)?.expireDate).toBe('2027-01-07')
    setTriState(draft, 5, 'follow', null)
    expect(draft.has(5)).toBe(false)
  })

  it('新进拒绝不带到期日(拒绝不要求限时)', () => {
    const draft: Draft = new Map()
    setTriState(draft, 6, 'deny', '2027-01-07')
    expect(draft.get(6)?.expireDate).toBeNull()
  })

  it('计数允许与拒绝', () => {
    const draft: Draft = new Map()
    setTriState(draft, 1, 'allow', null)
    setTriState(draft, 2, 'deny', null)
    setTriState(draft, 3, 'deny', null)
    expect(countDraft(draft)).toEqual({ allow: 1, deny: 2 })
  })
})

describe('变更集', () => {
  const baseline = (): Draft =>
    draftFromGrants([
      { menuId: 1, effect: UserMenuEffect.Allow, expireTime: null, remark: 'a', grantTime: 't' },
      { menuId: 2, effect: UserMenuEffect.Deny, expireTime: null, remark: null, grantTime: 't' },
    ])

  it('没改动就是空变更集', () => {
    expect(diffDraft(baseline(), baseline())).toEqual({ upserts: [], removes: [] })
  })

  it('新增、修改进 upserts,删掉的进 removes;到期日转成当天最后一秒', () => {
    const draft = baseline()
    draft.get(1)!.remark = 'b'
    draft.delete(2)
    setTriState(draft, 3, 'allow', '2026-10-20')
    expect(diffDraft(baseline(), draft)).toEqual({
      upserts: [
        { menuId: 1, effect: UserMenuEffect.Allow, expireTime: null, remark: 'b' },
        { menuId: 3, effect: UserMenuEffect.Allow, expireTime: '2026-10-20T23:59:59', remark: null },
      ],
      removes: [2],
    })
  })

  it('备注首尾空白不算改动', () => {
    const draft = baseline()
    draft.get(1)!.remark = ' a '
    expect(diffDraft(baseline(), draft).upserts).toEqual([])
  })
})

describe('到期日', () => {
  it('上限 = 今天 + 最长天数;不限时为 null', () => {
    expect(maxExpireDate(90, TODAY)).toBe('2027-01-07')
    expect(maxExpireDate(null, TODAY)).toBeNull()
  })

  it('受限时允许必须有到期日且不晚于上限;拒绝不要求;任何人都不能选过去的日期', () => {
    const draft: Draft = new Map()
    setTriState(draft, 1, 'allow', null) // 缺到期日
    setTriState(draft, 2, 'allow', '2027-01-08') // 超上限一天
    setTriState(draft, 3, 'allow', '2027-01-07') // 正好上限
    setTriState(draft, 4, 'deny', null) // 拒绝不要求
    draft.set(5, { effect: UserMenuEffect.Deny, expireDate: '2026-10-08', remark: null }) // 过去
    expect(invalidExpiry(new Map(), draft, 90, TODAY)).toEqual([1, 2, 5])
    expect(invalidExpiry(new Map(), draft, null, TODAY)).toEqual([5])
  })

  it('没改动的老记录不校验', () => {
    const old = draftFromGrants([{ menuId: 9, effect: UserMenuEffect.Allow, expireTime: null, remark: null, grantTime: 't' }])
    expect(invalidExpiry(old, draftFromGrants([{ menuId: 9, effect: UserMenuEffect.Allow, expireTime: null, remark: null, grantTime: 't' }]), 90, TODAY)).toEqual([])
  })
})

describe('漏网接口', () => {
  // 10 页面 → 11 按钮(a;shared);20 页面 → 21 按钮(shared)
  const tree = [
    node(10, MenuType.Menu, '', [node(11, MenuType.Button, 'GET:/a;GET:/shared')]),
    node(20, MenuType.Menu, '', [node(21, MenuType.Button, 'GET:/shared')]),
  ]

  it('被拒子树里的码还由子树外的有效节点携带时列出来', () => {
    expect(leakedCodes(tree, new Set([11, 21]), 10)).toEqual([{ code: 'GET:/shared', carriers: [21] }])
  })

  it('携带节点不有效,或只在子树里,就没有漏网', () => {
    expect(leakedCodes(tree, new Set([11]), 10)).toEqual([])
    expect(leakedCodes(tree, new Set([21]), 999)).toEqual([])
  })
})
```

Run: `cd web/packages/admin && npx vitest run src/views/system/user/components/userGrantState.spec.ts`
Expected: FAIL，`./userGrantState` 不存在。

- [ ] **Step 5: 写纯函数**

`views/system/user/components/userGrantState.ts`：

```ts
// 用户授权弹窗的草稿状态(纯函数,组件只管渲染)。草稿 = 菜单 Id → 单独授权记录;没有记录就是「跟随角色」。
// 保存只提交变更集:与打开时的快照比,算出新增 / 修改(upserts)与移除(removes),没动的记录不提交。
// 到期按日期:前端只选日期,提交当天 23:59:59;普通管理员授允许的上限是今天 + 最长天数(后端同一口径)。
import { splitPermission, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuGrantItem, type UserMenuGrantUpsert } from '#/types/api'

export type TriState = 'follow' | 'allow' | 'deny'

export interface DraftEntry {
  effect: UserMenuEffect
  /** 到期日 yyyy-MM-dd,当天最后一秒失效;null = 长期 */
  expireDate: string | null
  remark: string | null
}
export type Draft = Map<number, DraftEntry>

/** 弹窗只读的原因码 → 文案 key(直接复用后端错误码的 msgKey 文案)。 */
export const READONLY_REASON_KEYS: Record<number, string> = {
  42029: 'error.user.cannotOperateSelf',
  42007: 'error.user.superAdminProtected',
  41005: 'error.user.outOfDataScope',
  41007: 'error.perm.targetIsDelegatedAdmin',
}

const pad = (n: number) => String(n).padStart(2, '0')

/** 本地日期 yyyy-MM-dd。 */
export const formatDate = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`

/** 后端到期时间(本地时间串)→ 日期部分。 */
export const toExpireDate = (expireTime?: string | null) => (expireTime ? expireTime.slice(0, 10) : null)

/** 日期 → 后端到期时间:当天最后一秒失效。 */
export const toExpireTime = (date: string | null) => (date ? `${date}T23:59:59` : null)

export function draftFromGrants(grants: UserMenuGrantItem[]): Draft {
  return new Map(
    grants.map(g => [g.menuId, { effect: g.effect, expireDate: toExpireDate(g.expireTime), remark: g.remark ?? null }]),
  )
}

export function triStateOf(draft: Draft, menuId: number): TriState {
  const entry = draft.get(menuId)
  if (!entry) return 'follow'
  return entry.effect === UserMenuEffect.Allow ? 'allow' : 'deny'
}

/**
 * 切换三态。从「跟随」进入允许时带上默认到期日(普通管理员授允许必须限时,默认给到上限);
 * 进入拒绝不带(拒绝只会收紧,不要求限时);已有记录切换效果时保留已填的到期日与备注。
 */
export function setTriState(draft: Draft, menuId: number, next: TriState, defaultExpireDate: string | null): void {
  if (next === 'follow') {
    draft.delete(menuId)
    return
  }
  const effect = next === 'allow' ? UserMenuEffect.Allow : UserMenuEffect.Deny
  const cur = draft.get(menuId)
  const expireDate = cur?.expireDate ?? (effect === UserMenuEffect.Allow ? defaultExpireDate : null)
  draft.set(menuId, { effect, expireDate, remark: cur?.remark ?? null })
}

const normRemark = (remark: string | null) => remark?.trim() || null

const sameEntry = (a: DraftEntry, b: DraftEntry) =>
  a.effect === b.effect && a.expireDate === b.expireDate && normRemark(a.remark) === normRemark(b.remark)

/** 与打开时的快照比出变更集。按菜单 Id 升序,提交内容稳定。 */
export function diffDraft(baseline: Draft, draft: Draft): { upserts: UserMenuGrantUpsert[]; removes: number[] } {
  const upserts: UserMenuGrantUpsert[] = []
  for (const [menuId, entry] of draft) {
    const before = baseline.get(menuId)
    if (before && sameEntry(before, entry)) continue
    upserts.push({ menuId, effect: entry.effect, expireTime: toExpireTime(entry.expireDate), remark: normRemark(entry.remark) })
  }
  const removes = [...baseline.keys()].filter(id => !draft.has(id))
  return { upserts: upserts.sort((a, b) => a.menuId - b.menuId), removes: removes.sort((a, b) => a - b) }
}

export function countDraft(draft: Draft): { allow: number; deny: number } {
  let allow = 0
  let deny = 0
  for (const entry of draft.values()) {
    if (entry.effect === UserMenuEffect.Allow) allow++
    else deny++
  }
  return { allow, deny }
}

/** 普通管理员授允许的到期日上限 = 今天 + 最长天数(也是默认值);不限时为 null。 */
export function maxExpireDate(maxDays: number | null | undefined, today: Date): string | null {
  if (maxDays == null) return null
  return formatDate(new Date(today.getFullYear(), today.getMonth(), today.getDate() + maxDays))
}

/**
 * 要提交的记录里到期日不合规的菜单 Id:谁都不能选今天以前的日期;受限(maxDays 非空)时,
 * 「允许」还必须有到期日且不晚于上限。只看变更集,没动的老记录不校验(后端同样只校验变更集)。
 */
export function invalidExpiry(baseline: Draft, draft: Draft, maxDays: number | null | undefined, today: Date): number[] {
  const todayStr = formatDate(today)
  const max = maxExpireDate(maxDays, today)
  return diffDraft(baseline, draft)
    .upserts.filter(u => {
      const date = toExpireDate(u.expireTime)
      if (date && date < todayStr) return true
      if (u.effect !== UserMenuEffect.Allow || max == null) return false
      return !date || date > max
    })
    .map(u => u.menuId)
}

/**
 * 拒掉 menuId(连同子孙)后,仍由子树外的有效节点携带的权限码 → 携带它们的节点 Id。
 * 规则同后端 UserMenuGrantRules.LeakedCodes;这里给还没保存的「拒绝」就地提示用,effective 取打开时的有效节点。
 */
export function leakedCodes(tree: MenuTreeNode[], effective: Set<number>, menuId: number): { code: string; carriers: number[] }[] {
  const byId = new Map<number, MenuTreeNode>()
  const index = (nodes: MenuTreeNode[]) => {
    for (const n of nodes) {
      byId.set(n.id, n)
      index(n.children)
    }
  }
  index(tree)
  const root = byId.get(menuId)
  if (!root) return []

  const subtree = new Set<number>()
  const collect = (n: MenuTreeNode) => {
    subtree.add(n.id)
    n.children.forEach(collect)
  }
  collect(root)
  const denied = new Set([...subtree].flatMap(id => splitPermission(byId.get(id)!.permission)))
  if (!denied.size) return []

  const carriers = new Map<string, number[]>()
  for (const id of effective) {
    if (subtree.has(id)) continue
    const n = byId.get(id)
    if (!n) continue
    for (const code of splitPermission(n.permission)) if (denied.has(code)) carriers.set(code, [...(carriers.get(code) ?? []), id])
  }
  return [...carriers.keys()].sort().map(code => ({ code, carriers: carriers.get(code)!.sort((a, b) => a - b) }))
}
```

- [ ] **Step 6: 跑单测、类型检查、lint**

Run: `cd web/packages/admin && npx vitest run src/views/system/user/components/userGrantState.spec.ts`，再 `cd web && npm run typecheck && npm run lint && npm run format:check`
Expected: 单测全过，typecheck / lint / format 无错。`format:check` 报格式问题就 `npm run format` 后再查（只改格式）。
`api/index.ts` 若因 schema 生成的类型与手写类型不一致报错，以生成的 schema 为准修手写类型，不要手改 `schema.d.ts`。

- [ ] **Step 7: 提交**

```bash
git add web/packages/admin/src
git commit -m "feat(web): 用户单独授权的接口类型与草稿状态纯函数" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: 用户页「授权菜单」弹窗

**Files:**
- Create: `web/packages/admin/src/views/system/user/components/UserGrantNodeRow.vue`
- Create: `web/packages/admin/src/views/system/user/components/UserGrantMenuTable.vue`
- Create: `web/packages/admin/src/views/system/user/components/UserGrantEffectiveTable.vue`
- Create: `web/packages/admin/src/views/system/user/components/UserGrantMenuSheet.vue`
- Modify: `web/packages/admin/src/views/system/user/index.vue`
- Modify: `web/packages/admin/src/locales/zh-CN.ts`、`en-US.ts`（`userGrant` 命名空间）
- Modify: `web/packages/admin/src/views/listSearch.spec.ts`（`EMBEDDED` 登记有效权限表）
- Modify（生成）: `web/packages/admin/src/assets/icons/ph-subset.json`

**Interfaces:**
- Consumes: Task 9 的类型、`userApi.*`、`userGrantState.*`；`buildGroups`（`views/system/role/components/grantMenuGroups.ts`，不改它）；`menuApi.tree()`
- Produces: `UserGrantMenuSheet` 暴露 `open(user: { id: number; name: string })`，保存成功 `emit('saved')`

- [ ] **Step 1: 文案**

`zh-CN.ts` 在 `user: {...}` 块之后加：

```ts
  userGrant: {
    action: '授权菜单',
    title: '授权菜单 · {name}',
    tabGrant: '单独授权',
    tabEffective: '有效权限',
    hint: '常规授权请走角色,这里只放例外。允许只作用于这一个节点;拒绝连同下级一起收回,且优先于任何允许。',
    noRoles: '该用户没有任何角色,数据范围为「仅本人」:能打开授权的页面,但只看得到自己创建的数据。',
    readonly: '只能查看,不能修改:{reason}',
    follow: '跟随角色',
    allow: '允许',
    deny: '拒绝',
    effective: '有效',
    ineffective: '无效',
    expired: '已过期',
    deniedByAncestor: '被上级拒绝',
    notGrantable: '该模块只允许超管授权',
    expireLongTerm: '长期',
    remark: '授权理由(选填)',
    leaked: '以下接口仍可调用:',
    leakedFrom: '{code}(来自 {from})',
    summary: '允许 {allow} · 拒绝 {deny}',
    unsaved: '有未保存的修改',
    invalidExpiry: '有 {count} 条记录的到期日缺失或超出可选范围',
    saved: '授权已保存',
    overview: '单独授权一览',
    colUser: '用户',
    colMenu: '菜单',
    colState: '状态',
    colSource: '来源',
    colEffect: '效果',
    colStatus: '有效期',
    colExpire: '到期',
    colGrantor: '授权人',
    colGrantTime: '授权时间',
    colRemark: '授权理由',
    adjust: '去调整',
    statusActive: '生效中',
    statusExpiring: '7 天内到期',
    statusExpired: '已过期',
  },
```

`en-US.ts` 同位置：

```ts
  userGrant: {
    action: 'Grant menus',
    title: 'Grant menus · {name}',
    tabGrant: 'Individual grants',
    tabEffective: 'Effective access',
    hint: 'Grant through roles as usual and keep only exceptions here. Allow covers that one node; Deny also revokes everything below it and beats any allow.',
    noRoles: 'This user has no role, so the data scope is "self only": granted pages open but show only data the user created.',
    readonly: 'View only: {reason}',
    follow: 'Follow roles',
    allow: 'Allow',
    deny: 'Deny',
    effective: 'Effective',
    ineffective: 'Not effective',
    expired: 'Expired',
    deniedByAncestor: 'Denied by parent',
    notGrantable: 'Only a super admin can grant menus in this module',
    expireLongTerm: 'No expiry',
    remark: 'Reason (optional)',
    leaked: 'These APIs stay callable:',
    leakedFrom: '{code} (from {from})',
    summary: 'Allow {allow} · Deny {deny}',
    unsaved: 'Unsaved changes',
    invalidExpiry: '{count} record(s) have a missing or out-of-range expiry date',
    saved: 'Grants saved',
    overview: 'Individual grants',
    colUser: 'User',
    colMenu: 'Menu',
    colState: 'State',
    colSource: 'Source',
    colEffect: 'Effect',
    colStatus: 'Validity',
    colExpire: 'Expires',
    colGrantor: 'Granted by',
    colGrantTime: 'Granted at',
    colRemark: 'Reason',
    adjust: 'Adjust',
    statusActive: 'Active',
    statusExpiring: 'Expiring in 7 days',
    statusExpired: 'Expired',
  },
```

- [ ] **Step 2: 节点行 `UserGrantNodeRow.vue`**

```vue
<script setup lang="ts">
// 用户授权弹窗里一个节点一行:名称 / 来源与是否有效 / 三态;选了允许或拒绝再展开到期日与理由。
// 选「拒绝」时,这个节点的接口若还由别的有效节点携带,就地列出来:按节点拒绝不收回共用的接口。
import { computed } from 'vue'
import { NDatePicker, NInput, NRadioButton, NRadioGroup, NTag, NTooltip } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { UserMenuEffect, type UserMenuEffectiveNode } from '#/types/api'
import { formatDate, type DraftEntry, type TriState } from './userGrantState'

const props = defineProps<{
  title: string
  path?: string
  level: 'catalog' | 'page' | 'button'
  node?: UserMenuEffectiveNode
  state: TriState
  entry?: DraftEntry
  /** 目标用户可编辑且这个节点所属模块可授 */
  editable: boolean
  /** 只因模块不可转授而不能改(悬浮说明) */
  notGrantable: boolean
  /** 到期日上限 yyyy-MM-dd;null = 不限 */
  maxDate: string | null
  leaked: { code: string; from: string }[]
}>()

const emit = defineEmits<{
  (e: 'update:state', v: TriState): void
  (e: 'update:expireDate', v: string | null): void
  (e: 'update:remark', v: string): void
}>()

const { t } = useI18n()
/** 角色标签最多露两个,其余折成 +N。 */
const roles = computed(() => props.node?.roles ?? [])
const shownRoles = computed(() => roles.value.slice(0, 2))
const today = formatDate(new Date())
/** 今天以前、上限以后的日期不可选。 */
const isDateDisabled = (ts: number) => {
  const d = formatDate(new Date(ts))
  return d < today || (props.maxDate != null && d > props.maxDate)
}
/** 受限时「允许」的到期日必填(不给清空);拒绝与不受限时可留空 = 长期。 */
const clearable = computed(() => props.maxDate == null || props.entry?.effect === UserMenuEffect.Deny)
</script>

<template>
  <div class="ugr" :class="[`is-${level}`, { 'is-off': !node?.effective }]">
    <div class="ugr-main">
      <div class="ugr-name">
        <span class="ugr-title">{{ title }}</span>
        <span v-if="path" class="ugr-path">{{ path }}</span>
      </div>
      <div class="ugr-src">
        <n-tag v-for="r in shownRoles" :key="r" size="small" :bordered="false">{{ r }}</n-tag>
        <span v-if="roles.length > shownRoles.length" class="faint">+{{ roles.length - shownRoles.length }}</span>
        <n-tag v-if="node?.expired" size="small" type="warning" :bordered="false">{{ t('userGrant.expired') }}</n-tag>
        <n-tag v-if="node?.deniedByAncestor" size="small" type="error" :bordered="false">
          {{ t('userGrant.deniedByAncestor') }}
        </n-tag>
        <span class="ugr-dot" :class="{ on: node?.effective }">
          {{ node?.effective ? t('userGrant.effective') : t('userGrant.ineffective') }}
        </span>
      </div>
      <n-tooltip :disabled="!notGrantable">
        <template #trigger>
          <!-- 禁用的单选组收不到鼠标事件,包一层让悬浮说明能出来 -->
          <span class="ugr-tri">
            <n-radio-group
              :value="state"
              size="small"
              :disabled="!editable"
              @update:value="(v: TriState) => emit('update:state', v)"
            >
              <n-radio-button value="follow">{{ t('userGrant.follow') }}</n-radio-button>
              <n-radio-button value="allow">{{ t('userGrant.allow') }}</n-radio-button>
              <n-radio-button value="deny">{{ t('userGrant.deny') }}</n-radio-button>
            </n-radio-group>
          </span>
        </template>
        {{ t('userGrant.notGrantable') }}
      </n-tooltip>
    </div>

    <div v-if="entry" class="ugr-edit">
      <n-date-picker
        type="date"
        size="small"
        value-format="yyyy-MM-dd"
        :formatted-value="entry.expireDate"
        :clearable="clearable"
        :is-date-disabled="isDateDisabled"
        :disabled="!editable"
        :placeholder="t('userGrant.expireLongTerm')"
        @update:formatted-value="(v: string | null) => emit('update:expireDate', v)"
      />
      <n-input
        size="small"
        :value="entry.remark ?? ''"
        :maxlength="200"
        :disabled="!editable"
        :placeholder="t('userGrant.remark')"
        @update:value="(v: string) => emit('update:remark', v)"
      />
    </div>

    <div v-if="state === 'deny' && leaked.length" class="ugr-leak">
      <AppIcon icon="ph:warning" :size="14" />
      <span>{{ t('userGrant.leaked') }}</span>
      <code v-for="l in leaked" :key="l.code">{{ t('userGrant.leakedFrom', { code: l.code, from: l.from }) }}</code>
    </div>
  </div>
</template>

<style scoped>
.ugr {
  padding: 8px 16px;
  border-top: 1px solid var(--hairline);
}
.ugr.is-button {
  padding-left: 40px;
}
.ugr.is-catalog {
  flex: 1;
  min-width: 0;
  padding: 0;
  border-top: 0;
}
.ugr-main {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto;
  gap: 6px 16px;
  align-items: center;
}
.ugr-name {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.ugr-title {
  overflow-wrap: anywhere;
}
.ugr.is-catalog .ugr-title {
  font-size: 15px;
  font-weight: 600;
}
.ugr.is-off .ugr-title {
  color: var(--text-2);
}
.ugr-path {
  font-family: var(--font-mono);
  font-size: 12px;
  color: var(--text-3);
}
.ugr-src {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  font-size: 13px;
}
.ugr-dot {
  color: var(--text-3);
}
.ugr-dot.on {
  color: var(--sel-fg);
}
.ugr-tri {
  display: inline-flex;
}
.ugr-edit {
  display: grid;
  grid-template-columns: 170px minmax(0, 1fr);
  gap: 8px;
  margin-top: 8px;
}
.ugr-leak {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 8px;
  align-items: center;
  margin-top: 6px;
  font-size: 12px;
  color: var(--warn);
}
.ugr-leak code {
  font-family: var(--font-mono);
}
@media (max-width: 640px) {
  .ugr-main,
  .ugr-edit {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
```

- [ ] **Step 3: 节点列表 `UserGrantMenuTable.vue`**

```vue
<script setup lang="ts">
// 用户授权弹窗的节点列表:应用切换 + 搜索 + 目录卡片(目录 → 页面 → 按钮,每个节点一行三态)。
// 分组复用角色页 grantMenuGroups.buildGroups 的结构(不用它的勾选态),角色页的 GrantMenuTable 不动。
// 草稿在父组件(reactive Map),这里只经 userGrantState 的纯函数改它。嵌套目录与角色页一样展平成带前缀的行。
import { computed, reactive, ref, watch } from 'vue'
import { NButton, NInput, NSelect } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { translateMenuTitle } from '#/locales/menuTitle'
import type { MenuTreeNode } from '#/types/menu'
import type { UserMenuEffective } from '#/types/api'
import { buildGroups, type CatalogGroup, type MenuRow } from '../../role/components/grantMenuGroups'
import UserGrantNodeRow from './UserGrantNodeRow.vue'
import { leakedCodes, setTriState, triStateOf, type Draft, type TriState } from './userGrantState'

const UNASSIGNED = 0

const props = defineProps<{
  tree: MenuTreeNode[]
  effective: UserMenuEffective
  draft: Draft
  defaultModuleId: number
  maxDate: string | null
}>()

const { t } = useI18n()
const search = ref('')
const query = computed(() => search.value.trim().toLowerCase())
const moduleId = ref(props.defaultModuleId)
watch(
  () => props.defaultModuleId,
  v => (moduleId.value = v),
)

const groups = computed(() => buildGroups(props.tree, new Set(), t('role.apiOnlyRow')))
const nodeMap = computed(() => new Map(props.effective.nodes.map(n => [n.menuId, n])))
const effectiveIds = computed(() => new Set(props.effective.nodes.filter(n => n.effective).map(n => n.menuId)))
const titles = computed(() => {
  const map = new Map<number, string>()
  const walk = (nodes: MenuTreeNode[]) => {
    for (const n of nodes) {
      map.set(n.id, translateMenuTitle(n.title, n.path || undefined))
      walk(n.children)
    }
  }
  walk(props.tree)
  return map
})

// ── 应用切换:同角色页,按顶级节点的 moduleId 分 ──
const moduleOfTop = computed(() => new Map(props.tree.map(n => [n.id, n.moduleId ?? UNASSIGNED])))
const moduleOf = (g: CatalogGroup) => moduleOfTop.value.get(g.id) ?? UNASSIGNED
const moduleOptions = computed(() => {
  const options = props.effective.modules.map(m => ({ label: translateMenuTitle(m.title), value: m.id }))
  if (groups.value.some(g => moduleOf(g) === UNASSIGNED))
    options.push({ label: t('menu.moduleUnassigned'), value: UNASSIGNED })
  return options
})

// ── 搜索:目录名命中 → 整组;否则只留页面名 / 按钮名命中的行 ──
const matchesRow = (m: MenuRow, q: string) =>
  m.title.toLowerCase().includes(q) || m.buttons.some(b => b.title.toLowerCase().includes(q))
const views = computed(() => {
  const q = query.value
  const out: { group: CatalogGroup; menus: MenuRow[] }[] = []
  for (const g of groups.value) {
    if (moduleOf(g) !== moduleId.value) continue
    if (!q || g.title.toLowerCase().includes(q)) {
      out.push({ group: g, menus: g.menus })
      continue
    }
    const menus = g.menus.filter(m => matchesRow(m, q))
    if (menus.length) out.push({ group: g, menus })
  }
  return out
})

// ── 折叠(搜索时一律展开,免得命中项藏在折叠里) ──
const collapsed = reactive(new Set<number>())
const isOpen = (id: number) => !!query.value || !collapsed.has(id)
function toggle(id: number) {
  if (collapsed.has(id)) collapsed.delete(id)
  else collapsed.add(id)
}

// ── 行属性与改动 ──
function rowProps(id: number, title: string, path?: string) {
  const node = nodeMap.value.get(id)
  const grantable = node?.grantable ?? false
  const state = triStateOf(props.draft, id)
  return {
    title,
    path,
    node,
    state,
    entry: props.draft.get(id),
    editable: props.effective.targetEditable && grantable,
    notGrantable: props.effective.targetEditable && !grantable,
    maxDate: props.maxDate,
    leaked:
      state === 'deny'
        ? leakedCodes(props.tree, effectiveIds.value, id).map(l => ({
            code: l.code,
            from: l.carriers.map(c => titles.value.get(c) ?? String(c)).join('、'),
          }))
        : [],
  }
}
const onState = (id: number, next: TriState) => setTriState(props.draft, id, next, props.maxDate)
function onExpire(id: number, v: string | null) {
  const entry = props.draft.get(id)
  if (entry) entry.expireDate = v
}
function onRemark(id: number, v: string) {
  const entry = props.draft.get(id)
  if (entry) entry.remark = v
}
</script>

<template>
  <div class="ut">
    <div class="ut-toolbar">
      <n-select v-model:value="moduleId" class="ut-module" :options="moduleOptions" :aria-label="t('menu.module')" />
      <n-input
        v-model:value="search"
        clearable
        :placeholder="t('role.grantSearch')"
        :input-props="{ 'aria-label': t('role.grantSearch'), autocomplete: 'off' }"
        style="flex: 1; min-width: 0"
      >
        <template #prefix><AppIcon class="faint" icon="ph:magnifying-glass" :size="15" /></template>
      </n-input>
    </div>

    <div class="ut-scroll">
      <section v-for="v in views" :key="v.group.id" class="ut-group">
        <header v-if="!v.group.standalone" class="ut-head">
          <n-button
            text
            class="ut-fold"
            :aria-expanded="isOpen(v.group.id)"
            :aria-label="v.group.title"
            @click="toggle(v.group.id)"
          >
            <AppIcon :icon="isOpen(v.group.id) ? 'ph:caret-down' : 'ph:caret-right'" :size="14" />
          </n-button>
          <span v-if="v.group.synthetic" class="ut-head-title">{{ v.group.title }}</span>
          <UserGrantNodeRow
            v-else
            level="catalog"
            v-bind="rowProps(v.group.id, v.group.title)"
            @update:state="s => onState(v.group.id, s)"
            @update:expire-date="d => onExpire(v.group.id, d)"
            @update:remark="r => onRemark(v.group.id, r)"
          />
        </header>
        <div v-show="isOpen(v.group.id)">
          <template v-for="m in v.menus" :key="m.anchor ? `anchor-${m.id}` : m.id">
            <div v-if="m.anchor" class="ut-anchor">
              <span class="ut-tag">{{ m.title }}</span>
            </div>
            <UserGrantNodeRow
              v-else
              level="page"
              v-bind="rowProps(m.id, m.title, m.path)"
              @update:state="s => onState(m.id, s)"
              @update:expire-date="d => onExpire(m.id, d)"
              @update:remark="r => onRemark(m.id, r)"
            />
            <UserGrantNodeRow
              v-for="b in m.buttons"
              :key="b.id"
              level="button"
              v-bind="rowProps(b.id, b.title)"
              @update:state="s => onState(b.id, s)"
              @update:expire-date="d => onExpire(b.id, d)"
              @update:remark="r => onRemark(b.id, r)"
            />
          </template>
        </div>
      </section>

      <div v-if="!views.length" class="ut-empty">
        {{ query ? t('role.grantNoMatch') : t('role.grantNoMenus') }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.ut {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.ut-toolbar {
  flex: none;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  padding: 10px 20px 12px;
}
.ut-module {
  flex: none;
  width: 180px;
}
.ut-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 4px 20px 20px;
  scrollbar-width: thin;
}
.ut-group {
  border: 1px solid var(--hairline);
  border-radius: 14px;
  margin-bottom: 12px;
  overflow: hidden;
}
.ut-head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 46px;
  padding: 6px 16px;
}
.ut-head-title {
  font-size: 15px;
  font-weight: 600;
}
.ut-fold {
  flex: none;
}
.ut-anchor {
  padding: 8px 16px;
  border-top: 1px solid var(--hairline);
}
.ut-tag {
  display: inline-block;
  padding: 1px 8px;
  border-radius: 6px;
  background: var(--fill);
  color: var(--text-2);
  font-size: 13px;
  font-weight: 500;
}
.ut-empty {
  padding: 64px 24px;
  text-align: center;
  color: var(--text-2);
}
@media (max-width: 640px) {
  .ut-module {
    width: 100%;
  }
}
</style>
```

- [ ] **Step 4: 有效权限表 `UserGrantEffectiveTable.vue`**

```vue
<script setup lang="ts">
// 「有效权限」页签:只读列出有效的节点和有单独授权记录的节点,说清来源;单独授权带上授权人、时间、到期、理由。
// 嵌在弹窗里的小表,不套整页列表标准(登记在 listSearch.spec.ts 的 EMBEDDED)。
import { computed, h } from 'vue'
import { NTag } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import { translateMenuTitle } from '#/locales/menuTitle'
import type { MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuEffective, type UserMenuGrantItem } from '#/types/api'

const props = defineProps<{ tree: MenuTreeNode[]; effective: UserMenuEffective; grants: UserMenuGrantItem[] }>()
const { t } = useI18n()

interface Row {
  menuId: number
  path: string
  effective: boolean
  source: string
  grant?: UserMenuEffect | null
  grantorName?: string | null
  grantTime?: string
  expireTime?: string | null
  remark?: string | null
}

const rows = computed<Row[]>(() => {
  // 按树的先序排:同一目录下的节点挨在一起,路径带上目录 / 页面前缀
  const paths = new Map<number, { path: string; order: number }>()
  let order = 0
  const walk = (nodes: MenuTreeNode[], prefix: string) => {
    for (const n of nodes) {
      const path = prefix + translateMenuTitle(n.title, n.path || undefined)
      paths.set(n.id, { path, order: order++ })
      walk(n.children, `${path} / `)
    }
  }
  walk(props.tree, '')
  const grantOf = new Map(props.grants.map(g => [g.menuId, g]))
  return props.effective.nodes
    .filter(n => n.effective || n.grant != null)
    .map(n => {
      const g = grantOf.get(n.menuId)
      const source = [
        ...n.roles,
        n.grant === UserMenuEffect.Allow ? t('userGrant.allow') : n.grant === UserMenuEffect.Deny ? t('userGrant.deny') : null,
        n.expired ? t('userGrant.expired') : null,
        n.deniedByAncestor ? t('userGrant.deniedByAncestor') : null,
      ]
        .filter(Boolean)
        .join(' · ')
      return {
        menuId: n.menuId,
        path: paths.get(n.menuId)?.path ?? String(n.menuId),
        effective: n.effective,
        source: source || '—',
        grant: n.grant,
        grantorName: g?.grantorName,
        grantTime: g?.grantTime,
        expireTime: g?.expireTime,
        remark: g?.remark,
      }
    })
    .sort((a, b) => (paths.get(a.menuId)?.order ?? 0) - (paths.get(b.menuId)?.order ?? 0))
})

const dash = () => h('span', { class: 'faint' }, '—')
const columns: SmartTableColumn<Row>[] = [
  { key: 'path', title: () => t('userGrant.colMenu'), ellipsis: { tooltip: true } },
  {
    key: 'effective',
    title: () => t('userGrant.colState'),
    width: 90,
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: r.effective ? 'success' : 'default' }, () =>
        t(r.effective ? 'userGrant.effective' : 'userGrant.ineffective'),
      ),
  },
  { key: 'source', title: () => t('userGrant.colSource'), ellipsis: { tooltip: true } },
  {
    key: 'grantorName',
    title: () => t('userGrant.colGrantor'),
    width: 120,
    ellipsis: { tooltip: true },
    render: r => r.grantorName || dash(),
  },
  { key: 'grantTime', title: () => t('userGrant.colGrantTime'), width: 170, format: 'datetime' },
  {
    key: 'expireTime',
    title: () => t('userGrant.colExpire'),
    width: 170,
    render: r => (r.grant == null ? dash() : r.expireTime ? r.expireTime.replace('T', ' ') : t('userGrant.expireLongTerm')),
  },
  { key: 'remark', title: () => t('userGrant.colRemark'), ellipsis: { tooltip: true }, render: r => r.remark || dash() },
]
</script>

<template>
  <SmartTable :columns="columns" :data="rows" row-key="menuId" :toolbar="false" :pagination="false" fill-height />
</template>
```

`views/listSearch.spec.ts` 的 `EMBEDDED` 加一行（带注释）：

```ts
  // 用户授权弹窗「有效权限」页签里的只读表:随弹窗定高,搜索 / 工具栏对它没有意义
  'views/system/user/components/UserGrantEffectiveTable.vue',
```

- [ ] **Step 5: 弹窗壳 `UserGrantMenuSheet.vue`**

```vue
<script setup lang="ts">
// 用户「授权菜单」弹窗:壳的尺寸与底栏照角色页 GrantMenuSheet(定高、只有列表滚动、底栏常驻),
// 内容是两个页签——「单独授权」三态列表与「有效权限」只读表。打开时一次拉齐菜单树、授权记录与有效权限;
// 保存只提交变更集(与打开时的快照比)。目标用户不可编辑(自己 / 超管 / 范围外 / 对方是管理员)时整体只读并说明原因。
import { computed, reactive, ref, shallowRef, type CSSProperties } from 'vue'
import { NAlert, NButton, NModal, NSpin, NTabPane, NTabs, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useWindowSize } from '@vueuse/core'
import { menuApi, userApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import type { MenuTreeNode } from '#/types/menu'
import type { UserMenuEffective, UserMenuGrantItem } from '#/types/api'
import UserGrantMenuTable from './UserGrantMenuTable.vue'
import UserGrantEffectiveTable from './UserGrantEffectiveTable.vue'
import {
  READONLY_REASON_KEYS,
  countDraft,
  diffDraft,
  draftFromGrants,
  invalidExpiry,
  maxExpireDate,
  type Draft,
} from './userGrantState'

const emit = defineEmits<{ (e: 'saved'): void }>()
const { t } = useI18n()
const message = useMessage()
const auth = useAuthStore()

const show = ref(false)
const loading = ref(false)
const saving = ref(false)
const tab = ref<'grant' | 'effective'>('grant')
const user = ref<{ id: number; name: string } | null>(null)
const tree = shallowRef<MenuTreeNode[]>([])
const grants = shallowRef<UserMenuGrantItem[]>([])
const effective = shallowRef<UserMenuEffective | null>(null)
/** 打开时的快照;草稿另建一份,免得两边共用同一批对象、改一处两处都变。 */
let baseline: Draft = new Map()
const draft = reactive<Draft>(new Map())

/** 窄于此宽度弹窗几乎铺满视口(同角色页)。 */
const COMPACT = 640
const { width: winW } = useWindowSize()
const compact = computed(() => winW.value <= COMPACT)
const modalStyle = computed(() =>
  compact.value
    ? { width: 'calc(100vw - 16px)', height: 'calc(100dvh - 16px)' }
    : { width: 'min(1040px, calc(100vw - 48px))', height: 'min(780px, calc(100dvh - 48px))' },
)
const contentStyle: CSSProperties = { display: 'flex', flexDirection: 'column', flex: '1 1 0', minHeight: '0', padding: '0', overflow: 'hidden' }
const footerStyle = { padding: '0' }

const maxDate = computed(() => maxExpireDate(effective.value?.delegatedMaxDays, new Date()))
const counts = computed(() => countDraft(draft))
const dirty = computed(() => {
  const d = diffDraft(baseline, draft)
  return d.upserts.length + d.removes.length > 0
})
const readonlyText = computed(() => {
  const e = effective.value
  if (!e || e.targetEditable) return ''
  const key = e.readOnlyReason != null ? READONLY_REASON_KEYS[e.readOnlyReason] : undefined
  return t('userGrant.readonly', { reason: key ? t(key) : '' })
})
const defaultModuleId = computed(() => auth.currentModuleId ?? effective.value?.modules[0]?.id ?? 0)

async function open(target: { id: number; name: string }) {
  user.value = target
  tab.value = 'grant'
  effective.value = null
  show.value = true
  loading.value = true
  try {
    const [menuTree, records, eff] = await Promise.all([
      menuApi.tree(),
      userApi.getMenuGrants(target.id),
      userApi.getEffectiveMenus(target.id),
    ])
    tree.value = menuTree
    grants.value = records
    baseline = draftFromGrants(records)
    draft.clear()
    for (const [id, entry] of draftFromGrants(records)) draft.set(id, entry)
    effective.value = eff
  } catch (e) {
    message.error(translateError(e))
    show.value = false
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!user.value) return
  const bad = invalidExpiry(baseline, draft, effective.value?.delegatedMaxDays, new Date())
  if (bad.length) {
    message.warning(t('userGrant.invalidExpiry', { count: bad.length }))
    return
  }
  const { upserts, removes } = diffDraft(baseline, draft)
  if (!upserts.length && !removes.length) {
    show.value = false
    return
  }
  saving.value = true
  try {
    await userApi.setMenuGrants(user.value.id, upserts, removes)
    message.success(t('userGrant.saved'))
    show.value = false
    emit('saved')
  } catch (e) {
    message.error(translateError(e))
  } finally {
    saving.value = false
  }
}

defineExpose({ open })
</script>

<template>
  <n-modal
    v-model:show="show"
    preset="card"
    :title="user ? t('userGrant.title', { name: user.name }) : ''"
    :closable="!saving"
    :close-on-esc="!saving"
    :mask-closable="false"
    :auto-focus="false"
    :style="modalStyle"
    :content-style="contentStyle"
    :footer-style="footerStyle"
  >
    <n-spin :show="loading" class="ugs-spin">
      <div v-if="effective" class="ugs">
        <div class="ugs-notes">
          <n-alert v-if="readonlyText" type="warning" :bordered="false">{{ readonlyText }}</n-alert>
          <n-alert v-if="!effective.hasRoles" type="info" :bordered="false">{{ t('userGrant.noRoles') }}</n-alert>
          <p class="ugs-hint">{{ t('userGrant.hint') }}</p>
        </div>
        <n-tabs v-model:value="tab" type="line" class="ugs-tabs" pane-class="ugs-pane">
          <n-tab-pane name="grant" :tab="t('userGrant.tabGrant')">
            <UserGrantMenuTable
              :tree="tree"
              :effective="effective"
              :draft="draft"
              :default-module-id="defaultModuleId"
              :max-date="maxDate"
            />
          </n-tab-pane>
          <n-tab-pane name="effective" :tab="t('userGrant.tabEffective')">
            <UserGrantEffectiveTable :tree="tree" :effective="effective" :grants="grants" />
          </n-tab-pane>
        </n-tabs>
      </div>
    </n-spin>

    <template #footer>
      <footer class="ugs-foot" :class="{ 'is-narrow': compact }">
        <div class="ugs-summary">
          <span>{{ t('userGrant.summary', counts) }}</span>
          <span v-if="dirty" class="ugs-dirty">{{ t('userGrant.unsaved') }}</span>
        </div>
        <n-button :disabled="saving" @click="show = false">{{ t('common.cancel') }}</n-button>
        <n-button v-if="effective?.targetEditable" type="primary" :loading="saving" @click="save">
          {{ t('common.save') }}
        </n-button>
      </footer>
    </template>
  </n-modal>
</template>

<style scoped>
.ugs-spin,
.ugs-spin :deep(.n-spin-content),
.ugs,
.ugs-tabs {
  display: flex;
  flex-direction: column;
  flex: 1 1 0;
  min-height: 0;
}
.ugs-notes {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 20px 0;
}
.ugs-hint {
  margin: 0;
  font-size: 13px;
  color: var(--text-2);
}
.ugs-tabs :deep(.n-tabs-nav) {
  padding: 0 20px;
}
.ugs-tabs :deep(.n-tab-pane),
.ugs-tabs :deep(.n-tabs-pane-wrapper) {
  display: flex;
  flex-direction: column;
  flex: 1 1 0;
  min-height: 0;
  padding: 0;
}
.ugs-foot {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 20px 14px;
  border-top: 1px solid var(--hairline);
}
.ugs-summary {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 13px;
  color: var(--text-2);
}
.ugs-dirty {
  color: var(--warn);
}
.ugs-foot.is-narrow {
  flex-wrap: wrap;
}
.ugs-foot.is-narrow .ugs-summary {
  flex: 1 1 100%;
}
</style>
```

- [ ] **Step 6: 用户页入口**

`views/system/user/index.vue`：

1. import 区加：

```ts
import UserGrantMenuSheet from './components/UserGrantMenuSheet.vue'
import { useUserStore } from '#/stores/user'
```

2. `resetModalRef` 声明之后加：

```ts
// 授权菜单弹窗(用户单独授权);超管那一行和自己那一行不出入口(后端同样拒绝)
const grantSheetRef = ref<InstanceType<typeof UserGrantMenuSheet> | null>(null)
const userStore = useUserStore()
const myId = computed(() => userStore.userInfo?.userId)
```

3. 操作列 `rawMoreOptions` 数组最前面加：

```ts
        authStore.hasPerm('PUT:/api/v1/sys/user/menu') && !r.isSuperAdmin && r.id !== myId.value
          ? { key: 'grantMenus', label: t('userGrant.action'), icon: menuIcon('ph:list-checks') }
          : null,
```

4. `NDropdown` 的 `onSelect` 加分支：`else if (key === 'grantMenus') grantSheetRef.value?.open({ id: r.id, name: r.name })`。

5. 模板 `<ResetPasswordModal ref="resetModalRef" />` 之后加 `<UserGrantMenuSheet ref="grantSheetRef" @saved="() => tableRef?.refresh()" />`。

- [ ] **Step 7: 图标子集、单测、类型、lint、构建**

Run: `cd web && npm run gen:icons`（新用了 `ph:list-checks`、`ph:warning`、`ph:caret-right`，子集过期会让 `icons.spec.ts` 失败）
Run: `cd web && npm test && npm run typecheck && npm run lint && npm run format:check && npm run build`
Expected: 全部通过。本机 Windows 下 vitest 全量若出现 EBUSY / 超时这类随机失败，分批重跑确认（不是代码问题时以 CI 为准），不要改测试。

- [ ] **Step 8: 在浏览器里点一遍**

模板里未注册的组件只在运行时报 `Failed to resolve component`，构建与单测都查不出来。
后台起后端（同 Task 9 Step 1，指向 scratchpad 里的临时库，别动本地开发库）与前端（`cd web && npm run dev`），
用超管登录（临时库首次启动在控制台打印超管口令），进「系统 → 用户管理」：
1. 任一普通用户行「更多 → 授权菜单」能打开；超管行与自己那一行没有这个入口。
2. 弹窗里切应用、搜索、折叠正常；把某页面切到「允许」后出现到期日与理由；切到「拒绝」时，挂了共用接口的节点（如「角色-授权菜单」）出现「以下接口仍可调用」。
3. 保存后重开，状态回显一致；「有效权限」页签列出该页面与来源。
4. 浏览器控制台没有 `Failed to resolve component` 与 Vue 警告。
浏览器自动化时窗口铺满全屏（不要固定小视口），不要用真实账号输错密码。

- [ ] **Step 9: 提交**

```bash
git add web/packages/admin/src
git commit -m "feat(web): 用户页新增「授权菜单」弹窗,支持允许、拒绝与到期" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: 单独授权一览抽屉

**Files:**
- Create: `web/packages/admin/src/views/system/user/components/UserGrantOverviewDrawer.vue`
- Modify: `web/packages/admin/src/views/system/user/index.vue`

**Interfaces:**
- Consumes: `userApi.menuGrantPage`（Task 9）；`UserGrantMenuSheet.open`（Task 10）
- Produces: `UserGrantOverviewDrawer`：`v-model:show`，`emit('adjust', { id, name })`

- [ ] **Step 1: 抽屉组件**

```vue
<script setup lang="ts">
// 单独授权一览:全系统的例外放在一张表里给超管复核;普通管理员只看得到自己数据范围内用户的记录(后端收口)。
// 行操作「去调整」把用户交给父页,打开授权菜单弹窗。
import { computed, h } from 'vue'
import { NButton, NDrawer, NDrawerContent, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import TableTotal from '#/components/TableTotal/index.vue'
import { userApi } from '#/api'
import { translateMenuTitle } from '#/locales/menuTitle'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import { UserMenuEffect, UserMenuGrantStatus, type UserMenuGrantPageItem } from '#/types/api'

const show = defineModel<boolean>('show', { default: false })
const emit = defineEmits<{ (e: 'adjust', user: { id: number; name: string }): void }>()
const { t } = useI18n()
const message = useMessage()

const effectOptions = computed(() => [
  { label: t('userGrant.allow'), value: UserMenuEffect.Allow },
  { label: t('userGrant.deny'), value: UserMenuEffect.Deny },
])
const statusOptions = computed(() => [
  { label: t('userGrant.statusActive'), value: UserMenuGrantStatus.Active },
  { label: t('userGrant.statusExpiring'), value: UserMenuGrantStatus.Expiring },
  { label: t('userGrant.statusExpired'), value: UserMenuGrantStatus.Expired },
])
const STATUS_TAG: Record<UserMenuGrantStatus, 'success' | 'warning' | 'default'> = {
  [UserMenuGrantStatus.Active]: 'success',
  [UserMenuGrantStatus.Expiring]: 'warning',
  [UserMenuGrantStatus.Expired]: 'default',
}
const statusLabel = (s: UserMenuGrantStatus) => statusOptions.value.find(o => o.value === s)?.label ?? ''
const dash = () => h('span', { class: 'faint' }, '—')

const columns: SmartTableColumn<UserMenuGrantPageItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  // 只作搜索项:目标用户账号或姓名
  { key: 'user', title: () => t('userGrant.colUser'), hideInTable: true, search: { actions: SEARCH_ACTIONS.fuzzy } },
  {
    key: 'userName',
    title: () => t('userGrant.colUser'),
    ellipsis: { tooltip: true },
    card: 'title',
    render: r => `${r.userName}(${r.userAccount})`,
  },
  {
    key: 'menuTitle',
    title: () => t('userGrant.colMenu'),
    ellipsis: { tooltip: true },
    render: r =>
      [r.moduleTitle ? translateMenuTitle(r.moduleTitle) : null, translateMenuTitle(r.menuTitle)].filter(Boolean).join(' · '),
  },
  {
    key: 'effect',
    title: () => t('userGrant.colEffect'),
    width: 90,
    options: effectOptions,
    search: { actions: SEARCH_ACTIONS.exact, props: { clearable: true } },
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: r.effect === UserMenuEffect.Allow ? 'success' : 'error' }, () =>
        t(r.effect === UserMenuEffect.Allow ? 'userGrant.allow' : 'userGrant.deny'),
      ),
  },
  {
    key: 'status',
    title: () => t('userGrant.colStatus'),
    width: 110,
    options: statusOptions,
    search: { actions: SEARCH_ACTIONS.exact, props: { clearable: true } },
    render: r => h(NTag, { size: 'small', bordered: false, type: STATUS_TAG[r.status] }, () => statusLabel(r.status)),
  },
  {
    key: 'expireTime',
    title: () => t('userGrant.colExpire'),
    width: 170,
    render: r => (r.expireTime ? r.expireTime.replace('T', ' ') : t('userGrant.expireLongTerm')),
  },
  // 只作搜索项:授权人账号或姓名
  { key: 'grantor', title: () => t('userGrant.colGrantor'), hideInTable: true, search: { actions: SEARCH_ACTIONS.fuzzy } },
  {
    key: 'grantorName',
    title: () => t('userGrant.colGrantor'),
    width: 120,
    ellipsis: { tooltip: true },
    render: r => r.grantorName || dash(),
  },
  { key: 'grantTime', title: () => t('userGrant.colGrantTime'), width: 170, format: 'datetime' },
  { key: 'remark', title: () => t('userGrant.colRemark'), ellipsis: { tooltip: true }, render: r => r.remark || dash() },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 100,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(
        NButton,
        { size: 'small', quaternary: true, type: 'primary', onClick: () => emit('adjust', { id: r.userId, name: r.userName }) },
        () => t('userGrant.adjust'),
      ),
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <n-drawer v-model:show="show" placement="right" width="min(1120px, 96vw)">
    <n-drawer-content
      :title="t('userGrant.overview')"
      closable
      :body-content-style="{ height: '100%', display: 'flex', flexDirection: 'column' }"
    >
      <SmartTable
        :columns="columns"
        :fetcher="userApi.menuGrantPage"
        :search="{ container: 'table' }"
        :toolbar="TABLE_TOOLBAR"
        fill-height
        :min-row-height="TABLE_MIN_ROW_HEIGHT"
        card-on-narrow
        storage-key="sys-user-menu-grants"
        @error="e => message.error(translateError(e))"
      >
        <template #pagination-prefix="{ itemCount }">
          <TableTotal :count="itemCount" />
        </template>
      </SmartTable>
    </n-drawer-content>
  </n-drawer>
</template>
```

（SmartTable 把搜索值按列 `key` 交给 fetcher：`user`、`grantor`、`effect`、`status` 正好是 `userApi.menuGrantPage` 的参数名。如果实际传给 fetcher 的键与预期不符，参照 `userApi.page` 与 `flatSearchOf` 的现有做法在 `menuGrantPage` 里映射，不要改表格库。）

- [ ] **Step 2: 用户页工具栏「更多」**

`views/system/user/index.vue`：
1. import 加 `import UserGrantOverviewDrawer from './components/UserGrantOverviewDrawer.vue'`；在导入导出状态旁加 `const grantOverviewShow = ref(false)`。
2. `toolbarMore` 数组末尾加：

```ts
    authStore.hasPerm('GET:/api/v1/sys/user/menu-grants/page')
      ? { label: t('userGrant.overview'), key: 'grantOverview' }
      : null,
```

3. `onMoreSelect` 加 `else if (key === 'grantOverview') grantOverviewShow.value = true`。
4. 模板在 `UserGrantMenuSheet` 之后加：

```vue
  <UserGrantOverviewDrawer v-model:show="grantOverviewShow" @adjust="u => grantSheetRef?.open(u)" />
```

- [ ] **Step 3: 验证**

Run: `cd web && npm test && npm run typecheck && npm run lint && npm run format:check`
Expected: 全部通过（`listSearch.spec.ts` 会检查这张表套了整页标准；`tableColumns.spec.ts` 检查每个数据列写了 `width` 或 `ellipsis`）。
浏览器里点一遍：「用户管理 → 更多 → 单独授权一览」能打开，按状态 / 效果 / 授权人筛选有效，「去调整」打开对应用户的授权菜单弹窗；控制台无组件解析警告。

- [ ] **Step 4: 提交**

```bash
git add web/packages/admin/src
git commit -m "feat(web): 用户页新增「单独授权一览」抽屉" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: 模块页「可转授」开关，角色页对新建角色隐藏系统模块

**Files:**
- Modify: `web/packages/admin/src/views/system/module/index.vue`
- Modify: `web/packages/admin/src/views/system/role/components/grantMenuGroups.ts` + `grantMenuGroups.spec.ts`
- Modify: `web/packages/admin/src/views/system/role/components/GrantMenuSheet.vue`（可选 `hint`）
- Modify: `web/packages/admin/src/views/system/role/index.vue`
- Modify: `web/packages/admin/src/locales/zh-CN.ts`、`en-US.ts`

**Interfaces:**
- Consumes: `ModuleRow.isDelegatable`、`SysRole.isBuiltin`（Task 9）
- Produces: `SYSTEM_MODULE_ID = 1`、`treeForRole(tree, builtinRole)`、`modulesForRole(modules, builtinRole)`（`grantMenuGroups.ts`）；`GrantMenuSheet` 可选属性 `hint?: string`

- [ ] **Step 1: 写失败的单测**

追加到 `grantMenuGroups.spec.ts`（文件头的 import 补 `treeForRole, modulesForRole, SYSTEM_MODULE_ID`；构造节点沿用该文件已有的造数函数，没有就按下面直接写字面量）：

```ts
describe('角色授权范围:系统模块只授内置角色', () => {
  const top = (id: number, moduleId: number | null) => ({
    id, parentId: 0, type: MenuType.Catalog, title: `c${id}`, permission: '', sort: 0, enabled: true, visible: true, moduleId, children: [],
  })
  const tree = [top(200, SYSTEM_MODULE_ID), top(900, 2), top(950, null)]
  const modules = [{ id: SYSTEM_MODULE_ID }, { id: 2 }]

  it('非内置角色去掉系统模块的顶级节点与应用', () => {
    expect(treeForRole(tree, false).map(n => n.id)).toEqual([900, 950])
    expect(modulesForRole(modules, false).map(m => m.id)).toEqual([2])
  })

  it('内置角色原样', () => {
    expect(treeForRole(tree, true)).toBe(tree)
    expect(modulesForRole(modules, true)).toBe(modules)
  })
})
```

Run: `cd web/packages/admin && npx vitest run src/views/system/role/components/grantMenuGroups.spec.ts`
Expected: FAIL（函数不存在）。

- [ ] **Step 2: 实现范围函数**

追加到 `grantMenuGroups.ts`：

```ts
// ── 角色授权范围 ──

/** 内置 system 模块的 Id(与后端 DefaultModuleSeed.BUILTIN_MODULE_ID 一致)。 */
export const SYSTEM_MODULE_ID = 1

/** 系统模块的菜单只能授给内置角色:非内置角色去掉挂在系统模块下的顶级节点(后端同样拒绝)。 */
export const treeForRole = (tree: MenuTreeNode[], builtinRole: boolean): MenuTreeNode[] =>
  builtinRole ? tree : tree.filter(n => n.moduleId !== SYSTEM_MODULE_ID)

/** 同上:应用下拉里去掉系统模块。 */
export const modulesForRole = <T extends { id: number }>(modules: T[], builtinRole: boolean): T[] =>
  builtinRole ? modules : modules.filter(m => m.id !== SYSTEM_MODULE_ID)
```

Run: 同 Step 1。Expected: PASS。

- [ ] **Step 3: 角色页接上**

`GrantMenuSheet.vue`：`defineProps` 加 `/** 列表上方的一行说明(如范围限制);不给不显示 */ hint?: string`；模板 `<div class="gs" ...>` 里、`<GrantMenuTable` 之前加 `<p v-if="hint" class="gs-hint">{{ hint }}</p>`；样式加：

```css
.gs-hint {
  margin: 10px 20px 0;
  font-size: 13px;
  color: var(--text-2);
}
```

`role/index.vue`：
1. 从 `./components/grantMenuGroups` 导入 `treeForRole, modulesForRole`。
2. `defaultModuleId` 声明后加 `const menuModules = shallowRef<ModuleRow[]>([])`。
3. `openMenus` 改成：

```ts
async function openMenus(r: SysRole) {
  try {
    const [tree, granted] = await Promise.all([menuApi.tree(), roleApi.getMenus(r.id)])
    // 系统模块的菜单只能授给内置角色:新建角色的弹窗里不出现系统模块(后端同样拒绝)
    const builtin = r.isBuiltin === true
    menuRole.value = r
    menuTree.value = treeForRole(tree, builtin)
    menuModules.value = modulesForRole(modules.value, builtin)
    menuGranted.value = granted
    const preferred = auth.currentModuleId ?? menuModules.value[0]?.id ?? UNASSIGNED
    defaultModuleId.value = menuModules.value.some(m => m.id === preferred)
      ? preferred
      : (menuModules.value[0]?.id ?? UNASSIGNED)
    showMenus.value = true
  } catch (e) {
    message.error(translateError(e))
  }
}
```

4. 模板 `GrantMenuSheet` 的 `:modules="modules"` 改成 `:modules="menuModules"`，加 `:hint="menuRole?.isBuiltin ? undefined : t('role.systemMenusBuiltinOnly')"`。

文案：`zh-CN.ts` 的 `role` 块加 `systemMenusBuiltinOnly: '系统模块的菜单只能授给内置角色,这里不显示',`；
`en-US.ts` 加 `systemMenusBuiltinOnly: 'System module menus can only be granted to built-in roles and are hidden here',`。

- [ ] **Step 4: 模块页开关**

`module/index.vue`：
1. `blank()` 加 `isDelegatable: true,`（新建模块默认可转授）；`toInput(r)` 加 `isDelegatable: r.isDelegatable ?? false,`。
2. `editingId` 之后加：

```ts
/** 正在编辑内置 system 模块:可转授开关固定关、置灰(后端读写都按 false)。 */
const editingBuiltin = computed(() => editingId.value !== null && form.code === 'system')
/** NSwitch 只收布尔,null(存量模块)按关显示。 */
const delegatable = computed({
  get: () => form.isDelegatable === true,
  set: (v: boolean) => (form.isDelegatable = v),
})
```

3. 列定义在 `apiPrefix` 列之后加：

```ts
  {
    title: () => t('module.delegatable'),
    key: 'isDelegatable',
    width: 100,
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: r.isDelegatable ? 'success' : 'default' }, () =>
        t(r.isDelegatable ? 'common.yes' : 'common.no'),
      ),
  },
```

4. 表单在「状态」项之后加：

```vue
      <n-form-item :label="t('module.delegatable')">
        <n-space vertical :size="2" style="width: 100%">
          <n-tooltip :disabled="!editingBuiltin">
            <template #trigger>
              <span style="display: inline-flex">
                <n-switch v-model:value="delegatable" :disabled="editingBuiltin" />
              </span>
            </template>
            {{ t('module.builtinNotDelegatable') }}
          </n-tooltip>
          <span class="hint">{{ t('module.delegatableHint') }}</span>
        </n-space>
      </n-form-item>
```

文案：`module` 块 zh 加 `delegatable: '可转授',`、`delegatableHint: '打开后,普通管理员能把本应用的菜单单独授给或拒给用户(限时)',`、`builtinNotDelegatable: '内置应用固定不可转授',`；
en 加 `delegatable: 'Delegatable',`、`delegatableHint: "When on, administrators can grant or deny this app's menus to individual users (time-limited)",`、`builtinNotDelegatable: 'The built-in app is never delegatable',`。

- [ ] **Step 5: 验证**

Run: `cd web && npm test && npm run typecheck && npm run lint && npm run format:check`
Expected: 全部通过。
浏览器里点一遍：模块页「业务中心」显示「是」，编辑「系统」时开关置灰并有悬浮说明；角色页给新建角色「授权菜单」时应用下拉里没有「系统」、列表上方有提示，给「系统管理员」授权时照常显示系统模块。

- [ ] **Step 6: 提交**

```bash
git add web/packages/admin/src
git commit -m "feat(web): 模块可转授开关,新建角色授权时不显示系统模块" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: e2e 主流程

**Files:**
- Create: `web/e2e/user-menu-grant.spec.ts`

- [ ] **Step 1: 写用例**

```ts
import { test, expect, type Locator, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, enterFirstAppIfNeeded, login, sidebarLeafNames } from './helpers'
import { apiAdminToken, apiCreateUser } from './api'

/**
 * 用户单独授权主流程:超管给一个没有任何角色的用户「允许」岗位管理页 → 该用户登录能看到 →
 * 改成「拒绝」→ 该用户再登录就没有任何应用可进。走真实的弹窗,不经接口直改。
 */

const ACCOUNT = `e2e_grant_${Date.now().toString(36)}`
const PASSWORD = 'TestPass123!'
const PAGE_TITLE = /^(岗位管理|Positions)$/

async function logout(page: Page) {
  await page.evaluate(() => localStorage.clear())
  await page.goto('/login')
  await expect(page).toHaveURL(/\/login/)
}

/** 超管登录,进用户管理,按账号搜到这个用户,打开「授权菜单」弹窗。 */
async function openGrantSheet(page: Page): Promise<Locator> {
  await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
  await enterApp(page, SYSTEM_APP)
  await page.goto('/system/user')
  await expect(page.locator('.n-data-table')).toBeVisible({ timeout: 10_000 })
  const input = page.locator('.smart-table-cond input[placeholder="请输入"]')
  await input.fill(ACCOUNT)
  await input.press('Enter')
  const row = page.locator('.n-data-table-tr').filter({ hasText: ACCOUNT })
  await expect(row).toBeVisible({ timeout: 5_000 })
  await row.getByText(/更多|More/i).click()
  await page.locator('.n-dropdown-option').filter({ hasText: /授权菜单|Grant menus/ }).click()
  const sheet = page.locator('.n-modal').filter({ has: page.locator('.ut-scroll') })
  await expect(sheet.locator('.ugr').first()).toBeVisible({ timeout: 10_000 })
  return sheet
}

async function setPageState(sheet: Locator, state: RegExp) {
  const row = sheet.locator('.ugr.is-page').filter({ has: sheet.page().locator('.ugr-title', { hasText: PAGE_TITLE }) })
  await row.locator('.n-radio-button').filter({ hasText: state }).click()
  await sheet.getByRole('button', { name: /^(保存|Save)$/ }).click()
  await expect(sheet.page().locator('.n-message').first()).toContainText(/授权已保存|Grants saved/, { timeout: 5_000 })
}

test.describe('用户单独授权', () => {
  test.describe.configure({ mode: 'serial' })

  test.beforeAll(async ({ request }) => {
    const token = await apiAdminToken(request)
    await apiCreateUser(request, token, { account: ACCOUNT, name: 'E2E 授权用户', password: PASSWORD, forceTotp: false })
  })

  test('允许一个页面 → 该用户看得到', async ({ page }) => {
    const sheet = await openGrantSheet(page)
    await setPageState(sheet, /^(允许|Allow)$/)

    await logout(page)
    await login(page, ACCOUNT, PASSWORD)
    await enterFirstAppIfNeeded(page)
    expect((await sidebarLeafNames(page)).some(n => PAGE_TITLE.test(n))).toBe(true)
  })

  test('改成拒绝 → 该用户没有应用可进', async ({ page }) => {
    const sheet = await openGrantSheet(page)
    await setPageState(sheet, /^(拒绝|Deny)$/)

    await logout(page)
    await login(page, ACCOUNT, PASSWORD)
    await expect(page).toHaveURL(/\/module/)
    await expect(page.locator('.n-empty')).toContainText(/暂无可访问的应用|No accessible/)
  })
})
```

- [ ] **Step 2: 跑 e2e**

Run: `ci.bat -Stage web-e2e`（多会话同时跑时给 Playwright 一个独立的 `--output` 目录，否则 `test-results` 会被互相清掉，出现假失败）
Expected: 新用例与原有用例全部通过。

- [ ] **Step 3: 提交**

```bash
git add web/e2e/user-menu-grant.spec.ts
git commit -m "test(web): 用户单独授权的允许与拒绝主流程 e2e" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: 文档

先读 `skills/write-docs.md`（`/write-docs`）：中文是母版、英文是译文，口吻、标点、开头与破折号额度都按它来。

**Files:**
- Modify: `docs/permission-design-guide.md`
- Modify: `site/zh/backend/auth-security.md` + `site/backend/auth-security.md`
- Modify: `site/zh/guide/deployment/index.md:104` + `site/guide/deployment/index.md:104`
- Modify: `docs/agents/security-optional-config.md`（安全配置表加一行）
- Modify: `CONTEXT.md`

- [ ] **Step 1: 权限设计指导（消费端）**

`docs/permission-design-guide.md`：
- 「一、模型」末尾加一段：用户授权在角色之外按节点「允许 / 拒绝」，可带到期；有效菜单 =（启用角色授予 ∪ 生效中的允许）− 生效中的拒绝及其子孙，再只留启用节点；
  拒绝作用在节点不在权限码，共用接口的按钮被拒后接口仍可能由别的有效节点放行；单独授权只授功能不授数据，没有角色的用户数据范围是「仅本人」。
- 「四、菜单种子」要点末尾加一条：自己的模块种子（`ISeedData<SysModule>`）要显式写 `IsDelegatable`，不写即不可转授；
  模块种子开 `SyncOnUpgrade` 时用 `SyncColumns` 白名单，别把 `IsDelegatable` 放进去，否则升级会刷掉超管的设置。
- 新增一节「九、普通管理员怎么配」：收录规格第一节的推荐配置表（用户管理、角色管理两页勾哪些，业务模块按需），写明：
  系统模块的菜单只能授给内置角色，所以普通管理员用内置「系统管理员」角色，不能另建；角色管理里「新增 / 更新 / 删除 / 授权菜单 / 数据范围」对普通管理员无效（服务层只允许超管，勾了也只会 41003）；
  模块管理的「更新」权限能改「可转授」开关，等于决定委派边界，只给超管；`SmartAdmin:Security:DelegatedGrantMaxDays` 的含义。
- 「八、检查清单」加两条：消费方的模块种子写了 `IsDelegatable`；整体替换 `IPermissionProvider` / `IMenuService` 的项目自己实现了单独授权（否则不生效）。

- [ ] **Step 2: 文档站「认证与安全」新增一节**

`site/zh/backend/auth-security.md` 在「会话与强制下线」之前加「## 用户单独授权」，讲清：
做什么（允许 / 拒绝 / 有效期，拒绝优先且扩展到子孙）、谁能授（超管任意；普通管理员只能授可转授模块里的菜单、只能对范围内非管理员用户、允许必须限时）、
系统模块菜单只授内置角色、错误码 41005–41009 与 42031 各在什么时候出现、`DelegatedGrantMaxDays`、事件 `UserMenuGrantsChangedEvent`、
已知限制（已打开的页面不实时刷新）、整体替换 `IPermissionProvider` / `IMenuService` 时单独授权不生效。
`site/backend/auth-security.md` 照中文译出同一节。

Run: `cd site && npm run lint:prose -- zh/backend/auth-security.md backend/auth-security.md && npm run check:llms`
Expected: 无报错。

- [ ] **Step 3: 部署文档的模块种子一句**

`site/zh/guide/deployment/index.md:104` 的「模块表（`sys_module`）仍是整行刷回。」改成：
「模块表（`sys_module`）只刷编码、图标、落地路由、路由前缀，你改过的标题、排序、启用、备注与「可转授」留着。」
同段末尾补一句：「从种子版本 6 升级上来的那一次，还会删除非内置角色上授的系统模块菜单，详见更新日志的升级说明。」
`site/guide/deployment/index.md:104` 同步译文。再跑一次 `lint:prose` 覆盖这两页。

- [ ] **Step 4: 配置清单与术语表**

`docs/agents/security-optional-config.md` 的配置表在 `DefaultInitialPassword` 行之后加：
`| `DelegatedGrantMaxDays` | 普通管理员单独授权「允许」的最长天数，默认 90；0 = 不限 |`

`CONTEXT.md` 在「通用」之前加一节：

```markdown
## 授权(RBAC)

| 术语 | 定义 |
|---|---|
| 角色授权 | `sys_role_menu` 一行:角色被授予一个菜单节点。 |
| 内置角色 | 内核种子播的固定 Id(1–999)角色;系统模块的菜单只能授给它们。 |
| 用户授权 / 单独授权 | `sys_user_menu` 一行:对一个用户的一个菜单节点做「允许」或「拒绝」,可带到期时间。 |
| 生效中的用户授权 | 到期时间为空,或晚于当前时间的用户授权。 |
| 有效菜单 | (启用角色授予 ∪ 生效中的允许)− 生效中的拒绝及其子孙,再只留启用节点。 |
| 有效权限码 | 有效菜单里各节点 `Permission` 拆开去重后的集合,`[RolePermission]` 用它比对。 |
| 授权来源 | 一个菜单为什么有效或无效:来自哪些角色、允许、拒绝、已过期。 |
| 普通管理员 | 非超管、有效权限码里含 `PUT:/api/v1/sys/user/menu` 的用户。 |
| 委派授权 | 普通管理员发起的用户授权。 |
| 可转授模块 | `SysModule.IsDelegatable == true` 的模块;普通管理员只能对其中的菜单做单独授权。 |
| 菜单所属模块 | 沿 `ParentId` 上溯到根目录取 `ModuleId`;不挂模块的菜单视为不可转授。 |
```

- [ ] **Step 5: 跑文档闸门并提交**

Run: `ci.bat -Stage docs`
Expected: 通过（含散文 lint 自检、`check:llms`、skill 包装同步检查与 VitePress 构建）。

```bash
git add docs/permission-design-guide.md docs/agents/security-optional-config.md CONTEXT.md site
git commit -m "docs(rbac): 用户单独授权、模块可转授与系统菜单只授内置角色" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: 变更说明进 CHANGELOG，全量验证

**Files:**
- Modify: `CHANGELOG.md`（`## Unreleased`）

- [ ] **Step 1: 写更新日志**

在 `## Unreleased` 下、现有 `### Changed` 之前加 `### 升级说明`，内容取本计划顶部「变更说明」的全部条目，按 CHANGELOG 现有口吻改写，要求：
- 段首加粗一句「**本版含破坏性变更：升级时会删除新建角色上授的系统模块菜单。** 升级前先读这一节。」
- 「破坏性变更」「数据库」「种子」「接口与权限码」「后端 API 表面」「配置」「前端」「回滚」「已知限制」逐项写全，不省略关闸门生产库的两条路与升级前备份 `sys_role_menu`。
- 版本号不在这里定：发版走 `/smart-release`（只能由用户触发），本计划不发版。

另在 `### Added` 下写功能条目：用户单独授权（允许 / 拒绝 / 到期 / 有效权限 / 一览）、模块「可转授」、系统模块菜单只授内置角色；
在 `### Changed` 下写：模块种子升级只刷结构列；`IRbacService.InvalidatePermissionsByMenuAsync` 扇出到有单独授权的用户；`41005` 文案泛指授权。
正文段落按语义断点换行，与文件里已有条目一致。

- [ ] **Step 2: 全量本地闸门**

Run: `ci.bat`
Expected: backend(sqlite) + web + docs + template + audit 全部通过，汇总表里没有 Redis 未连上的警告（有的话按提示开 Redis 再跑一次 Redis 合约测试）。

本次动了数据层（新表、新列、种子同步、升级清理），再跑方言腿：

Run: `ci.bat -Stage backend -Dialect mysql,postgres,sqlserver`
Expected: 全部通过。MySQL 腿本机约 17 分钟、SqlServer 子集更慢，属正常，不要因为「看着卡住」中断。

Run: `ci.bat -Stage web-e2e`
Expected: 通过。

- [ ] **Step 3: 提交**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): 用户单独授权与系统菜单只授内置角色的升级说明" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 4: 收尾**

用 superpowers:finishing-a-development-branch 决定怎么并回 `dev`（PR 或本地合并由用户定）。`main` 受保护，不直接提交；发版只能由用户触发 `/smart-release`。


