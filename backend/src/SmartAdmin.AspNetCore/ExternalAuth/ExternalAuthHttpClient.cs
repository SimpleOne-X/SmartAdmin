using Microsoft.IdentityModel.Protocols;
using SmartAdmin.Core;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 外部登录出站 HTTP 的命名 <c>HttpClient</c>。OIDC 的 Authority 由管理员填写,服务端会去请求它
/// (发现文档、JWKS、令牌端点),所以这些请求走带 SSRF 围栏的处理器:禁重定向、连接时对解析后的每个 IP 复检黑名单
/// (防 DNS rebinding)。处理器在 <see cref="ExternalAuthSetup.AddExternalAuthProviders"/> 里挂载,围栏配置复用 <c>AdminAiOptions.Http</c>。
/// </summary>
public static class ExternalAuthHttpClient
{
    /// <summary>命名 HttpClient 常量,<c>IHttpClientFactory.CreateClient(ExternalAuthHttpClient.Name)</c> 取用</summary>
    public const string Name = "SmartAdmin.ExternalAuth";
}

/// <summary>
/// 走 <see cref="ExternalAuthHttpClient.Name"/> 的文档检索器,供 <c>ConfigurationManager</c> 取 OIDC 发现文档与 JWKS。
/// 每次取文档都向工厂要一个客户端,不长期持有:处理器的连接池与 DNS 轮换由工厂管理。
/// </summary>
public sealed class FencedDocumentRetriever(IHttpClientFactory httpFactory, bool requireHttps) : IDocumentRetriever
{
    /// <inheritdoc />
    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentNullException(nameof(address));
        if (requireHttps && !address.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"元数据地址必须是 https:{address}");

        using var http = httpFactory.CreateClient(ExternalAuthHttpClient.Name);
        using var resp = await http.GetAsync(address, cancel);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"取元数据失败 {(int)resp.StatusCode}:{address}");
        return await resp.Content.ReadAsStringAsync(cancel);
    }
}
