using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 内置的标准 OIDC 类型描述:管理员在「登录方式」页添加任意多条(Keycloak / Entra ID / Authing / Auth0 …),
/// 每条造出一个 <see cref="OidcExternalAuthProvider"/>。由 <see cref="ExternalAuthSetup.AddExternalAuthProviders"/> 固定注册。
/// <para>Authority 决定服务端请求发往哪里,故标为 <see cref="ExternalAuthField.DefinesEndpoint"/>:改它必须重输 Client Secret。</para>
/// </summary>
public class OidcExternalAuthProviderType : IExternalAuthProviderType
{
    /// <summary>类型码</summary>
    public const string TypeName = "oidc";

    /// <summary>Scopes 缺省值;<c>openid</c> 必带</summary>
    public const string DefaultScopes = "openid profile email";

    private static readonly IReadOnlyList<ExternalAuthField> FIELDS =
    [
        new("authority", DefinesEndpoint: true),
        new("clientId"),
        new("clientSecret", Secret: true),
        new("scopes", Required: false, Default: DefaultScopes),
        new("usePkce", Required: false, Default: "true"),
    ];

    /// <inheritdoc />
    public string Type => TypeName;

    /// <inheritdoc />
    public string DefaultDisplayName => "SSO";

    /// <inheritdoc />
    public string? DefaultIcon => null;

    /// <inheritdoc />
    public bool AllowMultiple => true;

    /// <inheritdoc />
    public IReadOnlyList<ExternalAuthField> Fields => FIELDS;

    /// <inheritdoc />
    public virtual IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services) =>
        new OidcExternalAuthProvider(
            new OidcProviderOptions
            {
                Code = config.Code,
                DisplayName = config.DisplayName,
                Icon = config.Icon,
                Authority = config.Get("authority"),
                ClientId = config.Get("clientId"),
                ClientSecret = config.Get("clientSecret"),
                Scopes = string.IsNullOrWhiteSpace(config.Get("scopes")) ? DefaultScopes : config.Get("scopes"),
                UsePkce = !string.Equals(config.Get("usePkce"), "false", StringComparison.OrdinalIgnoreCase),
            },
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILogger<OidcExternalAuthProvider>>(),
            // 仅开发环境放行 http 元数据;生产强制 https(fail-closed,见 OidcExternalAuthProvider 构造)
            allowHttpMetadata: services.GetRequiredService<IHostEnvironment>().IsDevelopment());

    /// <summary>
    /// 检查项:<c>discovery</c>(发现文档可取且是合法 JSON)、<c>issuer</c>(与 Authority 一致)、
    /// <c>endpoints</c>(有授权端点与令牌端点)、<c>clientSecret</c>(恒为未验证:标准 OIDC 没有不带用户就能验密钥的通用办法)。
    /// 请求走带 SSRF 围栏的命名客户端。
    /// </summary>
    public virtual async Task<ExternalAuthTestResult> TestAsync(
        ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var checks = new List<ExternalAuthCheck>();
        var authority = config.Get("authority").TrimEnd('/');
        var address = authority + "/.well-known/openid-configuration";

        var document = await FetchDiscoveryAsync(address, services, checks, cancellationToken);
        if (document is not null)
        {
            using (document)
            {
                var root = document.RootElement;
                checks.Add(CheckIssuer(root, authority));
                checks.Add(CheckEndpoints(root));
            }
        }
        checks.Add(new ExternalAuthCheck("clientSecret", ExternalAuthCheckStatus.Skipped));
        return new ExternalAuthTestResult(checks);
    }

    /// <summary>取发现文档并把 <c>discovery</c> 检查项写进 <paramref name="checks"/>;失败返回 <c>null</c>。网络与解析异常折成失败项。</summary>
    protected virtual async Task<JsonDocument?> FetchDiscoveryAsync(
        string address, IServiceProvider services, List<ExternalAuthCheck> checks, CancellationToken cancellationToken)
    {
        var allowHttp = services.GetRequiredService<IHostEnvironment>().IsDevelopment();
        if (!allowHttp && !address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new ExternalAuthCheck("discovery", ExternalAuthCheckStatus.Failed, "生产环境要求 Authority 使用 https"));
            return null;
        }

        try
        {
            using var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(ExternalAuthHttpClient.Name);
            using var resp = await http.GetAsync(address, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                checks.Add(new ExternalAuthCheck("discovery", ExternalAuthCheckStatus.Failed, $"HTTP {(int)resp.StatusCode}"));
                return null;
            }

            var document = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                checks.Add(new ExternalAuthCheck("discovery", ExternalAuthCheckStatus.Failed, "发现文档不是 JSON 对象"));
                return null;
            }
            checks.Add(new ExternalAuthCheck("discovery", ExternalAuthCheckStatus.Ok));
            return document;
        }
        catch (JsonException)
        {
            checks.Add(new ExternalAuthCheck("discovery", ExternalAuthCheckStatus.Failed, "发现文档不是合法 JSON"));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // 调用方主动取消要照原样冒泡;这里只接网络错误与内部时限
            if (cancellationToken.IsCancellationRequested) throw;
            checks.Add(new ExternalAuthCheck("discovery", ExternalAuthCheckStatus.Failed, ex.Message));
            return null;
        }
    }

    private static ExternalAuthCheck CheckIssuer(JsonElement root, string authority)
    {
        var issuer = Str(root, "issuer");
        return string.Equals(issuer?.TrimEnd('/'), authority, StringComparison.Ordinal)
            ? new ExternalAuthCheck("issuer", ExternalAuthCheckStatus.Ok)
            : new ExternalAuthCheck("issuer", ExternalAuthCheckStatus.Failed, $"issuer={issuer ?? "(缺失)"}");
    }

    private static ExternalAuthCheck CheckEndpoints(JsonElement root)
    {
        var missing = new[] { "authorization_endpoint", "token_endpoint" }
            .Where(name => string.IsNullOrWhiteSpace(Str(root, name))).ToList();
        return missing.Count == 0
            ? new ExternalAuthCheck("endpoints", ExternalAuthCheckStatus.Ok)
            : new ExternalAuthCheck("endpoints", ExternalAuthCheckStatus.Failed, "缺少 " + string.Join("、", missing));
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
