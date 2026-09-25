using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 第三方登录连接配置相关测试共用的替身:一个「只许一份」的假厂商类型(<see cref="AcmeAuthProviderType"/>,官方厂商包的形态)、
/// 对应的假 provider,以及带超管登录的 HTTP 客户端。真 OIDC 类型用来覆盖「允许多条 + 决定请求去向的字段」。
/// </summary>
internal static class ExternalAuthProviderTestSupport
{
    public const string AcmeSecret = "acme-secret-VALUE-1234567890";

    public static AdminAppFactory Factory(AcmeAuthProviderType acme, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? more = null) => new()
    {
        Settings = settings,
        Overrides = s =>
        {
            s.AddSingleton<IExternalAuthProviderType>(acme);
            more?.Invoke(s);
        },
    };

    public static async Task<HttpClient> SuperAdminClientAsync(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    public static ExternalAuthProviderSaveInput AcmeInput(string appId = "app-1", string? secret = AcmeSecret) => new(
        "acme", null, null,
        new Dictionary<string, string?> { ["appId"] = appId },
        secret is null ? null : new Dictionary<string, string?> { ["appSecret"] = secret });

    public static ExternalAuthProviderSaveInput OidcInput(string authority, string? secret, string clientId = "client-1") => new(
        "oidc", null, null,
        new Dictionary<string, string?> { ["authority"] = authority, ["clientId"] = clientId },
        secret is null ? null : new Dictionary<string, string?> { ["clientSecret"] = secret });
}

/// <summary>假的官方厂商类型:Code 必须等于类型名 <c>acme</c>,一个非机密字段、一个机密字段。记录 <c>Create</c> 调用次数。</summary>
internal sealed class AcmeAuthProviderType : IExternalAuthProviderType
{
    private int _created;

    /// <summary><c>Create</c> 被调用的次数:用来断言实例被复用、行变了才重建。</summary>
    public int CreateCount => _created;

    /// <summary>最近一次 <c>TestAsync</c> 收到的配置</summary>
    public ExternalAuthProviderConfig? LastTested { get; private set; }

    public string Type => "acme";
    public string DefaultDisplayName => "Acme";
    public string? DefaultIcon => "ph:acme";
    public bool AllowMultiple => false;

    public IReadOnlyList<ExternalAuthField> Fields { get; } =
    [
        new("appId"),
        new("appSecret", Secret: true),
    ];

    public IExternalAuthProvider Create(ExternalAuthProviderConfig config, IServiceProvider services)
    {
        Interlocked.Increment(ref _created);
        return new AcmeProvider(config);
    }

    /// <summary>故意把机密写进 Detail:验证服务层会把它打码,类型实现有疏漏也漏不出去。</summary>
    public Task<ExternalAuthTestResult> TestAsync(ExternalAuthProviderConfig config, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        LastTested = config;
        return Task.FromResult(new ExternalAuthTestResult(
        [
            new ExternalAuthCheck("credential", ExternalAuthCheckStatus.Ok, $"appId={config.Get("appId")};secret={config.Get("appSecret")}"),
            new ExternalAuthCheck("agent", ExternalAuthCheckStatus.Skipped),
        ]));
    }
}

/// <summary>假 provider:换身份恒回一个未绑定的外部身份。</summary>
internal sealed class AcmeProvider(ExternalAuthProviderConfig config) : IExternalAuthProvider
{
    public ExternalAuthProviderConfig Config => config;
    public string Code => config.Code;
    public string DisplayName => config.DisplayName;
    public string? Icon => config.Icon;

    public Task<string> BuildAuthorizeUrlAsync(ExternalAuthorizeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult($"https://acme.test/authorize?app={Uri.EscapeDataString(config.Get("appId"))}");

    public Task<ExternalIdentity> ExchangeAsync(ExternalExchangeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExternalIdentity(config.Code, "sub-acme", "Acme User"));
}

/// <summary>代码注册的 provider(消费者接自有 IdP 的形态)。</summary>
internal sealed class CodeRegisteredProvider(string code) : IExternalAuthProvider
{
    public string Code => code;
    public string DisplayName => "Code " + code;
    public string? Icon => null;

    public Task<string> BuildAuthorizeUrlAsync(ExternalAuthorizeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult("https://code.test/authorize");

    public Task<ExternalIdentity> ExchangeAsync(ExternalExchangeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExternalIdentity(code, "sub-code"));
}
