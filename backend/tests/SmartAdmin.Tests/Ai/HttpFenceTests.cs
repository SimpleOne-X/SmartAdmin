using System.Net;
using System.Net.Http;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 从 <see cref="JobHttpFence"/> 抽出的共享 <see cref="HttpFence"/>:抽取前后行为一致
/// (<see cref="JobSecurityTests"/> 对 <c>JobHttpFence.ValidateUrl/IsBlocked/TryParseCidr</c> 的既有覆盖,
/// 现在跑的就是转发到 HttpFence 之后的代码路径,同样必须全绿)。本类只补两点 JobHttpFence 没有的面:
/// 错误码可参数化(AI 网关要抛 49005 而不是 47009),以及 <see cref="HttpFenceOptions.Proxy"/> 分支。
/// </summary>
public class HttpFenceTests
{
    private static readonly HttpFenceOptions DefaultOptions = new();

    [Fact]
    public void ValidateUrl_throws_the_caller_supplied_error_code()
    {
        var ex = Assert.Throws<AdminException>(
            () => HttpFence.ValidateUrl("http://169.254.169.254/", DefaultOptions, ErrorCode.AiBaseUrlBlocked));
        Assert.Equal(ErrorCode.AiBaseUrlBlocked, ex.Code);
    }

    [Fact]
    public void ValidateUrl_passes_ordinary_targets()
    {
        HttpFence.ValidateUrl("https://api.deepseek.com/v1", DefaultOptions, ErrorCode.AiBaseUrlBlocked);
        HttpFence.ValidateUrl("http://localhost:11434/v1", DefaultOptions, ErrorCode.AiBaseUrlBlocked); // Ollama 预设,回环不封云元数据但也不封 localhost 之外的私网……
    }

    [Theory]
    [InlineData("http://169.254.169.254/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public void ValidateUrl_rejects_the_same_shapes_JobHttpFence_rejects(string url) =>
        Assert.Throws<AdminException>(() => HttpFence.ValidateUrl(url, DefaultOptions, ErrorCode.AiBaseUrlBlocked));

    [Fact]
    public void Allowed_hosts_whitelist_is_fail_closed()
    {
        var options = new HttpFenceOptions { AllowedHosts = ["ok.example.com"] };
        HttpFence.ValidateUrl("https://ok.example.com/x", options, ErrorCode.AiBaseUrlBlocked);
        Assert.Throws<AdminException>(() => HttpFence.ValidateUrl("https://evil.example.com/x", options, ErrorCode.AiBaseUrlBlocked));
    }

    [Theory]
    [InlineData("169.254.0.0/16", "169.254.169.254", true)]
    [InlineData("169.254.0.0/16", "169.253.1.1", false)]
    [InlineData("fd00:ec2::/32", "fd00:ec2::254", true)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void IsBlocked_matches_JobHttpFence_semantics(string cidr, string ip, bool expected) =>
        Assert.Equal(expected, HttpFence.IsBlocked(IPAddress.Parse(ip), [cidr]));

    [Theory]
    [InlineData("169.254.169.254", true)]   // 无斜杠 = 单地址
    [InlineData("169.254.0.0/33", false)]
    [InlineData("not-an-ip/16", false)]
    public void TryParseCidr_matches_JobHttpFence_semantics(string cidr, bool valid) =>
        Assert.Equal(valid, HttpFence.TryParseCidr(cidr, out _, out _));

    [Fact]
    public void CreateHandler_without_proxy_fences_via_connect_callback()
    {
        using var handler = HttpFence.CreateHandler(DefaultOptions);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void CreateHandler_with_proxy_routes_through_it_and_skips_connect_callback()
    {
        // 配置了 Proxy 就不再挂 ConnectCallback——围栏对代理链天然失效,是操作者的主动选择(§7.4)。
        using var handler = HttpFence.CreateHandler(new HttpFenceOptions { Proxy = "http://proxy.internal:8080" });
        Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy);
        Assert.Null(handler.ConnectCallback);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Fenced_client_refuses_to_connect_to_a_blocked_address()
    {
        // 端到端:真用围栏 handler 发一次请求,确认 ConnectCallback 真的会拒连(不只是校验函数说"拒",实际连接也被拦)。
        using var client = new HttpClient(HttpFence.CreateHandler(DefaultOptions)) { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data/"));
    }
}
