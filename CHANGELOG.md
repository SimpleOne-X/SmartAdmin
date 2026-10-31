# 更新日志

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

版本号规则：**主版本号 = 内核所用的 .NET 主版本**（10.x 对应 .NET 10，下一次大版本跟随下一个 .NET LTS），次版本号加功能，修订号修 bug；全部 NuGet 包、前端包 `smart-admin-web` 与 `SmartAdmin.Templates` 共用一个版本号。破坏性变更尽量攒到换 .NET 大版本时一起发；周期内确需破坏的在次版本发，并在该版本段落顶部加粗提示，升级前先读该版本的「升级说明」。

发布节奏：**开发在 `dev` 上进行，发布在 `main` 上完成**。先经 PR 把 `dev` 合进 `main`，合并后 `release` workflow 自动接手：合并进去的提交里版本号变了、且还没有对应的 `v*` tag，就先确认该提交的 CI 已经全绿，再跑构建、测试和模板冒烟（`dotnet new smart-app` 必须能还原并编译通过），全绿才打包，经 Trusted Publishing 推 nuget.org、发 npm，最后才建 `v*` tag 与 GitHub Release。

逐步的发版操作清单（写条目、定版、开 PR、核对发布）见 [`docs/releasing.md`](https://github.com/SimpleOne-X/SmartAdmin/blob/main/docs/releasing.md)。

> 发版时**前后端版本号必须一起改**：后端版本由发版的版本号经 `-p:Version` 注入；前端版本写在 `web/package.json`、`web/packages/admin/package.json`（发到 npm 的包）与 `web/template/package.json`（**显示在模板登录页页脚**），模板对 `smart-admin-web` 的依赖钉同一个号，文档站导航的两处版本徽章也跟着改。漏改一处，`release` 的 verify 就会拦下，否则 npm 上的包、界面上的版本会和实际安装的 NuGet 包对不上。

> **发了什么，以本文件为准**，而不是仓库的发行版页面：那里只是可选的镜像，内容从本文件对应版本的段落复制过去。

## Unreleased

### 升级说明

**本版含破坏性变更：`OrgAuditEntity` 不再实现 `IOrgScoped`，改实现新增的 `IOrgShared`。**
继承了 `OrgAuditEntity` 的应用，升级前先读这一节。只用 `BaseEntity`、`DataEntity` 的应用不受影响。
前五条是要做的事，漏改的后果写在各条里，大多不会有编译错误；最后一条是行为变化，不用改代码。

- **把 `.ClearFilter<IOrgScoped>()` 全局替换成 `.ClearFilter<IOrgScoped, IOrgShared>()`。**
  - `ClearFilter` 按过滤器注册的接口精确匹配。
    `OrgAuditEntity` 的机构范围过滤器现在注册在 `IOrgShared` 上，旧写法清不掉它。
  - **漏改编译照常通过，也不报错，查询结果却悄悄受机构范围限制**，比如全库判重查不到别的机构的行。
  - 已经写成 `ClearFilter<ISoftDelete, IOrgScoped>()` 的，同样把 `IOrgShared` 加进去，
    写成 `ClearFilter<ISoftDelete, IOrgScoped, IOrgShared>()`。
  - 新写法用在 `DataEntity` 上也无害，可以不加区分地全局替换。
- **给所有 `OrgAuditEntity` 表加一个可空布尔列 `IsShared`。** 存量行补出来都是 `NULL`，等同不共享，可见范围不变。
  - 启动时 CodeFirst 开着的环境（`Development` 默认开）自动加列。
    内核架构版本由 `7` 升到 `8`，钉了 `CodeFirstVersion` 的应用升级后会因此重扫一次，不用改它。
  - CodeFirst 关着的环境（生产默认关）要先手工 `ALTER TABLE` 加列，再启动新版本。
    不加的话，主库上的表会被启动期的缺列检查拦下，错误点名表和列；
    本次启动配 `SmartAdmin:Database:EnableCodeFirstInProduction=true`，也能让应用自己补列。
  - **CodeFirst 只扫主库。** `SmartAdmin:AdditionalDatabases` 副库上的 `OrgAuditEntity` 表同样要手工加列。
    副库不在启动检查的范围内，漏了要到第一次查询才报缺列。
  - 不确定列定义时，在预发库用新版本开闸门启动一次
    （SQL 日志默认关，先配 `SmartAdmin:Database:SqlLog:Enabled=true`），把日志里的补列语句照抄到生产库。
    副库上的表加列，方言与预发库相同时，也可以照抄日志里同一条补列语句。
- **测试宿主里自己注册的 `AddTableFilter<IOrgScoped>(...)` 不再作用于 `OrgAuditEntity`，要再注册一个 `AddTableFilter<IOrgShared>(...)`。**
  表达式照 `IOrgScoped` 那条写，末尾多一个 `|| e.IsShared == true` 分支。
  不补的话，涉及 `OrgAuditEntity` 的隔离测试测的就不是真实行为。
- **只传 `db` 的 `: SqlSugarRepository<T>(db)` 子类，用在 `OrgAuditEntity` 上时，要把 `IDataScopeContext` 传给构造函数。**
  构造函数新增的是可选参数 `IDataScopeContext? dataScope = null`，
  不传编译照常通过，更新或删除这类实体时却抛 `InvalidOperationException`。
  插入不经过写守卫，不受影响。
  DI 注入的 `IRepository<>` 自带它，也不受影响。
- **搜一遍代码里的 `IOrgScoped`：`OrgAuditEntity` 不再实现它，凡是拿它判断类型或做泛型约束的地方，用在 `OrgAuditEntity` 上结果会变。**
  - `x is IOrgScoped` 这类判断，对 `OrgAuditEntity` 现在为假，原来为真，编译不报错。
  - `where T : IOrgScoped` 的泛型帮助方法，传 `OrgAuditEntity` 子类现在编译不过。
  - 想让两类机构隔离实体都走原来的逻辑，同时判断 `IOrgShared`，比如 `x is IOrgScoped or IOrgShared`。
- **升级后存量行的可见范围不变。开始用共享之后，有两处行为要知道。**
  - 别的机构更新或删除共享行抛 `ErrorCode.OrgOutOfScope`（`42030`），不是返回 0 行。
    私有行被越权更新或删除仍返回 0 行。
  - 共享行对「仅本人」范围和空范围的用户也可见，所以「空范围查不到任何数据」今后只对继承 `DataEntity` 的表成立。
    接口权限照常校验。

### Added

- **`OrgAuditEntity` 的行可以按行共享给所有机构。**
  新增接口 `IOrgShared` 和可空列 `IsShared`。
  把某一行的 `IsShared` 设成 `true`，所有机构的用户都能查到它，包括范围为「仅本人」和空范围的用户；
  `null` 与 `false` 都表示不共享。
  同一张表里私有行和共享行并存，基类不用换，也不用改任何角色的数据范围。
  - 共享行所有机构可读，能写的只有两类用户：数据范围含归属机构的，以及范围含「仅本人」的创建人。
    他们可以更新、删除、打开或关闭共享标记，别的用户抛 `ErrorCode.OrgOutOfScope`。
    `CreateOrgId` 为空的共享行没有归属机构，受限用户里只有范围含「仅本人」的创建人能写。
  - 新增 `DataScopeResult.Owns(createOrgId, createUserId)`。
    写守卫用它判断归属，业务服务也可以用它给 DTO 算「能否编辑」。
  - 种子升级同步不会覆盖 `IsShared`。
  - 共享行是物理删除，没有回收站。`DataEntity` 不带共享能力，表结构和行为都不变。
  - 内核拦不了的两条约定：明细表不带机构字段、接口先校验主表；共享行不要引用某个机构的私有数据。
    写法见文档站「多组织数据权限」一页的「按行共享」一节。

### Fixed

- **数据层文档和实体注释把 `CreateOrgId` 为 `null` 的行说成「不受机构范围约束」，实际受限用户看不到这些行。**
  行为本来如此，没有改，只改了说明：这类行只有范围含「仅本人」的创建人和不受限用户能看到。

## 10.31.1 - 2026-10-29

### Added

- **导入向导按消费方自己的错误码显示单元格错误文案。** `CellError` 新增 `MsgKey`，与统一信封同一套解析：消费方在 `IImportProfile.ValidateRowAsync` 里报 `(ErrorCode)自己的码`，只要那个枚举登记过，单元格错误就带它自己的 `[MsgKey]`。前端新增 `translateCellError`：先按 `msgKey` 翻译并用 `args` 填占位符，查不到再按数字码回落；`ImportWizard` 的单元格提示、错误数提示和列映射告警都改用它。以前这些码在向导里一律显示兜底文案。内核 46xxx 码的显示不变。

### Security

- **前端容器镜像在运行层补装 Alpine 的安全更新。** `caddy:2-alpine` 发布之后 Alpine 才修的包（`brotli-libs` 的 CVE-2026-111497、`curl` 的 CVE-2026-92392）在基础镜像里还是旧版，镜像扫描的 CRITICAL 闸门判红。`web/Dockerfile` 在运行层加 `apk upgrade --no-cache`，构建时取到修复版。caddy 二进制里 Go 依赖的漏洞要等上游重建镜像。

## 10.31.0 - 2026-10-29

### 升级说明

- **没配 `AbsoluteHours` 的部署，一直在用的会话不再到登录满 `refreshMinutes`（默认 7 天）时失效。** 会话改为在连续 `refreshMinutes` 没续期后才失效。要「登录满 N 小时必须重登」的部署，显式配 `SmartAdmin:Security:Session:AbsoluteHours`；配了之后期限仍从登录时刻算，续期不能突破，与以前相同。
- **刷新接口新增业务码 40037（`RefreshTokenSuperseded`）。** 它表示这张刷新令牌刚被同一会话的另一个请求换掉，会话还在。自己写客户端调 `POST /api/v1/auth/refresh` 的，收到它时不要清会话：改用另一处已经拿到的新令牌，或者稍后再试。

### Fixed

- **挂墙常开的页面不再到登录满 7 天就被踢回登录页。** 没开 `AbsoluteHours` 时，每次续期都把会话期限顺延到「续期时刻 + `refreshMinutes`」，`sys_session` 和缓存同时更新；会话在连续 `refreshMinutes` 没续期后失效。开了 `AbsoluteHours` 的，行为不变。
- **同一浏览器开着多个标签页时，访问令牌同时到期不再吊销整个会话。** 以前两个标签页拿同一张刷新令牌续期，后到的那个被当成重放，整个会话被吊销。现在令牌刚轮换后 10 秒内再出示旧令牌，或并发轮换中落败，只回 40037、不吊销；前端随即改用其它标签页写进 localStorage 的新令牌。10 秒以外的重放仍然吊销整个会话。一个标签页退出登录后，其它标签页也回到登录页。
- **续期请求撞上后端重启、断网、超时、5xx 或 429 时，不再跳登录页。** 只有服务端明确判定凭证无效时才清会话，即刷新接口回 401 / 403，或业务码为 40005、40006、40007。其它情况都保留会话，本次请求按网络错误失败，不再把原来的 401 交回调用方；后端恢复后，下一次请求会自动续上。刷新页面时遇到这种情况，会进入可重试的启动错误页，不清会话；要去的是登录页或公开页时直接放行。要去的是 `/fullscreen/` 整屏页时，错误页每 30 秒自动重试，后端恢复后回到原页面，无人值守的大屏不用人去点。
- **登录后回到原来的地址。** 未登录时打开的页面（含查询串），登录成功后原样打开，账密、MFA 和第三方登录都能回去。回跳只接受站内路径，以 `//`、`/\` 开头的地址（含 `%2F`、`%5C` 编码形式）和绝对地址一律丢弃。直接打开 `/fullscreen/` 整屏页时，不会被应用选择页拦下。

### Security

- **vue-i18n 升到 11.4.16，修 GHSA-83rm-6c8r-g728。** 11.4.13 及以下的 `escapeParameter` 挡不住翻译文案里经 `srcdoc` 注入的 XSS。前端包对 vue-i18n 的 peer 范围 `^11.4.10` 不变，消费方自己的锁文件要升到 11.4.14 以上。

## 10.30.5 - 2026-10-26

### Fixed

- **关着建表闸门时启动不再多等十几秒，启动的第一条 SQL 也不再被误报成慢 SQL。** 建表跳过时的缺列检查以前逐表 `IsAnyTable`，再用 SqlSugar 的 `GetColumnInfosByTableName` 取列；这条查询在 SqlServer 上要联查索引、默认值与注释，远端库上一张表几百毫秒，44 张内核表实测合计 14.6 秒。现在表清单一条 SQL 取齐，列名读空结果集的表头，同一个库上降到 0.1 秒左右，缺列照样点名到列。初始化器先单独建好第一条物理连接，打一行 `数据库首次建连 N ms`：SQL 耗时从打开连接前算起，驱动冷启动加登录的近一秒以前全记在第一条探表语句头上，触发慢 SQL 告警。

## 10.30.4 - 2026-10-26

### Fixed

- **弹窗里带边框的表格在 125% 等缩放下有时缺左边框。** 弹窗卡的毛玻璃（`backdrop-filter`）让 Chrome 单独合成弹窗内容，表格外框恰好落在整数设备像素上时（随窗口宽度，大约一半的宽度会碰上），表头和表体会盖住 1px 的左边框，右边框照常。现在弹窗里的 `.n-data-table-wrapper` 单独合成一层，任何窗口宽度下左边框都在；`SmartTable` 与裸 `n-data-table` 都生效，抽屉不受影响。

## 10.30.3 - 2026-10-26

### Fixed

- **SQL 日志的颜色不再漏进 VS 输出窗口、文件和事件日志。** 以前 ANSI 码直接嵌在日志参数里，在 Visual Studio 里调试时控制台是彩色的，输出窗口却满屏 `[90m`、`[92m`；同时开着文件日志或 Seq 的话，里面也混进了转义序列。现在交给所有日志提供程序的都是纯文本，颜色只由内核的控制台格式化器（`smartadmin`）在写控制台时加上，控制台上看到的和以前一样。宿主自己配了 `Logging:Console:FormatterName` 的，控制台上的 SQL 不再带颜色。直接调用 `SqlLogFormatter.BlockArgs`、`SummaryArgs` 并传 `color: true` 的，上色的那几个值现在是 `ColoredText`：`ToString()` 是纯文本，上色版在 `Ansi` 属性上。

## 10.30.2 - 2026-10-25

### Changed

- **控制台上每条日志后面跟一条灰色分割线。** 所有级别、所有类别都画，不加空行，多行的 SQL 块和带堆栈的异常挨在一起也分得清。只在上色时画，判断和 `simple` 自己是否上色相同：看 `Logging:Console:FormatterOptions:ColorBehavior`，没配就看输出有没有被重定向，所以容器日志、写文件里不会出现。只在内核接管控制台格式（`smartadmin`）时生效；不想要的话配 `Logging:Console:FormatterName=simple`，代价是 `SmartAdmin.Sql[0]` 回到单独一行。

## 10.30.1 - 2026-10-25

### 升级说明

- **直接调用 `SqlLogFormatter` 的，语句块的模板和参数顺序又变了。** `BLOCK_TEMPLATE` 改成头行加语句两段，`BlockArgs` 仍返回七个值，依次是类型、时刻、耗时、参数说明、`ConfigId`、序号、语句；序号带前导空格（` #3`），没有序号时是空串。`BlockArgs` 末尾新增可选参数 `dbType` 和 `failed`。主库的 `ConfigId` 一栏是空串，副库是 ` [库名]`。`SUMMARY_TEMPLATE` 开头由 `└ SQL 汇总` 改为 `【汇总】`。日志类别名有了常量 `SqlLogFormatter.CATEGORY`（`SmartAdmin.Sql`）。按 `┌ SQL`、`SQL 汇总` 检索日志的脚本和告警规则要跟着改。
- **慢 SQL、失败 SQL 的日志换成了语句块格式，按旧文本检索的告警规则要改。** 以前是一行 `慢 SQL[SmartAdmin](1268ms ≥ 1000ms): … | 参数: …` 和 `SQL 执行失败[SmartAdmin]: … | 参数: …`，现在和控制台日志同一种块：参数内联进语句，头行在参数说明后面标 `· 慢 SQL(≥ 1000 ms)` 或 `· 执行失败`。级别没变（慢 SQL 是 `Warning`，失败是 `Error`），类别仍是 `SmartAdmin.Sql`。
- **控制台上 `SmartAdmin.Sql[0]` 不再单独占一行，并到了头行末尾。** 内核注册了名为 `smartadmin` 的控制台格式化器，只在宿主没配 `Logging:Console:FormatterName`、也没用过时的 `Logging:Console:IncludeScopes` 这类顶层开关时接管，其余类别照 `simple` 原样输出。想要回默认的两行，配 `Logging:Console:FormatterName=simple`。

### Changed

- **SQL 控制台日志去掉框线，打出来的语句框选下来就能贴进客户端跑。** 头行改为 `info: 【查询】时间 18:40:56.360 耗时 390.9 ms 参数 2 个(已内联) #1 SmartAdmin.Sql[0]`，主库不再标 `[SmartAdmin]`，副库的语句仍标库名；语句从下一行起，每行不带前缀；汇总行改为 `【汇总】…`。条件里顶层的 `AND`、`OR` 与 `WHERE` 对齐，不再缩进。
- **内联参数按方言写字面量。** PostgreSQL 上布尔写成 `true`、`false`，以前的 `0`、`1` 贴过去会报 `boolean = integer`；SqlServer 上含非 ASCII 字符的字符串带 `N` 前缀。
- **慢 SQL、失败 SQL 也按语句块打，能直接贴进客户端重放。** 没开 `Database:SqlLog` 时不截断、不上色，生产上拿到的是完整语句。

## 10.30.0 - 2026-10-23

### 升级说明

- **直接调用 `SqlLogFormatter` 的，语句块参数的顺序和个数变了。** 块头加了执行时刻，`BlockArgs` 返回七个值，依次是时刻、类型、耗时、`ConfigId`、序号、语句、截断提示；按下标取值的代码要跟着改。`BLOCK_TEMPLATE` 由 `const` 改为 `static readonly`（换行取 `Environment.NewLine`），用在 `const` 或特性参数里的编译不过。只经 `SmartAdmin:Database:SqlLog` 开关使用的不受影响。

### Changed

- **SQL 控制台日志按子句排版。** 不超过 100 字的语句保持一行；更长的子句各起一行，`WHERE`、`HAVING`、`JOIN … ON` 里顶层的 `AND`、`OR` 逐个起行，放不下的列表逐项起行、子查询缩进展开。只动空白和关键字大小写，排完照样能贴进客户端跑；行注释改写成 `/* … */`。超过 2 万字的语句原样一行打出。被日志级别滤掉的语句块不做参数内联和排版。
- **语句里的关键字按语句类型上色，块头加执行时刻（精确到毫秒）。** 配色改用亮色，修改由蓝改青；块头顺序改为时刻、类型、耗时、`ConfigId`、序号。

### Fixed

- **Windows 上 SQL 语句块的续行顶格，框线对不齐。** 模板里写死的 `\n` 不会被控制台格式化器补缩进，改用 `Environment.NewLine`。
- **不在 HTTP 请求里的语句（启动时读表结构、定时任务）块头多出一个空格。** 没有序号时不再留占位。

## 10.29.0 - 2026-10-23

### 升级说明

- **建表闸门改为只放行 `Development`：用 `Staging`、`UAT` 等环境名部署、又靠应用自动建表或补列的项目，空库首启和内核加列的那次启动会被拦下。**
  以前只有 `Production` 默认不建表，其余环境名照常自动建表改表；现在 Development 以外的环境一律视同部署。被拦时启动错误会点名缺的表和列，按提示在那次启动配上 `SmartAdmin:Database:EnableCodeFirstInProduction=true`，或由 DBA 先建好表再启动。表结构已经齐全的日常启动不受影响。
- **照仓库自带 `docker-compose.yml` 部署的，空库首启和带表结构变更的升级要多带一个变量。** compose 里的建表闸门从常开改为默认关，那一次用 `SMART_DB_CODEFIRST=true docker compose up -d --build` 打开，起来后再跑一次不带它的 `docker compose up -d` 关回去。

### Changed

- **CodeFirst 自动建表只在 `Development` 环境默认开启。** `SmartAdmin:Database:EnableCodeFirstInProduction` 的含义随之变为「Development 以外的环境是否允许建表改表」，配置键名不变。
- **仓库自带的 `docker-compose.yml` 不再每次启动都跑 CodeFirst。** 建表闸门改由 `SMART_DB_CODEFIRST` 控制，默认 `false`；建表改表是部署时做一次的事，日常启动不该带着改表的权限。

### Fixed

- **数据库账号只有增删改查权限时，应用在 SQL Server 上起不来。** 建表闸门关着也没用：雪花 WorkerId 租约守卫启动时会自己对 `sys_worker_lease` 跑一遍 CodeFirst，SQL Server 上这一步要删改表注释，需要 ALTER 权限。守卫不再建表改表；闸门关着且库里缺这张表时，启动错误点名缺表并给出两条出路。日常运行的账号在 SQL Server 上给 `db_datareader` 加 `db_datawriter` 就够。

### Security

- **启动日志「数据库就绪」一行不再打印完整连接串。** 以前整条连接串原样写进 Information 日志，口令是明文；现在只留服务器、端口、库名和 SQLite 文件路径，其余键一律不打。日志已经落盘或进了日志平台的，建议轮换数据库口令。

## 10.28.0 - 2026-10-22

### 升级说明

- **「文件管理」目录下两个页面的菜单编号对调：「文件路径」改为 `610`–`619`，「文件管理」改为 `620`–`625`。已经在用内核的库，升级前要先跑一遍迁移脚本 [`scripts/upgrade/renumber-file-menus.sql`](https://github.com/SimpleOne-X/SmartAdmin/blob/main/scripts/upgrade/renumber-file-menus.sql)。**
  顺序是：停掉应用，备份库，执行脚本，再升级并启动新版本。10.26 及更早的库（只有「文件管理」、还没有「文件路径」）也要跑，它会把「文件管理」挪出 `610` 段，给新版本腾位置。
  脚本把两个页面、它们的按钮，以及 `sys_role_menu`、`sys_user_menu` 里指向它们的授权一起改号，角色和用户已有的授权不丢，挂在这两个页面下的自建按钮跟着改挂。两个页面的排序值还是种子默认值时一并对调，让「文件路径」排在前面，在菜单管理页改过排序的不动。
  SQLite、MySQL、SQL Server、PostgreSQL 通用，建议放进事务里执行。已经迁移过的库、全新库再跑什么都不改。新版本已经启动过的库，脚本也不会动手（新版本此时已在旧编号上插了几行），请从备份恢复后按上面的顺序重来。
  自己的代码、种子、测试里写死了这几个菜单 Id 的，要同步改。装了 Redis 的，升级后到缓存管理页重建一次「门户菜单缓存」，否则缓存里的菜单树还是旧编号，直到自然过期。

### Changed

- **菜单种子：「文件管理」目录下，「文件路径」页排在「文件管理」页前面。**
  「文件路径」页及其按钮改为 Id `610`–`619`（页面排序 1），「文件管理」页及其按钮改为 `620`、`621`、`622`、`624`、`625`（页面排序 2）。权限码、路由、图标都不变。
  新库直接是新编号；已有库先跑上面升级说明里的迁移脚本。10.27.0 的更新日志里写的 `620`–`629` 是当时的编号。

## 10.27.0 - 2026-10-21

### Added

- **文件路径：管理员在后台登记文件服务器和文件夹，业务代码只认一个编码，就能把附件传到 NAS 或共享文件夹，再按编码下载。**
  与原有的「文件管理」是两套独立的上传下载，接口和组件互不通用。
  - 新接口，四个控制器，都挂在模块 `FilePath` 下，可经 `SmartAdmin:Api:DisabledModules` 一起关闭：
    - 文件路径配置：`GET /api/v1/sys/file-path/page`、`GET /api/v1/sys/file-path/{id}`、`GET /api/v1/sys/file-path/status`、`GET /api/v1/sys/file-path/environment`、`POST /api/v1/sys/file-path/add`、`PUT /api/v1/sys/file-path/{id}`、`DELETE /api/v1/sys/file-path/{id}`、`POST /api/v1/sys/file-path/test`。
    - 文件服务器配置：`GET /api/v1/sys/file-server/page`、`GET /api/v1/sys/file-server/{id}`、`GET /api/v1/sys/file-server/status`、`GET /api/v1/sys/file-server/options`、`POST /api/v1/sys/file-server/add`、`PUT /api/v1/sys/file-server/{id}`、`DELETE /api/v1/sys/file-server/{id}`、`POST /api/v1/sys/file-server/test`。
    - 单文件与分片上传：`POST /api/v1/sys/file-path/upload/{code}`、`POST /api/v1/sys/file-path/upload/{code}/chunk/init`、`POST /api/v1/sys/file-path/upload/{code}/chunk`、`POST /api/v1/sys/file-path/upload/{code}/chunk/complete`。
    - 下载：`GET /api/v1/sys/file-path/download/{code}`；匿名可读的签名直链是 `GET /api/v1/sys/file-path/download/{code}/view`。
  - 新组件 `FilePathUpload`（`smart-admin-web`），自动分片；一并导出 `uploadToFilePath`、`filePathFileApi`。
  - 后端新增 `IFilePathUploadService`、`IFilePathDownloadService`、`IFilePathLinkSigner`，供业务在后端写文件、取文件、签直链。
  - 文件服务器出现网络类失败后，30 秒内对它的上传、下载、删除直接报 `44114`，不再卡到系统的网络超时；每台服务器同时最多 16 个文件操作，超出报「繁忙」。可经 `IFilePathCircuit` 替换。
  - 新配置节 `SmartAdmin:FilePath`：`AllowedRoots`（根目录白名单，默认空：根路径直接在文件服务器页面填，只避开程序目录和分片暂存目录，碰到报 `44158`；配了则根路径必须落在其中）、`StagingPath`（不能位于 `AllowedRoots` 之下，否则启动失败）、`StagingTtlHours`、`LinkTtlMinutes`。
  - 新错误码段 `44100`–`44199`（`44101`–`44117` 文件路径与上传下载，`44150`–`44158` 文件服务器）。
  - 新菜单「文件管理 → 文件路径」，菜单 Id `620`–`629`，随种子进老库，但不会自动授给任何角色；超管直接可见，普通管理员要在角色授权页勾选。
  - 新表 `sys_file_server`、`sys_file_path`。跳过建表的部署（生产环境且 `SmartAdmin:Database:EnableCodeFirstInProduction=false`，或 `EnableCodeFirst=false`）要先把这两张表建出来，再使用本功能。

### Security

- 仓库锁文件里 `md-editor-v3` 间接依赖的 `markdown-it` 从 14.3.2 升到 14.3.3，修复两条拒绝服务公告（GHSA-vfgh-jcmw-g37q、GHSA-gfr2-rv44-r23c）。`smart-admin-web` 的依赖范围没变，已有应用在自己的 `web/` 下运行 `npm update markdown-it` 即可拿到修复。

## 10.26.0 - 2026-10-15

### 升级说明

- **自己实现 `INoticeService`（而不是继承 `NoticeService`）的项目，要补上新增的两个重载 `UnreadCountAsync(IReadOnlyCollection<NoticeType>?)` 与 `MarkAllReadAsync(IReadOnlyCollection<NoticeType>?)`。**
  继承 `NoticeService` 的不受影响；覆写过无参 `UnreadCountAsync` / `MarkAllReadAsync` 的，顶栏总角标与不带类型的「全部已读」行为不变。

- **顶栏铃铛的面板分成「消息」「通知」两个页签。**
  「消息」是 `NoticeType.Message`（业务系统推给个人的），「通知」是 `Notice` 与 `Announcement`（管理员发布的）；各页签带自己的未读数，打开面板时先停在第一个有未读的页签。
  「全部已读」只标当前页签。「消息」页签里不再显示类型标签。页签名可在应用 locale 里覆盖 `app.notice.tabMessage`、`app.notice.tabNotice`。

### Added

- **通知接口可按类型多选筛选，供铃铛页签用。**
  - `GET /api/v1/sys/notice/mine`、`GET /api/v1/sys/notice/page`：新增查询参数 `Types`（可重复，命中任一即可；与 `Type` 同时给时两条都要满足）。
  - `GET /api/v1/sys/notice/unread-count`、`PUT /api/v1/sys/notice/read-all`：新增查询参数 `Types`，只数 / 只标这几种类型；不带时与原来一致。
  - 前端 `noticeApi.mine({ types })`、`noticeApi.unreadCount(types?)`、`noticeApi.markAllRead(types?)`。
  - 新增 i18n 键 `app.notice.tabMessage`、`app.notice.tabNotice`、`app.notice.emptyMessage`。

### Fixed

- **`SmartAdmin.SqlSugar` 直接引用 `System.Security.Cryptography.Pkcs` 10.0.13，盖过 Oracle 驱动（随 `SqlSugarCore` 传递）带来的 8.0.1。**
  8.0.1 命中 GHSA-846f-f764-76mx（高危）与 GHSA-2fmv-rv2c-59x7（中危），`-warnaserror` 下报 NU1903 / NU1902 编不过；装本包即得修补版，应用不用改。

## 10.25.0 - 2026-10-11

### 升级说明

- **自己实现 `IOrgService`、`IPositionService`、`IRoleService`、`IUserService`（而不是继承内核默认实现）的项目，要补上各自新增的 `OptionsAsync`。**
  继承 `OrgService`、`PositionService`、`SysRoleService`、`UserService` 的不受影响，构造器没有改。

- **机构、人员、角色选择器改调下面的 options 端点，界面上有这几处变化：**
  - `UserPicker`：一个搜索框同时匹配姓名与账号，去掉「姓名 / 账号」范围切换；每行只显示姓名和账号，不再显示机构名与头像（头像由姓名首字母代替）。
    i18n 键 `userPicker.searchName`、`userPicker.searchAccount` 删除，换成 `userPicker.search`；应用若引用过旧键，要改。
  - 用户管理的机构栏搜索只匹配机构名称，不再匹配机构编码；`org.searchPlaceholder` 文案随之改为只提名称。
  - 用户管理的角色下拉、列表页「角色」搜索项只列启用角色。
  - `RoleSelect` 一次取回全部启用角色，名称搜索改在浏览器里过滤；`pageSize` 仍是下拉最多列出的个数。
  - 应用自己用 `userApi.page`、`roleApi.page`、`orgApi.list`、`positionApi.page` 当选择器数据源的页面不会自动改变，仍要对应的「查询」权限码，要的话自行改调 options。

### Added

- **机构、岗位、角色、用户新增四个 options 只读端点，给选择器用：登录即可调用，不占菜单按钮权限码。**
  此前机构树、人员选择、角色选择、用户页的岗位 / 角色 / 主管下拉都借他页的「查询」接口取数，自建角色没有对应查询按钮时下拉会静默变空，没法选机构、岗位、人员。
  - `GET /api/v1/sys/org/options`：平铺 `{ id, parentId, name }`，按 Sort、Id 排序；可见范围与 `org/list` 一致。
  - `GET /api/v1/sys/position/options`：`{ id, name }`，按 Sort、Id 排序。
  - `GET /api/v1/sys/role/options`：`{ id, name }`，仅启用。
  - `GET /api/v1/sys/user/options`：分页，入参 `keyword`（姓名或账号模糊）、`orgId`、`current`、`size`，仅启用用户，只回 `{ id, name, account }`。
    数据范围与 `user/page` 一致（非超管看不到超管账号，受数据范围约束）；固定按 Id 排序、不接受客户端排序字段；每页条数受 `SmartAdmin:Api:MaxPageSize` 限制，超过回 48001。

  完整的 `list`、`page`、详情接口不变，仍在各页「查询」按钮权限后面。
  前端对应是 `orgApi.options`、`positionApi.options`、`roleApi.options`、`userApi.options`，`OrgTreeSelect`、`UserSelect`、`RoleSelect`、`UserPicker` 与用户管理页改调它们；对外 props、事件、`UserSelect` 的「姓名(账号)」label 与回显行为不变。

### Changed

- **用户管理页的下拉数据拉取失败不再静默。**
  机构栏失败时显示「加载失败」而不是「无数据」；职位、角色、主管下拉失败合并成一条提示，列表照常可用。
  `OrgTreeSelect` 失败时下拉的空态同样写「加载失败」。新增 i18n 键 `common.loadFailed`、`user.optionsLoadFailed`。

## 10.24.0 - 2026-10-11

### Added

- **`createSmartAdmin({ table })` 新增 `emptyText`：统一改表格的空值占位。**
  值为 `null` / `undefined` 时，带 `format`（如日期时间）或 `options` 的列、以及可编辑表的静态格显示这个占位，库内置是 `—`。
  传 `emptyText: ''` 就留空；不传沿用 `—`，现有应用不受影响。
  此前应用只能自己再 `app.provide(SMART_TABLE_DEFAULTS, ...)`，而 inject 取最近一次 provide、不合并，会把内核注入的 `labels`、`density`、`filterSerializer` 整份挡掉。

### Fixed

- 弹窗、抽屉、浮层里的表格设了固定列后，横向滚动时不再有下面那些列的字从固定列背后透出来。
  现象是表头和表体的固定列上叠着一层淡淡的重影，亮色、暗色都有。
  影响所有放进 `FormContainer`（弹窗与抽屉两种形态）、`n-modal`、`n-drawer`、`n-popover` 并设了 `fixed: 'left'` 或 `fixed: 'right'` 的表格；放在页面里的表格不受影响。
  原因是 naive-ui 的表格放进这几类容器时，单元格底色改取 `Modal` / `Popover` 一族变体，而这一族没被主题覆盖，仍由半透明的弹层底色 `--bg-elevated`（亮色约 88%、暗色约 90% 不透明）派生。
  现在主题把这两族变体里 `td`、`th` 的默认、悬停、排序、斑马纹底色都补成与页面内表格相同的不透明实色，随明暗主题切换；边框色不变。

## 10.23.1 - 2026-10-11

### 升级说明

- **本版只有前端有改动。**
  后端、数据库与种子没有变化，NuGet 包按统一版本号一并发布。

- **`smart-naive-table` 的 peerDependency 升到 `^3.2.2`。**
  应用要同步升级该包，模板依赖已随版本升到 `^3.2.2`。
  内核此前钉在 3.1.3，这次一并吃进 3.2.0、3.2.1 与 3.2.2，不改公开导出，外观上有这几处变化：
  - 下拉表格 `SmartSelectTable`（以及可编辑表格里 `select-table` 的编辑态）触发器右侧的箭头由 ▾ 改为放大镜。
    内核自带页面没有用到它，应用里用了的会看到图标变化，没有回退开关。
  - 开了 `card-on-narrow` 且传了 `#toolbar` 的页面，窄档工具栏多一行（约 50px）：「标题 + `#toolbar`」独占第一行，「操作 ▾」靠左、内置图标靠右落到第二行。
    没传 `#toolbar` 的页面不变；内核里受影响的是菜单管理和用户管理的窄档。
    原本在窄档用 CSS 隐藏 `#toolbar` 的内容、另在 `#toolbar-right` 里放一份的应用，升级后头部那一行是空的，可以删掉那段绕行。
  - 窄档卡片列表认树表的 `children`，带 `children-key` 的树表切成卡片后不再只剩根节点。
    自己把树摊平再喂给卡片的应用，摊平后的行若仍带 `children` 字段，会被再展开一次、出现重复：去掉摊平直接传原树，或摊平时把 `children` 置空。

### Changed

- **机构管理、菜单管理的窄档（手机）卡片列表直接用库的树形卡片，不再自己把树摊平。**
  每个节点一张卡，有子级的卡片标题前有展开 / 折叠箭头，子级按层缩进；「展开 / 折叠全部」在窄档也可用，收在「操作 ▾」里。
  菜单管理在窄档进门由「全部展开」变为与桌面一致的「全折叠」，可逐级展开；机构管理本来就默认全展开，不变。
  卡片的层级缩进改为取表格的 `indent`：机构管理每层 16px，不变；菜单管理每层由 16px 变为 26px。

## 10.23.0 - 2026-10-10

### 升级说明

- **本版只有前端有改动。**
  后端、数据库与种子没有变化，NuGet 包按统一版本号一并发布。

### Added

- **`useSidePanel()` 与 `SidePanelDrawer`：「左分组栏 + 右列表」页在窄内容区里的面板抽屉。**
  内容区窄于 1000px 时分组栏不再占一列（200px 的栏加上表格，手机上表格只剩 160px 出头），收进从左侧滑出的抽屉。
  `useSidePanel()` 返回 `{ compact, drawerOpen }`：`compact` 是「内容区 < 1000」（可传阈值），宽度回到不紧凑时 `drawerOpen` 自动置回 `false`；
  `SidePanelDrawer` 是抽屉本身（`v-model:show`、`title`、默认插槽放面板内容）。
  两者都从 `smart-admin-web` 导出；用户页的机构栏改用它们，行为不变。
  用法见 `SidePanelDrawer` 的 README，应用里自己的分组侧栏页不必再各抄一份「窄档不画 aside、工具栏按钮、左侧 `n-drawer`」。

### Changed

- **窄档的 `.fill-split` 统一叠成上下；有两栏及以上时，每栏定高 `max(440px, 72dvh)`，栏内的卡片自己滚。**
  此前窄档各栏按内容分：上栏一长，下栏被挤到首屏之外，外层是 `overflow: hidden` 时直接被裁掉；左右并排的 `--row` 在窄档里每栏只剩一半宽。
  现在 `--row` 也叠成上下，整页仍自然滚动。
  只有一栏时仍按内容分、不给定高：字典管理窄档只渲染上栏（下栏走底部抽屉），它的表现不变。
- **窄档的表单弹窗（`FormContainer` 的弹窗形态）卡片限高在视口内，正文自己滚，底栏固定。**
  内容比视口高时，此前整张卡片连同「保存 / 取消」一起随遮罩层滚，要滚到底才点得到保存。
  宽 / 中档不变。

### Fixed

- 窄档壳层下，表格形态的定高表不再塌成最小高度。
  壳层窄档把页面放回自动高度，卡片列表有自然高度所以没事；
  但表若没开 `card-on-narrow`、也没用 `:fill-height="!narrow"` 放掉定高，表体没有基准高度：页签页（`.fill-page > .fill-tabs`）里塌成 0，页签条下面整片空白，顶层表和 `.side-page` 里塌到最小行高。
  现在这类页（顶层是 `SmartTable`、`.fill-page`、`.side-page`，且页内有表格形态的 fill-height 表）在窄档保持一屏定高，滚动留给表体。
  开了 `card-on-narrow` 或随窄档放掉定高的页面不受影响，仍整页自然滚动。
- `FormContainer` 的 `fullscreen` 全屏态里，正文不再把卡片撑出视口、底栏不再被顶出屏外。
  全屏态给正文写的 `flex: 1; overflow: auto` 选择器只写了 `.n-card__content`，而 naive-ui 2.45 输出的内容层类名是 `.n-card-content`，这条规则从没命中过。
  现在两种类名都写。

## 10.22.0 - 2026-10-10

### 升级说明

- **本版只有前端有改动。**
  后端、数据库与种子没有变化，NuGet 包按统一版本号一并发布。

- **`smart-naive-table` 的 peerDependency 升到 `^3.1.3`。**
  应用要同步升级该包，模板依赖已随版本升到 `^3.1.3`。
  内核此前钉在 3.1.1，这次一并吃进 3.1.2 与 3.1.3，不改公开导出，外观上有这几处变化：
  - 条件搜索栏（`search: { container: 'table' }`）里的值输入框封顶 320px。
    此前它吃掉一行里的全部剩余宽度，表格越宽越长，宽屏上能拉到 600 多甚至 1000 多像素。
    要改上限，在表格或其祖先元素上写 CSS 变量 `--smart-table-cond-value-max-width`，设成 `none` 取消封顶。
  - 条件搜索栏所在的工具栏，宽档与中档按可用宽度排：放得下，条件栏与操作区（新增 / 更多 / 内置图标）在同一行；放不下才折成两行，操作区在上、条件栏在下。
    宽档不再是两列等分，条件栏占满头部与操作区之外的全部宽度；中档常见的笔记本宽度由两行变成一行；没有头部内容时，条件栏左缘不再比表格缩进 12px。
    头部有内容（如用户页收起机构栏后的「机构」按钮），且连「头部 + 条件栏」一行也放不下时（72px 宽的按钮约在表格宽 830px 以下），头部落在条件栏下面单独一行。
  - 条件构造器的展开面板（点「»」弹出的气泡）里的控件由 `small` 变为 `medium`，与工具栏同一档；每行高 6px，条件越多气泡越高。
  - 树列配 `ellipsis`、列窄到放不下「缩进 + 箭头 + 文字」时，「…」不再画出单元格右边界。

  应用自己写过同样兜底的样式（给值输入框限宽、给中档工具栏改并排），可以删掉。

### Added

- **用户页的机构栏改为分组卡：默认只展开一级，可向左收起。**
  - 头部是灰色小字的分组名「机构」加两个图标按钮；树没有连接线，展开箭头换成小号 chevron，层级靠缩进。
  - 默认只展开一级：根节点展开、露出下一层，更深的收起。
    头部的「展开全部」与「折叠到一级」共用一个双箭头图标，转 180° 表示状态；没有比第一层更深的层级时不出这个按钮。
  - 搜索框只在机构数超过 15 时才出现，机构少时一眼能看完，不再常驻占一行。
  - 宽屏下整栏可向左收起（头部的侧栏图标），表格铺满整行。
    收起后工具栏最左出现显示「当前机构名」的按钮，点它请回机构栏，所以收起期间也看得出筛选还在生效。
    收起状态记在本浏览器里（`localStorage` 的 `sa-user-org-panel-hidden`），下次进页面保持。
    内容区窄于 1000px 时仍是左侧抽屉，抽屉里不带分组名和收起按钮。
  - 侧栏面板的公共样式新增 `.side-filter__title`、`.side-filter__btn`（头部图标按钮）和 `.side-filter.is-hidden`（向左收起），应用自己的「左分组栏 + 右列表」页面可以直接套用。
  - 语言包新增键：`user.collapseToFirstLevel`、`user.hideOrgPanel`、`user.showOrgPanel`。覆盖过内核语言包的应用不用改。

### Changed

- **侧栏与登录页左栏的站点名称字号调大。**
  侧栏里的名称由 15px 调到 18px，登录页左栏的标题由 22px 调到 28px。

### Fixed

- 登录页的鼠标视差与柔光不再跟随系统的「减少动态效果」。
  Windows 关了「动画效果」、远程桌面、省电模式下，Chromium 系浏览器都会报 `prefers-reduced-motion: reduce`，
  原来在这个状态下视差位移和柔光都被关掉，鼠标移动时整页毫无反馈，表现为「有的浏览器会动、有的不动」。
  指针视差与柔光是这页唯一的鼠标反馈，现在不再随该偏好关闭；色场漂移与入场动画仍按系统偏好处理。
- 手机宽度下，字典管理、菜单管理、机构管理、任务监控页都能完整查看。
  - 字典管理（上下分栏）：窄档各栏回到按内容分，卡片列表不再被压在一个小框里再滚。
  - 菜单、机构（树表）：窄档表格是卡片列表，库的卡片模式不渲染子节点；现在取数时把整棵树摊成一列、标题按层级缩进，每个节点一张卡，窄档不再显示「展开 / 折叠全部」。
  - 任务监控：窄档网格显式单列，表格不再被撑到 600px 宽，换得成卡片，页面也不再横向溢出。
- 直接访问另一个应用的页面地址时，404 页说明归属并可一键切换。
  动态路由只按当前应用的菜单注册，书签、地址栏历史里记着的另一个应用的页面地址，在当前应用里只会落到 404，页面上没有任何线索。
  现在多应用用户命中 404 时，会按路径到其它应用的菜单里反查归属：
  找到就提示「该页面属于『X』应用」并提供「切换到『X』并打开」，找不到则显示当前应用名和切换应用入口。单应用部署行为不变。
  反查只在 404 时发生，不新增后端接口。
  只做整条路径相等的匹配：别的应用的详情页（`/x/:id/detail`）识别不了，退回普通 404 加切换应用入口。
  语言包新增键：`notFound.currentApp`、`notFound.otherApp`、`notFound.switchAndOpen`。覆盖过内核语言包的应用不用改。

## 10.21.0 - 2026-10-09

### 升级说明

**本版含一项安全修复，也含破坏性变更：升级时会删除新建角色上授的、除用户管理与角色管理外的系统菜单。** 升级前先读这一节。

- **安全修复：持有「用户管理」按钮权限的普通管理员，可以经接口接管超管账号。已发布的 10.20.x 都有这个问题，建议尽快升级。**
  - 问题：`PUT /api/v1/sys/user/{id}/password`（用户-重置密码）对目标是不是超管不做任何校验。
    持有这颗按钮的普通管理员重置超管的密码，再用新密码以超管登录，就拿到了最高权限。
    `GET /api/v1/sys/user/{id}` 与 `PUT /api/v1/sys/user/{id}` 同样能读、能改超管的资料。
    用户列表对数据范围是「全部」的管理员也会列出超管。
    在线会话的强制下线本来写了「非超管不得踢超管」，但这层守卫只对数据范围受限的管理员生效，「全部」范围的普通管理员可以把超管踢下线。
  - 修复：已登录的非超管看不到超管账号，数据范围是「全部」也一样。
    列表与导出的查询里排除超管，按 Id 取到超管（详情、更新、重置密码、启停、删除、批量删除）一律当作不存在，返回 `42001`；
    单独授权的读接口返回 `41005`（目标不在数据范围内）。
    批量删除里夹带超管，整批被拒，同批的普通用户也一个不删。
    强制下线：目标会话属于超管时，非超管一律被拒（`42024`，会话不存在），不再区分数据范围。
    超管自己照常能看、能管。后台任务、种子这类没有登录上下文的调用视为可信，不受限。
    `IDataScopeGuard` 的 `ScopeUsers` 与 `IsUserInScopeAsync` 多了这一条排除；自定义了范围守卫的项目，要在自己的实现里补上同样的规则。
    `UserService` 新增 `protected virtual bool IsHiddenFromCaller(SysUser)`，子类可覆写；没有改任何构造器和已有签名。
  - 行为变化：下游若有集成测试用普通管理员的身份读写超管账号（例如断言 `42007` 超管受保护），现在会得到 `42001`。
    这类用例改用超管身份，或者直接经服务调用。
  - 自查：升级前，用一个只有「用户管理」按钮权限的账号，调一次 `PUT /api/v1/sys/user/1/password`（`1` 是内核种子里超管的 Id）。
    返回成功就说明库里的超管密码可能已被改过，升级后重置超管密码并检查「操作日志」里「重置用户密码」的记录。
  - 没有改动的部分：操作日志、登录日志、在线会话列表、通知这几处，对「全部」范围的管理员仍会列出超管产生的记录（在线会话里超管那一行看得到，但踢不掉）。
    清除他人二因子（高敏感权限 `MfaClear`）仍可对超管执行，这是显式授予的能力，需要的话请只授给可信的人。

- **破坏性变更：除「用户管理」「角色管理」外，系统菜单只能授给内置角色，升级时清掉新建角色上已授的那部分。**
  - 内置角色指内核种子里固定 Id 1–999 的角色，目前是「系统管理员」和五个数据范围角色。
    界面上新建的角色，以及你自己种子里播的角色（Id ≥ 1000），都不算。
  - 系统菜单指内置「系统」应用下内核种子播的目录（Id ≤ 999）连同它们下面的整棵子树：组织管理、系统运维、任务调度、日志审计、文件管理、AI 网关管理和系统工作台。
    你挂在这些目录下的页面也算系统菜单。
  - 例外：「用户管理」「角色管理」两个页面（含它们下面的按钮，以及你挂在它们下面的节点）和承载它们的「组织管理」目录，新建角色可以授，升级时也不清理。
    普通管理员靠这两项维护用户与角色。同一个目录里的机构管理、岗位管理，以及你直接挂在「组织管理」目录下的页面，仍只能授给内置角色。
    角色授权对「角色-授权菜单」「角色-数据范围」这些写接口另有超管专属守卫，放开这两个页面不会让新建角色获得改授权的能力。
  - 你自建在「系统」应用下的目录（Id ≥ 1000）不是系统菜单，不受影响：升级时不会清理，新建角色照常可授，角色授权弹窗里照常显示。
    但它们仍属于「系统」应用，而「系统」应用固定不可转授，所以仍不能单独授权给用户；要委派，先把它们改挂到你自己的应用下。
  - 清理只在种子版本从 6 或更早（版本号读不成整数的老库也算）升级上来的那一次启动里执行。
    库就绪之后，它物理删除非内置角色在 `sys_role_menu` 里指向这类系统菜单（用户管理、角色管理和「组织管理」目录壳除外）的行，删除不可恢复。
    随后失效这些角色下用户的权限码缓存，门户菜单缓存整体重算。
    空库首启和平时重启都不会执行。
  - 日志里搜「升级清理」：删除前有一条待删汇总（条数和涉及的角色），删完后每删一行一条 Warning（角色名称与编码、菜单标题与 Id），末尾再有一条完成汇总。
  - 影响：靠新建角色进用户管理、角色管理以外的系统页面的用户，升级后失去这些入口。要用这些页面的普通管理员，改用内置的「系统管理员」角色。
  - 升级前备份 `sys_role_menu` 表。先在生产库的副本上升级一次，从启动日志里核对被删的「角色 × 菜单」清单。
  - 你自己种子里播的角色（Id ≥ 1000）同样算非内置。
    升级那一次启动，种子先把授权补回，清理再把它们删掉；之后每次重启，种子（没有登录上下文，不受守卫限制）又会把缺的行补回来。
    依赖这种做法的项目，改用内置角色，或者改自己的种子。
  - 清理只做一次，不会重试：框架先把种子版本行写成 7，再跑清理。
    查找待删授权失败时，启动因异常中止，日志里有一条 Error，写明没有删任何授权。
    删除阶段失败时同样中止启动，日志里有一条 Error，写明事务已回滚、没有删任何授权。
    重启后不会再触发清理，补救办法是超管在角色授权页对每个非内置角色（删除失败时只处理日志点名的角色也行）重新保存一次授权：弹窗里不显示这类系统菜单，保存提交的只有弹窗里看得见的授权，它们就被收回了。
    授权删完之后缓存失效失败，只写一条 Error，启动照常；到缓存管理页分别清除「权限缓存」与「门户菜单缓存」即可。
    多副本同时升级时，先到的副本把授权删光，后到的副本发现待删的行已不在库里，只记一条说明，不再逐行写「删除」。
  - 清理排在缺表守卫之前执行。跳过建表的部署缺 `sys_user_menu` 时，守卫会中断启动，清理已经先跑完，不会因为这次启动失败而被永久跳过。
  - 关了种子（`EnableSeed=false`）的部署不走版本闸门，不会自动清理，这些授权暂时仍有效。
    超管在角色授权页逐个打开这类角色，保存一次即收回。
  - 滚动升级期间，还在跑的旧版本副本仍能给新建角色授系统菜单。
    这些授权不会被清理，升级完成后同样靠超管重新保存来收回。
  - 升级之后，超管在角色页给新建角色授用户管理、角色管理以外的系统菜单会被拒绝（`41009`）。
    后台代码在没有登录上下文时调 `IRbacService.SetRoleMenusAsync` 不受限，种子同理。
  - 下游的集成测试要留意：经 HTTP `PUT /api/v1/sys/role/menu` 给新建角色授用户管理、角色管理以外的系统菜单的用例，升级后会得到 `41009`。
    这类用例改为经服务直接授（`IRbacService.SetRoleMenusAsync`，没有登录上下文，不受限），或者把菜单授给内置角色。

- **数据库：新增一张表和一个可空列，CodeFirst 自动建、自动补。**
  - 新表 `sys_user_menu`（用户单独授权），`(UserId, MenuId)` 唯一索引。
  - `sys_module` 新增可空列 `IsDelegatable`。老库升级后所有存量模块都是 `NULL`，按「不可转授」处理。
  - `SysSchemaVersion.Current` 由 `6` 升到 `7`。
    升级后第一次启动会同步一次结构性种子，钉了 `CodeFirstVersion` 的项目也会因此重扫一次建表。
  - 跳过建表的部署（生产环境且 `SmartAdmin:Database:EnableCodeFirstInProduction=false`，或 `EnableCodeFirst=false`）要先让表和列就位，否则启动失败。
    缺列检查会点名 `sys_module(IsDelegatable)`，缺表由新增的库就绪守卫 `UserMenuGrantTableGuard` 点名 `sys_user_menu`。
    Worker 进程（`AddSmartAdminWorker`）同样装了这个守卫，与 API 一样必须先让表存在。
    两条路：
    1. 本次启动临时配 `EnableCodeFirstInProduction=true`，让应用补列建表。会丢数据的变更另有 `AllowDestructiveSchemaChange` 闸门拦着，默认拒绝，不会顺手执行。
    2. 由 DBA 先补：在预发库上用新版本开闸门启动一次（SQL 日志默认关，先配 `SmartAdmin:Database:SqlLog:Enabled=true`），把日志里的建表与补列语句照抄到生产库，再启动。

- **种子。**
  - 新按钮「用户-授权菜单」`Id = 239`（挂在「用户管理」页下）随种子进老库，但不会自动授给任何角色，包括已有的「系统管理员」。
    要让普通管理员使用，超管在角色授权页给他的角色勾上。
  - 新库的种子里，「系统」模块不可转授，「业务中心」可转授。
    老库里的「业务中心」保持 `NULL`（不可转授），要开由超管在模块管理页打开。
  - 模块种子改为列白名单，升级只刷编码、图标、落地路由、路由前缀。
    行为变化：此前升级会把内置模块整行刷回种子值，现在超管改过的标题、排序、启用、备注不再被刷回。

- **接口与权限码。**
  - 新增四个端点：`GET /api/v1/sys/user/{id}/menus`、`GET /api/v1/sys/user/{id}/menus/effective`、`PUT /api/v1/sys/user/menu`、`GET /api/v1/sys/user/menu-grants/page`。
    它们的权限码都挂在按钮 239 上；按钮 239 的权限串共五条，除这四条外还有弹窗要读的 `GET:/api/v1/sys/menu/tree`。
    前两个读端点按数据范围收口，目标用户在范围外返回 `41005`。
    `menus/effective` 的出参带 `delegatedMaxDays` 与 `delegatedMaxDate`。
    后者是服务器本地日期加最长天数，授权弹窗的日期选择器上限和保存前校验都按它算，浏览器与服务器不在同一时区也不会差一天；超管或配置为 `0` 时两者为空。
  - `PUT /api/v1/sys/user/menu` 进内核高敏权限码集合，端点带 `[RequireReauth]`：开了 TOTP 时保存授权要再认证一次。
    操作日志里记为「用户授权菜单」，请求体就是这次的增改删明细。
  - `POST /sys/module/add`、`PUT /sys/module/{id}` 的入参与 `GET /sys/module/list|{id}` 的出参多一个 `isDelegatable`。
    更新时不带这个字段表示保持原值，老前端与消费方自建的模块页不会把它清掉；新增时不带则存 `NULL`。
  - 角色出参（`GET /sys/role/page|{id}`）多一个只读字段 `isBuiltin`。
  - 新错误码：`41006` `MenuNotGrantable`、`41007` `TargetIsDelegatedAdmin`、`41008` `DelegatedGrantExpiryInvalid`、`41009` `SystemMenuNotAssignable`、`42031` `UserMenuGrantInvalid`。
    `42031` 的触发条件：同一菜单出现多次（新增、修改与移除合起来算）、效果取值不是允许或拒绝、到期时间不晚于当前时间（允许与拒绝都一样）、备注超过 200 字。
    新增或修改的菜单不存在，用已有的 `MenuNotFound`（`42015`）；目标用户不存在是 `42001`，对自己授权是 `42029`，对超管授权是 `42007`。
  - `41005` `UserOutOfDataScope` 的语义放宽为泛指「授权」：授予角色和单独授权都用它。码与 msgKey 不变。
  - 单独授权一览的分页参数规整与普通分页一致：`Size` 超过 `MaxPageSize` 抛 `48001`，`Current` 小于 1 按 1。
    查询落空时同样先过这套校验，超限的请求不会因为没有数据而成功。

- **后端 API 表面。**
  - 新接口 `IUserMenuGrantService`、`IUserMenuGrantPolicy`，用 `TryAdd` 注册，可前置替换。
    `IUserMenuGrantPolicy` 除 `DelegatedMaxDays` 外还有 `DateOnly? DelegatedMaxDate`：服务器本地日期加最长天数，保存校验与界面展示共用这一天，自己实现该接口的项目要一并提供。
    默认实现 `UserMenuGrantService`、`UserMenuGrantPolicy` 是 public 类，长流程拆成 `protected virtual` 步骤（校验、`PlanChangesAsync`、`PersistAsync`、`AfterCommitAsync` 等），可单独覆写。
  - 新事件 `UserMenuGrantsChangedEvent`：保存提交、缓存失效之后经 `IEventBus` 发布，带目标用户、操作人和新增、修改、移除的明细，内核自己不订阅。
  - 新增 public 类型：实体 `SysUserMenu`，枚举 `UserMenuEffect` 与 `UserMenuGrantStatus`，规则 `UserMenuGrantRules`，查询 `UserMenuGrantQueries`，菜单树运算 `MenuTree`（`RootModuleId`、`IsKernelSystemMenu`、`HasAncestorIn`、`WithDescendants`），安全选项启动校验 `AdminSecurityOptionsValidation`。
  - `SmartAdmin.SqlSugar` 的 `PagedListExtensions` 新增 `EmptyPage<T>`，查询在到达数据库之前就能确定没有结果时用它短路。
    页码、页大小的规整与超限校验抽成共用，`ToPagedListAsync` 的行为不变。
  - `RbacPermissionProvider`、`MenuService` 主构造器末尾追加可选参数 `TimeProvider? time = null`。
    继承它们的子类源码不用改，但已编译的子类程序集要随包升级重新编译，升级 NuGet 本来就会重编。
    两个类新增 `protected virtual` 步骤 `ApplyUserGrantsAsync` 与 `GetNextGrantExpiryAsync`，`MenuService` 另有 `ResolveGrantedMenuIdsAsync`，都可单独覆写。
  - `SysRole` 新增只读计算属性 `IsBuiltin`（不建列）。
    `RbacService` 主构造器不变，新增 `protected virtual` 步骤 `EnsureRoleMenusAssignableAsync`。
  - 库就绪钩子 `SystemMenuRoleGrantCleanup`（升级清理）与 `UserMenuGrantTableGuard`（缺表守卫）用 `TryAddEnumerable` 登记，升级清理登记在前、先执行。
    升级清理的查找、记日志、删除、失效缓存各是一个 `protected virtual` 步骤。
  - `IRbacService`、`IPermissionProvider`、`IMenuService` 的成员没有变，`InvalidatePermissionsByMenuAsync` 的默认实现扩大了失效范围，见下面 Changed 一节。
  - 整体替换了 `IPermissionProvider` 或 `IMenuService` 的项目，自己的实现不认 `sys_user_menu`，单独授权不会生效。
    要支持，就自己读这张表，再调 `UserMenuGrantRules.Compute` 算有效菜单。

- **配置。**
  `SmartAdmin:Security:DelegatedGrantMaxDays`（默认 `90`）：普通管理员授出的「允许」，到期日最多晚于今天几天，按日期判。
  取值 0 到 3650，超出范围启动即拒（API 与 Worker 都校验，抛 `InvalidOperationException`，信息里写明配置名、范围和实际值）。
  `0` 表示不限，既不要求到期时间，也没有上限。「拒绝」和超管不受这项约束。

- **前端（`smart-admin-web`）。**
  - 用户页行「更多」新增「授权菜单」（需要 `PUT:/api/v1/sys/user/menu`，超管那一行和自己那一行不出入口），工具栏「更多」新增「单独授权一览」（需要 `GET:/api/v1/sys/user/menu-grants/page`）。
  - 模块管理页多一列和一个「可转授」开关。
  - 角色页「授权菜单」：非内置角色只显示系统自带的「用户管理」「角色管理」（连同「组织管理」目录），其余系统菜单不再显示。「系统」应用下既没有这两项也没有你自建的目录时，应用下拉里也去掉「系统」。
    打开弹窗时只带入看得见的授权：角色身上残留的其余系统菜单授权不会被原样回传（否则保存会得到 `41009`），保存时它们就被收回。
  - 用户页「授权菜单」的「单独授权」列表同样收窄：系统应用里只列用户管理、角色管理，其余系统菜单不列出（目标用户从来不是超管，这些菜单只属于超管与内置角色）。
    「有效权限」页签仍是完整的只读视图。这一条只收窄界面，接口对超管单独授权系统菜单没有新增限制。
  - 包内部新增 `pruneSuperAdminOnly` 等辅助函数，不是公共 API（`index.ts` 没有导出），应用代码不受影响。
  - 类型：`ModuleRow` / `ModuleInput` 多可选字段 `isDelegatable`，`SysRole` 多可选字段 `isBuiltin`。
    新增枚举 `UserMenuEffect`、`UserMenuGrantStatus`，以及 `UserMenuGrantItem`、`UserMenuGrantUpsert`、`UserMenuLeakedCode`、`UserMenuEffectiveNode`、`UserMenuModuleItem`、`UserMenuEffective`、`UserMenuGrantPageItem`。
  - `userApi` 新增 `getMenuGrants`、`getEffectiveMenus`、`setMenuGrants`、`menuGrantPage` 四个方法。
  - 语言包新增键：`userGrant.*`、`module.delegatable*`、`role.systemMenusBuiltinOnly`、`menuTitle.button.userGrantMenus`，以及 `error.*` 下五条新错误码的文案。覆盖过内核语言包的应用不用改。
  - 没有删除或改名任何导出。

- **回滚。**
  - 新表与可空列都是加法，老版本代码不读它们。
  - 但老版本启动时若跑 CodeFirst（非生产环境，或生产开了 `EnableCodeFirstInProduction`），在 MySQL、SqlServer、PostgreSQL 上，破坏性变更闸门会因为 `sys_module.IsDelegatable`「实体没声明」而拒绝启动。
    回滚前由 DBA 删掉这一列，或回滚那一次配置 `AllowDestructiveSchemaChange=true`（会删掉该列）。
    SQLite 的 CodeFirst 只加列，只打一条 Warning。
  - 老版本启动会把种子版本写回 `6`，并按它当时的规则把内置模块整行刷回种子值。
    之后再升级，升级清理会再执行一次，只删回滚期间新授出的那部分。

- **已知限制。**
  - 授权改动在接口层立即生效，但目标用户已经打开的页面不会实时刷新侧栏与按钮，要刷新页面或重新登录。角色变更一直是这样，内核没有推送。
  - 单独授权一览会列出已软删用户名下的记录：软删保留授权，恢复后照旧生效。对这类行点「去调整」，读取目标用户时会得到 `42001`（目标用户不存在）。
  - 之后若有人把一个页面挪到内核目录下，或把内核目录改挂到别的应用后又改回「系统」（升级时种子也会把它刷回），期间授给新建角色的这些菜单不会被自动收回，要超管打开那个角色的授权弹窗保存一次。

### Added

- **用户单独授权：在角色授权之外，给单个用户的某个菜单节点授「允许」或「拒绝」。**
  角色管常规权限，单独授权只放例外。
  用户页行「更多」里的「授权菜单」打开弹窗，每个节点可设「跟随角色 / 允许 / 拒绝」，选填到期日和授权理由（最长 200 字）。
  保存按变更集走，没动的记录原样保留，授权人和授权时间不会被后一次保存重置。
  没有任何实际变化的保存（变更集为空，或内容与库里一致、要移除的记录本来就没有）不写库、不失效缓存、不发事件。
  有效菜单 =（启用角色授予 ∪ 生效中的允许）− 生效中的拒绝及其子孙，再只留启用节点；「生效中」指没有到期时间，或到期时间还没到。
  规则只在 `UserMenuGrantRules` 里实现一次，权限聚合、门户菜单和授权弹窗都调它。
  拒绝优先，并连子孙一起收回：拒掉一个页面，它下面的按钮跟着失效。
  拒绝落在节点上，不落在权限码上。几颗按钮共用同一个接口时，只要还有别的有效节点携带这条码，接口照样放行，弹窗会把这种漏网的接口和携带它的节点列出来。
  弹窗的「有效权限」页签逐个节点列出最终是否有效，以及来源：哪些角色授予、有没有单独授权、是否已过期、是否被上级拒绝。
  单独授权只授功能，不授数据，数据范围仍只来自角色；没有角色的用户按「仅本人」。
  超管照旧全量放行，不参与计算，也不能给超管建授权；对自己授权同样不行。
  到期不需要后台任务：缓存的过期时间被最近一条到期时刻封顶，到点后的下一次请求自然重算。

- **单独授权一览。**
  用户页工具栏「更多」里的「单独授权一览」，把全系统的授权例外放在一张表里复核。
  可按用户、授权人（账号或姓名）、效果、状态筛选；状态分「生效中」「7 天内到期」「已过期」，筛「生效中」时含 7 天内到期的。行尾「去调整」直接打开该用户的授权弹窗。
  非超管只看得到数据范围内用户的记录；菜单已被删除的记录不显示。

- **普通管理员可以委派单独授权。**
  给某个角色勾上按钮「用户-授权菜单」，有效权限码里含 `PUT:/api/v1/sys/user/menu` 的非超管用户就是普通管理员，可以做单独授权，受四条约束：
  - 目标用户必须在他的数据范围内。
  - 目标用户不能也是普通管理员。管理员之间的权限只由超管调整，两个管理员没法互相授权；目标被分进管理员角色后，原授权人再改他的记录也会被拒（`41007`）。
  - 变更里每一条的菜单所属模块都必须「可转授」。新增、修改、移除，「允许」与「拒绝」，一视同仁；他不必自己持有这些菜单。
  - 「允许」必须带到期日，到期日不能晚于今天加 `DelegatedGrantMaxDays`。「拒绝」只会收紧权限，不要求限时。

  可转授模块的内部不再逐条限制授权人：普通管理员可以把超管设的「拒绝」改成限期内的「允许」，也可以删掉超管设的永久「允许」。
  超管要锁定或收回某条授权，先关掉模块的「可转授」，再自己处理那条记录。关闭「可转授」不是撤销，已有授权照旧生效，只是普通管理员不能再改。

- **模块「可转授」开关。**
  模块管理页新增「可转授」列和开关，决定普通管理员能不能单独授权本模块里的菜单。
  只有显式 `true` 算可转授，`NULL` 与 `false` 都不算。内置「系统」模块固定不可转授，开关置灰，接口传入的值也被忽略。
  它和角色上的「可转授」（`SysRole.IsDelegatable`，决定普通管理员能不能把这个角色分给别人）是两回事。
  菜单属于哪个模块，沿 `ParentId` 上溯到根目录，取根目录的 `ModuleId`；不挂在模块下的菜单按不可转授处理。
  页面上新增模块时开关默认打开，接口不带 `isDelegatable` 则存 `NULL`。

- **除用户管理、角色管理外，系统菜单只授给内置角色。**
  系统菜单指内置「系统」应用下内核种子目录（Id ≤ 999）连同整棵子树；你自建在「系统」应用下的目录（Id ≥ 1000）不算。
  「用户管理」「角色管理」两个页面（整棵子树）和承载它们的「组织管理」目录放开，新建角色可以授；判定写在 `MenuTree.IsSuperAdminOnlyMenu` 一处，页面号是 `DefaultMenuSeed.USER_PAGE_ID`（230）、`ROLE_PAGE_ID`（240）、`ORG_CATALOG_ID`（200）。
  角色页「授权菜单」弹窗对非内置角色不再显示其余系统自带的菜单，并加一行说明；接口同样拒绝（`41009`）。
  判断菜单是否属于系统菜单读的是全表菜单（含停用与软删的节点）：中间隔着一个停用的目录不会断链，软删的菜单在回收站里恢复后会带着授权回来，同样算系统菜单。
  已有库里的存量授权怎么清，见上面的升级说明。

### Changed

- **弹窗遮罩与底色改成 macOS 的做法。**
  亮色下的遮罩原先是画布色的浅蒙层，压不暗白底页面，白色弹窗和背后的页面糊在一起、边界看不清。
  现在亮色遮罩是冷调深蓝黑（34%），暗色保持原来的深蓝黑；弹窗卡的底色也从半透明的弹层色改成近乎不透明的实底，表单里的字和输入框不再被背后的页面内容染灰。
  抽屉共用同一个 `--mask`，一并变暗。

- **暗色下弹窗后面的阴影收紧。**
  原先的 `--shadow-3` 是 `0 40px 100px -20px rgba(0,0,0,.9)`，叠上 58% 的遮罩，卡片四周是一大圈发脏的黑晕；
  现在是 `0 16px 40px -12px rgba(0,0,0,.55)` 加一圈细描边，抽屉与全局搜索面板共用，一并收紧。

- **暗色下按钮的辉光收敛，边不再被吞掉。**
  辉光原先套在所有主色按钮上，连表格操作列的文字按钮、「查询」这种淡色底按钮都带一圈光，光糊进透明或半透明的底色，边就看不见了。
  现在只有实心主按钮发光（强度也降了），并叠一道淡亮内描边；「查询」改成同色细描边，不发光；文字按钮不再有任何阴影。

- **登录页的鼠标视差更明显，卡片面上多一束跟着指针走的柔光。**
  卡片最大位移从 ±6px / ±4px 提到 ±16px / ±11px，跟随时长从 1s 缩到 0.45s；色场反向位移同步加大，景深更足。
  指针停在卡片上时位移趋近于零，原来这时卡片完全不动，现在有强调色柔光落在毛玻璃面上、内容下面。
  系统要求减少动态效果时，视差和柔光都关闭。

- **弹窗表单提交失败时，卡片左右晃一下。**
  `FormContainer` 的弹窗形态下，`onConfirm` 校验不过（reject）或返回 `false` 时触发，补足输入框内联提示之外的就地反馈。
  抽屉和全屏形态不晃（贴边，位移会露出缝）。

- **模块种子升级只刷结构列。**
  升级只刷模块的编码、图标、落地路由、路由前缀，不再整行覆盖。
  超管在模块管理页改过的内置模块标题、排序、启用、备注、可转授，升级后不再被刷回种子值。

- **`IRbacService.InvalidatePermissionsByMenuAsync` 的默认实现扇出到有单独授权的用户。**
  除经角色被授予该菜单的用户外，再失效所有有单独授权记录的用户的权限码缓存。
  拒绝会扩展到子孙，挪父节点会改变扩展结果，精确圈定代价高；有单独授权的用户是少数，过量失效无害。

- **没有启用角色的用户不再一律没有权限。**
  权限聚合与门户（模块列表、菜单树）在角色授予的菜单上叠加单独授权，没有角色的用户也能靠「允许」拿到权限；此前没有角色直接返回空。

- **菜单从回收站恢复，立即生效。**
  恢复菜单后立刻失效持有它的用户的权限码缓存，并让门户菜单缓存整体重算；此前要等缓存过期才生效。
  菜单的回收站类型由通用登记改为 `MenuRecycleBinType` 类，路由段仍是 `menu`。
  彻底删除菜单、彻底删除用户之前，会物理删掉指向它们的单独授权。
  前置登记过 `menu` 回收站类型的消费方，登记顺序先到者胜，上面这几个动作对它不生效。

- **`41005` 的语义放宽为泛指「授权」。**
  目标用户超出授权人的数据范围时，授予角色和单独授权（含读他的单独授权与有效权限）都返回它。码与 msgKey 不变，界面文案本来就是「目标用户超出当前数据范围」，也不变。

- **角色「授权用户」选择器重新设计，换成 macOS 风格。**
  原来是表格加条件构造器：同一个人有复选框、行尾「添加」、「添加选中」三种加法，还带刷新、列设置、每页条数，界面很杂。
  现在中栏是搜索框加用户列表，点整行即选中或取消，行尾圆形标记表示状态，另有「全选本页」；
  搜索框停顿后自动查询，左侧放大镜可在「姓名 / 账号」间切换（默认姓名，原来只能按账号查）。
  机构换成侧栏式列表，已选一行一人，悬停才出移除钮；弹窗宽度从 1100 收到 920。
  `UserPicker` 的属性、事件和 `open(ids?)` 不变；文案键 `userPicker.add` / `added` / `addChecked` 删除，新增搜索、分页和空态的键。

- **表格线色回到 `--separator`，并定为标准。**
  10.20.1 把列竖线、外框和行线换成 `--hairline-strong`（浅色 0.26），100% 缩放下偏重。
  现在统一取 `--separator`（浅色 0.16，深色 0.22），不再用更实的线色。
  代价是系统缩放 125% / 150% 时，虚拟滚动的行带有亚像素偏移，线会被摊淡；这一点接受，不靠加深线色补。

- **角色「授权菜单」弹窗：应用切换改成下拉，按钮标签不再弹权限码气泡。**
  原来应用不多于 4 个时是分段控件，再多才退回下拉，两种形态并存；现在一律是下拉。
  按钮标签上悬浮出现的权限码（`GET:/api/v1/...`）去掉了，授权人看的是按钮名，路由码在菜单管理的按钮弹窗里查。

- **`smart-naive-table` 的 peerDependency 升到 `^3.1.1`。**
  应用要同步升级该包。3.1.1 不改公开导出，只修两处外观：
  表格内置的淡色按钮和行内文字按钮，主色 / 错误色改为在「按下 / 常态 / 悬停」三档里取第一个对卡片底色达到 4.5:1 的，都不到就取最高的；浅色主题下选色不变，深色主题下这些按钮的文字略亮。
  条件构造器的前导词「条件」和分组标题，字号改跟控件尺寸档走主题字号（内置主题 `small` 档 14px，前导词原来是写死的 12px），前导词的颜色也由三级文字色提到二级。

### Fixed

- 暗色下内容卡片的边框看着不对：顶边比两侧重，圆角处像有两道同心的线，卡片里嵌卡片时内层也多出一圈框（工作台、用量统计等页）。
  顶边是两条线叠在一起：渐变描边 `::before` 一条，卡片投影里的 `inset 0 1px 0` 内高光再一条。
  描边环原先用 `padding: 1px` 画，125% / 150% 缩放下是 1.25 / 1.5 个设备像素，被摊成两个像素宽；现在改用 `border`，浏览器会把它对齐到整数设备像素，任何缩放下都是 1 个像素的细线。
  `--glass-shadow-soft` 去掉了内高光，只留外投影。
  描边与投影只画在带边框的卡片（`.n-card--bordered`）上，页面传 `bordered: false` 就是明确不要框，
  比如用量统计页里嵌在卡片中的表格（`SmartTable` 的 `card-props`），此前这个设置被全局描边无视。
- 登录失败时表单不晃动，在关了系统动画的机器上整个失效（Windows 关闭「显示动画」、远程桌面、部分企业策略下的 Edge 最常见）。
  原因是晃动靠加 class 触发 CSS 动画，而浏览器报告「减少动态效果」时，全局规则把所有 CSS 动画压到 0.01ms，登录页自己还写了 `animation: none`。
  现在改用 Web Animations API（`utils/shake`），不受这条规则影响，连续失败也能重播；
  系统要求减少动态效果时仍会晃，只是幅度和时长收小（约 ±3.6px、0.32s）。
- 表格单元格里的文字越过列边界、盖到相邻列上：角色页的「角色编码」（`scope_org_children` 溢出）、
  操作日志的「操作名」和各页的「创建时间」「登录时间」等都有。
  根因是 SmartTable 是 `table-layout: fixed`，没写 `width` 的列各分到约 120~150px，而列上的 `minWidth` 在固定布局下不起作用。
  现在时间列统一 `width: 170`，等宽编码 / IP 列写足 `width` 并加省略号，名称、标题、账号这类自由文本列加省略号和悬浮提示。
  机构页的「序号」也一并修了：树表的展开箭头和缩进原本落在 64px 的序号列上，层级一深序号就被挤没，现在指定落在「机构名称」列。
  个人会话里「设备」列的「当前设备」标签放不下时不再溢出。
- 树表（机构管理）的名称列被拖窄后，被截断的名称末尾的「…」画到了列边界外面，盖在隔壁列上。
  树单元格是「缩进 + 展开箭头 + 省略号」，省略号的 `max-width` 只有 100%，没扣掉前面两样，内容放不下时就越过单元格右缘。
  Naive UI 2.45 起不再把树的缩进层数（`--indent-offset`）写进 DOM，它自带的补偿跟着失效，
  现在样式里按缩进层数逐级扣掉（数到 10 层），所有写了 `tree: true` 加省略号的列都受益。
- 表格里被省略号截断的单元格，悬浮提示里的文字看不清：提示会原样带上单元格里的 `.muted`，
  深灰字压在亮暗都是黑色的气泡上几乎看不见，暗色下气泡又和页面几乎同色、底下那一行的字还会透出来。
  现在提示气泡和下拉、弹层同一套表面，随主题走：亮色近白底深字，暗色深蓝底亮字，都是实底加细描边和轻阴影。

## 10.20.1 - 2026-10-07

### Changed

- **内置页的表格恢复列竖线和外框。**
  10.20.0 起表格只留横向发丝线，宽表里列与列分不清，现在用回 Naive 原生的竖线和外框。
  线色取 `--hairline-strong`：虚拟滚动的行带亚像素偏移，系统缩放 125% / 150% 时较淡的 `--separator` 会被摊薄到几乎看不见。
  撑满容器的表格（`fill-height`）末行也画底线，行少时不再看着像没有横线。
  某张表要去线，给它传 `single-line`（去竖线）或 `:bordered="false"`（去外框）即可，不再需要全局 CSS 覆盖。

- **「AI 管理」目录改名「AI 网关管理」，图标换成 `ph:sparkle`。**
  目录是网关 `IAiChatClient` 加模型、用量两页的入口，原来的机器人图标在 15px 下五官挤成一团。
  图标随内核升级同步到已有的库；标题不回写，已有库里的节点仍叫「AI 管理」，且英文界面按原文显示不再翻译，在菜单管理页把标题改成「AI 网关管理」即可，改完英文界面显示 `AI Gateway`。

### Fixed

- 顶栏消息铃铛的未读徽标整体下移，两三位数（`99+`）的顶边不再被顶栏面板切平。
- 列表勾选后工具条换成批量栏时，表格不再被往下顶 4px：库把批量栏写死 34px，而内置主题的控件高是 30px，现在批量栏跟 `--control-h` 走。

## 10.20.0 - 2026-10-07

### 升级说明

**本版含破坏性变更。** 从 10.15.x 升级的应用先读这一节。

- **`smart-admin-web` 不再导出 `SelectTable`、`useTableRowSort`、`DRAG_HANDLE_CLASS`、`TableRowSortOptions`，也不再依赖 `sortablejs`。**
  - `SelectTable`（弹窗表格选择器）：改用 `smart-naive-table` 的 `SmartSelectTable`，注意几处差异：
    - 形态：弹窗 `NModal` 变成下拉浮层（窄屏为底部抽屉）。
    - 搜索：多字段逐项查询（`searchFields`）没有对应能力，只有单个关键字，远程请求带 `keyword`，后端入参要有关键字。
    - 双模型：`value` + `selected` 变成 `v-model:value` + `@pick`（行对象走事件），多选的 `values` / `selecteds` 合并进 `v-model:value` 数组，行对象数组走 `@pick`。
    - 没有对应开关：`paginated=false`（一次拉全）与 `triggerVisible=false`（隐藏触发器）。
  - `useTableRowSort` 等行拖拽：改用 SmartTable 自带的 `row-draggable`（配 `drag-handle` 与 `@row-drag-sort`）；仍依赖全局类 `.row-sort` 的样式要迁到应用自己的代码里。
- **`smart-naive-table` 的 peerDependency 升到 `^3.1.0`。** 应用要同步升级该包，否则表格的注入键与新增属性对不上。
- **分页单页上限 `SmartAdmin:Api:MaxPageSize` 的出厂默认值由 200 调到 10000。** 显式配置过该项的部署不受影响。想收紧上限，调小 `MaxPageSize`，并用 `createSmartAdmin({ table: { pageSizes } })` 把前端档位压到不超过它，否则用户选到超限的档位会收到 `48001`。`PagedListExtensions.MAX_SIZE` 常量值随之变为 10000。
- **列表页的搜索改为内置的条件构造器。** 应用自己的列表页若仍用独立搜索卡片，照常支持，不强制迁移；要回退某一项默认外观，写法见 `smart-naive-table` 包内的 `MIGRATION.md`。
- **登录页的三套皮肤（AuroraGlass / SplitPanel / Spotlight）、外观设置里的灰色模式已移除。** 水印不再是外观设置里的个人开关，旧的本地水印偏好不再生效，改由「系统配置 → 水印设置」统一设置。
- **应用选择页卡片的类名是 `.mod-card`，授权菜单的组头勾选框是 Naive 的 `n-checkbox`。** 依赖这些选择器的 e2e 要同步。
- **前端语言包里 `role.catalog`、`role.menuCol`、`role.buttonsCol` 三个键已删除。** 覆盖过这三个键的应用可以一并去掉。
- **`TableZoomButton` 与 `useTableZoom` 标记弃用，下一个 .NET 大版本删除。** SmartTable 3.x 自带放大按钮，写 `:toolbar="{ maximize: true }"`（`TABLE_TOOLBAR` 已含）。它们没有内核页面在用，仅为已依赖它们的应用保留。

### Added

- **水印在「系统配置 → 水印设置」里设置。**
  管理员决定印哪几项（姓名、账号、机构、手机尾号、当前时间、自定义文字，最多 40 字）和版式（单行 / 多行、字号、浓淡、倾斜角度、疏密、错位排列、时间格式），右侧预览用当前登录用户的真实信息即时显示。
  配置键 `sys.watermark.*`（10 个，种子 Id 48–57，默认关闭），随匿名的站点信息接口下发 `watermark` 节点；写错或越界的值由后端收口成默认值或边界，不会拖垮站点信息。
  每个人印的是自己的信息，只在登录后的页面显示，登录页不印。

- **前端条件搜索与表格统一标准的共用工具。**
  `createFlatFilterSerializer`、`flatFilterSerializer`、`SEARCH_ACTIONS`、`flatSearchOf`、`presetEqualFilter`、`createClientFilterFetcher`、`passthroughFilterSerializer`、`deriveHeaderFilters`、`TABLE_TOOLBAR`、`TABLE_MIN_ROW_HEIGHT`、`useTableTitle`、`TableTotal` 从 `smart-admin-web` 导出，应用自己的列表页接条件搜索、对齐内核的表格样式时用。
  条件构造器产出的是 `filters`，序列化器把后端真正支持的条件（文本包含、枚举与 id 相等、日期区间）翻回各接口的扁平参数，应用的 api 层不用改。
  `createClientFilterFetcher` 给后端没有过滤能力的分页接口用：没有条件时走服务端分页，有条件时取全量（上限 10000 条）在浏览器里过滤。
  应用的后端若已实现 3.x 默认的 `filters` 协议，用 `createSmartAdmin({ table: { filterSerializer: defaultFilterSerializer } })` 换回默认序列化器（`defaultFilterSerializer` 从 `smart-naive-table` 导入）。
  `createSmartAdmin({ table: { resizable: false } })` 全局关掉列宽拖拽，单列用列上的 `resizable` 覆盖。
  `TABLE_TOOLBAR`、`useTableTitle` 等的写法见 `web/COMPONENTS.md` 的 SmartTable 一节。

- **所有表格是同一个样子。**
  卡片顶部单行三段式：左边是条件搜索，右边是业务按钮和内置图标（刷新、放大还原、列设置；密度是「系统设置」里的全局偏好，表格上不放密度按钮）。业务按钮写在 `#toolbar-right`，批量操作用 `#batch` 批量栏；用户管理和操作日志的导入 / 导出收进「更多」菜单。
  分页栏左侧显示「共 N 条」，无数据统一走表格内置的空状态。
  列宽拖拽默认开启；表头筛选漏斗只开给声明了 `search` 的列，排序只给后端真正按 `SortField` 排序的端点开。
  搜索区并入表格卡片：一行「字段 + 比较符 + 值」，点「更多条件」展开多条件面板，生效的条件以 chips 显示，容器窄于 600px 时收成「输入框 + 筛选」。比较符只开放后端真正支持的：文本「包含」，枚举与 id「等于」，日期「大于等于 / 小于等于 / 等于」。后端表达不了的组合（同一字段两个「包含」、日期区间用「或」连接、不同字段之间用「或」连接）按第一个可识别的条件查询并提示一次；通用的多条件需要后端提供 `filters` 协议，内核尚未提供。
  后端没有过滤能力的接口（在线会话、我的通知、回收站）无条件时走服务端分页，有条件时一次取全量在浏览器里过滤；模块与按钮管理是静态数据，直接前端过滤；菜单与机构是树，走「本地取数器 + `filterTree`」（静态过滤不递归子节点）；AI 用量明细保留顶部的统计筛选，明细表另加「结果」条件。
  点行内的按钮、勾选框和链接不触发 `@row-click`；表头排序箭头与筛选漏斗悬停该列才显示；当前行高亮跟随主题主色。
  内核页面的表格全部使用 SmartTable：在线会话、任务监控的「即将执行」与「集群节点」、用量统计的分组表、字典项表是静态数据模式，前端分页、前端求值的条件搜索、窄档卡片列表（`card-on-narrow`）由库内置；执行记录详情抽屉里的「各次尝试」是嵌入式子表 `AttemptTable`；导入向导的预览改错步骤同样是 SmartTable，单元格是输入框与字典下拉。
  字典管理是上下两栏：字典类型在上、字典项在下，各占一半高度。

- **登录页有单栏与双栏两种布局，并带一套克制的动效。**
  单栏是居中的一张卡；双栏左边是品牌区、右边是登录卡，只在内容区 ≥ 900px 时提供，选择记在浏览器本地（`localStorage` 的 `sa-login-layout`）。
  入场时卡片、标题、输入框、按钮、第三方登录依次浮现；色场与卡片随鼠标做反向视差；切换登录方式时旧内容淡出、新内容逐项浮现；切换布局时卡片平滑滑到新位置（View Transitions）；账号或验证码错误时表单左右抖动；登录成功后整页淡出再跳转。系统开了「减少动态效果」时这些动效都关闭。
  第三方登录入口只显示品牌标。登录页亮点的界面文案叫「亮点」，配置键 `sys.login.hero.features.*`。

- **内置页是统一的 macOS 风格。**
  菜单与页签图标全站线条款（渲染时去掉菜单里存的 `-duotone` 后缀，库里的数据不用改）；亮色向登录页调色对齐，暗色是深蓝黑墨色并带强调色辉光；图表与工作台统计卡使用强调色的邻近色相。
  正文与表格 15px，默认控件高 30px，表格行高 32px，侧栏菜单行高 34px；表格只有横向发丝线；弹窗进场从 0.96 落定、退场比进场快；按钮点击没有扩散波纹；动效不回弹、悬停不位移，圆角落在令牌尺度上；卡片间距统一为 `--gap-card`。
  侧栏收起与展开是宽度、列间距、透明度的过渡而不是卸载，收起后退出 Tab 序列；图标与文字同色，选中项加粗，没有左侧竖条；顶栏折叠按钮隐藏整个侧栏（内容区铺满，品牌与搜索回到顶栏），侧栏品牌区只显示站点名。
  页签栏没有横向滚动条，页签可收缩，关闭带收拢动画。
  明暗切换是整屏交叉淡入（View Transitions；不支持或开了「减少动态效果」时直接切换）。
  切换菜单时先取回目标页的代码再落定导航，旧页保持到新页就绪。
  内核菜单标题有英文，侧栏、授权、菜单管理页随语言切换。
  外观设置里可切「表单形态」（弹窗 / 抽屉），默认弹窗，个别页面按实例写死 `variant` 的不受影响。回收站的页签是分段控件。

- **外观设置的强调色是一排圆点。**
  选中的带双层环和对勾，下面写出色名与色值，再放一条用真控件拼的效果条（主按钮、开关、链接、进度）；最后一颗彩虹圆点可以取任意颜色，自定义色值保存在浏览器里。

- **角色的「授权菜单」是居中的定高弹窗。**
  每个目录一张卡片，页面下的按钮是可勾选的胶囊，悬停显示权限码；内容区吃满、只有列表滚动，底栏（已授权统计 + 取消 / 保存）常驻。授权菜单不跟随全局的弹窗 / 抽屉偏好，固定用弹窗。应用不多于 4 个时用分段控件切换，更多则用下拉；「未分配」下没有目录时不占空分段。

- **分页单页上限默认 10000。**
  `SmartAdmin:Api:MaxPageSize` 与前端表格的最大档位对齐；超过上限直接抛 `48001`，不静默截断。前端表格初始每页 100，档位由 `smart-naive-table` 按 `fill-height` 选：没开 `100 / 500 / 1000`，开了（内核的整页列表基本都开了）`100 / 1000 / 10000`。
  前端自取全量的几处（在线会话、我的通知、回收站、操作日志的操作人下拉）遇到 `48001` 会按后端回的上限重取一次，上限调小后不会整页报错，只是覆盖的条数变少。
  单页最多一次回 10000 行，行宽较大的表（日志、文件）响应体可能达到数十 MB，介意的部署请调小 `MaxPageSize`。

- **操作日志的「操作人」条件是下拉。**
  条件构造器放不下自定义控件，选项是前 1000 个用户；用户超过 1000 时，排在后面的人在这里选不到。

- **`dev-start.bat` 在一个终端窗口里开两个页签。**
  Windows Terminal 的同一个窗口里是 `SmartAdmin-Api`（后端）与 `SmartAdmin-Web`（前端）两个页签；没有 `wt.exe` 时退回两个独立窗口。

### Fixed

- 在线会话页把同一账号的所有会话都标成「当前会话」，别的设备上的会话也被置灰、无法强制下线。现在按访问令牌里的 `sid` 只置灰当前这一条（Cookie 会话模式下访问令牌同样在内存里，一样适用）；读不到令牌的短暂时刻（比如 F5 后静默刷新还没完成）同账号一律置灰，宁可少踢也不误踢自己。
- 「我的通知」把「消息」类型显示成「通知」。三种类型各有各的文案与颜色，与系统通知管理页一致。
- 人员选择器 `UserPicker` 的账号搜索被丢弃：`account` 列声明了搜索，但 fetcher 只透传了 `name`。
- 列表页打开或点「查询」时，同一行的搜索输入框左右晃动。「查询」按钮的图标槽常驻，加载时在同一个槽里换成转圈，按钮宽度恒定。
- 字典管理的上下两栏没有各占一半，数据加载后分界线还会下移。
- 定高表格滚到底少渲染最后几行、底部留白。虚拟滚动的行高下限现在是 `TABLE_MIN_ROW_HEIGHT`，所有 `fill-height` 的表都要传 `:min-row-height`。
- 切换菜单时页面闪一下。
- 渲染异常兜底页的说明与异常信息折行。
- 亮色下提示气泡白底白字。
- 暗色切到亮色时输入框边框闪线。
