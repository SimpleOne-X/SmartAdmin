# 机构共享数据 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让继承 `OrgAuditEntity` 的表支持按行共享：`IsShared = true` 的行所有机构可读，只有数据范围包含其归属机构的用户可写。

**Architecture:** `OrgAuditEntity` 从 `IOrgScoped` 换到新接口 `IOrgShared`，并多一个可空列 `IsShared`。
`IOrgShared` 注册自己的全局过滤器，在原有机构条件上多放行共享行。
写守卫经 `DataScopeResult.Owns` 判断归属，`DataEntity` 与 `IOrgScoped` 的路径不动。

**Tech Stack:** .NET 10、SqlSugarCore 5.1.4.219、xUnit v3（Microsoft.Testing.Platform）。

**Spec:** `docs/superpowers/specs/2026-10-30-org-shared-data-design.md`

## Global Constraints

- `DataEntity` 不加列，它和 `IOrgScoped` 实体的可见性、写入行为不变。
- `IOrgShared` 不继承 `IOrgScoped`。
- 不新增基类；`IsShared` 是 `bool?`，`null` 与 `false` 都表示不共享。
- `CreateOrgId` 为空不代表共享。
- 不新增依赖；内置服务注册继续用 `TryAdd*`。
- 代码注释用中文，只写现在做什么和为什么，不写历史、日期、issue 号或设计文档引用。
- 提交信息用中文 conventional-commit 格式，见 `skills/write-commit.md`，末尾带 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 测试失败时先定位原因，不改断言、不加 skip。
- 只提交本计划涉及的文件；工作区里的 `docs/superpowers/specs/2026-10-28-license-design.md` 不属于本功能，不要动。
- 测试命令用 `dotnet test backend/SmartAdmin.slnx -- --filter-class "*X*"`，不用 `--filter "FullyQualifiedName~X"`。

## Review Focus

1. 归属机构读出共享行、改其他字段再保存：`IsShared` 仍是 `true`。测试在 Task 2。
2. 归属机构关闭共享：别的机构立刻查不到。测试在 Task 2。
3. 两张 `OrgAuditEntity` 表联查：没有列名歧义，两张表都按范围过滤。测试在 Task 1。
4. `DataEntity` 与 `OrgAuditEntity` 联查：两个过滤器都生效，`ClearFilter<IOrgScoped, IOrgShared>()` 能同时清掉。测试在 Task 1。
5. 给已有数据的老表补 `IsShared` 列：老行是 `null`，因而不共享。补列测试在 Task 3，`null` 行不共享的测试在 Task 1。

---

### Task 1: `IOrgShared` 接口、字段与查询过滤器

**Files:**
- Modify: `backend/src/SmartAdmin.SqlSugar/Entities/DataEntity.cs`
- Modify: `backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs:135-147`、`:168-172`
- Modify: `backend/src/SmartAdmin.SqlSugar/Repository/SqlSugarRepository.cs:25-27`、`:45-46`
- Create: `backend/tests/SmartAdmin.Tests/OrgShareTests.cs`
- Modify: `backend/tests/SmartAdmin.Tests/OrgAuditEntityTests.cs:55`、`:112`、`:139`（只改注释）

**Interfaces:**
- Produces: `public interface IOrgShared { long? CreateOrgId { get; } long? CreateUserId { get; } bool? IsShared { get; } }`
- Produces: `OrgAuditEntity : AuditEntity, IOrgShared`，新增 `public bool? IsShared { get; set; }`，标 `[SugarColumn(IsNullable = true, ColumnDescription = "是否共享给所有机构")]`
- Produces（测试类内）：`FooShare : OrgAuditEntity`（表 `foo_org_share`，`Title`）、`FooShareChild : OrgAuditEntity`（表 `foo_org_share_child`，`ParentId`、`Title`）、`FooPrivate : DataEntity`（表 `foo_org_private`，`Title`），以及照抄 `OrgAuditEntityTests.BuildSp` 的 `BuildSp`

- [ ] **Step 1: 写失败的测试**

在 `OrgShareTests` 中新增下面的用例。除特别说明外，都先以不受限上下文写入数据，再切换 `IDataScopeContext.Current` 查询。`Restricted` 是 `DataScopeResult.Restricted`。

```csharp
// Shared_row_is_visible_to_every_org
// 数据：{Org=10, IsShared=true}、{Org=10, IsShared=false}、{Org=10, IsShared=null}
Restricted([10], false, 0) → 3 行；Restricted([20], false, 0) → 只有共享行

// Shared_row_without_org_is_visible_to_every_org
// 数据：{Org=null, IsShared=true}
Restricted([20], false, 0) → 1 行

// Unshared_row_without_org_stays_hidden
// 数据：{Org=null, IsShared=null, CreateUserId=7}
Restricted([20], false, 0) → 0 行；Restricted([], true, 7) → 1 行

// Shared_row_is_visible_under_empty_scope
Restricted([], false, 0) → 只有共享行

// ClearFilter_of_IOrgScoped_does_not_clear_IOrgShared
// 数据：{Org=20, IsShared=null}；范围 Restricted([10], false, 0)
ClearFilter<IOrgScoped>() → 0 行；ClearFilter<IOrgScoped, IOrgShared>() → 1 行

// DataEntity_is_not_shareable
EntityMaintenance.GetEntityInfo<FooPrivate>().Columns 不含 "IsShared"
FooPrivate 仍实现 IOrgScoped、不实现 IOrgShared；OrgAuditEntity 反之

// Join_of_two_shared_tables_filters_both
// 数据：父 {Org=10, IsShared=true}、子 {Org=10, IsShared=null}、子 {Org=10, IsShared=true}
// 用 Queryable<FooShare>().InnerJoin<FooShareChild>((p, c) => c.ParentId == p.Id).Select((p, c) => c.Title)
Restricted([20], false, 0) → 只有共享子行，且没有列名歧义错误

// Join_of_private_and_shared_tables_applies_both_filters
// 用 Queryable<FooPrivate>().InnerJoin<FooShare>((p, s) => s.Title == p.Title)，范围 Restricted([10], false, 0)
// 主表被挡：Title="a"，FooPrivate {Org=20}、FooShare {Org=20, IsShared=true}
// 联表被挡：Title="b"，FooPrivate {Org=10}、FooShare {Org=20, IsShared=null}
不清过滤器 → 0 行；ClearFilter<IOrgScoped, IOrgShared>() → 2 行
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*OrgShareTests*"`
Expected: 编译失败，提示 `IsShared` 和 `IOrgShared` 不存在。

- [ ] **Step 3: 实现接口、字段和过滤器**

在 `DataEntity.cs` 声明 `IOrgShared`，改 `OrgAuditEntity`。
`DataEntity.cs:36` 和 `:54` 的 null 说明改成真实语义：受限用户看不到 `CreateOrgId` 为空的行，只有范围含「仅本人」的创建人和不受限用户能看到。

在 `SqlSugarSetup.cs` 的 `policy.ApplyDataScopeFilter` 分支追加：

```csharp
client.QueryFilter.AddTableFilter<IOrgShared>(e =>
    dataScope.Current.IsUnrestricted == true
    || (e.CreateOrgId != null && dataScope.Current.OrgIds.Contains(e.CreateOrgId.Value))
    || (dataScope.Current.IncludeSelf == true && e.CreateUserId == dataScope.Current.UserId)
    || e.IsShared == true);
```

审计 AOP（`:171`）补一个 `IOrgShared { CreateOrgId: null }` 分支，属性名用 `nameof(IOrgShared.CreateOrgId)`。

`OrgAuditEntity` 换接口后，仓储的 `IsOrgScoped` 判定对它变成 false，写守卫会失效，`OrgAuditEntityTests` 的越权写用例会失败。
所以这一步同时在 `SqlSugarRepository` 加 `IsOrgShared` 静态判定（写法同 `IsOrgScoped`），`InScopeAsync` 改成 `IOrgScoped` 或 `IOrgShared` 实体都走带过滤器的查询。
`IsOrgScoped` 本身不改。共享行的归属判断留给 Task 2。

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*OrgShareTests*" --filter-class "*OrgAuditEntityTests*" --filter-class "*DataScopeTests*"`
Expected: 全部通过。`OrgAuditEntityTests` 的断言不改。

如果过滤器不生效，或者 `ClearFilter` 行为与用例不符，**停下来向用户报告**，不要改用别的写法绕过。设计依赖这些假设。

- [ ] **Step 5: 提交**

```bash
git add docs/superpowers/specs/2026-10-30-org-shared-data-design.md docs/superpowers/plans/2026-10-30-org-shared-data.md backend/src/SmartAdmin.SqlSugar/Entities/DataEntity.cs backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs backend/src/SmartAdmin.SqlSugar/Repository/SqlSugarRepository.cs backend/tests/SmartAdmin.Tests/OrgShareTests.cs backend/tests/SmartAdmin.Tests/OrgAuditEntityTests.cs
git commit -m "feat(sqlsugar): OrgAuditEntity 支持按行共享给所有机构"
```

---

### Task 2: 归属判断与写守卫

**Files:**
- Modify: `backend/src/SmartAdmin.Core/Security/DataScopeResult.cs`
- Modify: `backend/src/SmartAdmin.SqlSugar/Repository/SqlSugarRepository.cs:14-18`、`:25-27`、`:39-46`、`:71`、`:78`
- Modify: `backend/src/SmartAdmin.SqlSugar/Repository/IRepository.cs`（`UpdateAsync`、`DeleteAsync`、`HardDeleteAsync` 的注释补上共享行抛 `OrgOutOfScope`）
- Modify: `backend/tests/SmartAdmin.Tests/OrgShareTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IOrgShared`、`FooShare`、`BuildSp`
- Produces: `public bool Owns(long? createOrgId, long? createUserId)`，定义在 `DataScopeResult`
- Produces: `SqlSugarRepository<TEntity>(ISqlSugarClient db, TimeProvider? time = null, ICurrentUser? currentUser = null, IDataScopeContext? dataScope = null)`

- [ ] **Step 1: 写失败的测试**

```csharp
// Owns_matches_the_filter（[Theory]，每组同时断言 Owns 和实际查询可见性，数据为 IsShared=null）
Unrestricted, org=10, user=1                → true
Restricted([10], false, 0), org=10, user=1  → true
Restricted([10], false, 0), org=20, user=1  → false
Restricted([10], false, 0), org=null, user=1 → false
Restricted([], true, 7), org=20, user=7     → true
Restricted([], true, 7), org=20, user=8     → false

// Owner_can_update_and_delete_shared_row
// {Org=10, IsShared=true}，范围 Restricted([10], false, 0)
UpdateAsync → 1；DeleteAsync → 1；删除后查不到

// Parent_org_scope_can_write_child_org_shared_row
// {Org=11, IsShared=true}，范围 Restricted([10, 11], false, 0)
UpdateAsync → 1

// Non_owner_write_to_shared_row_throws
// {Org=10, IsShared=true}，范围 Restricted([20], false, 0)
GetByIdAsync 非空；UpdateAsync、DeleteAsync、HardDeleteAsync 都抛 AdminException，Code == ErrorCode.OrgOutOfScope
切回不受限后，行内容未变

// Non_owner_write_to_private_row_returns_zero
// {Org=10, IsShared=null}，范围 Restricted([20], false, 0)
UpdateAsync → 0；DeleteAsync → 0；不抛异常

// Shared_row_without_org_is_writable_only_by_creator_or_unrestricted
// {Org=null, IsShared=true, CreateUserId=7}
Restricted([20], false, 0) 写入抛 OrgOutOfScope
Restricted([], true, 7) 写入 → 1；Unrestricted 写入 → 1

// Owner_edit_keeps_share_flag（Review Focus 1）
// {Org=10, IsShared=true}，范围 Restricted([10], false, 0)
GetByIdAsync → 改 Title → UpdateAsync → 重新读取，IsShared == true 且 Title 已变

// Turning_share_off_hides_row_from_other_orgs（Review Focus 2）
// 归属机构读出后置 IsShared=false 并保存
Restricted([20], false, 0) → 0 行

// Repository_without_scope_context_refuses_shared_entity_writes
new SqlSugarRepository<FooShare>(db).UpdateAsync(row) 抛 InvalidOperationException，DeleteAsync 同样
new SqlSugarRepository<FooPrivate>(db) 的行为不变，不抛这个异常
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*OrgShareTests*"`
Expected: 编译失败，提示 `Owns` 不存在。

- [ ] **Step 3: 实现 `DataScopeResult.Owns`**

```csharp
public bool Owns(long? createOrgId, long? createUserId) =>
    IsUnrestricted
    || (createOrgId is { } org && OrgIds.Contains(org))
    || (IncludeSelf && createUserId == UserId);
```

- [ ] **Step 4: 实现写守卫**

构造函数末尾加 `IDataScopeContext? dataScope = null`。`IsOrgShared` 静态判定在 Task 1 已加。
类注释 `:14-16` 把新参数列进“必须保持可选”的说明。

`InScopeAsync` 对 `IOrgShared` 实体按下面的顺序走，其余实体保持原逻辑：

1. `dataScope` 为 null：抛 `InvalidOperationException`，消息说明要把 `IDataScopeContext` 传给构造函数。
2. 用带过滤器的查询按 Id 取行，取不到返回 `false`。
3. `Owns` 为真返回 `true`；否则 `throw new AdminException(Core.ErrorCode.OrgOutOfScope)`。

注释里说明第 3 步为什么抛错：共享行能通过 `GetByIdAsync`，静默返回 0 会让忽略返回值的业务代码以为保存成功。

- [ ] **Step 5: 运行测试，确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*OrgShareTests*" --filter-class "*OrgAuditEntityTests*" --filter-class "*DataScopeTests*" --filter-class "*ReplaceabilityTests*"`
Expected: 全部通过。

- [ ] **Step 6: 提交**

```bash
git add backend/src/SmartAdmin.Core/Security/DataScopeResult.cs backend/src/SmartAdmin.SqlSugar/Repository/SqlSugarRepository.cs backend/src/SmartAdmin.SqlSugar/Repository/IRepository.cs backend/tests/SmartAdmin.Tests/OrgShareTests.cs
git commit -m "feat(sqlsugar): 共享行只允许归属机构写入"
```

---

### Task 3: 种子升级与老库补列

**Files:**
- Modify: `backend/src/SmartAdmin.SqlSugar/DatabaseInitializer.cs:413-418`
- Modify: `backend/src/SmartAdmin.SqlSugar/Entities/SysSchemaVersion.cs:17`
- Create: `backend/tests/SmartAdmin.TestHost/SampleOrgDoc.cs`
- Modify: `backend/tests/SmartAdmin.TestHost/Program.cs:13`
- Modify: `backend/tests/SmartAdmin.Tests/SeedUpgradeTests.cs`
- Modify: `backend/tests/SmartAdmin.Tests/CodeFirstNullableUpgradeTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IOrgShared`
- Produces（TestHost）：`SampleOrgDoc : OrgAuditEntity`（表 `sample_org_doc`，`Title`）；`SampleOrgDocSeed : ISeedData<SampleOrgDoc>`，`SyncOnUpgrade => true`，`HasData` 返回一行 `{ Id = SmartSeedIds.ConsumerMin + 20, Title = "种子标题" }`

- [ ] **Step 1: 加 TestHost 样例，写失败的测试**

建 `SampleOrgDoc.cs`，并在 `Program.cs:13` 后注册 `SampleOrgDocSeed`，写法同 `SampleWidgetDetailSeed`。

```csharp
// SeedUpgradeTests.Upgrade_keeps_share_flag_and_owner_org
// 沿用 RestartWithAsync + DowngradeVersionAsync
// tamper：把种子行改成 Title="用户改过", IsShared=true, CreateOrgId=77
// assert：Title == "种子标题"（确实发生了同步）；IsShared == true；CreateOrgId == 77

// CodeFirstNullableUpgradeTests.Existing_org_audit_table_gets_nullable_IsShared（Review Focus 5）
// 沿用该文件的两次启动写法：
// 首启插入一行 SampleOrgDoc { CreateOrgId = 10 }，然后 db.DbMaintenance.DropColumn("sample_org_doc", "IsShared")
// 二次启动后：
Assert.Contains("IsShared", 列名集合);
老行 IsShared == null;
```

这个文件的宿主没有 HTTP 上下文，范围固定为不受限，设不了受限范围，所以不在这里断言可见性。


- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*SeedUpgradeTests*" --filter-class "*CodeFirstNullableUpgradeTests*"`
Expected: `Upgrade_keeps_share_flag_and_owner_org` 失败：`OrgAuditEntity` 已不实现 `IOrgScoped`，种子升级会把 `IsShared` 和 `CreateOrgId` 都刷掉。`Existing_org_audit_table_gets_nullable_IsShared` 此时已通过：CodeFirst 本来就会补列，这条只是锁住行为。

- [ ] **Step 3: 实现种子忽略列，升架构版本**

`DatabaseInitializer.cs` 的 `ignored`：`IOrgScoped` 实体沿用现有四列；`IOrgShared` 实体忽略这四列再加 `nameof(IOrgShared.IsShared)`；其他实体不变。
`:413` 的注释同步改成两种接口都覆盖。

`SysSchemaVersion.Current` 从 `"7"` 改成 `"8"`。
它的注释补一句：消费方表继承的基类加列时同样要升。
`SystemMenuRoleGrantCleanup` 按 `from >= 7` 跳过，这次升级不会让它重跑，`SystemMenuRoleGrantTests` 里写死 `"7"` 的用例不用改。

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*SeedUpgradeTests*" --filter-class "*CodeFirstNullableUpgradeTests*" --filter-class "*CodeFirstVersionTests*" --filter-class "*SeedContractTests*"`
Expected: 全部通过。

- [ ] **Step 5: 跑完整后端测试**

Run: `dotnet test backend/SmartAdmin.slnx`
Expected: 全部通过。如果新增 TestHost 表让统计表数或实体数的测试失败，先确认原因；只有断言统计的就是 TestHost 表清单时才更新。

- [ ] **Step 6: 提交**

```bash
git add backend/src/SmartAdmin.SqlSugar/DatabaseInitializer.cs backend/src/SmartAdmin.SqlSugar/Entities/SysSchemaVersion.cs backend/tests/SmartAdmin.TestHost/SampleOrgDoc.cs backend/tests/SmartAdmin.TestHost/Program.cs backend/tests/SmartAdmin.Tests/SeedUpgradeTests.cs backend/tests/SmartAdmin.Tests/CodeFirstNullableUpgradeTests.cs
git commit -m "fix(seed): 种子升级不覆盖共享标记,架构版本升到 8"
```

---

### Task 4: 文档、CI 子集与升级说明

**Files:**
- Modify: `backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs:137`、`:168-170`
- Modify: `backend/src/SmartAdmin.SqlSugar/Repository/SqlSugarRepository.cs:25`、`:40-43`
- Modify: `backend/src/SmartAdmin.SqlSugar/Entities/BaseEntity.cs:46`
- Modify: `backend/src/SmartAdmin.SqlSugar/Entities/DataEntity.cs`（类型注释）
- Modify: `backend/src/SmartAdmin.Services/Security/IDataScopeGuard.cs:9`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs:132`
- Modify: `backend/src/SmartAdmin.Core/Options/AdminDatabaseConnectionOptions.cs:30`
- Modify: `site/zh/backend/data-scope.md:96,108,126,166`（另加新节）、`site/backend/data-scope.md`（对应位置）
- Modify: `site/zh/backend/data-layer.md:25,39,51,63,85,96-97,106,110`、`site/backend/data-layer.md`（对应位置）
- Modify: `site/zh/standard/backend.md:26,60`、`site/standard/backend.md`（对应位置）
- Modify: `skills/create-entity.md:12-15,144-146`
- Modify: `backend/CLAUDE.md`（Data layer conventions）
- Modify: `CLAUDE.md:92`
- Modify: `.github/workflows/ci.yml:178`
- Modify: `scripts/ci-local.ps1:98-104`
- Modify: `CHANGELOG.md:15`（`## Unreleased`）

**Interfaces:**
- Consumes: Task 1-3 的全部公开名字

- [ ] **Step 1: 改代码注释**

把上面列出的注释里 `OrgAuditEntity` 属于 `IOrgScoped` 的说法改掉，说明 `DataEntity` 用 `IOrgScoped`，`OrgAuditEntity` 用 `IOrgShared`。
只写现状和原因。

- [ ] **Step 2: 写站点文档**

先读 `skills/write-docs.md`。先写中文，再翻译英文。

`data-scope.md` 新增一节，内容来自规格：

- `IsShared` 的查询规则表（四种组合）。
- 谁能写共享行，共享行对空范围用户也可见，接口权限照常校验。
- 明细表不带机构字段、接口先校验主表。
- 共享行不要引用私有数据，由业务保存时校验。
- 绕过仓储直接用 `Db.Updateable` 不受写守卫约束。
- `ClearFilter<IOrgScoped, IOrgShared>()` 的用法。

`data-layer.md` 的继承图、基类表和写路径守卫改成两种接口。
`:85` 及英文对应句修正 null 的说明。
`standard/backend.md`、`skills/create-entity.md` 的基类选型加上共享能力。
`backend/CLAUDE.md` 的数据层约定补一句 `IOrgShared`。

- [ ] **Step 3: 检查站点文案**

Run: `cd site && npm run lint:prose -- zh/backend/data-scope.md backend/data-scope.md zh/backend/data-layer.md backend/data-layer.md zh/standard/backend.md standard/backend.md`
Expected: 无报错。

- [ ] **Step 4: 加入 SqlServer 子集**

把 `--filter-class "*OrgShareTests*"` 加进 `ci.yml:178` 和 `scripts/ci-local.ps1:98-104`，两边保持一致。
`CLAUDE.md:92` 的子集列表补上共享行过滤。
用不改变编码的方式编辑 `ci-local.ps1`，它必须保留 UTF-8 BOM。

Run: `powershell -NoProfile -Command "$b=[IO.File]::ReadAllBytes('scripts/ci-local.ps1'); '{0:X2}{1:X2}{2:X2}' -f $b[0],$b[1],$b[2]"`
Expected: `EFBBBF`

- [ ] **Step 5: 写 CHANGELOG**

在 `## Unreleased` 下写 `### 升级说明` 和 `### Added`，并在段落顶部加粗提示这是破坏性变更。

升级说明写规格“这是破坏性变更”一节的三步，再加一条：只传 `db` 的 `SqlSugarRepository` 子类用在 `OrgAuditEntity` 上时，要把 `IDataScopeContext` 传给构造函数。
加列那一步要写明：CodeFirst 只扫主库，副库上的 `OrgAuditEntity` 表也要手工加列。

不写任何消费方项目的名字和统计数字。

- [ ] **Step 6: 跑文档闸门**

Run: `ci.bat -Stage docs`
Expected: docs 阶段全绿。完整闸门和方言测试耗时很长，由主控在所有任务完成后统一运行。

- [ ] **Step 7: 提交**

```bash
git add backend/src site skills/create-entity.md backend/CLAUDE.md CLAUDE.md .github/workflows/ci.yml scripts/ci-local.ps1 CHANGELOG.md
git commit -m "docs(data-scope): 说明共享行规则和 OrgAuditEntity 升级步骤"
```

提交前用 `git status` 确认 `backend/src` 和 `site` 下没有带进别的改动。
