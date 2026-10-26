# SQL Console Log

Whether a request fired 2 SQL statements or 143 is not something you can see statement by statement, because each one of them is fast. Turn on `SmartAdmin:Database:SqlLog` and every statement gets a block, with a summary line added once the request ends, and that summary is where the difference lands. It ships off; this is a development-time tool.

The slow-SQL threshold `Database:SlowSqlMillis` and the failed-SQL log are a different pair. Those are on by default and should stay on in production; they're covered in the [deployment guide](/guide/deployment/). They print as the same kind of block shown below; see [Slow and failed SQL are the same block](#slow-and-failed-sql-are-the-same-block).

## Switches

```json
{
  "SmartAdmin": {
    "Database": {
      "SqlLog": {
        "Enabled": true,
        "MinMillis": 0,
        "MaxSqlChars": 4000,
        "RequestSummary": true,
        "Color": null
      }
    }
  }
}
```

| Key | Default | What it does |
| --- | --- | --- |
| `Enabled` | `false` | Master switch |
| `MinMillis` | `0` | Only print statements taking at least this long (ms); `0` prints all |
| `MaxSqlChars` | `4000` | Print limit for one statement; longer ones are truncated with a note. A batch insert can run to hundreds of thousands of characters |
| `RequestSummary` | `true` | Whether to add a summary line when the request ends |
| `Color` | auto | ANSI color on the console. Left unset, colors appear only when stdout is a terminal and `NO_COLOR` is not set |

Color only reaches the console. The same entry also goes to the Visual Studio Output window, log files and the Windows Event Log. None of them understand ANSI codes, so they always get plain text, and leaving `Color` on doesn't put escape sequences into them.

`MinMillis` gates the statement blocks only, not the statistics: statements it filters out still count toward the summary's total count and total elapsed time. No single query in an N+1 is slow, so if filtering by duration also shrank the count, the switch would have defeated its own purpose.

## One block per statement

```text
info: 【查询】时间 10:21:33.402 耗时 3.4 ms 参数 2 个(已内联) #1 SmartAdmin.Sql[0]
      SELECT COUNT(1) FROM "sys_user" WHERE ("IsDelete" = false) AND ("OrgId" IN (100, 101))
──────────────────────────────────────────────────────────────────────────────────────────────────────────
info: 【查询】时间 10:21:33.415 耗时 11.8 ms 参数 2 个(已内联) #2 SmartAdmin.Sql[0]
      SELECT "Id", "Account", "RealName", "OrgId"
      FROM "sys_user"
      WHERE ("IsDelete" = false)
      AND ("OrgId" IN (100, 101))
      ORDER BY "Id" DESC
      LIMIT 10 OFFSET 0
──────────────────────────────────────────────────────────────────────────────────────────────────────────
```

Everything below the header line is the statement itself, with no frame or other prefix on any line, so you can select it and paste it straight into a client. The gray rule under the block is not part of the statement; stop the selection at the statement's last line and it stays out.

The header line carries, in order: statement kind, execution time, elapsed time, parameter count, sequence number, and it ends with the log category `SmartAdmin.Sql[0]`. .NET's default console format has no timestamp, so the millisecond-precision time in the header is what you line a statement up against a frontend request or another log with. Kind is decided by the leading keyword, and a CTE query starting with `WITH` still counts as a query. Statements on the main database carry no database name; those on an extra connection get a `[Report]` before the sequence number, where the bracket holds the `ConfigId` you gave it under `AdditionalDatabases`, so you can see at a glance which database a statement hit. The sequence number matches the "slowest was #n" in the summary line; statements that run outside an HTTP request (reading table schemas at startup, scheduled jobs) have none.

A statement of 100 characters or fewer stays on one line; only longer ones get laid out. Each clause starts its own line. Top-level `AND` and `OR` inside `WHERE`, `HAVING` and `JOIN … ON` each start a line, aligned with the clause keyword, except the `AND` that belongs to a `BETWEEN`. When a `SELECT` column list, `SET` assignments, `VALUES` rows, `GROUP BY` or `ORDER BY` don't fit on one line, the keyword gets a line to itself and each item follows on its own indented line. A subquery that doesn't fit is expanded and indented one level.

Layout only touches the whitespace between tokens and the case of keywords. Strings and quoted identifiers are left exactly as they are, so the result still pastes into a client and runs. The one exception is line comments, which are rewritten as `/* … */`: once several lines are joined into one, a `--` would swallow every statement after it. Statements over 20,000 characters (mostly batch inserts) are printed on one line as they are. Nobody reads a thousand lines of them, while the cost of laying them out grows with their length. A typical statement takes a few to a few dozen microseconds to lay out.

Color lands in three places. The statement kind and the keywords inside the statement are colored by category: query green, insert yellow, update cyan, delete red, DDL magenta, other gray, with values left in the default color. Elapsed time is bucketed against the slow-SQL threshold: over the line red, past halfway yellow, everything else green. The execution time is gray.

Parameters are not listed on a separate line; they're inlined into the statement. What you want while debugging is one SQL statement you can paste into a client and run, not "statement here, parameters there" to be reassembled by hand. Values are written as literals in the dialect's own form. On PostgreSQL booleans become `true` or `false`, because its `boolean` columns won't compare against integers; every other dialect gets `1` or `0`. On SqlServer, strings containing non-ASCII characters get an `N` prefix. Without it the literal goes through the code page of the database's collation, Chinese can turn into `?`, and the query finds no rows.

## The summary line is the way into N+1

```text
info: 【汇总】GET /api/v1/sys/user/page → 2 条 / 15.2 ms / 最慢 #2 11.8 ms SmartAdmin.Sql[0]
```

Count, total elapsed, and which one was slowest. This is the line that makes good on the opening claim. At 20 statements it also calls the problem out at the end of the line:

```text
info: 【汇总】GET /api/v1/sys/order/page → 143 条 / 812.6 ms / 最慢 #7 24.1 ms  ← 143 条,疑似 N+1 SmartAdmin.Sql[0]
```

Only HTTP requests get a summary. A request that ran no statements at all (static assets, 404s) doesn't take up the line; a job trigger goes down the same per-statement path and still prints blocks, but gets no summary line.

## Sensitive parameters print as `'***'`

Any parameter whose name matches `password`, `pwd`, `secret`, `token`, `credential`, `header`, `authorization`, `apikey`, `api_key` or `cookie` has its value rendered as `'***'`. Matching is by substring, so `newPassword` and `access_token` count too.

```text
info: 【修改】时间 10:21:33.419 耗时 2.1 ms 参数 3 个(已内联) #4 SmartAdmin.Sql[0]
      UPDATE "sys_user" SET "Password"='***', "UpdateTime"='2026-09-06 10:21:33.417' WHERE "Id"=1
```

The kernel deliberately does not build this line with SqlSugar's own `UtilMethods.GetSqlString`, because that would dump password hashes and TOTP seeds verbatim. This log gets screenshotted into support tickets.

The list (`SensitiveKeys`) is shared with the operation log's parameter redaction, and the failed-SQL and slow-SQL diagnostics mask against the same one. Two outlets keeping two lists means patching one and missing the other, and the sensitive value still reaches disk down the other path.

::: warning Masking only reads names
The test is whether the parameter's name looks like a credential, not what its value holds. Sensitive columns in a consumer's own tables whose names carry none of those words (national ID, bank card, medical findings) are inlined into the statement in the clear.
:::

## The channel is `ILogger("SmartAdmin.Sql")`

Statement blocks and summary lines both leave through this log category rather than going straight to the console. As a result:

- File logging (`Logging:File`) picks them up as well, so you don't have to sit watching a terminal.
- The level is tunable on its own. Set `Logging:LogLevel:SmartAdmin.Sql` to `Warning` and ordinary statement blocks (`Information`) go quiet while the ones over the slow-SQL threshold (`Warning`) stay. A block filtered out by level skips parameter inlining and layout entirely.
- A structured sink receives the template plus its arguments, with kind, execution time, elapsed time, parameter count, `ConfigId` and sequence number each as its own field. The statement field is laid-out multi-line text with no prefix on its continuation lines. On the main database the `ConfigId` field is an empty string.

.NET's default console format (`simple`) puts `SmartAdmin.Sql[0]` on a line of its own, which adds a line to every block and separates the header from the statement. So the kernel registers a console formatter named `smartadmin` that moves the tag to the end of the header line for this category only. Every other category, along with timestamps, level colors and exceptions, comes out exactly as `simple` writes it.

The SQL colors are applied here too. The message a statement block hands out is plain text, and only when writing to the console does the formatter swap in the colored kind, time, elapsed time and keywords. If the host names its own console format, SQL on the console comes out uncolored.

The formatter also draws a gray rule after every log entry, at every level and for every category, with no blank line. When multi-line statement blocks and exceptions with stack traces sit next to each other, you can't tell where one entry ends and the next begins. The rule is only drawn when the output is colored, decided the same way `simple` decides its own colors: by `Logging:Console:FormatterOptions:ColorBehavior`, or, when that isn't set, by whether the output is redirected. Container logs and log files are collected line by line, and every extra rule would turn into an empty event.

If the host sets `Logging:Console:FormatterName` explicitly (`json`, `systemd`, or `simple` spelled out to get the default two lines back), the kernel leaves the console alone. It also stays out when the host still uses the obsolete top-level switches `Logging:Console:IncludeScopes`, `TimestampFormat` or `DisableColors`: those only apply when no formatter is named, so taking over would silently switch them off. With scopes on (`FormatterOptions:IncludeScopes`), the line under the tag is the scope line, and those blocks keep the default two-line shape too.

## Slow and failed SQL are the same block

The `Database:SlowSqlMillis` warning and the failed-SQL error are not governed by `SqlLog`; they're on by default. They print as the same kind of block, though: parameters inlined, statement laid out, and one more item after the parameter note in the header.

```text
warn: 【查询】时间 10:21:34.020 耗时 1268.4 ms 参数 1 个(已内联) · 慢 SQL(≥ 1000 ms) SmartAdmin.Sql[0]
      SELECT "Id", "OrderNo", "Amount"
      FROM "sys_order"
      WHERE ("CustomerName" LIKE '%张%')
──────────────────────────────────────────────────────────────────────────────────────────────────────────
fail: 【查询】时间 10:21:34.311 耗时 2.3 ms 参数 1 个(已内联) · 执行失败 SmartAdmin.Sql[0]
      SELECT 1 FROM "sys_ordr" WHERE ("Id" = 42)
      SqlSugar.SqlSugarException: 42P01: relation "sys_ordr" does not exist
──────────────────────────────────────────────────────────────────────────────────────────────────────────
```

Log platforms search for the two terms 慢 SQL (slow SQL) and 执行失败 (execution failed). With `SqlLog` on, a statement over the line still gets just this one block, raised to `Warning`, and no second entry. Truncation follows `SqlLog`: with it on, statements are cut at `MaxSqlChars`; with it off, they print in full, because the complete statement is what a production investigation needs. Color works the same way and appears only while `SqlLog` is on.

## Don't turn it on in production

Log volume is the first layer: several statements per request, at least two lines each, every one carrying the full statement. The second layer costs more. The masking above only reads parameter names, so values whose names miss the list land verbatim in your log files, your log pipeline, and the hands of anyone with permission to read either.

Raising `MinMillis` is not the same as turning it off; it just prints fewer statement blocks while the statistics and the summary keep running. To turn it off, set `Enabled` back to `false`.

To keep a performance trail in production, `Database:SlowSqlMillis` is enough: it prints only the statements that cross the line, which is orders of magnitude less.
