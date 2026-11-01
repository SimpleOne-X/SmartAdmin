# 站内通知推送到企业微信 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 经 `INoticeService.PublishAsync` 发布的通知，自动以文字卡片推给接收人里已绑定企业微信的用户。

**Architecture:** Core 定义 `IExternalNoticeSender`，`WeComExternalAuthProvider` 实现它并复用现有 token 缓存。
Services 新增 `NoticeDispatcher`（有界队列 + `BackgroundService`），解析接收人、转换内容、重试。
`NoticeService` 只在写库后入队。配置页加开关和测试按钮。

**Tech Stack:** .NET 10、SqlSugar、xUnit v3（MTP）、Vue 3 + Naive UI、Playwright。不新增依赖。

**Spec:** `docs/superpowers/specs/2026-10-31-notice-to-wecom-design.md`

## Global Constraints

- 只用 SqlSugarCore + Microsoft.\*；`SmartAdmin.Auth.WeCom` 只引用 `SmartAdmin.Core`。
- 内置服务用 `TryAdd*` 注册，方法 `virtual`，构造函数只加可选尾参。
- SqlServer 谓词写比较式：`u.Enabled == true`。
- 错误是数字 `ErrorCode`；厂商错误码只放在 `ExternalNoticeException.ProviderCode` 和测试结果里。
- `access_token`、`corpsecret` 不进日志、异常消息和接口响应；请求 URL 也不进（URL 里有 token）。
- 代码注释和文档只写现在做什么与为什么，不写历史、日期、issue 号或设计文档引用；CHANGELOG 除外。
- 测试失败不改断言、不加 skip、不放宽阈值，先定位原因。
- 后端测试命令：`dotnet test backend/SmartAdmin.slnx -- --filter-class "*X*"`。
- 提交信息用中文 conventional-commit，结尾 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 不提交 `docs/superpowers/specs/2026-10-28-license-design.md`，不碰 `git stash` 里的内容。
- 企业微信错误码（已核对[全局错误码](https://developer.work.weixin.qq.com/document/path/90313)）：
  `-1` 系统繁忙；`40014`、`42001` token 失效；`60020` IP 不可信；`81013` 接收人全部无效。

## Review Focus

- 正文只有一个链接或为空：卡片 `description` 不能为空，转换后为空时用标题。
- 链接含中文或超长：用 `new Uri(...).AbsoluteUri` 编码；编码后超过 2048 字节时改用 `/personal/notice`。
- 正文含大量 `<`、`&` 时截断：`description` 不超过 512 字符，也不能留下半个 `&lt;`。
- 已删除或停用的用户、已解绑的绑定：不推送。多角色的同一个人只推一次。
- 一个 provider 或一批发送失败：其他 provider 和后面的消息照常处理。

---

## 文件结构

| 文件 | 职责 |
|------|------|
| 新建 `backend/src/SmartAdmin.Core/Security/IExternalNoticeSender.cs` | 发送接口、消息、结果、异常 |
| 改 `backend/src/SmartAdmin.Core/Security/ExternalAuthProviderType.cs` | `SupportsNotice` 默认接口成员 |
| 改 `backend/src/SmartAdmin.Auth.WeCom/WeComExternalAuthProvider.cs` | `SendAsync` |
| 改 `backend/src/SmartAdmin.Auth.WeCom/WeComAuthProviderType.cs` | `SupportsNotice => true` |
| 新建 `backend/src/SmartAdmin.Services/Notice/NoticeText.cs` | Markdown 转纯文本、取站内链接、相对链接校验 |
| 新建 `backend/src/SmartAdmin.Services/Notice/INoticeDispatcher.cs` | 分发接口、入队项、测试结果 |
| 新建 `backend/src/SmartAdmin.Services/Notice/NoticeDispatcher.cs` | 队列、接收人解析、重试、测试消息 |
| 改 `NoticeService.cs`、`ServicesSetup.cs`、`ISysUserExternalService.cs`、`SysUserExternalService.cs` | 入队、注册、`notify` 开关 |
| 改 `ExternalAuthController.cs`、`ExternalAuthProviderController.cs`、`ExternalAuthModels.cs`、`ExternalAuthProviderModels.cs`、`ExternalAuthProviderService.cs`、`DefaultMenuSeed.cs` | 出参字段、测试接口、权限种子 |
| 改 `web/packages/admin/src/views/system/config/{draft.ts,components/SigninSection.vue}`、`utils/oauthBrand.ts`、`api/index.ts`、`locales/{zh-CN,en-US}.ts` | 开关和测试按钮 |
| 改 `layouts/AppHeader.vue`、`views/module/index.vue`、`views/personal/password.vue`、`web/e2e/wecom-login.spec.ts`、`web/e2e/api.ts` | 退出漏洞、直达 e2e |
| 改 `.github/workflows/ci.yml`、`scripts/ci-local.ps1` | SqlServer 子集加接收人测试 |
| 改 `CHANGELOG.md`、`site/zh/backend/external-login.md`、`site/backend/external-login.md` | 发布说明和使用文档 |

---

### Task 1: 发送契约和企业微信实现

**Files:**
- Create: `backend/src/SmartAdmin.Core/Security/IExternalNoticeSender.cs`
- Modify: `backend/src/SmartAdmin.Core/Security/ExternalAuthProviderType.cs`
- Modify: `backend/src/SmartAdmin.Auth.WeCom/WeComExternalAuthProvider.cs`
- Modify: `backend/src/SmartAdmin.Auth.WeCom/WeComAuthProviderType.cs`
- Test: `backend/tests/SmartAdmin.Tests/WeComNoticeSenderTests.cs`

**Interfaces:**
- Produces（命名空间 `SmartAdmin.Core`）：

```csharp
public interface IExternalNoticeSender
{
    Task<ExternalNoticeResult> SendAsync(
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default);
}
public sealed record ExternalNoticeMessage(long NoticeId, string Title, string Text, string Url);
public sealed record ExternalNoticeResult(IReadOnlyList<string> Rejected);
public sealed class ExternalNoticeException(
    string message, bool retryable, string? providerCode = null, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
    public string? ProviderCode { get; } = providerCode;
}
// IExternalAuthProviderType 新增：
bool SupportsNotice => false;
```

- [ ] **Step 1: 写失败测试**

沿用 `WeComDingTalkAuthProviderTests` 的假 handler 写法（`SeqHandler`，第一步回 gettoken，第二步回 message/send）。
测试和断言：

```csharp
Send_posts_a_textcard_with_duplicate_check
// 请求：POST https://qyapi.weixin.qq.com/cgi-bin/message/send?access_token=T
// JSON：touser=="u1|u2"、msgtype=="textcard"、agentid 是数字 1000002、
// textcard.title/description/url 与输入一致、textcard.btntxt=="详情"、
// enable_duplicate_check==1、duplicate_check_interval==600
Send_returns_invaliduser_and_unlicenseduser_as_rejected
// errcode=0, invaliduser="u1|u2", unlicenseduser="u3" → Rejected == [u1,u2,u3]
Send_truncates_title_to_128_and_escapes_description
// 标题 200 字 → 128 字；Text "a<b&c>" → description "a&lt;b&amp;c&gt;"
Send_truncated_description_never_splits_an_entity
// Text 600 个 '<' → Length <= 512，以 "…" 结尾，去掉省略号后能被 "&lt;" 整除
Send_refreshes_token_once_on_invalid_token(int errcode) // [40014] [42001]
// send 失败 → 再 gettoken → send 成功；共 2 次 gettoken、2 次 send
Send_gives_up_when_refreshed_token_is_still_invalid
// 连续两次 42001 → 抛 Retryable==false、ProviderCode=="42001"，不再请求（共 2 次 send）
Send_maps_errors_to_retryability(int errcode, bool retryable)
// [-1,true] [60020,false] [81013,false] [40001,false]；ProviderCode 等于 errcode 的字符串
Send_network_failure_and_5xx_are_retryable
// handler 抛 HttpRequestException、回 502 → Retryable==true
Send_60020_message_mentions_trusted_ip
// ex.Message 含 "企业可信 IP"
Send_never_leaks_token_or_secret
// 所有失败路径：ex.Message 和 ex.InnerException 链不含 token "T" 的完整值和 corpsecret；
// 用足够长的唯一字符串做 token 和 secret 来断言；用捕获型 ILogger 断言日志也不含
Send_rejects_non_numeric_agent_id
// AgentId="abc" → Retryable==false，且没有发出任何请求
WeCom_type_supports_notice
// new WeComAuthProviderType().SupportsNotice == true；
// ((IExternalAuthProviderType)假类型).SupportsNotice == false
Login_exchange_still_maps_errors_to_OAuthExchangeFailed
// gettoken errcode=40001 时 ExchangeAsync 仍抛 AdminException(OAuthExchangeFailed)
```

- [ ] **Step 2: 确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*WeComNoticeSenderTests*"`
Expected: 编译失败，`IExternalNoticeSender` 未定义。

- [ ] **Step 3: 实现**

在 `IExternalNoticeSender.cs` 写上面的类型。
`WeComExternalAuthProvider : IExternalAuthProvider, IExternalNoticeSender`，新增：

```csharp
public virtual Task<ExternalNoticeResult> SendAsync(...);
protected virtual void InvalidateAccessToken(); // 清 _cachedToken
```

- 把 `GetJsonAsync`、`EnsureOk` 抛的异常换成继承 `AdminException(ErrorCode.OAuthExchangeFailed)` 的私有类型，
  带 `int? ErrCode`、`int? HttpStatus`、`string? ErrMsg`。登录路径的 `catch (AdminException)` 行为不变。
- 判定只看 `errcode`，不看 `errmsg` 文字。
- 异常消息写 `企业微信 message/send errcode={code} {errmsg}`；`60020` 追加提示“把服务器出口 IP 加入应用的企业可信 IP”。
- 网络异常转成 `ExternalNoticeException` 时**不传 `inner`**，消息只写异常类型名，防止底层异常带出请求 URL。
- 调用方取消（`OperationCanceledException` 且 token 已取消）直接抛出，不包装。
- `unlicenseduser`、`invaliduser` 只在 JSON 值为字符串时读取。

`WeComAuthProviderType` 加 `public bool SupportsNotice => true;`。

- [ ] **Step 4: 确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*WeComNoticeSenderTests*" --filter-class "*WeComDingTalk*"`
Expected: 失败 0，已有的企业微信登录测试不受影响。

- [ ] **Step 5: 提交**

```bash
git add backend/src/SmartAdmin.Core/Security backend/src/SmartAdmin.Auth.WeCom backend/tests/SmartAdmin.Tests/WeComNoticeSenderTests.cs
git commit -m "feat(wecom): 登录 provider 支持发送应用文字卡片消息"
```

---

### Task 2: 通知正文转换

**Files:**
- Create: `backend/src/SmartAdmin.Services/Notice/NoticeText.cs`
- Modify: `backend/src/SmartAdmin.Services/Notice/NoticeService.cs:227-234`
- Test: `backend/tests/SmartAdmin.Tests/NoticeTextTests.cs`

**Interfaces:**
- Produces（`public static class NoticeText`，命名空间 `SmartAdmin.Services`）：

```csharp
public static bool IsSiteRelativeUrl(string url);
public static string ToPlainText(string? markdown);
public static string? FirstSiteLink(string? markdown);
```

- [ ] **Step 1: 写失败测试**

```csharp
IsSiteRelativeUrl_rules
// true："/a"、"/flow/todo?taskId=1"
// false：""、"a"、"//evil.com"、"/\\evil.com"、"/a b"、"/a\tb"、"https://x"
ToPlainText_strips_markdown
// "# 标题\n\n**粗** `码`\n\n\n[前往审批](/flow/todo?taskId=1)" → "标题\n粗 码\n前往审批"
// "user_name" 保留下划线；null、"   " → ""
FirstSiteLink_skips_external_links
// "[外](https://x) [内](/a) [二](/b)" → "/a"
// "[协议相对](//evil.com)" → null；"![图](/img.png) [内](/a)" → "/a"；null → null
```

- [ ] **Step 2: 确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*NoticeTextTests*"`
Expected: 编译失败，`NoticeText` 未定义。

- [ ] **Step 3: 实现**

把 `NoticeService.IsSiteRelativeUrl` 的方法体搬到 `NoticeText.IsSiteRelativeUrl`，原 `protected virtual` 方法保留并调用它。
用正则处理链接和图片，再去掉行首 `#`、`>`、`-`，以及 `*`、`` ` ``，合并连续空行。
不追求完整的 Markdown 解析，不处理下划线。

- [ ] **Step 4: 确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*NoticeTextTests*" --filter-class "*NoticeTests*"`
Expected: 失败 0。

- [ ] **Step 5: 提交**

```bash
git add backend/src/SmartAdmin.Services/Notice backend/tests/SmartAdmin.Tests/NoticeTextTests.cs
git commit -m "feat(notice): 增加通知正文转纯文本和取站内链接"
```

---

### Task 3: 后台分发

**Files:**
- Create: `backend/src/SmartAdmin.Services/Notice/INoticeDispatcher.cs`
- Create: `backend/src/SmartAdmin.Services/Notice/NoticeDispatcher.cs`
- Modify: `backend/src/SmartAdmin.Services/Notice/NoticeService.cs`（构造函数尾参、`PublishAsync`）
- Modify: `backend/src/SmartAdmin.Services/ServicesSetup.cs:168`
- Modify: `backend/src/SmartAdmin.Services/ExternalAuth/ISysUserExternalService.cs`
- Modify: `backend/src/SmartAdmin.Services/ExternalAuth/SysUserExternalService.cs`
- Modify: `backend/tests/SmartAdmin.Tests/ReplaceabilityContract.cs`
- Modify: `.github/workflows/ci.yml:178`、`scripts/ci-local.ps1:104`
- Test: `backend/tests/SmartAdmin.Tests/NoticeDispatcherTests.cs`、`backend/tests/SmartAdmin.Tests/NoticeRecipientTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IExternalNoticeSender` 等；Task 2 的 `NoticeText`。
- Produces（命名空间 `SmartAdmin.Services`）：

```csharp
public interface INoticeDispatcher
{
    /// <summary>入队;不阻塞、不抛异常。</summary>
    void Enqueue(NoticeDispatchItem item);
    Task<ExternalNoticeTestView> SendTestAsync(string providerCode, long userId, CancellationToken cancellationToken = default);
}
public sealed record NoticeDispatchItem(
    long NoticeId, string Title, string? Content, ReceiverType ReceiverType, IReadOnlyList<long> TargetIds);
/// <param name="Status">ok / rejected / failed / notBound / noBaseUrl / unsupported</param>
public sealed record ExternalNoticeTestView(string Status, string? ProviderCode = null, string? Detail = null);

public class NoticeDispatcher(IServiceScopeFactory scopes, AdminExternalAuthOptions options, ILogger<NoticeDispatcher> logger)
    : BackgroundService, INoticeDispatcher
{
    protected virtual Task<List<string>> ResolveSubjectsAsync(
        IServiceProvider services, NoticeDispatchItem item, string providerCode, CancellationToken cancellationToken);
    protected virtual ExternalNoticeMessage? BuildMessage(NoticeDispatchItem item);
    protected virtual TimeSpan GetRetryDelay(int failedAttempts); // 1→5 秒，2→30 秒
}

// ISysUserExternalService 新增（默认实现让已有的消费者实现不用改）：
Task<bool> IsNotifyEnabledAsync(string provider) => Task.FromResult(true);
```

- [ ] **Step 1: 写失败测试**

替身：`FakeSenderProvider : IExternalAuthProvider, IExternalNoticeSender`，记录每次调用，可配置抛出的异常序列。
经 `AdminAppFactory.Overrides` 用 `AddSingleton<IExternalAuthProvider>` 注册；
`Settings` 配 `SmartAdmin:ExternalAuth:CallbackBaseUrl = https://admin.example.com/app/`。
**断言“没发”用哨兵：** 再注册一个总开着的 provider，它排在后面。队列按序处理，等哨兵收到后再断言前者没收到，不靠 `Delay`。

`NoticeRecipientTests`（真实数据库，加入 SqlServer 子集）：

```csharp
All_reaches_enabled_bound_users_only
// 3 个用户：绑定启用、绑定停用、未绑定 → 只收到第一个的 Subject
Role_reaches_role_members_once
// 用户同时属于被选中的两个角色 → Subject 只出现一次；不属于该角色的绑定用户收不到
User_reaches_only_targets
Deleted_user_and_unbound_binding_are_skipped
// 删除用户、解绑后 → 收不到
Only_bindings_of_this_provider_count
// 用户只绑定了 github → wecom 发送方收不到
```

`NoticeDispatcherTests`：

```csharp
Publish_builds_message_with_absolute_link
// Content "请处理\n[前往审批](/flow/todo?taskId=1)"
// Url=="https://admin.example.com/app/flow/todo?taskId=1"；Text=="请处理\n前往审批"
Publish_without_link_lands_on_notice_page
// Url=="https://admin.example.com/app/personal/notice"
Publish_with_link_only_or_empty_content_uses_title_as_text
Publish_encodes_non_ascii_link_and_falls_back_when_too_long
// "/a?name=张三" → Url 含 "%E5%BC%A0"；编码后超过 2048 字节的链接 → /personal/notice
Publish_without_base_url_skips_send
// 不配 CallbackBaseUrl → 发送方未被调用，Warning 日志一条
Enabled_or_notify_off_skips_that_provider(string key) // ["enabled"] ["notify"]
// sys.externalauth.{code}.{key}=false → 该 provider 未被调用，哨兵收到
More_than_1000_subjects_are_batched
// 直接覆写 ResolveSubjectsAsync 返回 2500 个 → 批次大小 [1000,1000,500]
Retryable_failure_retries_up_to_three_attempts
// 用覆写 GetRetryDelay 返回 0 的子类；一直抛 Retryable=true → 正好 3 次
Non_retryable_failure_sends_once
Provider_failure_does_not_block_others_or_later_notices
// 第一个 provider 抛异常 → 哨兵仍收到，下一条通知也照常处理
Publish_succeeds_when_sender_throws
// 发送方总抛异常 → PublishAsync 返回 Id，站内信可查
Full_queue_drops_without_throwing
// 未启动的 dispatcher，Enqueue 10001 次不抛异常，Error 日志至少一条
No_sender_means_nothing_is_queued
// 只注册不支持发送的 provider，Enqueue 后不解析用户（用计数替身断言 ResolveSubjectsAsync 未被调用）
SendTest_statuses
// 无此 provider 或不支持 → unsupported；没绑定 → notBound；没配基址 → noBaseUrl；
// 成功 → ok；Rejected 非空 → rejected；抛 ExternalNoticeException → failed + ProviderCode + Detail；
// 只调用 1 次 SendAsync（不重试）；Text 含发送时间
```

`ReplaceabilityContract.cs` 加 `(typeof(INoticeDispatcher), ServiceLifetime.Singleton)`。

- [ ] **Step 2: 确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*NoticeDispatcherTests*" --filter-class "*NoticeRecipientTests*"`
Expected: 编译失败，`INoticeDispatcher` 未定义。

- [ ] **Step 3: 实现**

- **队列：** `Channel.CreateBounded<NoticeDispatchItem>(new BoundedChannelOptions(10000) { SingleReader = true })`。
  `Enqueue` 用 `TryWrite`，失败记 Error 并带 `NoticeId`。
- **零开销：** 首次 `Enqueue` 时开一个 scope 判断一次并缓存：
  有 `IExternalAuthProviderType.SupportsNotice`，或 `IEnumerable<IExternalAuthProvider>` 里有实现 `IExternalNoticeSender` 的。
  不要在构造函数注入这两个集合，消费者可能把 provider 注册成 Scoped，单例捕获会在启动时报错。
- **处理一条：** 开 scope → `IExternalAuthProviderRegistry.ListAsync()` → 筛 `is IExternalNoticeSender` →
  `IsEnabledAsync` 和 `IsNotifyEnabledAsync` 都为 true → 解析接收人 → `Chunk(1000)` → 带重试发送。
  每个 provider 和整条消息都单独 `try/catch` 记 Error，循环不能退出。
- **接收人：** 从 `IRepository<SysUserExternal>.AsQueryable()` 开始，`InnerJoin<SysUser>`，`Role` 再 `InnerJoin<SysUserRole>`，
  条件 `e.Provider == code && u.Enabled == true`，`.Select(e.Subject).Distinct()`。三张表都继承 `BaseEntity`，不用 `ClearFilter`。
- **内容：** `Title`；`Text` 是 `NoticeText.ToPlainText(Content)`，为空时用 `Title`；
  链接是 `FirstSiteLink(Content) ?? "/personal/notice"`。
  `new Uri(CallbackBaseUrl.TrimEnd('/') + path).AbsoluteUri`，UTF-8 超过 2048 字节时改用 `/personal/notice`。
  基址为空时 `BuildMessage` 返回 null，记 Warning，跳过发送。
- **重试：** 共 3 次。只有 `ExternalNoticeException { Retryable: true }` 才重试，其他异常不重试。
  最终失败记 Error（provider 码、`ProviderCode`、`Message`）。`Rejected` 非空记 Warning，提示检查应用可见范围和接口许可。
- **`SendTestAsync`：** 不入队、不重试；标题“测试消息”，正文 `这是一条测试消息,发送时间 {本地时间:yyyy-MM-dd HH:mm:ss}。`；
  链接 `/personal/notice`；接收人是 `ISysUserExternalService.ListByUserAsync(userId)` 里该 provider 的 `Subject`。
- **`PublishAsync`：** 写完接收行之后、实时广播之前调用
  `dispatcher?.Enqueue(new NoticeDispatchItem(entity.Id, input.Title, input.Content, input.ReceiverType, targetIds))`；
  构造函数尾部加 `INoticeDispatcher? dispatcher = null`。XML 注释写明：在事务内调用时，回滚后消息可能已经发出。
- **注册（紧跟 `INoticeService`）：**

```csharp
services.TryAddSingleton<NoticeDispatcher>();
services.TryAddSingleton<INoticeDispatcher>(sp => sp.GetRequiredService<NoticeDispatcher>());
services.AddHostedService(sp => sp.GetRequiredService<NoticeDispatcher>());
```

- **`IsNotifyEnabledAsync`：** 键 `sys.externalauth.{code}.notify`，缺省 true，写法同 `IsEnabledAsync`。

在 `ci.yml:178` 的过滤串和 `ci-local.ps1:104` 的数组里都加 `*NoticeRecipientTests*`。

- [ ] **Step 4: 确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*NoticeDispatcherTests*" --filter-class "*NoticeRecipientTests*" --filter-class "*NoticeTests*" --filter-class "*ReplaceabilityTests*"`
Expected: 失败 0。

- [ ] **Step 5: 提交**

```bash
git add backend/src/SmartAdmin.Services backend/tests/SmartAdmin.Tests .github/workflows/ci.yml scripts/ci-local.ps1
git commit -m "feat(notice): 发布通知后在后台推送给已绑定的外部账号"
```

---

### Task 4: 管理接口

**Files:**
- Modify: `backend/src/SmartAdmin.Services/ExternalAuth/ExternalAuthProviderModels.cs:19-24`
- Modify: `backend/src/SmartAdmin.Services/ExternalAuth/ExternalAuthProviderService.cs:257-259`
- Modify: `backend/src/SmartAdmin.AspNetCore/ExternalAuth/ExternalAuthModels.cs:44-54`
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/ExternalAuthController.cs:66-73`
- Modify: `backend/src/SmartAdmin.AspNetCore/Controllers/ExternalAuthProviderController.cs`
- Modify: `backend/src/SmartAdmin.Services/Seed/DefaultMenuSeed.cs:125`
- Modify: `web/packages/admin/src/api/schema.d.ts`（重新生成）
- Test: `backend/tests/SmartAdmin.Tests/ExternalAuthProviderEndpointTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `INoticeDispatcher.SendTestAsync`、`ExternalNoticeTestView`、`IsNotifyEnabledAsync`。
- Produces:
  - `ExternalAuthTypeView` 加尾参 `bool SupportsNotice = false`。
  - `ExternalProviderAdminItem` 加 `public bool Notify { get; init; }`。
  - `POST /api/v1/sys/external-auth/providers/{code}/test-notice` 返回 `Result<ExternalNoticeTestView>`。

- [ ] **Step 1: 写失败测试**

```csharp
Catalog_reports_supports_notice
// 目录里 acme 类型 supportsNotice==false；声明了 SupportsNotice 的假类型 == true
ProvidersAll_reports_notify_default_true_and_saved_value
// 缺省 notify==true；sys.externalauth.{code}.notify=false 后 == false
TestNotice_requires_permission
// 无该权限的用户 → 权限错误码；seed 的按钮 312 含 "POST:/api/v1/sys/external-auth/providers/{code}/test-notice"
TestNotice_returns_status_in_body_not_error
// 超管没绑定 → 信封 code==0，data.status=="notBound"
```

- [ ] **Step 2: 确认失败**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ExternalAuthProviderEndpointTests*"`
Expected: 上面 4 个新用例失败。

- [ ] **Step 3: 实现**

动作写法照现有 `Test`，不声明 `CancellationToken` 参数，也不改控制器构造函数：

```csharp
[HttpPost("{code}/test-notice")]
[RolePermission]
[OperationLog("发送登录方式测试消息")]
public async Task<Result<ExternalNoticeTestView>> TestNotice(string code)
{
    var userId = HttpContext.RequestServices.GetRequiredService<ICurrentUser>().UserId
        ?? throw new AdminException(ErrorCode.TokenInvalid);
    return Result<ExternalNoticeTestView>.Ok(await HttpContext.RequestServices
        .GetRequiredService<INoticeDispatcher>().SendTestAsync(code, userId, HttpContext.RequestAborted));
}
```

把这条路由加进 `DefaultMenuSeed.cs:125`（Id 312）的 `Codes(...)`。
这个种子 `SyncOnUpgrade` 为 true，存量库升级时跟着更新，不用另写迁移。

- [ ] **Step 4: 确认通过**

Run: `dotnet test backend/SmartAdmin.slnx -- --filter-class "*ExternalAuthProviderEndpointTests*" --filter-class "*SeedUpgradeTests*"`
Expected: 失败 0。

- [ ] **Step 5: 重新生成前端契约**

后台运行 `dotnet run --project backend/samples/MinimalHost`，在 `web/` 运行 `npm run gen:api`，然后停掉这个进程。
Expected: `schema.d.ts` 出现 `supportsNotice`、`notify`、`test-notice`。不要手改这个文件。

- [ ] **Step 6: 提交**

```bash
git add backend/src backend/tests/SmartAdmin.Tests web/packages/admin/src/api/schema.d.ts
git commit -m "feat(external-auth): 增加推送开关字段和发送测试消息接口"
```

---

### Task 5: 配置页开关和测试按钮

**Files:**
- Modify: `web/packages/admin/src/utils/oauthBrand.ts:60-115`
- Modify: `web/packages/admin/src/views/system/config/draft.ts`
- Modify: `web/packages/admin/src/views/system/config/components/SigninSection.vue`
- Modify: `web/packages/admin/src/api/index.ts`
- Modify: `web/packages/admin/src/locales/zh-CN.ts`、`en-US.ts`
- Test: `web/packages/admin/src/utils/oauthBrand.spec.ts`、配置页已有的 `draft` 测试

**Interfaces:**
- Consumes: Task 4 的字段和接口。
- Produces: `ConfigProviderRow` 加 `notify: boolean`、`supportsNotice: boolean`；
  `notifyKey(code)` 返回 `sys.externalauth.${code}.notify`；
  `externalAuthApi.testNotice(code)` 返回 `ExternalNoticeTestView`。

- [ ] **Step 1: 写失败测试**

```ts
it('buildConfigProviderRows 带出推送开关')
// 类型 supportsNotice:true、admin 项 notify:false → row.supportsNotice===true、row.notify===false
// admin 没有 notify 字段 → row.notify===true；未注册 → row.notify===false
it('草稿只为支持推送的已配置方式载入 notify 键，并计入 claimedKeys')
// wecom 有 sys.externalauth.wecom.notify；github 没有
it('方式被清除后 refreshProviders 移除 notify 键')
```

- [ ] **Step 2: 确认失败**

Run（在 `web/packages/admin`）：`npx vitest run oauthBrand draft`。
Expected: 新用例失败。

- [ ] **Step 3: 实现**

- `SigninSection.vue`：在 `hasLinkSwitch` 那一行之后，`v-if="p.supportsNotice"` 加一个 `sub` 的 `CfgRow`：
  标签 `config.externalAuth.notify`，`keys` 为 `[notifyKey(p.code)]`，`disabled` 条件同上一行，
  内容是 `n-button`（`data-testid="provider-test-notice"`）加 `n-switch`。
  按钮 `loading` 时禁用，调用 `testNotice`，用 `useMessage` 显示结果：`ok` 用 success，其余用 error。
- `draft.ts`：`providerSwitchValues`、`claimedKeys`、`refreshProviders` 的删除循环都带上 `notifyKey`。
- 文案（`zh-CN` 如下，`en-US` 对应翻译，`parity.spec.ts` 会检查）：

```ts
notify: '同时推送消息',
notifyHint: '系统消息和审批消息同时发到企业微信,没绑定的人只收站内信',
testNotice: '发送测试消息',
noticeTest: {
  ok: '已发送,请在企业微信里查看',
  rejected: '企业微信拒收了这个账号,请检查应用可见范围和接口许可',
  failed: '发送失败:{detail}',
  notBound: '当前账号没有绑定企业微信,请先到个人中心绑定',
  noBaseUrl: '没有配置 SmartAdmin:ExternalAuth:CallbackBaseUrl,无法生成卡片链接',
  unsupported: '这种登录方式不支持推送消息',
},
```

- [ ] **Step 4: 确认通过**

Run（在 `web/`）：`npm run typecheck`，`npm run lint`；在 `web/packages/admin`：`npx vitest run oauthBrand draft parity`。
Expected: 全部通过。全量 vitest 在这台 Windows 机器上会随机 `EBUSY`/超时，按目标文件跑，全量以 CI 为准。

- [ ] **Step 5: 提交**

```bash
git add web/packages/admin/src
git commit -m "feat(config): 登录方式页增加推送开关和发送测试消息"
```

---

### Task 6: 点卡片直达和退出漏洞

**Files:**
- Modify: `web/packages/admin/src/utils/oauthBrand.ts`
- Modify: `web/packages/admin/src/layouts/AppHeader.vue:187-199`
- Modify: `web/packages/admin/src/views/module/index.vue:46`
- Modify: `web/packages/admin/src/views/personal/password.vue:76`
- Modify: `web/e2e/wecom-login.spec.ts`、`web/e2e/api.ts`
- Test: `web/packages/admin/src/utils/oauthBrand.spec.ts`

**Interfaces:**
- Produces: `export function suppressWeComAutoLogin(storage?: Pick<Storage, 'setItem'>): void`，
  默认用 `sessionStorage`，存储不可用时不抛异常。
  e2e 的 `apiAdminSession(request)` 返回登录接口的完整 `data`。

已有的 `oauthBrand.spec.ts` 用例和 `wecom-login.spec.ts` 第一个用例不改。

- [ ] **Step 1: 写失败测试**

```ts
// oauthBrand.spec.ts
it('suppressWeComAutoLogin 之后 claimWeComAutoLogin 返回 false')
it('suppressWeComAutoLogin 在存储不可用时不抛异常')
```

`wecom-login.spec.ts` 新增（复用现有 `WECOM_UA` 和假应用配置）：

```ts
test('未登录打开深链 → 静默登录后回到原页面')
// page.route authorize → 302 到 /oauth/callback?ticket=e2e
// page.route POST /api/v1/auth/external/exchange → { code: 0, data: 真实登录会话 }
// page.goto('/personal/notice') → 最终 URL 路径是 /personal/notice，authorizeHits===1
test('本地有登录态、没进过登录页时主动退出 → 不再自动登录')
// addInitScript 写入 localStorage 'user' 的真实会话（用 sessionStorage 标记只写一次）
// goto('/') → 点退出 → 看到账号输入框，等 networkidle，authorizeHits===0
test('登录态失效回到登录页 → 仍会自动登录')
// addInitScript 写入一个无效 accessToken、refreshToken 为空的会话 → goto('/personal/notice') → authorizeHits===1
```

退出操作照 `web/e2e/rbac-permission.spec.ts:26` 的 `logout` 里点击头像菜单的写法；
如果它只是清存储，就按 `AppHeader` 的菜单项定位。

- [ ] **Step 2: 确认失败**

Run：在 `web/packages/admin` 运行 `npx vitest run oauthBrand`，在 `web/` 运行
`npx playwright test wecom-login --output "<scratchpad>/pw-wecom"`（`--output` 避免和其他会话互清结果）。
Expected: `suppressWeComAutoLogin` 未定义；退出用例 `authorizeHits===1`，**证实漏洞确实存在**。
如果退出用例在未修复时就通过，停下来报告：漏洞推断不成立，先查清原因再决定是否保留这处改动。

- [ ] **Step 3: 实现**

在 `oauthBrand.ts` 加 `suppressWeComAutoLogin`，写入和 `claimWeComAutoLogin` 相同的键 `smart.sso.wecomAutoLogin`。
在 `AppHeader.vue`、`views/module/index.vue`、`views/personal/password.vue` 三处 `authApi.logout()` 之前调用。
不要写进 `user.clear()`：token 失效时也走它，点卡片直达就失效了。

- [ ] **Step 4: 确认通过**

Run：在 `web/packages/admin` 运行 `npx vitest run oauthBrand`，在 `web/` 运行
`npx playwright test wecom-login --output "<scratchpad>/pw-wecom"`。
Expected: 全部通过。

- [ ] **Step 5: 提交**

```bash
git add web/packages/admin/src web/e2e
git commit -m "fix(login): 主动退出后企业微信内不再自动登录,补深链直达回归"
```

---

### Task 7: 文档和完整闸门

**Files:**
- Modify: `CHANGELOG.md`（`## Unreleased`）
- Modify: `site/zh/backend/external-login.md`（`## 在企业微信客户端里登录` 之后）
- Modify: `site/backend/external-login.md`（英文译文）
- Modify: `site/zh/backend/external-login.md` 和英文版的「运营项」一节

- [ ] **Step 1: 读写作规范**

先读 `skills/write-docs.md`。`site/` 只在全角标点后断行，英文是中文的译文。

- [ ] **Step 2: 写文档站**

新增 `## 推送消息到企业微信`，内容来自 spec「配置」一节的 5 步，并说明：

- 发给谁、没绑定怎么办、失败只记日志并有限重试；进程重启会丢没发出的推送，站内信不丢。
- 频率限制：同一成员每应用 30 次/分钟，超出直接丢弃，不报错。
- 企业可信 IP：`60020` 的含义和处理，附官方文档链接。
- 卡片链接用 `CallbackBaseUrl` 拼接，前后端不同源的部署要让这个地址能打开前端。
- 自定义渠道：实现 `IExternalNoticeSender`。完全替换 `INoticeService` 的应用要自己调 `INoticeDispatcher`。
- 在事务内调用 `PublishAsync` 的限制。

「运营项」加 `notify`，默认 `true`。

- [ ] **Step 3: 写 CHANGELOG**

在 `## Unreleased` 下加 `### 升级说明` 和 `### Added`、`### Fixed`：

- **升级说明：** 已配好企业微信登录的应用，升级后马上开始推送，包括发给全体的公告。
  不想要就先把 `sys.externalauth.wecom.notify` 设成 `false`。
- **Added：** 通知推送、`IExternalNoticeSender`、`INoticeDispatcher`、推送开关、发送测试消息。
- **Fixed：** 本地有登录态时在企业微信里主动退出，会被立刻自动登录回去。

- [ ] **Step 4: 跑文档闸门**

Run: `cd site && npm run lint:prose -- zh/backend/external-login.md backend/external-login.md && npm run check:llms`
Expected: 通过。

- [ ] **Step 5: 跑完整本地闸门**

在 PowerShell 运行 `.\ci.bat`，再运行 `.\ci.bat -Stage web-e2e`。
Expected: backend、docs、template-smoke、deps-audit 为 PASS，e2e 通过。
web 闸门如果因 `EBUSY`/`EPERM`/超时变红，按已知的 Windows 现象分批重跑取证，并如实报告；
其他失败要定位原因。Task 3 的接收人查询要靠推送后的 CI 验证方言腿（MySQL、PostgreSQL、SqlServer）。

- [ ] **Step 6: 提交**

```bash
git add CHANGELOG.md site/zh/backend/external-login.md site/backend/external-login.md
git commit -m "docs(external-auth): 说明通知推送到企业微信的配置和限制"
```

---

## 执行前提

- 等 10.32.0 的 `release` 跑完并核对后再开始提交。设计文档和这份计划作为第一个提交：

```bash
git add docs/superpowers/specs/2026-10-31-notice-to-wecom-design.md docs/superpowers/plans/2026-10-31-notice-to-wecom.md
git commit -m "docs(notice): 增加通知推送到企业微信的设计和实现计划"
```

- 真实企业微信发送不在自动化范围内：卡片样式和手机端直达，由有企业微信账号的人用「发送测试消息」验证。
- 这份计划不包含发版。
