using Microsoft.IdentityModel.JsonWebTokens;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

public class TenantLoginTests
{
    [Fact]
    public async Task Seeded_super_admin_login_token_carries_default_tenant_and_platform_admin()
    {
        using var f = new AdminAppFactory();
        var c = f.CreateClient();

        var token = await c.LoginToken("superAdmin", "Test@123456");
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);

        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID.ToString(), jwt.GetClaim("tid").Value);
        Assert.Equal("true", jwt.GetClaim("padm").Value);
    }
}
