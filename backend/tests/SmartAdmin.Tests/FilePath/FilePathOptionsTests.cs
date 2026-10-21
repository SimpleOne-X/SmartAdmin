using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>文件路径配置节:暂存目录的解析基准与启动期校验。</summary>
public class FilePathOptionsTests
{
    [Fact]
    public void Relative_staging_path_resolves_against_content_root()
    {
        using var factory = new AdminAppFactory();

        var staging = factory.Services.GetRequiredService<AdminFilePathOptions>().StagingPath;
        var contentRoot = factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;

        Assert.True(Path.IsPathFullyQualified(staging), $"暂存目录应已解析为绝对路径,实际 {staging}");
        Assert.StartsWith(contentRoot, staging);
        Assert.EndsWith("file-path-staging", staging);
    }

    [Fact]
    public void Relative_allowed_root_fails_startup() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSmartAdminOptions(
            new SmartAdminOptions { FilePath = { AllowedRoots = ["rel/dir"] } }));

    [Fact]
    public void Non_positive_ttl_fails_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSmartAdminOptions(
            new SmartAdminOptions { FilePath = { LinkTtlMinutes = 0 } }));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSmartAdminOptions(
            new SmartAdminOptions { FilePath = { StagingTtlHours = 0 } }));
    }

    /// <summary>按给定的内容根目录解析文件路径配置节(暂存目录在解析时才按内容根目录落成绝对路径)。</summary>
    private static AdminFilePathOptions Resolve(string contentRoot, AdminFilePathOptions filePath)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { ContentRootPath = contentRoot })
            .AddSmartAdminOptions(new SmartAdminOptions { FilePath = filePath });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<AdminFilePathOptions>();
    }

    [Fact]
    public void Staging_path_under_an_allowed_root_fails_startup()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"smart-fp-app-{Guid.NewGuid():N}");
        var share = Path.Combine(Path.GetTempPath(), $"smart-fp-share-{Guid.NewGuid():N}");

        // 绝对路径直接落在白名单根下,或者就是根本身
        foreach (var staging in new[] { Path.Combine(share, "staging"), share })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Resolve(contentRoot, new AdminFilePathOptions { AllowedRoots = [share], StagingPath = staging }));
            Assert.Contains("StagingPath", ex.Message);
        }

        // 相对路径要按内容根目录解析后再比:默认的 ./data/file-path-staging 落在被放进白名单的内容根目录下
        Assert.Throws<InvalidOperationException>(() =>
            Resolve(contentRoot, new AdminFilePathOptions { AllowedRoots = [contentRoot] }));

        // 前缀相同的兄弟目录不算在根下
        var sibling = Resolve(contentRoot, new AdminFilePathOptions { AllowedRoots = [share], StagingPath = share + "-staging" });
        Assert.Equal(share + "-staging", sibling.StagingPath);
    }
}
