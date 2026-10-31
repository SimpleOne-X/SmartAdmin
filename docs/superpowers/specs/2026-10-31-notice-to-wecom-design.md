# 站内通知自动推送到企业微信

## 为什么做

系统消息和审批消息现在只进站内信，人不打开系统就看不到。
使用方已经在企业微信里用自建应用登录这个系统，登录时系统已经对应到具体的人，
所以应用凭据和“发给谁”都已经有了，缺的只是发送这一步。

配好企业微信登录，通知就自动同时发到企业微信，不需要再配一套。

## 目标

- 经 `INoticeService.PublishAsync` 发布的每条通知，都推给接收人里已绑定企业微信的用户。
  定时任务告警、管理员发的公告和审批模块的消息都走这个方法，所以都覆盖到，上层模块不用改。
- 复用「系统配置 → 登录方式」里已有的企业 Id、AgentId、Secret。
- 消息是一张文字卡片，点开进入系统对应页面；在企业微信里没有登录时自动静默登录，不用再点一次。
- 管理员能关掉推送，也能发一条测试消息确认配置通了。

## 不做

- 在企业微信卡片上直接点通过或驳回。第一版点开进入系统处理。
- 钉钉等其他渠道的实现。接口留好，以后实现即可。
- 发送记录表、失败重发界面、消息撤回、按人订阅。
- 群机器人、外部联系人消息。

## 绝不能发生

- 企业微信发送失败或变慢，影响通知发布、审批流程或任何请求的响应时间。
- `access_token`、`corpsecret` 进日志或接口响应。
- 消息正文里的内容能注入卡片的 HTML，或者卡片链接能跳到站外。
- 退出登录后，在企业微信里又被自动登录回去。

## 用户已决定

- 用现有自建应用，与登录共用一套凭据。
- 企业微信实现放在 `SmartAdmin.Auth.WeCom`，内核只定义接口。
- 站内信是正式记录，企业微信只是多一条通知渠道；没绑定的人只收站内信；发送失败记日志并重试有限次数。
- 消息用文字卡片，第一版不做卡片上直接审批。
- 推送默认打开，全体公告也推给所有已绑定的人。

## 设计者的决定

下面这些没有和用户逐条商量，是写设计时定的，由用户批准文档时确认：

- 发送能力实现在现有登录 provider 上，不另建渠道注册表和配置。
- 用内存队列，不建发送记录表。进程重启会丢还没发的推送，站内信不丢。
- 用 `CallbackBaseUrl` 拼链接，不新增配置。
- 瞬时错误最多重试 3 次，用 10 分钟重复消息检查防止重试造成重复。
- 增加「发送测试消息」按钮和 `notify` 开关。
- 顺手修复“主动退出后被自动登录回去”的已有漏洞。

## 官方接口约束

来自企业微信官方文档，实现时以当时文档为准：

- 接口：`POST https://qyapi.weixin.qq.com/cgi-bin/message/send?access_token=...`
  （[发送应用消息](https://developer.work.weixin.qq.com/document/path/90236)）。
- `touser` 用 `|` 分隔，一次最多 1000 个。
- `textcard` 字段：`title` 最多 128 字符，`description` 最多 512 字符，
  `url` 必填、带协议头、最长 2048 字节，`btntxt` 最多 4 个字，超出都会截断。
  `description` 支持 `div` 和 `br` 标签。
- `enable_duplicate_check` 开启后，`duplicate_check_interval`（最长 4 小时）内请求 JSON 相同的消息不会重复接收。
- 部分接收人无效时仍会发送，无效的人列在 `invaliduser`，没有接口许可的列在 `unlicenseduser`；
  全部无效时 `errcode` 为 `81013`。接收人不在应用可见范围内是常见原因。
- 每个应用每天不超过“账号人数上限 × 200”人次；对同一成员不超过 30 次/分钟、1000 次/小时，
  **超出的直接丢弃，不报错**（[频率限制](https://developer.work.weixin.qq.com/document/path/90312)）。
- 2022 年 6 月 20 日之后新建的自建应用要配「企业可信 IP」，否则报 `60020`
  （[官方开发者中心文章](https://developer.work.weixin.qq.com/community/article/detail?content_id=16393566093602657085)）。

## 设计

### 分层

```text
SmartAdmin.Core       IExternalNoticeSender、ExternalNoticeMessage、ExternalNoticeResult、ExternalNoticeException
SmartAdmin.Services   NoticeService 发布后入队；INoticeDispatcher 后台解析接收人并调用发送方
SmartAdmin.Auth.WeCom WeComExternalAuthProvider 实现 IExternalNoticeSender
```

`SmartAdmin.Auth.WeCom` 只引用 Core，不能碰 `SysUserExternal`，
所以解析“发给谁”归内核，发送方只收到企业微信 userid 列表。

### 发送接口放在登录 provider 上

```csharp
public interface IExternalNoticeSender
{
    Task<ExternalNoticeResult> SendAsync(
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default);
}

public sealed record ExternalNoticeMessage(long NoticeId, string Title, string Text, string Url);

/// <param name="Rejected">对方明确拒收的外部标识,只用于记日志。</param>
public sealed record ExternalNoticeResult(IReadOnlyList<string> Rejected);

/// <summary>发送失败。Retryable 为 false 时重试也没用(配置错、IP 不可信);ProviderCode 是对方的错误码。</summary>
public sealed class ExternalNoticeException(
    string message, bool retryable, string? providerCode = null, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
    public string? ProviderCode { get; } = providerCode;
}
```

`WeComExternalAuthProvider` 实现这个接口。
这样不用新注册表：内核遍历 `IExternalAuthProviderRegistry.ListAsync()`，`is IExternalNoticeSender` 的就能发。
实例已经按配置缓存，所以直接复用它的 `access_token` 缓存；
管理员改配置后实例随之重建，用代码注册的 provider 也一样能发。

异常消息不许含 URL：URL 里有 `access_token`。

### 发布流程

1. `NoticeService.PublishAsync` 写完通知和接收行后，把快照（Id、标题、正文、接收范围、接收目标）交给 `INoticeDispatcher`。
   入队不阻塞、不抛异常。`NoticeService` 新增可选尾参 `INoticeDispatcher? dispatcher = null`，子类不用改。
2. 默认实现是一个后台服务，自带有界队列（容量 10000），满了丢弃并记 Error。
   **不走 `IEventBus`**：`ChannelEventBus` 是单循环按序派发，一次慢的 HTTP 请求会卡住所有订阅者。
3. 没有任何 provider 实现 `IExternalNoticeSender` 时不入队，没装企业微信包的应用零开销。
4. 后台逐条处理：对每个能发的 provider，查 `sys.externalauth.{code}.enabled` 和 `.notify`，都未关闭才继续。
5. 解析接收人，得到启用用户的 Id：`All` 是全部启用用户，`Role` 经 `sys_user_role`，`User` 是目标本身。
   再按 `(Provider, UserId)` 查 `sys_user_external` 得到 `Subject`，去重。
6. 每 1000 个 `Subject` 一批调用 `SendAsync`。

`SysUser`、`SysUserRole`、`SysUserExternal` 都继承 `BaseEntity`，不受机构数据范围过滤，所以查询不用 `ClearFilter`。
消费循环必须是宿主启动时拉起的 `BackgroundService`，不能在请求线程里懒启动，
否则请求的 `HttpContext` 会经 `AsyncLocal` 流进后台循环。

只在发布的那个实例上入队，多副本不会重复发。进程重启时队列里还没发的消息会丢，站内信不受影响。

`PublishAsync` 如果在调用方的事务里执行，事务回滚时消息可能已经发出。
内核的两处调用和审批模块都在事务外发布，这个限制写进 XML 注释。

### 重试

- 网络错误、超时、HTTP 5xx、`errcode` 为 `-1`（系统繁忙）：最多 3 次，间隔 5 秒、30 秒。
- `access_token` 失效：清掉缓存，换新 token 重试一次，不占上面的次数。
- 其余 `errcode`（`60020`、`81013`、凭据错误等）：不重试，记日志。
- 请求带 `enable_duplicate_check=1`、`duplicate_check_interval=600`，
  这样超时但其实发成功了的请求，重试时用户不会收到两条。
  代价是 10 分钟内内容完全相同的两条消息只收到一条，这是可接受的。
- 队列是单消费者，重试期间后面的消息等待。这段时间很短，而且企业微信不通时后面的消息也发不出去。

日志级别：`60020` 记 Error 并提示配置企业可信 IP；其他失败记 Error；
`invaliduser`、`unlicenseduser` 记 Warning，提示检查应用可见范围和接口许可。

### 消息内容

由内核把通知转成 `ExternalNoticeMessage`，放在 `protected virtual` 方法里，消费方可以覆写：

- **标题：** 通知标题。
- **正文：** `Content` 从 Markdown 转成纯文本：链接只留文字，去掉标记，折叠空行。
- **链接：** 正文里第一个 `IsSiteRelativeUrl` 通过的相对链接；没有就用 `/personal/notice`。
  `SysNotice.ActionsJson` 里是接口地址，不是页面，不用它。
- **完整地址：** `SmartAdmin:ExternalAuth:CallbackBaseUrl` 加相对路径。
  外部登录的结果页本来就是这个地址下的相对路径，所以生产环境必配的这一项够用，不新增配置。
  没配时（只可能是开发环境）跳过发送并记 Warning。

企业微信实现再把它转成 `textcard`：`description` 先转义 `&`、`<`、`>` 防止注入标签，
超过 512 字符时自己截断并加省略号，`btntxt` 用「详情」。

### 点开后直达

这条路现在就通，不用新做：未登录打开深链，路由守卫跳到 `/login?redirect=...` 并暂存回跳地址；
`LoginForm.vue` 经 `claimWeComAutoLogin` 判断在企业微信客户端里，自动发起静默授权；
`oauth/callback.vue` 换到登录态后回到暂存的地址。
每个浏览器会话只自动跳一次，授权失败回到登录页不会循环。
不带 `redirect` 的自动登录也保留，从工作台点应用图标进来靠它。

只补两处：

- **加一条 e2e**，锁住“带 `redirect` 的深链，静默登录后回到原页面”。
- **修一个已有漏洞：** 本地有持久化 token 时，本次会话没进过登录页，一次性标记还没写，
  这时主动退出会被立刻静默登录回去。三处主动退出（`AppHeader.vue`、`views/module/index.vue`、`views/personal/password.vue`）
  在跳登录页前写入这个标记。
  不能写在 `user.clear()` 里：token 过期时也走它，点卡片直达就失效了。

### 配置

新增运营开关 `sys.externalauth.{code}.notify`，存 `sys_config`，缺省为 true，与现有 `enabled` 同一种形式。

`IExternalAuthProviderType` 加默认接口成员 `bool SupportsNotice => false`（已有实现不用改），
`WeComAuthProviderType` 返回 true。登录方式管理页只对它显示：

- 「同时推送消息」开关。
- 「发送测试消息」按钮：发给当前管理员绑定的企业微信账号，把成功、`ProviderCode` 和对方的错误说明显示出来。

测试消息直接调一次 `SendAsync`，不入队、不重试，几秒内有结果。
正文带发送时间，否则重复消息检查会让 10 分钟内的第二次点击显示成功却收不到。
当前管理员没绑定、没配 `CallbackBaseUrl`，都作为结果里的提示返回，不抛错。
接口放在 `ExternalAuthProviderController`，形状照现有连接测试 `Test`；
权限码就是路由，新路由要加到 `DefaultMenuSeed.cs` 里连接测试所在的按钮（Id 312），并照现有种子升级做法覆盖存量库。

连接测试不验证发送能力，因为不真发就查不出可见范围问题，所以才要这个按钮。

使用者要做的事，写进文档站：

1. 已装 `SmartAdmin.Auth.WeCom`，并在 `AddSmartAdmin()` 前调了 `AddSmartAdminWeComAuth()`。
2. 在「系统配置 → 登录方式」配好企业微信，生产环境配好 `CallbackBaseUrl`。
3. 在企业微信管理后台确认：应用可见范围包含要接收的成员；部署服务器的出口 IP 在「企业可信 IP」里。
4. 用户在企业微信里登录过或绑定过账号。
5. 点「发送测试消息」确认。

### 可替换性

- `INoticeDispatcher` 用 `TryAddSingleton` 注册，并加进 `ReplaceabilityContract`。
- `SendAsync`、转换方法都是 `virtual`。
- 完全替换 `INoticeService` 的应用不会自动推送，它可以自己调 `INoticeDispatcher`。

### 升级影响

不新增表和列，不新增依赖，不是破坏性变更，走次版本。

**行为变化要写进 CHANGELOG：** 已配好企业微信登录的应用，升级后马上开始推送，
包括发给全体的公告。不想要就先把 `sys.externalauth.wecom.notify` 设成 `false`。

## 测试

全部用假 `HttpMessageHandler` 和假 `IExternalNoticeSender`，不连企业微信：

- 内核：全体、角色、用户三种接收范围都解析到正确的 `Subject`；停用用户、没绑定的人被跳过；
  `enabled` 或 `notify` 关闭时不发；超过 1000 人分批。
- 发送失败、发送方抛异常、队列满，都不影响 `PublishAsync` 的返回和站内信。
- 重试：可重试错误重试到上限，不可重试的只发一次。
- 企业微信：请求 JSON 字段正确；`invaliduser` 进 `Rejected`；token 失效换新后重试；
  正文里的 `<`、`&` 被转义；日志和异常消息不含 token。
- 链接：取第一个站内相对链接；协议相对地址和外链不被采用；没有链接时用 `/personal/notice`。
- 前端：带 `redirect` 的深链静默登录后回到原页面；主动退出后不再自动登录；token 过期回到登录页仍会自动登录。
  已有的 `oauthBrand.spec.ts`、`wecom-login.spec.ts` 不改。
- 测试消息：不走重试；没绑定、没配 `CallbackBaseUrl`、对方报错都返回提示，不抛错。
- 可替换性合约测试和四种数据库的用例照常跑；接收人查询用比较式谓词，兼容 SqlServer。

**自动化测不到真实企业微信。** 卡片样式、`description` 转义后的显示、手机端静默登录直达，
要在真实企业里用测试消息人工验一次，这一步由有企业微信账号的人来做。

## 留给实现者

- 通知发布的 XML 注释和文档站页面的具体落点。
- `sys_config` 里 `notify` 键是否需要种子行，按 `enabled` 现有做法。
- Markdown 转纯文本的具体写法。没有依赖，够用即可，不追求完整解析。
- 测试消息接口的路由和错误码，按现有登录方式管理接口的约定。
