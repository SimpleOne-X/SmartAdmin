namespace SmartAdmin.SqlSugar;

/// <summary>
/// 带颜色的日志参数值:<see cref="ToString"/> 是纯文本,<see cref="Ansi"/> 是嵌了 ANSI 码的版本。
/// <para>同一条日志会发给所有日志提供程序,VS 输出窗口(Debug)、文件、事件日志、Seq 都不认 ANSI 码,
/// 所以交出去的默认是纯文本;只有内核的控制台格式化器(<c>smartadmin</c>)认得本类型,写控制台时换成上色版。</para>
/// </summary>
public sealed class ColoredText(string plain, Func<string> ansi)
{
    /// <summary>上色版。用到才算:语句排版按长度计费,只有控制台那一路会取。</summary>
    public string Ansi => ansi();

    public override string ToString() => plain;
}
