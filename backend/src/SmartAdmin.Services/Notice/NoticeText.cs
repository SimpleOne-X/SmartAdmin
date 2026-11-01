using System.Text.RegularExpressions;

namespace SmartAdmin.Services;

/// <summary>
/// 通知正文转换:Markdown 转纯文本、取第一个站内链接,供只认纯文本的外部渠道(如企业微信文字卡片)使用。
/// 只处理通知正文里常见的写法,不是完整的 Markdown 解析;下划线不处理,免得吃掉 <c>user_name</c> 这类标识符。
/// </summary>
public static class NoticeText
{
    // NonBacktracking 保证线性时间:正文由发布人填,"[a](x" 重复很多次这类输入不能拖慢发布线程。
    // 一条正则同时认链接和图片(组 1 为 "!" 表示图片),FirstSiteLink 才能跳过图片。
    private static readonly Regex LinkOrImage =
        new(@"(!?)\[([^\]]*)\]\(([^)\s]*)\)", RegexOptions.NonBacktracking);

    private static readonly Regex LinePrefix =
        new(@"^(?:#{1,6}[ \t]+|>[ \t]*|-[ \t]+)+", RegexOptions.NonBacktracking);

    /// <summary>
    /// 只接受以<b>单个</b> <c>/</c> 开头的站内相对路径。<c>//host</c> 与 <c>/\host</c> 是协议相对 URL,
    /// 浏览器会跳出站;控制字符与空白也拒绝,免得存进去的 url 在前端拼不出请求。
    /// </summary>
    public static bool IsSiteRelativeUrl(string url)
    {
        if (url.Length < 1 || url[0] != '/') return false;
        if (url.Length > 1 && url[1] is '/' or '\\') return false;
        foreach (var ch in url)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\') return false;
        return true;
    }

    /// <summary>链接与图片只留文字,去掉行首的 <c>#</c>、<c>&gt;</c>、<c>-</c> 以及 <c>*</c>、<c>`</c>,空行丢弃。</summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var text = LinkOrImage.Replace(markdown, m => m.Groups[2].Value);
        var lines = text.Split('\n')
            .Select(l => LinePrefix.Replace(l.Replace("*", "").Replace("`", "").Trim(), "").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>第一个目标是站内相对路径的链接;外链、协议相对链接和图片都跳过。</summary>
    public static string? FirstSiteLink(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return null;
        foreach (Match m in LinkOrImage.Matches(markdown))
            if (m.Groups[1].Length == 0 && IsSiteRelativeUrl(m.Groups[3].Value))
                return m.Groups[3].Value;
        return null;
    }
}
