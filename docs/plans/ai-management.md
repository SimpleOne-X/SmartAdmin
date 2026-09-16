# AI 管理模块开发计划

> **状态**:已完成 · **拟定**:2026-09-13 · **完成**:2026-09-14 · **设计图**:[`docs/design-mockups/ai-management.html`](../design-mockups/ai-management.html)(在线版:https://claude.ai/code/artifact/76b596b4-cc15-4db3-8277-411fb5c52219)
>
> 本文是执行文档:按第 12 节的批次逐批实施、逐项勾选。「为什么这么定」已沉淀成 [ADR 0009](../adr/0009-ai-gateway-in-kernel.md)。
>
> **与本文/设计图的偏离**(实施时发现,记录在此供复核):
> - §7.1 服务层规则原文未明说 `SysAiModel.IsDefault` 的唯一范围,实施时定为**全库唯一**而非同厂商内唯一——网关在调用方未指定厂商/模型时按厂商遍历顺序取第一个默认模型,顺序不保证稳定,只有全局唯一才能让"全局默认模型"这个语义成立;已写进 [ADR 0009](../adr/0009-ai-gateway-in-kernel.md) 决策二与实体注释。
> - §6 厂商预置表里的模型名是起草时的占位示例;批次 1 实施时按"逐家核对官方文档"的要求重新核对,`AiProviderPresets.cs` 落的是 2026-09-13 当天的真实模型目录,新增厂商或大版本更新时需要重新核对。
> - §9 前端类型契约表(`AiModelPreset` 等)起草时漏了 `displayName` 字段,批次 5 前端联调时对照真实 OpenAPI schema 发现并补上,`types/api.ts` 现在和后端记录字段一一对应。

## 0. 一句话

SmartAdmin 内核提供**大模型接入网关**:运维在后台「AI 管理」目录下配好各厂商的 Key,业务项目(如 SmartAdmin-Pro 的 AI 审批)通过内核统一入口 `IAiChatClient` 调模型,内核把每次调用的 Token 记下来,后台能看用量。

产品线边界:**网关在开源内核,审批流与 AI 审批在 Pro(付费)**。内核是插座,Pro 是电器。

## 1. 目标与范围

| | 一期(本计划) | 二期(不在本计划内,表结构不挡路) |
|---|---|---|
| 能力 | 文本对话(非流式 + 流式) | Embedding、图片、函数调用、提示词模板页 |
| 厂商 | 10 个预置 + 自定义(OpenAI 兼容) | 需要 SDK 的厂商以可选包形式接 `IAiProtocolAdapter` |
| Key | 每厂商一个 | 多 Key 轮换 |
| 用量 | 到厂商 / 模型 / 用户 / 场景,按日趋势 | 费用估算界面、配额与告警 |
| 页面 | 「AI 模型」「用量统计」两页 | 提示词模板、对话记录 |

不做的事:内核不提供面向浏览器的对话 SSE 端点(业务方自己的控制器按需暴露,文档给示例);不引任何厂商 SDK;不做多租户级别的厂商隔离(厂商配置是系统级)。

## 2. 已定决策(不再重开)

1. 顶层目录「AI 管理」(Id 700),下设「AI 模型」(710)与「用量统计」(720);用量独立成页以便单独授权。
2. **做进内核本体**,不建独立 NuGet 包。控制器挂 `[Module("Ai")]`,消费者可用 `SmartAdmin:Api:DisabledModules` 关掉路由。理由:HttpClient 直连没有第三方依赖,符合「内核只依赖 SqlSugarCore + Microsoft.*」;仓库可选包的先例(Excel / Redis / Auth.*)都是「契约在内核、重依赖在包」,功能本身从不在包里;独立包要单独走种子、权限一致性测试、发版计数,收益只是让不用 AI 的消费者少三张空表。
3. 运维只填 Key:厂商预设决定协议、Base URL、默认模型清单;Base URL 可改(代理 / 自建网关)。
4. 按**协议**写适配器,不按厂商:一期 OpenAI 兼容 + Anthropic 两套。Gemini 走其 OpenAI 兼容端点;Azure OpenAI 走 `/openai/v1` 端点 + `api-key` 头,预设里带鉴权方式。
5. Key 用现有 `ISecretProtector`(AES-GCM,TOTP 种子同款)加密落库;接口只回脱敏尾四位 + `hasApiKey`;编辑留空 = 不改。
6. 写 Key 的端点挂 `[RequireReauth]`,与系统配置写端点一致。
7. 代理只在 appsettings 配置(`SmartAdmin:Ai:Http:Proxy`),界面不开放。
8. 生产环境未配 `SmartAdmin:Security:DataProtection:Key` 时,**保存 Key 报错提示**,不改零配置启动。
9. 调用必须带 `Scene` 标签,用量页按它拆账;单价字段留表不做界面。
10. 前端页面进 `smart-admin-web` 内置页,路径沿用 `/system/*` 惯例。

## 3. 总体架构

```
业务代码(Pro 的 AI 审批 / 任何消费者)
   │  注入 IAiChatClient,ChatAsync / StreamAsync(AiChatRequest{ Scene, Messages, ... })
   ▼
AiChatClient(Services)
   ├─ 解析厂商与模型:显式 ProviderCode/Model → 否则全局默认模型 → 都没有抛 49011
   ├─ 取启用厂商(缓存 CacheKeys.AiProviders,变更即失效)+ Unprotect Key(只在内存)
   ├─ 选 IAiProtocolAdapter(按 Protocol,TryAddEnumerable 多实现)
   ├─ 计时、调用、把上游错误映射成 ErrorCode 49020–49024
   └─ 成败都写 sys_ai_usage_log(IAiUsageService.RecordAsync;流式在结束时补写)
         ▼
   OpenAiCompatibleAdapter / AnthropicAdapter
         │  命名 HttpClient "SmartAdmin.Ai":超时 + SSRF 围栏(HttpFence,从 JobHttpFence 抽出)
         ▼
   厂商 REST(chat/completions · messages)

后台页面 ──► AiProviderController(/api/v1/sys/ai/provider)──► IAiProviderService
           ──► AiUsageController(/api/v1/sys/ai/usage)──────► IAiUsageService
定时任务 AiUsageLogCleanupJob(IAdminJob,种子 Id 2)按 sys.ai.usageRetentionDays 清理
```

分层落点(依赖只向下):

| 层 | 新增 |
|---|---|
| `SmartAdmin.Core` | `Ai/` 契约与 record、`Options/AdminAiOptions.cs`、`ErrorCode` 49xxx、`CacheKeys.AiProviders()`、`IDataProtectionKeyProvider.IsEphemeral` |
| `SmartAdmin.Services` | `Entities/SysAiProvider.cs` `SysAiModel.cs` `SysAiUsageLog.cs`、`Ai/`(服务、网关、适配器、预置表、围栏)、`Jobs/AiUsageLogCleanupJob.cs`、种子追加 |
| `SmartAdmin.AspNetCore` | `Controllers/AiProviderController.cs`、`Controllers/AiUsageController.cs` |
| `web/packages/admin` | `views/system/ai-model/`、`views/system/ai-usage/`、`types/api.ts`、`api/index.ts`、语言包、图标子集 |

## 4. 数据模型

### 4.1 `sys_ai_provider`(厂商)— `BaseEntity`(软删,进回收站)

| 列 | 类型 | 说明 |
|---|---|---|
| `Code` | string(64),唯一索引 `idx_sys_ai_provider_code` | 厂商编码,业务方 `ProviderCode` 用它;新增时默认取预设 code,可改,保存后不可改 |
| `Name` | string(64) | 显示名 |
| `Preset` | string(32) | 预设 code(见第 6 节);保存后不可改 |
| `Protocol` | string(32) | `openai` / `anthropic`;由预设带出 |
| `BaseUrl` | string(512) | 可改;保存与调用前都过围栏 |
| `AuthScheme` | string(16) | `bearer` / `x-api-key` / `api-key` / `none`;由预设带出 |
| `ApiKeyProtected` | string?(2048) | `ISecretProtector` 信封;`null` = 未配置 |
| `ApiKeyHint` | string?(16) | 脱敏尾四位,只用于展示 |
| `Enabled` | bool | 未配置 Key 且 `AuthScheme != none` 时不能为 true(49004) |
| `Sort` | int | 卡片顺序 |
| `Remark` | string?(512) | |

### 4.2 `sys_ai_model`(模型)— `BaseEntity`

| 列 | 类型 | 说明 |
|---|---|---|
| `ProviderId` | long,索引 `idx_sys_ai_model_provider` | 所属厂商;厂商删除时级联软删 |
| `Name` | string(128) | 调用用的模型名(`deepseek-chat`);同厂商内唯一(服务层校验,含软删行) |
| `DisplayName` | string(128) | |
| `Enabled` | bool | |
| `IsDefault` | bool | **全库只允许一个** true;设默认时服务层先清掉其它行 |
| `ContextWindow` | int? | 预设带出,展示用 |
| `InputPrice` / `OutputPrice` | decimal?(4 位小数) | 每百万 Token 单价,一期只建列不做界面 |

### 4.3 `sys_ai_usage_log`(调用记录)— `AuditEntity`(只增,物理删,对齐 `SysJobLog`)

| 列 | 类型 | 说明 |
|---|---|---|
| `ProviderId` / `ProviderCode` | long / string(64) | 冗余 code,厂商删了也能读 |
| `Model` | string(128) | |
| `Scene` | string(64) | 业务场景标签,如 `approval.summary`;测试连接固定 `system.test` |
| `UserId` | long? | 来自 `ICurrentUser`;后台任务里可能为空 |
| `InputTokens` / `OutputTokens` / `TotalTokens` | int | 上游未回 usage 时三者为 0 |
| `UsageSource` | int | 1 = 上游回报,2 = 上游未回(流式且厂商不支持 `include_usage`) |
| `LatencyMs` | int | |
| `Success` | bool | |
| `ErrorCode` | int? | 失败时的内核错误码 |
| `ErrorMessage` | string?(512) | 上游错误摘要,截断,**不含请求内容** |
| `Streamed` | bool | |
| `RequestId` | string?(64) | 上游返回的请求 id,排障用 |

索引:`(CreateTime desc)`、`(Scene, CreateTime)`、`(ProviderId, CreateTime)`、`(UserId, CreateTime)`。**不记 prompt 与回复正文**,审批内容属于业务数据,不进内核日志。

## 5. Core 契约

```csharp
// SmartAdmin.Core/Ai/IAiChatClient.cs
public interface IAiChatClient
{
    Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken cancellationToken = default);
}

public sealed record AiChatRequest
{
    public required string Scene { get; init; }            // 必填,空串抛 49031
    public required IReadOnlyList<AiChatMessage> Messages { get; init; }
    public string? ProviderCode { get; init; }              // 不填 → 全局默认模型所属厂商
    public string? Model { get; init; }                     // 不填 → 全局默认模型
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; } // 透传给日志扩展,一期不落库
}

public sealed record AiChatMessage(AiChatRole Role, string Content)
{
    public static AiChatMessage System(string content) => new(AiChatRole.System, content);
    public static AiChatMessage User(string content) => new(AiChatRole.User, content);
    public static AiChatMessage Assistant(string content) => new(AiChatRole.Assistant, content);
}
public enum AiChatRole { System = 1, User = 2, Assistant = 3 }

public sealed record AiChatResponse(string Content, string ProviderCode, string Model, AiUsage Usage, string? FinishReason, int LatencyMs, string? RequestId);
public sealed record AiChatChunk(string Delta, AiUsage? Usage = null, string? FinishReason = null); // Usage 只在最后一块出现
public sealed record AiUsage(int InputTokens, int OutputTokens, AiUsageSource Source) { public int TotalTokens => InputTokens + OutputTokens; }
public enum AiUsageSource { Reported = 1, Missing = 2 }

// 协议适配器:多实现扩展点(TryAddEnumerable),消费者可加自己的协议
public interface IAiProtocolAdapter
{
    string Protocol { get; }   // "openai" / "anthropic"
    Task<AiChatResponse> ChatAsync(AiEndpoint endpoint, AiChatRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<AiChatChunk> StreamAsync(AiEndpoint endpoint, AiChatRequest request, CancellationToken cancellationToken);
}
public sealed record AiEndpoint(string ProviderCode, string BaseUrl, string? ApiKey, string AuthScheme, string Model);
```

`AdminAiOptions`(节 `SmartAdmin:Ai`,挂到 `SmartAdminOptions.Ai`):

| 属性 | 默认 | 说明 |
|---|---|---|
| `TimeoutSeconds` | 120 | 单次调用超时;流式按整段计 |
| `Http.BlockedCidrs` | 与 `Jobs:Http` 同一默认(云元数据段) | SSRF 围栏 |
| `Http.AllowedHosts` | 空 = 不限 | 配了则 Base URL 主机必须命中 |
| `Http.Proxy` | 空 | 显式代理地址;配了就不再对目标 IP 复检,只做 URL 级校验(操作者自担) |

错误码(`ErrorCode.cs` 新开 `49000–49999  AI 模型` 段,头部分段表同步加一行):

| 码 | 名 | MsgKey |
|---|---|---|
| 49001 | `AiProviderNotFound` | `error.ai.providerNotFound` |
| 49002 | `AiProviderCodeExists` | `error.ai.providerCodeExists` |
| 49003 | `AiProviderDisabled` | `error.ai.providerDisabled` |
| 49004 | `AiApiKeyMissing` | `error.ai.apiKeyMissing` |
| 49005 | `AiBaseUrlBlocked` | `error.ai.baseUrlBlocked` |
| 49006 | `AiPresetNotFound` | `error.ai.presetNotFound` |
| 49007 | `AiPresetImmutable` | `error.ai.presetImmutable` |
| 49010 | `AiModelNotFound` | `error.ai.modelNotFound` |
| 49011 | `AiNoDefaultModel` | `error.ai.noDefaultModel` |
| 49012 | `AiModelDisabled` | `error.ai.modelDisabled` |
| 49013 | `AiModelNameExists` | `error.ai.modelNameExists` |
| 49020 | `AiUpstreamError` | `error.ai.upstreamError` |
| 49021 | `AiUpstreamTimeout` | `error.ai.upstreamTimeout` |
| 49022 | `AiUpstreamRateLimited` | `error.ai.upstreamRateLimited` |
| 49023 | `AiUpstreamAuthFailed` | `error.ai.upstreamAuthFailed` |
| 49024 | `AiUpstreamBadResponse` | `error.ai.upstreamBadResponse` |
| 49030 | `AiDataProtectionKeyMissing` | `error.ai.dataProtectionKeyMissing` |
| 49031 | `AiSceneRequired` | `error.ai.sceneRequired` |

其它 Core 改动:`CacheKeys.AiProviders()`(`"ai:providers"`);`IDataProtectionKeyProvider` 加默认接口成员 `bool IsEphemeral => false`,`LocalDataProtectionKeyProvider` 在「进程内临时密钥」分支返回 true(非破坏性,消费者自定义实现不受影响)。

## 6. 厂商预置(`AiProviderPresets`,静态表,`GET /presets` 给表单下拉)

| code | 名称 | 协议 | Base URL | 鉴权 | 起步模型清单 |
|---|---|---|---|---|---|
| `openai` | OpenAI | openai | `https://api.openai.com/v1` | bearer | gpt-4.1、gpt-4.1-mini |
| `azure-openai` | Azure OpenAI | openai | `https://{resource}.openai.azure.com/openai/v1`(用户改) | api-key | 模型名 = 部署名,清单留空 |
| `anthropic` | Anthropic | anthropic | `https://api.anthropic.com` | x-api-key(+ `anthropic-version: 2023-06-01`) | claude-opus-5、claude-sonnet-5、claude-haiku-4-5 |
| `deepseek` | DeepSeek | openai | `https://api.deepseek.com/v1` | bearer | deepseek-chat、deepseek-reasoner |
| `qwen` | 通义千问 | openai | `https://dashscope.aliyuncs.com/compatible-mode/v1` | bearer | qwen-plus、qwen-max、qwen-turbo |
| `zhipu` | 智谱 GLM | openai | `https://open.bigmodel.cn/api/paas/v4` | bearer | glm-4-plus、glm-4-flash |
| `moonshot` | Kimi | openai | `https://api.moonshot.cn/v1` | bearer | 实施时按官方文档填 |
| `doubao` | 豆包(火山方舟) | openai | `https://ark.cn-beijing.volces.com/api/v3` | bearer | 模型名 = 接入点 id,清单留空 |
| `gemini` | Gemini | openai | `https://generativelanguage.googleapis.com/v1beta/openai` | bearer | gemini-2.5-pro、gemini-2.5-flash |
| `ollama` | Ollama(本地) | openai | `http://localhost:11434/v1` | none | 清单留空,用户按本地已拉取的填 |
| `custom` | 自定义(OpenAI 兼容) | openai | 用户填 | bearer | 空 |

模型名变动快,**实施批次 1 时逐家核对官方文档再落表**;预置只是起步,界面上可增删。围栏默认只封云元数据段,`localhost` 放行,所以 Ollama 预设开箱可用。

## 7. 服务层规则

### 7.1 `AiProviderService`(`IAiProviderService`,Scoped,全 `virtual`)

- `PageAsync` / `GetAsync` / `AddAsync` / `UpdateAsync` / `DeleteAsync` / `SetEnabledAsync` / `TestAsync` / `PresetsAsync` / `GetEnabledAsync`(缓存)/ `ResolveAsync(providerCode?, model?)`。
- 出参一律 `AiProviderView`:不含 `ApiKeyProtected`,只有 `ApiKeyHint` + `HasApiKey`;附带模型列表。
- Key 规则:入参 `ApiKey` 为 null/空 → 不改;非空 → 先查 `IDataProtectionKeyProvider.IsEphemeral`(true 抛 49030)→ `Protect` → 写 `ApiKeyProtected` + `ApiKeyHint`(尾四位)。明文只在这一次经过内存,不写日志(操作日志脱敏名单已含 `apikey`,入参自动打码)。
- 新增:`Preset` 必须命中预置表(49006);Code 唯一性检查含软删行(`ClearFilter<ISoftDelete>()`);按预置带出协议 / BaseUrl / 鉴权 / 模型行。
- 更新:`Preset` 与 `Protocol` 不可改(49007);模型行按入参整体同步(新增 / 更新 / 软删);`IsDefault` 全库唯一。
- BaseUrl:保存与测试都先过 `HttpFence.ValidateUrl`(49005);必须 http(s) 绝对地址。
- 启用:`AuthScheme != none` 且无 Key → 49004。
- 删除:软删厂商 + 其模型;用量日志保留(有冗余 `ProviderCode`)。
- 变更后:`cache.RemoveAsync(CacheKeys.AiProviders())` + `IEventBus.PublishAsync(new AiProviderChangedEvent(...))`。
- `TestAsync(id)`:用当前库里的配置向该厂商默认模型(没有则第一个启用模型)发一条最小对话(`"ping"`,`MaxTokens = 8`),返回 `AiTestResult(Ok, Model, LatencyMs, Usage, Error)`;走 `IAiChatClient`,`Scene = "system.test"`,所以也进用量日志。

### 7.2 `AiChatClient`(`IAiChatClient`,Scoped)

- 解析顺序:`ProviderCode + Model` 都给 → 校验存在且启用;只给 `Model` → 在启用厂商里找第一个含该模型的;都不给 → 全局默认模型;找不到分别抛 49001 / 49010 / 49011,停用抛 49003 / 49012。
- 取适配器:按 `Protocol` 在 `IEnumerable<IAiProtocolAdapter>` 里匹配,没有抛 49024。
- 计时用 `TimeProvider`;超时用 `CancellationTokenSource` 联动 `AdminAiOptions.TimeoutSeconds`。
- 上游错误映射:HTTP 401/403 → 49023;429 → 49022;超时 / `TaskCanceledException`(非调用方取消)→ 49021;JSON 解析失败 → 49024;其它非 2xx → 49020,`ErrorMessage` 记状态码 + 响应前 512 字。
- 记账:非流式完成后写一行;流式在枚举结束(含异常)时写一行,`Usage` 取最后一块,没有则 `Source = Missing`、Token 记 0。调用方中途放弃枚举也要写(用 `try/finally`)。
- `UserId` 取 `ICurrentUser`,无会话时为 null。

### 7.3 适配器

**OpenAI 兼容**(`POST {BaseUrl}/chat/completions`):
- 鉴权头按 `AuthScheme`:bearer → `Authorization: Bearer`;api-key → `api-key`;none → 不加。
- 非流式读 `choices[0].message.content` 与 `usage.prompt_tokens / completion_tokens`,`id` 进 `RequestId`。
- 流式:`stream: true` + `stream_options: { include_usage: true }`;按行读 `data:`,`[DONE]` 结束;delta 取 `choices[0].delta.content`;最后一块 `usage` 有值即 `Reported`,否则 `Missing`。豆包 / Kimi 等若不支持 `stream_options`,由适配器捕获 400 后**去掉该字段重试一次**,并把 usage 标 Missing。

**Anthropic**(`POST {BaseUrl}/v1/messages`):
- 头:`x-api-key`、`anthropic-version: 2023-06-01`;`system` 消息抽到顶层 `system` 字段;`max_tokens` 必填(缺省 4096)。
- 非流式读 `content[].text` 拼接、`usage.input_tokens / output_tokens`、`stop_reason`。
- 流式:SSE 事件流,`message_start.message.usage.input_tokens` 记输入,`content_block_delta.delta.text` 是增量,`message_delta.usage.output_tokens` 是累计输出,`message_stop` 结束。
- 实施前按 `claude-api` skill 的 `curl/examples.md` 核对字段,不凭记忆。

### 7.4 HTTP 客户端与围栏

- `services.AddHttpClient(AiHttpClient.Name)`,`ConfigurePrimaryHttpMessageHandler` 用 `HttpFence.CreateHandler(options.Ai.Http)`;`Timeout` 设为 `Timeout.InfiniteTimeSpan`,由网关的 CTS 控制(流式需要)。
- `HttpFence`:从 `JobHttpFence` 抽出 `ValidateUrl / IsBlocked / TryParseCidr / CreateHandler`,入参改为公共的 `HttpFenceOptions`(`BlockedCidrs`、`AllowedHosts`、`Proxy`);`JobHttpFence` 保留原签名转发,消费者不受影响。**抽之前先确认 `JobHttpFence` 的现有测试覆盖,缺则先补**(CLAUDE.md:重构前先锁行为)。
- `Proxy` 非空时 handler 走该代理、不挂 `ConnectCallback`;文档写明这是操作者的选择。

### 7.5 用量查询(`AiUsageService`)

- `SummaryAsync(query)`:`Totals`(总 / 输入 / 输出 / 调用数 / 失败数 / 失败分类)+ `Groups`(按 `GroupBy` = provider / model / scene / user 聚合:Token、占比、调用、均耗时)。
- `TrendAsync(query)`:按日 `InputTokens` / `OutputTokens` / 调用数;日期边界按服务器时区,`From`/`To` 闭区间。
- `PageAsync(query)`:明细分页,按 `CreateTime desc`,支持厂商 / 模型 / 场景 / 用户 / 成败过滤。
- 一期直接 `GROUP BY` 查日志表,靠索引与保留期;量大再加日汇总表(二期)。

### 7.6 清理任务与配置

- `AiUsageLogCleanupJob : IAdminJob`,读配置中心 `sys.ai.usageRetentionDays`(默认 90,≤ 0 不清理),分批 500 删,照抄 `JobLogCleanupJob`。
- `DefaultJobSeed` 加一行:`Id = 2`,`Code = "sys-ai-usage-cleanup"`,cron `0 40 3 * * ?`,`IsSystem = true`,`SyncOnUpgrade` 沿用 false。
- `ConfigSeed` 加 `sys.ai.usageRetentionDays = 90`(分类与 `sys.job.logRetentionDays` 同组)。

## 8. 接口与权限

| 方法 | 路由 | 特性 | 权限按钮 |
|---|---|---|---|
| GET | `/api/v1/sys/ai/provider/page` | `[RolePermission]` | 711 |
| GET | `/api/v1/sys/ai/provider/{id}` | `[RolePermission]` | 711 |
| GET | `/api/v1/sys/ai/provider/presets` | `[RolePermission]` | 711 |
| POST | `/api/v1/sys/ai/provider/add` | `[RolePermission] [RequireReauth] [OperationLog("新增 AI 厂商")]` | 712 |
| PUT | `/api/v1/sys/ai/provider/{id}` | `[RolePermission] [RequireReauth] [OperationLog("更新 AI 厂商")]` | 713 |
| PUT | `/api/v1/sys/ai/provider/{id}/enabled` | `[RolePermission] [OperationLog("启停 AI 厂商")]` | 713 |
| DELETE | `/api/v1/sys/ai/provider/{id}` | `[RolePermission] [OperationLog("删除 AI 厂商")]` | 714 |
| POST | `/api/v1/sys/ai/provider/{id}/test` | `[RolePermission] [OperationLog("测试 AI 连接")]` | 715 |
| GET | `/api/v1/sys/ai/usage/summary` | `[RolePermission]` | 721 |
| GET | `/api/v1/sys/ai/usage/trend` | `[RolePermission]` | 721 |
| GET | `/api/v1/sys/ai/usage/page` | `[RolePermission]` | 721 |

两个控制器都挂 `[Module("Ai")]`,返回 `Result<T>`,分页入参继承 `PageInputBase`。

菜单种子(`DefaultMenuSeed.cs`,新开 `// ═══ 8xx AI 管理 ═══` 段;实施时为 `7xx`,后因多租户改造插入 `2xx 租户管理` 段整体后移一位,现为 `8xx`,详见 `DefaultMenuSeed` 头部分区表):

```csharp
new SysMenu { Id = 800, ParentId = 0,   Type = MenuType.Catalog, Title = "AI 管理",  Permission = "", Icon = "ph:robot-duotone", Sort = 7, Enabled = true, ModuleId = DefaultModuleSeed.BUILTIN_MODULE_ID },
new SysMenu { Id = 810, ParentId = 800, Type = MenuType.Menu,    Title = "AI 模型",  Permission = "", Path = "/system/ai-model", Component = "system/ai-model/index", Icon = "ph:brain-duotone", Sort = 1, Enabled = true, Visible = true },
new SysMenu { Id = 811, ParentId = 810, Type = MenuType.Button,  Title = "AI 厂商-查询",     Permission = Codes("GET:/api/v1/sys/ai/provider/page", "GET:/api/v1/sys/ai/provider/{id}", "GET:/api/v1/sys/ai/provider/presets"), Sort = 1, Enabled = true },
new SysMenu { Id = 812, ParentId = 810, Type = MenuType.Button,  Title = "AI 厂商-新增",     Permission = "POST:/api/v1/sys/ai/provider/add", Sort = 2, Enabled = true },
new SysMenu { Id = 813, ParentId = 810, Type = MenuType.Button,  Title = "AI 厂商-更新",     Permission = Codes("PUT:/api/v1/sys/ai/provider/{id}", "PUT:/api/v1/sys/ai/provider/{id}/enabled"), Sort = 3, Enabled = true },
new SysMenu { Id = 814, ParentId = 810, Type = MenuType.Button,  Title = "AI 厂商-删除",     Permission = "DELETE:/api/v1/sys/ai/provider/{id}", Sort = 4, Enabled = true },
new SysMenu { Id = 815, ParentId = 810, Type = MenuType.Button,  Title = "AI 厂商-测试连接", Permission = "POST:/api/v1/sys/ai/provider/{id}/test", Sort = 5, Enabled = true },
new SysMenu { Id = 820, ParentId = 800, Type = MenuType.Menu,    Title = "用量统计", Permission = "", Path = "/system/ai-usage", Component = "system/ai-usage/index", Icon = "ph:chart-line-up-duotone", Sort = 2, Enabled = true, Visible = true },
new SysMenu { Id = 821, ParentId = 820, Type = MenuType.Button,  Title = "AI 用量-查询",     Permission = Codes("GET:/api/v1/sys/ai/usage/summary", "GET:/api/v1/sys/ai/usage/trend", "GET:/api/v1/sys/ai/usage/page"), Sort = 1, Enabled = true },
```

`Sort` 以现有目录的实际排序为准,实施时核对。`DefaultMenuSeed` 头部注释的分区表加 `8xx AI 管理`;`MenuSeedIdLayoutTests` 会核对 1–4 号位的 HTTP 方法,815 是本页特有操作,落 5 号位。

## 9. 前端

| 文件 | 内容 |
|---|---|
| `types/api.ts` | `SysAiProviderView`、`SysAiModel`、`AiProviderInput`、`AiModelInput`、`AiProviderPreset`、`AiTestResult`、`AiUsageSummary`、`AiUsageGroupRow`、`AiUsageTrendPoint`、`SysAiUsageLog`、`AiUsageGroupBy` |
| `api/index.ts` | `aiProviderApi`(page / get / presets / add / update / setEnabled / remove / test)、`aiUsageApi`(summary / trend / page);查询参转 PascalCase,`pageParams` / `toPage` / `unwrap` 照旧 |
| `views/system/ai-model/index.vue` | 汇总行 + 工具栏 + 卡片网格;卡片 `components/ProviderCard.vue`;抽屉 `components/ProviderForm.vue`(`FormContainer variant="drawer" :width="640"`);`presets.ts` 放各预设的缩写与色块 |
| `views/system/ai-usage/index.vue` | 筛选卡(快捷区间 + 日期范围 + 厂商 / 模型 / 场景)+ 四格指标 + `LineChart` 趋势 + 分组表(`n-tabs` 四维度,`display-directive="show:lazy"`)+ `SmartTable` 明细 |
| `locales/zh-CN.ts` / `en-US.ts` | `aiModel.*`、`aiUsage.*`、`error.ai.*`(与第 5 节 MsgKey 逐字对上);`parity.spec.ts` 强制中英键一致 |
| `assets/icons/ph-subset.json` | `npm run gen:icons`;把三个种子图标名加进生成脚本的内核种子图标清单 |

页面行为要点:

- 卡片三态:启用(主色描边)、停用(降透明)、待配置 Key(虚线框 + 开关禁用)。启用开关走 `PUT {id}/enabled`,失败回弹(`StatusSwitch` 模式)。
- 抽屉:新增态「厂商预设」下拉选中即带出名称 / 协议 / Base URL / 模型清单;编辑态预设与协议只读。API Key 输入框编辑态留空 = 不改,旁边显示当前尾四位;新增态非 `none` 鉴权时必填。模型清单用可勾选列表,单选一个「全局默认」,可添加自定义模型名。头部「测试连接」按当前库里的配置测(未保存的改动不生效,提示语写清)。
- 用量页:筛选变化即刷新全部区块;分组表四维度共用 `summary` 接口,`groupBy` 参数切换;占比条按最大值归一;明细失败行显示错误类型。
- 权限:工具栏与卡片按钮 `v-auth` 对应第 8 节的码;用量页只有查询一颗。
- 分页尺寸不超过 200(`maxPageSize.spec.ts`)。

命令顺序:后端起来 → `cd web && npm run gen:api` → 写页面 → `npm run gen:icons` → `npm run typecheck && npm run lint && npm run format:check && npm test`。

## 10. 测试

后端(`backend/tests/SmartAdmin.Tests/Ai/`,xUnit v3 + `AdminAppFactory`,SQLite 默认):

| 文件 | 覆盖 |
|---|---|
| `AiProviderCrudTests` | 新增按预设带出模型;Code 唯一(含软删);出参不含 Key 只有尾四位;直接读库确认 `ApiKeyProtected` 不是明文且能 `Unprotect`;编辑留空不改 Key;预设 / 协议不可改;无 Key 不能启用;`IsDefault` 全库唯一;删除级联模型;云元数据 Base URL 被拒 |
| `AiChatClientTests` | 桩 `HttpMessageHandler`:OpenAI 非流式 / 流式(含 `include_usage` 末块);Anthropic 非流式 / 流式;流式无 usage → `Missing`;401 / 429 / 超时 / 坏 JSON 各映射到对应错误码;失败也记日志;解析顺序(显式 / 只给模型 / 默认模型 / 无默认抛 49011);Scene 为空抛 49031;`stream_options` 被 400 拒后去字段重试 |
| `AiUsageQueryTests` | 造 log 行后 summary / trend / page 的聚合、分组、过滤、日期边界 |
| `AiUsageLogCleanupJobTests` | 保留期内外各留 / 删;≤ 0 不清理 |
| `AiDataProtectionGuardTests` | `IsEphemeral = true` 的假 provider 下保存 Key 抛 49030 |
| `AiModuleDisableTests` | `DisabledModules = ["Ai"]` 时两个控制器 404,`IAiChatClient` 仍可注入 |
| `HttpFenceTests` | 从 `JobHttpFence` 抽公共件前后行为一致(先补齐现有覆盖) |

契约与一致性:`ReplaceabilityContract.Points` 加 `IAiChatClient`(Scoped)、`IAiProviderService`(Scoped)、`IAiUsageService`(Scoped);`MultiImplementation` 加 `IAiProtocolAdapter`。`PermissionCodeConsistencyTests`、`MenuSeedIdLayoutTests`、`SeedIdRangeTests`、`CodeFirst` 相关测试自动覆盖新种子与新表。

前端:不为页面单写 spec(仓库惯例);`parity.spec.ts`、`icons.spec.ts`、`maxPageSize.spec.ts` 三道闸自动命中。用量页若抽出纯函数(归一化占比、趋势序列拼装)则配一个 `*.spec.ts`。

e2e(可选):在 `web/e2e` 现有页面级冒烟模板上加「AI 模型页可渲染 + 新增厂商抽屉可打开」一条;不加也不阻塞本计划。

## 11. 文档与发布产出

- 文档站:`site/zh/guide/ai-models.md`(中文母版)+ `site/guide/ai-models.md`(英文译文):配置流程、预设表、消费者调用示例(含流式与 SSE 端点示例)、`DataProtection:Key` 与多副本、代理配置、模块关闭。写前读 `skills/write-docs.md`,过 `cd site && npm run lint:prose -- <page>`。
- `site/zh/guide/deployment/docker.md`(多副本页)补一句:多副本必须显式配置 `DataProtection:Key`,否则各副本临时密钥互不相认(调研时发现的现有缺口,与 AI 无关也该补)。
- `CHANGELOG.md` `## Unreleased` 下 `### Added` 一条。
- `README.md` 内置功能清单加一行。
- `docs/adr/0009-ai-gateway-in-kernel.md`:AI 网关进内核、协议适配器、直连不引 SDK、审批留给 Pro;被否方案:独立包、只留契约、Microsoft.Extensions.AI。
- `CONTEXT.md` 加「AI 管理」术语段:厂商、预设、协议、全局默认模型、场景、用量记录。
- `docs/design-mockups/ai-management.html` 已放入,实施后如有偏离在本文记一笔。

## 12. 实施批次(按提交切,每批独立可验)

### 批次 1:契约、错误码、实体、预置

- [x] `Core/Ai/*.cs`:第 5 节全部契约与 record
- [x] `Core/Options/AdminAiOptions.cs` + `SmartAdminOptions.Ai` + `HttpFenceOptions`(先定义,批次 4 再接围栏)
- [x] `ErrorCode.cs`:49xxx 段 + 头部分段表
- [x] `CacheKeys.AiProviders()`;`IDataProtectionKeyProvider.IsEphemeral` + `LocalDataProtectionKeyProvider` 实现
- [x] 三个实体(第 4 节),`Services/Ai/AiProviderPresets.cs`(逐家核对官方文档后落表)
- [x] `ConfigSeed` 加 `sys.ai.usageRetentionDays`
- [x] **验收**:`dotnet build backend/SmartAdmin.slnx -c Release` 零警告;`dotnet test` 全绿(现有测试不受影响);`MinimalHost` 启动建出三张表

### 批次 2:协议适配器与网关(不含控制器)

- [x] `Services/Ai/Protocols/OpenAiCompatibleAdapter.cs`、`AnthropicAdapter.cs`(先读 `claude-api` skill 的 curl 示例核对字段)
- [x] `Services/Ai/AiHttpClient.cs`(命名客户端常量与注册;围栏 handler 批次 4 接)
- [x] `Services/Ai/AiUsageService.cs`(先做 `RecordAsync`)、`AiChatClient.cs`
- [x] `ServicesSetup`:`TryAddScoped<IAiChatClient>`、`TryAddScoped<IAiUsageService>`、`TryAddEnumerable` 两个适配器、`AddHttpClient`
- [x] `ReplaceabilityContract` 登记 `IAiChatClient`、`IAiUsageService`、`IAiProtocolAdapter`
- [x] 测试:`AiChatClientTests`(桩 handler)
- [x] **验收**:`dotnet test backend/SmartAdmin.slnx -- --filter-class "*AiChatClient*"` 全绿;`ReplaceabilityTests` 全绿

### 批次 3:厂商服务、控制器、菜单种子

- [x] `Services/Ai/AiModels.cs`(DTO)、`IAiProviderService.cs`、`AiProviderService.cs`(第 7.1 节)、`AiProviderChangedEvent`
- [x] `AiUsageService` 补 `SummaryAsync / TrendAsync / PageAsync`
- [x] `AiProviderController.cs`、`AiUsageController.cs`
- [x] `DefaultMenuSeed` 8xx 段(实施时为 7xx,后因多租户改造整体后移一位) + 头部分区表
- [x] `ReplaceabilityContract` 登记 `IAiProviderService`
- [x] 测试:`AiProviderCrudTests`、`AiUsageQueryTests`、`AiModuleDisableTests`
- [x] **验收**:`dotnet test` 全绿(含 `PermissionCodeConsistencyTests`、`MenuSeedIdLayoutTests`);`/openapi/v1.json` 里 11 个端点齐全

### 批次 4:清理任务、缓存与事件、围栏、密钥守卫

- [x] 确认 / 补齐 `JobHttpFence` 测试 → 抽 `HttpFence` + `HttpFenceOptions`,`JobHttpFence` 转发 → 接到 `AiHttpClient` 的 handler
- [x] `AiProviderService` 的缓存读穿透 + 变更失效 + 事件;`AiChatClient` 改用缓存(批次 3 顺手做了)
- [x] `IsEphemeral` 守卫接入保存 Key 路径(批次 3 顺手做了)
- [x] `Jobs/AiUsageLogCleanupJob.cs` + `DefaultJobSeed` Id 2 + `TryAddEnumerable`
- [x] 测试:`AiUsageLogCleanupJobTests`、`AiDataProtectionGuardTests`、`HttpFenceTests`
- [x] **验收**:`dotnet test` 全绿(1008/1008);`ci.bat`(默认集)绿

### 批次 5:前端两页

- [x] 后端跑起来 → `npm run gen:api`
- [x] `types/api.ts`、`api/index.ts`
- [x] `views/system/ai-model/`(index + ProviderCard + ProviderForm + presets.ts)
- [x] `views/system/ai-usage/index.vue`
- [x] 语言包三段(zh / en);`npm run gen:icons`
- [x] **验收**:`npm run typecheck && npm run lint && npm run format:check && npm test` 全绿;`npm run dev` 手工走查:新增 / 编辑 / 留空不改 Key / 测试连接 / 启停回弹 / 删除 / 无权限按钮隐藏 / 用量页筛选联动 / 暗色

### 批次 6:文档、ADR、变更记录

- [x] `site/zh/guide/ai-models.md` + 英文页,`lint:prose` 过
- [x] 多副本部署页补 `DataProtection:Key`
- [x] `CHANGELOG.md`、`README.md`、`CONTEXT.md`
- [x] `docs/adr/0009-ai-gateway-in-kernel.md`,ADR README 索引加行
- [x] 本文状态改「已完成」,记录与设计图的偏离
- [x] **验收**:`ci.bat` 默认集全绿;推 `dev` 让 CI 跑方言腿(留给用户决定何时推送)

每批一个或几个提交,提交信息按 `skills/write-commit.md`(`feat(ai): …` / `test(ai): …` / `docs(site): …`)。

## 13. 验证命令

```bash
dotnet build backend/SmartAdmin.slnx -c Release
dotnet test  backend/SmartAdmin.slnx
dotnet test  backend/SmartAdmin.slnx -- --filter-class "*Ai*"
dotnet run   --project backend/samples/MinimalHost          # 看三张表建出、/openapi/v1.json 有 ai 端点
cd web && npm run gen:api && npm run gen:icons
cd web && npm run typecheck && npm run lint && npm run format:check && npm test
ci.bat                                                       # 每批结束前
ci.bat -Stage backend -Dialect mysql,postgres,sqlserver      # 只在改了实体 / 查询后跑一次,或交给 CI
```

## 14. 风险与注意事项

- **主密钥**:生产未配 `DataProtection:Key` 时内核用进程内临时密钥,重启后 Key 解不开。本计划在保存时拒绝(49030),文档站要写清;多副本必须共享同一把。
- **SSRF**:Base URL 与测试连接是用户可控的出站请求,围栏必须在保存与每次调用两处都过;`Proxy` 一旦配置就绕过 IP 复检,只给可信运维用。
- **流式记账**:部分国内厂商不支持 `stream_options.include_usage`,用量标 `Missing` 而不是估算;用量页对 Missing 行单独标注,不混进总数误导。
- **模型名漂移**:预置清单只是起步,页面可增删;不要为了"全"把不确定的名字塞进去。
- **CodeFirst 演进**:三张表都是新表,可用非空列;将来给已发版的表加列必须可空(`create-entity.md` 的规则)。
- **SqlServer 方言**:大文本列走 `StaticConfig.CodeFirst_BigString`,不用 `text`;布尔谓词与 `GROUP BY` 日期截断在四种方言上各跑一遍(CI 方言腿)。
- **可替换性**:所有服务方法 `virtual`、`TryAdd*` 注册;消费者要接 KMS 换 `ISecretProtector`,要接私有协议加 `IAiProtocolAdapter`,不需要改内核。
- **不记正文**:用量日志不含 prompt 与回复,审批内容留在 Pro 自己的表里。

## 15. 二期候选(按需求再排)

提示词模板页(后台维护提示词,代码按模板名调用)、配额与告警(月上限、到 80% 通知)、费用估算界面、日汇总表、多 Key 轮换、Embedding 与函数调用、对话记录页、面向浏览器的通用 SSE 端点。
