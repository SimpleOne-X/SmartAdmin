# 多租户(共享库模式)设计

日期:2026-09-15
范围:内核新增租户维度(`ITenantScoped`/`SysTenant`)、机构/用户/角色/菜单授权/业务数据按租户硬隔离、登录链路携带租户、"租户管理"顶级菜单目录第一个页面(租户列表)。
不涉及(仅预留数据模型钩子,不实现):二期"独立库/混合模式"的动态连接路由、CodeFirst 自动建库、跨租户排障视角。
关联 ADR:`docs/adr/0010-multi-tenancy-shared-database.md`(反转 `rebuild-design.md` 原"不做多租户"非目标)。
可视化设计稿(菜单放置 + 平台管理员/租户内用户视角切换 + 明暗主题):<https://claude.ai/artifact/7WZTGofLYXK57m1aaaKXJt>
前置调研(未提交,评估阶段草稿):`docs/plans/kernel-consistency-and-modernization.md` 第 2 节。

## 1. 背景与决策摘要

`rebuild-design.md` 把多租户明确列为非目标("不做,不是推迟,是整体不做")。评估阶段确认现状:`IOrgScoped`/`DataEntity` 机构数据权限范式成熟但**零个内置实体在用**(全部 `Sys*` 继承 `BaseEntity`,机构范围靠 `IDataScopeGuard` 对系统表手工叠加 `Where`);`IRepository<>` 恒打主库;multi ConfigId(`AdditionalDatabases`)是启动时静态挂载,官方文档原话"内核不会按租户自动切库";四个实体基类零 `TenantId` 预留。三处硬伤均已用 file:line 核实(见前置调研)。

以下四项经用户确认,是本设计的硬约束,不再讨论:

1. **隔离模型**:租户是与机构树正交的独立维度,不复用"机构及以下"数据范围模拟。隔离过滤器对所有角色恒定生效,不受机构数据范围(`DataScopeType.All` 等)影响。
2. **租户识别**:账号固定归属一个租户。登录只需账号密码,后端按账号反查所属租户;**账号继续全平台唯一**(不改成 `(TenantId, Account)` 联合唯一)——这意味着两个不同租户不能各自注册同名账号,是本设计的已知权衡(见 §9)。
3. **平台级配置范围**:字典(`SysDict`)、系统参数配置(`SysConfig`)、AI 服务商/模型配置继续全平台共享,不按租户隔离。
4. **二期是"混合模式",不是"整租户搬家"**:参照 SqlSugar 官方 SAAS 分库文档(donet5.com "仓储+多租户"/"SAAS 分库"两页核实)的"基础信息库(组织架构/用户/权限)+ 业务库(按租户分库)"划分——二期只让**消费方自己的业务实体**可选路由到独立连接串,内核自身的机构/用户/角色/菜单授权永远留在共享库、按 `TenantId` 过滤,不因租户选了"独立库"模式而搬家。

## 2. 数据模型

### 2.1 `ITenantScoped` + 新实体基类层级

不改动现有四个基类(`PrimaryId`/`AuditEntity`/`BaseEntity`/`DataEntity`/`OrgAuditEntity`),避免影响已经用它们定义实体的消费方。新增一个平行层级,新文件 `backend/src/SmartAdmin.SqlSugar/Entities/TenantEntity.cs`(与 `IOrgScoped` 同放在 `DataEntity.cs` 的既有惯例对齐,即"标记接口与它修饰的基类同文件"):

```csharp
namespace SmartAdmin.SqlSugar;

/// <summary>租户隔离标记。与 IOrgScoped 的区别:这是硬安全边界,过滤器不设"数据范围=全部"式逃逸条件——
/// 机构数据范围回答"同租户内我能看多少",租户隔离回答"我根本不该看见别的租户",两者语义不同,不可共用同一个开关。</summary>
public interface ITenantScoped
{
    long TenantId { get; }
}

/// <summary>审计 + 软删 + 租户隔离,不含机构数据范围。多数内置 Sys* 表(用户/角色/菜单授权等)用这个。</summary>
public abstract class TenantEntity : BaseEntity, ITenantScoped
{
    public long TenantId { get; set; }
}

/// <summary>审计 + 软删 + 机构数据范围 + 租户隔离。需要同时具备"同租户内还要分机构可见范围"能力的实体用这个
/// (如未来把 IOrgScoped 范式真正接到某张表上时)。</summary>
public abstract class TenantDataEntity : DataEntity, ITenantScoped
{
    public long TenantId { get; set; }
}
```

`TenantId` 是 `long`(非空)而非 `long?`——不像 `CreateOrgId` 允许为空,租户归属是硬性的,由 AOP 在插入前自动填充(与 `CreateOrgId` 同一套审计 AOP 机制扩展,写入位置在 `SqlSugarSetup.cs` 现有 AOP 段旁边)。

### 2.2 `SysTenant`(新内置实体)

`backend/src/SmartAdmin.Services/Entities/SysTenant.cs`,命名空间 `SmartAdmin.Services`(实体定义在 Services 层,不在 SqlSugar 层),继承 `BaseEntity`(它自己是隔离边界的根,不实现 `ITenantScoped`,不自我引用),参照 `SysOrg.cs`/`SysConfig.cs` 的写法:

```csharp
[SugarTable("sys_tenant", TableDescription = "租户")]
public class SysTenant : BaseEntity
{
    [SugarColumn(ColumnDescription = "租户编码", Length = 32)]
    public string Code { get; set; } = "";

    [SugarColumn(ColumnDescription = "租户名称", Length = 128)]
    public string Name { get; set; } = "";

    [SugarColumn(ColumnDescription = "联系人", Length = 64, IsNullable = true)]
    public string? ContactName { get; set; }

    [SugarColumn(ColumnDescription = "联系电话", Length = 32, IsNullable = true)]
    public string? ContactPhone { get; set; }

    [SugarColumn(ColumnDescription = "到期时间", IsNullable = true)]
    public DateTime? ExpireTime { get; set; }

    /// <summary>二期预留,一期恒为 Shared,写 Standalone 一律拒绝(见 §8)。</summary>
    [SugarColumn(ColumnDescription = "隔离模式")]
    public TenantIsolationMode IsolationMode { get; set; } = TenantIsolationMode.Shared;

    /// <summary>二期预留:对应 SqlSugar ConfigId,只路由消费方业务库连接,不影响内核自身的机构/用户/角色/菜单数据
    /// (那些永远走主库 + TenantId 过滤,见 §8)。一期恒为 null。</summary>
    [SugarColumn(ColumnDescription = "业务库连接配置Id", Length = 64, IsNullable = true)]
    public string? ConnectionConfigId { get; set; }

    [SugarColumn(ColumnDescription = "启用")]
    public bool Enabled { get; set; } = true;

    [SugarColumn(ColumnDescription = "备注", Length = 256, IsNullable = true)]
    public string? Remark { get; set; }
}
```

`TenantIsolationMode`(`backend/src/SmartAdmin.Core/Security/TenantIsolationMode.cs`,与 `DataScopeType.cs` 同目录):

```csharp
public enum TenantIsolationMode
{
    Shared = 1,     // 共享库,一期唯一可选值
    Standalone = 2, // 独立库(业务数据),二期开放;一期写入即拒绝
}
```

### 2.3 哪些内置实体迁移到 `TenantEntity`

需要从 `BaseEntity` 改成 `TenantEntity`(或 `TenantDataEntity`,如果同时要接机构数据范围)的表:`SysOrg`、`SysUser`、`SysRole`、`SysUserRole`、`SysRoleMenu`、`SysRoleDataScope`、`SysPosition`,以及通知/文件/登录日志/操作日志/异常日志/在线会话对应的实体(**具体类名待实施计划阶段逐个核对**——前置调研未穷举这几张表的确切实体类名,不能凭印象写)。

**保持 `BaseEntity`,不迁移**(平台级共享,对应 §1 决策 3):`SysMenu`(内置功能地图,全租户统一结构)、`SysDict`/`SysDictItem`、`SysConfig`、AI 相关实体(`SysAiProvider`/`SysAiModel`/`SysAiUsageLog`)、`SysTenant` 自身。

**消费方自己的业务表**:不强制。`create-entity` 技能补一节:想要租户隔离就把基类换成 `TenantEntity`/`TenantDataEntity` 或自己实现 `ITenantScoped`,不想要就不动——这是 opt-in 能力,不是破坏性变更。

### 2.4 账号唯一性(不变)

`SysUser.Account` 保持全局唯一索引不变(不改成 `(TenantId, Account)` 联合唯一)。登录仍是"按 Account 查用户"这一条路径,查到后带出 `TenantId`。已知权衡见 §9。

## 3. 隔离边界:全局过滤器

`backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs` 的 `AttachHooks` 局部函数(现有 `ISoftDelete`/`IOrgScoped` 过滤器所在处,约 126-227 行)新增第三个过滤器:

```csharp
if (policy.ApplyTenantFilter)
{
    client.QueryFilter.AddTableFilter<ITenantScoped>(e => e.TenantId == currentTenant.Current);
}
```

**刻意不设 `Unrestricted` 逃逸条件**——这是与 `IOrgScoped` 过滤器最重要的区别,理由见 §2.1 接口注释。`currentTenant.Current` 的具体读取方式(是否需要一个新的 `ITenantContext`,还是直接扩展 `ICurrentUser`)见 §4,精确表达式留到实施阶段核对 `ICurrentUser` 现有实现后再定稿。

`ApplyTenantFilter` 加入 `AdminDatabaseConnectionOptions` 的三个 opt-in 布尔旁边(`ApplySoftDeleteFilter`/`ApplyDataScopeFilter`/`ApplyAuditAop`),主库固定开启(`HookPolicy.ForMain`)。

**这部分改动只能落在内核内部**:`SqlSugarSetup` 是 `static class`,`AddSmartAdminSqlSugar` 是 `static` 扩展方法,消费方在升级前无法通过常规扩展点自行加装等价能力——唯一的"整体替换"路径(`AddSmartAdmin()` 之前整包 `TryAddSingleton<ISqlSugarClient>`)会导致这个过滤器不生效,必须在消费方升级文档里显著提示(见 §9)。

## 4. 认证与 JWT

`backend/src/SmartAdmin.Core/Security/ITokenProvider.cs`:

- `TokenClaimNames` 新增 `TENANT_ID = "tid"`(与现有 `SESSION_ID`/`SUPER_ADMIN`/`ORG_ID`/`API_KEY` 并列)。
- `TokenSubject` record 新增 `long? TenantId`(可空——平台管理员没有租户,见 §5)。

`backend/src/SmartAdmin.AspNetCore/Security/JwtTokenProvider.cs` 的 `BuildClaims`(已是 `protected virtual`,49-58 行)按现有 `sadm`/`org` 的写法加一行:`TenantId` 非空时塞 `tid` claim。

`backend/src/SmartAdmin.Services/Auth/AuthService.cs` 的 `ValidateUserAsync`(81-95 行)查到 `SysUser` 后,连同其他字段一起把 `TenantId` 传给 `CreateTokenAsync`(468-476 行)构造的 `TokenSubject`。

**当前用户读取租户 Id**:大概率扩展 `ICurrentUser`(现有 `OrgId` 的读取方式——直接从 HTTP 请求的 claim 同步读,不涉及异步 DB 解析,不需要像 `IDataScopeContext` 那样搞 `AsyncLocal`/`HttpContext.Items` 双载体,因为那套复杂机制是为了应对"授权过滤器内部 `await` 之后异步解析结果不回流"这个问题,而 `TenantId` 只是个同步可读的 JWT claim)。精确改动点(`SystemCurrentUser`/`HttpContextCurrentUser` 两个实现类的具体文件位置)留到实施阶段核对,前置调研只确认了 DI 注册行,未展开这两个类的字段列表。

## 5. 平台管理员 vs 租户内超管

现有 `sadm`(超管)语义收窄为"**租户内超管**"——绕过 RBAC,但仍然属于某个具体租户(`TenantId` 非空)。

新增"**平台管理员**"概念:`TenantId` 为 `null`,不属于任何租户,是唯一能对 `SysTenant` 做增删改的角色。大概率就是种子里的初始超管账号(`SuperAdminSeed`,`Id=1`)天然充当——不新增一套独立的账号体系。

`ITenantScoped` 过滤器对平台管理员的行为:`currentTenant.Current` 为 `null` 时,过滤谓词恒不成立(查不到任何行),即平台管理员**默认查不到任何租户域数据**,不是"看到所有租户"——这是刻意的最小权限默认值,不是遗漏。"平台管理员代入某租户排障"这类跨租户视角**明确不在本次范围**,留作未来 P1/P2 评估(不新增字段,不占位)。

## 6. 菜单与前端

### 6.1 菜单编号(已实施,已提交)

`backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs` 已完成:全部内置目录整体 Id +100(1xx 工作台不变、2xx 留给租户管理未占行、3xx 组织管理…8xx AI 管理),Sort 也已前置一位,给租户管理让出 Sort=1。本次落地时按以下号段追加(**不预埋尚不存在页面的菜单**这条规矩此前只是"暂不占行",页面/接口真正实现后才能把下面这几行加进种子):

| Id | 类型 | 说明 |
|---|---|---|
| 200 | 目录 | 租户管理,`Icon` 用区别于"组织管理"(`ph:buildings-duotone`)的同风格图标,`Sort=1` |
| 210 | 页面 | 租户列表,`Path=/system/tenant`,`Component=system/tenant/index` |
| 211-215 | 按钮 | 租户-查询/新增/更新/删除/启停(对应列表页 `StatusSwitch`) |

`220`(数据库配置,二期)本次仍不占行。

### 6.2 租户列表页

标准 `SmartTable` + `FormContainer` CRUD,参照 `views/system/user/index.vue` 写法(见 `web/COMPONENTS.md`)。列:租户编码/租户名称/隔离模式(chip)/联系人/联系电话/到期时间/状态(`StatusSwitch`)/创建时间/操作。新增/编辑表单字段:Code(编辑态禁改)/Name/隔离模式(分段控件,"共享库"可选,"独立库"置灰标"二期开放")/联系人/联系电话/到期时间/备注。效果见设计稿(顶部链接)。

### 6.3 不做租户切换器

账号固定归属一个租户(§1 决策 2),对普通用户是透明的,不需要全局视角切换 UI——这点和现有"多应用门户"(同一账号可跨应用)不同。可选:顶栏或个人中心加一个只读的"所属租户"小标签,纯展示不参与交互(设计稿里给了一版,是否要由你在实施前再定)。

### 6.4 store

`stores/auth.ts` 或 `stores/user.ts` 加 `tenantId`/`tenantName` 只读字段,登录响应带出,不新增"当前租户"可写状态。

## 7. CodeFirst / 种子

内核 `SysTenant` 加入 `SmartAdmin.Services` 程序集,随现有 `ApplicationAssemblies` 路径自动进入 CodeFirst 建表,不需要新的扫描逻辑。是否需要 `DefaultTenantSeed`(默认给存量演示数据建一个租户)留到实施计划阶段按需评估——生产环境不应该有预置租户数据,大概率不需要种子,只需要保证表结构存在。

## 8. 二期(混合模式)预留说明

只是说明 §2.2 里 `IsolationMode`/`ConnectionConfigId` 两个字段将来怎么用,**本次不实现**:

- 参照 SqlSugar 官方"仓储+多租户"/"SAAS 分库"文档(donet5.com),分库路由用 `ISqlSugarClient.AsTenant()` → `IsAnyConnection(configId)` → `AddConnection(new ConnectionConfig{ConfigId=configId,...})` → `GetConnectionScope(configId)`(线程安全版本,官方推荐;非 Scope 版 `GetConnection` 有线程安全代价)。
- 路由只影响**消费方自己的业务实体**(通过 `TenantEntity`/`TenantDataEntity` 之外、消费方自定义走独立连接的仓储),内核自身的 `SysOrg`/`SysUser`/`SysRole`/`SysMenu` 等永远留在主库,不因某租户选了"独立库"而搬家——对应 SqlSugar 官方文档"基础信息库(组织架构/用户/权限,固定共享)+ 业务库(按租户分库)"的划分。
- 新租户的独立库照常跑一遍 CodeFirst 建表,官方文档没有专门的"分库建表 API"。
- SmartAdmin 的 `PrimaryId` 基类本来就是雪花主键,天然满足 SqlSugar 官方"分库场景禁止自增列"的要求,不需要额外处理。

## 9. 消费方影响(破坏性变更清单)

1. 内置表新增非空 `TenantId` 列——存量库升级需要一次性迁移给老数据塞"默认租户",需要类似 `docs/coding-standards.md`/CLAUDE.md 提到的"CodeFirst nullable upgrade"测试范式,SQLite/MySQL/SqlServer/PostgreSQL 四个方言都要跑一遍。
2. 在 `AddSmartAdmin()` 之前整体 `TryAddSingleton<ISqlSugarClient>` 替换过数据层的消费方,升级后**不会自动获得**租户隔离,需要自己补 `ITenantScoped` 过滤器——必须在迁移文档里显著提示。
3. `ICurrentUser`/`TokenSubject` 新增字段,若消费方覆写过这些接口/记录需要跟进(`TokenSubject` 是 record,命名参数构造大概率兼容,位置参数构造会编译报错,实现阶段要逐个排查覆写点)。
4. 账号唯一性不变(仍全局唯一),登录接口签名不变,**不是**破坏性变更。
5. **已知权衡**:账号全局唯一意味着两个不同租户不能各自注册同名账号(如两家客户都想用 `admin` 作登录名会冲突)——这是 §1 决策 2 换来"登录页/前端零改动"的代价,如果将来有强烈的"每个租户内账号各自独立"诉求,需要重新评估账号唯一性范围,是二期或更远future的话题,本次不处理。
6. `TryAdd`/`ApplicationAssemblies`/`AddApplicationPart` 可替换性契约不受影响。

## 10. 不在本次设计范围

- 二期独立库的具体动态路由实现、跨租户排障视角、租户级字典/配置定制——均明确不做(§1 决策 3、§5、§8)。
- 具体迁移到 `TenantEntity` 的通知/文件/日志/会话实体的确切类名与字段——留给实施计划阶段核对源码后逐个列出。
- `ICurrentUser`/`SystemCurrentUser`/`HttpContextCurrentUser` 的精确改动点——留给实施计划阶段核对现有实现。
- `DefaultTenantSeed` 是否需要、种子内容——留给实施计划阶段按需评估。
