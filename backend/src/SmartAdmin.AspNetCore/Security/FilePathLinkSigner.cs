using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SmartAdmin.Core;
using SmartAdmin.Services;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// <see cref="IFilePathLinkSigner"/> 的 HMAC-SHA256 默认实现。
/// <para>签名密钥<b>不直接用签令牌的那把</b>,而是由它派生出一把子密钥(<c>HMAC(jwtKey, "smart:file-path-view")</c>),
/// 且与 <see cref="FileUrlSigner"/> 的子密钥不同:两个用途各用各的密钥材料,一边的链接换到另一边验不过。</para>
/// <para>载荷是 <c>{code}\n{规范化后的相对路径}\n{exp}</c>。路径里不可能有换行(规范化时已拒绝控制字符),
/// <c>exp</c> 又只有数字,所以从右往左切就能唯一还原三个字段,换一组 code 与路径拼出同一载荷是不可能的。</para>
/// </summary>
public class FilePathLinkSigner(SymmetricSecurityKey jwtKey, AdminFilePathOptions options, TimeProvider? time = null)
    : IFilePathLinkSigner
{
    /// <summary>派生用途标签:同一把主密钥下,不同用途走不同子密钥。</summary>
    private const string PURPOSE = "smart:file-path-view";

    private readonly byte[] _subKey = HMACSHA256.HashData(jwtKey.Key, Encoding.UTF8.GetBytes(PURPOSE));

    private TimeProvider Clock => time ?? TimeProvider.System;

    /// <inheritdoc />
    public virtual string BuildUrl(string code, string path, TimeSpan? ttl = null)
    {
        var lifetime = ttl ?? TimeSpan.FromMinutes(options.LinkTtlMinutes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        // 按秒累加而不是 DateTimeOffset.Add:TimeSpan.MaxValue 这样的超长有效期会让后者溢出抛异常
        var exp = Clock.GetUtcNow().ToUnixTimeSeconds() + (long)lifetime.TotalSeconds;
        var sig = Base64Url.EncodeToString(Sign(code, path, exp));
        return $"/api/v1/sys/file-path/download/{Uri.EscapeDataString(code)}/view?path={Uri.EscapeDataString(path)}&exp={exp}&sig={sig}";
    }

    /// <inheritdoc />
    public virtual bool Verify(string code, string path, long exp, string? signature)
    {
        // exp 直接按数值比较,不转成 DateTimeOffset:超大的 exp 转换会抛
        if (string.IsNullOrEmpty(signature) || exp < Clock.GetUtcNow().ToUnixTimeSeconds()) return false;

        string expected;
        try
        {
            expected = Base64Url.EncodeToString(Sign(code, path, exp));
        }
        catch (AdminException)
        {
            return false;   // 路径不合法:签发时就不会签出这样的链接
        }

        // 比较编码后的文本而不是解码再比字节:畸形、过长、非规范的写法都不等于期望值,不用解码也就不会抛;
        // 定长比较不给按前缀爆破留时间侧信道(长度不同直接 false,长度不是秘密)
        return CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(expected.AsSpan()), MemoryMarshal.AsBytes(signature.AsSpan()));
    }

    /// <summary>算签名原始字节。路径不合法抛 <see cref="ErrorCode.FilePathInvalidPath"/>。</summary>
    private byte[] Sign(string code, string path, long exp)
    {
        var relative = FilePathRules.NormalizeRelative(path, allowEmpty: false);
        var payload = string.Create(CultureInfo.InvariantCulture, $"{code}\n{relative}\n{exp}");
        return HMACSHA256.HashData(_subKey, Encoding.UTF8.GetBytes(payload));
    }
}
