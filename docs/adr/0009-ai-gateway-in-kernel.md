# ADR 0009 — AI 网关进内核:统一入口、按协议适配、直连不引 SDK

- 状态:已采纳(2026-09-13)
- 相关:[[ADR-0004]](自建调度器,「进内核不进包」的同款先例);面向读者的文档见 `site/zh/guide/ai-models.md`

## 背景

下游消费方(SmartAdmin-Pro 的 AI 审批等)需要调用大模型。不做统一网关,每个消费方就要各自接厂商 SDK、各自存 Key、各自记账,厂商一多复用直接为负,Key 的加密落库与脱敏展示还会在每个消费方重写一遍。产品线边界先定:网关能力(接入 + 用量)进开源内核,审批流程本身是 Pro(商业付费)的东西,内核不做工作流。

## 决策一:进内核,不做独立包

契约在 `SmartAdmin.Core/Ai/`,服务与适配器在 `SmartAdmin.Services/Ai/`,控制器挂 `[Module("Ai")]`,随内核一起发布,不建 `SmartAdmin.Ai` 可选包。理由:HttpClient 直连没有第三方依赖,本就符合"内核只依赖 SqlSugarCore + Microsoft.*"这条红线,不需要用"进包"来规避依赖问题;独立包要单独走种子、权限一致性测试(`HighSensitivityConsistencyTests`)、发版计数,换来的收益只是让不用 AI 的消费者少建三张空表——而消费者本就可以用 `SmartAdmin:Api:DisabledModules` 关掉这两个控制器的路由,不需要"不装"这一级自由度。与 ADR 0004(定时任务)是同一条推理链。

## 决策二:按协议写适配器,不按厂商;不引任何厂商 SDK

一期两套协议适配器(OpenAI 兼容、Anthropic)覆盖 10 个预置厂商。Gemini、DeepSeek、通义千问等全部复用 OpenAI 兼容适配器,只是 Base URL 和鉴权方式不同。被否的两条路:

- **各厂商官方 SDK**:每接一家就添一个包,版本各自演进,内核的依赖红线守不住。
- **`Microsoft.Extensions.AI`**:表面是一个统一抽象,但它的每个厂商实现仍是独立 NuGet 包,并不能省掉"按厂商装包"这一步;它是一个仍在快速演进的外部抽象层,把内核对外的 `IAiChatClient` 契约绑定在一个自己不掌控演进节奏的接口上,不如直接拥有一个内核自己的、按需求定制的最小契约。

`IAiProtocolAdapter` 走 `TryAddEnumerable` 登记,消费者需要私有协议时自己加一个实现,内核不必先支持。

## 决策三:Key 加密落库,KMS/HSM 留给扩展点

一期用内核已有的 `ISecretProtector`(AES-GCM)加密 Key,接口只回脱敏尾四位,不接外部 KMS。理由是 TOTP 种子已经在用同一套保护机制,复用而不是新引入一条加密路径;要接 KMS/HSM 的消费者前置注册 `IDataProtectionKeyProvider` 即可整体替换,内核侧零改动。`IsEphemeral` 是这条链路新加的一个契约:数据保护密钥若是进程内临时密钥(未显式配置主密钥的非开发环境),保存 Key 直接拒绝(49030),否则重启或者换个副本,已保存的 Key 就解不开——这个坑此前只有 TOTP 会踩到,且没有主动拒绝,这次借 AI 网关的机会把守卫做成契约默认成员,顺带补上。

## 决策四:内核不做浏览器直连的 SSE 端点

`IAiChatClient.StreamAsync` 只回 `IAsyncEnumerable<AiChatChunk>`,是纯领域层抽象,不预设传输协议。要不要包成 SSE、挂什么鉴权、限不限流,全部留给消费方自己的控制器。理由:审批场景要不要流式展示、流式端点挂在哪条路由、跟什么鉴权模型绑定,这些都是业务决策,内核先做一个通用端点反而会框死消费方的选择;.NET 10 原生 `TypedResults.ServerSentEvents` 已经把这层封装做得足够薄,消费方自己接不构成负担。

## 约定与后果

- **错误码**:`ErrorCode.cs` 新开 49000-49999 段,49001-49013 建模层(厂商/模型),49020-49024 上游调用映射,49030-49031 密钥守卫与场景校验。
- **菜单 Id**:顶层目录 700(「AI 管理」),710/720 两个页面,7xx 段落定后 8xx-9xx 留给后续新目录。
- **缓存**:`CacheKeys.AiProviders()` 整表读穿透,厂商增删改/启停时经 `AiProviderChangedEvent` + 缓存失效双通道同步,不接事件总线之外的机制。
- **用量表**:`sys_ai_usage_log` 不记对话正文,只记统计字段;审批等业务内容留在消费方自己的表里。
- **可替换性**:`IAiChatClient`/`IAiProviderService`/`IAiUsageService` 全部 `TryAdd` + `virtual`,受 `ReplaceabilityTests` 锁定;`IAiProtocolAdapter` 走 `MultiImplementation` 登记。
- 预置表、接口路由、页面细节以代码和 `site/zh/guide/ai-models.md` 为准,本 ADR 不重复。
