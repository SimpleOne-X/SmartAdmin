using System.Text.RegularExpressions;
using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// SQL 控制台日志的排版:子句起行、条件逐个起行、长列表与长子查询展开。
/// <para>排版只许动空白和关键字大小写——日志里的语句要能原样贴进客户端跑。</para>
/// </summary>
public class SqlPrettyPrinterTests
{
    private static string Format(string sql) => SqlPrettyPrinter.Format(sql, keywordColor: null, newLine: "\n");

    [Fact]
    public void 短语句保持单行原样()
    {
        Assert.Equal("SELECT * FROM t WHERE a=1 AND b=10", Format("SELECT * FROM t WHERE a=1 AND b=10"));
    }

    [Fact]
    public void 长语句按子句起行且条件逐个起行()
    {
        var sql = "SELECT [Id],[Account],[RealName],[OrgId],[Phone],[Email],[CreateTime] FROM [sys_user] "
                  + "WHERE ( [IsDelete] = 0 ) AND ( [OrgId] IN (100,101) ) ORDER BY [Id] DESC";

        Assert.Equal(
            """
            SELECT [Id], [Account], [RealName], [OrgId], [Phone], [Email], [CreateTime]
            FROM [sys_user]
            WHERE ([IsDelete] = 0)
            AND ([OrgId] IN (100, 101))
            ORDER BY [Id] DESC
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Fact]
    public void LIMIT与OFFSET同行()
    {
        var sql = "SELECT \"Id\",\"Account\",\"RealName\",\"OrgId\" FROM \"sys_user\" WHERE ( \"IsDelete\" = 0 ) "
                  + "AND ( \"OrgId\" IN (100,101) ) ORDER BY \"Id\" DESC LIMIT 10 OFFSET 0";

        Assert.Equal(
            """
            SELECT "Id", "Account", "RealName", "OrgId"
            FROM "sys_user"
            WHERE ("IsDelete" = 0)
            AND ("OrgId" IN (100, 101))
            ORDER BY "Id" DESC
            LIMIT 10 OFFSET 0
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Fact]
    public void 列表超宽时逐项起行()
    {
        var sql = "SELECT [Id],[Account],[RealName],[OrgId],[Phone],[Email],[CreateTime],[UpdateTime],[CreateUserId] FROM [sys_user]";

        Assert.Equal(
            """
            SELECT
              [Id],
              [Account],
              [RealName],
              [OrgId],
              [Phone],
              [Email],
              [CreateTime],
              [UpdateTime],
              [CreateUserId]
            FROM [sys_user]
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    /// <summary>字符串、方括号里的同名词不是关键字;BETWEEN 自带的 AND 不拆;行注释改成块注释,免得吞掉后面的语句。</summary>
    [Fact]
    public void 字符串与标识符里的关键字不拆()
    {
        var sql = "select a from t where name = 'x from y and z' and [order by] = 1 and d between 1 and 9 and e = 2 or f = 3 -- note";

        Assert.Equal(
            """
            SELECT a
            FROM t
            WHERE name = 'x from y and z'
            AND [order by] = 1
            AND d BETWEEN 1 AND 9
            AND e = 2
            OR f = 3 /* note */
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Fact]
    public void 长子查询缩进展开()
    {
        var sql = "SELECT COUNT(1) FROM (SELECT [Id],[Account],[RealName] FROM [sys_user] "
                  + "WHERE ( [IsDelete] = 0 ) AND ( [OrgId] IN (100,101) )) CountTable";

        Assert.Equal(
            """
            SELECT COUNT(1)
            FROM (
              SELECT [Id], [Account], [RealName]
              FROM [sys_user]
              WHERE ([IsDelete] = 0)
              AND ([OrgId] IN (100, 101))
            ) CountTable
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Fact]
    public void 连接按整个连接词起行()
    {
        var sql = "SELECT u.[Id], r.[Name] FROM [sys_user] u LEFT OUTER JOIN [sys_user_role] ur ON ur.[UserId] = u.[Id] "
                  + "AND ur.[IsDelete] = 0 INNER JOIN [sys_role] r ON r.[Id] = ur.[RoleId]";

        Assert.Equal(
            """
            SELECT u.[Id], r.[Name]
            FROM [sys_user] u
            LEFT OUTER JOIN [sys_user_role] ur ON ur.[UserId] = u.[Id]
            AND ur.[IsDelete] = 0
            INNER JOIN [sys_role] r ON r.[Id] = ur.[RoleId]
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    /// <summary>
    /// 截断的语句、没配平的括号、没收尾的字符串都可能进来(<c>MaxSqlChars</c> 截在半截)。
    /// 不能抛,也不能丢字:去掉空白之后与原文一字不差。
    /// </summary>
    [Theory]
    [InlineData("SELECT [a], [b], [c], [d], [e], [f], [g], [h] FROM [t] WHERE [x] = 'unterminated AND (y = 1) OR z IN (1, 2")]
    [InlineData("SELECT [a], [b], [c], [d], [e], [f], [g], [h] FROM [t] WHERE [x] IN (SELECT [y] FROM [u] WHERE [z] = (1")]
    [InlineData("SELECT 1) FROM [t] WHERE [a] = 1)) AND [b] = 2 OR [c] = 3 AND CASE WHEN [d] = 1 AND [e] = 2 THEN 1 END = 1")]
    [InlineData("DELETE FROM [t] WHERE [Id] IN (1, 2, 3) AND [Name] = 'it''s [x]' AND [Remark] = N'a,b;c' ; SELECT @@ROWCOUNT; SELECT 1")]
    public void 残缺语句不抛且不丢字(string sql)
    {
        var padded = sql + " AND [padding_column_to_exceed_the_line_width] = 1";

        var formatted = Format(padded);

        Assert.Equal(Strip(padded), Strip(formatted));
    }

    private static string Strip(string s) => Regex.Replace(s, @"\s+", "");
}
