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

    /// <summary>
    /// 回归:刷新令牌换发的新 accessToken 必须与登录令牌携带相同的 tid/padm——
    /// SessionService.RefreshAsync 曾遗漏 TokenSubject 的这两个尾参,刷新后静默丢失租户/平台管理员身份。
    /// </summary>
    [Fact]
    public async Task Refreshed_access_token_carries_same_tenant_and_platform_admin_claims_as_login()
    {
        using var f = new AdminAppFactory();
        var c = f.CreateClient();

        var loginData = (await (await c.PostJson("/api/v1/auth/login",
            new { account = "superAdmin", password = "Test@123456" })).ReadEnvelope()).GetProperty("data");
        var loginToken = loginData.GetProperty("accessToken").GetString()!;
        var refreshToken = loginData.GetProperty("refreshToken").GetString()!;

        var handler = new JsonWebTokenHandler();
        var loginJwt = handler.ReadJsonWebToken(loginToken);
        // 登录令牌本身必须携带这两个 claim,否则下面的"相同"断言在两边都为空时无意义地通过
        Assert.Equal(DefaultTenantSeed.DEFAULT_TENANT_ID.ToString(), loginJwt.GetClaim("tid").Value);
        Assert.Equal("true", loginJwt.GetClaim("padm").Value);

        var refreshedData = (await (await c.PostJson("/api/v1/auth/refresh",
            new { refreshToken })).ReadEnvelope()).GetProperty("data");
        var refreshedJwt = handler.ReadJsonWebToken(refreshedData.GetProperty("accessToken").GetString()!);

        Assert.Equal(loginJwt.GetClaim("tid").Value, refreshedJwt.GetClaim("tid").Value);
        Assert.Equal(loginJwt.GetClaim("padm").Value, refreshedJwt.GetClaim("padm").Value);
    }
}
