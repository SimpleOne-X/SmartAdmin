using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartAdmin.Core;

namespace SmartAdmin.Auth.DingTalk;

/// <summary>
/// 钉钉登录装配(可选包入口)。在 <c>AddSmartAdmin()</c> <b>之前</b>调用,把钉钉「类型描述」并入
/// <see cref="IExternalAuthProviderType"/> 集合;连接配置由管理员在「系统配置 → 登录方式」填写并加密入库。
/// </summary>
public static class DingTalkSetup
{
    /// <summary>
    /// 启用钉钉登录:注册命名 <see cref="HttpClient"/>(超时 15 秒)与 <see cref="DingTalkAuthProviderType"/>。
    /// 不读配置、不抛异常;填好连接配置之前登录页不出现钉钉入口。
    /// </summary>
    public static IServiceCollection AddSmartAdminDingTalkAuth(this IServiceCollection services)
    {
        services.AddHttpClient(DingTalkExternalAuthProvider.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProviderType, DingTalkAuthProviderType>());
        return services;
    }

    /// <summary>
    /// 在代码里显式用给定配置注册钉钉 provider(不看数据库)。它是消费者代码注册的 <see cref="IExternalAuthProvider"/>,
    /// 与库里配置的同 Code(<c>dingtalk</c>)provider 冲突时,本注册优先。
    /// </summary>
    public static IServiceCollection AddSmartAdminDingTalkAuth(this IServiceCollection services, DingTalkAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppKey) || string.IsNullOrWhiteSpace(options.AppSecret))
            throw new InvalidOperationException("启用钉钉登录需配置 AppKey + AppSecret。");

        services.AddSingleton(options);
        services.AddHttpClient(DingTalkExternalAuthProvider.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));

        // 与 GitHub 相同:TryAddEnumerable + TImplementation,工厂注入命名 HttpClient
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExternalAuthProvider, DingTalkExternalAuthProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(DingTalkExternalAuthProvider.HttpClientName);
            return new DingTalkExternalAuthProvider(
                sp.GetRequiredService<DingTalkAuthOptions>(),
                http,
                sp.GetRequiredService<ILogger<DingTalkExternalAuthProvider>>());
        }));
        return services;
    }
}
