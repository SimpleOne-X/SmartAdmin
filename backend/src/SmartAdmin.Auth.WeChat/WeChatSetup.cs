using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.WeChat;

/// <summary>微信登录装配:在 <c>AddSmartAdmin()</c> 之前调用。</summary>
public static class WeChatSetup
{
    /// <summary>
    /// 接入微信登录:注册命名 HttpClient 与 <see cref="IExternalAuthProviderType"/> 类型描述。
    /// 不读取配置、不抛异常;AppId / AppSecret 由管理员在 系统配置 → 登录方式 填写,加密保存到数据库,
    /// 填写完整后登录页才出现微信入口。同 Code 的 <see cref="IExternalAuthProvider"/> 若已由代码注册
    /// (见带 <see cref="WeChatAuthOptions"/> 参数的重载),以代码注册的为准。
    /// </summary>
    public static IServiceCollection AddSmartAdminWeChatAuth(this IServiceCollection services)
    {
        AddHttpClient(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProviderType, WeChatAuthProviderType>());
        return services;
    }

    /// <summary>
    /// 用代码里给定的选项接入微信登录:注册命名 HttpClient 并挂载 <see cref="IExternalAuthProvider"/>。
    /// 这是消费者用代码注册 provider 的路径,注册表里它优先于数据库中同 Code 的配置。
    /// </summary>
    public static IServiceCollection AddSmartAdminWeChatAuth(this IServiceCollection services, WeChatAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.AppSecret))
            throw new InvalidOperationException("启用微信登录需配置 AppId + AppSecret。");

        services.AddSingleton(options);
        AddHttpClient(services);

        // 必须带 TImplementation=WeChatExternalAuthProvider(同 WeCom TryAddEnumerable 成法);
        // 仅 Singleton<IExternalAuthProvider>(factory) → ArgumentException,装包后 0 个 provider。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProvider, WeChatExternalAuthProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(WeChatExternalAuthProvider.HttpClientName);
            return new WeChatExternalAuthProvider(
                sp.GetRequiredService<WeChatAuthOptions>(),
                http,
                sp.GetRequiredService<ILogger<WeChatExternalAuthProvider>>());
        }));
        return services;
    }

    private static void AddHttpClient(IServiceCollection services) =>
        services.AddHttpClient(WeChatExternalAuthProvider.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
}
