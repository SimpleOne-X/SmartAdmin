# AI 模型

运维在后台「AI 管理」页填一个 Key，业务代码注入 `IAiChatClient` 就能调用任意已配置厂商的大模型，选型、鉴权、Token 记账全部不用自己写。内核不提供聊天界面，也不提供面向浏览器的 SSE 端点，那部分由消费方自己的控制器负责。

## 配置一个厂商

去「系统管理 → AI 管理 → AI 模型」，点「新增厂商」，选一个预置（协议、Base URL、鉴权方式跟着带出来），填 Key，保存。厂商创建后编码和预置不可再改，改的话删了重建。

新增后厂商默认停用，没有 Key 就是这个状态。填了 Key 保存，或者本来就走 `none` 鉴权（比如 Ollama），才能点「启用」。启用前点一下「测试连接」，用当前配置发一条最小对话，能看到延迟和用量，坏配置当场定位，不用等业务代码跑起来才发现。

## 厂商预置

| 编码 | 名称 | 协议 | 鉴权 | 说明 |
|---|---|---|---|---|
| `openai` | OpenAI | openai | bearer | |
| `azure-openai` | Azure OpenAI | openai | api-key | 模型名 = 部署名，起步模型清单留空 |
| `anthropic` | Anthropic | anthropic | x-api-key | |
| `deepseek` | DeepSeek | openai | bearer | |
| `qwen` | 通义千问 | openai | bearer | |
| `zhipu` | 智谱 GLM | openai | bearer | |
| `moonshot` | Kimi | openai | bearer | |
| `doubao` | 豆包（火山方舟） | openai | bearer | 模型名可用示例值，也可换成控制台建的接入点 `ep-xxx` |
| `gemini` | Google Gemini | openai | bearer | 走官方 OpenAI 兼容层，该层文档仍标 beta |
| `ollama` | Ollama（本地） | openai | none | 本地跑，围栏默认放行 `localhost` |
| `custom` | 自定义（OpenAI 兼容） | openai | bearer | Base URL 留空待填，接自建代理或反代网关 |

模型名变动快，这张表只是起步清单，界面上随时能增删。协议只有两种：OpenAI 兼容与 Anthropic。新增厂商只要接口长得像其中一种，选对应协议、自己填 Base URL 和鉴权方式即可，不必等内核适配。

## 业务代码怎么调

```csharp
public class ApprovalSummaryService(IAiChatClient ai)
{
    public async Task<string> SummarizeAsync(long approvalId, string content, CancellationToken cancellationToken)
    {
        var response = await ai.ChatAsync(new AiChatRequest
        {
            Scene = "approval.summary",
            Messages = [AiChatMessage.User($"用一句话总结这条审批的核心诉求：\n{content}")],
        }, cancellationToken);
        return response.Content;
    }
}
```

`Scene` 必填，空串直接抛 `49031`。用量统计页按它拆账，写 `approval.summary`、`ticket.reply` 这类点分层级的名字，方便按前缀聚合查询。不显式给 `ProviderCode` 或 `Model`，网关按全局默认模型解析；两者都没配置时抛 `49011`，去「AI 模型」页把某个模型设成默认再试。

## 流式与 SSE

`StreamAsync` 返回 `IAsyncEnumerable<AiChatChunk>`，是纯网关层的抽象，不假定任何传输方式。接成浏览器能订阅的 SSE，用 .NET 10 原生的 `TypedResults.ServerSentEvents`：

```csharp
app.MapGet("/api/approval/{id}/summarize/stream", (
    long id, string content, IAiChatClient ai, CancellationToken cancellationToken) =>
    TypedResults.ServerSentEvents(
        ai.StreamAsync(new AiChatRequest
        {
            Scene = "approval.summary",
            Messages = [AiChatMessage.User($"总结审批单 {id}：{content}")],
        }, cancellationToken),
        eventType: "chunk"));
```

鉴权、限流、这条路由要不要挂在权限校验之下，都是消费方控制器自己的事，内核不预设。`AiChatChunk.Usage` 只在最后一块非空，前端拼接 `Delta` 展示增量文本，看到 `Usage` 非空就代表这轮对话结束。

## 代理配置

出站请求默认直连，走 `SmartAdmin:Ai:Http` 的围栏，默认封云元数据段与回环，放行内网与 `localhost`，和[定时任务](/zh/guide/scheduled-jobs)那道 HTTP 围栏是同一套机制。需要经代理出网时配 `SmartAdmin:Ai:Http:Proxy`，配了之后请求改走代理，内核不再对目标 IP 做解析后复检：代理链上真实目标由代理自己解析，围栏对它天然失效。这是运维的主动选择，风险自担，界面不开放这一项，只能写配置文件。

## 多副本部署

多副本部署下，`DataProtection:Key` 必须每个副本配成同一个值。配错或者漏配，副本 A 保存的 Key 到副本 B 就读不出来，详见[容器化与多副本](/zh/guide/deployment/docker)的「多副本必须共享同一把 `DataProtection:Key`」。

## 用量记录与清理

用量记录写进 `sys_ai_usage_log`，不含对话正文，只有 Token 数、耗时、成败这类统计字段。审批内容这类业务数据不进内核日志，该留在消费方自己的表里。保留天数是配置中心的 `sys.ai.usageRetentionDays`，默认 90 天，内置的 `AiUsageLogCleanupJob` 每天 03:40 分批清理过期行，和[定时任务](/zh/guide/scheduled-jobs)讲的执行记录清理是同一套节奏。

## 关掉整个模块

整模块可以下线：`SmartAdmin:Api:DisabledModules` 里加 `"Ai"`，两个控制器的路由随之消失，后台侧栏也不再有「AI 管理」入口。`IAiChatClient` 不受影响，业务代码照常能注入、照常能调，因为模块开关只摘路由，从不摘 DI 注册，这条规矩对所有 `[Module]` 控制器都成立，不是 AI 网关的特例。已经配好的厂商和 Key 还在库里，只是没有界面能再改它们。
