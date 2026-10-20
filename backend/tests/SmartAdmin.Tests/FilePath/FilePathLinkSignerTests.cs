using System.Web;
using Microsoft.IdentityModel.Tokens;
using SmartAdmin.AspNetCore;
using SmartAdmin.Core;

namespace SmartAdmin.Tests;

/// <summary>
/// 文件路径签名直链:签出的链接能验过;code、路径、过期时刻任何一项被改都验不过;过期、畸形签名、非法路径一律返回 false 而不是抛异常。
/// </summary>
public class FilePathLinkSignerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero);

    private static FilePathLinkSigner NewSigner(FakeTimeProvider clock, int ttlMinutes = 60, byte key = 0) =>
        new(new SymmetricSecurityKey(Enumerable.Repeat(key, 32).ToArray()), new AdminFilePathOptions { LinkTtlMinutes = ttlMinutes }, clock);

    private static (long Exp, string Sig) Parse(string url)
    {
        var query = HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        return (long.Parse(query["exp"]!), query["sig"]!);
    }

    [Fact]
    public void Round_trip_verifies()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));

        var (exp, sig) = Parse(signer.BuildUrl("contracts", "2026/10/报价 单.pdf"));

        Assert.True(signer.Verify("contracts", "2026/10/报价 单.pdf", exp, sig));
    }

    [Fact]
    public void Url_has_the_expected_shape()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));

        var url = signer.BuildUrl("a b", "报价/单 1.pdf");

        Assert.StartsWith("/api/v1/sys/file-path/download/a%20b/view?path=" + Uri.EscapeDataString("报价/单 1.pdf") + "&exp=", url);
        var query = HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        Assert.Equal("报价/单 1.pdf", query["path"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", query["sig"]);   // SHA-256 的 Base64Url,无填充
    }

    [Fact]
    public void Default_and_explicit_lifetimes()
    {
        var signer = NewSigner(new FakeTimeProvider(Start), ttlMinutes: 15);
        var now = Start.ToUnixTimeSeconds();

        Assert.Equal(now + 15 * 60, Parse(signer.BuildUrl("c", "a.pdf")).Exp);
        Assert.Equal(now + 90, Parse(signer.BuildUrl("c", "a.pdf", TimeSpan.FromSeconds(90))).Exp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_lifetime_is_rejected(int seconds)
    {
        var signer = NewSigner(new FakeTimeProvider(Start));

        Assert.Throws<ArgumentOutOfRangeException>(() => signer.BuildUrl("c", "a.pdf", TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Huge_lifetime_does_not_overflow()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));

        var (exp, sig) = Parse(signer.BuildUrl("c", "a.pdf", TimeSpan.MaxValue));

        Assert.True(signer.Verify("c", "a.pdf", exp, sig));
    }

    [Fact]
    public void Tampering_any_part_fails()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));
        var (exp, sig) = Parse(signer.BuildUrl("contracts", "a/b.pdf"));
        Assert.True(signer.Verify("contracts", "a/b.pdf", exp, sig));

        Assert.False(signer.Verify("contract", "a/b.pdf", exp, sig));
        Assert.False(signer.Verify("contracts", "a/c.pdf", exp, sig));
        Assert.False(signer.Verify("contracts", "a/b.pdf", exp + 1, sig));
        Assert.False(signer.Verify("contracts", "a/b.pdf", exp, sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A')));
    }

    [Fact]
    public void Another_key_does_not_verify()
    {
        var clock = new FakeTimeProvider(Start);
        var (exp, sig) = Parse(NewSigner(clock, key: 0).BuildUrl("c", "a.pdf"));

        Assert.False(NewSigner(clock, key: 1).Verify("c", "a.pdf", exp, sig));
    }

    [Fact]
    public void Expired_link_fails()
    {
        var clock = new FakeTimeProvider(Start);
        var signer = NewSigner(clock, ttlMinutes: 10);
        var (exp, sig) = Parse(signer.BuildUrl("c", "a.pdf"));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(signer.Verify("c", "a.pdf", exp, sig));   // 到期那一秒仍有效

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(signer.Verify("c", "a.pdf", exp, sig));
    }

    [Fact]
    public void Backslash_path_signs_same_as_slash()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));

        var (exp, sig) = Parse(signer.BuildUrl("c", @"a\b.png"));

        Assert.True(signer.Verify("c", "a/b.png", exp, sig));
        Assert.True(signer.Verify("c", @"a\b.png", exp, sig));
    }

    [Theory]
    [InlineData("!!")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("A")]
    [InlineData("AAAA")]
    [InlineData("not base64url ä")]
    public void Malformed_signature_fails_without_throwing(string? signature)
    {
        var signer = NewSigner(new FakeTimeProvider(Start));
        var (exp, _) = Parse(signer.BuildUrl("c", "a.pdf"));

        Assert.False(signer.Verify("c", "a.pdf", exp, signature));
    }

    [Fact]
    public void Over_long_signature_fails_without_throwing()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));
        var (exp, sig) = Parse(signer.BuildUrl("c", "a.pdf"));

        Assert.False(signer.Verify("c", "a.pdf", exp, sig + sig));
        Assert.False(signer.Verify("c", "a.pdf", exp, new string('A', 100_000)));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void Extreme_exp_fails_without_throwing(long exp)
    {
        var signer = NewSigner(new FakeTimeProvider(Start));
        var (_, sig) = Parse(signer.BuildUrl("c", "a.pdf"));

        // exp 直接按数值和当前时刻比较,不转 DateTimeOffset(超大值转换会抛)
        Assert.False(signer.Verify("c", "a.pdf", exp, sig));
        Assert.False(signer.Verify("c", "a.pdf", exp, "AAAA"));
    }

    [Fact]
    public void Invalid_path_fails_verification_without_throwing()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));
        var (exp, sig) = Parse(signer.BuildUrl("c", "a.pdf"));

        foreach (var path in new[] { "../a.pdf", "/a.pdf", "a:b.pdf", "", "a.pdf.", "a//b.pdf" })
            Assert.False(signer.Verify("c", path, exp, sig));
        Assert.False(signer.Verify("c", null!, exp, sig));
    }

    [Fact]
    public void Signing_an_invalid_path_is_rejected()
    {
        var signer = NewSigner(new FakeTimeProvider(Start));

        var ex = Assert.Throws<AdminException>(() => signer.BuildUrl("c", "../a.pdf"));

        Assert.Equal(ErrorCode.FilePathInvalidPath, ex.Code);
    }
}
