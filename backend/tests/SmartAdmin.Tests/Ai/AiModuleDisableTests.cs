using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// <c>Api:DisabledModules=["Ai"]</c> 整体关闭 AI 模块的契约:两个控制器路由消失,
/// 但 <see cref="IAiChatClient"/> 等服务仍可从 DI 解出——模块开关只砍路由(<c>DisabledModuleConvention</c>),
/// 不砍服务注册,消费者代码(比如 Pro 的 AI 审批)不依赖内置管理页也能继续调用网关。
/// </summary>
public class AiModuleDisableTests
{
    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    [Fact]
    public async Task Disabling_Ai_module_404s_both_controllers()
    {
        using var f = new AdminAppFactory { DisabledModules = ["Ai"] };
        var admin = await SuperAdminClient(f);

        var provider = await admin.GetAsync("/api/v1/sys/ai/provider/page");
        Assert.Equal(HttpStatusCode.NotFound, provider.StatusCode);

        var usage = await admin.GetAsync("/api/v1/sys/ai/usage/page?From=2026-01-01&To=2026-01-02");
        Assert.Equal(HttpStatusCode.NotFound, usage.StatusCode);
    }

    [Fact]
    public void Disabling_Ai_module_still_allows_resolving_IAiChatClient()
    {
        using var f = new AdminAppFactory { DisabledModules = ["Ai"] };
        using var scope = f.Services.CreateScope();

        // 模块开关只影响路由注册(DisabledModuleConvention),不影响 DI——消费者代码直接注入网关不受管理页开关限制
        var chatClient = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        Assert.NotNull(chatClient);
    }
}
