# 第三方登录:连接与密钥改在「登录方式」页配置,加密入库

日期:2026-09-23
状态:已确认
范围:`backend/src/SmartAdmin.Core`(契约)、`SmartAdmin.Services`(实体、注册表、管理服务)、
`SmartAdmin.AspNetCore`(管理端点、内置 OIDC、`ExternalAuthController`)、四个可选包
`SmartAdmin.Auth.{WeCom,DingTalk,GitHub,WeChat}`、`web/packages/admin/src/views/system/config/**`、
`web/e2e/`、`site/`(zh + en)、`docs/adr/`。
不涉及:`sys.externalauth.{code}.*` 运营键(启用 / 自动开户 / 按账号关联 / 默认角色 / 默认机构)一个都不动;
`sys_user_external` 绑定表;登录、回调、票据换令牌的流程;Gitee、QQ 后端包。

## 1. 决定

| 问题 | 决定 |
| --- | --- |
| provider 连接配置的来源 | 只有数据库一个来源 |
| Gitee、QQ | 不做(后端没有包) |
| 测试连接 | 提供,逐项报告 |
| 密钥怎么存 | 加密入库,复用现有 `ISecretProtector`(AES-GCM),做法同 `sys_ai_provider` |
| 在哪里配 | 系统配置 → 「登录方式」页,点某个第三方弹出设置面板 |

## 2. 同步维护的文档与注释

provider 的连接与密钥只来自数据库。下列位置的表述与之保持一致:

- `AdminExternalAuthOptions` 类注释。
- ADR-0011(新增,记录本决策)。
- `site/{zh/,}backend/external-login.md` 的配置章节。
- `web/playwright.config.ts` 中 e2e 宿主的企业微信环境变量:e2e 用例通过管理接口写入配置,结束时清理。

## 3. 数据模型

新表 `sys_external_auth_provider`(继承 `BaseEntity`,带软删与审计字段,建表走 CodeFirst):

| 列 | 说明 |
| --- | --- |
| `Code` | provider 码,唯一索引(**含软删行**,避免撞库唯一约束抛 500,同 `sys_ai_provider`)。保存后不可改 |
| `Type` | `oidc` / `wecom` / `dingtalk` / `github` / `wechat`。保存后不可改 |
| `DisplayName` / `Icon` | 登录按钮文案、图标(可空,回退类型默认) |
| `SettingsJson` | 非机密字段的 JSON,如 `{"corpId":"…","agentId":"…"}`。明文,页面上回显,方便核对填的是哪个应用 |
| `SecretsProtected` | 机密字段整体一个 JSON,用 `ISecretProtector.Protect` 封成信封。`null` = 未配置 |
| `SecretHints` | 各机密字段的尾四位提示,JSON。密钥不足 8 位时不给提示,避免短密钥被整个露出 |

**为什么单独建表,不放 `sys_config`**:`sys_config` 的值会出现在配置列表、「高级」页签、导出和操作日志里,
密钥放进去等于明文露出。

**机密字段与非机密字段**:

| 类型 | 非机密(明文) | 机密(加密) |
| --- | --- | --- |
| `wecom` | CorpId、AgentId | CorpSecret |
| `dingtalk` | AppKey | AppSecret |
| `github` | ClientId | ClientSecret |
| `wechat` | AppId | AppSecret |
| `oidc` | Authority、ClientId、Scopes、UsePkce | ClientSecret |

**Code 规则**:`wecom` / `dingtalk` / `github` / `wechat` 四种类型 Code 就是类型名,只能配一份
(和 ADR-0007 一致:官方包 Code 硬固定)。`oidc` 可配多条,Code 由管理员起,
格式 `^[a-z][a-z0-9-]{1,31}$`,且不能与上面四个保留码相同。
企业微信、钉钉的 Code 同样固定为类型名。

`enabled` / `provisioning` / `linkByAccount` 等运营键**继续在 `sys_config`**,页面上它们仍走页面草稿和底部保存条。
这样 `ISysUserExternalService` 和现有登录页开关的语义完全不变。

## 4. 后端

### 4.1 契约(`SmartAdmin.Core`)

`IExternalAuthProvider` 接口**不动**,消费者自己实现的 provider 照常工作。新增:

- `IExternalAuthProviderType`:一个「类型描述」。
  `Type`、默认展示名与图标、是否允许多条、字段清单(名字、是否机密、是否必填、默认值)、
  `Create(settings, context)` 造出 provider 实例、`TestAsync(settings, context)` 做连接测试。
- `IExternalAuthProviderRegistry`:`FindAsync(code)` / `ListAsync()`。登录、回调、绑定统一从这里取 provider。

### 4.2 注册表(`SmartAdmin.Services`)

- 结果 = 消费者用 `IExternalAuthProvider` 注册的实例 ∪ 库里按类型现建的实例。
  同 Code 冲突时**消费者代码注册的优先**(与仓库一贯的「消费者胜」一致)。
- 库里的行整表读穿透缓存(`CacheKeys` 新增一个键,写路径 `RemoveAsync` 失效,多实例走 Redis 缓存时全集群生效),
  实例按「行的 `UpdateTime`」缓存,行变了就重建。企业微信 provider 里缓存的 access_token 随实例重建而丢弃,正常。
- 未配置完整(缺必填项或缺密钥)的行不出实例,`FindAsync` 返回 `null` → 现有代码里的 40013。

### 4.3 管理服务与端点

新服务 `IExternalAuthProviderService`(`virtual`、`TryAdd`),新控制器 `ExternalAuthProviderController`,
路由 `api/v1/sys/external-auth/providers`,`[Module("ExternalAuth")]`:

| 端点 | 用途 | 门槛 |
| --- | --- | --- |
| `GET` | 目录:已装的类型 + 每个 provider 的配置状态、非机密字段、机密字段的 `hasValue` 与尾四位、回调地址(另有带 `{code}` 占位的模板 `callbackUriTemplate`,新增时用)、页面级告警 `dataProtectionEphemeral` / `callbackBaseUrlMissing` | `[RolePermission]` |
| `PUT {code}` | 新增 / 更新一条(请求带 `type`)。机密字段留空 = 不改 | `[RolePermission]` `[RequireReauth]` |
| `DELETE {code}` | 清除配置(软删)。**不**删 `sys_user_external` 绑定 | `[RolePermission]` `[RequireReauth]` |
| `POST test` | 连接测试:用表单里的值,机密留空则用已保存的 | `[RolePermission]` |

`GET auth/external/providers/all` 读注册表,返回已配置完整的 provider 及其运营开关。
按钮权限并入「系统配置」菜单(Id 310)下已有的 311–314 四个按钮,按 HTTP 方法分配:311 授 `GET` 目录,312 授 `POST test`,313 授 `PUT {code}`,314 授 `DELETE {code}`。同十位段的编号已满,不再新增 Id。
`gen:api` 重新生成前端契约。

**响应里永远没有机密明文**:只回 `hasValue` 和尾四位。这条要有测试锁住(把响应整段序列化后断言不含任何密钥)。

### 4.4 保存时的规则

1. 数据保护主密钥是进程内临时密钥(`IDataProtectionKeyProvider.IsEphemeral`)→ 拒绝保存,
   提示先配 `SmartAdmin:Security:DataProtection:Key`。重启后临时密钥丢失,存进去的密钥就永远解不开了。
2. 机密字段留空 = 不改;首次保存时机密字段必填。
3. **改了「决定请求发往哪里」的字段,机密必须重新输入**。目前只有 OIDC 的 `Authority` 属于这一类:
   否则有权限的人可以把 Authority 指到自己控制的地址,登录时服务端会带着已保存的密钥去那里换令牌,等于把密钥送出去。
4. OIDC 的 `Authority` 保存与测试前过 `HttpFence.ValidateUrl`;实际发起的 HTTP 请求也要走带解析后复检的 handler(见 §8 风险)。
5. 操作日志(`[OperationLog]`)不记机密字段。实现时核对现有脱敏器是否按字段名匹配,不够就补。
6. 写操作要求近期重新验证身份(`[RequireReauth]`)。这里能决定「谁能登录系统」,
   和「敏感操作」页签、配置新增的门槛一致。

### 4.5 可选包与内置 OIDC

- 四个可选包各暴露一个无参的 `AddSmartAdminXxxAuth()`:只 `TryAddEnumerable` 一个 `IExternalAuthProviderType`,
  **不读配置、不抛异常**。包里没有带 `IConfiguration` 的重载;
  带 `Options` 参数的重载是消费者在代码里显式注册 provider 的路径,由注册表按「代码注册优先」处理。
  `AddSmartAdmin` 启动时若检测到 `SmartAdmin:ExternalAuth:{Oidc,WeCom,DingTalk,GitHub,WeChat}` 配置节,
  打一条警告:「该配置节不会被读取,请到 系统配置 → 登录方式 配置」。
- 内置 OIDC 由 `AddSmartAdmin` 固定注册 `oidc` 类型描述;`AdminExternalAuthOptions` 没有 `Oidc` 列表,
  `ExternalAuthSetup.AddExternalAuthProviders` 只做 `CallbackBaseUrl` 校验。
- `CallbackBaseUrl`、`FrontendResultPath` **仍在 appsettings**:它们不是机密,而且启动时校验的保护有价值。
  页面上把「回调地址」算出来只读显示,旁边有复制按钮。生产环境没配 `CallbackBaseUrl` 时,页面顶部给一条警告,
  因为这种情况下登录时控制器会抛异常。

### 4.6 测试连接(`TestAsync`)

结果是一组检查项,每项 `ok` / `fail` / `skipped`,界面逐项列出,不是一个笼统的「成功」。
能验证到哪一步,就写到哪一步,验证不了的明说,不假装通过:

| 类型 | 检查 | 说明 |
| --- | --- | --- |
| `wecom` | 用 CorpId + CorpSecret 换 access_token;再用 AgentId 查应用 | 凭据和 AgentId 都能真实验证 |
| `dingtalk` | 用 AppKey + AppSecret 换应用 access_token | 凭据能验证 |
| `github` | 用假的授权码请求换令牌端点:返回「码无效」= 凭据有效,返回「客户端凭据错误」= 无效 | 借错误类型区分,不发起真登录 |
| `wechat` | 用假授权码请求令牌端点,确认 AppId 被微信识别 | AppSecret 标为「未验证」(微信先校验 AppId 与授权码,不会走到密钥校验) |
| `oidc` | 取发现文档、确认 `issuer` 一致、有授权端点与令牌端点;**Secret 标为「未验证」** | 标准 OIDC 没有不带用户就能验密钥的通用办法 |

这些厂商接口的地址、参数、错误码以官方文档为准;没有可用验证方式的检查项标为 `skipped`,不硬凑。
所有测试请求走服务端、固定厂商域名(OIDC 例外,走围栏),不带用户的真实登录状态,不写库。

## 5. 前端(「登录方式」页)

列表每个第三方一行,右侧从左到右:**状态**、**设置**按钮、**开关**。状态三种:

- **未安装**:后端目录里没有这个类型(服务器没装对应的包)。行灰掉,写「需先在服务器安装 xxx 包」。
- **未配置**:已安装,库里还没有完整配置。开关灰掉。
- **已配置**:开关可用。

列表只含后端已有类型的行;Gitee、QQ 没有后端包,不出现在列表里(前端品牌图标资源保留)。
列表下方有一个「添加 OIDC」入口,可添加多条。

**设置面板**(居中的卡片式弹层,Apple 的 sheet 风格):

- 顶部品牌图标 + 名称。
- 字段:非机密的回显;机密的显示 `••••1a2b`,占位「留空则不修改」。改了 OIDC 的 Authority 时,Secret 变为必填并提示原因。
- 「回调地址」只读一行 + 复制。旁注:请把它填到厂商后台。
- 底部:左边「测试连接」,右边「取消」「保存」。测试用当前表单里的值,不要求先保存。
  结果是检查项清单(`✓ 已连通` / `✕ 凭据无效` / `– Secret 未验证`),而且不弹 toast,直接显示在面板里。
- 已配置的 provider 里有「清除配置」(红色、二次确认,说明不会删除用户已绑定的外部账号)。

**保存路径**:面板里的「保存」直接调 `PUT`,**不进页面草稿**,也不走底部保存条,
与「敏感操作」页签的增删一致。原因:机密不应该长时间挂在页面草稿里,也不该出现在「离开页面」的未保存确认里。
开关仍是草稿项,保存条逻辑不变。

**预览**:仍是登录框;按钮显示条件是「已配置且启用」。

## 6. 测试计划

**动手前先跑一遍现有基线**:`ExternalAuthTests`(26 处)、`GitHubWeChatAuthProviderTests`、`WeComDingTalkAuthProviderTests`
覆盖登录流程与四个 provider 的换身份逻辑,这些测试的通过数与断言保持不变。

新增:

- 注册表:并集与同 Code 冲突时代码注册优先;行变更后实例重建;未配置完整的行不出实例;缓存失效。
- 管理服务:加密往返;机密留空不改;首次必填;改 Authority 要重输机密;临时密钥拒绝保存;
  Code 格式与保留码;软删后同 Code 重新添加不撞唯一索引;删除不动绑定表。
- 端点:权限码与 `[RequireReauth]`;响应序列化后不含任何机密明文;操作日志不含机密。
- 各类型 `Create` / `TestAsync`:用假的 `HttpMessageHandler`,覆盖 ok、凭据错、网络错、响应畸形。
- `HttpFence`:OIDC Authority 指向命中黑名单的地址时保存与测试都被拒。
- 替换契约(`ReplaceabilityTests` / `ReplaceabilityContract.cs`)加入新服务与注册表的 `TryAdd` 预注册优先。
- 前端:vitest 覆盖面板的字段规则(留空、改 Authority)与状态三态;
  e2e:通过界面配好 GitHub 后登录页出现按钮、机密不回显、测试连接出结果、清除配置后按钮消失。
  e2e 用例通过管理接口写入第三方配置,并在结束时清理。

## 7. 实施批次

1. **契约与存储**:Core 契约、实体与 CodeFirst、注册表、管理服务、端点、错误码、菜单权限种子、缓存键;先写测试。
2. **四个可选包**:类型描述 + `TestAsync`,只保留无参与 `Options` 两种重载。
3. **内置 OIDC**:类型描述、SSRF 围栏。
4. **控制器与 `AuthService` 换源**:`ExternalAuthController` 与 `AuthService` 改从注册表取 provider。
   `AuthService` 现在有一个可选尾参 `IEnumerable<IExternalAuthProvider>?`,子类兼容要保留,
   另有一个 `IExternalAuthProviderRegistry?` 可选尾参,为空时使用该集合。
5. **前端**:`gen:api`、设置面板、列表改造、i18n、`gen:icons`、e2e。
6. **文档**:ADR-0011、`external-login.md`(zh 先,en 译)、`CHANGELOG.md`、`MinimalHost/Program.cs` 的注释。
   动 `site/` 前先读 `skills/write-docs.md`,并跑 `lint:prose`。

每批完成后 `dotnet build -warnaserror` + 对应测试;前端跑 lint / `format:check` / typecheck / vitest。

## 8. 风险

- **主密钥在库外。** `DataProtection:Key` 来自 appsettings 或环境变量,生产必须配置。
  加密保护的是「只拿到数据库或备份」的情形,不是「拿到了服务器」。
- **多实例使用同一个主密钥**,并共享缓存(Redis),否则配置变更在别的实例上不可见。
- **SSRF。** OIDC 的 `Authority` 是管理员填的地址,服务端会请求它(发现文档、JWKS、令牌端点)。
  这些请求走带解析后复检的围栏 handler。默认围栏只拦回环和链路本地地址(不拦 10.x / 192.168.x),
  内网部署的 Keycloak 不受影响。
- **「测试连接」触发出站请求**,只给有权限的人;它不落库,不要求重新验证身份,只受全局限流约束。
- **写权限等于决定谁能登录。** 文档写明这一点,并提示开着「按账号自动关联」时要防止有人添加自己控制的 IdP。
