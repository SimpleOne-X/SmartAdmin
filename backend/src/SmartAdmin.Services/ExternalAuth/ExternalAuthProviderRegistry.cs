using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;
using SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IExternalAuthProvider"/> 实例的进程级持有器(单例)。
/// <para>实例由类型描述按库里的配置现建,建一次、复用到那一行变化为止:企业微信的 access_token、
/// OIDC 的发现文档与 JWKS 都缓存在实例里,每个请求重建就等于每次登录都回源。
/// 持有器用根 <see cref="IServiceProvider"/> 造实例,不捕获任何 scoped 服务。</para>
/// <para>「行变化」按整行内容判定(含每次保存都会变的加密信封),而不是只看 <c>UpdateTime</c>:
/// 部分数据库的时间精度是秒,同一秒内连续保存两次会让时间戳相同。</para>
/// </summary>
public class ExternalAuthProviderInstances(IServiceProvider rootServices)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary><see cref="Provider"/> 为 <c>null</c> 也缓存:配置不完整的行不必每个请求都重新解密、重复记警告。</summary>
    private sealed record Entry(ExternalAuthProviderRow Row, IExternalAuthProvider? Provider);

    /// <summary>造实例用的根服务容器,传给 <see cref="IExternalAuthProviderType.Create"/>。</summary>
    public IServiceProvider Services => rootServices;

    /// <summary>行没变就复用已建的实例,否则用 <paramref name="create"/> 重建。<paramref name="create"/> 返回 <c>null</c> 表示这行不出实例。</summary>
    public IExternalAuthProvider? GetOrCreate(ExternalAuthProviderRow row, Func<IExternalAuthProvider?> create)
    {
        if (_entries.TryGetValue(row.Code, out var hit) && hit.Row == row) return hit.Provider;

        var created = create();
        _entries[row.Code] = new Entry(row, created);
        return created;
    }

    /// <summary>丢弃已不在 <paramref name="liveCodes"/> 里的实例(配置被清除,或类型被卸载)。</summary>
    public void Retain(IReadOnlySet<string> liveCodes)
    {
        foreach (var code in _entries.Keys)
            if (!liveCodes.Contains(code)) _entries.TryRemove(code, out _);
    }
}

/// <summary>
/// <see cref="IExternalAuthProviderRegistry"/> 默认实现:消费者用代码注册的 provider ∪ 库里按类型现建的实例,
/// 同 Code 时代码注册的优先。
/// <para>库里的行整表读穿透缓存(键 <see cref="CacheKeys.ExternalAuthProviders"/>),
/// <see cref="ExternalAuthProviderService"/> 的写路径整体失效;多实例走共享缓存时全集群同时生效。
/// 缓存里只有加密信封,解密发生在建实例那一刻。</para>
/// <para>配置不完整(缺必填项或缺机密)、类型没装、机密解不开的行不出实例,<see cref="FindAsync"/> 返回 <c>null</c>。</para>
/// </summary>
public class ExternalAuthProviderRegistry(
    IEnumerable<IExternalAuthProvider> codeProviders,
    IEnumerable<IExternalAuthProviderType> types,
    ExternalAuthProviderInstances instances,
    ISqlSugarClient db,
    ICacheProvider cache,
    ISecretProtector secretProtector,
    AdminCacheOptions cacheOptions,
    ILogger<ExternalAuthProviderRegistry> logger) : IExternalAuthProviderRegistry
{
    /// <inheritdoc />
    public virtual async Task<IExternalAuthProvider?> FindAsync(string code, CancellationToken cancellationToken = default)
    {
        var fromCode = codeProviders.FirstOrDefault(p => p.Code == code);
        if (fromCode is not null) return fromCode;

        var row = (await GetRowsAsync(cancellationToken)).FirstOrDefault(r => r.Code == code);
        return row is null ? null : BuildProvider(row);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<IExternalAuthProvider>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<IExternalAuthProvider>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in codeProviders)
            if (taken.Add(p.Code)) result.Add(p);

        var dbRows = await GetRowsAsync(cancellationToken);
        instances.Retain(dbRows.Select(r => r.Code).ToHashSet(StringComparer.Ordinal));
        foreach (var row in dbRows.Where(r => !taken.Contains(r.Code)))
            if (BuildProvider(row) is { } provider) result.Add(provider);
        return result;
    }

    /// <summary>库里的配置行,整表读穿透缓存。</summary>
    protected virtual async Task<IReadOnlyList<ExternalAuthProviderRow>> GetRowsAsync(CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.ExternalAuthProviders();
        var cached = await cache.GetAsync<List<ExternalAuthProviderRow>>(cacheKey, cancellationToken);
        if (cached is not null) return cached;

        var entities = await db.Queryable<SysExternalAuthProvider>().OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var list = entities.Select(ExternalAuthProviderRow.From).ToList();

        var ttl = cacheOptions.PermissionMinutes > 0 ? TimeSpan.FromMinutes(cacheOptions.PermissionMinutes) : (TimeSpan?)null;
        await cache.SetAsync(cacheKey, list, ttl, cancellationToken);
        return list;
    }

    /// <summary>取这一行对应的实例:行没变复用,变了重建;配置不完整、类型没装或机密解不开返回 <c>null</c>。</summary>
    protected virtual IExternalAuthProvider? BuildProvider(ExternalAuthProviderRow row) =>
        instances.GetOrCreate(row, () => CreateProvider(row));

    /// <summary>解密并造实例。任何一步失败都只记一条警告并视为「没配置」,不让一行坏配置拖垮登录页。</summary>
    protected virtual IExternalAuthProvider? CreateProvider(ExternalAuthProviderRow row)
    {
        var type = types.FirstOrDefault(t => string.Equals(t.Type, row.Type, StringComparison.Ordinal));
        if (type is null)
        {
            logger.LogWarning("外部登录 provider {Code} 的类型 {Type} 未安装,不生成实例", row.Code, row.Type);
            return null;
        }

        var settings = ExternalAuthProviderRows.ReadMap(row.SettingsJson);
        var secrets = ExternalAuthProviderRows.TryDecryptSecrets(secretProtector, row.SecretsProtected);
        if (secrets is null)
        {
            logger.LogWarning("外部登录 provider {Code} 的机密无法解密(数据保护主密钥与保存时不一致?),不生成实例", row.Code);
            return null;
        }
        if (!ExternalAuthProviderRows.IsComplete(type, settings, [.. secrets.Keys]))
            return null;

        var values = new Dictionary<string, string>(settings);
        foreach (var (name, value) in secrets) values[name] = value;
        var config = new ExternalAuthProviderConfig(
            row.Code,
            string.IsNullOrWhiteSpace(row.DisplayName) ? type.DefaultDisplayName : row.DisplayName,
            string.IsNullOrWhiteSpace(row.Icon) ? type.DefaultIcon : row.Icon,
            values);
        try
        {
            return type.Create(config, instances.Services);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "外部登录 provider {Code} 创建失败,不生成实例", row.Code);
            return null;
        }
    }
}
