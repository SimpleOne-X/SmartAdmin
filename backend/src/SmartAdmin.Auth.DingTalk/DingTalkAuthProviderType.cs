using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.DingTalk;

/// <summary>
/// 钉钉的「类型描述」:字段清单、按配置造 <see cref="DingTalkExternalAuthProvider"/>、连接测试。
/// 连接配置由管理员在「系统配置 → 登录方式」填写,<c>appSecret</c> 加密入库;钉钉只能配置一份,Code 固定为 <see cref="TypeName"/>。
/// </summary>
public class DingTalkAuthProviderType : IExternalAuthProviderType
{
    /// <summary>类型码,同时是 provider 的 Code。</summary>
    public const string TypeName = "dingtalk";

    /// <summary>字段名:应用 AppKey。</summary>
    public const string AppKeyField = "appKey";

    /// <summary>字段名:应用 AppSecret(机密)。</summary>
    public const string AppSecretField = "appSecret";

    private const string AccessTokenUrl = "https://api.dingtalk.com/v1.0/oauth2/accessToken";

    // 展示名与图标的缺省值只在 DingTalkAuthOptions 里定义一份
    private static readonly DingTalkAuthOptions Defaults = new();

    private static readonly IReadOnlyList<ExternalAuthField> FieldList =
    [
        new(AppKeyField),
        new(AppSecretField, Secret: true),
    ];

    /// <inheritdoc />
    public string Type => TypeName;

    /// <inheritdoc />
    public string DefaultDisplayName => Defaults.DisplayName;

    /// <inheritdoc />
    public string? DefaultIcon => Defaults.Icon;

    /// <inheritdoc />
    public bool AllowMultiple => false;

    /// <inheritdoc />
    public IReadOnlyList<ExternalAuthField> Fields => FieldList;

    /// <inheritdoc />
    public virtual IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services)
    {
        var options = new DingTalkAuthOptions
        {
            Code = TypeName,
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? DefaultDisplayName : config.DisplayName.Trim(),
            Icon = string.IsNullOrWhiteSpace(config.Icon) ? DefaultIcon : config.Icon,
            AppKey = config.Get(AppKeyField),
            AppSecret = config.Get(AppSecretField),
        };
        return new DingTalkExternalAuthProvider(
            options,
            services.GetRequiredService<IHttpClientFactory>().CreateClient(DingTalkExternalAuthProvider.HttpClientName),
            CreateLogger(services));
    }

    /// <summary>
    /// 两项检查:<c>reachable</c>(钉钉接口可达)、<c>credentials</c>(AppKey + AppSecret 能换到应用 access_token)。
    /// 请求只发往钉钉固定域名(凭据放在请求体里),不带用户登录态,不写库。
    /// </summary>
    public virtual async Task<ExternalAuthTestResult> TestAsync(
        ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var secret = config.Get(AppSecretField);
        try
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(DingTalkExternalAuthProvider.HttpClientName);
            var payload = JsonSerializer.Serialize(new { appKey = config.Get(AppKeyField), appSecret = secret });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(AccessTokenUrl, content, cancellationToken);
            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (status >= 500)
                return Result(Failed("reachable", $"HTTP {status}"), Skipped("credentials"));

            var reachable = new ExternalAuthCheck("reachable", ExternalAuthCheckStatus.Ok);
            if (!TryParseObject(body, out var doc))
                return Result(reachable, Failed("credentials", $"HTTP {status},响应无法解析"));
            using (doc)
            {
                var root = doc!.RootElement;
                var accessToken = StringProp(root, "accessToken");
                if (resp.IsSuccessStatusCode && !string.IsNullOrEmpty(accessToken))
                    return Result(reachable, new ExternalAuthCheck("credentials", ExternalAuthCheckStatus.Ok));

                // 失败响应形如 {"code":"...","message":"...","requestid":"..."};只取 code / message
                var code = StringProp(root, "code");
                var message = StringProp(root, "message");
                var detail = code is null && message is null
                    ? (resp.IsSuccessStatusCode ? "响应缺 accessToken" : $"HTTP {status}")
                    : $"HTTP {status} code={code} message={Scrub(message, secret)}";
                return Result(reachable, Failed("credentials", detail));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException or FormatException or ArgumentException)
        {
            // 只记异常类型,不记消息
            CreateLogger(services).LogWarning("钉钉连接测试失败 ({Type})", ex.GetType().Name);
            return Result(Failed("reachable", ex is TaskCanceledException ? "请求超时" : "网络请求失败"), Skipped("credentials"));
        }
    }

    private static ExternalAuthTestResult Result(params ExternalAuthCheck[] checks) => new(checks);

    private static ExternalAuthCheck Failed(string key, string detail) => new(key, ExternalAuthCheckStatus.Failed, detail);

    private static ExternalAuthCheck Skipped(string key) => new(key, ExternalAuthCheckStatus.Skipped);

    private static bool TryParseObject(string body, out JsonDocument? doc)
    {
        doc = null;
        try
        {
            var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                return false;
            }
            doc = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Detail 只放厂商返回的 message;万一回显了 secret 也抹掉,并限长
    private static string Scrub(string? message, string secret)
    {
        var msg = message ?? "";
        if (secret.Length > 0)
            msg = msg.Replace(secret, "***", StringComparison.Ordinal);
        return msg.Length > 200 ? msg[..200] : msg;
    }

    private static ILogger<DingTalkExternalAuthProvider> CreateLogger(IServiceProvider services) =>
        (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<DingTalkExternalAuthProvider>();
}
