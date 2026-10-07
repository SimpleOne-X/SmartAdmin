using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SmartAdmin.Core;

namespace SmartAdmin.Services;

/// <summary>
/// 本地签名直链(<see cref="IFileUrlSigner.BuildUrl"/> 产出的 <c>/api/v1/sys/file/{id}/view?sig=…[&amp;exp=…]</c>)的解析与续签。
/// 存进配置的直链在部署配了 <c>SignedUrlTtlMinutes</c> 时会过期;下发前按文件 Id 现签一份,链接就不会坏。
/// </summary>
public static partial class LocalFileUrl
{
    [GeneratedRegex(@"^/api/v1/sys/file/(\d+)/view\?sig=([A-Za-z0-9_-]+)(?:&exp=(\d+))?$")]
    private static partial Regex Pattern();

    /// <summary>
    /// 若 <paramref name="url"/> 是本站签发过的签名直链(签名对得上,<b>不论是否已过期</b>),返回按当前签名策略新签的直链;
    /// 否则原样返回(外部 URL、空值、伪造签名都不改)。
    /// <para>先验签再续签:否则任何能写配置的人填个 <c>/file/{任意 id}/view?sig=x</c>,就能让匿名端点替他签出任意文件的直链。</para>
    /// </summary>
    public static string? Refresh(string? url, IFileUrlSigner? signer)
    {
        if (signer is null || string.IsNullOrEmpty(url)) return url;
        var m = Pattern().Match(url);
        if (!m.Success) return url;

        var fileId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        DateTimeOffset? expiresAt = m.Groups[3].Success
            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
        var expected = Encoding.ASCII.GetBytes(signer.Sign(fileId, expiresAt));
        var provided = Encoding.ASCII.GetBytes(m.Groups[2].Value);
        return CryptographicOperations.FixedTimeEquals(expected, provided) ? signer.BuildUrl(fileId) : url;
    }
}
