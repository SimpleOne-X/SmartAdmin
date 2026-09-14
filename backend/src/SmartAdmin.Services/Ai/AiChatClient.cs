using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlSugar;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <inheritdoc cref="IAiChatClient" />
public class AiChatClient(
    ISqlSugarClient db,
    ICacheProvider cache,
    AdminCacheOptions cacheOptions,
    ISecretProtector secretProtector,
    IEnumerable<IAiProtocolAdapter> adapters,
    IAiUsageService usageService,
    ICurrentUser currentUser,
    AdminAiOptions options,
    ILogger<AiChatClient> logger) : IAiChatClient
{
    /// <inheritdoc />
    public virtual async Task<AiChatResponse> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        AdminException.ThrowIf(string.IsNullOrWhiteSpace(request.Scene), ErrorCode.AiSceneRequired);
        AdminException.ThrowIf(HasSystemMultimodalMessage(request), ErrorCode.AiMultimodalSystemUnsupported);

        var (provider, model) = await ResolveAsync(request.ProviderCode, request.Model, cancellationToken);
        var adapter = ResolveAdapter(provider.Protocol);
        var endpoint = BuildEndpoint(provider, model);
        AdminException.ThrowIf(request.ResponseSchema.HasValue && !endpoint.SupportsJsonSchema, ErrorCode.AiStructuredOutputUnsupported);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await adapter.ChatAsync(endpoint, request, cts.Token);
            sw.Stop();
            var result = response with { LatencyMs = (int)sw.ElapsedMilliseconds };
            await RecordAsync(provider, model, request, result.Usage, result.LatencyMs, success: true, error: null, streamed: false, result.RequestId);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var mapped = MapAdapterException(ex, cancellationToken);
            await RecordAsync(provider, model, request, usage: null, (int)sw.ElapsedMilliseconds, success: false, error: mapped, streamed: false, requestId: null);
            throw mapped;
        }
    }

    /// <inheritdoc />
    public virtual async IAsyncEnumerable<AiChatChunk> StreamAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AdminException.ThrowIf(string.IsNullOrWhiteSpace(request.Scene), ErrorCode.AiSceneRequired);
        AdminException.ThrowIf(HasSystemMultimodalMessage(request), ErrorCode.AiMultimodalSystemUnsupported);

        var (provider, model) = await ResolveAsync(request.ProviderCode, request.Model, cancellationToken);
        var adapter = ResolveAdapter(provider.Protocol);
        var endpoint = BuildEndpoint(provider, model);
        AdminException.ThrowIf(request.ResponseSchema.HasValue && !endpoint.SupportsJsonSchema, ErrorCode.AiStructuredOutputUnsupported);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var sw = Stopwatch.StartNew();
        AiUsage? usage = null;
        AdminException? failure = null;
        var enumerator = adapter.StreamAsync(endpoint, request, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // 外层 try 只挂 finally(不挂 catch)才允许在其中 yield;内层 try/catch 专门兜 MoveNextAsync 的异常并转码。
            while (true)
            {
                AiChatChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    chunk = enumerator.Current;
                }
                catch (Exception ex)
                {
                    failure = MapAdapterException(ex, cancellationToken);
                    throw failure;
                }

                if (chunk.Usage is not null) usage = chunk.Usage;
                yield return chunk;
            }
        }
        finally
        {
            // 调用方中途放弃枚举(提前 break/Dispose 上层迭代器)也会走到这里——用量必须照记,不能只在正常收尾时记账。
            sw.Stop();
            await enumerator.DisposeAsync();
            await RecordAsync(provider, model, request, usage, (int)sw.ElapsedMilliseconds, success: failure is null, failure, streamed: true, requestId: null);
        }
    }

    /// <summary>按显式厂商/模型、只给模型、只给厂商、都不给(全局默认模型)四种组合解析;找不到/停用按 §7.2 规则分别抛码。</summary>
    protected virtual async Task<(CachedProvider Provider, CachedModel Model)> ResolveAsync(
        string? providerCode, string? modelName, CancellationToken cancellationToken)
    {
        var providers = await GetProvidersAsync(cancellationToken);

        if (providerCode is not null && modelName is not null)
        {
            var provider = FindProvider(providers, providerCode);
            var model = FindModel(provider, modelName);
            return (provider, model);
        }

        if (modelName is not null)
        {
            foreach (var provider in providers.Where(p => p.Enabled))
            {
                var model = provider.Models.FirstOrDefault(m => m.Name == modelName && m.Enabled);
                if (model is not null) return (provider, model);
            }
            throw new AdminException(ErrorCode.AiModelNotFound);
        }

        if (providerCode is not null)
        {
            var provider = FindProvider(providers, providerCode);
            var model = provider.Models.FirstOrDefault(m => m.IsDefault && m.Enabled)
                ?? provider.Models.FirstOrDefault(m => m.Enabled);
            AdminException.ThrowIf(model is null, ErrorCode.AiNoDefaultModel);
            return (provider, model!);
        }

        foreach (var provider in providers.Where(p => p.Enabled))
        {
            var model = provider.Models.FirstOrDefault(m => m.IsDefault && m.Enabled);
            if (model is not null) return (provider, model);
        }
        throw new AdminException(ErrorCode.AiNoDefaultModel);
    }

    /// <summary>两个协议的 system 字段都只接受纯文本,System 角色消息带 Parts(图片等多段内容)直接拒绝</summary>
    private static bool HasSystemMultimodalMessage(AiChatRequest request) =>
        request.Messages.Any(m => m.Role == AiChatRole.System && m.Parts is { Count: > 0 });

    private static CachedProvider FindProvider(IReadOnlyList<CachedProvider> providers, string code)
    {
        var provider = providers.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
        AdminException.ThrowIf(provider is null, ErrorCode.AiProviderNotFound);
        AdminException.ThrowIf(!provider!.Enabled, ErrorCode.AiProviderDisabled);
        return provider;
    }

    private static CachedModel FindModel(CachedProvider provider, string name)
    {
        var model = provider.Models.FirstOrDefault(m => m.Name == name);
        AdminException.ThrowIf(model is null, ErrorCode.AiModelNotFound);
        AdminException.ThrowIf(!model!.Enabled, ErrorCode.AiModelDisabled);
        return model;
    }

    /// <summary>厂商(含模型)整表读穿透缓存;变更即失效(批次 3 的 AiProviderService 接管失效)。缓存全表而非只缓存启用行,
    /// 是因为显式指定了停用厂商/模型时要能分清"不存在"(49001/49010)与"存在但停用"(49003/49012)。</summary>
    protected virtual async Task<IReadOnlyList<CachedProvider>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.AiProviders();
        var cached = await cache.GetAsync<List<CachedProvider>>(cacheKey, cancellationToken);
        if (cached is not null) return cached;

        var providers = await db.Queryable<SysAiProvider>().ToListAsync(cancellationToken);
        var providerIds = providers.Select(p => p.Id).ToList();
        var models = providerIds.Count == 0
            ? []
            : await db.Queryable<SysAiModel>().Where(m => providerIds.Contains(m.ProviderId)).ToListAsync(cancellationToken);

        var result = providers
            .Select(p => new CachedProvider(
                p.Id, p.Code, p.Preset, p.Protocol, p.BaseUrl, p.AuthScheme, p.ApiKeyProtected, p.Enabled,
                models.Where(m => m.ProviderId == p.Id)
                    .Select(m => new CachedModel(m.Name, m.Enabled, m.IsDefault))
                    .ToList()))
            .ToList();

        var ttl = cacheOptions.PermissionMinutes > 0 ? TimeSpan.FromMinutes(cacheOptions.PermissionMinutes) : (TimeSpan?)null;
        await cache.SetAsync(cacheKey, result, ttl, cancellationToken);
        return result;
    }

    /// <summary>解密 Key(明文只在本次调用经过内存,不落日志);未配置 Key 时传 null,none 鉴权的厂商(如 Ollama)本就用不到。</summary>
    protected virtual AiEndpoint BuildEndpoint(CachedProvider provider, CachedModel model)
    {
        var apiKey = string.IsNullOrEmpty(provider.ApiKeyProtected) ? null : secretProtector.Unprotect(provider.ApiKeyProtected);
        var supportsJsonSchema = AiProviderPresets.Find(provider.Preset)?.SupportsJsonSchema ?? true;
        return new AiEndpoint(provider.Code, provider.BaseUrl, apiKey, provider.AuthScheme, model.Name, supportsJsonSchema);
    }

    protected virtual IAiProtocolAdapter ResolveAdapter(string protocol)
    {
        var adapter = adapters.FirstOrDefault(a => string.Equals(a.Protocol, protocol, StringComparison.OrdinalIgnoreCase));
        AdminException.ThrowIf(adapter is null, ErrorCode.AiUpstreamBadResponse);
        return adapter!;
    }

    /// <summary>把适配器抛出的异常映射成网关自己的 ErrorCode(§7.2);已经是 AdminException 的直接放行,不重复包装。</summary>
    private static AdminException MapAdapterException(Exception ex, CancellationToken callerToken) => ex switch
    {
        AdminException admin => admin,
        AiUpstreamHttpException { StatusCode: 401 or 403 } http =>
            new AdminException(ErrorCode.AiUpstreamAuthFailed, http, fallbackMessage: $"{http.StatusCode}: {http.BodySnippet}"),
        AiUpstreamHttpException { StatusCode: 429 } http =>
            new AdminException(ErrorCode.AiUpstreamRateLimited, http, fallbackMessage: $"{http.StatusCode}: {http.BodySnippet}"),
        AiUpstreamHttpException http =>
            new AdminException(ErrorCode.AiUpstreamError, http, fallbackMessage: $"{(http.StatusCode?.ToString() ?? "?")}: {http.BodySnippet}"),
        JsonException json => new AdminException(ErrorCode.AiUpstreamBadResponse, json),
        // 调用方自己的 token 触发的取消要照原样冒泡(不该被吞成 49021);只有内部超时 CTS 触发的才算"调用超时"。
        OperationCanceledException oce when !callerToken.IsCancellationRequested =>
            new AdminException(ErrorCode.AiUpstreamTimeout, oce),
        _ => new AdminException(ErrorCode.AiUpstreamError, ex, fallbackMessage: ex.Message),
    };

    /// <summary>用量记录是旁路观测:写失败只记日志,不能让一次已经成功/已经失败的对话调用因为记账失败而改变结果。</summary>
    private async Task RecordAsync(
        CachedProvider provider, CachedModel model, AiChatRequest request,
        AiUsage? usage, int latencyMs, bool success, AdminException? error, bool streamed, string? requestId)
    {
        var record = new AiUsageRecord(
            ProviderId: provider.Id,
            ProviderCode: provider.Code,
            Model: model.Name,
            Scene: request.Scene,
            UserId: currentUser.UserId,
            InputTokens: usage?.InputTokens ?? 0,
            OutputTokens: usage?.OutputTokens ?? 0,
            UsageSource: usage?.Source ?? AiUsageSource.Missing,
            LatencyMs: latencyMs,
            Success: success,
            ErrorCode: error is null ? null : (int)error.Code,
            ErrorMessage: error?.Message,
            Streamed: streamed,
            RequestId: requestId);
        try
        {
            await usageService.RecordAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI 用量记录写入失败(scene={Scene}, provider={ProviderCode})", request.Scene, provider.Code);
        }
    }

    /// <summary>protected 而非 private:被同样 protected virtual 的 ResolveAsync/BuildEndpoint/GetProvidersAsync 用在签名里,可访问性必须能覆盖到子类。</summary>
    protected sealed record CachedProvider(
        long Id, string Code, string Preset, string Protocol, string BaseUrl, string AuthScheme,
        string? ApiKeyProtected, bool Enabled, IReadOnlyList<CachedModel> Models);

    protected sealed record CachedModel(string Name, bool Enabled, bool IsDefault);
}
