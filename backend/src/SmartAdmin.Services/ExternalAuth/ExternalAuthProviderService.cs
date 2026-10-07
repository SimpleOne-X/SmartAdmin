using System.Text.RegularExpressions;
using SmartAdmin.Core;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Services;

/// <summary>
/// <see cref="IExternalAuthProviderService"/> 默认实现。
/// <para>规则集中在 <see cref="ResolveFields"/>:必填校验、机密留空不改、改了决定请求去向的字段(<see cref="ExternalAuthField.DefinesEndpoint"/>)
/// 时机密必须重输。保存与连接测试共用它,所以「测试能通过」与「保存能通过」是同一组条件。</para>
/// <para>机密整体序列化成一个 JSON,用 <see cref="ISecretProtector"/> 封成信封入库;明文只在本次调用的内存里经过,不写日志。</para>
/// </summary>
public class ExternalAuthProviderService(
    IRepository<SysExternalAuthProvider> rows,
    IEnumerable<IExternalAuthProviderType> types,
    IEnumerable<IExternalAuthProvider> codeProviders,
    IDataProtectionKeyProvider keyProvider,
    ISecretProtector secretProtector,
    ICacheProvider cache,
    AdminAiOptions aiOptions,
    IServiceProvider services) : IExternalAuthProviderService
{
    /// <summary>官方厂商类型的 Code 就是类型名,这几个名字即使对应的包没装也不能被 OIDC 条目占用。</summary>
    private static readonly string[] OFFICIAL_CODES = ["wecom", "dingtalk", "github", "wechat"];

    private static readonly Regex CODE_PATTERN = new("^[a-z][a-z0-9-]{1,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>连接测试的整体时限:任何一次厂商调用都不该让管理页无限等待。</summary>
    private static readonly TimeSpan TEST_TIMEOUT = TimeSpan.FromSeconds(20);

    /// <summary>本次调用里各字段的最终取值(机密为明文,只在内存里)。</summary>
    protected sealed record ResolvedFields(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyDictionary<string, string> Secrets);

    // ── 目录 ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task<ExternalAuthCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var typeViews = types.Select(ToTypeView).ToList();
        var dbRows = await rows.AsQueryable().OrderBy(r => r.Id).ToListAsync(cancellationToken);

        var providers = new List<ExternalAuthProviderView>();
        var codeCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in codeProviders)
        {
            if (!codeCodes.Add(p.Code)) continue;
            providers.Add(new ExternalAuthProviderView(
                p.Code, "", p.DisplayName, p.Icon, ExternalAuthProviderSource.Code, Installed: true, Configured: true,
                new Dictionary<string, string>(), new Dictionary<string, ExternalAuthSecretState>()));
        }
        // 同 Code 时代码注册的优先(与注册表一致),库里那一行被遮蔽,不在目录里重复出现
        foreach (var row in dbRows.Where(r => !codeCodes.Contains(r.Code)))
            providers.Add(ToProviderView(row));

        return new ExternalAuthCatalog(keyProvider.IsEphemeral, typeViews, providers);
    }

    // ── 保存 ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task SaveAsync(string code, ExternalAuthProviderSaveInput input, CancellationToken cancellationToken = default)
    {
        var type = RequireType(input.Type);
        ValidateCode(code, type);
        EnsureCodeNotTakenByCodeProvider(code);
        // 主密钥是进程内临时密钥时,存进去的机密重启后就永远解不开,直接拒绝
        AdminException.ThrowIf(keyProvider.IsEphemeral, ErrorCode.ExternalAuthDataProtectionKeyMissing);

        var existing = await rows.GetFirstAsync(r => r.Code == code);
        AdminException.ThrowIf(existing is not null && existing.Type != type.Type, ErrorCode.ExternalAuthCodeExists,
            new Dictionary<string, object?> { ["code"] = code });

        var resolved = ResolveFields(type, input.Values, input.Secrets, existing);
        EnforceEndpointFence(type, resolved.Values);

        var entity = existing ?? new SysExternalAuthProvider { Code = code, Type = type.Type };
        Apply(entity, input, resolved);
        if (existing is null) await rows.InsertAsync(entity);
        else await rows.UpdateAsync(entity);

        await InvalidateAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task DeleteAsync(string code, CancellationToken cancellationToken = default)
    {
        var existing = await rows.GetFirstAsync(r => r.Code == code);
        AdminException.ThrowIf(existing is null, ErrorCode.ExternalAuthProviderNotFound,
            new Dictionary<string, object?> { ["code"] = code });

        // 软删:仓储会给唯一列追加 _del_{id} 后缀,同 Code 之后可以重新添加。sys_user_external 的绑定行不动。
        await rows.DeleteAsync(existing!.Id);
        await InvalidateAsync(cancellationToken);
    }

    // ── 连接测试 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task<ExternalAuthTestView> TestAsync(ExternalAuthProviderTestInput input, CancellationToken cancellationToken = default)
    {
        var type = RequireType(input.Type);

        SysExternalAuthProvider? existing = null;
        if (!string.IsNullOrEmpty(input.Code))
        {
            existing = await rows.GetFirstAsync(r => r.Code == input.Code);
            if (existing is not null && existing.Type != type.Type) existing = null;   // 换了类型就不能沿用已存机密
        }

        var resolved = ResolveFields(type, input.Values, input.Secrets, existing);
        EnforceEndpointFence(type, resolved.Values);

        var merged = new Dictionary<string, string>(resolved.Values);
        foreach (var (name, value) in resolved.Secrets) merged[name] = value;
        var config = new ExternalAuthProviderConfig(
            string.IsNullOrEmpty(input.Code) ? type.Type : input.Code, type.DefaultDisplayName, type.DefaultIcon, merged);

        var result = await RunTestAsync(type, config, cancellationToken);
        return ToTestView(result, resolved.Secrets.Values);
    }

    /// <summary>
    /// 带时限地调用类型的 <c>TestAsync</c>。契约要求类型把网络与解析异常折成失败项,这里再兜一层:
    /// 超时或类型自己抛出的异常也只变成一个失败项,不让测试请求变成 500。
    /// </summary>
    protected virtual async Task<ExternalAuthTestResult> RunTestAsync(
        IExternalAuthProviderType type, ExternalAuthProviderConfig config, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TEST_TIMEOUT);
        try
        {
            return await type.TestAsync(config, services, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ExternalAuthTestResult([new ExternalAuthCheck("timeout", ExternalAuthCheckStatus.Failed)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExternalAuthTestResult([new ExternalAuthCheck("error", ExternalAuthCheckStatus.Failed, ex.Message)]);
        }
    }

    // ── 校验与取值 ───────────────────────────────────────────────────

    /// <summary>按类型码取已安装的类型;未知或没装对应包抛 <see cref="ErrorCode.ExternalAuthTypeNotFound"/>。</summary>
    protected virtual IExternalAuthProviderType RequireType(string? typeName)
    {
        var type = types.FirstOrDefault(t => string.Equals(t.Type, typeName, StringComparison.Ordinal));
        AdminException.ThrowIf(type is null, ErrorCode.ExternalAuthTypeNotFound,
            new Dictionary<string, object?> { ["type"] = typeName });
        return type!;
    }

    /// <summary>
    /// Code 规则:只许一份的类型(官方厂商包)Code 就是类型名;允许多条的类型(OIDC)由管理员起名,
    /// 格式 <c>^[a-z][a-z0-9-]{1,31}$</c>,且不能占用官方类型的保留码。
    /// </summary>
    protected virtual void ValidateCode(string code, IExternalAuthProviderType type)
    {
        var ok = type.AllowMultiple
            ? CODE_PATTERN.IsMatch(code) && !IsReservedCode(code)
            : string.Equals(code, type.Type, StringComparison.Ordinal);
        AdminException.ThrowIf(!ok, ErrorCode.ExternalAuthCodeInvalid, new Dictionary<string, object?> { ["code"] = code });
    }

    /// <summary>保留码:官方厂商名,加上任何已安装的「只许一份」类型的类型名。</summary>
    protected virtual bool IsReservedCode(string code) =>
        OFFICIAL_CODES.Contains(code, StringComparer.Ordinal)
        || types.Any(t => !t.AllowMultiple && string.Equals(t.Type, code, StringComparison.Ordinal));

    /// <summary>Code 已被消费者用代码注册的 provider 占用时拒绝:同 Code 时代码注册的优先,库里的配置永远不会生效。</summary>
    protected virtual void EnsureCodeNotTakenByCodeProvider(string code) =>
        AdminException.ThrowIf(codeProviders.Any(p => string.Equals(p.Code, code, StringComparison.Ordinal)),
            ErrorCode.ExternalAuthCodeExists, new Dictionary<string, object?> { ["code"] = code });

    /// <summary>
    /// 合并入参与已存配置,得到各字段的最终取值:
    /// <list type="bullet">
    /// <item>非机密字段:空值取类型缺省值;必填仍空抛 <see cref="ErrorCode.ExternalAuthFieldMissing"/>(args.field)。</item>
    /// <item>机密字段:入参非空用入参;留空则沿用已存值,首次保存(无已存值)必填。</item>
    /// <item>决定请求去向的字段变了:已存机密不会被沿用,必须重新输入,否则有权限的人能把地址指到自己的服务器,
    /// 登录时服务端会带着已保存的密钥过去。</item>
    /// </list>
    /// 入参里不属于该类型字段清单的键一律忽略;机密只认 <c>Secrets</c>、非机密只认 <c>Values</c>,不会串位。
    /// </summary>
    protected virtual ResolvedFields ResolveFields(
        IExternalAuthProviderType type,
        IReadOnlyDictionary<string, string?>? inputValues,
        IReadOnlyDictionary<string, string?>? inputSecrets,
        SysExternalAuthProvider? existing)
    {
        var storedValues = existing is null
            ? new Dictionary<string, string>()
            : ExternalAuthProviderRows.ReadMap(existing.SettingsJson);
        // 解不开(主密钥换过)等同于没有已存机密,走「必填」路径让管理员重输
        var storedSecrets = existing is null
            ? new Dictionary<string, string>()
            : ExternalAuthProviderRows.TryDecryptSecrets(secretProtector, existing.SecretsProtected)
              ?? new Dictionary<string, string>();

        var values = new Dictionary<string, string>();
        foreach (var field in type.Fields.Where(f => !f.Secret))
        {
            var value = Pick(inputValues, field.Name) ?? field.Default ?? "";
            if (value.Length == 0 && field.Required) throw MissingField(field.Name);
            if (value.Length > 0) values[field.Name] = value;
        }

        var endpointChanged = existing is not null && type.Fields.Any(f =>
            f.DefinesEndpoint && !f.Secret &&
            !string.Equals(Normalize(values.GetValueOrDefault(f.Name)), Normalize(storedValues.GetValueOrDefault(f.Name)), StringComparison.Ordinal));

        var secrets = new Dictionary<string, string>();
        foreach (var field in type.Fields.Where(f => f.Secret))
        {
            var value = Pick(inputSecrets, field.Name);
            if (value is null && !endpointChanged && storedSecrets.TryGetValue(field.Name, out var kept)) value = kept;
            if (value is null && field.Required) throw MissingField(field.Name);
            if (value is not null) secrets[field.Name] = value;
        }

        return new ResolvedFields(values, secrets);
    }

    /// <summary>OIDC 的 Authority 这类决定请求去向的字段,保存与测试前都过出站围栏(复用 <c>AdminAiOptions.Http</c> 那份围栏配置)。</summary>
    protected virtual void EnforceEndpointFence(IExternalAuthProviderType type, IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in type.Fields.Where(f => f.DefinesEndpoint && !f.Secret))
            if (values.TryGetValue(field.Name, out var url))
                HttpFence.ValidateUrl(url, aiOptions.Http, ErrorCode.ExternalAuthEndpointBlocked);
    }

    /// <summary>把最终取值写进实体:非机密明文 JSON、机密整体加密、尾四位提示。</summary>
    protected virtual void Apply(SysExternalAuthProvider entity, ExternalAuthProviderSaveInput input, ResolvedFields resolved)
    {
        entity.DisplayName = Blank(input.DisplayName);
        entity.Icon = Blank(input.Icon);
        entity.SettingsJson = ExternalAuthProviderRows.WriteMap(resolved.Values);

        var hints = resolved.Secrets.ToDictionary(kv => kv.Key, kv => ExternalAuthProviderRows.Hint(kv.Value));
        entity.SecretsProtected = resolved.Secrets.Count == 0
            ? null
            : secretProtector.Protect(ExternalAuthProviderRows.WriteMap(resolved.Secrets)!);
        entity.SecretHints = ExternalAuthProviderRows.WriteMap(hints);
    }

    /// <summary>失效注册表的整表缓存;多实例走共享缓存时全集群同时生效。</summary>
    protected virtual Task InvalidateAsync(CancellationToken cancellationToken) =>
        cache.RemoveAsync(CacheKeys.ExternalAuthProviders(), cancellationToken);

    // ── 视图 ─────────────────────────────────────────────────────────

    private static ExternalAuthTypeView ToTypeView(IExternalAuthProviderType t) => new(
        t.Type, t.DefaultDisplayName, t.DefaultIcon, t.AllowMultiple,
        [.. t.Fields.Select(f => new ExternalAuthFieldView(f.Name, f.Secret, f.Required, f.Default, f.DefinesEndpoint))]);

    /// <summary>库里的行 → 视图。类型没装时仍列出(管理员能看到并清除),只是不可用。</summary>
    protected virtual ExternalAuthProviderView ToProviderView(SysExternalAuthProvider row)
    {
        var type = types.FirstOrDefault(t => string.Equals(t.Type, row.Type, StringComparison.Ordinal));
        var settings = ExternalAuthProviderRows.ReadMap(row.SettingsJson);
        var hints = ExternalAuthProviderRows.ReadMap(row.SecretHints);

        var secretNames = type is null ? hints.Keys : type.Fields.Where(f => f.Secret).Select(f => f.Name);
        var secrets = secretNames.ToDictionary(
            name => name,
            name => new ExternalAuthSecretState(
                hints.ContainsKey(name),
                hints.TryGetValue(name, out var hint) && hint.Length > 0 ? hint : null));

        var configured = type is not null
            && ExternalAuthProviderRows.IsComplete(type, settings, [.. hints.Keys]);
        return new ExternalAuthProviderView(
            row.Code, row.Type,
            Blank(row.DisplayName) ?? type?.DefaultDisplayName ?? row.Code,
            Blank(row.Icon) ?? type?.DefaultIcon,
            ExternalAuthProviderSource.Db, Installed: type is not null, configured, settings, secrets);
    }

    /// <summary>
    /// 结果转出参。厂商返回的细节里若原样带出了本次用到的机密,替换成 <c>***</c>:
    /// 类型实现有疏漏也不至于让机密从测试接口漏出去。
    /// </summary>
    protected virtual ExternalAuthTestView ToTestView(ExternalAuthTestResult result, IEnumerable<string> secretValues)
    {
        var secrets = secretValues.Where(s => s.Length > 0).ToList();
        var checks = result.Checks.Select(c => new ExternalAuthCheckView(
            c.Key, StatusText(c.Status), c.Detail is null ? null : secrets.Aggregate(c.Detail, (d, s) => d.Replace(s, "***")))).ToList();
        return new ExternalAuthTestView(result.Ok, checks);
    }

    private static string StatusText(ExternalAuthCheckStatus status) => status switch
    {
        ExternalAuthCheckStatus.Ok => "ok",
        ExternalAuthCheckStatus.Failed => "failed",
        _ => "skipped",
    };

    // ── 小工具 ───────────────────────────────────────────────────────

    /// <summary>取入参里某字段的值:缺键或空白返回 <c>null</c>,否则去首尾空白(粘贴时带进来的换行最常见)。</summary>
    private static string? Pick(IReadOnlyDictionary<string, string?>? map, string name)
    {
        if (map is null || !map.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v)) return null;
        return v.Trim();
    }

    /// <summary>比较「决定请求去向」的字段时忽略末尾斜杠,免得 <c>https://a/</c> 与 <c>https://a</c> 被当成改了地址。</summary>
    private static string Normalize(string? v) => (v ?? "").Trim().TrimEnd('/');

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static AdminException MissingField(string field) =>
        new(ErrorCode.ExternalAuthFieldMissing, new Dictionary<string, object?> { ["field"] = field });
}
