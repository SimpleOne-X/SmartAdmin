using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.WeCom;

/// <summary>
/// 企业微信外部登录 provider(可选包)。授权页按客户端分两种:桌面浏览器走<b>扫码授权登录</b>,
/// 企业微信客户端内置浏览器(桌面端与手机端)走 <b>OAuth 网页授权</b>(<c>snsapi_base</c> 静默授权,不弹确认页)。
/// 两种授权带回的 <c>code</c> 都用企业 access_token 调同一个 <c>auth/getuserinfo</c> 换 <c>userid</c>,
/// 作为外部身份 <see cref="ExternalIdentity.Subject"/>。
/// <para>同一份应用凭据与 access_token 缓存也用来发应用消息(<see cref="IExternalNoticeSender"/>):站内通知以文字卡片推给成员。</para>
/// <para>不走 OIDC(无 id_token/nonce/PKCE);<see cref="HttpClient"/> 由 DI 注入(与 GitHub/WeChat 做法一致)。方法 <c>virtual</c> 可覆写。</para>
/// </summary>
public class WeComExternalAuthProvider : IExternalAuthProvider, IExternalNoticeSender
{
    /// <summary>注册在 <see cref="IHttpClientFactory"/> 里的命名 HttpClient 键。</summary>
    public const string HttpClientName = "SmartAdmin.Auth.WeCom";

    private const string ApiBase = "https://qyapi.weixin.qq.com/cgi-bin";

    // 文字卡片的上限:标题 128、描述 512 个字符
    private const int TitleMax = 128;
    private const int DescriptionMax = 512;

    private readonly WeComAuthOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<WeComExternalAuthProvider> _logger;

    // 企业 access_token 有额度且有效期 2h,不能每次登录都换 → 实例内缓存(每 DI 单例一份)
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;

    /// <summary>创建实例;<paramref name="http"/> 应为按 <see cref="HttpClientName"/> 命名注册的客户端。</summary>
    public WeComExternalAuthProvider(
        WeComAuthOptions options,
        HttpClient http,
        ILogger<WeComExternalAuthProvider> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Code => _options.Code;

    /// <inheritdoc />
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_options.DisplayName) ? "企业微信" : _options.DisplayName.Trim();

    /// <inheritdoc />
    public string? Icon => _options.Icon;

    /// <inheritdoc />
    public virtual Task<string> BuildAuthorizeUrlAsync(ExternalAuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        // 扫码页只能在桌面浏览器里用,企业微信客户端里没法扫自己,只能走网页授权。state 防 CSRF,不使用 nonce/PKCE。
        var url = IsWeComClient(request.UserAgent)
            ? "https://open.weixin.qq.com/connect/oauth2/authorize" +
              $"?appid={Uri.EscapeDataString(_options.CorpId)}" +
              $"&redirect_uri={Uri.EscapeDataString(request.RedirectUri)}" +
              "&response_type=code&scope=snsapi_base" +
              $"&state={Uri.EscapeDataString(request.State)}" +
              $"&agentid={Uri.EscapeDataString(_options.AgentId)}" +
              "#wechat_redirect"
            : "https://login.work.weixin.qq.com/wwlogin/sso/login?login_type=CorpApp" +
              $"&appid={Uri.EscapeDataString(_options.CorpId)}" +
              $"&agentid={Uri.EscapeDataString(_options.AgentId)}" +
              $"&redirect_uri={Uri.EscapeDataString(request.RedirectUri)}" +
              $"&state={Uri.EscapeDataString(request.State)}";
        return Task.FromResult(url);
    }

    /// <summary>是否在企业微信客户端的内置浏览器里:企业微信的 User-Agent 带 <c>wxwork</c>(同时也带微信的 <c>MicroMessenger</c>,不能拿它判断)。</summary>
    protected virtual bool IsWeComClient(string? userAgent) =>
        userAgent?.Contains("wxwork", StringComparison.OrdinalIgnoreCase) == true;

    /// <inheritdoc />
    public virtual async Task<ExternalIdentity> ExchangeAsync(ExternalExchangeRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExchangeCoreAsync(request, cancellationToken);
        }
        catch (AdminException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException or FormatException or ArgumentException
                                       or KeyNotFoundException)
        {
            // 对齐 GitHub:网络/解析类异常 → 40015,勿 500;不记含 secret 的 URL
            _logger.LogWarning(ex, "WeCom OAuth exchange failed ({Type})", ex.GetType().Name);
            throw new AdminException(ErrorCode.OAuthExchangeFailed);
        }
    }

    private async Task<ExternalIdentity> ExchangeCoreAsync(ExternalExchangeRequest request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var url = $"{ApiBase}/auth/getuserinfo?access_token={Uri.EscapeDataString(token)}&code={Uri.EscapeDataString(request.Code)}";
        using var doc = await GetJsonAsync("getuserinfo", url, cancellationToken);
        var root = doc.RootElement;
        EnsureOk(root, "getuserinfo", token);

        // 企业成员回 userid;外部联系人回 openid。任取其一作为唯一标识。
        var subject = StringProp(root, "userid") ?? StringProp(root, "openid");
        if (string.IsNullOrEmpty(subject))
        {
            _logger.LogWarning("企业微信 getuserinfo 未返回 userid/openid");
            throw new AdminException(ErrorCode.OAuthExchangeFailed);
        }
        // DisplayName 需再调 user/get,本批不扩;pending-link 确认框仍可用 provider 展示名
        return new ExternalIdentity(_options.Code, subject);
    }

    /// <summary>取企业 access_token(实例内缓存,提前 5 分钟过期刷新)。</summary>
    protected virtual async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // 局部变量只读一次:InvalidateAccessToken 可能在判断与返回之间把字段清空
        var cached = _cachedToken;
        if (cached is not null && DateTimeOffset.UtcNow < _tokenExpiry) return cached;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cachedToken;
            if (cached is not null && DateTimeOffset.UtcNow < _tokenExpiry) return cached;
            // corpsecret 走 query 是企业微信 gettoken 的 GET API 契约;日志禁止完整 URL
            var url = $"{ApiBase}/gettoken?corpid={Uri.EscapeDataString(_options.CorpId)}&corpsecret={Uri.EscapeDataString(_options.CorpSecret)}";
            using var doc = await GetJsonAsync("gettoken", url, cancellationToken);
            var root = doc.RootElement;
            EnsureOk(root, "gettoken");
            var token = root.GetProperty("access_token").GetString();
            var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 7200;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300));
            _cachedToken = token;
            return token!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>丢弃缓存的 access_token,下次取用时重新换。服务端提前让 token 失效(40014 / 42001)时调用。</summary>
    protected virtual void InvalidateAccessToken() => _cachedToken = null;

    /// <inheritdoc />
    public virtual async Task<ExternalNoticeResult> SendAsync(
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(_options.AgentId, out var agentId))
            throw new ExternalNoticeException("企业微信 AgentId 必须是数字", retryable: false);

        var payload = JsonSerializer.Serialize(new
        {
            touser = string.Join('|', subjects),
            msgtype = "textcard",
            agentid = agentId,
            textcard = new
            {
                title = Truncate(message.Title, TitleMax),
                description = EscapeAndTruncate(message.Text, DescriptionMax),
                url = message.Url,
                btntxt = "详情",
            },
            // 同一批人、同样内容 10 分钟内企业微信自己去重,重试不会重复弹
            enable_duplicate_check = 1,
            duplicate_check_interval = 600,
        });

        try
        {
            try
            {
                return await SendOnceAsync(payload, cancellationToken);
            }
            catch (WeComApiException ex) when (ex.ErrCode is 40014 or 42001)
            {
                // token 被服务端提前作废:换一个再发一次,仍失败就是真失败
                InvalidateAccessToken();
                return await SendOnceAsync(payload, cancellationToken);
            }
        }
        catch (WeComApiException ex)
        {
            throw ToNoticeException(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException or FormatException or ArgumentException
                                       or KeyNotFoundException)
        {
            // 底层异常的消息可能带请求 URL(含 access_token / corpsecret):只记类型,不传 inner
            _logger.LogWarning("企业微信 message/send 请求失败 ({Type})", ex.GetType().Name);
            throw new ExternalNoticeException($"企业微信 message/send 请求失败 ({ex.GetType().Name})", retryable: true);
        }
    }

    private async Task<ExternalNoticeResult> SendOnceAsync(string payload, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var url = $"{ApiBase}/message/send?access_token={Uri.EscapeDataString(token)}";
        using var doc = await PostJsonAsync("message/send", url, payload, cancellationToken);
        var root = doc.RootElement;
        EnsureOk(root, "message/send", token);

        // 部分接收人无效时 errcode 仍是 0,无效的列在这两个字段里(用 | 分隔)
        return new ExternalNoticeResult([.. Names(root, "invaliduser"), .. Names(root, "unlicenseduser")]);
    }

    private static string[] Names(JsonElement root, string name) =>
        StringProp(root, name)?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];

    private static ExternalNoticeException ToNoticeException(WeComApiException ex)
    {
        if (ex.ErrCode is not { } code)
            return new($"企业微信 {ex.Api} HTTP {ex.HttpStatus}", retryable: ex.HttpStatus is >= 500 or 408 or 429);

        var text = $"企业微信 {ex.Api} errcode={code} {ex.ErrMsg}";
        if (code == 60020) text += "(把服务器出口 IP 加入应用的企业可信 IP)";
        if (code == 81013) text += "(接收人不在应用可见范围内,到企业微信管理后台调整)";
        // -1 是企业微信「系统繁忙」,稍后重试;其余错误码重试也不会变
        return new(text, retryable: code == -1, code.ToString(CultureInfo.InvariantCulture));
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..max];
        // 不把代理对从中间劈开
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    // 描述支持企业微信的有限 HTML,正文里的 < > & 必须转义;截断在转义之后做,且不能停在 &lt; 这类实体中间
    private static string EscapeAndTruncate(string text, int max)
    {
        var escaped = text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
        if (escaped.Length <= max) return escaped;

        var cut = escaped[..(max - 1)];
        var amp = cut.LastIndexOf('&');
        if (amp >= 0 && cut.IndexOf(';', amp) < 0) cut = cut[..amp];
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut + "…";
    }

    private Task<JsonDocument> GetJsonAsync(string api, string url, CancellationToken cancellationToken) =>
        ReadJsonAsync(api, () => _http.GetAsync(url, cancellationToken), cancellationToken);

    private Task<JsonDocument> PostJsonAsync(string api, string url, string json, CancellationToken cancellationToken) =>
        ReadJsonAsync(api, () => _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken), cancellationToken);

    private async Task<JsonDocument> ReadJsonAsync(string api, Func<Task<HttpResponseMessage>> request, CancellationToken cancellationToken)
    {
        using var resp = await request();
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("企业微信 {Api} HTTP {Status}", api, (int)resp.StatusCode);
            throw new WeComApiException(api, null, (int)resp.StatusCode, null);
        }
        return JsonDocument.Parse(body);
    }

    private void EnsureOk(JsonElement root, string api, string? token = null)
    {
        if (root.TryGetProperty("errcode", out var ec) && ec.GetInt32() != 0)
        {
            var code = ec.GetInt32();
            var msg = Scrub(StringProp(root, "errmsg"), token);
            _logger.LogWarning("企业微信 {Api} errcode={Code} errmsg={Msg}", api, code, msg);
            throw new WeComApiException(api, code, null, msg);
        }
    }

    // 厂商的 errmsg 偶尔回显请求参数:把 corpsecret 与 access_token 抹掉再进日志和异常消息,并限长
    private string Scrub(string? text, string? token)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (_options.CorpSecret.Length > 0)
            text = text.Replace(_options.CorpSecret, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(token))
            text = text.Replace(token, "***", StringComparison.Ordinal);
        return text.Length > 200 ? text[..200] : text;
    }

    private static string? StringProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // 登录路径只认 AdminException(OAuthExchangeFailed);发送路径靠它带出的厂商错误码和 HTTP 状态做重试判断
    private sealed class WeComApiException(string api, int? errCode, int? httpStatus, string? errMsg)
        : AdminException(ErrorCode.OAuthExchangeFailed)
    {
        public string Api { get; } = api;
        public int? ErrCode { get; } = errCode;
        public int? HttpStatus { get; } = httpStatus;
        public string? ErrMsg { get; } = errMsg;
    }
}
