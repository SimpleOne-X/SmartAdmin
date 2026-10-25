using System.Text;

namespace SmartAdmin.SqlSugar;

/// <summary>
/// SQL 控制台日志的排版:子句起行、WHERE 条件逐个起行、长列表逐项起行、长子查询缩进展开,关键字大写并可上色。
/// <para>只服务于人读的日志,不求完整语法:分词只认字符串、带引号的标识符、注释与括号,
/// 改的只是 token 之间的空白和关键字大小写,token 本身(含内联进来的参数值)不动。</para>
/// </summary>
internal static class SqlPrettyPrinter
{
    /// <summary>一行的目标宽度:整条不超过它就保持单行,列表、子查询超过它才展开。</summary>
    internal const int LINE_WIDTH = 100;

    /// <summary>超过这么长不排版、原样返回:几万字的批量插入拆成上千行也没人读,排版的开销却跟着长度涨。</summary>
    internal const int MAX_FORMAT_CHARS = 20_000;

    /// <summary>括号嵌套超过这么深不排版:渲染是递归的,病态输入不能把日志钩子拖进栈溢出。</summary>
    private const int MAX_DEPTH = 64;

    private const int INDENT = 2;
    private const string RESET = "\u001b[0m";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "SELECT", "DISTINCT", "TOP", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "EXISTS",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "APPLY", "ON", "AS", "GROUP", "ORDER", "BY", "HAVING",
        "UNION", "ALL", "EXCEPT", "INTERSECT", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE", "USING",
        "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "LIMIT", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY",
        "WITH", "OVER", "PARTITION", "OUTPUT", "RETURNING", "CREATE", "ALTER", "DROP", "TRUNCATE", "TABLE", "INDEX",
        "IF", "BEGIN", "COMMIT", "ROLLBACK",
    };

    private static readonly HashSet<string> JoinModifiers = new(StringComparer.Ordinal)
        { "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "OUTER", "NATURAL" };

    /// <summary>排版。<paramref name="keywordColor"/> 为 null 不上色;各行之间用 <paramref name="newLine"/> 连接。</summary>
    internal static string Format(string sql, string? keywordColor, string newLine)
    {
        if (sql.Length > MAX_FORMAT_CHARS) return sql;
        var nodes = Parse(sql, out var depth);
        if (depth > MAX_DEPTH) return sql;

        var flat = new Writer(keywordColor);
        WriteFlat(flat, nodes, 0, nodes.Count);
        if (flat.Column <= LINE_WIDTH) return flat.Finish(newLine);

        var w = new Writer(keywordColor);
        WriteClauses(w, nodes, 0);
        return w.Finish(newLine);
    }

    private enum TokenKind { Word, Literal, Comment, Open, Close, Comma, Semicolon, Symbol }

    /// <summary>一个 token,或一对括号(<see cref="Children"/> 非 null)。</summary>
    private sealed class Node(TokenKind kind, string text, bool spaceBefore, bool qualified = false)
    {
        public TokenKind Kind { get; } = kind;
        public string Text { get; } = text;
        public bool SpaceBefore { get; } = spaceBefore;

        /// <summary>大写后的单词;非单词与 <c>t.Order</c> 这种点号后的列名为 null,不参与关键字判断。</summary>
        public string? Word { get; } = kind == TokenKind.Word && !qualified ? text.ToUpperInvariant() : null;

        public bool IsKeyword => Word is not null && Keywords.Contains(Word);
        public List<Node>? Children { get; init; }
        public bool Closed { get; set; }
        public int? FlatLength { get; set; }
    }

    private static List<Node> Parse(string sql, out int maxDepth)
    {
        var root = new List<Node>();
        var open = new Stack<Node>();
        var space = false;
        maxDepth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { space = true; i++; continue; }

            var siblings = open.Count > 0 ? open.Peek().Children! : root;
            if (c == '(')
            {
                var group = new Node(TokenKind.Open, "(", space) { Children = [] };
                siblings.Add(group);
                open.Push(group);
                maxDepth = Math.Max(maxDepth, open.Count);
                space = false;
                i++;
                continue;
            }
            if (c == ')' && open.Count > 0)
            {
                open.Pop().Closed = true;
                space = false;
                i++;
                continue;
            }

            var start = i;
            string? text = null;
            TokenKind kind;
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                if (end < 0) end = sql.Length;
                // 行注释改写成块注释:排版会把几行并成一行,留着 -- 就把后面的语句全吞进注释里
                text = "/*" + sql[(i + 2)..end].TrimEnd().Replace("*/", "* /", StringComparison.Ordinal) + " */";
                kind = TokenKind.Comment;
                i = end;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                kind = TokenKind.Comment;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                i = SkipQuoted(sql, i, c == '[' ? ']' : c);
                kind = TokenKind.Literal;
            }
            else if (IsWordChar(c))
            {
                while (i < sql.Length && IsWordChar(sql[i])) i++;
                kind = TokenKind.Word;
            }
            else
            {
                i++;
                kind = c switch { ')' => TokenKind.Close, ',' => TokenKind.Comma, ';' => TokenKind.Semicolon, _ => TokenKind.Symbol };
            }

            var qualified = kind == TokenKind.Word && !space && siblings is [.., { Kind: TokenKind.Symbol, Text: "." }];
            siblings.Add(new Node(kind, text ?? sql[start..i], space, qualified));
            space = false;
        }
        return root;
    }

    /// <summary>跳过一段引号包着的内容,连写两个收尾符是转义(<c>'it''s'</c>、<c>[a]]b]</c>)。没收尾就到串尾。</summary>
    private static int SkipQuoted(string sql, int start, char close)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>
    /// 一层括号内(或整条语句)按子句切段,每段起一行。列表子句整段超宽时关键字独占一行、各项缩进一级。
    /// </summary>
    private static void WriteClauses(Writer w, List<Node> nodes, int indent)
    {
        var starts = new List<(int At, string Clause, int Head)>();
        var caseDepth = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Word == "CASE") caseDepth++;
            else if (nodes[i].Word == "END" && caseDepth > 0) caseDepth--;

            if (caseDepth == 0 && ClauseAt(nodes, i) is (string clause, int head)) starts.Add((i, clause, head));
            else if (i == 0 || nodes[i - 1].Kind == TokenKind.Semicolon) starts.Add((i, "", 0));
        }

        for (var s = 0; s < starts.Count; s++)
        {
            var (from, clause, head) = starts[s];
            var to = s + 1 < starts.Count ? starts[s + 1].At : nodes.Count;
            w.Break(indent);
            if (clause is "SELECT" or "SET" or "GROUP BY" or "ORDER BY" or "VALUES"
                && indent + Measure(nodes, from, to) > LINE_WIDTH
                && HasComma(nodes, from + head, to))
            {
                WriteInline(w, nodes, from, from + head, indent, false);
                var item = from + head;
                for (var i = item; i < to; i++)
                {
                    if (nodes[i].Kind != TokenKind.Comma && i != to - 1) continue;
                    w.Break(indent + INDENT);
                    WriteInline(w, nodes, item, i + 1, indent + INDENT, false);
                    item = i + 1;
                }
                continue;
            }
            WriteInline(w, nodes, from, to, indent, clause is "WHERE" or "HAVING" or "JOIN");
        }
    }

    /// <summary>第 <paramref name="i"/> 个 token 是否起一个子句;是则给出子句名与关键字占几个 token。</summary>
    private static (string Clause, int Head)? ClauseAt(List<Node> nodes, int i)
    {
        var word = nodes[i].Word;
        var prev = i > 0 ? nodes[i - 1].Word : null;
        var next = i + 1 < nodes.Count ? nodes[i + 1].Word : null;
        switch (word)
        {
            case "SELECT":
                var head = 1;
                if (next is "DISTINCT" or "ALL") head++;
                if (i + head < nodes.Count && nodes[i + head].Word == "TOP") head += 2;
                return ("SELECT", Math.Min(head, nodes.Count - i));
            case "FROM" when prev is not ("DELETE" or "DISTINCT"):
            case "OFFSET" when i < 2 || nodes[i - 2].Word != "LIMIT":   // LIMIT 10 OFFSET 20 是一句话,不拆
            case "WHERE" or "HAVING" or "VALUES" or "SET" or "LIMIT" or "RETURNING" or "EXCEPT" or "INTERSECT":
                return (word, 1);
            case "GROUP" or "ORDER" when next == "BY":
                return (word + " BY", 2);
            case "UNION":
                return ("UNION", next == "ALL" ? 2 : 1);
            case "JOIN" when prev is null || !JoinModifiers.Contains(prev):
                return ("JOIN", 1);
            case "LEFT" or "RIGHT" or "FULL" or "INNER" or "CROSS" or "NATURAL" or "OUTER"
                when next is "JOIN" or "OUTER" or "APPLY" && (word != "OUTER" || prev is null || !JoinModifiers.Contains(prev)):
                var end = i;
                while (end < nodes.Count && nodes[end].Word is not ("JOIN" or "APPLY")) end++;
                return ("JOIN", Math.Min(end + 1, nodes.Count) - i);
            default:
                return null;
        }
    }

    /// <summary>
    /// 顺序写出,不切子句。条件子句里顶层的 AND / OR 各起一行、与子句关键字对齐;BETWEEN 自带的那个 AND 和 CASE 里的不算。
    /// </summary>
    private static void WriteInline(Writer w, List<Node> nodes, int from, int to, int indent, bool condition)
    {
        var caseDepth = 0;
        var between = false;
        for (var i = from; i < to; i++)
        {
            var node = nodes[i];
            switch (node.Word)
            {
                case "CASE": caseDepth++; break;
                case "END" when caseDepth > 0: caseDepth--; break;
                case "BETWEEN": between = true; break;
                case "AND" when between: between = false; break;
                case "AND" or "OR" when condition && caseDepth == 0 && i > from: w.Break(indent); break;
            }
            Write(w, node);
        }
    }

    private static void Write(Writer w, Node node)
    {
        if (node.Children is null)
        {
            w.Write(node);
            return;
        }

        var subquery = node.Children is [{ Word: "SELECT" or "WITH" }, ..];
        if (!subquery || w.Column + 1 + (node.FlatLength ??= Measure([node], 0, 1)) <= LINE_WIDTH)
        {
            w.Write(TokenKind.Open, "(", node.SpaceBefore, false);
            foreach (var child in node.Children) Write(w, child);
            if (node.Closed) w.Write(TokenKind.Close, ")", false, false);
            return;
        }

        // 放不下的子查询:左括号留在行尾,内容缩进一级按子句排,右括号回到这一行的缩进
        var indent = w.Indent;
        w.Write(TokenKind.Open, "(", node.SpaceBefore, false);
        WriteClauses(w, node.Children, indent + INDENT);
        if (!node.Closed) return;
        w.Break(indent);
        w.Write(TokenKind.Close, ")", false, false);
    }

    private static void WriteFlat(Writer w, List<Node> nodes, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var node = nodes[i];
            if (node.Children is null)
            {
                w.Write(node);
                continue;
            }
            w.Write(TokenKind.Open, "(", node.SpaceBefore, false);
            WriteFlat(w, node.Children, 0, node.Children.Count);
            if (node.Closed) w.Write(TokenKind.Close, ")", false, false);
        }
    }

    private static int Measure(List<Node> nodes, int from, int to)
    {
        var w = new Writer(null, measureOnly: true);
        WriteFlat(w, nodes, from, to);
        return w.Column;
    }

    private static bool HasComma(List<Node> nodes, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (nodes[i].Kind == TokenKind.Comma) return true;
        return false;
    }

    /// <summary>逐行攒输出。空白只在这里决定:括号内侧与逗号前不留空格,逗号后留一个,其余照原文有无。</summary>
    private sealed class Writer(string? color, bool measureOnly = false)
    {
        private readonly List<string> _lines = [];
        private readonly StringBuilder _text = new();
        private TokenKind? _prev;

        public int Indent { get; private set; }

        /// <summary>当前行的可见宽度,不含颜色转义。</summary>
        public int Column { get; private set; }

        public void Break(int indent)
        {
            if (_prev is not null) _lines.Add(_text.ToString());
            _text.Clear().Append(' ', indent);
            Indent = Column = indent;
            _prev = null;
        }

        public void Write(Node node) =>
            Write(node.Kind, node.IsKeyword ? node.Word! : node.Text, node.SpaceBefore, node.IsKeyword);

        public void Write(TokenKind kind, string text, bool spaceBefore, bool keyword)
        {
            var space = _prev switch
            {
                null or TokenKind.Open => false,
                _ when kind is TokenKind.Comma or TokenKind.Close or TokenKind.Semicolon => false,
                TokenKind.Comma => true,
                _ => spaceBefore,
            };
            Column += text.Length + (space ? 1 : 0);
            _prev = kind;
            if (measureOnly) return;

            if (space) _text.Append(' ');
            if (keyword && color is not null) _text.Append(color).Append(text).Append(RESET);
            else _text.Append(text);
        }

        public string Finish(string newLine)
        {
            if (_prev is not null) _lines.Add(_text.ToString());
            return string.Join(newLine, _lines);
        }
    }
}
