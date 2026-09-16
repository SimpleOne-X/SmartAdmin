# 多租户(共享库模式) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 SmartAdmin 内核加一个新的隔离维度——租户(`TenantId`),让机构/用户/角色/菜单授权和消费方业务数据按租户硬隔离,同一套部署能同时服务多个客户而互相看不见对方的数据;登录链路自动识别用户所属租户;新增一个只有"平台管理员"能看到的"租户管理"顶级菜单入口。

**Architecture:** 新增 `ITenantScoped` 标记接口 + `TenantEntity`/`TenantDataEntity` 基类(与既有 `IOrgScoped`/`DataEntity` 并列,不改动它们),配一个不设"数据范围=全部"逃逸条件的 SqlSugar 全局查询过滤器——这是与机构数据范围刻意不同的地方:机构范围是"同租户内我能看多少",租户隔离是"我根本不该看见别的租户"。`TenantId` 通过 JWT `tid` claim 在登录时写入、经 `ICurrentUser` 在每次请求里读出、由审计 AOP 在插入时自动填充。已发版的内置表(`SysOrg`/`SysUser`/`SysRole` 等)补列必须可空,配一个启动期回填钩子把老库的存量行统一归到一个内置的"默认租户"下,新老部署行为无缝衔接。"平台管理员"是独立于"租户内超管"的显式标志,只有它能管理租户注册表本身,避免任何一个客户自己的超管账号越权管到别的客户。

**Tech Stack:** .NET 10 / ASP.NET Core / SqlSugarCore / xUnit v3(Microsoft.Testing.Platform)/ Vue 3 + Naive UI + `smart-naive-table`。

**Spec:** `docs/superpowers/specs/2026-09-15-multi-tenancy-shared-database-design.md`(架构决策见配套 ADR `docs/adr/0010-multi-tenancy-shared-database.md`)。执行者应先读完整份 spec,本计划里的每个任务都是把 spec 的某一节落成可执行代码,不重复解释"为什么"。

## Global Constraints

- **`已有表加列,数据库列必须可空`**(`docs/superpowers/specs/.../design.md` §2.1;回归锁 `CodeFirstNullableUpgradeTests`)——`TenantId` 一律 `long?`,不是 `long`。这条贯穿几乎每个任务,不是某一步的特例。
- **租户隔离过滤器不设"数据范围=全部"式逃逸条件**(spec §3)——写 `ITenantScoped` 过滤器谓词时不要模仿 `IOrgScoped` 过滤器里的 `IsUnrestricted == true ||` 前缀,那是刻意的差异,不是疏漏。
- **平台管理员用显式 `IsPlatformAdmin` 标志判定,不用 `TenantId==null` 判定**(spec §5)——`TenantId=null` 只表示"升级补列后还没回填",两件事不能混用同一个信号。
- **`TryAdd*` 可替换性契约**:新服务一律 `TryAddScoped`/`TryAddSingleton`/`TryAddEnumerable`,新扩展点登记进 `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs`。
- **雪花主键**:`InsertAsync(entity)` 之后直接读 `entity.Id`;禁止 `ExecuteReturnBigIdentityAsync`/`ExecuteReturnIdentityAsync`(会把 AOP 分配好的 Id 覆盖成 0)。
- **权限码 = 路由**:`Permission = "METHOD:/路由模板"`,与 Controller 路由字符串完全一致,`PermissionCodeConsistencyTests` 双向锁。
- **内置菜单种子 Id 编号**(`DefaultMenuSeed.cs` 头部注释,`MenuSeedIdLayoutTests` 锁定):百位是分区,2xx 已预留给本次的"租户管理"目录;整百是目录,十位是页面,个位 1–4 固定是查询/新增/更新/删除。
- **ErrorCode 分段**:42000–42999 是"用户/组织/角色/菜单"段,本次新增码从 42031 起接着写(现有最大是 `OrgOutOfScope=42030`)。
- 每个任务结束前跑 `dotnet test backend/SmartAdmin.slnx -- --filter-class "*<本任务相关类名>*"`,全部任务做完后跑一次不带 filter 的全量 `dotnet test backend/SmartAdmin.slnx`(默认 SQLite,足够;方言矩阵留给 CI/`ci.bat -Stage backend -Dialect ...`,不在本计划要求范围)。
- 中文注释、Chinese commit messages(`type(scope): 主题`,见 `skills/write-commit.md`),每个任务一个 commit。

---

### Task 1: 租户核心类型(`ITenantScoped` / `TenantEntity` / `TenantDataEntity` / `TenantIsolationMode`)

**Files:**
- Create: `backend/src/SmartAdmin.SqlSugar/Entities/TenantEntity.cs`
- Create: `backend/src/SmartAdmin.Core/Security/TenantIsolationMode.cs`
- Test: `backend/tests/SmartAdmin.Tests/TenantEntityLayoutTests.cs`

**Interfaces:**
- Produces: `interface ITenantScoped { long? TenantId { get; } }`、`abstract class TenantEntity : BaseEntity, ITenantScoped`、`abstract class TenantDataEntity : DataEntity, ITenantScoped`(均在 `SmartAdmin.SqlSugar` 命名空间,与 `IOrgScoped`/`DataEntity` 同规则:接口写在与它修饰的基类同一文件)、`enum TenantIsolationMode { Shared = 1, Standalone = 2 }`(`SmartAdmin.Core` 命名空间)。

- [ ] **Step 1: 写失败测试——新基类必须携带 `TenantId` 且默认为空**

```csharp
// backend/tests/SmartAdmin.Tests/TenantEntityLayoutTests.cs
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>租户实体基类层级的纯类型契约——不接触数据库,只锁字段/接口/默认值。</summary>
public class TenantEntityLayoutTests
{
    [Fact]
    public void TenantEntity_implements_ITenantScoped_with_nullable_default()
    {
        var doc = new ProbeTenantEntity();
        Assert.Null(doc.TenantId);            // 未显式赋值时为空——AOP 填充前的正常状态
        ITenantScoped scoped = doc;
        Assert.Null(scoped.TenantId);
    }

    [Fact]
    public void TenantDataEntity_combines_org_scope_and_tenant_scope()
    {
        var doc = new ProbeTenantDataEntity();
        Assert.IsAssignableFrom<IOrgScoped>(doc);
        Assert.IsAssignableFrom<ITenantScoped>(doc);
    }

    [SugarTable("probe_tenant_entity")]
    private class ProbeTenantEntity : TenantEntity;

    [SugarTable("probe_tenant_data_entity")]
    private class ProbeTenantDataEntity : TenantDataEntity;
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantEntityLayoutTests*"`
Expected: 编译失败——`ITenantScoped`/`TenantEntity`/`TenantDataEntity` 尚不存在。

- [ ] **Step 3: 写最小实现**

```csharp
// backend/src/SmartAdmin.SqlSugar/Entities/TenantEntity.cs
namespace SmartAdmin.SqlSugar;

/// <summary>
/// 租户隔离标记。与 <see cref="IOrgScoped"/> 的区别:这是硬安全边界,配套的全局过滤器
/// (见 SqlSugarSetup.AttachHooks)不设"数据范围=全部"式逃逸条件——机构数据范围回答
/// "同租户内我能看多少",租户隔离回答"我根本不该看见别的租户",语义不同,不能共用同一个开关。
/// </summary>
public interface ITenantScoped
{
    long? TenantId { get; }
}

/// <summary>
/// 审计 + 软删 + 租户隔离,不含机构数据范围。多数内置 Sys* 表(用户/角色/菜单授权等)用这个。
/// <para><c>TenantId</c> 是 <c>long?</c> 而非 <c>long</c>:已发版表补列必须可空(见
/// <c>CodeFirstNullableUpgradeTests</c>),不能假设这一列总有值。新行由插入 AOP 自动填充;
/// 老库升级后的存量 null 行由 <c>TenantBackfillHook</c> 统一回填。</para>
/// </summary>
public abstract class TenantEntity : BaseEntity, ITenantScoped
{
    public long? TenantId { get; set; }
}

/// <summary>
/// 审计 + 软删 + 机构数据范围 + 租户隔离。需要"同租户内还要分机构可见范围"的实体用这个。
/// </summary>
public abstract class TenantDataEntity : DataEntity, ITenantScoped
{
    public long? TenantId { get; set; }
}
```

```csharp
// backend/src/SmartAdmin.Core/Security/TenantIsolationMode.cs
namespace SmartAdmin.Core;

/// <summary>
/// 租户的数据隔离模式。一期只支持 <see cref="Shared"/>;<see cref="Standalone"/> 是二期"混合模式"的预留值,
/// 一期 <c>TenantService</c> 会拒绝写入(见 <see cref="ErrorCode.TenantIsolationModeNotSupported"/>)。
/// 二期即便某租户选了 Standalone,内核自身的机构/用户/角色/菜单授权数据仍然留在共享主库,只有消费方
/// 自己的业务表会路由到独立连接串——对应 SqlSugar 官方"基础信息库 + 业务库"的划分,见设计文档 §8。
/// </summary>
public enum TenantIsolationMode
{
    /// <summary>共享库:与其它租户共用主库,按 TenantId 过滤隔离。一期唯一可选项。</summary>
    Shared = 1,

    /// <summary>独立库(二期):消费方业务数据路由到独立连接串。一期写入即拒绝。</summary>
    Standalone = 2,
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantEntityLayoutTests*"`
Expected: PASS(2 passed)

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.SqlSugar/Entities/TenantEntity.cs backend/src/SmartAdmin.Core/Security/TenantIsolationMode.cs backend/tests/SmartAdmin.Tests/TenantEntityLayoutTests.cs
git commit -m "feat(backend): 新增 ITenantScoped/TenantEntity 租户隔离基类

与 IOrgScoped/DataEntity 并列,不改动既有基类;TenantId 为 long? 而非 long——已发版表补列必须可空,过滤器/AOP/升级回填在后续任务接入。TenantIsolationMode 预留二期混合模式的枚举值。"
```

---

### Task 2: `ICurrentUser`/JWT 携带 `TenantId` + `IsPlatformAdmin`

**Files:**
- Modify: `backend/src/SmartAdmin.Core/Security/ICurrentUser.cs`
- Modify: `backend/src/SmartAdmin.Core/Security/ITokenProvider.cs`(`TokenClaimNames`、`TokenSubject`)
- Modify: `backend/src/SmartAdmin.SqlSugar/Security/SystemCurrentUser.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/Security/HttpContextCurrentUser.cs`
- Modify: `backend/src/SmartAdmin.AspNetCore/Security/JwtTokenProvider.cs`(`BuildClaims`)
- Modify(测试桩,否则编译不过): `backend/tests/SmartAdmin.Tests/DataScopeTests.cs`(`StubCurrentUser`)、`backend/tests/SmartAdmin.Tests/OrgAuditEntityTests.cs`、`backend/tests/SmartAdmin.Tests/SoftDeleteAuditTests.cs`
- Test: `backend/tests/SmartAdmin.Tests/TenantClaimsTests.cs`

**Interfaces:**
- Consumes: 无(纯类型/claim 扩展)。
- Produces: `ICurrentUser.TenantId : long?`、`ICurrentUser.IsPlatformAdmin : bool`;`TokenClaimNames.TENANT_ID = "tid"`、`TokenClaimNames.PLATFORM_ADMIN = "padm"`;`TokenSubject` 新增两个尾随可选参数 `long? TenantId = null, bool IsPlatformAdmin = false`。后续任务(尤其 Task 6/7)直接消费这四个成员。

- [ ] **Step 1: 写失败测试——`BuildClaims` 应该按 `TokenSubject` 塞 `tid`/`padm`,`HttpContextCurrentUser` 应该能读回来**

```csharp
// backend/tests/SmartAdmin.Tests/TenantClaimsTests.cs
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>租户 claim 的签发与回读——不经 HTTP,直接对 JwtTokenProvider + HttpContextCurrentUser 两端验证。</summary>
public class TenantClaimsTests
{
    [Fact]
    public void BuildClaims_includes_tid_and_padm_when_subject_has_them()
    {
        var provider = new JwtTokenProvider(
            new AdminJwtOptions { Issuer = "test", ExpireMinutes = 30, RefreshExpireMinutes = 60 },
            new SymmetricSecurityKey(RandomNumberGenerator_GetBytes32()),
            TimeProvider.System);

        var pair = provider.Create(new TokenSubject(1, "acc", "sid", IsSuperAdmin: true, OrgId: null, TenantId: 77, IsPlatformAdmin: true));

        var handler = new JsonWebTokenHandler();
        var token = handler.ReadJsonWebToken(pair.AccessToken);
        Assert.Equal("77", token.GetClaim(TokenClaimNames.TENANT_ID).Value);
        Assert.Equal("true", token.GetClaim(TokenClaimNames.PLATFORM_ADMIN).Value);
    }

    [Fact]
    public void BuildClaims_omits_tid_and_padm_when_subject_has_neither()
    {
        var provider = new JwtTokenProvider(
            new AdminJwtOptions { Issuer = "test", ExpireMinutes = 30, RefreshExpireMinutes = 60 },
            new SymmetricSecurityKey(RandomNumberGenerator_GetBytes32()),
            TimeProvider.System);

        var pair = provider.Create(new TokenSubject(1, "acc", "sid"));

        var token = new JsonWebTokenHandler().ReadJsonWebToken(pair.AccessToken);
        Assert.DoesNotContain(token.Claims, c => c.Type == TokenClaimNames.TENANT_ID);
        Assert.DoesNotContain(token.Claims, c => c.Type == TokenClaimNames.PLATFORM_ADMIN);
    }

    [Fact]
    public void HttpContextCurrentUser_reads_tid_and_padm_claims()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(TokenClaimNames.TENANT_ID, "42"),
            new Claim(TokenClaimNames.PLATFORM_ADMIN, "true"),
        ], authenticationType: "test");
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };

        var currentUser = new HttpContextCurrentUser(accessor);
        Assert.Equal(42, currentUser.TenantId);
        Assert.True(currentUser.IsPlatformAdmin);
    }

    private static byte[] RandomNumberGenerator_GetBytes32() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantClaimsTests*"`
Expected: 编译失败——`TokenSubject` 没有 `TenantId`/`IsPlatformAdmin` 参数,`TokenClaimNames` 没有 `TENANT_ID`/`PLATFORM_ADMIN`,`ICurrentUser` 没有对应成员。

- [ ] **Step 3: 实现**

`backend/src/SmartAdmin.Core/Security/ITokenProvider.cs` 里 `TokenSubject` 与 `TokenClaimNames`:

```csharp
/// <param name="TenantId">
/// 所属租户 Id:写入令牌 tid claim。为 null 表示尚未接入租户(升级过渡态或系统上下文)。
/// </param>
/// <param name="IsPlatformAdmin">
/// 平台管理员标志:写入令牌 padm claim。与 <paramref name="IsSuperAdmin"/> 独立——
/// 超管绕过 [RolePermission],但仍受 ITenantScoped 过滤器约束;只有平台管理员能管理 SysTenant 本身。
/// </param>
public record TokenSubject(long UserId, string Account, string SessionId, bool IsSuperAdmin = false, long? OrgId = null,
    long? TenantId = null, bool IsPlatformAdmin = false);

public static class TokenClaimNames
{
    public const string SESSION_ID = "sid";
    public const string SUPER_ADMIN = "sadm";
    public const string ORG_ID = "org";
    public const string API_KEY = "akn";

    /// <summary>所属租户 Id(ITenantScoped 过滤器与 AOP TenantId 填充的读取源)</summary>
    public const string TENANT_ID = "tid";

    /// <summary>平台管理员标志(值为 "true" 时可管理 SysTenant 注册表本身)</summary>
    public const string PLATFORM_ADMIN = "padm";
}
```

`backend/src/SmartAdmin.Core/Security/ICurrentUser.cs` 追加两个成员(紧跟 `OrgId` 之后):

```csharp
    /// <summary>当前用户所属租户 Id(令牌 tid claim);未认证/无租户为 null。
    /// ITenantScoped 过滤器与 TenantId 的 AOP 填充源(见 SqlSugarSetup)。</summary>
    long? TenantId { get; }

    /// <summary>是否平台管理员(令牌 padm claim)。与 IsSuperAdmin 独立:唯一能管理
    /// SysTenant 注册表本身的身份,不因某个租户内部有超管账号而被绕过。</summary>
    bool IsPlatformAdmin { get; }
```

`backend/src/SmartAdmin.SqlSugar/Security/SystemCurrentUser.cs` 追加:

```csharp
    /// <inheritdoc/>
    public long? TenantId => null;
    /// <inheritdoc/>
    public bool IsPlatformAdmin => false;
```

`backend/src/SmartAdmin.AspNetCore/Security/HttpContextCurrentUser.cs` 追加(紧跟 `OrgId` 之后):

```csharp
    /// <inheritdoc />
    public virtual long? TenantId =>
        long.TryParse(Principal?.FindFirstValue(TokenClaimNames.TENANT_ID), out var tenantId) ? tenantId : null;

    /// <inheritdoc />
    public virtual bool IsPlatformAdmin => Principal?.HasClaim(TokenClaimNames.PLATFORM_ADMIN, "true") == true;
```

`backend/src/SmartAdmin.AspNetCore/Security/JwtTokenProvider.cs` 的 `BuildClaims` 追加(紧跟 `OrgId` 判断之后,`return` 之前的最后两行):

```csharp
        if (subject.TenantId is { } tenantId)
            yield return new Claim(TokenClaimNames.TENANT_ID, tenantId.ToString());
        if (subject.IsPlatformAdmin)
            yield return new Claim(TokenClaimNames.PLATFORM_ADMIN, "true");
```

三个测试桩各加两行(`TenantId => null`/`IsPlatformAdmin => false`,与桩里已有的 `OrgId`/`IsSuperAdmin` 写法一致)——`backend/tests/SmartAdmin.Tests/DataScopeTests.cs` 的 `StubCurrentUser`、`backend/tests/SmartAdmin.Tests/OrgAuditEntityTests.cs` 与 `backend/tests/SmartAdmin.Tests/SoftDeleteAuditTests.cs` 里同名接口实现,不改测试逻辑,只为满足接口契约让编译通过。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantClaimsTests*" --filter-class "*DataScopeTests*" --filter-class "*OrgAuditEntityTests*" --filter-class "*SoftDeleteAuditTests*"`
Expected: PASS,且原有三个测试类不受影响(证明桩类修改没有破坏既有逻辑)。

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Core/Security/ICurrentUser.cs backend/src/SmartAdmin.Core/Security/ITokenProvider.cs backend/src/SmartAdmin.SqlSugar/Security/SystemCurrentUser.cs backend/src/SmartAdmin.AspNetCore/Security/HttpContextCurrentUser.cs backend/src/SmartAdmin.AspNetCore/Security/JwtTokenProvider.cs backend/tests/SmartAdmin.Tests/DataScopeTests.cs backend/tests/SmartAdmin.Tests/OrgAuditEntityTests.cs backend/tests/SmartAdmin.Tests/SoftDeleteAuditTests.cs backend/tests/SmartAdmin.Tests/TenantClaimsTests.cs
git commit -m "feat(backend): ICurrentUser/JWT 新增 TenantId 与 IsPlatformAdmin

tid/padm 两个 claim 通过 TokenSubject 签发、经 ICurrentUser 在每次请求同步读出,不需要像数据范围那样搞 AsyncLocal/HttpContext.Items 双载体。IsPlatformAdmin 独立于 IsSuperAdmin,为后续 SysTenant 的管理门禁铺路。"
```

---

### Task 3: `SysTenant` 实体 + `DefaultTenantSeed`(默认租户)

**Files:**
- Create: `backend/src/SmartAdmin.Services/Entities/SysTenant.cs`
- Create: `backend/src/SmartAdmin.Services/Seed/DefaultTenantSeed.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`(登记种子)
- Test: `backend/tests/SmartAdmin.Tests/DefaultTenantSeedTests.cs`

**Interfaces:**
- Consumes: 无。
- Produces: `SysTenant`(`SmartAdmin.Services` 命名空间)、`DefaultTenantSeed.DEFAULT_TENANT_ID`(`const long`,后续任务作为"回填目标"与"零配置启动落脚租户"反复引用,是本计划里除 `TenantId`/`IsPlatformAdmin` 外第三个被跨任务消费的符号)。

- [ ] **Step 1: 写失败测试——CodeFirst 建表 + 首启种下唯一的默认租户**

```csharp
// backend/tests/SmartAdmin.Tests/DefaultTenantSeedTests.cs
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

public class DefaultTenantSeedTests
{
    [Fact]
    public async Task Fresh_start_seeds_exactly_one_protected_default_tenant()
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<IRepository<SysTenant>>();

        var all = await tenants.AsQueryable().ToListAsync();
        var seeded = Assert.Single(all);
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, seeded.Id);
        Assert.Equal("default", seeded.Code);
        Assert.Equal(TenantIsolationMode.Shared, seeded.IsolationMode);
        Assert.True(seeded.Enabled);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*DefaultTenantSeedTests*"`
Expected: 编译失败——`SysTenant`/`DefaultTenantSeed` 不存在。

- [ ] **Step 3: 实现**

```csharp
// backend/src/SmartAdmin.Services/Entities/SysTenant.cs
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 租户表——隔离边界的根,自己不实现 ITenantScoped(不自我引用)。新表,首次建表可直接 NOT NULL,
/// 不受"已有表加列必须可空"约束。
/// </summary>
[SugarTable("sys_tenant", TableDescription = "租户")]
[SugarIndex("idx_sys_tenant_code", nameof(Code), OrderByType.Asc, IsUnique = true)]
public class SysTenant : BaseEntity
{
    [SugarColumn(Length = 32, ColumnDescription = "租户编码(唯一)")]
    public string Code { get; set; } = "";

    [SugarColumn(Length = 128, ColumnDescription = "租户名称")]
    public string Name { get; set; } = "";

    [SugarColumn(Length = 64, IsNullable = true, ColumnDescription = "联系人")]
    public string? ContactName { get; set; }

    [SugarColumn(Length = 32, IsNullable = true, ColumnDescription = "联系电话")]
    public string? ContactPhone { get; set; }

    [SugarColumn(IsNullable = true, ColumnDescription = "到期时间")]
    public DateTime? ExpireTime { get; set; }

    /// <summary>二期预留,一期恒为 Shared;TenantService 对 Standalone 写入一律拒绝。</summary>
    [SugarColumn(ColumnDescription = "隔离模式")]
    public TenantIsolationMode IsolationMode { get; set; } = TenantIsolationMode.Shared;

    /// <summary>二期预留:对应 SqlSugar ConfigId,只路由消费方业务库连接,不影响内核自身数据。一期恒为空。</summary>
    [SugarColumn(Length = 64, IsNullable = true, ColumnDescription = "业务库连接配置Id(二期预留)")]
    public string? ConnectionConfigId { get; set; }

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Enabled { get; set; } = true;

    [SugarColumn(Length = 256, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }
}
```

```csharp
// backend/src/SmartAdmin.Services/Seed/DefaultTenantSeed.cs
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 默认租户种子——唯一一条固定 Id 的保留行,身兼两职:
/// 1) 全新安装:初始超管(SuperAdminSeed)挂在这个租户下,零配置启动不强制"先建租户再登录"。
/// 2) 老库升级:TenantBackfillHook 把补列后 TenantId 为 null 的存量行统一回填到这个租户,
///    升级前的单租户部署行为完全不变。
/// TenantService 对 DEFAULT_TENANT_ID 的删除/禁用请求一律拒绝(受保护,仿 SysModule 内置模块的保护写法)。
/// </summary>
public class DefaultTenantSeed : ISeedData<SysTenant>
{
    public const long DEFAULT_TENANT_ID = 1;

    public virtual IEnumerable<SysTenant> HasData() =>
    [
        new SysTenant
        {
            Id = DEFAULT_TENANT_ID,
            Code = "default",
            Name = "默认租户",
            IsolationMode = TenantIsolationMode.Shared,
            Enabled = true,
            Remark = "内置默认租户,承载初始超管账号与老库升级回填的存量数据,不可删除",
        },
    ];
}
```

`backend/src/SmartAdmin.Services/ServicesSetup.cs` 的 `AddSmartAdminServices` 方法里,紧邻其它 `TryAddEnumerable(ServiceDescriptor.Transient<ISeedData, ...>())` 调用追加:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Transient<ISeedData, DefaultTenantSeed>());
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*DefaultTenantSeedTests*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Entities/SysTenant.cs backend/src/SmartAdmin.Services/Seed/DefaultTenantSeed.cs backend/src/SmartAdmin.Services/ServicesSetup.cs backend/tests/SmartAdmin.Tests/DefaultTenantSeedTests.cs
git commit -m "feat(backend): SysTenant 实体 + 默认租户种子

固定 Id=1 的受保护默认租户,身兼零配置启动落脚点与老库升级回填目标两职,详见类注释。"
```

---

### Task 4: 租户注册表 CRUD(Models / Interface / Service / ErrorCode)

**Files:**
- Create: `backend/src/SmartAdmin.Services/Tenant/TenantModels.cs`
- Create: `backend/src/SmartAdmin.Services/Tenant/ITenantService.cs`
- Create: `backend/src/SmartAdmin.Services/Tenant/TenantService.cs`
- Modify: `backend/src/SmartAdmin.Core/ErrorCode.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`(DI 注册)
- Test: `backend/tests/SmartAdmin.Tests/TenantServiceTests.cs`

**Interfaces:**
- Consumes: `SysTenant`/`DefaultTenantSeed.DEFAULT_TENANT_ID`(Task 3)、`ICurrentUser.IsPlatformAdmin`(Task 2)、`IRepository<>`、`IPasswordHasher`(既有扩展点)。
- Produces: `ITenantService { PageAsync, GetAsync, AddAsync(TenantCreateInput), UpdateAsync, DeleteAsync }`——`AddAsync` 接收 `TenantCreateInput`(而非普通 CRUD 的 `TenantInput`),因为新增租户要同时建它的第一个管理员账号,是这个 Service 与标准模板(`create-crud-backend.md`)的唯一差异点,Task 5 的 `TenantController` 直接调用。

- [ ] **Step 1: 写失败测试**

```csharp
// backend/tests/SmartAdmin.Tests/TenantServiceTests.cs
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

public class TenantServiceTests
{
    private static async Task<(AdminAppFactory f, ITenantService svc, IServiceScope scope)> PlatformAdminScope()
    {
        var f = new AdminAppFactory();
        _ = f.CreateClient();
        var scope = f.Services.CreateScope();
        // TenantService 的门禁校验 ICurrentUser.IsPlatformAdmin——单测不走 HTTP,直接前置注册一个平台管理员桩顶掉 SystemCurrentUser
        return (f, scope.ServiceProvider.GetRequiredService<ITenantService>(), scope);
    }

    [Fact]
    public async Task AddAsync_creates_tenant_and_its_initial_admin_user()
    {
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(new PlatformAdminStub()) };
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<SysUser>>();

        var id = await svc.AddAsync(new TenantCreateInput
        {
            Code = "acme", Name = "Acme Inc", IsolationMode = TenantIsolationMode.Shared,
            AdminAccount = "acme_admin", AdminPassword = "Test@123456",
        });

        var tenant = await svc.GetAsync(id);
        Assert.Equal("acme", tenant.Code);

        var admin = await users.AsQueryable().ClearFilter<ITenantScoped>().FirstAsync(u => u.Account == "acme_admin");
        Assert.NotNull(admin);
        Assert.Equal(id, admin!.TenantId);
        Assert.True(admin.IsSuperAdmin);
        Assert.True(admin.MustChangePassword);

        f.Dispose();
    }

    [Fact]
    public async Task AddAsync_rejects_standalone_isolation_mode_in_phase_one()
    {
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(new PlatformAdminStub()) };
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => svc.AddAsync(new TenantCreateInput
        {
            Code = "standalone-try", Name = "X", IsolationMode = TenantIsolationMode.Standalone,
            AdminAccount = "x_admin", AdminPassword = "Test@123456",
        }));
        Assert.Equal(ErrorCode.TenantIsolationModeNotSupported, ex.Code);
        f.Dispose();
    }

    [Fact]
    public async Task AddAsync_without_platform_admin_is_rejected()
    {
        // 默认 SystemCurrentUser.IsPlatformAdmin=false,不前置注册桩
        using var f = new AdminAppFactory();
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => svc.AddAsync(new TenantCreateInput
        {
            Code = "no-perm", Name = "X", AdminAccount = "np_admin", AdminPassword = "Test@123456",
        }));
        Assert.Equal(ErrorCode.PlatformAdminRequired, ex.Code);
    }

    [Fact]
    public async Task DeleteAsync_protects_default_tenant()
    {
        var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ICurrentUser>(new PlatformAdminStub()) };
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITenantService>();

        var ex = await Assert.ThrowsAsync<AdminException>(() => svc.DeleteAsync(DefaultTenantSeed.DEFAULT_TENANT_ID));
        Assert.Equal(ErrorCode.TenantProtected, ex.Code);
        f.Dispose();
    }

    private sealed class PlatformAdminStub : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long? UserId => DefaultTenantSeed.DEFAULT_TENANT_ID;
        public string? SessionId => null;
        public bool IsSuperAdmin => true;
        public long? OrgId => null;
        public long? TenantId => DefaultTenantSeed.DEFAULT_TENANT_ID;
        public bool IsPlatformAdmin => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantServiceTests*"`
Expected: 编译失败——`ITenantService`/`TenantCreateInput`/相关 ErrorCode 不存在。

- [ ] **Step 3: 实现**

```csharp
// backend/src/SmartAdmin.Services/Tenant/TenantModels.cs
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>租户编辑入参(更新用;不含初始管理员字段——那是创建独有的一次性动作)。</summary>
public record TenantInput
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public DateTime? ExpireTime { get; init; }
    public TenantIsolationMode IsolationMode { get; init; } = TenantIsolationMode.Shared;
    public bool Enabled { get; init; } = true;
    public string? Remark { get; init; }
}

/// <summary>租户创建入参:在编辑字段基础上,额外携带该租户第一个管理员账号的凭据——
/// 新租户必须带着能登录的管理员一起出生,否则平台管理员建完之后没人能进去继续配置。</summary>
public record TenantCreateInput : TenantInput
{
    public string AdminAccount { get; init; } = "";
    public string AdminPassword { get; init; } = "";
}

public record TenantPageInput : PageInputBase
{
    public string? Name { get; init; }
}
```

```csharp
// backend/src/SmartAdmin.Services/Tenant/ITenantService.cs
using SmartAdmin.Core;

namespace SmartAdmin.Services;

public interface ITenantService
{
    Task<PagedList<SysTenant>> PageAsync(TenantPageInput input);
    Task<SysTenant> GetAsync(long id);
    Task<long> AddAsync(TenantCreateInput input);
    Task UpdateAsync(long id, TenantInput input);
    Task DeleteAsync(long id);
}
```

```csharp
// backend/src/SmartAdmin.Services/Tenant/TenantService.cs
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// SysTenant 的标准 CRUD,叠加两条本表独有的规则:只有平台管理员能操作(RequirePlatformAdmin,
/// 与 [RolePermission]/IsSuperAdmin 完全独立的第二道门,见 ICurrentUser.IsPlatformAdmin 注释);
/// 一期只接受 Shared 隔离模式(RequireSharedMode)。
/// </summary>
public class TenantService(
    IRepository<SysTenant> tenants,
    IRepository<SysUser> users,
    IPasswordHasher hasher,
    ICurrentUser currentUser) : ITenantService
{
    public virtual async Task<PagedList<SysTenant>> PageAsync(TenantPageInput input) =>
        await tenants.AsQueryable()
            .WhereIF(!string.IsNullOrEmpty(input.Name), t => t.Name.Contains(input.Name!))
            .ToPagedListAsync(input, q => q.OrderBy(t => t.CreateTime));

    public virtual async Task<SysTenant> GetAsync(long id)
    {
        var tenant = await tenants.GetByIdAsync(id);
        AdminException.ThrowIf(tenant is null, ErrorCode.TenantNotFound);
        return tenant!;
    }

    public virtual async Task<long> AddAsync(TenantCreateInput input)
    {
        RequirePlatformAdmin();
        RequireSharedMode(input.IsolationMode);

        AdminException.ThrowIf(
            await tenants.AsQueryable().ClearFilter<ISoftDelete>().AnyAsync(t => t.Code == input.Code),
            ErrorCode.TenantCodeExists);
        AdminException.ThrowIf(
            await users.AsQueryable().ClearFilter<ISoftDelete>().ClearFilter<ITenantScoped>().AnyAsync(u => u.Account == input.AdminAccount),
            ErrorCode.AccountExists);

        var tenant = new SysTenant
        {
            Code = input.Code,
            Name = input.Name,
            ContactName = input.ContactName,
            ContactPhone = input.ContactPhone,
            ExpireTime = input.ExpireTime,
            IsolationMode = input.IsolationMode,
            Enabled = input.Enabled,
            Remark = input.Remark,
        };
        await tenants.InsertAsync(tenant);

        // 新租户的初始管理员——租户内超管,不是平台管理员。TenantId 必须显式指定:
        // 插入 AOP 会按"当前登录者"(平台管理员自己所在的默认租户)回填,那不是这个新用户该归属的租户。
        await users.InsertAsync(new SysUser
        {
            TenantId = tenant.Id,
            Account = input.AdminAccount,
            Password = hasher.Hash(input.AdminPassword),
            Name = input.Name + "管理员",
            IsSuperAdmin = true,
            MustChangePassword = true,
        });

        return tenant.Id;
    }

    public virtual async Task UpdateAsync(long id, TenantInput input)
    {
        RequirePlatformAdmin();
        RequireSharedMode(input.IsolationMode);

        var tenant = await GetAsync(id);
        AdminException.ThrowIf(
            input.Code != tenant.Code &&
            await tenants.AsQueryable().ClearFilter<ISoftDelete>().AnyAsync(t => t.Code == input.Code && t.Id != id),
            ErrorCode.TenantCodeExists);

        tenant.Code = input.Code;
        tenant.Name = input.Name;
        tenant.ContactName = input.ContactName;
        tenant.ContactPhone = input.ContactPhone;
        tenant.ExpireTime = input.ExpireTime;
        tenant.IsolationMode = input.IsolationMode;
        tenant.Enabled = input.Enabled;
        tenant.Remark = input.Remark;
        await tenants.UpdateAsync(tenant);
    }

    public virtual async Task DeleteAsync(long id)
    {
        RequirePlatformAdmin();
        AdminException.ThrowIf(id == DefaultTenantSeed.DEFAULT_TENANT_ID, ErrorCode.TenantProtected);
        await GetAsync(id);
        await tenants.DeleteAsync(id);
    }

    /// <summary>只有平台管理员能管理租户注册表本身——不依赖角色授权,防止某个租户内部的超管账号
    /// (同样绕过 [RolePermission])顺带管到别的租户。</summary>
    protected virtual void RequirePlatformAdmin() =>
        AdminException.ThrowIf(!currentUser.IsPlatformAdmin, ErrorCode.PlatformAdminRequired);

    /// <summary>一期只支持共享库;写 Standalone 一律拒绝(二期解锁前的硬校验)。</summary>
    protected virtual void RequireSharedMode(TenantIsolationMode mode) =>
        AdminException.ThrowIf(mode != TenantIsolationMode.Shared, ErrorCode.TenantIsolationModeNotSupported);
}
```

`backend/src/SmartAdmin.Core/ErrorCode.cs`,紧接 `OrgOutOfScope = 42030` 之后追加:

```csharp
    /// <summary>目标租户不存在</summary>
    [MsgKey("error.tenant.notFound")]
    TenantNotFound = 42031,

    /// <summary>租户编码已存在(编码唯一)</summary>
    [MsgKey("error.tenant.codeExists")]
    TenantCodeExists = 42032,

    /// <summary>一期只支持共享库隔离模式,独立库二期开放</summary>
    [MsgKey("error.tenant.isolationModeNotSupported")]
    TenantIsolationModeNotSupported = 42033,

    /// <summary>默认租户受保护:不可删除/禁用</summary>
    [MsgKey("error.tenant.protected")]
    TenantProtected = 42034,

    /// <summary>该操作仅平台管理员可执行</summary>
    [MsgKey("error.tenant.platformAdminRequired")]
    PlatformAdminRequired = 42035,
```

`backend/src/SmartAdmin.Services/ServicesSetup.cs` 追加:

```csharp
services.TryAddScoped<ITenantService, TenantService>();
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantServiceTests*"`
Expected: PASS(4 passed)

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Tenant backend/src/SmartAdmin.Core/ErrorCode.cs backend/src/SmartAdmin.Services/ServicesSetup.cs backend/tests/SmartAdmin.Tests/TenantServiceTests.cs
git commit -m "feat(backend): 租户注册表 CRUD 服务,新增即建首个管理员账号

AddAsync 与标准 CRUD 模板的差异:同一事务里连带建该租户的初始超管,否则平台管理员建完租户后没人能登录进去继续配置。写操作统一挂 RequirePlatformAdmin 门禁,独立于 [RolePermission]/IsSuperAdmin。"
```

---

### Task 5: `TenantController` + 可替换性登记

**Files:**
- Create: `backend/src/SmartAdmin.AspNetCore/Controllers/TenantController.cs`
- Modify: `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs`
- Test: `backend/tests/SmartAdmin.Tests/TenantControllerTests.cs`

**Interfaces:**
- Consumes: `ITenantService`(Task 4)。
- Produces: `POST /api/v1/sys/tenant/add`、`PUT /api/v1/sys/tenant/{id}`、`DELETE /api/v1/sys/tenant/{id}`、`GET /api/v1/sys/tenant/page`、`GET /api/v1/sys/tenant/{id}`——Task 11(菜单种子)按这几条路由拼 `Permission`。

- [ ] **Step 1: 写失败测试——HTTP 级验证平台管理员门禁生效**

```csharp
// backend/tests/SmartAdmin.Tests/TenantControllerTests.cs
using System.Net.Http.Headers;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

public class TenantControllerTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    [Fact]
    public async Task Seeded_super_admin_is_platform_admin_and_can_manage_tenants()
    {
        // 种子超管(SuperAdminSeed)天然是初始平台管理员(Task 6),不需要额外授权就能建租户
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var add = await c.PostJson("/api/v1/sys/tenant/add", new
        {
            code = "http-crud", name = "HTTP CRUD Co", isolationMode = 1, enabled = true,
            adminAccount = "http_crud_admin", adminPassword = "Test@123456",
        });
        var addEnv = await add.ReadEnvelope();
        Assert.Equal(0, addEnv.GetProperty("code").GetInt32());
        var newId = addEnv.GetProperty("data").GetInt64();

        var get = await (await c.GetAsync($"/api/v1/sys/tenant/{newId}")).ReadEnvelope();
        Assert.Equal("http-crud", get.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Add_with_standalone_mode_returns_TenantIsolationModeNotSupported()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var add = await (await c.PostJson("/api/v1/sys/tenant/add", new
        {
            code = "standalone-http", name = "X", isolationMode = 2, enabled = true,
            adminAccount = "sa_admin", adminPassword = "Test@123456",
        })).ReadEnvelope();
        Assert.Equal((int)ErrorCode.TenantIsolationModeNotSupported, add.GetProperty("code").GetInt32());
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantControllerTests*"`
Expected: 404——路由不存在(Controller 未创建;`Add_with_standalone_mode...` 会因为找不到路由而不是因为业务码失败,同样判定为失败)。

- [ ] **Step 3: 实现**

```csharp
// backend/src/SmartAdmin.AspNetCore/Controllers/TenantController.cs
using Microsoft.AspNetCore.Mvc;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>租户注册表端点——业务门禁(仅平台管理员)在 TenantService 里,不在这层。</summary>
[ApiController]
[Route("api/v1/sys/tenant")]
public class TenantController(ITenantService tenantService) : ControllerBase
{
    [HttpGet("page")]
    [RolePermission]
    public async Task<Result<PagedList<SysTenant>>> Page([FromQuery] TenantPageInput input) =>
        Result<PagedList<SysTenant>>.Ok(await tenantService.PageAsync(input));

    [HttpGet("{id}")]
    [RolePermission]
    public async Task<Result<SysTenant>> Get(long id) =>
        Result<SysTenant>.Ok(await tenantService.GetAsync(id));

    [HttpPost("add")]
    [RolePermission]
    [OperationLog("新增租户")]
    public async Task<Result<long>> Add(TenantCreateInput input) =>
        Result<long>.Ok(await tenantService.AddAsync(input));

    [HttpPut("{id}")]
    [RolePermission]
    [OperationLog("更新租户")]
    public async Task<Result<bool>> Update(long id, TenantInput input)
    {
        await tenantService.UpdateAsync(id, input);
        return Result<bool>.Ok(true);
    }

    [HttpDelete("{id}")]
    [RolePermission]
    [OperationLog("删除租户")]
    public async Task<Result<bool>> Delete(long id)
    {
        await tenantService.DeleteAsync(id);
        return Result<bool>.Ok(true);
    }
}
```

`backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs` 的 `Points` 数组追加一行(放在其它 `Scoped` 服务旁边):

```csharp
(typeof(ITenantService), ServiceLifetime.Scoped),
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantControllerTests*" --filter-class "*ReplaceabilityContract*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.AspNetCore/Controllers/TenantController.cs backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs backend/tests/SmartAdmin.Tests/TenantControllerTests.cs
git commit -m "feat(backend): TenantController 端点 + 可替换性契约登记"
```

---

### Task 6: `SysUser` 挂 `TenantId`/`IsPlatformAdmin`,种子与登录链路接入

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Entities/SysUser.cs`(`: BaseEntity` → `: TenantEntity`,新增 `IsPlatformAdmin`)
- Modify: `backend/src/SmartAdmin.Services/Seed/SuperAdminSeed.cs`(种子超管挂默认租户 + 平台管理员)
- Modify: `backend/src/SmartAdmin.Services/Auth/AuthService.cs`(`CreateTokenAsync` 把 `TenantId`/`IsPlatformAdmin` 塞进 `TokenSubject`)
- Test: `backend/tests/SmartAdmin.Tests/TenantLoginTests.cs`

**Interfaces:**
- Consumes: `TenantEntity`(Task 1)、`TokenSubject` 新参数(Task 2)、`DefaultTenantSeed.DEFAULT_TENANT_ID`(Task 3)。
- Produces: `SysUser.TenantId : long?`(继承自 `TenantEntity`)、`SysUser.IsPlatformAdmin : bool?`——Task 7/9/10 的隔离测试、Task 4 已经在用的 `ClearFilter<ITenantScoped>()` 查询都依赖这张表已经是 `ITenantScoped`。

- [ ] **Step 1: 写失败测试——登录响应的 JWT 应该带种子超管的 tid/padm**

```csharp
// backend/tests/SmartAdmin.Tests/TenantLoginTests.cs
using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

public class TenantLoginTests
{
    [Fact]
    public async Task Seeded_super_admin_login_token_carries_default_tenant_and_platform_admin()
    {
        using var f = new AdminAppFactory();
        var c = f.CreateClient();

        var token = await c.LoginToken("superAdmin", "Test@123456");
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);

        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID.ToString(), jwt.GetClaim("tid").Value);
        Assert.Equal("true", jwt.GetClaim("padm").Value);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantLoginTests*"`
Expected: FAIL——`jwt.GetClaim("tid")` 抛异常(claim 不存在),因为 `SysUser` 还没有 `TenantId`,`AuthService` 也还没往 `TokenSubject` 里塞。

- [ ] **Step 3: 实现**

`backend/src/SmartAdmin.Services/Entities/SysUser.cs`:把类声明从 `public class SysUser : BaseEntity` 改成 `public class SysUser : TenantEntity`(`TenantId` 随之继承,不用在这个文件里重复声明列),并在文件末尾(紧邻 `TotpBoundAt` 之后)追加:

```csharp
    /// <summary>
    /// 是否平台管理员(可管理全部租户,包括 SysTenant 注册表本身)。与 IsSuperAdmin 独立、不互相派生。
    /// 只能由种子/数据库手工设置,接口永远不暴露修改入口——防提权(同 IsSuperAdmin 的写法)。
    /// 演进列必须可空:MSSQL 无法对有数据的表 ADD 无 DEFAULT 的 NOT NULL 列(同 ForceTotp 的成法)。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "是否平台管理员")]
    public bool? IsPlatformAdmin { get; set; }
```

`backend/src/SmartAdmin.Services/Seed/SuperAdminSeed.cs`:在构造 `SysUser` 种子行的对象初始化器里追加两个赋值(与已有的 `IsSuperAdmin = true` 相邻):

```csharp
TenantId = DefaultTenantSeed.DEFAULT_TENANT_ID,
IsPlatformAdmin = true,
```

`backend/src/SmartAdmin.Services/Auth/AuthService.cs` 的 `CreateTokenAsync`(约第 468-476 行),把:

```csharp
var pair = tokens.Create(new TokenSubject(user.Id, user.Account, sessionId, user.IsSuperAdmin, user.OrgId),
    TimeSpan.FromMinutes(accessMin), TimeSpan.FromMinutes(refreshMin));
```

改成:

```csharp
var pair = tokens.Create(
    new TokenSubject(user.Id, user.Account, sessionId, user.IsSuperAdmin, user.OrgId,
        TenantId: user.TenantId, IsPlatformAdmin: user.IsPlatformAdmin == true),
    TimeSpan.FromMinutes(accessMin), TimeSpan.FromMinutes(refreshMin));
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantLoginTests*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Entities/SysUser.cs backend/src/SmartAdmin.Services/Seed/SuperAdminSeed.cs backend/src/SmartAdmin.Services/Auth/AuthService.cs backend/tests/SmartAdmin.Tests/TenantLoginTests.cs
git commit -m "feat(backend): SysUser 迁入 TenantEntity,登录令牌携带租户与平台管理员标志

种子超管显式挂默认租户 + IsPlatformAdmin=true,零配置启动即可用。CreateTokenAsync 把两个字段接进 TokenSubject。"
```

---

### Task 7: 租户隔离全局过滤器(硬边界,无逃逸条件)

**Files:**
- Modify: `backend/src/SmartAdmin.Core/Options/AdminDatabaseConnectionOptions.cs`(新增 `ApplyTenantFilter`)
- Modify: `backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs`(`HookPolicy` 结构体 + `AttachHooks` 过滤器 + AOP 自动填充)
- Test: `backend/tests/SmartAdmin.Tests/TenantScopeFilterTests.cs`

**Interfaces:**
- Consumes: `ITenantScoped`(Task 1)、`ICurrentUser.TenantId`(Task 2)。
- Produces: 主库固定挂载的 `ITenantScoped` 查询过滤器 + 插入 AOP 自动填充——Task 9/10 迁移的所有实体从这一步起自动获得隔离,不需要各自再写过滤器代码。

- [ ] **Step 1: 写失败测试——两个模拟租户互不可见,且插入自动回填**

```csharp
// backend/tests/SmartAdmin.Tests/TenantScopeFilterTests.cs
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 租户隔离全局过滤器——直压 SqlSugar 层。与 DataScopeTests 的机构范围测试刻意不同:
/// 这里没有"数据范围=全部"那样的整体逃逸开关(见 ITenantScoped 接口注释),所以不需要、也不应该
/// 测"某个标志位=true 时能看到全部"这种用例。
/// </summary>
public class TenantScopeFilterTests
{
    private static async Task<ServiceProvider> BuildProvider(string id, string dbFile, long? tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AdminCacheOptions());
        services.AddSingleton<ICurrentUser>(new StubTenantUser(tenantId));
        services.AddSmartAdminSqlSugar(
            new AdminDatabaseOptions { DbType = TestDb.DbType, ConnectionString = TestDb.ConnectionString(id, dbFile) },
            [typeof(ServicesSetup).Assembly]);
        services.AddSmartAdminServices();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISqlSugarClient>().CodeFirst.InitTables(typeof(TenantScopeDoc));
        return sp;
    }

    [Fact]
    public async Task Tenant_filter_isolates_rows_with_no_override_and_hides_unbackfilled_legacy_rows()
    {
        var id = $"tenantscope-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");

        await using (var spSeed = await BuildProvider(id, dbFile, tenantId: null))
        {
            using var scope = spSeed.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>();
            await repo.InsertRangeAsync(
            [
                new() { Title = "A-doc", TenantId = 10 },
                new() { Title = "B-doc", TenantId = 20 },
                new() { Title = "Legacy-doc", TenantId = null },   // 模拟升级补列后、还没被 TenantBackfillHook 回填的老行
            ]);
        }

        await using (var spA = await BuildProvider(id, dbFile, tenantId: 10))
        {
            using var scope = spA.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>().AsQueryable().ToListAsync();
            var only = Assert.Single(rows);
            Assert.Equal("A-doc", only.Title);
        }

        await using (var spB = await BuildProvider(id, dbFile, tenantId: 20))
        {
            using var scope = spB.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>().AsQueryable().ToListAsync();
            var only = Assert.Single(rows);
            Assert.Equal("B-doc", only.Title);
        }

        TestDb.Cleanup(id, dbFile);
    }

    [Fact]
    public async Task TenantId_is_filled_from_current_user_tenant_on_insert()
    {
        var id = $"tenantfill-{Guid.NewGuid():N}";
        var dbFile = Path.Combine(Path.GetTempPath(), $"smart-{id}.db");

        await using var sp = await BuildProvider(id, dbFile, tenantId: 77);
        using (var scope = sp.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<TenantScopeDoc>>();
            await repo.InsertAsync(new TenantScopeDoc { Title = "auto-filled" });   // 不显式设 TenantId
            var saved = await repo.AsQueryable().Where(d => d.Title == "auto-filled").FirstAsync();
            Assert.Equal(77, saved.TenantId);
        }

        TestDb.Cleanup(id, dbFile);
    }

    [SugarTable("tenant_scope_doc")]
    public class TenantScopeDoc : TenantEntity
    {
        [SugarColumn(Length = 64)] public string Title { get; set; } = "";
    }

    private sealed class StubTenantUser(long? tenantId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long? UserId => 1;
        public string? SessionId => null;
        public bool IsSuperAdmin => false;
        public long? OrgId => null;
        public long? TenantId => tenantId;
        public bool IsPlatformAdmin => false;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantScopeFilterTests*"`
Expected: FAIL——两个用例都会看到 3 行(过滤器还不存在,`AsQueryable()` 直接查全表)或 `TenantId` 仍为 null(AOP 未接入)。

- [ ] **Step 3: 实现**

`backend/src/SmartAdmin.Core/Options/AdminDatabaseConnectionOptions.cs`,紧邻 `ApplyAuditAop` 之后追加:

```csharp
    /// <summary>
    /// 是否挂租户隔离全局过滤器(<c>ITenantScoped</c>)。
    /// <para><b>默认 false</b>:副库通常不按 TenantId 建模;主库固定开启,不受本项影响。</para>
    /// </summary>
    public bool ApplyTenantFilter { get; set; }
```

`backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs` 三处改动:

1) 私有 `HookPolicy` 结构体(文件末尾)追加一个字段,**接在已有四个字段最后**(纯新增,不打乱已有的位置参数顺序):

```csharp
    /// <summary>单条连接的钩子策略(主库全开 / 副库按 Options)。</summary>
    private readonly record struct HookPolicy(
        bool ApplySoftDeleteFilter,
        bool ApplyDataScopeFilter,
        bool ApplyAuditAop,
        int SlowSqlMillis,
        bool ApplyTenantFilter)
    {
        public static HookPolicy ForMain(int slowSqlMillis) =>
            new(ApplySoftDeleteFilter: true, ApplyDataScopeFilter: true, ApplyAuditAop: true, SlowSqlMillis: slowSqlMillis, ApplyTenantFilter: true);

        public static HookPolicy ForAdditional(AdminDatabaseConnectionOptions o) =>
            new(o.ApplySoftDeleteFilter, o.ApplyDataScopeFilter, o.ApplyAuditAop, o.SlowSqlMillis, o.ApplyTenantFilter);

        /// <summary>未知 ConfigId 的安全兜底:不挂业务钩子。</summary>
        public static HookPolicy ForAdditionalBare() =>
            new(ApplySoftDeleteFilter: false, ApplyDataScopeFilter: false, ApplyAuditAop: false, SlowSqlMillis: 0, ApplyTenantFilter: false);
    }
```

2) `AttachHooks` 局部函数里,`ApplyDataScopeFilter` 那个 `if` 块结束之后(紧接着 `ApplyAuditAop` 块之前)插入:

```csharp
                if (policy.ApplyTenantFilter)
                {
                    // 租户隔离(硬边界):刻意不设 IsUnrestricted 式逃逸条件——机构数据范围回答
                    // "同租户内我能看多少",这里回答"我根本不该看见别的租户",两者不能共用同一个开关。
                    // e.TenantId 与 currentUser.TenantId 都可能是 null(升级补列未回填 / 系统上下文无租户);
                    // SQL 的 NULL = NULL 恒非真,天然拒绝而不是意外放行,不需要额外判空。
                    client.QueryFilter.AddTableFilter<ITenantScoped>(e => e.TenantId == currentUser.TenantId);
                }
```

3) `DataExecuting` 的 `InsertByObject` 分支里,`CreateOrgId` 那个 `else if` 之后(仍在 `break;` 之前)追加:

```csharp
                                // TenantId 未指定(实现 ITenantScoped 的实体)→ 填当前用户所属租户;
                                // 无租户上下文(系统写入/未登录)则留空,交给 TenantBackfillHook 在升级期统一处理。
                                else if (info is { PropertyName: nameof(ITenantScoped.TenantId), EntityValue: ITenantScoped { TenantId: null } } && currentUser.TenantId is { } insTenantId)
                                    info.SetValue(insTenantId);
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantScopeFilterTests*"`
Expected: PASS(2 passed)。同时补跑一次既有过滤器/AOP 的回归,证明新增分支没有影响老逻辑:
Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*DataScopeTests*" --filter-class "*SoftDeleteAuditTests*" --filter-class "*OrgAuditEntityTests*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Core/Options/AdminDatabaseConnectionOptions.cs backend/src/SmartAdmin.SqlSugar/SqlSugarSetup.cs backend/tests/SmartAdmin.Tests/TenantScopeFilterTests.cs
git commit -m "feat(backend): 租户隔离全局过滤器,主库固定开启且不设逃逸条件

ITenantScoped 过滤器与既有 IOrgScoped 过滤器并列注册,插入 AOP 同步接入 TenantId 自动填充,写法都在 SqlSugarSetup.AttachHooks 一处收口,不改动既有软删/机构范围逻辑。"
```

---

### Task 8: 升级回填钩子(`TenantBackfillHook`)

**Files:**
- Create: `backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs`
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs`(登记 `IDatabaseReadyHook`)
- Test: `backend/tests/SmartAdmin.Tests/TenantBackfillUpgradeTests.cs`

**Interfaces:**
- Consumes: `IDatabaseReadyHook`(既有多实现扩展点,`TryAddEnumerable`)、`DefaultTenantSeed.DEFAULT_TENANT_ID`(Task 3)。
- Produces: 每次启动自动把 `TenantId IS NULL` 的存量行回填到默认租户。**Task 9/10 迁移更多实体到 `TenantEntity` 时,要回到这个文件给构造函数加对应的 `IRepository<T>` 参数、`OnDatabaseReadyAsync` 里加一行回填**——本任务先只接 `SysUser`。

- [ ] **Step 1: 写失败测试——模拟老库升级,验证重启后自动回填**

```csharp
// backend/tests/SmartAdmin.Tests/TenantBackfillUpgradeTests.cs
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 升级回填契约:已发版表补列后 TenantId 为 null 的存量行,重启后必须被回填到默认租户,
/// 否则套上 ITenantScoped 过滤器后这些行会对所有人不可见——等同升级后数据"消失"。
/// 仿 CodeFirstNullableUpgradeTests 的"先有数据 → 退化成老库状态 → 二次启动补回"手法。
/// </summary>
public class TenantBackfillUpgradeTests
{
    [Fact]
    public async Task Legacy_null_TenantId_row_is_backfilled_to_default_tenant_on_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"smart-tenant-backfill-{Guid.NewGuid():N}.db");

        try
        {
            using (var v1 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false, FreshDatabase = true })
            {
                _ = v1.CreateClient();
                using var scope = v1.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

                // 模拟"升级前的老库":把种子超管这一行的 TenantId 直接置空(绕过仓储,不经过滤器/AOP)
                await db.Updateable<SysUser>().SetColumns(u => u.TenantId == null).Where(u => u.IsSuperAdmin).ExecuteCommandAsync();
            }

            using var v2 = new AdminAppFactory { DbPath = dbPath, DeleteDbOnDispose = false };
            _ = v2.CreateClient();
            using var s2 = v2.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<ISqlSugarClient>();

            var admin = await db2.Queryable<SysUser>().Where(u => u.IsSuperAdmin).FirstAsync();
            Assert.NotNull(admin);
            Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, admin!.TenantId);
        }
        finally
        {
            TestDb.Cleanup(dbPath, dbPath);
        }
    }
}
```

注:第二次启动的读取直接用 `db2.Queryable<SysUser>()`(裸 SqlSugar,不经 `IRepository<>`)而不是 `IRepository<SysUser>.GetFirstAsync`——这个断言要证明的是"数据库里这一行确实被回填了",不应该被 `ITenantScoped` 过滤器本身要不要按当前系统上下文放行这行数据的问题干扰(系统上下文 `TenantId` 恒为 `null`,而 `Queryable<T>()` 同样会经过全局过滤器;真要严格避开过滤器应写 `.ClearFilter<ITenantScoped>()`,这里选择直接断言库里的值,更直接)。若实现阶段发现 `Queryable<SysUser>()` 仍被过滤导致查不到行,改成 `db2.Queryable<SysUser>().ClearFilter<ITenantScoped>().Where(...)`。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantBackfillUpgradeTests*"`
Expected: FAIL——`admin!.TenantId` 仍是 `null`(回填钩子不存在)。

- [ ] **Step 3: 实现**

```csharp
// backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs
using SqlSugar;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// 升级期一次性回填:把已发版表补列后 TenantId 为 null 的存量行,统一置为默认租户(DefaultTenantSeed)。
/// 不这样做的话,升级后这些行会被 ITenantScoped 过滤器判定为"看不见"——等于老部署升级后数据消失。
/// 新行此后一律由插入 AOP 自动填充(见 SqlSugarSetup),不会再产生 null,所以这里只处理"存量"。
/// 每次启动都跑,只更新真正为 null 的行,第二次起是 no-op,不需要额外的"是否已回填过"标记。
/// <para>迁移更多实体到 TenantEntity 时(见 Task 9/10),回这个类加对应的 IRepository&lt;T&gt; 构造参数
/// 与一行 UpdateAsync 调用——这里刻意不用反射遍历所有 ITenantScoped 类型,保持每个实体显式可见。</para>
/// </summary>
public class TenantBackfillHook(IRepository<SysUser> users) : IDatabaseReadyHook
{
    public virtual async Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        await users.Db.Updateable<SysUser>()
            .SetColumns(u => u.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID)
            .Where(u => u.TenantId == null)
            .ExecuteCommandAsync(cancellationToken);
    }
}
```

`backend/src/SmartAdmin.Services/ServicesSetup.cs` 追加:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Transient<IDatabaseReadyHook, TenantBackfillHook>());
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantBackfillUpgradeTests*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs backend/src/SmartAdmin.Services/ServicesSetup.cs backend/tests/SmartAdmin.Tests/TenantBackfillUpgradeTests.cs
git commit -m "feat(backend): 升级回填钩子,老库补列后的存量行自动落进默认租户

IDatabaseReadyHook 多实现扩展点接入;每次启动幂等执行,只动 TenantId 为 null 的行。当前只覆盖 SysUser,Task 9/10 迁移更多实体时在这里补齐。"
```

---

### Task 9: 迁移 RBAC 核心实体到 `TenantEntity` + 跨租户隔离回归

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Entities/SysOrg.cs`、`SysRole.cs`、`SysUserRole.cs`、`SysRoleMenu.cs`、`SysRoleDataScope.cs`、`SysPosition.cs`(均 `: BaseEntity` → `: TenantEntity`)
- Modify: `backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs`(补齐这 6 个实体的回填)
- Test: `backend/tests/SmartAdmin.Tests/TenantIsolationTests.cs`

**Interfaces:**
- Consumes: `TenantEntity`(Task 1)、Task 7 的全局过滤器与 AOP(自动生效,不需要这几个实体各自写代码)、`TenantController`(Task 5,测试里用来建两个租户)。
- Produces: 无新符号——纯粹是"把 Task 7 的能力接到真实业务表上"并用 HTTP 级测试证明。

- [ ] **Step 1: 写失败测试——两个真实租户各自建机构,互相看不见,即使角色是超管(数据范围天然不受限)**

```csharp
// backend/tests/SmartAdmin.Tests/TenantIsolationTests.cs
using System.Net.Http.Headers;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// 跨租户 HTTP 级隔离回归:两个租户各自建机构,互相看不到——即使操作者是租户内超管(数据范围天然
/// "全部")也看不穿,证明 ITenantScoped 过滤器不受机构数据范围/超管身份影响(spec §3)。
/// </summary>
public class TenantIsolationTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    private static async Task<HttpClient> NewTenantAdminClient(AdminAppFactory f, HttpClient platform, string codePrefix)
    {
        var code = $"{codePrefix}{Guid.NewGuid():N}"[..16];
        var account = $"{code}_admin";
        const string password = "Test@123456";

        var add = await platform.PostJson("/api/v1/sys/tenant/add", new
        {
            code, name = $"{code}-Inc", isolationMode = 1, enabled = true,
            adminAccount = account, adminPassword = password,
        });
        Assert.Equal(0, (await add.ReadEnvelope()).GetProperty("code").GetInt32());

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await client.LoginToken(account, password));
        return client;
    }

    [Fact]
    public async Task Two_tenants_cannot_see_each_others_orgs()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var clientA = await NewTenantAdminClient(f, platform, "tnA");
        var clientB = await NewTenantAdminClient(f, platform, "tnB");

        var addOrg = await (await clientA.PostJson("/api/v1/sys/org/add",
            new { name = "A的机构", code = $"ORG_A_{Guid.NewGuid():N}"[..16], parentId = 0, sort = 1, enabled = true })).ReadEnvelope();
        Assert.Equal(0, addOrg.GetProperty("code").GetInt32());
        var orgAId = addOrg.GetProperty("data").GetInt64();

        // 租户 B 的超管列表里看不到租户 A 建的机构
        var listB = (await (await clientB.GetAsync("/api/v1/sys/org/list")).ReadEnvelope()).GetProperty("data");
        var idsB = listB.EnumerateArray().Select(o => o.GetProperty("id").GetInt64()).ToList();
        Assert.DoesNotContain(orgAId, idsB);

        // 直接按 Id 越权查询 → 404(OrgNotFound),不暴露"存在但无权"
        var crossGet = await (await clientB.GetAsync($"/api/v1/sys/org/{orgAId}")).ReadEnvelope();
        Assert.Equal((int)ErrorCode.OrgNotFound, crossGet.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Two_tenants_cannot_see_each_others_roles()
    {
        using var f = new AdminAppFactory();
        var platform = await SuperAdminClient(f);

        var clientA = await NewTenantAdminClient(f, platform, "roleA");
        var clientB = await NewTenantAdminClient(f, platform, "roleB");

        var addRole = await (await clientA.PostJson("/api/v1/sys/role/add",
            new { name = "A的角色", code = $"ROLE_A_{Guid.NewGuid():N}"[..16], sort = 1, enabled = true })).ReadEnvelope();
        Assert.Equal(0, addRole.GetProperty("code").GetInt32());
        var roleAId = addRole.GetProperty("data").GetInt64();

        var pageB = (await (await clientB.GetAsync("/api/v1/sys/role/page?Current=1&Size=100")).ReadEnvelope()).GetProperty("data");
        var idsB = pageB.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
        Assert.DoesNotContain(roleAId, idsB);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantIsolationTests*"`
Expected: FAIL——`SysOrg`/`SysRole` 还是 `BaseEntity`,不受 `ITenantScoped` 过滤器约束,租户 B 能看到租户 A 的机构/角色。

- [ ] **Step 3: 实现**

六个文件里各自把类声明的 `: BaseEntity` 改成 `: TenantEntity`(其余内容、字段、索引一律不动):

- `backend/src/SmartAdmin.Services/Entities/SysOrg.cs`:`public class SysOrg : TenantEntity`
- `backend/src/SmartAdmin.Services/Entities/SysRole.cs`:`public class SysRole : TenantEntity`
- `backend/src/SmartAdmin.Services/Entities/SysUserRole.cs`:`public class SysUserRole : TenantEntity`
- `backend/src/SmartAdmin.Services/Entities/SysRoleMenu.cs`:`public class SysRoleMenu : TenantEntity`
- `backend/src/SmartAdmin.Services/Entities/SysRoleDataScope.cs`:`public class SysRoleDataScope : TenantEntity`
- `backend/src/SmartAdmin.Services/Entities/SysPosition.cs`:`public class SysPosition : TenantEntity`

`backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs` 补齐这六个实体的回填(构造函数加参数,方法体各加一行):

```csharp
public class TenantBackfillHook(
    IRepository<SysUser> users,
    IRepository<SysOrg> orgs,
    IRepository<SysRole> roles,
    IRepository<SysUserRole> userRoles,
    IRepository<SysRoleMenu> roleMenus,
    IRepository<SysRoleDataScope> roleDataScopes,
    IRepository<SysPosition> positions) : IDatabaseReadyHook
{
    public virtual async Task OnDatabaseReadyAsync(DatabaseReadyContext context, CancellationToken cancellationToken)
    {
        await users.Db.Updateable<SysUser>().SetColumns(u => u.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(u => u.TenantId == null).ExecuteCommandAsync(cancellationToken);
        await orgs.Db.Updateable<SysOrg>().SetColumns(o => o.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(o => o.TenantId == null).ExecuteCommandAsync(cancellationToken);
        await roles.Db.Updateable<SysRole>().SetColumns(r => r.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(r => r.TenantId == null).ExecuteCommandAsync(cancellationToken);
        await userRoles.Db.Updateable<SysUserRole>().SetColumns(ur => ur.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(ur => ur.TenantId == null).ExecuteCommandAsync(cancellationToken);
        await roleMenus.Db.Updateable<SysRoleMenu>().SetColumns(rm => rm.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(rm => rm.TenantId == null).ExecuteCommandAsync(cancellationToken);
        await roleDataScopes.Db.Updateable<SysRoleDataScope>().SetColumns(rds => rds.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(rds => rds.TenantId == null).ExecuteCommandAsync(cancellationToken);
        await positions.Db.Updateable<SysPosition>().SetColumns(p => p.TenantId == DefaultTenantSeed.DEFAULT_TENANT_ID).Where(p => p.TenantId == null).ExecuteCommandAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantIsolationTests*"`
Expected: PASS(2 passed)。再补跑一次全套既有 RBAC/数据范围相关测试,证明基类切换没有破坏单租户场景下的既有行为:
Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*DataScope*" --filter-class "*Rbac*" --filter-class "*Role*" --filter-class "*Position*" --filter-class "*Org*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Entities/SysOrg.cs backend/src/SmartAdmin.Services/Entities/SysRole.cs backend/src/SmartAdmin.Services/Entities/SysUserRole.cs backend/src/SmartAdmin.Services/Entities/SysRoleMenu.cs backend/src/SmartAdmin.Services/Entities/SysRoleDataScope.cs backend/src/SmartAdmin.Services/Entities/SysPosition.cs backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs backend/tests/SmartAdmin.Tests/TenantIsolationTests.cs
git commit -m "feat(backend): 机构/角色/职位/授权关联迁入 TenantEntity,HTTP 级隔离回归绿

六张 RBAC 核心表切换基类即自动获得 Task 7 的过滤器与 AOP,不需要各自写隔离代码。新增两租户各自建数据、互相看不见的端到端回归,覆盖'角色数据范围天然不受限也看不穿'这个安全关键场景。"
```

---

### Task 10: 迁移运维数据实体到 `TenantEntity`

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Entities/SysNotice.cs`、`SysNoticeReceiver.cs`、`SysNoticeRead.cs`、`SysFile.cs`、`SysLoginLog.cs`、`SysOpLog.cs`、`SysExceptionLog.cs`、`SysSession.cs`(均 `: BaseEntity` → `: TenantEntity`)
- Modify: `backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs`(补齐这 8 个实体的回填)
- Test: `backend/tests/SmartAdmin.Tests/TenantOperationalEntitiesTests.cs`

**Interfaces:**
- Consumes: `TenantEntity`(Task 1)、Task 7 的过滤器/AOP(自动生效)。
- Produces: 无新符号。

- [ ] **Step 1: 写失败测试——CodeFirst 建表后这 8 张表都有 `TenantId` 列,且默认租户下能读到种子期产生的会话/日志行**

```csharp
// backend/tests/SmartAdmin.Tests/TenantOperationalEntitiesTests.cs
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Services;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 运维数据实体(通知/文件/日志/会话)迁入 TenantEntity 的表结构回归——不重复 Task 9 已经证明过的
/// HTTP 级跨租户隔离手法,只验证列存在 + 种子期产生的数据能被正确回填/归属默认租户。
/// </summary>
public class TenantOperationalEntitiesTests
{
    public static IEnumerable<object[]> MigratedTables() =>
    [
        ["sys_notice"], ["sys_notice_receiver"], ["sys_notice_read"], ["sys_file"],
        ["sys_login_log"], ["sys_op_log"], ["sys_exception_log"], ["sys_session"],
    ];

    [Theory]
    [MemberData(nameof(MigratedTables))]
    public async Task Table_has_nullable_TenantId_column(string tableName)
    {
        using var f = new AdminAppFactory();
        _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

        var cols = db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        var tenantCol = Assert.Single(cols, c => c.DbColumnName.Equals("TenantId", StringComparison.OrdinalIgnoreCase));
        Assert.True(tenantCol.IsNullable);
    }

    [Fact]
    public async Task Login_creates_a_session_row_scoped_to_the_logging_in_users_tenant()
    {
        using var f = new AdminAppFactory();
        var c = f.CreateClient();
        _ = await c.LoginToken("superAdmin", "Test@123456");

        using var scope = f.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IRepository<SysSession>>();
        var mine = await sessions.AsQueryable().Where(s => s.Account == "superAdmin").ToListAsync();

        Assert.NotEmpty(mine);
        Assert.All(mine, s => Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID, s.TenantId));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantOperationalEntitiesTests*"`
Expected: FAIL——8 张表都没有 `TenantId` 列;会话行 `TenantId` 为 `null`(登录时 `SessionService.OpenAsync` 插入 `SysSession` 走的是同一条插入 AOP,一旦实体本身没实现 `ITenantScoped`,AOP 判断分支不命中,自然不会填)。

- [ ] **Step 3: 实现**

八个文件里各自把类声明的 `: BaseEntity` 改成 `: TenantEntity`:

- `backend/src/SmartAdmin.Services/Entities/SysNotice.cs`
- `backend/src/SmartAdmin.Services/Entities/SysNoticeReceiver.cs`
- `backend/src/SmartAdmin.Services/Entities/SysNoticeRead.cs`
- `backend/src/SmartAdmin.Services/Entities/SysFile.cs`
- `backend/src/SmartAdmin.Services/Entities/SysLoginLog.cs`
- `backend/src/SmartAdmin.Services/Entities/SysOpLog.cs`
- `backend/src/SmartAdmin.Services/Entities/SysExceptionLog.cs`
- `backend/src/SmartAdmin.Services/Entities/SysSession.cs`

`backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs` 构造函数再加 8 个 `IRepository<T>` 参数,方法体各加一行(与 Task 9 同样的写法,此处从略,按 Task 9 模板逐一补齐:`SysNotice`/`SysNoticeReceiver`/`SysNoticeRead`/`SysFile`/`SysLoginLog`/`SysOpLog`/`SysExceptionLog`/`SysSession`)。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*TenantOperationalEntitiesTests*"`
Expected: PASS(9 passed:8 个 Theory + 1 个 Fact)。再跑一次通知/文件/日志/会话相关的既有测试,确认基类切换没有破坏原有功能:
Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*Notice*" --filter-class "*File*" --filter-class "*Log*" --filter-class "*Session*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Entities/SysNotice.cs backend/src/SmartAdmin.Services/Entities/SysNoticeReceiver.cs backend/src/SmartAdmin.Services/Entities/SysNoticeRead.cs backend/src/SmartAdmin.Services/Entities/SysFile.cs backend/src/SmartAdmin.Services/Entities/SysLoginLog.cs backend/src/SmartAdmin.Services/Entities/SysOpLog.cs backend/src/SmartAdmin.Services/Entities/SysExceptionLog.cs backend/src/SmartAdmin.Services/Entities/SysSession.cs backend/src/SmartAdmin.Services/Seed/TenantBackfillHook.cs backend/tests/SmartAdmin.Tests/TenantOperationalEntitiesTests.cs
git commit -m "feat(backend): 通知/文件/日志/会话迁入 TenantEntity

内核全部运维数据表纳入租户隔离,回填钩子同步补齐。至此 spec §2.3 列出的迁移清单全部落地。"
```

---

### Task 11: 菜单种子——"租户管理"目录正式上线

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs`

**Interfaces:**
- Consumes: `TenantController` 的 5 条路由(Task 5)。
- Produces: 无新代码符号,是纯数据(菜单树 + 权限按钮),前端 Task 12/13 依赖 `Component = "system/tenant/index"` 这个页面 key。

- [ ] **Step 1: 确认失败——本任务复用既有测试,不新写测试类**

`backend/tests/SmartAdmin.Tests/PermissionCodeConsistencyTests.cs` 双向锁"每个受 `[RolePermission]` 保护的端点都要出现在某颗菜单按钮里";`TenantController`(Task 5)的 5 个端点目前没有任何按钮引用,这条既有测试就是本任务的失败测试。

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*PermissionCodeConsistencyTests*"`
Expected: FAIL——`TenantController` 的 5 个端点已经受 `[RolePermission]` 保护,但没有任何菜单按钮携带它们的权限码。

- [ ] **Step 2: 补种子数据**

`backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs`,在 `// ═══ 1xx 工作台 ═══` 段与 `// ═══ 3xx 组织管理 ═══` 段之间(替换掉此前 Task"菜单编号重排"留下的占位注释段落 `// ═══ 2xx 租户管理 ═══════...规划中,尚未落地...`)插入:

```csharp
        // ═══ 2xx 租户管理 ═══════════════════════════════════════════
        new SysMenu { Id = 200, ParentId = 0, Type = MenuType.Catalog, Title = "租户管理", Permission = "", Icon = "ph:buildings-duotone", Sort = 1, Enabled = true, ModuleId = DefaultModuleSeed.BUILTIN_MODULE_ID },

        // 租户列表页(TenantController)。只有平台管理员实际能操作,菜单本身对所有角色可授权,
        // 门禁在 TenantService.RequirePlatformAdmin(见 Task 4),不是靠菜单可见性把人挡在外面。
        new SysMenu { Id = 210, ParentId = 200, Type = MenuType.Menu, Title = "租户列表", Permission = "", Path = "/system/tenant", Component = "system/tenant/index", Icon = "ph:identification-card-duotone", Sort = 1, Enabled = true, Visible = true },
        new SysMenu { Id = 211, ParentId = 210, Type = MenuType.Button, Title = "租户-查询", Permission = Codes("GET:/api/v1/sys/tenant/page", "GET:/api/v1/sys/tenant/{id}"), Sort = 1, Enabled = true },
        new SysMenu { Id = 212, ParentId = 210, Type = MenuType.Button, Title = "租户-新增", Permission = "POST:/api/v1/sys/tenant/add", Sort = 2, Enabled = true },
        new SysMenu { Id = 213, ParentId = 210, Type = MenuType.Button, Title = "租户-更新", Permission = "PUT:/api/v1/sys/tenant/{id}", Sort = 3, Enabled = true },
        new SysMenu { Id = 214, ParentId = 210, Type = MenuType.Button, Title = "租户-删除", Permission = "DELETE:/api/v1/sys/tenant/{id}", Sort = 4, Enabled = true },

        // 220(数据库配置)留给二期独立库,现在不占行——见头部分区表注释。
```

同时把头部 `<remarks>` 里那句"2xx 留给规划中的租户管理(尚未落地,不占行)"顺手改成"2xx 租户管理"(与其它分区的措辞一致,不再强调"规划中")。

- [ ] **Step 3: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*PermissionCodeConsistencyTests*" --filter-class "*MenuSeedIdLayoutTests*" --filter-class "*SeedIdRangeTests*"`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs
git commit -m "feat(backend): 租户管理菜单正式上线(200/210/211-214)

TenantController 五个端点接进权限码体系,220(数据库配置)留给二期继续不占行。"
```

---

### Task 12: 前端类型 + API(`SysTenant` / `TenantInput` / `tenantApi`)

**Files:**
- Modify: `web/packages/admin/src/types/api.ts`
- Modify: `web/packages/admin/src/api/index.ts`

**Interfaces:**
- Consumes: Task 11 上线的 5 条路由。
- Produces: `SysTenant`、`TenantInput`、`TenantCreateInput` 类型;`tenantApi.{page,add,update,remove}`——Task 13 的页面直接导入。

**前置步骤**:后端跑起来后重新生成契约类型(`cd web && npm run gen:api`,更新 `web/packages/admin/src/api/schema.d.ts`)。这一步没有"失败测试"可跑(纯类型 + 薄封装,遵照仓库既有 `positionApi` 的写法照抄,`create-crud-frontend.md` 已经把这当作不需要单独测试的产出),验证方式是 Step 4 的 `npm run typecheck`。

- [ ] **Step 1: 追加类型定义**

```typescript
// 在 web/packages/admin/src/types/api.ts 末尾追加

/** 租户行(后端 SysTenant) */
export interface SysTenant {
  id: number
  code: string
  name: string
  contactName?: string | null
  contactPhone?: string | null
  expireTime?: string | null
  isolationMode: number // 1=共享库(一期唯一可选) 2=独立库(二期,前端锁定不可选)
  connectionConfigId?: string | null
  enabled: boolean
  remark?: string | null
  createTime?: string
}

/** 租户编辑入参(后端 TenantInput) */
export interface TenantInput {
  code: string
  name: string
  contactName?: string | null
  contactPhone?: string | null
  expireTime?: string | null
  isolationMode: number
  enabled: boolean
  remark?: string | null
}

/** 租户创建入参(后端 TenantCreateInput:比 TenantInput 多两个初始管理员字段) */
export interface TenantCreateInput extends TenantInput {
  adminAccount: string
  adminPassword: string
}
```

- [ ] **Step 2: 追加 API 函数**

```typescript
// 在 web/packages/admin/src/api/index.ts 中追加,并在文件顶部 import type 里补上 SysTenant/TenantInput/TenantCreateInput

export const tenantApi = {
  page: (params: { page: number; pageSize: number; name?: string; sortField?: string; sortOrder?: string }) =>
    client
      .GET('/api/v1/sys/tenant/page', {
        params: {
          query: {
            ...pageParams(params),
            Name: params.name,
            SortField: params.sortField,
            SortOrder: params.sortOrder,
          },
        },
      })
      .then((r) => toPage<SysTenant>(r)),
  add: (body: TenantCreateInput) =>
    client.POST('/api/v1/sys/tenant/add', { body }).then((r) => unwrap<number>(r)),
  update: (id: number, body: TenantInput) =>
    client.PUT('/api/v1/sys/tenant/{id}', { params: { path: { id } }, body }).then((r) => unwrap<boolean>(r)),
  remove: (id: number) =>
    client.DELETE('/api/v1/sys/tenant/{id}', { params: { path: { id } } }).then((r) => unwrap<boolean>(r)),
}
```

- [ ] **Step 3: (无独立单测——类型与薄封装,见前置说明)**

- [ ] **Step 4: 跑类型检查确认通过**

Run: `cd web && npm run gen:api && npm run typecheck`
Expected: 无类型错误(需要后端已启动,`gen:api` 才能连上 `/openapi/v1.json` 拉到 `TenantController` 的路由定义)。

- [ ] **Step 5: Commit**

```bash
git add web/packages/admin/src/types/api.ts web/packages/admin/src/api/index.ts web/packages/admin/src/api/schema.d.ts
git commit -m "feat(web): 租户类型定义与 API 封装"
```

---

### Task 13: 前端页面 `views/system/tenant/index.vue` + i18n

**Files:**
- Create: `web/packages/admin/src/views/system/tenant/index.vue`
- Modify: `web/packages/admin/src/locales/zh-CN.ts`
- Modify: `web/packages/admin/src/locales/en-US.ts`

**Interfaces:**
- Consumes: `tenantApi`(Task 12)、`SmartTable`/`FormContainer`/`StatusSwitch`/`useConfirm`/`useAuthStore`/`translateError`(既有共享组件,见 `web/COMPONENTS.md`)。
- Produces: 页面 key `system/tenant/index`,与 Task 11 菜单种子的 `Component` 字段对应。

- [ ] **Step 1: 写页面(比标准模板多一个"隔离模式"分段控件 + 创建态才显示的初始管理员两个字段)**

```vue
<!-- web/packages/admin/src/views/system/tenant/index.vue -->
<script setup lang="ts">
import { h, reactive, ref } from 'vue'
import {
  NButton, NSpace, NInput, NDatePicker, NPopconfirm, NTag,
  NForm, NFormItem, NSwitch,
  useMessage, type FormInst, type FormRules,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { tenantApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import type { TenantCreateInput, TenantInput, SysTenant } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<SysTenant>>()

const toInput = (r: SysTenant): TenantInput => ({
  code: r.code, name: r.name, contactName: r.contactName, contactPhone: r.contactPhone,
  expireTime: r.expireTime, isolationMode: r.isolationMode, enabled: r.enabled, remark: r.remark,
})

const columns: SmartTableColumn<SysTenant>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { key: 'code', title: () => t('tenant.code'), search: true },
  { key: 'name', title: () => t('tenant.name'), search: true },
  {
    key: 'isolationMode',
    title: () => t('tenant.isolationMode'),
    width: 100,
    render: (r) => h(NTag, { size: 'small', type: r.isolationMode === 1 ? 'info' : 'default' }, () =>
      r.isolationMode === 1 ? t('tenant.isolationModeShared') : t('tenant.isolationModeStandalone')),
  },
  { key: 'contactName', title: () => t('tenant.contactName') },
  { key: 'contactPhone', title: () => t('tenant.contactPhone') },
  { key: 'expireTime', title: () => t('tenant.expireTime'), format: 'date' },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 90,
    render: (r) =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !authStore.hasPerm('PUT:/api/v1/sys/tenant/{id}'),
        request: (next: boolean) => tenantApi.update(r.id, { ...toInput(r), enabled: next }),
        'onUpdate:value': (v: boolean) => { r.enabled = v },
      }),
  },
  { key: 'createTime', title: () => t('common.createTime'), format: 'datetime' },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 120,
    fixed: 'right',
    hideInSetting: true,
    render: (r) =>
      h(NSpace, { size: 4, wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/tenant/{id}')
          ? h(NButton, { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) }, () => t('common.edit'))
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/tenant/{id}')
          ? h(NPopconfirm, {
              onPositiveClick: () =>
                run(() => tenantApi.remove(r.id), t('tenant.deleted')).then((ok) => { if (ok) tableRef.value?.refresh() }),
            }, {
              trigger: () => h(NButton, { size: 'small', quaternary: true, type: 'error' }, () => t('common.delete')),
              default: () => t('tenant.deleteConfirm', { name: r.name }),
            })
          : null,
      ]),
  },
]

// ── 新增/编辑弹窗 ──
const show = ref(false)
const formRef = ref<FormInst | null>(null)
const editingId = ref<number | null>(null)
const rules: FormRules = {
  code: { required: true, whitespace: true, message: () => t('tenant.codeRequired'), trigger: ['input', 'blur'] },
  name: { required: true, whitespace: true, message: () => t('tenant.nameRequired'), trigger: ['input', 'blur'] },
  adminAccount: { required: true, whitespace: true, message: () => t('tenant.adminAccountRequired'), trigger: ['input', 'blur'] },
  adminPassword: { required: true, whitespace: true, message: () => t('tenant.adminPasswordRequired'), trigger: ['input', 'blur'] },
}
const blank = (): TenantCreateInput => ({
  code: '', name: '', isolationMode: 1, enabled: true, adminAccount: '', adminPassword: '',
})
const form = reactive<TenantCreateInput>(blank())

function openAdd() {
  editingId.value = null
  Object.assign(form, blank())
  show.value = true
}
function openEdit(r: SysTenant) {
  editingId.value = r.id
  Object.assign(form, toInput(r), { adminAccount: '', adminPassword: '' })
  show.value = true
}
async function save() {
  await formRef.value?.validate()
  try {
    if (editingId.value === null) await tenantApi.add({ ...form })
    else await tenantApi.update(editingId.value, { ...form })
    message.success(t('tenant.saved'))
    await tableRef.value?.refresh()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>

<template>
  <SmartTable
    ref="tableRef"
    :default-page-size="100"
    :columns="columns"
    :fetcher="tenantApi.page"
    flex-height
    virtual-scroll
    storage-key="sys-tenant"
    @error="(e) => message.error(translateError(e))"
  >
    <template #toolbar>
      <n-button v-auth="'POST:/api/v1/sys/tenant/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
  </SmartTable>

  <FormContainer
    v-model:show="show"
    :title="editingId === null ? t('tenant.addTitle') : t('tenant.editTitle')"
    :width="480"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="90">
      <n-form-item :label="t('tenant.code')" path="code">
        <n-input v-model:value="form.code" :disabled="editingId !== null" :placeholder="t('tenant.codePlaceholder')" />
      </n-form-item>
      <n-form-item :label="t('tenant.name')" path="name">
        <n-input v-model:value="form.name" :placeholder="t('tenant.name')" />
      </n-form-item>

      <!-- 隔离模式:一期只有"共享库"可选,"独立库"置灰并标"二期开放"——见设计稿的路线图展示 -->
      <n-form-item :label="t('tenant.isolationMode')">
        <n-space vertical style="width: 100%">
          <div
            class="iso-option"
            :class="{ selected: form.isolationMode === 1 }"
            @click="form.isolationMode = 1"
          >
            <div class="iso-title">{{ t('tenant.isolationModeShared') }}</div>
            <div class="iso-desc">{{ t('tenant.isolationModeSharedDesc') }}</div>
          </div>
          <div class="iso-option disabled" :title="t('tenant.isolationModePhase2')">
            <div class="iso-title">{{ t('tenant.isolationModeStandalone') }}<n-tag size="tiny" type="warning" style="margin-left: 6px">{{ t('tenant.isolationModePhase2') }}</n-tag></div>
            <div class="iso-desc">{{ t('tenant.isolationModeStandaloneDesc') }}</div>
          </div>
        </n-space>
      </n-form-item>

      <n-form-item :label="t('tenant.contactName')">
        <n-input v-model:value="form.contactName ?? undefined" />
      </n-form-item>
      <n-form-item :label="t('tenant.contactPhone')">
        <n-input v-model:value="form.contactPhone ?? undefined" />
      </n-form-item>
      <n-form-item :label="t('tenant.expireTime')">
        <n-date-picker v-model:formatted-value="form.expireTime" value-format="yyyy-MM-dd" type="date" style="width: 100%" />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>

      <!-- 初始管理员账号:只在新增时出现,编辑已有租户不改其管理员 -->
      <template v-if="editingId === null">
        <div class="form-section"><div class="form-section__title">{{ t('tenant.initialAdmin') }}</div></div>
        <n-form-item :label="t('tenant.adminAccount')" path="adminAccount">
          <n-input v-model:value="form.adminAccount" :placeholder="t('tenant.adminAccountPlaceholder')" />
        </n-form-item>
        <n-form-item :label="t('tenant.adminPassword')" path="adminPassword">
          <n-input v-model:value="form.adminPassword" type="password" show-password-on="click" :placeholder="t('tenant.adminPasswordPlaceholder')" />
        </n-form-item>
      </template>

      <n-form-item :label="t('common.remark')">
        <n-input v-model:value="form.remark ?? undefined" type="textarea" :rows="2" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
.iso-option {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md, 10px);
  padding: 10px 12px;
  cursor: pointer;
}
.iso-option.selected {
  border-color: var(--color-primary);
  background: var(--color-primary-light);
}
.iso-option.disabled {
  cursor: not-allowed;
  opacity: 0.6;
  background: var(--color-fill);
}
.iso-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-primary);
  display: flex;
  align-items: center;
}
.iso-desc {
  font-size: 11.5px;
  color: var(--color-text-tertiary);
  margin-top: 3px;
  line-height: 1.5;
}
</style>
```

- [ ] **Step 2: i18n——`zh-CN.ts` 追加(`error` 对象下与顶层各一处)**

```typescript
// zh-CN.ts 顶层追加
tenant: {
  title: '租户管理',
  code: '租户编码',
  name: '租户名称',
  codePlaceholder: '如 acme,创建后不可修改',
  contactName: '联系人',
  contactPhone: '联系电话',
  expireTime: '到期时间',
  isolationMode: '隔离模式',
  isolationModeShared: '共享库',
  isolationModeSharedDesc: '与其它租户共用主库,按 TenantId 过滤隔离。一期唯一可选项。',
  isolationModeStandalone: '独立库',
  isolationModeStandaloneDesc: '该租户的业务数据物理隔离到独立连接串;组织架构/用户/权限仍统一存于共享库(混合模式)。',
  isolationModePhase2: '二期开放',
  initialAdmin: '初始管理员',
  adminAccount: '管理员账号',
  adminAccountPlaceholder: '该租户的第一个登录账号',
  adminPassword: '管理员密码',
  adminPasswordPlaceholder: '首次登录后需强制修改',
  codeRequired: '请输入租户编码',
  nameRequired: '请输入租户名称',
  adminAccountRequired: '请输入管理员账号',
  adminPasswordRequired: '请输入管理员密码',
  addTitle: '新增租户',
  editTitle: '编辑租户',
  deleteConfirm: '确定删除租户「{name}」?',
  saved: '保存成功',
  deleted: '删除成功',
},
```

```typescript
// zh-CN.ts 的 error 对象下追加
tenant: {
  notFound: '目标租户不存在',
  codeExists: '租户编码已存在',
  isolationModeNotSupported: '一期只支持共享库隔离模式,独立库二期开放',
  protected: '默认租户受保护,不可删除或禁用',
  platformAdminRequired: '该操作仅平台管理员可执行',
},
```

`en-US.ts` 补对应英文键(`title/code/name/...` 全套,与 `zh-CN.ts` 结构一一对应;`locales/parity.spec.ts` 会核对两份键集合一致,漏一个就红)。

- [ ] **Step 3: (页面 + i18n 无独立单测——手动验证清单见 Step 4)**

- [ ] **Step 4: 验证**

Run:
```bash
cd web
npm run typecheck
npm run lint
npm run format:check
npm test          # locales/parity.spec.ts 等既有用例覆盖 zh/en 键对齐
npm run dev        # 手动验证
```

手动验证清单(登录种子超管 `superAdmin`/`Test@123456`,侧栏应出现"租户管理"目录):
- [ ] 列表加载、按编码/名称搜索、分页正常
- [ ] 新增租户:隔离模式默认选中"共享库","独立库"选项置灰且不可点选;填完初始管理员账号密码保存成功;新登录该账号能进系统
- [ ] 编辑已有租户:不显示初始管理员两个字段;保存成功
- [ ] 删除默认租户(`Id=1`)时被后端拒绝,提示"默认租户受保护"对应文案
- [ ] StatusSwitch 切换启停后刷新页面状态不回弹
- [ ] 无权限时新增/编辑/删除按钮隐藏

- [ ] **Step 5: Commit**

```bash
git add web/packages/admin/src/views/system/tenant/index.vue web/packages/admin/src/locales/zh-CN.ts web/packages/admin/src/locales/en-US.ts
git commit -m "feat(web): 租户管理页面

隔离模式用分段选项展示路线图,独立库置灰标二期开放;新增时额外收初始管理员账号密码,编辑时隐藏。"
```

---

### Task 14: 个人资料接口带出租户名 + 前端只读展示

**Files:**
- Modify: `backend/src/SmartAdmin.Services/Personal/PersonalModels.cs`(`UserProfile` 加 `TenantId`/`TenantName`)
- Modify: `backend/src/SmartAdmin.Services/Personal/PersonalService.cs`(`GetProfileAsync` 回填 + `TenantNameAsync` 辅助方法)
- Modify: `web/packages/admin/src/stores/user.ts`(`UserInfo` 加 `tenantName`)
- Modify: `web/packages/admin/src/composables/useModule.ts`(`enterInitial` 回填)
- Modify: `backend/tests/SmartAdmin.Tests/UserProfileFieldsTests.cs`

**Interfaces:**
- Consumes: `SysUser.TenantId`(Task 6)、`SysTenant`(Task 3)。
- Produces: `GET /api/v1/personal/profile` 响应体新增 `tenantName`(spec §6.4 要求的只读展示字段,不新增任何"当前租户"可写状态,也不做租户切换器——见 spec §6.3)。

- [ ] **Step 1: 写失败测试——个人资料应该带出所属租户名称**

在 `backend/tests/SmartAdmin.Tests/UserProfileFieldsTests.cs` 追加一个 `[Fact]`(与既有 `Profile_fields_and_director_name_round_trip` 相邻):

```csharp
    [Fact]
    public async Task Profile_includes_tenant_name()
    {
        using var f = new AdminAppFactory();
        var c = await SuperAdminClient(f);

        var profile = (await (await c.GetAsync("/api/v1/personal/profile")).ReadEnvelope()).GetProperty("data");
        Assert.Equal("默认租户", profile.GetProperty("tenantName").GetString());
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserProfileFieldsTests*"`
Expected: FAIL——`profile.GetProperty("tenantName")` 抛异常(属性不存在)。

- [ ] **Step 3: 实现**

`backend/src/SmartAdmin.Services/Personal/PersonalModels.cs` 的 `UserProfile` 里,紧邻 `PositionName` 之后追加:

```csharp
    public long? TenantId { get; init; }

    /// <summary>所属租户名称(已删则为 null)。前端只读展示,不参与任何切换交互(spec §6.3)。</summary>
    public string? TenantName { get; init; }
```

`backend/src/SmartAdmin.Services/Personal/PersonalService.cs` 的 `GetProfileAsync` 里,`OrgName = await OrgNameAsync(user.OrgId),` 之后追加:

```csharp
            TenantId = user.TenantId,
            TenantName = await TenantNameAsync(user.TenantId),
```

紧邻 `OrgNameAsync`/`PositionNameAsync` 之后追加(同款"走 `users.Db` 逃生舱"写法,不改构造函数——加构造参数对继承本类的消费者是破坏性变更):

```csharp
    /// <summary>取租户名称(未分配/已删则 null)。同 OrgNameAsync,SysTenant 亦非机构范围实体。</summary>
    protected virtual async Task<string?> TenantNameAsync(long? tenantId) =>
        tenantId is null ? null : await users.Db.Queryable<SysTenant>().Where(t => t.Id == tenantId.Value).Select(t => t.Name).FirstAsync();
```

前端:`web/packages/admin/src/stores/user.ts` 的 `UserInfo` 接口,紧邻 `isSuperAdmin?: boolean` 之后追加:

```typescript
  /** 所属租户名称,登录后经 /personal/profile 回填;只读展示,不参与任何切换交互。 */
  tenantName?: string | null
```

`web/packages/admin/src/composables/useModule.ts` 的 `enterInitial` 里,把:

```typescript
      personalApi
        .profile()
        .then(p => ({ sadm: p.isSuperAdmin, avatar: p.avatar ?? null }))
        .catch(() => ({ sadm: useUserStore().userInfo?.isSuperAdmin ?? false, avatar: null })),
```

改成:

```typescript
      personalApi
        .profile()
        .then(p => ({ sadm: p.isSuperAdmin, avatar: p.avatar ?? null, tenantName: p.tenantName ?? null }))
        .catch(() => ({ sadm: useUserStore().userInfo?.isSuperAdmin ?? false, avatar: null, tenantName: null })),
```

并把紧接着的回填逻辑:

```typescript
    if (user.userInfo) user.userInfo.avatar = profile.avatar
```

改成:

```typescript
    if (user.userInfo) {
      user.userInfo.avatar = profile.avatar
      user.userInfo.tenantName = profile.tenantName
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*UserProfileFieldsTests*"`
Expected: PASS。前端类型检查:
Run: `cd web && npm run gen:api && npm run typecheck`
Expected: 无类型错误(`tenantName` 随 `UserProfile` 的 OpenAPI schema 自动出现在 `personalApi.profile()` 的返回类型里,不需要手改 `types/api.ts`——那份文件只放手写的 `SysTenant`/`TenantInput` 这类管理端类型)。

- [ ] **Step 5: Commit**

```bash
git add backend/src/SmartAdmin.Services/Personal/PersonalModels.cs backend/src/SmartAdmin.Services/Personal/PersonalService.cs backend/tests/SmartAdmin.Tests/UserProfileFieldsTests.cs web/packages/admin/src/stores/user.ts web/packages/admin/src/composables/useModule.ts web/packages/admin/src/api/schema.d.ts
git commit -m "feat: 个人资料带出租户名,前端只读展示

同 OrgName/PositionName 的'走 Db 逃生舱'写法,不给 PersonalService 主构造器加参数(对继承它的消费者是破坏性变更)。前端只读展示,不做租户切换交互。"
```

---

## 收尾

全部 13 个任务完成后:

```bash
dotnet build backend/SmartAdmin.slnx -c Release
dotnet test  backend/SmartAdmin.slnx
cd web && npm run build && npm test && npm run lint && npm run format:check
```

以及至少一次 SqlServer 方言的定向子集(升级回填/CodeFirst 补列这条路径历史上只在 SqlServer 上炸过,见 CLAUDE.md 对 `CodeFirstNullableUpgradeTests` 的强调):

```bash
ci.bat -Stage backend -Dialect sqlserver
```

发版前(不在本计划范围,`/smart-release` 触发时处理):`CHANGELOG.md` 补条目、更新 `web/DESIGN.md`/`web/COMPONENTS.md`(如涉及)、`site/` 补一篇多租户指南、`skills/create-entity.md` 补一节"要不要接入 `TenantEntity`"。
