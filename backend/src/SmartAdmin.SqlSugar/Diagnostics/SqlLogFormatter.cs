using System.Globalization;
using System.Text;
using SmartAdmin.Core;
using SqlSugar;

namespace SmartAdmin.SqlSugar;

/// <summary>
/// SQL 控制台日志的渲染。参数<b>内联进语句</b>——排查时要的是一条能直接贴进客户端跑的 SQL,
/// 而不是"语句在这里、参数在那里"再人肉拼。
/// <para>不用 SqlSugar 自带的 <c>UtilMethods.GetSqlString</c>:它会把口令哈希、TOTP 种子这类值原样吐出来。
/// 这里按参数名过 <see cref="SensitiveKeys"/>,命中的只打 <c>'***'</c>。</para>
/// <para>颜色用 ANSI 码嵌进消息串,<b>不用 <c>Console.ForegroundColor</c></b>:那是进程级状态,
/// 多线程并发写日志时颜色会串到别的行上。</para>
/// <para>语句经 <see cref="SqlPrettyPrinter"/> 排版成多行,关键字按语句类型上色,一眼分得出查询、新增、修改、删除。</para>
/// </summary>
public static class SqlLogFormatter
{
    private const string RESET = "\u001b[0m";
    private const string GRAY = "\u001b[90m";

    /// <summary>SQL 日志的类别名。固定不变,消费者靠它单独调级别(<c>Logging:LogLevel:SmartAdmin.Sql</c>)。</summary>
    public const string CATEGORY = "SmartAdmin.Sql";

    /// <summary>
    /// 语句块的消息模板:头行一行,语句另起、每行不带任何前缀,框选下来就能贴进客户端跑。
    /// 控制台日志、慢 SQL、失败 SQL 共用这一个。颜色随参数值走,结构化 sink 仍拿得到各字段。
    /// <para>换行必须是 <see cref="Environment.NewLine"/>:控制台格式化器只在它后面给续行补缩进,写死 <c>\n</c> 在 Windows 上续行会顶格。</para>
    /// </summary>
    public static readonly string BLOCK_TEMPLATE =
        "【{Kind}】时间 {Time} 耗时 {Elapsed} {Params}{ConfigId}{Index}" + Environment.NewLine
        + "{Sql}";

    /// <summary>汇总行模板。</summary>
    public const string SUMMARY_TEMPLATE = "【汇总】{Label} → {Count} 条 / {Total} / 最慢 {Slowest}{Hint}";

    /// <summary>一个请求里超过这么多条语句就提示疑似 N+1。</summary>
    private const int N_PLUS_ONE_HINT = 20;

    /// <summary>是否加颜色:显式配了就听配置,没配则 stdout 是终端且未设 NO_COLOR 才加。</summary>
    public static bool ShouldColor(bool? configured) => configured
        ?? (!Console.IsOutputRedirected
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")));

    /// <summary>
    /// 语句块的七个参数值(按 <see cref="BLOCK_TEMPLATE"/> 的顺序)。<paramref name="at"/> 不传取当前时间;
    /// <paramref name="dbType"/> 决定布尔与字符串怎么写成字面量,不传按 SQLite / MySQL 的写法。
    /// 主库不打 ConfigId:类别名 <see cref="CATEGORY"/> 已经说明了是谁,只有副库才标出库名。
    /// 越过 <paramref name="slowMillis"/>(大于 0 时)标「慢 SQL」,<paramref name="failed"/> 标「执行失败」,日志平台按这两个词检索。
    /// </summary>
    public static object?[] BlockArgs(
        int? index, string sql, object? parameters, double millis, string configId, int maxSqlChars, bool color, int slowMillis,
        DateTimeOffset? at = null, DbType? dbType = null, bool failed = false)
    {
        var (kind, kindColor) = Classify(sql);
        var inlined = Inline(sql, parameters, maxSqlChars, dbType, out var paramCount, out var truncated);

        var note = paramCount == 0 ? "无参数" : $"参数 {paramCount} 个(已内联)";
        if (failed) note += " · 执行失败";
        if (slowMillis > 0 && millis >= slowMillis) note += $" · 慢 SQL(≥ {slowMillis} ms)";
        if (truncated) note += $" · 语句超 {maxSqlChars} 字已截断";

        return
        [
            Paint(kind, kindColor, color),
            Paint((at ?? DateTimeOffset.Now).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), GRAY, color),
            Paint(millis.ToString("F1", CultureInfo.InvariantCulture) + " ms", ElapsedColor(millis, slowMillis), color),
            note,
            configId == SqlSugarSetup.MainConfigId ? "" : $" [{configId}]",
            index is null ? "" : " #" + index.Value.ToString(CultureInfo.InvariantCulture),
            Pretty(inlined, color ? kindColor : null),
        ];
    }

    private static string Pretty(string sql, string? keywordColor)
    {
        try
        {
            return SqlPrettyPrinter.Format(sql, keywordColor, Environment.NewLine);
        }
        catch (Exception)   // 排版只为好看:它在执行 SQL 的钩子里,出了岔子退回原文,不能连累查询本身
        {
            return sql;
        }
    }

    /// <summary>汇总行的五个参数值(按 <see cref="SUMMARY_TEMPLATE"/> 的顺序)。</summary>
    public static object?[] SummaryArgs(SqlLogStats stats, bool color)
    {
        var hint = stats.Count >= N_PLUS_ONE_HINT
            ? Paint($"  ← {stats.Count} 条,疑似 N+1", "\u001b[33m", color)
            : "";
        return
        [
            stats.Label,
            stats.Count,
            stats.TotalMillis.ToString("F1", CultureInfo.InvariantCulture) + " ms",
            stats.Count == 0 ? "-" : $"#{stats.SlowestIndex} {stats.SlowestMillis.ToString("F1", CultureInfo.InvariantCulture)} ms",
            hint,
        ];
    }

    /// <summary>按首个关键字分类,顺带给一个颜色。用亮色:深色终端上普通蓝几乎看不清。</summary>
    private static (string Kind, string Color) Classify(string sql)
    {
        var head = sql.AsSpan().TrimStart();
        // 带 CTE 的查询以 WITH 开头,别归到"其它"里去
        if (StartsWith(head, "SELECT") || StartsWith(head, "WITH")) return ("查询", "\u001b[92m");
        if (StartsWith(head, "INSERT")) return ("新增", "\u001b[93m");
        if (StartsWith(head, "UPDATE")) return ("修改", "\u001b[96m");
        if (StartsWith(head, "DELETE")) return ("删除", "\u001b[91m");
        if (StartsWith(head, "CREATE") || StartsWith(head, "ALTER")
            || StartsWith(head, "DROP") || StartsWith(head, "TRUNCATE")) return ("建表", "\u001b[95m");
        return ("其它", GRAY);
    }

    private static bool StartsWith(ReadOnlySpan<char> text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>耗时配色:快的绿、够得着慢 SQL 阈值一半的黄、越线的红。</summary>
    private static string ElapsedColor(double millis, int slowMillis)
    {
        var slow = slowMillis > 0 ? slowMillis : 1000;
        if (millis >= slow) return "\u001b[91m";
        return millis >= slow / 2.0 ? "\u001b[93m" : "\u001b[92m";
    }

    private static string Paint(string text, string color, bool enabled) =>
        enabled ? color + text + RESET : text;

    /// <summary>
    /// 把参数值替换进语句。按参数名<b>从长到短</b>替换:否则 <c>@p1</c> 会先把 <c>@p10</c> 咬掉半截。
    /// </summary>
    private static string Inline(
        string sql, object? parameters, int maxChars, DbType? dbType, out int paramCount, out bool truncated)
    {
        var pars = parameters as SugarParameter[] ?? [];
        paramCount = pars.Length;

        var text = sql;
        foreach (var p in pars.OrderByDescending(p => p.ParameterName?.Length ?? 0))
        {
            var name = p.ParameterName;
            if (string.IsNullOrEmpty(name)) continue;
            var literal = SensitiveKeys.IsSensitive(name) ? "'***'" : Literal(p.Value, dbType);
            text = text.Replace(name, literal, StringComparison.Ordinal);
            // 参数名有时不带符号,而语句里带着;两种写法都试一遍
            if (name[0] is not ('@' or ':' or '?'))
                text = text.Replace("@" + name, literal, StringComparison.Ordinal);
        }

        truncated = text.Length > maxChars;
        return truncated ? string.Concat(text.AsSpan(0, maxChars), " …") : text;
    }

    /// <summary>值渲染成 SQL 字面量,按方言写成贴进客户端就能跑的样子。</summary>
    private static string Literal(object? value, DbType? dbType) => value switch
    {
        null or DBNull => "NULL",
        // PG 的 boolean 列不跟整数比(boolean = integer 直接报错);SqlServer 的 bit 反过来不吃 TRUE,其余方言 1/0 都认
        bool b when dbType == DbType.PostgreSQL => b ? "true" : "false",
        bool b => b ? "1" : "0",
        string s => Text(s, dbType),
        char c => Text(c.ToString(), dbType),
        Guid g => Quote(g.ToString()),
        DateTime d => Quote(d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
        DateTimeOffset d => Quote(d.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)),
        byte[] bytes => $"0x{Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)))}({bytes.Length} 字节)",
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? ""),
    };

    /// <summary>SqlServer 上不带 N 的字面量按库排序规则的代码页走,中文可能变成 ?,查不出行;纯 ASCII 不加,读着清爽。</summary>
    private static string Text(string s, DbType? dbType) =>
        dbType == DbType.SqlServer && !Ascii.IsValid(s) ? "N" + Quote(s) : Quote(s);

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            if (c == '\'') sb.Append('\'');      // SQL 里单引号靠重复转义
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}
