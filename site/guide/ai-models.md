# AI Models

An operator fills in one API key on the "AI Models" admin page, and business code injects `IAiChatClient` to call any configured provider's model — provider selection, auth, and token accounting are handled for you. The kernel ships no chat UI and no browser-facing SSE endpoint; that's the consuming controller's job.

## Configure a provider

Go to System → AI Models → AI Models, click "Add Provider," pick a preset (protocol, base URL, and auth scheme come along with it), fill in the key, save. Once created, a provider's code and preset can't be changed — delete and recreate instead.

A newly added provider starts disabled; that's simply what "no key yet" looks like. It can only be enabled once a key is saved, or when its auth scheme is `none` (Ollama, for instance). Before enabling, click "Test Connection" to send a minimal chat with the current configuration — latency and usage come back right there, so a bad config surfaces on the spot instead of the first time business code hits it.

## Provider presets

| Code | Name | Protocol | Auth | Notes |
|---|---|---|---|---|
| `openai` | OpenAI | openai | bearer | |
| `azure-openai` | Azure OpenAI | openai | api-key | Model name = deployment name; starter model list is empty |
| `anthropic` | Anthropic | anthropic | x-api-key | |
| `deepseek` | DeepSeek | openai | bearer | |
| `qwen` | Qwen | openai | bearer | |
| `zhipu` | Zhipu GLM | openai | bearer | |
| `moonshot` | Kimi | openai | bearer | |
| `doubao` | Doubao (Volcano Ark) | openai | bearer | Sample model names work as-is, or swap in an `ep-xxx` endpoint created in the console |
| `gemini` | Google Gemini | openai | bearer | Goes through the official OpenAI-compatible layer, which is still marked beta |
| `ollama` | Ollama (local) | openai | none | Runs locally; the fence allows `localhost` by default |
| `custom` | Custom (OpenAI-compatible) | openai | bearer | Base URL is left blank for you to fill in — point it at your own proxy or gateway |

Model names churn fast, so this table is only a starting point — the admin page lets you add or remove entries freely. There are only two protocols: OpenAI-compatible and Anthropic. Adding a provider just means picking whichever protocol its API resembles and filling in its base URL and auth scheme yourself; you don't have to wait on a kernel update.

## Calling it from business code

```csharp
public class ApprovalSummaryService(IAiChatClient ai)
{
    public async Task<string> SummarizeAsync(long approvalId, string content, CancellationToken cancellationToken)
    {
        var response = await ai.ChatAsync(new AiChatRequest
        {
            Scene = "approval.summary",
            Messages = [AiChatMessage.User($"Summarize this approval request in one sentence:\n{content}")],
        }, cancellationToken);
        return response.Content;
    }
}
```

`Scene` is required — an empty string throws `49031`. The usage page groups by it, so dotted, hierarchical names like `approval.summary` or `ticket.reply` pay off when you later aggregate by prefix. Leave `ProviderCode`/`Model` unset and the gateway resolves the global default model; with neither configured it throws `49011` — go mark some model as default on the AI Models page.

## Streaming and SSE

`StreamAsync` returns `IAsyncEnumerable<AiChatChunk>` — a pure gateway-layer abstraction that assumes nothing about transport. Wiring it up as a browser-subscribable SSE stream uses .NET 10's native `TypedResults.ServerSentEvents`:

```csharp
app.MapGet("/api/approval/{id}/summarize/stream", (
    long id, string content, IAiChatClient ai, CancellationToken cancellationToken) =>
    TypedResults.ServerSentEvents(
        ai.StreamAsync(new AiChatRequest
        {
            Scene = "approval.summary",
            Messages = [AiChatMessage.User($"Summarize approval {id}: {content}")],
        }, cancellationToken),
        eventType: "chunk"));
```

Auth, rate limiting, whether this route sits behind a permission check — all of that is the consuming controller's own call; the kernel doesn't presume any of it. `AiChatChunk.Usage` is only non-null on the final chunk — the frontend concatenates `Delta` to render incremental text, and a non-null `Usage` is the signal that this turn is done.

## Proxy configuration

Outbound calls connect directly by default, behind the `SmartAdmin:Ai:Http` fence — cloud metadata ranges and loopback are blocked, private networks and `localhost` are allowed, the same mechanism [Scheduled Jobs](/guide/scheduled-jobs) uses for its own HTTP fence. Route through a proxy by setting `SmartAdmin:Ai:Http:Proxy`; once set, requests go through that proxy and the kernel stops re-checking the resolved IP, because the proxy is the one actually resolving and connecting to the real target — the fence has no visibility into that hop. That's a deliberate operator choice with the risk on them, so it's config-file only; there's no UI toggle for it.

## Multi-replica deployments

Every replica needs the exact same `DataProtection:Key`. Get it wrong or leave it unset and a key saved on replica A won't decrypt on replica B — see "Every replica needs the same `DataProtection:Key`" in [Containers & Multi-Replica](/guide/deployment/docker).

## Usage records and cleanup

Usage records land in `sys_ai_usage_log` with no conversation content — just statistics: token counts, latency, success/failure. Approval content and the like is business data that stays out of the kernel's log, in the consumer's own tables. Retention lives in the config center as `sys.ai.usageRetentionDays` (default 90 days), and the built-in `AiUsageLogCleanupJob` sweeps expired rows in batches every day at 03:40, on the same cadence as the log cleanup job described in [Scheduled Jobs](/guide/scheduled-jobs).

## Turning off the whole module

The whole module can be switched off: add `"Ai"` to `SmartAdmin:Api:DisabledModules` and both controllers' routes disappear, along with the "AI Models" entry in the admin sidebar. `IAiChatClient` is unaffected — business code keeps injecting and calling it, because the module switch only removes routes, never DI registrations. That's true of every `[Module]`-tagged controller in the kernel, not something special to the AI gateway. Providers and keys already configured stay in the database; there's just no UI left to change them.
