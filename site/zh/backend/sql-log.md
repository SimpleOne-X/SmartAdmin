# SQL 控制台日志

一个请求到底打了 2 条 SQL 还是 143 条，逐条看是看不出来的，因为每条都很快。开了 `SmartAdmin:Database:SqlLog`，每条语句打一个块，请求结束再补一行汇总，差别就落在那行汇总上。默认关着，它是开发期的工具。

慢 SQL 阈值 `Database:SlowSqlMillis` 和失败 SQL 那两条诊断日志是另一回事，它们默认就在、生产也该开着，配置说明在[部署指南](/zh/guide/deployment/)。它们打出来也是下面这种语句块，见[慢 SQL 与失败 SQL 也是这种块](#慢-sql-与失败-sql-也是这种块)。

## 开关

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

| 键 | 默认 | 作用 |
| --- | --- | --- |
| `Enabled` | `false` | 总开关 |
| `MinMillis` | `0` | 只打耗时不低于本值（毫秒）的语句；`0` 全打 |
| `MaxSqlChars` | `4000` | 单条语句的打印上限，超出截断并在末尾注明。批量插入的语句能有几十万字符 |
| `RequestSummary` | `true` | 请求结束是否补一行汇总 |
| `Color` | 自动 | ANSI 配色。不配则 stdout 是终端、且未设 `NO_COLOR` 时才上色 |

同时开着文件日志的话，`Color` 请显式配 `false`，否则文件里会混进一堆转义序列。

`MinMillis` 只挡语句块，不挡统计：被它滤掉的语句照样计入汇总的条数与合计耗时。N+1 的每一条都不慢，要是按耗时过滤之后连条数也跟着少了，这个开关就白开了。

## 一条语句打一个块

```text
info: 【查询】时间 10:21:33.402 耗时 3.4 ms 参数 2 个(已内联) #1 SmartAdmin.Sql[0]
      SELECT COUNT(1) FROM "sys_user" WHERE ("IsDelete" = false) AND ("OrgId" IN (100, 101))
info: 【查询】时间 10:21:33.415 耗时 11.8 ms 参数 2 个(已内联) #2 SmartAdmin.Sql[0]
      SELECT "Id", "Account", "RealName", "OrgId"
      FROM "sys_user"
      WHERE ("IsDelete" = false)
      AND ("OrgId" IN (100, 101))
      ORDER BY "Id" DESC
      LIMIT 10 OFFSET 0
```

头行之下就是语句本身，每行前面不带框线或别的前缀，框选下来直接贴进客户端就能跑。

头行依次是语句类型、执行时刻、耗时、参数个数、序号，行尾是日志类别 `SmartAdmin.Sql[0]`。.NET 默认的控制台格式不带时间戳，对照前端请求或别的日志时靠头行这个精确到毫秒的时刻。类型按首个关键字判，带 CTE 的查询以 `WITH` 开头，也算查询。主库的语句不标库名，副库的在序号前多一个 `[Report]`，方括号里是你在 `AdditionalDatabases` 里给的 `ConfigId`，所以一眼看得出这条打的是哪个库。序号和汇总行里「最慢第几条」对得上；不在 HTTP 请求里跑的语句（启动时读表结构、定时任务）没有序号。

整条不超过 100 字就保持一行，超过才排版。子句各起一行；`WHERE`、`HAVING`、`JOIN … ON` 里顶层的 `AND`、`OR` 各起一行、与子句关键字对齐，`BETWEEN` 自带的那个 `AND` 不拆；`SELECT` 列、`SET` 赋值、`VALUES` 行、`GROUP BY`、`ORDER BY` 放不下一行时，关键字独占一行、各项逐行缩进；放不下的子查询缩进一级展开。

排版只动 token 之间的空白和关键字的大小写，字符串、带引号的标识符一个字不改，排完照样能贴进客户端跑。唯一的例外是行注释，它会改写成 `/* … */`，否则几行并成一行之后，后面的语句全被吞进注释里。超过 2 万字的语句（多半是批量插入）原样一行打出：拆成上千行没人读，排版的开销却随长度涨。常见语句的排版只要几微秒到几十微秒。

颜色落在三处。语句类型和语句里的关键字按种类上色：查询绿、新增黄、修改青、删除红、建表紫、其它灰，值保持默认色。耗时按慢 SQL 阈值分三档：越线红，到一半黄，其余绿。时刻是灰色。

参数不另起一行列出来，直接内联进语句。排查时要的是一条能贴进客户端就跑的 SQL，而不是「语句在这里、参数在那里」再人肉拼一遍。值按方言写成字面量。布尔在 PostgreSQL 上写成 `true`、`false`，因为它的 `boolean` 列不跟整数比；其余方言写成 `1`、`0`。SqlServer 上含非 ASCII 字符的字符串带 `N` 前缀，不带的话按库的排序规则走代码页，中文可能变成 `?`，一行也查不出来。

## 汇总行才是查 N+1 的入口

```text
info: 【汇总】GET /api/v1/sys/user/page → 2 条 / 15.2 ms / 最慢 #2 11.8 ms SmartAdmin.Sql[0]
```

条数、合计耗时、最慢的是第几条，开头那句话就靠这一行兑现。条数到 20 还会在行尾直接点名：

```text
info: 【汇总】GET /api/v1/sys/order/page → 143 条 / 812.6 ms / 最慢 #7 24.1 ms  ← 143 条,疑似 N+1 SmartAdmin.Sql[0]
```

汇总只有 HTTP 请求有。一条语句都没跑的请求（静态资源、404）不占这一行；定时任务触发走的是同一条逐语句路径，语句块照打，但没有汇总行。

## 敏感参数只打 `'***'`

参数名命中 `password`、`pwd`、`secret`、`token`、`credential`、`header`、`authorization`、`apikey`、`api_key`、`cookie` 的，值一律渲染成 `'***'`。是子串匹配，所以 `newPassword`、`access_token` 都算。

```text
info: 【修改】时间 10:21:33.419 耗时 2.1 ms 参数 3 个(已内联) #4 SmartAdmin.Sql[0]
      UPDATE "sys_user" SET "Password"='***', "UpdateTime"='2026-09-06 10:21:33.417' WHERE "Id"=1
```

内核没有用 SqlSugar 自带的 `UtilMethods.GetSqlString` 来拼这一行，就是因为它会把口令哈希、TOTP 种子这类值原样吐出来。这份日志是要被截图贴进工单的。

名单（`SensitiveKeys`）与操作日志的入参脱敏共用一份，失败 SQL、慢 SQL 那两条诊断日志也照它打码。两条出口各拿一份名单，补了这边漏了那边，敏感值照样从另一条路落盘。

::: warning 打码只认名字
判据是参数名像不像口令，不看值里装的是什么。消费方业务表里那些名字不带这些词的敏感列（身份证号、银行卡号、体检结论），内联进语句之后就是原文。
:::

## 通道是 `ILogger("SmartAdmin.Sql")`

语句块和汇总行都从这个日志类别出去，不是直接写控制台。于是：

- 开了文件日志（`Logging:File`）同样收得到，不必守着终端看。
- 级别能单独调。`Logging:LogLevel:SmartAdmin.Sql` 设成 `Warning`，普通语句块（`Information`）就安静了，越过慢 SQL 阈值的那些（`Warning`）还在。被级别滤掉的语句块连参数内联和排版都不做。
- 结构化 sink 收到的是模板加参数，类型、时刻、耗时、参数个数、`ConfigId`、序号各是一个字段。语句字段是排好版的多行文本，续行不带前缀。主库的 `ConfigId` 字段是空串。

.NET 默认的控制台格式（`simple`）会让 `SmartAdmin.Sql[0]` 单独占一行，每个块都多出一行，还把头行和语句隔开。所以内核注册了一个名为 `smartadmin` 的控制台格式化器，它只对这个类别把标签挪到头行末尾。其余类别，连同时间戳、级别配色、异常，都照 `simple` 原样输出。

宿主显式配了 `Logging:Console:FormatterName`（`json`、`systemd`，或者写明 `simple` 退回默认的两行）就不接管。还在用 `Logging:Console:IncludeScopes`、`TimestampFormat`、`DisableColors` 这些过时顶层开关的也不接管，因为它们只在没指定格式时才生效，接管了就会悄悄失效。开了作用域（`FormatterOptions:IncludeScopes`）时，标签下面先是一行作用域，这种块也保持默认的两行。

## 慢 SQL 与失败 SQL 也是这种块

`Database:SlowSqlMillis` 的告警和失败 SQL 的错误日志不归 `SqlLog` 管，默认就在。打出来却是同一种块：参数照样内联，语句照样排版，头行在参数说明后面多标一项。

```text
warn: 【查询】时间 10:21:34.020 耗时 1268.4 ms 参数 1 个(已内联) · 慢 SQL(≥ 1000 ms) SmartAdmin.Sql[0]
      SELECT "Id", "OrderNo", "Amount"
      FROM "sys_order"
      WHERE ("CustomerName" LIKE '%张%')
fail: 【查询】时间 10:21:34.311 耗时 2.3 ms 参数 1 个(已内联) · 执行失败 SmartAdmin.Sql[0]
      SELECT 1 FROM "sys_ordr" WHERE ("Id" = 42)
      SqlSugar.SqlSugarException: 42P01: relation "sys_ordr" does not exist
```

日志平台按「慢 SQL」「执行失败」这两个词检索。开着 `SqlLog` 时，越线的语句也只打这一块，级别升成 `Warning`，不会再另打一条。截断跟着 `SqlLog` 走：开着就按 `MaxSqlChars` 截；没开时打全文，生产上排障要的就是那条完整语句。颜色同理，只有开着 `SqlLog` 才上色。

## 生产别开

日志量是第一层：每请求若干条语句，每条至少两行，还都带着完整语句。第二层更值钱，上面那条打码只认参数名，命不中名单的值会原样落进日志文件、日志采集，以及任何有权限看这些的人手里。

`MinMillis` 调大不算关掉它，那只是少打几条语句块，统计与汇总照跑。要关就把 `Enabled` 置回 `false`。

生产上想留一条性能线索，配 `Database:SlowSqlMillis` 就够：只有越线的语句才打，量级差着好几个数量级。
