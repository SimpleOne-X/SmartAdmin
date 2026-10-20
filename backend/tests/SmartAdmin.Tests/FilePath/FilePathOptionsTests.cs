using Microsoft.Extensions.DependencyInjection;
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

        Assert.True(Path.IsPathFullyQualified(staging), $"暂存目录应已解析为绝对路径,实际 {staging}");
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
}
