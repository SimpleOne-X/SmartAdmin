# ADR 0010 — API 文档 UI 进核心包:依赖红线的具名例外、鉴权划在壳与数据之间

- 状态:已采纳(2026-09-17)
- 相关:[[ADR-0004]] / [[ADR-0009]](「进内核不进包」的同款推理链);[[ADR-0003]](query-string 令牌只对 Hub 路径开口);面向读者的文档见 `site/zh/backend/api-docs.md`

## 背景

内核此前只生成契约 JSON,没有渲染那一层。消费方装完包拿到的是裸的 `/openapi/v1.json`:接口长什么样、试一次请求要带什么,全靠把这坨 JSON 贴进外部工具去看。而契约本身就是内核自己产的(`AddOpenApi()`),前端 `npm run gen:api` 也正从它取数,缺的只是一个渲染器。更麻烦的是它只在 Development 挂载,线上查一次接口签名得回开发机。

零配置是这个内核对外的第一条承诺:`AddSmartAdmin` / `MapSmartAdmin` 两行,后台就齐了。API 文档 UI 算不算进这个「齐」里,是下面第一条决策要定的事。

## 决策一:`Scalar.AspNetCore` 直接进 `SmartAdmin.AspNetCore`,不做可选包

被否的方案是 `SmartAdmin.Scalar` 可选包——`docs/rebuild-design.md` §2.2 的包矩阵里原本就挂着这么一行规划(`MapSmartAdminApiDocs()`,开发期 API 调试 UI),本次一并删掉。它照的是 `SmartAdmin.Excel` / `SmartAdmin.Caching.Redis` / `SmartAdmin.Auth.*` 的先例:第三方依赖一律下沉到「装了才有」的包里,核心四包的运行时依赖红线一寸不让。

不走这条路,是因为这次的东西和那批可选包不是一类:

- **那批可选包背后是业务能力**。xlsx 导入导出、Redis 缓存、某一家第三方登录,多数消费方用不上,「不装」本身就是收益:少几张表、少一份配置、少一条升级时要照看的路径。API 文档 UI 是开发期工具,设计意图是**每个消费方默认都有**。做成可选包等于把「默认都有」改成「知道它存在、且愿意再装一个包、再写一行 `MapSmartAdminApiDocs()` 的人才有」,零配置这条承诺在这一项上直接作废。
- **代价可量,且量过了**。`Scalar.AspNetCore` 在 nuget.org 上 net8.0 / net9.0 / net10.0 三个目标框架均标「No dependencies」(逐个核对过 nuspec),MIT 许可,UI 资源内嵌在包内(只有默认字体走 CDN,可关)。它进来不拖一棵依赖树,也不参与序列化、存储、鉴权任何一条核心链路,只做端点映射与页面渲染。红线防的是依赖失控和内核行为被第三方绑架,这两样它都不沾。

所以红线的表述改成「只允许 `SqlSugarCore` + `Microsoft.*`,外加一个具名例外」。**例外是具名的、单个的**,并同步写进所有约束文档(根 `CLAUDE.md`、`backend/CLAUDE.md`、`.github/copilot-instructions.md`、`docs/coding-standards.md`、`docs/rebuild-design.md` §2.1、`site/standard/backend.md` 中英两侧、`site/backend/architecture.md`、`site/public/llms.txt`)。下一个想开例外的库不能拿这一条当先例,它得自己再过一遍上面两问:是不是每个消费方默认都该有?传递依赖是不是零?两问有一个答不上,就还是下沉到可选包。

## 决策二:鉴权边界划在「壳」与「数据」之间

生产环境要能开(`SmartAdmin:Scalar:EnabledInProduction`,默认关),开了就得有 RBAC 网关:一份完整 API 契约匿名可取,等于把侦察面拱手送出。难点在浏览器——这套 JWT 是纯 Bearer Header 认证,没有 Cookie 会话,`window.open` 出去的新标签页不带 SPA 里存着的令牌,裸导航过去就是 401。

被否的两条:

1. **直接挂 `[RolePermission]`**。它是 `IAsyncAuthorizationFilter`(`RolePermissionAttribute.cs`),只在 MVC 控制器 action 管线里生效;`MapScalarApiReference` / `MapOpenApi` 是 Minimal API 端点,走授权中间件的 Policy 机制,两条管道不通用,属性挂不上去。硬把它们并成一条(改 `RolePermissionAttribute` 去兼容 Minimal API)代价远大于另写一个 Handler,而且动的是所有控制器都在用的东西。所以另起 `ScalarAccessRequirement` + `ScalarAccessAuthorizationHandler`:判定逻辑与 `RolePermissionAttribute` 逐条对齐(已认证 → 会话仍活跃 → 超管放行 → 否则比对权限码),但两份实现各自独立。
2. **仿 SignalR 的 `?access_token=` query 兜底**。ADR 0003 定的那套目前只对 `Realtime.HubPath` 开口,照搬过来能一次性解决新标签页不带令牌的问题,代价是把长效 JWT 明文塞进 URL,留痕浏览器历史与服务端访问日志。ADR 0003 当时肯付这笔代价,是因为 WebSocket 握手在协议上就带不了请求头,别无他法;文档 UI 有别的办法,就不该再付一次,也不该让那个口子从一条路径扩到两条。

**采用的方案**是把边界划在壳和数据之间。`/scalar` 是纯静态渲染器,页面里没有任何契约数据,始终匿名(开发、生产都一样);真正敏感的 `/openapi/{documentname}.json` 在生产开启时收紧到 `ScalarAccess` 策略。管理员在后台「接口文档」页复制自己当前的令牌,粘进 Scalar 自带的 Authentication 面板,Scalar 再拿它去请求契约 JSON——完全复用现有的 Bearer Header 管线,不发明新机制,令牌也不进 URL。代价是多一步手动粘贴,只落在生产环境下少数要查接口的管理员身上。

## 约定与后果

- **权限码**:`GET:/openapi/{documentname}.json`,和别处一样是规范化路由,不设字符串常量。它由 `PermissionCode.Build` 从 Minimal API 的 `RoutePattern.RawText` 小写化得来,菜单种子里手写的那个必须与之逐字符一致。
- **测试一致性**:`PermissionCodeConsistencyTests.BuiltInEndpointCodes()` 只反射控制器上的 `[RolePermission]`,看不见走 Policy 的 Minimal API 端点,故在那里补一条硬编码白名单。以后再出这类端点,走同一处。
- **配置**:`AdminScalarOptions.EnabledInProduction` 默认 `false`;开发环境不受它影响,始终暴露——契约源本就是开发期给 `npm run gen:api` 用的。
- **前端**:内核内置一个薄入口页(菜单「接口文档」,系统运维目录下),`window.open` 打开壳页面,并提供「复制我的接口令牌」按钮。它不感知后端的环境开关:生产没开时新标签页收到 404,按钮空跑一次没有副作用,比让前端去猜后端配了什么便宜。
- **可替换性**:`ScalarAccess` 策略与它的 Handler 照内核惯例登记,守「消费方前置注册者胜」这条契约。消费方要换成自己的判定逻辑,在 `AddSmartAdmin()` 之前先注册同名策略即可,不必 fork。

## 本 ADR 不涵盖

自定义路由前缀(固定 `/scalar`)、多 OpenAPI 文档与 AsyncAPI 聚合、Scalar 的 CDN bundle 切换选项(保持默认的本地内嵌资源),都是设计阶段明确划出去的 YAGNI,见 `docs/superpowers/specs/2026-09-17-scalar-api-docs-design.md` 第 9 节。真有消费方要,那是届时的新决策,不是这一份的遗漏。
