using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>
/// 安全选项的启动期取值校验。<c>DelegatedGrantMaxDays</c> 负数若被当成「不限」会在配错时放行,
/// 过大则让到期上限的 <c>AddDays</c> 越界抛异常、普通管理员每次保存都 500,所以两头都在启动时拒掉。
/// API 宿主与独立 Worker 共用同一个校验入口,两侧都要拒。
/// </summary>
public class SecurityOptionsValidationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(3651)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_delegated_grant_max_days_is_rejected(int days)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AdminSecurityOptionsValidation.Validate(new AdminSecurityOptions { DelegatedGrantMaxDays = days }));

        Assert.Contains("DelegatedGrantMaxDays", ex.Message);
        Assert.Contains(days.ToString(), ex.Message);
        Assert.Contains("3650", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(3650)]
    public void In_range_delegated_grant_max_days_passes(int days) =>
        AdminSecurityOptionsValidation.Validate(new AdminSecurityOptions { DelegatedGrantMaxDays = days });

    [Fact]
    public void Default_security_options_pass() => AdminSecurityOptionsValidation.Validate(new AdminSecurityOptions());

    /// <summary>API 宿主的组合根调到了校验:配置里写 -1 或 3651,AddSmartAdmin 当场抛。</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("3651")]
    public void AddSmartAdmin_rejects_out_of_range_delegated_grant_max_days(string value)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SmartAdmin:Database:DbType"] = "Sqlite",
            ["SmartAdmin:Database:ConnectionString"] = "Data Source=:memory:",
            ["SmartAdmin:Jwt:SecretKey"] = "smart-security-validation-test-signing-key-32plus",
            ["SmartAdmin:Id:WorkerId"] = "3",
            ["SmartAdmin:Security:DelegatedGrantMaxDays"] = value,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Services.AddSmartAdmin(builder.Configuration));
        Assert.Contains("DelegatedGrantMaxDays", ex.Message);
    }

    /// <summary>Worker 的组合根同样调到了校验,不能只有 API 侧才发现配错。</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("3651")]
    public void AddSmartAdminWorker_rejects_out_of_range_delegated_grant_max_days(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SmartAdmin:Database:DbType"] = "Sqlite",
            ["SmartAdmin:Database:ConnectionString"] = "Data Source=ignored.db",
            ["SmartAdmin:Database:EnableCodeFirst"] = "false",
            ["SmartAdmin:Database:EnableSeed"] = "false",
            ["SmartAdmin:Id:WorkerId"] = "7",
            ["SmartAdmin:Security:DelegatedGrantMaxDays"] = value,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSmartAdminWorker(configuration));
        Assert.Contains("DelegatedGrantMaxDays", ex.Message);
    }
}
