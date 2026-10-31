# 外部登录（SSO）

第一次用企业微信扫码进来的人会被拒绝，报 `OAuthAccountNotBound`：默认策略如此，不是配错了。内核一贯的立场是账号只由管理员开，没有自注册，外部身份也照这条办。要让 SSO 自己开户，得按 provider 显式打开那个开关。

## 五个 provider，两种打包方式

| provider | 装在哪 | 对接方式 |
| --- | --- | --- |
| `oidc` | 内核内置（AspNetCore 层） | 标准 OIDC，通吃 Keycloak、Entra、Authing、Auth0 |
| `wecom` | 可选包 `SmartAdmin.Auth.WeCom` | 桌面浏览器扫码登录，企业微信客户端里走网页授权，按 User-Agent 自动选 |
| `dingtalk` | 可选包 `SmartAdmin.Auth.DingTalk` | 钉钉 PC 扫码 / 网页授权 |
| `github` | 可选包 `SmartAdmin.Auth.GitHub` | GitHub OAuth App，scope 仅 `read:user` |
| `wechat` | 可选包 `SmartAdmin.Auth.WeChat` | 微信开放平台网站应用扫码（`snsapi_login`），身份只取 `unionid` |

内置 OIDC 零新增依赖：发现文档、JWKS、`id_token` 验签全用 JwtBearer 已经传递进来的 `Microsoft.IdentityModel.*`。四个厂商包各自只引 `Core` 加 Microsoft.\*，用裸 `HttpClient` 对接厂商 API。所以它们能独立发版，也不会把厂商 SDK 拖进内核。

四个可选包在 `AddSmartAdmin()` 之前注册。它们只注册厂商的类型描述（`IExternalAuthProviderType`），不读配置，也不抛异常。内置 OIDC 的类型描述由 `AddSmartAdmin()` 注册，不用另调。

```csharp
builder.Services.AddSmartAdminWeComAuth();
builder.Services.AddSmartAdminDingTalkAuth();
builder.Services.AddSmartAdminGitHubAuth();
builder.Services.AddSmartAdminWeChatAuth();
builder.Services.AddSmartAdmin(builder.Configuration);
```

第三方登录的连接配置只有数据库这一个来源，appsettings 里 `SmartAdmin:ExternalAuth` 下的厂商与 OIDC 连接节点不会被读取，启动时给出一条警告。传入 `WeComAuthOptions` 这类选项对象的重载，是在代码里显式注册一个 provider，不看数据库，与库里同 `Code` 的配置冲突时以它为准。

装了哪个包，「登录方式」页里才有对应的一行；没装的显示「未安装」。登录、回调、绑定统一从注册表 `IExternalAuthProviderRegistry` 取 provider，它的结果是两部分的并集：库里配好的连接按类型现建的实例，加上消费者自己实现并注册的 `IExternalAuthProvider`。两边 `Code` 相同时，代码里注册的那个优先。

## 配置放在哪里

### 连接与密钥

连接与密钥在 系统配置 → 登录方式 里填：点某个第三方的「设置」，填好保存，立即生效。每一行的状态有三种：「未安装」是服务器没装对应的包，「未配置」是已安装但库里还没有完整配置，开关灰掉，「已配置」才能开关。Gitee、QQ 没有后端包，列表里不出现。连接与密钥只来自数据库，没有 appsettings 里的对应写法。`SmartAdmin:ExternalAuth` 下 `Oidc`、`WeCom`、`DingTalk`、`GitHub`、`WeChat` 这些连接节点不会被读取，启动时如果发现它们还在，会打一条警告，提示到页面里填写。

每种类型要填的字段如下，机密字段加密存放：

| 类型 | 明文字段 | 机密字段（加密） |
| --- | --- | --- |
| `wecom` | CorpId、AgentId | CorpSecret |
| `dingtalk` | AppKey | AppSecret |
| `github` | ClientId | ClientSecret |
| `wechat` | AppId | AppSecret |
| `oidc` | Authority、ClientId、Scopes、UsePkce | ClientSecret |

明文字段在页面上回显，方便核对填的是哪个应用。机密字段整体用 `ISecretProtector`（AES-GCM）加密后存进 `sys_external_auth_provider` 表，页面和接口只回「已配置」与尾四位，任何响应和操作日志里都没有机密明文。密钥不足 8 位时连尾四位也不给，免得短密钥被整个露出。机密字段留空表示不修改，首次保存时必填。

这张表和 `sys_config` 是分开的，因为 `sys_config` 的值会出现在配置列表、「高级」页、导出和操作日志里，密钥放进去就等于明文露出。

`Code` 的规则：

- 官方厂商类型的 `Code` 固定为类型名（`wecom`、`dingtalk`、`github`、`wechat`），每种只能配一份。
- `oidc` 可以配多条，页面上点「添加 OIDC」。`Code` 由管理员起，格式是 `^[a-z][a-z0-9-]{1,31}$`，不能与上面四个类型名相同。
- `Code` 和类型保存后不能改。

几条保存时的规则：

- **数据保护主密钥必须配置。** 加密用的主密钥是 `SmartAdmin:Security:DataProtection:Key`。主密钥是进程内的临时密钥时，页面拒绝保存，因为重启后临时密钥丢失，存进去的密钥就永远解不开。
- **改 OIDC 的 Authority 必须重新输入 Client Secret。** 否则有权限的人可以把 Authority 指向自己控制的地址，登录时服务端就会带着已保存的密钥去那里换令牌。Authority 保存与测试前都要过 `HttpFence` 的地址校验，默认只拦回环和链路本地地址，内网的 Keycloak 不受影响。
- **保存和清除配置要求近期重新验证过身份。** 清除只删这条配置，不会删除用户已经绑定的外部账号。

主密钥仍在库外，这套加密保护的是「只拿到数据库或备份」的情形，不是「拿到了服务器」。多实例部署时，所有实例必须用同一个主密钥，并且共享缓存（Redis）：配置行整表走读穿透缓存，缓存不共享，一个实例改了配置，别的实例就看不到。

::: warning 谁能改这里，谁就能决定谁能登录
这几个操作的按钮权限挂在「系统配置」菜单下，读取目录并入「配置-查询」，保存、清除、测试连接各是一个按钮。授给谁要慎重。

和「按账号自动关联」同时开着时，风险最大：有权限的人加一个自己控制的 OIDC IdP，就能让账号名与本地账号相同的人登进那个本地账号。开着自动关联的系统，只把这项权限授给管理 IdP 的人。
:::

### 测试连接

设置面板左下角的「测试连接」用表单里当前的值检查，不要求先保存；机密留空时用已保存的。结果是一组检查项，每项是 `ok`、`fail` 或 `skipped`，逐项列在面板里，不是一个笼统的「成功」。能验证到哪一步就报到哪一步，验证不了的明说：

| 类型 | 检查项 | 验证不了的 |
| --- | --- | --- |
| `wecom` | 用 CorpId 与 CorpSecret 换 access_token，再用 AgentId 查应用 | 无 |
| `dingtalk` | 用 AppKey 与 AppSecret 换应用 access_token | 无 |
| `github` | 用一个假授权码请求令牌端点，「码无效」说明凭据有效，「客户端凭据错误」说明无效 | 不发起真实登录 |
| `wechat` | 用一个假授权码请求令牌端点，确认 AppId 被微信识别 | AppSecret 标为「未验证」 |
| `oidc` | 取发现文档，确认 `issuer` 一致、有授权端点与令牌端点 | Client Secret 标为「未验证」 |

OIDC 的 Client Secret 验证不了，是因为标准协议没有不带用户就能验证密钥的通用办法。微信的 AppSecret 验证不了，是因为微信先校验 AppId 和授权码，授权码无效时不会走到密钥校验。某个厂商没有可用的验证方式时，对应检查项标为 `skipped`，而不是硬凑一个通过。

测试请求都由服务端发出，不带任何用户的登录状态，不写库，只受全局限流约束。除了 OIDC 走地址围栏，请求的域名固定为厂商域名。测试连接是个出站请求的触发器，所以只给有权限的人。它不落库，不要求重新验证身份。

### 回调基址与前端结果页

`CallbackBaseUrl` 和 `FrontendResultPath` 在 `appsettings`，和 Database、Jwt、Email 一个路子。它们不是机密，而且启动时的校验有价值：填错就拒绝启动，比运行时才发现好。

```jsonc
{
  "SmartAdmin": {
    "ExternalAuth": {
      "CallbackBaseUrl": "https://admin.example.com"
    }
  }
}
```

页面上的设置面板把完整的回调地址算出来只读显示，旁边有复制按钮，填到厂商后台即可。生产环境没配 `CallbackBaseUrl` 时，页面顶部给一条警告，因为这种情况下登录时控制器会抛异常。

`CallbackBaseUrl` 只填后端对外的根地址，回调路径 `/api/v1/auth/external/{provider}/callback` 由内核接在后面。开发环境不配会回退到请求主机；生产环境必须配，因为 Host 头能伪造。填错了厂商照样跳转，它们只校验域名，最后落在一条不存在的路径上，看着就是「授权完什么也没发生」。所以启动时先校验：整条回调地址、前端结果页 `FrontendResultPath`、带查询串或 `#` 片段、不是 `http(s)` 绝对地址，都会让应用拒绝启动。网关子路径可以，比如 `https://gw.example.com/admin`。这时前端的 `apiBase` 也要走同一个前缀。内核靠两个 cookie 确认回调与认领来自发起登录的那个浏览器，cookie 的 Path 跟着前缀走（`/admin/api/v1/auth/external`）。前端绕开前缀调接口，cookie 就带不上，登录回调或认领待绑定会报 40014。

`GET /api/v1/auth/external/providers` 只回非密钥字段（code、显示名、图标），够前端点亮按钮就行。登录页的按钮只对「已配置且启用」的 provider 出现，没配置的不显示。

### 运营项

运营项走 `sys_config`，配置页上运行时可改，按 provider code 组键：

| 配置键 | 默认 | 管什么 |
| --- | --- | --- |
| `sys.externalauth.{code}.enabled` | 启用 | 这个 provider 开不开 |
| `sys.externalauth.{code}.provisioning` | 拒绝 | 未绑定账号首次登录时开不开户 |
| `sys.externalauth.{code}.linkByAccount` | 关 | 未绑定的外部身份按账号名关联同名本地账号 |
| `sys.externalauth.{code}.defaultRoleIds` | 空 | 自动开户时给什么角色 |
| `sys.externalauth.{code}.defaultOrgId` | 空 | 自动开户时落哪个机构 |
| `sys.externalauth.{code}.notify` | 开 | 站内通知要不要同时推送到这个 provider，`enabled` 关着时同样不推，详见[推送消息到企业微信](#推送消息到企业微信) |

一个键都不配，默认行为就是**启用 + 拒绝开户**：连接配好，就是一套绑定优先的 SSO。`wecom` 在此之外还会推送通知。

`enabled` 是「登录方式」页每一行右侧的开关，和连接配置无关：开关走页面底部的保存条，设置面板里的保存则直接写库。企业微信的行还多两个开关：「按账号自动关联」对应它的 `linkByAccount`，「同时推送消息」对应 `notify`。其余的键不预置，别的 provider 的 `linkByAccount` 也算在内。要用时到「高级」页新增，分组填 `externalauth`。

这几个键的读取收口在 `ISysUserExternalService`，控制器和 `AuthService` 都只调它，不各自散读配置键。

## 未绑定的账号怎么办

`sys_user_external` 表按 `(Provider, Subject)` 唯一，记的是「哪个外部身份对应哪个本地用户」。首次外部登录时查不到绑定，这个 provider 开了 `linkByAccount` 就先按账号名关联，没开或没关联上，再由 `provisioning` 决定：

- **拒绝**（默认）：内部抛 `OAuthAccountNotBound`（40016），但登录回调不会把这个错误直接甩给用户——它转成一张「待认领」的一次性票据（`pendingLink`），引导用户改走账密或短信登录；登录成功后调 `POST pending-link/claim` 带上这张票据，才把外部身份绑到当前账号（认领要求与发起登录同一浏览器，防钓鱼抢绑）。反过来也行：先有本地账号，再去个人中心用 `POST {provider}/bind` 主动发起绑定。
- **自动开户**（JIT）：建一个本地账号，随机口令占位、不要求改密，角色和机构取上面那两个配置键。

### 按账号名关联

本地账号本来就是企业微信账号时，待认领是多余的一步：员工手上根本没有密码可填。把 `sys.externalauth.{code}.linkByAccount` 设成 `true` 之后，外部身份的标识（企业微信就是 `userid`）和某个本地账号完全相同，首次登录就绑上并直接进。以后只认绑定，不再按名字找。比较口径和账号密码登录查账号一样，大小写敏不敏感看数据库排序规则。企业微信的 `userid` 本身不区分大小写，两边写法最好统一。在「登录方式」页企业微信那一行打开它，会先弹确认框；别的 provider 照前面说的，到「高级」页加键。

超级管理员、已停用的账号、已经绑过这个 provider 的账号，永不自动关联；找不到同名账号也一样，都交回 `provisioning`。超级管理员要用外部登录，只能自己去个人中心绑。和自动开户同时开着时，先关联，关联不上再开户。按邮箱、手机号这类别的口径关联，覆写 `AuthService.LinkByAccountAsync` 或 `ResolveExternalUserAsync` 即可，不必改内核。

::: warning 打开前先确认两边是同一批人
打开它，「这个人是谁」就交给 IdP 的账号来认了。

- 两边账号得是同一套来源、同一批人，比如本地账号就是从企业微信通讯录同步来的。各自建号的，同名未必同人，关联上就是张冠李戴。这是最现实的风险，开之前拿两边名单核一遍。
- 谁能改 IdP 里的账号名，谁就能让一个人登成同名本地账号。企业微信里只有管理员和通讯录同步接口改得了成员账号，普通成员自己改不了。企业内部，这项权限通常就在 IT 手里。
- 企业微信里给谁改了账号，本地要跟着改账号或解绑重绑。不然改名后的新账号下次首次登录，会去匹配同名的另一个本地账号。
- 只给标识由企业统一分配、用户自己选不了的 provider 开。企业微信的 `userid` 合适：非企业成员只拿得到 openid，互联企业的成员是 `CorpId/userid` 形式，都对不上普通账号。钉钉、微信、GitHub 的标识是 unionid 或数字 id，开了也对不上。允许自助注册、`sub` 就是用户名的 OIDC IdP，不要开。
:::

## 在企业微信客户端里登录

同一个 `wecom` provider 出两种授权页，看发起授权那次请求的 User-Agent 选。带 `wxwork` 的是企业微信客户端的内置浏览器，桌面端和手机端都算。扫码页在那里用不了，于是走 OAuth 网页授权，`scope` 是 `snsapi_base`，静默授权，不弹确认页。其余情况一律是扫码登录。两种授权带回的 `code` 都拿去调 `auth/getuserinfo` 换 `userid`，后面的流程完全一样。

要让员工在企业微信里点开应用就进系统，企业微信后台配两处：

- 应用主页指向站点。
- 可信域名覆盖 `CallbackBaseUrl` 的域名，带端口的话端口也要登记，否则跳转时报 `redirect_uri` 参数错误。

前端包的内置登录页在企业微信客户端里会自动发起一次企业微信登录，每个浏览器会话只发一次。登录失败，或者主动退出后回到登录页，就停在那里让人自己选，不会来回跳。

首次登录照样要过未绑定这一关。默认拒绝，员工会落回登录页走待认领。本地账号就是企业微信账号的，到「登录方式」页，在企业微信那一行打开「按账号自动关联」。员工第一次点开就进，不用拿密码认领。本地还没有账号的，打开自动开户。

## 推送消息到企业微信

配好企业微信登录，站内通知就会同时推到企业微信，不用再配一套。
管理员发的公告、定时任务的告警，以及业务模块经 `INoticeService.PublishAsync` 发布的通知，都在其中。

### 接通步骤

1. 装好 `SmartAdmin.Auth.WeCom`，并在 `AddSmartAdmin()` 之前调用 `AddSmartAdminWeComAuth()`。
2. 在 系统配置 → 登录方式 里配好企业微信，生产环境同时配好 `CallbackBaseUrl`。
3. 在企业微信管理后台确认两处：应用的可见范围包含要接收的成员，部署服务器的出口 IP 在「企业可信 IP」里。
4. 成员已经绑定企业微信账号：通过企业微信成功登录过，或在个人中心绑定过。系统靠这条绑定才知道通知该发给哪个 `userid`。
5. 在「登录方式」页企业微信那一行点「发送测试消息」，到企业微信里确认收到了卡片。

测试消息只发给当前管理员自己绑定的企业微信账号，用的是已保存的配置，所以要先保存再点。
它直接发一次，不入队、不重试，几秒内就有结果。
没绑定、没配 `CallbackBaseUrl`、被企业微信拒收和发送失败，都在页面上给出提示，失败的还带着企业微信的错误码和说明。
「测试连接」不真发消息，查不出应用可见范围的问题，所以才需要这个按钮。

那一行的「同时推送消息」开关对应 `notify`，默认开。关掉以后通知不再推送，站内信照常发。

### 发给谁，失败怎么办

接收人是通知范围（全体、按角色、指定用户）里启用的用户中，已绑定企业微信的那些。
没绑定的人只收站内信，不报错。

推送在后台进行。发布通知的请求只把它放进内存队列就返回，所以企业微信慢或者不通，不会拖慢发布，也不会影响调用方的请求。
发送失败只记日志，不向调用方抛错，并按下面的规则重试：

- 网络错误、超时、HTTP 5xx 和企业微信的 `-1`（系统繁忙）：最多发 3 次，第一次失败后等 5 秒，第二次失败后等 30 秒。
- `access_token` 失效（`40014`、`42001`）：清掉缓存，换新 token 重发一次，不占上面的次数。
- 其余错误码（`60020`、`81013`、凭据错误等）：不重试，记 Error 日志。

请求带着企业微信的重复消息检查（10 分钟）。超时了但其实已经发成功的请求，重试时用户不会收到两条。
因此 10 分钟内内容完全相同的两条消息，用户只会收到一条。

队列放在内存里，按序处理。进程重启时还没发出的推送会丢，站内信不受影响。
一条消息在重试的那段时间，后面的消息排队等着。队列满了（容量 10000）会丢弃新的并记 Error。
多副本部署时，只有发布通知的那个实例会发，不会重复。

`PublishAsync` 在方法返回前就把通知交给队列。调用方如果在自己的事务里调用它，事务回滚了，推送可能已经发出。
需要回滚安全的调用方，在事务提交之后再发布通知。内核自己的两处调用（管理员发公告、定时任务告警）都在事务外。

### 企业微信侧的限制

- **频率**：同一成员在每个应用上不超过 30 次/分钟、1000 次/小时，超出的企业微信直接丢弃，也不返回错误，所以日志里看不到失败。短时间连发很多条通知时，后面的卡片可能收不到，站内信都在。官方说明见[频率限制](https://developer.work.weixin.qq.com/document/path/90312)。
- **企业可信 IP**：自建应用要在应用详情页配「企业可信 IP」，把部署服务器的出口 IP 填进去，否则发送报 `60020`。后台日志会以 Error 记下它，测试消息的失败提示里也带着。较早创建的应用可能没有这条要求。官方说明见[这篇文章](https://developer.work.weixin.qq.com/community/article/detail?content_id=16393566093602657085)。
- **应用可见范围**：接收人不在应用的可见范围里，企业微信就拒收这个人。全部接收人都不在时报 `81013`，只有部分不在时消息照发，不在的人记进 Warning 日志。
- **卡片长度**：标题最多 128 个字符，正文最多 512 个字符，超出的部分被截断，正文截断处加省略号。

### 卡片链接与点开后的行为

卡片的标题是通知标题，正文是通知内容去掉 Markdown 标记后的纯文本。
点开的地址是 `CallbackBaseUrl` 加一个站内相对路径：通知正文里的第一个站内相对链接，没有就用 `/personal/notice`。
没配 `CallbackBaseUrl` 时（只可能在开发环境）拼不出地址，这条通知不推送，日志记一条 Warning。

`CallbackBaseUrl` 填的是后端对外的根地址，所以前后端不同源的部署，要让这个地址下的页面路径（比如 `/personal/notice`）能打开前端。
前后端同源的部署不用管。

点开卡片时，登录态还在就直接打开页面。没有登录就沿用上一节的流程：企业微信客户端里静默授权，登录后回到卡片指向的页面。可信域名配好即可，不用另配。

多应用的部署有一个限制：既没有默认应用、也没有记住上次所在应用的用户，点卡片会落在应用选择页，而不是卡片指向的页面。
登录态已过期的用户通常也在此列，因为记住的应用随会话一起清掉了。
给这些用户设好默认应用就能避开：顶栏点「切换应用」进入应用选择页，再点某个应用卡片上的「设为默认」。
只有一个应用的部署，以及设了默认应用的用户，会直接进到卡片指向的页面。

### 自定义渠道和替换

内核只认接口，不认企业微信。同时实现了 `IExternalAuthProvider` 和 `IExternalNoticeSender` 的 provider 都会收到推送，企业微信包里的 `WeComExternalAuthProvider` 就是这么做的。

```csharp
public class MyProvider : IExternalAuthProvider, IExternalNoticeSender
{
    // IExternalAuthProvider 的成员省略

    public Task<ExternalNoticeResult> SendAsync(
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default)
    {
        // subjects 是这批接收人在渠道里的标识，和绑定表里的 Subject 同值，每批最多 1000 个
        // 整次发送失败就抛 ExternalNoticeException，retryable 决定内核要不要重试
        // 渠道拒收的人放进 Rejected，内核只记日志
        return Task.FromResult(new ExternalNoticeResult([]));
    }
}
```

`ExternalNoticeException` 的消息会原样写进内核日志，别带含令牌的请求地址。
要让「登录方式」页给这种类型显示推送开关和测试按钮，类型描述 `IExternalAuthProviderType` 的 `SupportsNotice` 要返回 `true`，默认是 `false`。
`notify` 对每个 provider 一视同仁，缺省开。

要换掉默认行为时：

- `INoticeDispatcher` 用 `TryAddSingleton` 注册，在 `AddSmartAdmin()` 之前注册自己的实现就整体替换。
- 只想改消息的样子，继承 `NoticeDispatcher` 覆写 `BuildMessage`（标题、正文、链接都在这一步生成），覆写 `ResolveSubjectsAsync` 能改发给谁。把子类注册成 `NoticeDispatcher`，写在 `AddSmartAdmin()` 之前。
- 完全替换 `INoticeService` 的应用不会自动推送。推送是 `NoticeService.PublishAsync` 发布后调用 `INoticeDispatcher.Enqueue` 触发的，自己的实现要自己调。

## 端点

登录、回调、绑定的端点挂在 `api/v1/auth/external` 下：

| 端点 | 用途 |
| --- | --- |
| `GET providers` | 列可用 provider，前端据此渲染登录按钮 |
| `GET providers/all` | 管理端：列出注册表里全部已配置的 provider（含已禁用），带启用状态和按账号关联开关，供「登录方式」页的开关 |
| `GET {provider}/authorize` | 换取跳转地址，带上一次性 state |
| `GET {provider}/callback` | 厂商回调落点 |
| `POST exchange` | 用一次性票据换令牌 |
| `POST pending-link/claim` | 认领未绑定的外部身份：账密/短信登录成功后，把回调阶段解析出的外部身份绑到当前用户 |
| `GET bindings` | 当前用户已绑定的外部身份 |
| `POST {provider}/bind` | 【个人中心】发起绑定一个外部身份 |
| `DELETE {provider}/binding` | 【个人中心】解绑一个外部身份 |

「登录方式」页管理 provider 配置的端点挂在 `api/v1/sys/external-auth/providers` 下，模块名是 `ExternalAuth`：

| 端点 | 用途 |
| --- | --- |
| `GET` | 目录：已装的类型，加每个 provider 的配置状态、明文字段、`hasValue` 与尾四位、回调地址 |
| `PUT {code}` | 新增或更新一条配置，请求带 `type`；机密字段留空表示不修改，要求近期重新验证身份 |
| `DELETE {code}` | 清除一条配置，不删除用户已绑定的外部账号，要求近期重新验证身份 |
| `POST test` | 测试连接，用表单里的值，机密留空则用已保存的 |
| `POST {code}/test-notice` | 发送测试消息：向当前管理员在该 provider 下绑定的账号发一张卡片，用已保存的配置。结果在返回体的 `status` 里（`ok`、`rejected`、`failed`、`notBound`、`noBaseUrl`、`unsupported`），信封码仍是 0 |

`state` 和一次性票据都复用短信验证码那套成法：进缓存、`GetAndRemoveAsync` 原子取删，单次有效。`state` 只用字母和数字，企业微信和微信的 OAuth 只收这个字符集。

外部登录解析出 `SysUser` 之后，接的是 `AuthService.CreateTokenAsync`。建会话、发令牌这段尾链，和账密登录、短信登录完全共用。所以会话并发策略、强退、刷新令牌轮换，对它一视同仁。

## 回调换令牌示例

`authorize` 和 `callback` 都是浏览器整页跳转，不是前端能 `fetch` 的 JSON 接口。`authorize` 302 到 IdP 的授权页；IdP 验证完用户后回跳 `callback`，`callback` 再 302 回前端结果页（默认 `FrontendResultPath`，即 `/oauth/callback`），查询串上带着下一步要用的东西：

```
成功：GET /oauth/callback?ticket=<一次性票据>
失败：GET /oauth/callback?error=40015
```

前端在结果页里认到 `ticket`，拿它去换令牌，这一步才是真正能 `fetch` 的接口：

```bash
curl -X POST http://localhost:5100/api/v1/auth/external/exchange \
  -H "Content-Type: application/json" \
  -d '{"ticket":"<回调带回来的一次性票据>"}'
```

响应信封和[账密登录](/zh/guide/getting-started)同一个形状：

```json
{ "code": 0, "data": { "accessToken": "eyJ...", "expiresAt": "...", "refreshToken": "...", "mustChangePassword": false } }
```

票据一次性：`exchange` 内部用 `GetAndRemoveAsync` 原子取删，重复换第二次会拿到 `OAuthStateInvalid`（40014）。

## 错误码

| 码 | 名 | 什么时候 |
| --- | --- | --- |
| 40013 | `OAuthProviderDisabled` | 这个 provider 被运营开关关了，或者没有完整配置 |
| 40014 | `OAuthStateInvalid` | state 对不上或已被消费 |
| 40015 | `OAuthExchangeFailed` | 向厂商换令牌失败 |
| 40016 | `OAuthAccountNotBound` | 没绑定，且这个 provider 不许自动开户 |
| 40017 | `OAuthAlreadyBound` | 这个外部身份已经绑在别的账号上 |

按[前后端契约](/zh/frontend/api-contract)的规矩，这几个码在两份语言包里都要配上对应的 `msgKey` 文案。漏一个，后端的一致性测试就直接变红。
