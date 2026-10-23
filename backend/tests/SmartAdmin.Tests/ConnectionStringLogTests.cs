using SmartAdmin.SqlSugar;

namespace SmartAdmin.Tests;

/// <summary>
/// 启动日志里的连接串只留定位用的几项(服务器、端口、库名、SQLite 文件),其余一概不打。
/// <para>按白名单留而不是按黑名单遮:各家驱动的口令键名不统一(Password / Pwd / SSL Password…),
/// 未加引号的值里带 <c>;</c> 时后半截还会被解析成一个独立的键——遮哪几个键都可能漏。</para>
/// </summary>
public class ConnectionStringLogTests
{
    [Theory]
    [InlineData("Server=127.0.0.1,1433;Database=smart;User ID=app;Password=Secret_Pa55!;TrustServerCertificate=true",
        "server=127.0.0.1,1433;database=smart")]
    [InlineData("Server=db;Port=3306;Database=smart;User ID=root;pwd=a;b;AllowPublicKeyRetrieval=true",
        "server=db;port=3306;database=smart")]
    [InlineData("Server=db;Port=3306;User ID=root;Password='a;b=c';SSL Mode=None;",
        "server=db;port=3306")]
    [InlineData("Host=pg;Port=5432;Database=smart;Username=postgres;PASSWORD=secret;SSL Password=k",
        "host=pg;port=5432;database=smart")]
    [InlineData("Data Source=./data/SmartAdmin.db", "data source=./data/SmartAdmin.db")]
    public void Keeps_only_location_keys(string conn, string expected)
    {
        Assert.Equal(expected, SqlSugarSetup.DescribeConnectionForLog(conn));
    }

    [Fact]
    public void Unparseable_connection_string_is_not_echoed()
    {
        var shown = SqlSugarSetup.DescribeConnectionForLog("garbage Password secret");

        Assert.DoesNotContain("secret", shown, StringComparison.OrdinalIgnoreCase);
    }
}
