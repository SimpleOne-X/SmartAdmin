# 更新日志

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

版本号规则：**主版本号 = 内核所用的 .NET 主版本**（10.x 对应 .NET 10，下一次大版本跟随下一个 .NET LTS），次版本号加功能，修订号修 bug；全部 NuGet 包、前端包 `smart-admin-web` 与 `SmartAdmin.Templates` 共用一个版本号。破坏性变更尽量攒到换 .NET 大版本时一起发；周期内确需破坏的在次版本发，并在该版本段落顶部加粗提示，升级前先读 *Changed*。

发布节奏：**开发在 `dev` 上进行，发布在 `main` 上完成**。先把 `dev` 合进 `main`，再**在 `main` 上**打 `v*` tag。tag 一推，`release` workflow 校验 tag 落在 `main` 上，跑构建、测试和模板冒烟（`dotnet new smart-app` 必须能还原并编译通过），全绿才打包，经 Trusted Publishing 推 nuget.org、发 npm，同时建 GitHub Release。

逐步的发版操作清单（改版本号、验证、合 `main`、打 tag）见 [`docs/releasing.md`](https://github.com/SmartCode-X/SmartAdmin/blob/main/docs/releasing.md)。

> 发版时**前后端版本号必须一起改**：后端版本由 tag 经 `-p:Version` 注入；前端版本写在 `web/package.json`、`web/packages/admin/package.json`（发到 npm 的包）与 `web/template/package.json`（**显示在模板登录页页脚**），模板对 `smart-admin-web` 的依赖钉同一个号，文档站导航的两处版本徽章也跟着改。漏改一处，`release` 的 verify 就会拦下，否则 npm 上的包、界面上的版本会和实际安装的 NuGet 包对不上。

> **发了什么，以本文件为准**，而不是仓库的发行版页面：那里只是可选的镜像，内容从本文件对应版本的段落复制过去。

## Unreleased

### Added

- **内置 Scalar API 文档 UI。** 装了包就有：开发环境 `/scalar` 零配置可用，渲染的正是前端 `npm run gen:api` 取数的那份 `/openapi/v1.json`。生产环境两个端点默认都不挂载，经 `SmartAdmin:Scalar:EnabledInProduction` 显式开启后，壳页面 `/scalar` 仍匿名（它不含契约数据），契约 JSON 收紧到权限码 `GET:/openapi/{documentname}.json`，在角色管理里把内置菜单「系统运维 → 接口文档」下的「查看契约」按钮授给谁、谁才取得到。后台同步新增「接口文档」入口页：打开文档并复制当前登录态的接口令牌，粘进 Scalar 自带的 Authentication 面板即可调试。`Scalar.AspNetCore` 是核心包「只依赖 SqlSugarCore + Microsoft.\*」这条红线上唯一的具名例外（零传递依赖、只做 UI 渲染），取舍见 [ADR 0010](https://github.com/SmartCode-X/SmartAdmin/blob/main/docs/adr/0010-scalar-api-docs-in-core.md)，用法见[文档](https://smartcode-x.github.io/SmartAdmin/zh/backend/api-docs)。

## 10.13.1 - 2026-09-17

### Added

- **业务工作台重设计：在线状态、待办卡片、可自定义快捷方式。** 后端新增待办扩展点 `IWorkbenchTodoProvider`(内置空实现，消费者可覆盖接入自己的待办来源)、`IUserShortcutService` 快捷方式服务(按用户持久化常用功能入口)与「上次登录时间 / IP」查询；前端首页工作台据此重做，展示在线状态、待办列表与可增删的快捷方式面板。
- **用户管理左侧机构树接入侧栏筛选面板。** 复用既有侧栏筛选约定：加搜索过滤、展开/收起全部，选中项自动展开祖先链；机构本身的增删改仍在机构管理页，这里保持只读筛选语义。

### Fixed

- **SQL 执行失败/超时时也能按阈值记一条慢 SQL 日志。** SqlSugarCore 的慢 SQL 统计只在语句成功执行时触发，越容易超时的语句反而越拿不到诊断记录；失败分支现在复用同一套耗时/阈值判断，超阈值的失败语句会带上「慢 SQL(执行失败)」标记、耗时与阈值，不再只是一条没有上下文的执行失败日志。（[#14](https://github.com/SmartCode-X/SmartAdmin/issues/14)）
- **用户管理机构树侧栏消除进页面的布局跳动。** 展开/收起按钮所在的头部行改为常驻，机构数据到位前给树一个占位，避免搜索框与树顶跟着数据到达时机跳动；展开按钮尺寸与表格工具栏按钮对齐。

## 10.12.1 - 2026-09-14

### Fixed

- **AI 网关：厂商不支持 OpenAI `json_schema` 严格结构化输出时不再静默失效。** `OpenAiCompatibleAdapter` 此前对所有 openai 协议厂商一律透传 `response_format.type: "json_schema"`;智谱 GLM 等厂商官方协议只支持 `text` / `json_object`,收到该字段既不报错也不降级,只会忽略约束按自由文本作答(常见还会用 Markdown 代码块包裹结果),`ResponseSchema` 契约悄悄失效。厂商预设新增 `SupportsJsonSchema` 能力位(智谱标记为 `false`),网关在发起上游调用前按此直接拒绝(`49033`),不把一个对方读不懂的字段透传过去。（[#8](https://github.com/SmartCode-X/SmartAdmin/issues/8)）

## 10.12.0 - 2026-09-14

### Added

- **AI 网关：`IAiChatClient` 消息支持多模态内容（图片），对话请求支持结构化输出约束（JSON Schema）。** `AiChatMessage.User(parts)` 接收文本与图片混排的内容（`AiChatContentPart.Text` / `.Image`，图片来源支持 base64 或 URL），两个协议适配器分别按 OpenAI 兼容协议的 `image_url` 与 Anthropic 协议的 `image` 内容块序列化，DeepSeek、通义千问、智谱、Kimi、豆包等中国厂商与 OpenAI/DeepSeek 共用同一套 `OpenAiCompatibleAdapter`，无需单独适配。`AiChatRequest.ResponseSchema` 约束模型输出必须匹配给定 JSON Schema，OpenAI 兼容协议映射到 `response_format.json_schema`，Anthropic 映射到 `output_config.format`；`System` 角色消息不支持多段内容，传了直接抛 `49032`；厂商或模型不支持图片或结构化输出时，上游报错按现有 `49020` 系列错误码映射，网关不做静默降级。两条都是追加的可选字段，不传就是原有的纯文本行为，不影响现有调用方。（[#7](https://github.com/SmartCode-X/SmartAdmin/issues/7)）

## 10.11.0 - 2026-09-14

### Added

- **AI 管理：统一大模型接入网关。** 新增 `IAiChatClient` 统一入口（`ChatAsync` / `StreamAsync`），按协议适配 OpenAI 兼容与 Anthropic 两套协议，覆盖 OpenAI、Azure OpenAI、Anthropic、DeepSeek、通义千问、智谱 GLM、Kimi、豆包、Gemini、Ollama 十个预置厂商及自定义厂商；不引入任何厂商 SDK，HttpClient 直连并套 SSRF 围栏（与定时任务共用同一套围栏实现）。后台新增「AI 管理」目录（AI 模型、用量统计两页），运维选厂商预设、填 Key 即可，Key 经 `ISecretProtector` 加密落库，接口只回脱敏尾四位；每次调用按厂商 / 模型 / 场景 / 用户记 Token 用量，用量页支持多维度聚合、占比与按天趋势。两个控制器挂 `[Module("Ai")]`，可用 `Api:DisabledModules` 整体下线（`IAiChatClient` 不受影响）；内置 `AiUsageLogCleanupJob` 按保留天数（默认 90 天）定期清理用量记录。用法与消费者调用示例见[文档](https://smartcode-x.github.io/SmartAdmin/zh/guide/ai-models)。

### Fixed

- **定时任务执行记录不再在 8192 字符处静默截断、丢掉最有价值的结尾内容。** 上限提到可配置项 `SmartAdmin:Jobs:MaxMessageChars`（单位改为明确的字符数，默认约 26 万，`≤0` 不限），异常信息走同一套规则；超限不再从中间硬切、后续输出直接丢弃，改成保留开头与结尾、只截中间，断点处留一句标注原长度的标记。多步任务里排在前面的大量重复明细不会再把后面的结论性汇总行整段挤没。（[#5](https://github.com/SmartCode-X/SmartAdmin/issues/5)、[#6](https://github.com/SmartCode-X/SmartAdmin/issues/6)）

## 10.10.1 - 2026-09-13

### Added

- **应用可以生成自己的 `ph` 离线图标子集。** `smart-admin-web` 带上命令 `smart-admin-icons`：扫描应用 `src` 里的 `ph:*` 名字，从 Phosphor 整集裁出这些图标写成 JSON；`--check` 只比对不写盘，产物过期或有拼错的名字时非 0 退出，给 CI 用。`createSmartAdmin` 新增 `iconSets` 选项，启动时把这份子集和内核子集一起同步注册，业务页的 `ph` 图标首帧就能离线渲染，不再懒加载整套 `ph`（约 946 KB gz）。模板已经接好：`npm run gen:icons` 写出 `src/assets/icons/ph-subset.json`，`main.ts` 经 `iconSets` 传入。已有应用照这两处补上即可。应用的子集不剔除内核子集已有的名字，内核升级不会让应用提交的子集过期。（[#3](https://github.com/SmartCode-X/SmartAdmin/issues/3)）

### Fixed

- **模板装依赖不再提示 esbuild 的安装脚本待批准。** `web/template/package.json` 加上 `"allowScripts": { "esbuild": true }`。degit 出去的模板没有 lockfile，esbuild 的补丁版本会浮动，所以按包名批准、不钉版本；npm 11 对未批准的依赖安装脚本会在 `npm install` 末尾列出警告。
- **网关子路径部署下，外部登录回调与待绑定认领不再一律 40014。** 两个 binder cookie（`tn_oauth_state`、`tn_oauth_pending`）的 Path 跟随对外路径前缀：配了 `CallbackBaseUrl` 取它的路径部分（`https://gw.example.com/admin` → `/admin/api/v1/auth/external`），没配时（仅开发环境）取 `Request.PathBase`，开发环境回退拼出的回调地址也带上 PathBase。根路径部署不受影响。在网关上改写 cookie Path 的临时绕法（如 nginx `proxy_cookie_path /api/ /admin/api/;`）升级后不再匹配，自然失效，可以删掉。（[#1](https://github.com/SmartCode-X/SmartAdmin/issues/1)）
- **`smart-admin-web` 的枚举可以当值导入。** 包入口把 `types/api` 整体导出（类型与枚举一起），`DuplicateStrategy`、`DataScopeType`、`NoticeType`、`ReceiverType` 与 `Job*` 系列都能直接用成员值，比如给 `ImportWizard` 传 `:strategies="[DuplicateStrategy.Skip]"`，不必再自己镜像一份数值。（[#2](https://github.com/SmartCode-X/SmartAdmin/issues/2)）

## 10.10.0 - 2026-09-13

后端 13 个 NuGet 包、前端 npm 包 `smart-admin-web` 与项目模板同号发布。能力清单见 [`README.md`](https://github.com/SmartCode-X/SmartAdmin/blob/main/README.md) 的「内置功能」，接入与扩展见[文档站](https://smartcode-x.github.io/SmartAdmin/zh/)。

### Added

- **后端内核。** 元包 `SmartAdmin` 引入四个分层包：`SmartAdmin.Core`（契约）、`SmartAdmin.SqlSugar`（数据层）、`SmartAdmin.Services`（领域服务）、`SmartAdmin.AspNetCore`（宿主集成），宿主里 `AddSmartAdmin` / `MapSmartAdmin` 两行接入。覆盖认证与会话、RBAC（权限码即规范化路由）、五种数据范围、多应用门户、组织与用户、字典与配置中心、通知公告、日志、文件、定时任务，以及 SQLite / MySQL / SQL Server / PostgreSQL 四种方言与多副本部署。内置服务一律接口化、`virtual`、经 `TryAdd` 注册，消费方可以整体替换，也可以子类覆写单步。
- **可选包与工具包。** `SmartAdmin.Excel`（xlsx 导入导出）、`SmartAdmin.Caching.Redis`（Redis 缓存，多副本共享会话）、`SmartAdmin.Auth.WeCom` / `.DingTalk` / `.GitHub` / `.WeChat`（第三方登录）；测试基础设施 `SmartAdmin.Testing`；项目模板 `SmartAdmin.Templates`（`dotnet new smart-app`）。
- **前端内核 `smart-admin-web`。** 布局壳、动态菜单路由、登录鉴权、`v-auth`、全部内置页、共享组件、stores 与语言包，预编译成 ESM + `.d.ts` + 一份 `style.css`。应用从 `web/template` 起步（`npx degit SmartCode-X/SmartAdmin/web/template web`），自己的页面、文案、静态路由、图标经 `createSmartAdmin(...)` 交给内核，页面 key 与内置页相同即覆盖内置页；vue、vue-router、pinia、vue-i18n、naive-ui、@vueuse/core、@iconify/vue、`smart-naive-table`、`smart-naive-icon` 是 peerDependencies，由应用安装。
