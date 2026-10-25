using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.AspNetCore;

/// <summary>
/// 控制台上把 SQL 日志的类别标签挪到头行末尾:<c>info: 【查询】时间 … SmartAdmin.Sql[0]</c>。
/// 默认的 simple 格式让 <c>SmartAdmin.Sql[0]</c> 单独占一行,每个语句块都多一行,头行和语句也被它隔开。
/// <para>自己不渲染:整条交给内置 simple 格式写,时间戳、级别配色、作用域、异常都照旧,只挪标签那一段;
/// 认不出预期的形状(SingleLine 模式、将来 simple 改了排版)就原样输出,最坏退回默认的两行。其余类别直接转交。</para>
/// </summary>
internal sealed class SqlLogConsoleFormatter(IServiceProvider services) : ConsoleFormatter(NAME)
{
    public const string NAME = "smartadmin";

    /// <summary>simple 格式给续行补的缩进,即 <c>"info: "</c> 的宽度。</summary>
    private const string PADDING = "      ";

    // 惰性取:构造本类时 IEnumerable<ConsoleFormatter> 正在解析,自己也在里面,构造期去取就是循环依赖
    private ConsoleFormatter? _simple;

    private ConsoleFormatter Simple =>
        _simple ??= services.GetServices<ConsoleFormatter>().First(f => f.Name == ConsoleFormatterNames.Simple);

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (logEntry.Category != SqlLogFormatter.CATEGORY)
        {
            Simple.Write(logEntry, scopeProvider, textWriter);
            return;
        }

        using var buffer = new StringWriter();
        Simple.Write(logEntry, scopeProvider, buffer);
        var text = buffer.ToString();

        var tag = $"{logEntry.Category}[{logEntry.EventId.Id}]";
        var head = tag + Environment.NewLine + PADDING;
        var at = text.IndexOf(head, StringComparison.Ordinal);
        var lineEnd = at < 0 ? -1 : text.IndexOf(Environment.NewLine, at + head.Length, StringComparison.Ordinal);
        // 开了作用域时标签下面先是一行 "=> …",标签挪过去会挂在作用域后面,不如保持原样
        if (lineEnd < 0 || text.AsSpan(at + head.Length).StartsWith("=> ", StringComparison.Ordinal))
        {
            textWriter.Write(text);
            return;
        }
        textWriter.Write(text[..at] + text[(at + head.Length)..lineEnd] + " " + tag + text[lineEnd..]);
    }
}
