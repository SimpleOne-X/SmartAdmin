namespace SmartAdmin.Core;

/// <summary>
/// 文件路径的签名直链:给「编码 + 相对路径」签出<b>不可伪造但可匿名访问</b>的 URL,让 <c>&lt;img src&gt;</c> 这类
/// 带不了 Authorization 头的场景也能取到文件夹里的文件。与 <see cref="IFileUrlSigner"/> 互相独立:密钥材料、载荷、链接格式都不同。
/// <para>这是一条能力链接:拿得到链接就读得到文件,所以<b>一定带过期时刻</b>(编进签名,改不了也伪造不了),
/// 不像 <see cref="IFileUrlSigner"/> 那样默认永久。撤销手段:等它过期,或轮换 <c>Jwt:SecretKey</c>(全部直链一起失效)。</para>
/// </summary>
public interface IFilePathLinkSigner
{
    /// <summary>
    /// 签出可直接塞进 <c>&lt;img src&gt;</c> 的相对 URL(含 <c>path</c>、<c>exp</c>、<c>sig</c>)。
    /// <paramref name="ttl"/> 缺省取配置的 <c>SmartAdmin:FilePath:LinkTtlMinutes</c>。
    /// 路径不合法抛 <see cref="ErrorCode.FilePathInvalidPath"/>:签出去的链接必然读不到文件,不如当场报错。
    /// </summary>
    string BuildUrl(string code, string path, TimeSpan? ttl = null);

    /// <summary>
    /// 校验链接。<paramref name="exp"/> 是 Unix 秒;签名须匹配同一组 code、路径、<paramref name="exp"/>,且当前时刻未超过 <paramref name="exp"/>。
    /// 路径按签发时的规则规范化,所以 <c>a\b.png</c> 与 <c>a/b.png</c> 等价。
    /// <b>绝不抛异常</b>:路径不合法、签名不是合法 Base64Url、长度不对、<paramref name="exp"/> 取任何值,一律返回 <c>false</c>。
    /// </summary>
    bool Verify(string code, string path, long exp, string? signature);
}
