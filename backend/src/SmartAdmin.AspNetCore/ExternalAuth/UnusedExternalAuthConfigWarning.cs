using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 启动时提示:配置里存在 <c>SmartAdmin:ExternalAuth:{Oidc|WeCom|DingTalk|GitHub|WeChat}</c> 节。
/// 这些节不会被读取,各登录方式的连接与密钥只从库里取,所以这里说清楚该去哪配,免得按钮迟迟不出现却查不出原因。
/// 日志里只有节名,不带任何配置值。
/// </summary>
public sealed class UnusedExternalAuthConfigWarning(IReadOnlyList<string> sections, ILogger<UnusedExternalAuthConfigWarning> logger) : IHostedService
{
    private static readonly string[] SECTION_NAMES = ["Oidc", "WeCom", "DingTalk", "GitHub", "WeChat"];

    /// <summary>在 <c>SmartAdmin:ExternalAuth</c> 节下找出存在(有子键或有值)的、不会被读取的节,返回带完整路径的节名。</summary>
    public static IReadOnlyList<string> Find(IConfigurationSection externalAuth) =>
        [.. externalAuth.GetChildren()
            .Where(c => SECTION_NAMES.Contains(c.Key, StringComparer.OrdinalIgnoreCase) && (c.Value is not null || c.GetChildren().Any()))
            .Select(c => $"SmartAdmin:ExternalAuth:{c.Key}")];

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (sections.Count > 0)
            logger.LogWarning("检测到 {Sections} 配置节,不会被读取;请到 系统配置 → 登录方式 配置", string.Join('、', sections));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
