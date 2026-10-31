using System.Diagnostics;
using SmartAdmin.Services;

namespace SmartAdmin.Tests;

/// <summary>通知正文转换:Markdown 转纯文本、取第一个站内链接,以及站内相对路径判定。</summary>
public class NoticeTextTests
{
    [Theory]
    [InlineData("/a", true)]
    [InlineData("/flow/todo?taskId=1", true)]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("//evil.com", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("/a b", false)]
    [InlineData("/a\tb", false)]
    [InlineData("https://x", false)]
    public void IsSiteRelativeUrl_rules(string url, bool expected) =>
        Assert.Equal(expected, NoticeText.IsSiteRelativeUrl(url));

    [Fact]
    public void ToPlainText_strips_markdown()
    {
        Assert.Equal("标题\n粗 码\n前往审批",
            NoticeText.ToPlainText("# 标题\n\n**粗** `码`\n\n\n[前往审批](/flow/todo?taskId=1)"));
        Assert.Equal("引用\n条目\n图", NoticeText.ToPlainText("> 引用\r\n- 条目\r\n![图](/img.png)"));
        Assert.Equal("user_name", NoticeText.ToPlainText("user_name"));
        Assert.Equal("", NoticeText.ToPlainText(null));
        Assert.Equal("", NoticeText.ToPlainText("   "));
    }

    [Fact]
    public void ToPlainText_stays_linear_on_unclosed_links()
    {
        var body = string.Concat(Enumerable.Repeat("[a](x", 40_000));
        var sw = Stopwatch.StartNew();
        NoticeText.ToPlainText(body);
        NoticeText.FirstSiteLink(body);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {sw.Elapsed}");
    }

    [Fact]
    public void FirstSiteLink_skips_external_links()
    {
        Assert.Equal("/a", NoticeText.FirstSiteLink("[外](https://x) [内](/a) [二](/b)"));
        Assert.Null(NoticeText.FirstSiteLink("[协议相对](//evil.com)"));
        Assert.Equal("/a", NoticeText.FirstSiteLink("![图](/img.png) [内](/a)"));
        Assert.Null(NoticeText.FirstSiteLink(null));
    }
}
