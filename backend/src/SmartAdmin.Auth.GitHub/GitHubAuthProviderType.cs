using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.GitHub;

/// <summary>
/// GitHub OAuth App 的外部登录类型描述:声明管理页要填的字段(<c>clientId</c> 明文、<c>clientSecret</c> 加密),
/// 并按库里的配置造出 <see cref="GitHubExternalAuthProvider"/>。Code 固定为 <c>github</c>,只能配置一份。
/// </summary>
public class GitHubAuthProviderType : IExternalAuthProviderType
{
    /// <summary>字段名:OAuth App 的 Client ID(非机密)。</summary>
    public const string ClientIdField = "clientId";

    /// <summary>字段名:OAuth App 的 Client Secret(机密)。</summary>
    public const string ClientSecretField = "clientSecret";

    /// <summary>连接测试检查项:能否连通 github.com。</summary>
    public const string ReachableCheck = "reachable";

    /// <summary>连接测试检查项:Client ID / Client Secret 是否被 GitHub 接受。</summary>
    public const string CredentialsCheck = "credentials";

    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";

    // 固定的假授权码,GitHub 必然判其无效;取值本身不承载任何含义。
    private const string ProbeCode = "smartadmin-connection-test";

    private static readonly IReadOnlyList<ExternalAuthField> FieldList =
    [
        new ExternalAuthField(ClientIdField),
        new ExternalAuthField(ClientSecretField, Secret: true),
    ];

    /// <inheritdoc />
    public string Type => GitHubExternalAuthProvider.FixedCode;

    /// <inheritdoc />
    public string DefaultDisplayName => "GitHub";

    /// <inheritdoc />
    public string? DefaultIcon => null;

    /// <inheritdoc />
    public bool AllowMultiple => false;

    /// <inheritdoc />
    public IReadOnlyList<ExternalAuthField> Fields => FieldList;

    /// <inheritdoc />
    public virtual IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services)
    {
        var options = new GitHubAuthOptions
        {
            ClientId = config.Get(ClientIdField).Trim(),
            ClientSecret = config.Get(ClientSecretField).Trim(),
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? DefaultDisplayName : config.DisplayName,
            Icon = string.IsNullOrWhiteSpace(config.Icon) ? DefaultIcon : config.Icon,
        };
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubExternalAuthProvider.HttpClientName);
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<GitHubExternalAuthProvider>();
        return new GitHubExternalAuthProvider(options, http, logger);
    }

    /// <summary>
    /// GitHub 没有「不带用户就验证 OAuth App 凭据」的接口,这里用一个必然无效的授权码请求换令牌端点,
    /// 靠错误类型区分:<c>bad_verification_code</c>(码无效)说明 Client ID 与 Secret 都通过了校验;
    /// <c>incorrect_client_credentials</c> 或 HTTP 404(Client ID 不存在)说明凭据有误。全程不发起真实登录。
    /// </summary>
    public virtual async Task<ExternalAuthTestResult> TestAsync(
        ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var clientId = config.Get(ClientIdField).Trim();
        var clientSecret = config.Get(ClientSecretField).Trim();
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<GitHubAuthProviderType>();

        int status;
        string body;
        try
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubExternalAuthProvider.HttpClientName);
            GitHubExternalAuthProvider.EnsureDefaultUserAgent(http);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = ProbeCode,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = content };
            GitHubExternalAuthProvider.ApplyRequestUserAgent(req);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, cancellationToken);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // 只记异常类型:请求体含 secret,异常消息与堆栈不落日志
            logger.LogWarning("GitHub connection test failed to reach token endpoint ({Type})", ex.GetType().Name);
            return new ExternalAuthTestResult(
            [
                new ExternalAuthCheck(ReachableCheck, ExternalAuthCheckStatus.Failed, ex.GetType().Name),
                new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Skipped),
            ]);
        }

        if (status >= 500)
        {
            return new ExternalAuthTestResult(
            [
                new ExternalAuthCheck(ReachableCheck, ExternalAuthCheckStatus.Failed, $"HTTP {status}"),
                new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Skipped),
            ]);
        }

        return new ExternalAuthTestResult(
        [
            new ExternalAuthCheck(ReachableCheck, ExternalAuthCheckStatus.Ok),
            ClassifyCredentials(status, body, clientSecret),
        ]);
    }

    /// <summary>把换令牌端点对假授权码的响应归类为凭据检查项。</summary>
    private static ExternalAuthCheck ClassifyCredentials(int status, string body, string clientSecret)
    {
        // Client ID 不存在时 GitHub 直接回 404(不带 OAuth 错误码)
        if (status == 404)
            return new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Failed, "HTTP 404");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return InvalidResponse(status);

            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            {
                var error = Sanitize(errEl.GetString(), clientSecret);
                return error switch
                {
                    "incorrect_client_credentials" => new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Failed, error),
                    "bad_verification_code" => new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Ok, error),
                    // 其他错误码(如 redirect_uri_mismatch)不足以判断凭据是否有效
                    _ => new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Skipped, error),
                };
            }

            // 假授权码不可能换到令牌;返回了 access_token 说明端点行为异常,无法据此判断凭据
            if (root.TryGetProperty("access_token", out _))
                return new ExternalAuthCheck(CredentialsCheck, ExternalAuthCheckStatus.Skipped, "unexpected token response");

            return InvalidResponse(status);
        }
        catch (JsonException)
        {
            return InvalidResponse(status);
        }
    }

    private static ExternalAuthCheck InvalidResponse(int status) =>
        new(CredentialsCheck, ExternalAuthCheckStatus.Failed, $"invalid response (HTTP {status})");

    /// <summary>厂商返回的错误串只截取前 64 字符并抹掉 secret,保证 Detail 不会携带机密。</summary>
    private static string Sanitize(string? value, string secret)
    {
        var text = value ?? "";
        if (secret.Length > 0)
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text.Length > 64 ? text[..64] : text;
    }
}
