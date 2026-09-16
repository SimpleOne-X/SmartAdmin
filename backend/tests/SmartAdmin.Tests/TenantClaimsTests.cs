using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>租户 claim 的签发与回读——不经 HTTP,直接对 JwtTokenProvider + HttpContextCurrentUser 两端验证。</summary>
public class TenantClaimsTests
{
    [Fact]
    public void BuildClaims_includes_tid_and_padm_when_subject_has_them()
    {
        var provider = new JwtTokenProvider(
            new AdminJwtOptions { Issuer = "test", ExpireMinutes = 30, RefreshExpireMinutes = 60 },
            new SymmetricSecurityKey(RandomNumberGenerator_GetBytes32()),
            TimeProvider.System);

        var pair = provider.Create(new TokenSubject(1, "acc", "sid", IsSuperAdmin: true, OrgId: null, TenantId: 77, IsPlatformAdmin: true));

        var handler = new JsonWebTokenHandler();
        var token = handler.ReadJsonWebToken(pair.AccessToken);
        Assert.Equal("77", token.GetClaim(TokenClaimNames.TENANT_ID).Value);
        Assert.Equal("true", token.GetClaim(TokenClaimNames.PLATFORM_ADMIN).Value);
    }

    [Fact]
    public void BuildClaims_omits_tid_and_padm_when_subject_has_neither()
    {
        var provider = new JwtTokenProvider(
            new AdminJwtOptions { Issuer = "test", ExpireMinutes = 30, RefreshExpireMinutes = 60 },
            new SymmetricSecurityKey(RandomNumberGenerator_GetBytes32()),
            TimeProvider.System);

        var pair = provider.Create(new TokenSubject(1, "acc", "sid"));

        var token = new JsonWebTokenHandler().ReadJsonWebToken(pair.AccessToken);
        Assert.DoesNotContain(token.Claims, c => c.Type == TokenClaimNames.TENANT_ID);
        Assert.DoesNotContain(token.Claims, c => c.Type == TokenClaimNames.PLATFORM_ADMIN);
    }

    [Fact]
    public void HttpContextCurrentUser_reads_tid_and_padm_claims()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(TokenClaimNames.TENANT_ID, "42"),
            new Claim(TokenClaimNames.PLATFORM_ADMIN, "true"),
        ], authenticationType: "test");
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };

        var currentUser = new HttpContextCurrentUser(accessor);
        Assert.Equal(42, currentUser.TenantId);
        Assert.True(currentUser.IsPlatformAdmin);
    }

    private static byte[] RandomNumberGenerator_GetBytes32() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
