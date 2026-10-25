using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SmartAdmin.SqlSugar;
using SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// SQL 控制台日志(<c>Database:SqlLog</c>):开发期看 ORM 到底生成了什么、每条跑了多久。
/// <para>与 <see cref="SqlLoggingTests"/> 覆盖的慢/失败 SQL 是两件事:那两条是生产诊断且默认就在,
/// 这一套默认全关,开了才逐条打,并在请求结束补一行汇总。</para>
/// </summary>
public class SqlConsoleLogTests
{
    private static AdminAppFactory Factory(CaptureLoggerProvider log, params (string Key, string Value)[] settings) =>
        new()
        {
            Settings = settings.ToDictionary(x => x.Key, x => (string?)x.Value),
            Overrides = s => s.AddSingleton<ILoggerProvider>(log),
        };

    private static (string, string) On => ("SmartAdmin:Database:SqlLog:Enabled", "true");
    private static (string, string) NoColor => ("SmartAdmin:Database:SqlLog:Color", "false");

    private static async Task<HttpClient> SuperAdminClient(AdminAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await c.LoginToken("superAdmin", "Test@123456"));
        return c;
    }

    /// <summary>默认必须安静:开发期的观察工具漏进生产,等于每请求多打若干条日志。</summary>
    [Fact]
    public async Task 默认不打任何语句块()
    {
        var log = new CaptureLoggerProvider();
        using var f = new AdminAppFactory { Overrides = s => s.AddSingleton<ILoggerProvider>(log) };
        var c = await SuperAdminClient(f);

        await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=10");

        Assert.DoesNotContain(log.Entries, e => IsBlock(e.Text));
    }

    [Fact]
    public async Task 开启后每条语句打出SQL与耗时()
    {
        var log = new CaptureLoggerProvider();
        using var f = Factory(log, On, NoColor);
        var c = await SuperAdminClient(f);

        await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=10");

        // 按语句内容挑,别取 First:第一个越过阈值被打出来的块不一定是这次分页查询——SqlServer 上是建表探测的
        // IF EXISTS (SELECT …),它归到"其它"是对的,只是不是这个用例要验的那条。
        var block = log.Entries.First(e => IsBlock(e.Text) && ReadsSysUser(e.Text));
        var lines = block.Text.Split(Environment.NewLine);
        Assert.Matches(
            @"^【查询】时间 \d{2}:\d{2}:\d{2}\.\d{3} 耗时 \d+\.\d ms (无参数|参数 \d+ 个\(已内联\))( · [^·#]+)*( #\d+)?$", lines[0]);   // 主库不标 ConfigId
        Assert.StartsWith("SELECT", lines[1], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 日志里的查询要能原样贴进客户端跑:去掉头行,剩下的交回数据库执行,不能报错。
    /// 四种方言的腿都跑这一条,布尔、字符串字面量的写法和方言对不上会当场报错。
    /// </summary>
    [Fact]
    public async Task 打出来的查询原样贴回去能跑()
    {
        var log = new CaptureLoggerProvider();
        using var f = Factory(log, On, NoColor);
        var c = await SuperAdminClient(f);

        await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=10");

        var queries = log.Entries
            .Where(e => e.Text.StartsWith("【查询】", StringComparison.Ordinal) && ReadsSysUser(e.Text))
            .Select(e => e.Text[(e.Text.IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length)..])
            .ToList();
        Assert.NotEmpty(queries);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        foreach (var sql in queries) await db.Ado.GetDataTableAsync(sql);
    }

    private static bool IsBlock(string text) => text.StartsWith('【') && text.Contains("】时间 ", StringComparison.Ordinal);

    /// <summary>"FROM sys_user"(四方言的引号都吃)只会命中读 sys_user 的查询,建表 / 索引探测 / INSERT 都不含它。</summary>
    private static bool ReadsSysUser(string text) =>
        Regex.IsMatch(text, @"FROM\s+[\[`""]?sys_user(?!\w)", RegexOptions.IgnoreCase);

    /// <summary>汇总行是查 N+1 的入口:单看语句块只会觉得"每条都很快"。</summary>
    [Fact]
    public async Task 请求结束打一行汇总()
    {
        var log = new CaptureLoggerProvider();
        using var f = Factory(log, On, NoColor);
        var c = await SuperAdminClient(f);

        await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=10");

        // 按路径挑:登录那个请求也会打一行汇总,而且排在前面
        var summary = log.Entries.First(e =>
            e.Text.Contains("【汇总】", StringComparison.Ordinal)
            && e.Text.Contains("/api/v1/sys/user/page", StringComparison.Ordinal));
        Assert.Contains("GET /api/v1/sys/user/page", summary.Text, StringComparison.Ordinal);
        Assert.Contains("条", summary.Text, StringComparison.Ordinal);
        Assert.Contains("最慢", summary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 汇总可单独关掉()
    {
        var log = new CaptureLoggerProvider();
        using var f = Factory(log, On, NoColor, ("SmartAdmin:Database:SqlLog:RequestSummary", "false"));
        var c = await SuperAdminClient(f);

        await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=10");

        Assert.Contains(log.Entries, e => IsBlock(e.Text));   // 语句块照打
        Assert.DoesNotContain(log.Entries, e => e.Text.Contains("【汇总】", StringComparison.Ordinal));
    }

    /// <summary>阈值调到不可能达到时一条都不该打——否则 MinMillis 形同虚设。</summary>
    [Fact]
    public async Task 耗时阈值过滤掉快语句()
    {
        var log = new CaptureLoggerProvider();
        using var f = Factory(log, On, NoColor, ("SmartAdmin:Database:SqlLog:MinMillis", "100000"));
        var c = await SuperAdminClient(f);

        await c.GetAsync("/api/v1/sys/user/page?Current=1&Size=10");

        Assert.DoesNotContain(log.Entries, e => IsBlock(e.Text));
    }

    [Fact]
    public void 关掉颜色后不出现转义序列()
    {
        var args = SqlLogFormatter.BlockArgs(
            1, "SELECT 1", null, 3.5, "main", maxSqlChars: 4000, color: false, slowMillis: 1000);

        Assert.DoesNotContain(args, a => a?.ToString()?.Contains('\u001b') == true);
    }

    /// <summary>参数内联进语句,但口令这类名字只出现 ***:这份日志会被贴进工单和聊天窗。</summary>
    [Fact]
    public void 参数内联且敏感值打码()
    {
        var pars = new[]
        {
            new SugarParameter("@name", "张三"),
            new SugarParameter("@password", "super-secret-value"),
        };

        var args = SqlLogFormatter.BlockArgs(
            1, "SELECT * FROM sys_user WHERE Name=@name AND Password=@password", pars,
            1.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000);
        var sql = args[6]!.ToString()!;

        Assert.Contains("'张三'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", sql, StringComparison.Ordinal);
        Assert.Contains("'***'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@name", sql, StringComparison.Ordinal);   // 真的替换掉了,不是并排打印
    }

    /// <summary>@p1 不能把 @p10 咬掉半截——按名字从长到短替换。</summary>
    [Fact]
    public void 参数名有公共前缀时不串位()
    {
        var pars = new[]
        {
            new SugarParameter("@p1", 1),
            new SugarParameter("@p10", 10),
        };

        var args = SqlLogFormatter.BlockArgs(
            1, "SELECT * FROM t WHERE a=@p1 AND b=@p10", pars,
            1.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000);

        Assert.Equal("SELECT * FROM t WHERE a=1 AND b=10", args[6]);
    }

    [Fact]
    public void 超长语句被截断()
    {
        var longSql = "SELECT " + new string('x', 500);

        var args = SqlLogFormatter.BlockArgs(
            1, longSql, null, 1.0, "main", maxSqlChars: 100, color: false, slowMillis: 1000);

        Assert.True(args[6]!.ToString()!.Length < longSql.Length);
        Assert.Contains("已截断", args[3]!.ToString()!, StringComparison.Ordinal);
    }

    /// <summary>默认控制台格式没有时间戳,块头自己带一个,对得上是哪一刻跑的。</summary>
    [Fact]
    public void 块头带执行时刻()
    {
        var at = new DateTimeOffset(2026, 10, 23, 9, 5, 7, 42, TimeSpan.FromHours(8));

        var args = SqlLogFormatter.BlockArgs(
            1, "SELECT 1", null, 1.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000, at);

        Assert.Equal("09:05:07.042", args[1]);
    }

    /// <summary>
    /// 语句另起一行、续行不带任何前缀,框选下来就是 SQL;换行用 <see cref="Environment.NewLine"/>:
    /// 控制台格式化器只在它后面补缩进,Windows 上写死 \n 的话续行顶格、和首行错开六格。
    /// </summary>
    [Fact]
    public void 多行语句续行不带前缀()
    {
        // SqlSugar 在 SqlServer 上按会话号查会话的原样语句
        var sql = "SELECT TOP 1 [SessionId],[UserId],[Account],[Ip],[UserAgent],[ExpiresAt],[AbsoluteExpiresAt],[LastActivityAt],"
                  + "[RevokedAt],[IsDelete],[CreateTime],[CreateUserId],[UpdateTime],[UpdateUserId],[Id] FROM [sys_session]  "
                  + "WHERE ( [SessionId] = @SessionId0 )  AND ( [IsDelete] = @IsDelete1 )";
        var pars = new[]
        {
            new SugarParameter("@SessionId0", "01a16ba7f4d574a6936162cf444895e2"),
            new SugarParameter("@IsDelete1", false),
        };

        var args = SqlLogFormatter.BlockArgs(
            1, sql, pars, 1.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000, dbType: DbType.SqlServer);

        Assert.Equal(
            """
            SELECT TOP 1
              [SessionId],
              [UserId],
              [Account],
              [Ip],
              [UserAgent],
              [ExpiresAt],
              [AbsoluteExpiresAt],
              [LastActivityAt],
              [RevokedAt],
              [IsDelete],
              [CreateTime],
              [CreateUserId],
              [UpdateTime],
              [UpdateUserId],
              [Id]
            FROM [sys_session]
            WHERE ([SessionId] = '01a16ba7f4d574a6936162cf444895e2')
            AND ([IsDelete] = 0)
            """.ReplaceLineEndings(),
            args[6]);
        Assert.EndsWith(Environment.NewLine + "{Sql}", SqlLogFormatter.BLOCK_TEMPLATE, StringComparison.Ordinal);
    }

    /// <summary>
    /// 头行的字段顺序:类型、时刻、耗时、参数说明、ConfigId、序号。主库不标 ConfigId(类别名已经说明了),副库标库名;
    /// 不在请求里的语句没有序号,也不留空格。
    /// </summary>
    [Fact]
    public void 块头按类型时刻耗时参数排列()
    {
        var at = new DateTimeOffset(2026, 10, 25, 18, 40, 56, 360, TimeSpan.Zero);
        var pars = new[] { new SugarParameter("@id", 1) };

        var inRequest = SqlLogFormatter.BlockArgs(
            3, "SELECT 1 WHERE 1=@id", pars, 390.94, SqlSugarSetup.MainConfigId, maxSqlChars: 4000, color: false, slowMillis: 1000, at);
        var outside = SqlLogFormatter.BlockArgs(
            null, "SELECT 1", null, 1.0, "Report", maxSqlChars: 4000, color: false, slowMillis: 1000, at);

        Assert.Equal<object?>(["查询", "18:40:56.360", "390.9 ms", "参数 1 个(已内联)", "", " #3"], inRequest[..6]);
        Assert.Equal<object?>([" [Report]", ""], outside[4..6]);
    }

    /// <summary>慢 SQL、失败 SQL 和控制台日志是同一种块,头行标出来,日志平台按「慢 SQL」「执行失败」检索。</summary>
    [Fact]
    public void 慢SQL与失败SQL在头行标出()
    {
        var slow = SqlLogFormatter.BlockArgs(
            null, "SELECT 1", null, 1500, "main", maxSqlChars: 4000, color: false, slowMillis: 1000);
        var failed = SqlLogFormatter.BlockArgs(
            null, "SELECT 1", null, 2.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000, failed: true);

        Assert.Equal("无参数 · 慢 SQL(≥ 1000 ms)", slow[3]);
        Assert.Equal("无参数 · 执行失败", failed[3]);
    }

    /// <summary>PG 的 boolean 列不跟 0/1 比,SqlServer 的 bit 不吃 true:布尔按方言写,其余方言维持 1/0。</summary>
    [Theory]
    [InlineData(DbType.PostgreSQL, "WHERE \"IsDelete\" = false")]
    [InlineData(DbType.SqlServer, "WHERE \"IsDelete\" = 0")]
    [InlineData(DbType.MySql, "WHERE \"IsDelete\" = 0")]
    [InlineData(DbType.Sqlite, "WHERE \"IsDelete\" = 0")]
    public void 布尔按方言内联(DbType dbType, string expected)
    {
        var pars = new[] { new SugarParameter("@IsDelete0", false) };

        var sql = SqlLogFormatter.BlockArgs(
            1, "SELECT 1 WHERE \"IsDelete\" = @IsDelete0", pars, 1.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000,
            dbType: dbType)[6]!.ToString()!;

        Assert.EndsWith(expected, sql, StringComparison.Ordinal);
    }

    /// <summary>SqlServer 上中文值要带 N 前缀,否则按库排序规则的代码页走,可能变成 ? 查不出行;纯 ASCII 不加。</summary>
    [Fact]
    public void SqlServer上非ASCII字符串带N前缀()
    {
        var pars = new[] { new SugarParameter("@name", "张三"), new SugarParameter("@account", "admin") };

        var sql = SqlLogFormatter.BlockArgs(
            1, "SELECT 1 WHERE a=@name AND b=@account", pars, 1.0, "main", maxSqlChars: 4000, color: false, slowMillis: 1000,
            dbType: DbType.SqlServer)[6]!.ToString()!;

        Assert.Equal("SELECT 1 WHERE a=N'张三' AND b='admin'", sql);
    }

    /// <summary>关键字按语句类型上色,值不上色:分得出类型,值也读得清。</summary>
    [Fact]
    public void 开了颜色关键字按语句类型上色()
    {
        var select = SqlLogFormatter.BlockArgs(
            1, "SELECT a FROM t WHERE b = 'from'", null, 1.0, "main", maxSqlChars: 4000, color: true, slowMillis: 1000)[6]!.ToString()!;
        var delete = SqlLogFormatter.BlockArgs(
            1, "DELETE FROM t WHERE b = 1", null, 1.0, "main", maxSqlChars: 4000, color: true, slowMillis: 1000)[6]!.ToString()!;

        Assert.Contains("\u001b[92mSELECT\u001b[0m", select, StringComparison.Ordinal);
        Assert.Contains("'from'", select, StringComparison.Ordinal);   // 字符串里的同名词不是关键字
        Assert.Contains("\u001b[91mDELETE\u001b[0m", delete, StringComparison.Ordinal);
    }

    [Fact]
    public void 语句多到一定条数时汇总提示疑似NPlusOne()
    {
        var stats = new SqlLogStats("GET /x");
        for (var i = 0; i < 25; i++) stats.Record(1.0);

        var args = SqlLogFormatter.SummaryArgs(stats, color: false);

        Assert.Equal(25, args[1]);
        Assert.Contains("N+1", args[4]!.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void 条数不多时不提示()
    {
        var stats = new SqlLogStats("GET /x");
        stats.Record(1.0);
        stats.Record(9.0);

        var args = SqlLogFormatter.SummaryArgs(stats, color: false);

        Assert.Equal("", args[4]);
        Assert.Contains("#2", args[3]!.ToString()!, StringComparison.Ordinal);   // 最慢的是第二条
    }

    private static (string, string) NoConsoleColor => ("Logging:Console:FormatterOptions:ColorBehavior", "Disabled");

    /// <summary>经宿主注册好的控制台格式化器写一条,拿到的就是控制台上那几行。</summary>
    private static string WriteConsole(AdminAppFactory f, string category, string message)
    {
        var name = f.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName;
        var formatter = f.Services.GetServices<ConsoleFormatter>().Single(x => x.Name == name);
        using var writer = new StringWriter();
        formatter.Write(new LogEntry<string>(LogLevel.Information, category, new EventId(0), message, null, (s, _) => s), null, writer);
        return writer.ToString();
    }

    /// <summary>默认 simple 格式让 SmartAdmin.Sql[0] 单独占一行;并进头行末尾,语句续行照旧缩进对齐。</summary>
    [Fact]
    public void 控制台上类别标签并进头行()
    {
        using var f = Factory(new CaptureLoggerProvider(), NoConsoleColor);
        var nl = Environment.NewLine;

        var text = WriteConsole(f, SqlLogFormatter.CATEGORY, "【查询】时间 18:40:56.360 耗时 1.0 ms 无参数" + nl + "SELECT 1" + nl + "FROM t");

        Assert.Equal(
            "info: 【查询】时间 18:40:56.360 耗时 1.0 ms 无参数 SmartAdmin.Sql[0]" + nl + "      SELECT 1" + nl + "      FROM t" + nl, text);
    }

    [Fact]
    public void 控制台上其余类别保持默认两行()
    {
        using var f = Factory(new CaptureLoggerProvider(), NoConsoleColor);
        var nl = Environment.NewLine;

        Assert.Equal("info: Demo.Other[0]" + nl + "      hello" + nl, WriteConsole(f, "Demo.Other", "hello"));
    }

    /// <summary>宿主写明了格式、或还在用过时的顶层开关(它们只在没指定格式时生效)就不接管。</summary>
    [Theory]
    [InlineData(null, null, "smartadmin")]
    [InlineData("Logging:Console:FormatterName", "simple", "simple")]
    [InlineData("Logging:Console:FormatterName", "json", "json")]
    [InlineData("Logging:Console:IncludeScopes", "true", null)]
    public void 控制台格式只在宿主没指定时接管(string? key, string? value, string? expected)
    {
        using var f = key is null ? Factory(new CaptureLoggerProvider()) : Factory(new CaptureLoggerProvider(), (key, value!));

        Assert.Equal(expected, f.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
    }
}
