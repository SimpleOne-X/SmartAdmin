using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.WeCom;

/// <summary>
/// 企业微信登录装配(可选包入口)。在 <c>AddSmartAdmin()</c> <b>之前</b>调用,把企业微信「类型描述」并入
/// <see cref="IExternalAuthProviderType"/> 集合;连接配置由管理员在「系统配置 → 登录方式」填写并加密入库。
/// </summary>
public static class WeComSetup
{
    /// <summary>
    /// 启用企业微信登录:注册命名 <see cref="HttpClient"/>(超时 15 秒)与 <see cref="WeComAuthProviderType"/>。
    /// 不读配置、不抛异常;填好连接配置之前登录页不出现企业微信入口。
    /// </summary>
    public static IServiceCollection AddSmartAdminWeComAuth(this IServiceCollection services)
    {
        services.AddHttpClient(WeComExternalAuthProvider.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProviderType, WeComAuthProviderType>());
        return services;
    }

    /// <summary>
    /// 在代码里显式用给定配置注册企业微信 provider(不看数据库)。它是消费者代码注册的 <see cref="IExternalAuthProvider"/>,
    /// 与库里配置的同 Code(<c>wecom</c>)provider 冲突时,本注册优先。
    /// </summary>
    public static IServiceCollection AddSmartAdminWeComAuth(this IServiceCollection services, WeComAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CorpId)
            || string.IsNullOrWhiteSpace(options.CorpSecret)
            || string.IsNullOrWhiteSpace(options.AgentId))
            throw new InvalidOperationException(
                "启用企业微信登录需配置 CorpId + AgentId + CorpSecret。");

        services.AddSingleton(options);
        services.AddHttpClient(WeComExternalAuthProvider.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));

        // 与 GitHub 相同:TryAddEnumerable + TImplementation,工厂注入命名 HttpClient
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProvider, WeComExternalAuthProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(WeComExternalAuthProvider.HttpClientName);
            return new WeComExternalAuthProvider(
                sp.GetRequiredService<WeComAuthOptions>(),
                http,
                sp.GetRequiredService<ILogger<WeComExternalAuthProvider>>());
        }));
        return services;
    }
}
