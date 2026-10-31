# 机构共享数据设计

## 要解决的问题

多机构部署下，一张业务表里大部分数据归各机构私有，个别数据要让所有机构都能看到。
典型例子是工艺路线：A、B 两个事业部各有自己的路线，但辅料管理路线是公用的。

内核现在表达不了这种需求：

- 全局过滤器只判断 `CreateOrgId ∈ 我能看的机构`（`SqlSugarSetup.cs:143`），每行只属于一个机构。
- 数据范围挂在角色上，每个角色一条（`SysRoleDataScope`），对所有业务表一刀切。

于是为了看到一条公用路线，只能把角色范围开成「全部」。
这样该角色在所有业务表上都能看到别的机构的数据，隔离就没了。

## 目标

- 继承 `OrgAuditEntity` 的表自动带共享能力，业务代码不用换基类。
- 同一张表里，私有行和共享行并存，按行决定。
- 共享行所有机构可读，只有归属机构可写。
- 不需要改任何角色的数据范围。
- 升级后存量数据的可见范围不变。

## 不做的事

- `DataEntity` 不带共享能力，也不加列。
- 只共享给部分机构（需要关系表，每次查询多一次关联，当前场景用不上）。
- 别的机构修改共享数据。
- 像金蝶分配型那样，给每个机构复制一份数据。
- 内核内置表使用共享，内核前端页面不做改动。

## 绝不能发生

- `DataEntity` 表的结构、可见性或写入行为发生任何变化。
- 把 `CreateOrgId` 为空当成共享。存量空值行（系统数据、无机构用户创建的数据）会立刻对所有人可见。
- 别的机构能改、删共享行。
- 升级后，未设共享的 `OrgAuditEntity` 行可见范围变大。

## 行为

以 A 机构的路线为例，B 机构用户是受限范围：

| 操作 | A 机构 | B 机构 |
|---|---|---|
| 查询 A 的私有行 | 可见 | 不可见 |
| 查询 A 的共享行 | 可见 | 可见 |
| 更新、删除 A 的共享行 | 允许 | 抛 `ErrorCode.OrgOutOfScope` |
| 更新、删除 A 的私有行 | 允许 | 返回 0 行（现有语义） |
| 打开或关闭共享标记 | 允许 | 同更新，被拒 |

`IsShared = true` 时，不管 `CreateOrgId` 有值还是为空，所有机构都能查到。
`CreateOrgId` 为空的共享行没有归属机构，受限用户里只有范围含「仅本人」的创建人能写，其他人写入时抛 `ErrorCode.OrgOutOfScope`。

`IsShared` 为空或 `false` 时，可见性与现在完全相同：`CreateOrgId` 在范围内才可见；`CreateOrgId` 为空的行，受限用户里只有范围含「仅本人」的创建人能看到。

「全部」范围与超管不受限，全部允许。
后台任务、启动期这类无 HTTP 上下文的调用同样不受限，沿用 `HttpContextDataScopeContext` 现有语义。

共享行对所有受限用户可见，包括「仅本人」范围和未绑定范围时的空范围回退。
接口权限照常校验，共享只放开数据范围这一层。
所以“空范围查不到任何数据”今后只对 `IOrgScoped` 表成立。

`OrgAuditEntity` 是物理删除，删除共享行后所有机构都看不到了，也无法恢复。
它不进回收站（`RecycleBinType<TEntity>` 约束 `BaseEntity`），所以不用处理已删除共享行的可见性。

## 内核改动

### 接口与基类（`SmartAdmin.SqlSugar/Entities/DataEntity.cs`）

```csharp
public interface IOrgShared
{
    long? CreateOrgId { get; }
    long? CreateUserId { get; }
    bool? IsShared { get; }
}

public abstract class OrgAuditEntity : AuditEntity, IOrgShared   // 不再实现 IOrgScoped
{
    [SugarColumn(IsNullable = true, ColumnDescription = "归属机构 Id(数据范围锚点)")]
    public long? CreateOrgId { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "是否共享给所有机构")]
    public bool? IsShared { get; set; }
}
```

`DataEntity` 继续实现 `IOrgScoped`，一行不动。

`OrgAuditEntity` 必须换接口，原因有两个：

- 过滤器按接口匹配，读 `IsShared` 的过滤器要求该接口的所有实现表都有这一列，而 `DataEntity` 不加列。
- 同一实体上的多个全局过滤器是 AND 关系，保留 `IOrgScoped` 的话，共享行会先被原过滤器挡掉。`IOrgShared` 也不能继承 `IOrgScoped`，因为 SqlSugar 用 `GetInterfaces()` 匹配，继承来的接口同样算匹配。

`IsShared` 用可空列，因为给有数据的表加列必须可空（`CodeFirstNullableUpgradeTests`）。
`null` 与 `false` 都表示不共享。

### 查询过滤器（`SqlSugarSetup.cs`）

在 `policy.ApplyDataScopeFilter` 分支里，给 `IOrgShared` 再注册一个过滤器：

```csharp
client.QueryFilter.AddTableFilter<IOrgShared>(e =>
    dataScope.Current.IsUnrestricted == true
    || (e.CreateOrgId != null && dataScope.Current.OrgIds.Contains(e.CreateOrgId.Value))
    || (dataScope.Current.IncludeSelf == true && e.CreateUserId == dataScope.Current.UserId)
    || e.IsShared == true);
```

### 归属判断（`SmartAdmin.Core/Security/DataScopeResult.cs`）

新增方法，写守卫用它判断，业务服务也可以用它给 DTO 算“能否编辑”：

```csharp
public bool Owns(long? createOrgId, long? createUserId) =>
    IsUnrestricted
    || (createOrgId is { } org && OrgIds.Contains(org))
    || (IncludeSelf && createUserId == UserId);
```

它必须与过滤器前三个分支含义一致，由测试锁住。

### 写守卫（`SqlSugarRepository.cs`）

`IsOrgScoped` 判定保持不变，`IOrgScoped` 实体的路径一行不动。
新增 `IsOrgShared` 判定，让 `InScopeAsync` 对 `IOrgShared` 实体这样走：

1. 目标行经过滤器查不到：返回 false，调用方返回 0 行。
2. 查到且 `Owns` 为真：返回 true。
3. 查到但 `Owns` 为假：抛 `ErrorCode.OrgOutOfScope`。

第 3 步抛错而不是返回 0，因为共享行能通过 `GetByIdAsync`。
如果 `UpdateAsync` 静默返回 0，忽略返回值的业务代码会以为保存成功。

仓储要读当前范围，构造函数末尾加可选参数 `IDataScopeContext? dataScope = null`。
`IOrgShared` 实体的仓储拿不到它时，写操作抛 `InvalidOperationException`，不能静默放行。
`: SqlSugarRepository<T>(db)` 这种只传 `db` 的子类如果用在 `OrgAuditEntity` 上，要补传这个参数，写进升级说明。

### 其余要兼容新接口的位置

- `SqlSugarSetup.cs:171`：审计 AOP 同样自动填充 `CreateOrgId`。
- `DatabaseInitializer.cs:416`：种子升级时同样忽略 `CreateOrgId`，并且忽略 `IsShared`，否则升级会把用户打开的共享刷回去。
- `SysSchemaVersion.Current` 从 `"7"` 升到 `"8"`：它参与 CodeFirst 门控键，不升的话钉了 `CodeFirstVersion` 的消费方不会重新扫描，也就不会加列。

## 这是破坏性变更

`OrgAuditEntity` 不再实现 `IOrgScoped`，消费方升级时要做三件事，都写进 `CHANGELOG.md` 的升级说明。

### 1. 替换 `ClearFilter<IOrgScoped>()`

`ClearFilter` 按过滤器注册的接口精确匹配（`RemoveFilters.Contains(it.type)`）。
升级后，它在 `OrgAuditEntity` 上清不掉 `IOrgShared` 的过滤器。
**编译照常通过，不报错，但查询结果会悄悄受到机构范围限制**，比如全库判重查不到别的机构的行。

全局替换即可，SqlSugar 5.1.4.219 有这个重载，用在 `DataEntity` 上也无害：

```csharp
.ClearFilter<IOrgScoped>()  →  .ClearFilter<IOrgScoped, IOrgShared>()
```

已经写成 `ClearFilter<ISoftDelete, IOrgScoped>()` 的，同样把 `IOrgShared` 加进去。

### 2. 给所有 `OrgAuditEntity` 表加列

开了 CodeFirst 的环境启动时自动加列。
关了 CodeFirst 的环境（生产默认关闭）**必须先手工执行 `ALTER TABLE`**，否则主库会被启动期的缺列检查拦下。
CodeFirst 只扫主库，副库上的 `OrgAuditEntity` 表同样要手工加列，副库不在启动检查范围内，漏了要到第一次查询才报错。

### 3. 更新测试宿主

消费方测试里自己注册的 `AddTableFilter<IOrgScoped>(...)` 不再作用于 `OrgAuditEntity`。
要再注册一个 `AddTableFilter<IOrgShared>(...)`，否则相关隔离测试测的就不是真实行为。

## 业务怎么接入

```csharp
public class ProcessRoute : OrgAuditEntity { ... }   // 基类不变
route.IsShared = true;                               // 设为共享
```

业务方要遵守两条约定，内核拦不了：

- **明细表不带机构字段（继承 `PrimaryId` 或 `BaseEntity`），接口先校验主表。** 过滤器按表生效，明细如果也带机构字段，别的机构能看到主表却看不到明细。
- **共享行不要引用某个机构的私有数据。** 比如共享路线引用了 A 的私有工作中心，B 打开时这部分查不到。内核不知道业务外键，由业务保存时校验。

## 已发现的问题

这些是读代码时发现的现有问题，都还没有实际验证：

1. **`DataEntity` 软删除后可能无法彻底删除。** `HardDeleteAsync` 经 `InScopeAsync` 判断，而那个查询带软删过滤器，已删除的行查不到，于是返回 0（`SqlSugarRepository.cs:45`、`:102`）。现有测试只覆盖 `BaseEntity` 类型的回收站。
2. **回收站彻底删除的顺序有风险。** `RecycleBinType.PurgeAsync` 先跑 `BeforePurgeAsync` 清关联数据，再过写守卫（`RecycleBinType.cs:80`）。如果守卫拒绝，关联数据已经被清掉了。
3. **文档与行为不一致。** `DataEntity.cs:36`、`DataEntity.cs:54` 和 `data-layer.md:85` 写着“为 null 表示不受机构范围约束”，实际上受限用户看不到这些行（`DataScopeTests.cs:53`）。

第 3 条随本次一起修。
第 1、2 条只涉及 `DataEntity` 和回收站，与本功能无关，不在本次范围内，另行处理。

## 验证

第一步先写测试验证下面的假设，都成立再改其余部分：

- `IOrgShared` 上的过滤器能生效，且不受 `IOrgScoped` 过滤器影响。
- `ClearFilter<IOrgScoped>()` 清不掉它，`ClearFilter<IOrgScoped, IOrgShared>()` 能清掉。匹配规则已经从 SqlSugar 源码确认，这条测试用来锁住行为。
- `bool?` 的 `== true` 比较在 SQLite、MySQL、PostgreSQL、SqlServer 上都正常，`null` 不算共享。

如果有假设不成立，停下来重新讨论，不要改用别的写法绕过。

新增测试类，比如 `OrgShareTests`，覆盖：

- “行为”一节表格的每一行。
- 存量 `null` 行不共享；`DataEntity` 实体的查询和写入行为不变，表里没有 `IsShared` 列。
- `Owns` 与过滤器结果一致。
- 种子升级不覆盖 `IsShared` 和 `CreateOrgId`。
- 仓储拿不到 `IDataScopeContext` 时，`IOrgShared` 实体的写操作抛错。

`OrgAuditEntityTests` 现有用例必须原样通过，只更新注释里提到 `IOrgScoped` 的地方。
`OrgShareTests` 加入 `ci.yml` 和 `scripts/ci-local.ps1` 的 SqlServer `--filter-class` 子集。
`scripts/ci-local.ps1` 要保留 UTF-8 BOM。

## 文档与发布

- `site/zh/backend/data-scope.md`、`site/zh/backend/data-layer.md`、`site/zh/standard/backend.md` 写明共享的用法和约定，再同步英文页。按 `skills/write-docs.md` 的规矩写。
- 更新 `skills/create-entity.md` 的基类选型，以及 `backend/CLAUDE.md` 的数据层约定。
- 更新代码注释里把 `OrgAuditEntity` 归到 `IOrgScoped` 的地方。
- `CHANGELOG.md` 记新功能和破坏性变更，附“这是破坏性变更”一节的三步升级说明。

## 留给实施者决定

- `InScopeAsync` 的具体写法。
- 过滤器与 `Owns` 的注释措辞、测试的组织方式。

## 决策记录

- **别的机构只看和引用，不修改。** 能编辑共享行的是数据范围包含其归属机构的用户，规则与现有机构数据一致。
- **能力放在 `OrgAuditEntity`，`DataEntity` 不加列。** 代价是 `OrgAuditEntity` 要换接口，消费方要替换 `ClearFilter`。
- 不新增基类：业务表继续继承 `OrgAuditEntity`，不用为共享换基类。代价是所有 `OrgAuditEntity` 表都多一列 `IsShared`，不用共享的表这一列一直是 `null`。
- 不把字段放进 `IOrgScoped`：那样 `ClearFilter` 不用改，但 `DataEntity` 表也会多一列。
- 不用共享关系表：那样任何业务表都不用加列，但所有受限查询永远多一个 `OR EXISTS`，也没有哪个目标需要它。
- 用独立的共享字段，不用空机构表示共享，这样能保留“归属机构负责维护”。金蝶共享型的语义相同：创建组织维护和使用，其他组织只能使用。
- 版本号由发版时决定。项目规定破坏性变更尽量并入 .NET 大版本，这次是否等到大版本，由维护者定。
