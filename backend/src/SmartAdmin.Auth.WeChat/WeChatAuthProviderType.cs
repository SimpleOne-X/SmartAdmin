using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.WeChat;

/// <summary>
/// 微信开放平台网站应用(个人微信扫码登录)的外部登录类型描述:声明管理页要填的字段
/// (<c>appId</c> 明文、<c>appSecret</c> 加密),并按库里的配置造出 <see cref="WeChatExternalAuthProvider"/>。
/// Code 固定为 <c>wechat</c>,只能配置一份。
/// </summary>
public class WeChatAuthProviderType : IExternalAuthProviderType
{
    /// <summary>字段名:网站应用 AppId(非机密)。</summary>
    public const string AppIdField = "appId";

    /// <summary>字段名:网站应用 AppSecret(机密)。</summary>
    public const string AppSecretField = "appSecret";

    /// <summary>连接测试检查项:能否连通微信开放平台接口。</summary>
    public const string ReachableCheck = "reachable";

    /// <summary>连接测试检查项:AppId 是否被微信识别。</summary>
    public const string AppIdCheck = "appId";

    /// <summary>连接测试检查项:AppSecret 是否正确(微信在校验授权码之后才校验它,故通常只能标为未验证)。</summary>
    public const string SecretCheck = "secret";

    // 微信官方错误码:https://developers.weixin.qq.com/doc/oplatform/developers/troubleshooting/
    private const int InvalidAppId = 40013;
    private const int InvalidCode = 40029;
    private const int CodeAlreadyUsed = 40163;
    private const int InvalidAppSecret = 40125;

    private const string TokenEndpoint = "https://api.weixin.qq.com/sns/oauth2/access_token";

    // 固定的假授权码,微信必然判其无效;取值本身不承载任何含义。
    private const string ProbeCode = "smartadmin-connection-test";

    private static readonly IReadOnlyList<ExternalAuthField> FieldList =
    [
        new ExternalAuthField(AppIdField),
        new ExternalAuthField(AppSecretField, Secret: true),
    ];

    /// <inheritdoc />
    public string Type => WeChatExternalAuthProvider.FixedCode;

    /// <inheritdoc />
    public string DefaultDisplayName => "微信";

    /// <inheritdoc />
    public string? DefaultIcon => null;

    /// <inheritdoc />
    public bool AllowMultiple => false;

    /// <inheritdoc />
    public IReadOnlyList<ExternalAuthField> Fields => FieldList;

    /// <inheritdoc />
    public virtual IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services)
    {
        var options = new WeChatAuthOptions
        {
            AppId = config.Get(AppIdField).Trim(),
            AppSecret = config.Get(AppSecretField).Trim(),
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? DefaultDisplayName : config.DisplayName,
            Icon = string.IsNullOrWhiteSpace(config.Icon) ? DefaultIcon : config.Icon,
        };
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(WeChatExternalAuthProvider.HttpClientName);
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<WeChatExternalAuthProvider>();
        return new WeChatExternalAuthProvider(options, http, logger);
    }

    /// <summary>
    /// 微信没有「不带用户就验证网站应用凭据」的接口,这里用一个必然无效的授权码请求换令牌端点:
    /// AppId 不存在返回 <c>40013</c>,可据此判定 AppId 有误;授权码无效返回 <c>40029</c>,说明 AppId 被识别,
    /// 但微信先校验授权码、后校验 AppSecret,所以此时 AppSecret 只能标为未验证。全程不发起真实登录。
    /// </summary>
    public virtual async Task<ExternalAuthTestResult> TestAsync(
        ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var appId = config.Get(AppIdField).Trim();
        var appSecret = config.Get(AppSecretField).Trim();
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<WeChatAuthProviderType>();

        int status;
        string body;
        try
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(WeChatExternalAuthProvider.HttpClientName);
            // secret 在 query 中是微信接口的约定;该 URL 不落日志
            var url = TokenEndpoint
                      + $"?appid={Uri.EscapeDataString(appId)}"
                      + $"&secret={Uri.EscapeDataString(appSecret)}"
                      + $"&code={ProbeCode}"
                      + "&grant_type=authorization_code";
            using var resp = await http.GetAsync(url, cancellationToken);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // 只记异常类型:异常消息可能带请求 URL,而 URL 含 secret
            logger.LogWarning("WeChat connection test failed to reach token endpoint ({Type})", ex.GetType().Name);
            return Result(
                new ExternalAuthCheck(ReachableCheck, ExternalAuthCheckStatus.Failed, ex.GetType().Name),
                new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Skipped),
                new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Skipped));
        }

        if (status >= 500)
        {
            return Result(
                new ExternalAuthCheck(ReachableCheck, ExternalAuthCheckStatus.Failed, $"HTTP {status}"),
                new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Skipped),
                new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Skipped));
        }

        var reachable = new ExternalAuthCheck(ReachableCheck, ExternalAuthCheckStatus.Ok);
        return Classify(reachable, status, body, appSecret);
    }

    private static ExternalAuthTestResult Classify(ExternalAuthCheck reachable, int status, string body, string appSecret)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("errcode", out var ec)
                && ec.ValueKind == JsonValueKind.Number
                && ec.TryGetInt32(out var code))
            {
                var detail = Describe(code, root, appSecret);
                return code switch
                {
                    InvalidAppId => Result(reachable,
                        new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Failed, detail),
                        new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Skipped)),
                    InvalidAppSecret => Result(reachable,
                        new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Ok),
                        new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Failed, detail)),
                    InvalidCode or CodeAlreadyUsed => Result(reachable,
                        new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Ok, detail),
                        new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Skipped)),
                    // 其他错误码不足以判断 AppId / AppSecret 是否有效
                    _ => Result(reachable,
                        new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Skipped, detail),
                        new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Skipped)),
                };
            }
        }
        catch (JsonException)
        {
            // 落到下面的畸形响应分支
        }

        var invalid = $"invalid response (HTTP {status})";
        return Result(reachable,
            new ExternalAuthCheck(AppIdCheck, ExternalAuthCheckStatus.Failed, invalid),
            new ExternalAuthCheck(SecretCheck, ExternalAuthCheckStatus.Skipped));
    }

    private static ExternalAuthTestResult Result(params ExternalAuthCheck[] checks) => new(checks);

    /// <summary>Detail 只含 errcode 与截断后的 errmsg(去掉微信附带的 rid),并抹掉 secret。</summary>
    private static string Describe(int code, JsonElement root, string secret)
    {
        var msg = root.TryGetProperty("errmsg", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() ?? "" : "";
        var rid = msg.IndexOf(", rid:", StringComparison.Ordinal);
        if (rid >= 0)
            msg = msg[..rid];
        if (secret.Length > 0)
            msg = msg.Replace(secret, "***", StringComparison.Ordinal);
        if (msg.Length > 64)
            msg = msg[..64];
        return msg.Length == 0 ? $"errcode {code}" : $"errcode {code}: {msg}";
    }
}
