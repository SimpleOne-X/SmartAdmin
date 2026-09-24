# ADR 0011 — 第三方登录的连接与密钥在页面配置、加密入库

- 状态:已采纳(2026-09-23)
- 相关:[[ADR-0002]](外部登录骨架、未绑定默认拒绝)、[[ADR-0007]](登录页品牌 UI 与官方 provider 集合);设计稿见 `docs/superpowers/specs/2026-09-23-external-auth-db-config-design.md`;面向读者的文档见 `site/zh/backend/external-login.md`

## 背景

接入一个第三方登录身份源需要两类信息:连接信息(应用标识、OIDC 的 Authority 等)和密钥(Client Secret、CorpSecret 等)。管理员在系统配置的「登录方式」页上就要能完成接入,也要能在填完后当场确认填的是否有效,而不必改部署文件、重启进程。

约束有五条:

- 密钥不能以明文出现在数据库、接口响应、配置导出或操作日志里。
- 内核已有 `ISecretProtector`(AES-GCM),AI 厂商配置 `sys_ai_provider` 已经用它做了「密钥加密入库、响应只回尾四位」,做法可以直接复用。
- 「谁能改这里,谁就能决定谁能登录系统」,写操作的门槛要与敏感操作一致。
- OIDC 的 Authority 由用户填写并被服务端请求,是 SSRF 的入口。
- 内核的替换契约不变:`IExternalAuthProvider` 是公开扩展点,消费者自己实现的 provider 必须照常工作,可选包仍只依赖 Core 与 Microsoft.\*。

## 决策

1. **连接与密钥只有数据库一个来源。** 表 `sys_external_auth_provider`(`BaseEntity`,软删,CodeFirst 建表):非机密字段明文存 `SettingsJson`,页面回显,方便核对填的是哪个应用;机密字段整体加密存 `SecretsProtected`,另存各字段的尾四位提示(不足 8 位不给提示)。不设 appsettings 作为第二来源:两个来源并存时,页面上看到的和实际生效的可能不一致。单独建表而不放 `sys_config`,是因为 `sys_config` 的值会出现在配置列表、「高级」页签、导出和操作日志里。
2. **类型描述模型。** `IExternalAuthProviderType` 描述一种厂商类型:字段清单(名字、是否机密、是否必填、默认值)、如何造出 provider 实例、如何测试连接。可选包只注册类型描述,`AddSmartAdminXxxAuth()` 无参,不读配置。
3. **注册表。** `IExternalAuthProviderRegistry` 是登录、回调、绑定取 provider 的唯一入口,结果是消费者代码注册的 `IExternalAuthProvider` 与库里按类型现建的实例的并集。同一 Code 冲突时消费者代码注册的优先,与仓库一贯的「消费者胜」一致。库里的行整表读穿透缓存,写路径失效;实例按行的 `UpdateTime` 缓存,行变了就重建。配置不完整(缺必填项或缺密钥)的行不出实例。
4. **Code 规则。** `wecom`、`dingtalk`、`github`、`wechat` 四种官方类型的 Code 固定为类型名,只能配一份;`oidc` 可配多条,Code 由管理员起,格式 `^[a-z][a-z0-9-]{1,31}$` 且不与上述保留码相同。Code 与类型保存后不可改。唯一索引含软删行,软删后同 Code 重新添加不会撞库。
5. **不做 Gitee、QQ。** 没有后端包,列表里不出现这两项。
6. **测试连接按能验证到的程度逐项报告。** 结果是一组 `ok` / `fail` / `skipped` 检查项,验证不了的明说,不假装通过。OIDC 只验证发现文档、`issuer` 与端点,Client Secret 标为「未验证」,因为标准协议没有不带用户就能验证密钥的通用办法。厂商接口的验证方式逐个核对官方文档,没有可用方式的降级为 `skipped`。测试请求走服务端,不带用户的登录状态,不写库,只受全局限流约束。
7. **写操作要求近期重新验证身份。** 保存与清除配置带 `[RequireReauth]`。测试连接不落库,不要求。
8. **修改「决定请求发往哪里」的字段时,机密必须重新输入。** 目前是 OIDC 的 Authority。否则有权限的人可以把 Authority 指向自己控制的地址,服务端登录时就会带着已保存的密钥去那里换令牌。
9. **SSRF 围栏。** OIDC 的 Authority 在保存与测试前过 `HttpFence.ValidateUrl`,实际发出的 HTTP 请求走带解析后复检的 handler。默认围栏只拦回环与链路本地地址,内网部署的 Keycloak 不受影响。
10. **数据保护主密钥必须持久。** 主密钥是进程内临时密钥(`IDataProtectionKeyProvider.IsEphemeral`)时拒绝保存,因为重启后已存的密钥解不开。响应里只有 `hasValue` 与尾四位,操作日志不记机密字段。
11. **`CallbackBaseUrl` 与 `FrontendResultPath` 在 appsettings。** 它们不是机密,启动时的校验有价值。页面上把回调地址算出来只读显示,生产环境未配 `CallbackBaseUrl` 时页面顶部给出警告。
12. **运营键与绑定表独立。** `sys.externalauth.{code}.*`(启用、开户、按账号关联、默认角色 / 机构)继续在 `sys_config`,`sys_user_external` 绑定表不变。清除配置只软删 provider 行,不删绑定。

## 被否方案

- **appsettings 与数据库并存(库优先、配置兜底)。** 排障时要先问一条配置到底从哪来,明文密钥还会留在配置文件与环境变量里。
- **启动时把 appsettings 的连接导入数据库。** 同样把明文密钥留在配置文件里,并把「配置节是否还在」变成一个隐性状态。
- **把连接存进 `sys_config`。** 密钥会随配置列表、导出和操作日志露出,见决策 1。
- **只做前端页面、后端不存储。** 页面要有存储、加密、回显、连接测试和权限,这些都在后端。

## 后果

- **主密钥仍在库外。** `SmartAdmin:Security:DataProtection:Key` 在 appsettings 或环境变量里,生产必须配。这套方案保护的是「只拿到数据库或备份」的情形,不是「拿到了服务器」。
- **多实例必须使用同一个主密钥,并且共享缓存(Redis)。** 否则一个实例改了配置,别的实例看不到,或者解不开别的实例存入的密钥。
- **appsettings 里 `SmartAdmin:ExternalAuth` 下的 provider 连接节点不会被读取,启动时给出警告。** 内置 OIDC 由 `AddSmartAdmin` 固定注册类型描述,可选包只有无参的 `AddSmartAdminXxxAuth()`;传入 `XxxAuthOptions` 的重载在代码里显式注册一个 provider,不看数据库,与库里同 Code 的配置冲突时以它为准。
- **能改这里就能决定谁能登录。** 与「按账号自动关联」同时开启时,有权限的人加一个自己控制的 IdP,就能登成同名的本地账号。这项权限要慎给,文档写明。
- **测试连接是出站请求触发器。** 只给有权限的人,只受全局限流约束。
- **未配置的 provider 不出现在登录页。** 按钮的显示条件是「已配置且启用」。

## 非目标

Gitee、QQ 后端包;把 `sys.externalauth.{code}.*` 运营键移进 provider 表;改变登录、回调、票据换令牌的流程;从 appsettings 导入连接。
