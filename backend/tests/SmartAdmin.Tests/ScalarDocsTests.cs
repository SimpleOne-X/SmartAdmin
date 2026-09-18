using Microsoft.Extensions.DependencyInjection;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

public class ScalarDocsTests
{
    [Fact]
    public void AdminScalarOptions_defaults_to_disabled_and_resolves_via_DI()
    {
        var services = new ServiceCollection();
        services.AddSmartAdminOptions(new SmartAdminOptions());
        using var sp = services.BuildServiceProvider();

        var scalar = sp.GetRequiredService<AdminScalarOptions>();

        Assert.False(scalar.EnabledInProduction);
    }
}
