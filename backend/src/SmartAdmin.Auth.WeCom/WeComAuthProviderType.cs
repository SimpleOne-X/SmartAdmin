using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.WeCom;

/// <summary>
/// 企业微信的「类型描述」:字段清单、按配置造 <see cref="WeComExternalAuthProvider"/>、连接测试。
/// 连接配置由管理员在「系统配置 → 登录方式」填写,<c>corpSecret</c> 加密入库;企业微信只能配置一份,Code 固定为 <see cref="TypeName"/>。
/// </summary>
public class WeComAuthProviderType : IExternalAuthProviderType
{
    /// <summary>类型码,同时是 provider 的 Code。</summary>
    public const string TypeName = "wecom";

    /// <summary>字段名:企业 Id。</summary>
    public const string CorpIdField = "corpId";

    /// <summary>字段名:应用 AgentId。</summary>
    public const string AgentIdField = "agentId";

    /// <summary>字段名:应用 Secret(机密)。</summary>
    public const string CorpSecretField = "corpSecret";

    private const string ApiBase = "https://qyapi.weixin.qq.com/cgi-bin";

    // 展示名与图标的缺省值只在 WeComAuthOptions 里定义一份
    private static readonly WeComAuthOptions Defaults = new();

    private static readonly IReadOnlyList<ExternalAuthField> FieldList =
    [
        new(CorpIdField),
        new(AgentIdField),
        new(CorpSecretField, Secret: true),
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
        var options = new WeComAuthOptions
        {
            Code = TypeName,
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? DefaultDisplayName : config.DisplayName.Trim(),
            Icon = string.IsNullOrWhiteSpace(config.Icon) ? DefaultIcon : config.Icon,
            CorpId = config.Get(CorpIdField),
            AgentId = config.Get(AgentIdField),
            CorpSecret = config.Get(CorpSecretField),
        };
        return new WeComExternalAuthProvider(
            options,
            services.GetRequiredService<IHttpClientFactory>().CreateClient(WeComExternalAuthProvider.HttpClientName),
            CreateLogger(services));
    }

    /// <summary>
    /// 三项检查:<c>reachable</c>(企业微信接口可达)、<c>credentials</c>(CorpId + CorpSecret 能换到 access_token)、
    /// <c>agent</c>(用该 access_token 能查到 AgentId 对应的应用,且应用未停用)。前一项不通过,后面的项标为 Skipped。
    /// 请求只发往企业微信固定域名,不带用户登录态,不写库。
    /// </summary>
    public virtual async Task<ExternalAuthTestResult> TestAsync(
        ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var logger = CreateLogger(services);
        var secret = config.Get(CorpSecretField);
        var checks = new List<ExternalAuthCheck>();
        try
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(WeComExternalAuthProvider.HttpClientName);

            // corpsecret 走 query 是 gettoken 的接口契约;URL 不进日志、不进 Detail
            var tokenUrl = $"{ApiBase}/gettoken?corpid={Uri.EscapeDataString(config.Get(CorpIdField))}&corpsecret={Uri.EscapeDataString(secret)}";
            var token = await GetAsync(http, tokenUrl, cancellationToken);
            if (token.Status >= 500)
            {
                checks.Add(new("reachable", ExternalAuthCheckStatus.Failed, $"HTTP {token.Status}"));
                return Finish(checks, "credentials", "agent");
            }
            checks.Add(new("reachable", ExternalAuthCheckStatus.Ok));

            if (!TryReadErrcode(token.Body, out var tokenErr, out var tokenMsg, out var root))
            {
                checks.Add(new("credentials", ExternalAuthCheckStatus.Failed, "响应无法解析"));
                return Finish(checks, "agent");
            }
            using var tokenDoc = root!;
            var accessToken = tokenDoc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            if (tokenErr != 0 || string.IsNullOrEmpty(accessToken))
            {
                checks.Add(new("credentials", ExternalAuthCheckStatus.Failed,
                    tokenErr != 0 ? Describe(tokenErr, tokenMsg, secret) : "响应缺 access_token"));
                return Finish(checks, "agent");
            }
            checks.Add(new("credentials", ExternalAuthCheckStatus.Ok));

            var agentUrl = $"{ApiBase}/agent/get?access_token={Uri.EscapeDataString(accessToken)}&agentid={Uri.EscapeDataString(config.Get(AgentIdField))}";
            var agent = await GetAsync(http, agentUrl, cancellationToken);
            if (!TryReadErrcode(agent.Body, out var agentErr, out var agentMsg, out var agentRoot))
            {
                checks.Add(new("agent", ExternalAuthCheckStatus.Failed, "响应无法解析"));
                return new ExternalAuthTestResult(checks);
            }
            using var agentDoc = agentRoot!;
            if (agentErr != 0)
                checks.Add(new("agent", ExternalAuthCheckStatus.Failed, Describe(agentErr, agentMsg, secret)));
            else if (agentDoc.RootElement.TryGetProperty("close", out var close) && close.ValueKind == JsonValueKind.Number && close.GetInt32() == 1)
                checks.Add(new("agent", ExternalAuthCheckStatus.Failed, "应用已被停用(close=1)"));
            else
                checks.Add(new("agent", ExternalAuthCheckStatus.Ok));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException or FormatException or ArgumentException)
        {
            // 只记异常类型,不记消息与 URL(URL 里带 secret / access_token)
            logger.LogWarning("企业微信连接测试失败 ({Type})", ex.GetType().Name);
            var failed = ex is TaskCanceledException ? "请求超时" : "网络请求失败";
            if (checks.Count == 0)
                checks.Add(new("reachable", ExternalAuthCheckStatus.Failed, failed));
            else
                checks.Add(new(NextKey(checks), ExternalAuthCheckStatus.Failed, failed));
            return Finish(checks, "credentials", "agent");
        }
        return new ExternalAuthTestResult(checks);
    }

    // 异常发生时下一个尚未出结果的检查项:reachable 之后是 credentials,再之后是 agent
    private static string NextKey(List<ExternalAuthCheck> checks) => checks.Count == 1 ? "credentials" : "agent";

    // 把还没出结果的检查项补成 Skipped(去重:异常发生在中途时已补的项不重复)
    private static ExternalAuthTestResult Finish(List<ExternalAuthCheck> checks, params string[] rest)
    {
        foreach (var key in rest)
            if (checks.All(c => c.Key != key))
                checks.Add(new(key, ExternalAuthCheckStatus.Skipped));
        return new ExternalAuthTestResult(checks);
    }

    private static async Task<(int Status, string Body)> GetAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var resp = await http.GetAsync(url, cancellationToken);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(cancellationToken));
    }

    // 解析企业微信通用返回体;errcode 缺省视为 0。解析失败返回 false。成功时调用方负责释放 root。
    private static bool TryReadErrcode(string body, out int errcode, out string? errmsg, out JsonDocument? root)
    {
        errcode = 0;
        errmsg = null;
        root = null;
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return false;
            }
            if (doc.RootElement.TryGetProperty("errcode", out var ec))
            {
                if (ec.ValueKind != JsonValueKind.Number || !ec.TryGetInt32(out errcode))
                {
                    doc.Dispose();
                    return false;
                }
            }
            if (doc.RootElement.TryGetProperty("errmsg", out var em) && em.ValueKind == JsonValueKind.String)
                errmsg = em.GetString();
            root = doc;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Detail 只放厂商的 errcode / errmsg;万一 errmsg 回显了 secret 也抹掉,并限长
    private static string Describe(int errcode, string? errmsg, string secret)
    {
        var msg = errmsg ?? "";
        if (secret.Length > 0)
            msg = msg.Replace(secret, "***", StringComparison.Ordinal);
        if (msg.Length > 200)
            msg = msg[..200];
        return $"errcode={errcode} errmsg={msg}";
    }

    private static ILogger<WeComExternalAuthProvider> CreateLogger(IServiceProvider services) =>
        (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<WeComExternalAuthProvider>();
}
