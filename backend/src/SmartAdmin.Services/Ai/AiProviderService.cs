using SqlSugar;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IAiProviderService"/> 默认实现。
/// <para>
/// <see cref="SysAiModel.IsDefault"/> 是<b>全库唯一</b>(见该字段文档——<see cref="Core.IAiChatClient"/> 在调用方
/// 未指定厂商/模型时,按 provider 遍历顺序取第一个 IsDefault 的模型,顺序本身并不保证稳定,唯一性因此必须由写路径
/// 维护):<see cref="SyncModelsAsync"/> 在同一次提交里只认第一个 <c>IsDefault=true</c>(其余原样降为 false,
/// 方法文档另有说明),写库后再清掉全库其它厂商模型上残留的 <c>true</c>。
/// </para>
/// </summary>
public class AiProviderService(
    IRepository<SysAiProvider> providers,
    IRepository<SysAiModel> models,
    IDataProtectionKeyProvider keyProvider,
    ISecretProtector secretProtector,
    ICacheProvider cache,
    IEventBus events,
    IAiChatClient chatClient,
    AdminAiOptions aiOptions) : IAiProviderService
{
    /// <inheritdoc />
    public virtual async Task<PagedList<AiProviderView>> PageAsync(AiProviderPageInput input)
    {
        var query = providers.AsQueryable()
            .WhereIF(!string.IsNullOrEmpty(input.Keyword), p => p.Code.Contains(input.Keyword!) || p.Name.Contains(input.Keyword!))
            .WhereIF(input.Enabled.HasValue, p => p.Enabled == input.Enabled!.Value);

        var paged = await query.OrderBy(p => p.Sort).ToPagedListAsync(input.Current, input.Size);
        var providerIds = paged.Items.Select(p => p.Id).ToList();
        var modelRows = providerIds.Count == 0
            ? []
            : await models.AsQueryable()
                .Where(m => providerIds.Contains(m.ProviderId))
                .ToListAsync();

        return new PagedList<AiProviderView>
        {
            Current = paged.Current,
            Size = paged.Size,
            Total = paged.Total,
            Items = paged.Items.Select(p => ToView(p, modelRows.Where(m => m.ProviderId == p.Id).OrderBy(m => m.Id).ToList())).ToList(),
        };
    }

    /// <inheritdoc />
    public virtual async Task<AiProviderView> GetAsync(long id)
    {
        var provider = await providers.GetRequiredAsync(id, ErrorCode.AiProviderNotFound);
        var modelRows = await models.AsQueryable().Where(m => m.ProviderId == id).OrderBy(m => m.Id).ToListAsync();
        return ToView(provider, modelRows);
    }

    /// <inheritdoc />
    public virtual async Task<long> AddAsync(AiProviderAddInput input)
    {
        var preset = AiProviderPresets.Find(input.Preset);
        AdminException.ThrowIf(preset is null, ErrorCode.AiPresetNotFound);

        await EnsureCodeAvailableAsync(input.Code, excludingId: null);
        EnsureValidBaseUrl(input.BaseUrl);

        var entity = new SysAiProvider
        {
            Code = input.Code,
            Name = input.Name,
            Preset = preset!.Code,
            Protocol = preset.Protocol,   // 预置带出,不接受入参覆盖
            BaseUrl = input.BaseUrl,
            AuthScheme = input.AuthScheme,
            Sort = input.Sort,
            Remark = input.Remark,
        };
        ApplyApiKey(entity, input.ApiKey);
        await providers.InsertAsync(entity);

        await SyncModelsAsync(entity.Id, input.Models);
        await InvalidateAndNotifyAsync(entity.Id);
        return entity.Id;
    }

    /// <inheritdoc />
    public virtual async Task UpdateAsync(long id, AiProviderUpdateInput input)
    {
        var provider = await providers.GetRequiredAsync(id, ErrorCode.AiProviderNotFound);

        if (!string.Equals(provider.Code, input.Code, StringComparison.Ordinal))
            await EnsureCodeAvailableAsync(input.Code, excludingId: id);
        EnsureValidBaseUrl(input.BaseUrl);

        provider.Code = input.Code;
        provider.Name = input.Name;
        provider.BaseUrl = input.BaseUrl;
        provider.AuthScheme = input.AuthScheme;
        provider.Sort = input.Sort;
        provider.Remark = input.Remark;
        ApplyApiKey(provider, input.ApiKey);
        await providers.UpdateAsync(provider);

        await SyncModelsAsync(id, input.Models);
        await InvalidateAndNotifyAsync(id);
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(long id)
    {
        await providers.GetRequiredAsync(id, ErrorCode.AiProviderNotFound);

        var modelRows = await models.AsQueryable().Where(m => m.ProviderId == id).ToListAsync();
        foreach (var model in modelRows)
            await models.DeleteAsync(model.Id);
        await providers.DeleteAsync(id);

        await InvalidateAndNotifyAsync(id);
    }

    /// <inheritdoc />
    public virtual async Task SetEnabledAsync(long id, bool enabled)
    {
        var provider = await providers.GetRequiredAsync(id, ErrorCode.AiProviderNotFound);
        if (enabled)
            AdminException.ThrowIf(
                !string.Equals(provider.AuthScheme, "none", StringComparison.Ordinal) && string.IsNullOrEmpty(provider.ApiKeyProtected),
                ErrorCode.AiApiKeyMissing);

        provider.Enabled = enabled;
        await providers.UpdateAsync(provider);
        await InvalidateAndNotifyAsync(id);
    }

    /// <inheritdoc />
    public virtual async Task<AiTestResult> TestAsync(long id)
    {
        var provider = await providers.GetRequiredAsync(id, ErrorCode.AiProviderNotFound);
        var modelRows = await models.AsQueryable().Where(m => m.ProviderId == id).ToListAsync();
        var model = modelRows.FirstOrDefault(m => m.IsDefault && m.Enabled) ?? modelRows.FirstOrDefault(m => m.Enabled);
        if (model is null)
            return new AiTestResult(false, null, 0, null, "该厂商没有可用模型");

        try
        {
            var response = await chatClient.ChatAsync(new AiChatRequest
            {
                Scene = "system.test",
                ProviderCode = provider.Code,
                Model = model.Name,
                MaxTokens = 8,
                Messages = [AiChatMessage.User("ping")],
            });
            return new AiTestResult(true, response.Model, response.LatencyMs, response.Usage, null);
        }
        catch (AdminException ex)
        {
            return new AiTestResult(false, model.Name, 0, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<AiProviderPreset>> PresetsAsync() => Task.FromResult(AiProviderPresets.All);

    // ── 内部辅助 ─────────────────────────────────────────────────────

    private static AiProviderView ToView(SysAiProvider provider, IReadOnlyList<SysAiModel> modelRows) => new(
        provider.Id, provider.Code, provider.Name, provider.Preset, provider.Protocol, provider.BaseUrl,
        provider.AuthScheme, provider.ApiKeyHint, !string.IsNullOrEmpty(provider.ApiKeyProtected), provider.Enabled, provider.Sort,
        provider.Remark,
        modelRows.Select(m => new AiModelView(
            m.Id, m.Name, m.DisplayName, m.Enabled, m.IsDefault, m.ContextWindow, m.InputPrice, m.OutputPrice)).ToList());

    /// <summary>Code 唯一性校验,纳入软删行(唯一索引覆盖已软删行,漏检会撞库唯一约束抛原生 500)。</summary>
    protected virtual async Task EnsureCodeAvailableAsync(string code, long? excludingId)
    {
        var exists = await providers.AsQueryable().ClearFilter<ISoftDelete>()
            .WhereIF(excludingId.HasValue, p => p.Id != excludingId!.Value)
            .AnyAsync(p => p.Code == code);
        AdminException.ThrowIf(exists, ErrorCode.AiProviderCodeExists);
    }

    /// <summary>同厂商内模型名唯一性校验,纳入软删行(见 <see cref="SysAiModel.Name"/> 文档)。</summary>
    protected virtual async Task EnsureModelNameAvailableAsync(long providerId, string name, long? excludingId)
    {
        var exists = await models.AsQueryable().ClearFilter<ISoftDelete>()
            .Where(m => m.ProviderId == providerId && m.Name == name)
            .WhereIF(excludingId.HasValue, m => m.Id != excludingId!.Value)
            .AnyAsync();
        AdminException.ThrowIf(exists, ErrorCode.AiModelNameExists);
    }

    /// <summary>Base URL 必须是合法的 http(s) 绝对地址,且不命中 SSRF 围栏(§7.1:保存与测试都先过 HttpFence.ValidateUrl)。
    /// 实际出站调用还会在 <see cref="AiHttpClient"/> 的 ConnectCallback 里对解析后的 IP 再复检一次(防 DNS rebinding),
    /// 这里的校验是保存时的快速拒绝,不是唯一防线。</summary>
    protected virtual void EnsureValidBaseUrl(string baseUrl) =>
        HttpFence.ValidateUrl(baseUrl, aiOptions.Http, ErrorCode.AiBaseUrlBlocked);

    /// <summary>
    /// Key 规则:<paramref name="apiKey"/> 为 null/空串 → 不改动;非空 → 先查数据保护密钥是否为进程内临时密钥
    /// (是则拒绝保存,抛 <see cref="ErrorCode.AiDataProtectionKeyMissing"/>),再加密写入 + 算出尾四位提示。
    /// 明文只在本次调用经过内存,不写日志。
    /// </summary>
    protected virtual void ApplyApiKey(SysAiProvider entity, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return;
        AdminException.ThrowIf(keyProvider.IsEphemeral, ErrorCode.AiDataProtectionKeyMissing);
        entity.ApiKeyProtected = secretProtector.Protect(apiKey);
        entity.ApiKeyHint = apiKey[^Math.Min(4, apiKey.Length)..];
    }

    /// <summary>
    /// 按入参整体同步某厂商的模型行:Id 为空新增,Id 存在则更新,库中存在但入参未提及的行软删除。
    /// 同一批入参里出现多个 <c>IsDefault=true</c> 时只保留第一个,其余在写库前就地降为 false;
    /// 写库后如最终确有一行为 true,顺带清空全库其它厂商模型上残留的 true(<see cref="SysAiModel.IsDefault"/> 全库唯一)。
    /// </summary>
    protected virtual async Task SyncModelsAsync(long providerId, IReadOnlyList<AiModelInput> inputModels)
    {
        // 组内去重:同一次提交里多个 IsDefault=true 只认第一个,其余原样降为 false
        var seenDefault = false;
        var normalized = new List<AiModelInput>(inputModels.Count);
        foreach (var m in inputModels)
        {
            if (m.IsDefault && seenDefault)
                normalized.Add(m with { IsDefault = false });
            else
            {
                if (m.IsDefault) seenDefault = true;
                normalized.Add(m);
            }
        }

        // 同一批入参内名称不能重复(与"含软删行"的库内唯一性校验分开处理)
        var duplicateNames = normalized.Select(m => m.Name).GroupBy(n => n, StringComparer.Ordinal).Any(g => g.Count() > 1);
        AdminException.ThrowIf(duplicateNames, ErrorCode.AiModelNameExists);

        var existingRows = await models.AsQueryable().Where(m => m.ProviderId == providerId).ToListAsync();
        var existingById = existingRows.ToDictionary(m => m.Id);
        var keepIds = new HashSet<long>();
        long? winnerId = null;

        foreach (var input in normalized)
        {
            if (input.Id is { } existingId)
            {
                AdminException.ThrowIf(!existingById.TryGetValue(existingId, out var row), ErrorCode.AiModelNotFound);
                await EnsureModelNameAvailableAsync(providerId, input.Name, excludingId: existingId);

                row!.Name = input.Name;
                row.DisplayName = input.DisplayName;
                row.Enabled = input.Enabled;
                row.IsDefault = input.IsDefault;
                row.ContextWindow = input.ContextWindow;
                row.InputPrice = input.InputPrice;
                row.OutputPrice = input.OutputPrice;
                await models.UpdateAsync(row);

                keepIds.Add(existingId);
                if (input.IsDefault) winnerId = existingId;
            }
            else
            {
                await EnsureModelNameAvailableAsync(providerId, input.Name, excludingId: null);

                var row = new SysAiModel
                {
                    ProviderId = providerId,
                    Name = input.Name,
                    DisplayName = input.DisplayName,
                    Enabled = input.Enabled,
                    IsDefault = input.IsDefault,
                    ContextWindow = input.ContextWindow,
                    InputPrice = input.InputPrice,
                    OutputPrice = input.OutputPrice,
                };
                await models.InsertAsync(row);

                keepIds.Add(row.Id);
                if (input.IsDefault) winnerId = row.Id;
            }
        }

        // 库中存在但入参未提及的行 → 软删
        foreach (var row in existingRows)
            if (!keepIds.Contains(row.Id))
                await models.DeleteAsync(row.Id);

        if (winnerId is { } id)
            await ClearOtherDefaultsAsync(id);
    }

    /// <summary>把除 <paramref name="winnerId"/> 外所有仍标记 IsDefault=true 的模型行(跨全部厂商)清成 false。</summary>
    protected virtual async Task ClearOtherDefaultsAsync(long winnerId)
    {
        var others = await models.AsQueryable().Where(m => m.IsDefault && m.Id != winnerId).ToListAsync();
        foreach (var other in others)
        {
            other.IsDefault = false;
            await models.UpdateAsync(other);
        }
    }

    /// <summary>失效 AiChatClient 的整表读穿透缓存并广播变更事件;Add/Update/Delete/SetEnabled 成功后都要调。</summary>
    protected virtual async Task InvalidateAndNotifyAsync(long providerId)
    {
        await cache.RemoveAsync(CacheKeys.AiProviders());
        await events.PublishAsync(new AiProviderChangedEvent(providerId));
    }
}
