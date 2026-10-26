using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 内核接管的控制台格式,在内置 simple 格式上改两处:
/// <list type="number">
/// <item>SQL 日志的类别标签挪到头行末尾:<c>info: 【查询】时间 … SmartAdmin.Sql[0]</c>。
/// 默认的 simple 格式让 <c>SmartAdmin.Sql[0]</c> 单独占一行,每个语句块都多一行,头行和语句也被它隔开。</item>
/// <item>每条日志(所有级别、所有类别)写完跟一条灰色分割线。多行的 SQL 块和带堆栈的异常首尾相接时,
/// 分不清一条在哪结束、下一条从哪开始。只在上色时画:输出被重定向(容器日志、写文件)时按行采集,
/// 多出的横线每条都会变成一条无用事件。</item>
/// </list>
/// <para>SQL 日志的颜色也在这里上:参数值是 <see cref="ColoredText"/> 时按消息模板重拼一遍,换成上色版。
/// 消息串本身是纯文本,发给 VS 输出窗口、文件的那几路因此不带转义序列。</para>
/// <para>自己不渲染:整条交给内置 simple 格式写,时间戳、级别配色、作用域、异常都照旧;
/// 标签认不出预期的形状(SingleLine 模式、将来 simple 改了排版)就原样输出,最坏退回默认的两行。</para>
/// </summary>
internal sealed partial class SqlLogConsoleFormatter(IServiceProvider services, IOptionsMonitor<SimpleConsoleFormatterOptions> simpleOptions)
    : ConsoleFormatter(NAME)
{
    public const string NAME = "smartadmin";

    /// <summary>simple 格式给续行补的缩进,即 <c>"info: "</c> 的宽度。</summary>
    private const string PADDING = "      ";

    /// <summary>分割线:续行缩进加语句排版的目标行宽,盖住绝大多数 SQL 行;不跟终端宽度走,重定向时拿不到宽度。</summary>
    private static readonly string Rule = "\u001b[90m" + new string('─', PADDING.Length + 100) + "\u001b[0m" + Environment.NewLine;

    // 惰性取:构造本类时 IEnumerable<ConsoleFormatter> 正在解析,自己也在里面,构造期去取就是循环依赖
    private ConsoleFormatter? _simple;

    private ConsoleFormatter Simple =>
        _simple ??= services.GetServices<ConsoleFormatter>().First(f => f.Name == ConsoleFormatterNames.Simple);

    /// <summary>和 simple 格式上不上色同一个判断:没配就看输出是否被重定向。</summary>
    private bool Colored => simpleOptions.CurrentValue.ColorBehavior switch
    {
        LoggerColorBehavior.Enabled => true,
        LoggerColorBehavior.Disabled => false,
        _ => !Console.IsOutputRedirected,
    };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        using var buffer = new StringWriter();
        if (logEntry.Category == SqlLogFormatter.CATEGORY && AnsiMessage(logEntry.State) is { } ansi)
            Simple.Write(new LogEntry<TState>(logEntry.LogLevel, logEntry.Category, logEntry.EventId, logEntry.State, logEntry.Exception, (_, _) => ansi), scopeProvider, buffer);
        else
            Simple.Write(logEntry, scopeProvider, buffer);
        var text = buffer.ToString();
        if (text.Length == 0) return;   // simple 对没有消息也没有异常的条目什么都不写,也就不画线

        if (logEntry.Category == SqlLogFormatter.CATEGORY) text = MoveTag(text, $"{logEntry.Category}[{logEntry.EventId.Id}]");
        textWriter.Write(Colored ? text + Rule : text);
    }

    /// <summary>
    /// 按 <c>{OriginalFormat}</c> 把占位符换回参数值,<see cref="ColoredText"/> 取上色版;没有这种参数就返回 null,照常格式化。
    /// 只认 <c>{Name}</c> 形式的占位符:带颜色参数的只有 <see cref="SqlLogFormatter"/> 的两个模板,都不带格式串和花括号转义。
    /// </summary>
    private static string? AnsiMessage<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values
            || !values.Any(v => v.Value is ColoredText)
            || values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is not string template) return null;

        return Hole().Replace(template, m => values.FirstOrDefault(v => v.Key == m.Groups[1].Value).Value switch
        {
            ColoredText t => t.Ansi,
            var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        });
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Hole();

    private static string MoveTag(string text, string tag)
    {
        var head = tag + Environment.NewLine + PADDING;
        var at = text.IndexOf(head, StringComparison.Ordinal);
        var lineEnd = at < 0 ? -1 : text.IndexOf(Environment.NewLine, at + head.Length, StringComparison.Ordinal);
        // 开了作用域时标签下面先是一行 "=> …",标签挪过去会挂在作用域后面,不如保持原样
        if (lineEnd < 0 || text.AsSpan(at + head.Length).StartsWith("=> ", StringComparison.Ordinal)) return text;
        return text[..at] + text[(at + head.Length)..lineEnd] + " " + tag + text[lineEnd..];
    }
}
