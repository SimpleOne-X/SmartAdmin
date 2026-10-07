using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.GitHub;

/// <summary>GitHub 登录装配:在 <c>AddSmartAdmin()</c> 之前调用。</summary>
public static class GitHubSetup
{
    /// <summary>
    /// 接入 GitHub 登录:注册命名 HttpClient 与 <see cref="IExternalAuthProviderType"/> 类型描述。
    /// 不读取配置、不抛异常;Client ID / Client Secret 由管理员在 系统配置 → 登录方式 填写,加密保存到数据库,
    /// 填写完整后登录页才出现 GitHub 入口。同 Code 的 <see cref="IExternalAuthProvider"/> 若已由代码注册
    /// (见带 <see cref="GitHubAuthOptions"/> 参数的重载),以代码注册的为准。
    /// </summary>
    public static IServiceCollection AddSmartAdminGitHubAuth(this IServiceCollection services)
    {
        AddHttpClient(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProviderType, GitHubAuthProviderType>());
        return services;
    }

    /// <summary>
    /// 用代码里给定的选项接入 GitHub 登录:注册命名 HttpClient 并挂载 <see cref="IExternalAuthProvider"/>。
    /// 这是消费者用代码注册 provider 的路径,注册表里它优先于数据库中同 Code 的配置。
    /// </summary>
    public static IServiceCollection AddSmartAdminGitHubAuth(this IServiceCollection services, GitHubAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new InvalidOperationException("启用 GitHub 登录需配置 ClientId + ClientSecret。");

        services.AddSingleton(options);
        AddHttpClient(services);

        // 必须带 TImplementation=GitHubExternalAuthProvider:TryAddEnumerable 用 impl 类型去重;
        // 仅 Singleton<IExternalAuthProvider>(factory) 会把 impl 当成接口本身 → ArgumentException。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProvider, GitHubExternalAuthProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(GitHubExternalAuthProvider.HttpClientName);
            return new GitHubExternalAuthProvider(
                sp.GetRequiredService<GitHubAuthOptions>(),
                http,
                sp.GetRequiredService<ILogger<GitHubExternalAuthProvider>>());
        }));
        return services;
    }

    private static void AddHttpClient(IServiceCollection services) =>
        services.AddHttpClient(GitHubExternalAuthProvider.HttpClientName)
            .ConfigureHttpClient(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(15);
                // GitHub API 要求有效 User-Agent;无密钥
                GitHubExternalAuthProvider.EnsureDefaultUserAgent(c);
            });
}
