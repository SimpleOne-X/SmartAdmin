using System.Text.Json;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 注册表缓存里的 provider 配置行:只带运行时需要的列,机密仍是加密信封。
/// 用独立的记录而不是实体,是为了缓存序列化只涉及这几个简单属性。
/// </summary>
public sealed record ExternalAuthProviderRow(
    long Id,
    string Code,
    string Type,
    string? DisplayName,
    string? Icon,
    string? SettingsJson,
    string? SecretsProtected,
    string? SecretHints,
    DateTime? UpdateTime)
{
    /// <summary>从实体取运行时需要的列。</summary>
    public static ExternalAuthProviderRow From(SysExternalAuthProvider e) => new(
        e.Id, e.Code, e.Type, e.DisplayName, e.Icon, e.SettingsJson, e.SecretsProtected, e.SecretHints, e.UpdateTime);
}

/// <summary>配置行的 JSON 列读写与「是否配置完整」判定,注册表与管理服务共用同一份规则。</summary>
public static class ExternalAuthProviderRows
{
    /// <summary>机密至少多长才给尾四位提示;更短的密钥给了尾四位就等于露出了大半。</summary>
    public const int HintMinSecretLength = 8;

    /// <summary>读一个「字段名 → 字符串」的 JSON 列;空或畸形返回空字典。</summary>
    public static IReadOnlyDictionary<string, string> ReadMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>把字典写成 JSON 列;空字典返回 <c>null</c>。</summary>
    public static string? WriteMap(IReadOnlyDictionary<string, string> map) =>
        map.Count == 0 ? null : JsonSerializer.Serialize(map);

    /// <summary>
    /// 解开机密信封。信封为空返回空字典;主密钥不对或信封被篡改时返回 <c>null</c>,
    /// 调用方据此把这行当作「机密不可用」,而不是让一次解密异常拖垮整个登录页。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryDecryptSecrets(ISecretProtector protector, string? envelope)
    {
        if (string.IsNullOrEmpty(envelope)) return new Dictionary<string, string>();
        try
        {
            return ReadMap(protector.Unprotect(envelope));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>尾四位提示;机密不足 <see cref="HintMinSecretLength"/> 位返回空串。</summary>
    public static string Hint(string secret) =>
        secret.Length >= HintMinSecretLength ? secret[^4..] : "";

    /// <summary>
    /// 必填的非机密字段都有值、必填的机密字段都已配置。<paramref name="configuredSecrets"/> 是已配置机密的字段名。
    /// </summary>
    public static bool IsComplete(
        IExternalAuthProviderType type,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyCollection<string> configuredSecrets) =>
        type.Fields.Where(f => f.Required).All(f => f.Secret
            ? configuredSecrets.Contains(f.Name)
            : settings.TryGetValue(f.Name, out var v) && !string.IsNullOrWhiteSpace(v));
}
