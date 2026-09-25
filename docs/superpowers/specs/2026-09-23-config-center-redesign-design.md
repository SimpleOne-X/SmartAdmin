# 配置中心:页签、实时预览、Logo 上传

日期:2026-09-23
状态:已确认
范围:`web/packages/admin/src/views/system/config/**`、`web/packages/admin/src/composables/{useSite,usePageTitle}.ts`、
`web/packages/admin/src/lib/`、`web/packages/admin/src/locales/*`、`web/e2e/config-center.spec.ts`、
`backend/src/SmartAdmin.Services/Config/*`、`backend/src/SmartAdmin.AspNetCore/Controllers/ConfigController.cs`、
`backend/src/SmartAdmin.Services/Seed/*`、`site/zh/frontend/appearance.md` 及英文页。
不涉及:配置表结构、配置键名、高敏感权限的交互、「按账号自动关联」的交互。

设计稿:`config-apple.html`(macOS「系统设置」风格,会话临时文件,以本文为准)。
第三方登录的连接配置见 `2026-09-23-external-auth-db-config-design.md`。

## 1. 目标

1. 每个页签的内容与页签名一致,一屏放下,页面本身不滚动。
2. 每一类都是「左边设置、右边预览」,未保存的改动实时反映在预览里。
3. Logo 像换头像一样:编辑、上传、裁剪、预览、保存生效。
4. 全页一个保存入口:底部保存条统计未保存项,导航上标出改过的分类。
5. 界面极简,遵循 macOS「系统设置」的分组列表风格。

## 2. 信息架构

顶部导航 7 项,分两组;当前项写进 URL 查询 `?tab=`,刷新和分享链接回到同一项。

| 组 | 页签(`tab`) | 管理的配置 | 预览 |
|---|---|---|---|
| 站点与安全 | 品牌与登录页 `brand` | `sys.site.*`、`sys.login.hero.*` | 一块预览,分段控件切「后台 / 登录页」,编辑哪一组自动切过去 |
| 站点与安全 | 安全策略 `security` | `sys.security.*`(不含短信验证码登录) | 用户会遇到的规则清单 + 密码试填 |
| 站点与安全 | 登录方式 `signin` | `sys.externalauth.*`、`sys.security.smsLogin.enabled`、各第三方的连接配置 | 登录框 |
| 站点与安全 | 敏感操作 `sensitive` | 高敏感权限表 | 对应的再次验证框 |
| 系统 | 文件上传 `upload` | `sys.upload.*` | 用户看到的上传框 |
| 系统 | 定时任务 `job` | `sys.job.*` | 生效说明 |
| 系统 | 高级 `advanced` | 没有被以上页签认领的配置行 | 无,整页给键值表 |

页签名与内容一致的取舍:

- 短信验证码登录属于登录方式,归「登录方式」。
- 高敏感权限是接口清单、即时增删,和安全开关不是一类,单独成「敏感操作」页签。
- `tabOfKey` 把 `sys.site.*` 与 `sys.login.*` 归到 `brand`,把 `sys.externalauth.*` 与短信登录键归到 `signin`。

## 3. 版式与组件

- **`SectionLayout`**:页头 + 左右两栏。`layout` 取 `even`(1:1)、`wide`(3:2,设置多的安全策略)、`slim`(2:3,设置少的上传与定时任务)。
  没有 `#preview` 的分类整页给内容。设置列放不下时在列内滚。
  `previewAlign` 决定预览内容垂直居中(缩小版界面)还是自上而下(列表类)。
- **`CfgGroup`**:圆角分组卡片,1px 边框,组名在卡片外的左上方。
- **`CfgRow`**:一行,左标签(可带一行说明)右控件,统一最小高度 48px。`keys` 里有未保存改动时标签前显示橙点。
  带文本输入框的行,标签与输入按 1:1.8 分。
- **`CfgSegment`**:分段控件,radiogroup 语义。
- **`vFlash`**:改了哪一项,预览里受影响的部分闪橙框;`prefers-reduced-motion` 下关闭。
- **字号**:引用全站字号变量。标签、输入、预览正文用 `--font-size-base`,说明、分组标题、单位用 `--font-size-sm`。
- 开关的长说明放在 ⓘ 悬停提示里,数值框没有加减按钮。
- 宽度小于 1280px 时,安全策略从两列变单列。

## 4. 统一草稿与保存

`useConfigDraft()`(`draft.ts`,页面内 `provide`/`inject`,不进 Pinia,离开页面即丢弃):

- 进入页面时一次并发加载所有结构化分组与第三方目录,得到 `original` 与 `values`。
- `dirtyKeys` 是值与 `original` 不同的键,每个键登记所属页签,由此算出导航上的黄点。
- Logo 另有待上传的 `pendingLogo: Blob | null`,预览期间 `values[LOGO_KEY]` 是它的 `blob:` 地址。
- 各分类组件只读写草稿,没有自己的保存按钮。
- 第三方开关(`enabled`、`linkByAccount`)是草稿项;连接信息与密钥在设置面板里直接保存,不进草稿。
  `refreshProviders()` 在面板保存或清除后重读目录:新配好的开关补进草稿,已有的不动,被清除的移出。

**底部保存条**:浮在底部正中的毛玻璃胶囊,有改动时滑出,「有 N 项修改未保存 · 放弃修改 · 保存」。

- 保存:有 `pendingLogo` 先上传并把签名直链写进草稿 → 一次 `configApi.saveBatch(只含脏键)` →
  `original = values` → `loadSite(true)` 让全站品牌即时刷新 → `bumpConfigRevision()` 让「高级」表重拉。
- 保存按钮受 `v-auth="'PUT:/api/v1/sys/config/batch'"` 控制;无权限时保存条仍显示改动数,只有「放弃修改」。
- 校验:强调词不在主标题里时警告但允许保存;卖点超过 5 条时阻止保存并跳到「品牌与登录页」。
- 防丢改动:有未保存改动时 `onBeforeRouteLeave` 弹确认框,`beforeunload` 让浏览器提示。
  切换页签不丢改动,草稿在页面级。

后端 `SaveValuesAsync` 逐条更新、不带事务。一次保存跨多个分组时,数据库中途故障会部分写入;
写的是最终值,重试保存即可收敛。

## 5. Logo:头像式上传

### 5.1 交互

- 圆角方框显示当前 Logo;没有 Logo 时显示站点标题首字。
- 「编辑」下拉:上传新图片(选文件或拖入)、使用图片链接(填 URL,如放在 CDN 上的 Logo)、移除 Logo。
  移除后回落到 `createSmartAdmin({ brand: { logo } })` 或内置矢量标。
- 选图后弹出裁剪框:正方形取景框,拖动平移,滑块或滚轮缩放,键盘方向键平移、`+`/`-` 缩放。
- 裁剪结果存成 `pendingLogo`(PNG Blob)+ 一个 `objectURL` 用于预览;保存时才上传,
  放弃修改或离开页面不会在文件表里留下孤儿文件。

### 5.2 约束

- 裁成正方形,导出 256×256 PNG,保留透明底。侧栏、登录页、应用选择页都是「图标 + 站点标题」,
  文字由站点标题提供,Logo 只需是图标;正方形同时能直接当 favicon。
- 不引入裁剪库:`LogoCropper.vue` 用 canvas 实现「正方形 + 平移 + 缩放」。
- 选图限制:PNG / JPG / WEBP,原图不超过 5 MB。不接受 SVG(`/view` 端点出于 XSS 考虑不内联 SVG,见 `SysFileController.InlineSafeTypes`)。

### 5.3 后端

`POST /api/v1/sys/config/logo`(multipart,字段 `file`)返回签名直链,由可替换服务 `ISiteLogoService`(`TryAddScoped`,已登记进 `ReplaceabilityContract`)实现。

- 权限:按钮「配置-上传Logo」(Id 319,`POST:/api/v1/sys/config/logo`),挂在系统配置菜单下。
  按钮个位 3 固定是 PUT(`MenuSeedIdLayoutTests`),所以它独立一颗,不并入「配置-更新」。
- 上传策略:固定白名单(`.png` `.jpg` `.jpeg` `.webp`)和 1 MB 上限,不读全局「上传限制」。
- 内容校验:按文件头魔数确认是图片,存储后缀按识别结果定,不信文件名与客户端报的 Content-Type。
- 存储与记账走 `IFileStorage` 与 `sys_file`,文件管理里可见。

### 5.4 签名链接续签

`sys.site.logo` 存的是 `/api/v1/sys/file/{id}/view?sig=…[&exp=…]`。开了 `SmartAdmin:Upload:SignedUrlTtlMinutes` 时链接会过期。
`ConfigService.GetSiteInfoAsync` 在读出缓存之后,对匹配本地签名直链形状的 `logo` 调 `LocalFileUrl.Refresh` 重新签一份再返回:

- 重签在缓存之外,因为站点信息缓存的寿命可能比链接寿命长。
- 先验签:签名(忽略过期)对得上才按文件 Id 现签;伪造的签名原样返回,防止能写配置的人借匿名的站点信息端点签出任意文件的直链。
- 外部 URL 原样返回。没开寿命时 `BuildUrl` 结果不变。

## 6. favicon 跟随 Logo

`sys.site.logo` 非空时,浏览器标签页图标是这张图;清空时是模板自带的 favicon。

- 实现在 `usePageTitle`(它管浏览器标题与 `<html lang>`),纯函数在 `lib/favicon.ts`:
  首次替换前把 `link[rel~="icon"]` 的 `href` 记在 `data-default-href` 上,有 Logo 时把这些 `link` 的 `href` 全部设成 Logo 地址,清空时还原。
  改所有 `rel=icon` 而不是追加一个,因为各浏览器从多个候选里挑哪个并不一致。
- `apple-touch-icon` 不动:iOS 主屏图标需要 180×180 不透明底,和 Logo 的要求不同。
- `createSmartAdmin({ brand: { faviconFromLogo: false } })` 关闭该行为,默认 `true`。

## 7. 「高级」页

- 数据:分页列出所有未被结构化表单认领的配置行。认领清单在前端 `groups.ts` 里集中定义,各分类组件从这里取。
- 后端 `ConfigPageInput.ExcludedKeys` 与 `ExcludedGroupCodes` 并列,服务端过滤后再分页,总数按过滤后计算。
- 交互:分组下拉、键/名称搜索、新增/编辑/删除,复用 `ConfigFormModal`。
- 新增的键恰好是被认领的键(如手动加了 `sys.site.title`)时,保存后提示「该配置由『品牌与登录页』管理」。

## 8. 预览

预览只读草稿,不调接口。登录框由 `LoginMock` 画(`form-only` 只画右侧登录框),后台缩略图由 `AdminMock` 画;
预览按固定设计尺寸绘制,由 `ScaleToFit` 按剩余空间整体等比缩放。

| 预览 | 内容 |
|---|---|
| 品牌与登录页 | 后台缩略图(浏览器标签页、侧栏品牌、页脚版权),或登录页(Hero 复用 `LoginHeroPanel`,登录框读验证码、短信入口与第三方按钮的草稿) |
| 安全策略 | 规则文案由纯函数 `lib/securityRules.ts` 从草稿算出;关闭的规则只用灰色,读屏用隐藏文字「(未启用)」;下方是密码试填 |
| 登录方式 | 登录框;第三方按钮的显示条件是「已配置且启用」 |
| 敏感操作 | 点表格一行,右侧显示对应的再次验证框 |
| 文件上传 | 上传框与「支持 … 单个文件不超过 … MB」,后缀为空时写「服务端默认格式」 |
| 定时任务 | 两条生效说明,收件人为空时灰显 |

## 9. 契约

- 配置键、分组编码、种子数据不变。
- `sys.site.logo` 可以是外部 URL 或本地签名直链,两者都能显示。
- 前端公开 API 只有 `brand.faviconFromLogo` 一个选项。`views/system/config/*` 的子组件不在 `index.ts` 导出;
  同名页面 key 覆盖整页的消费方不受影响。
- 后端:`ConfigPageInput` 的可选字段 `ExcludedKeys`、一个新端点、一条按钮种子、一个可替换服务 `ISiteLogoService`。

## 10. 测试

后端(xUnit,`ConfigCenterTests`、`SiteLogoTests`):

- Logo 端点:PNG/JPG/WEBP 成功并返回签名直链;SVG、改了扩展名的非图片、超过 1 MB 被拒;
  全局上传白名单去掉 `.png` 后 Logo 仍能上传;无权限 403。
- 站点信息:开启 `SignedUrlTtlMinutes` 后,库里过期的签名直链经 `GetSiteInfoAsync` 返回新签、可访问的链接;
  伪造签名与外部 URL 原样返回。
- 分页:`ExcludedKeys` 生效,总数按过滤后计算。

前端(vitest):

- `securityRules.ts`:各开关、数值、时长换算的文案。
- `draft.ts`:脏键与页签黄点、放弃还原、保存只发脏键、先传 Logo 再批量保存、`refreshProviders`。
- `favicon.ts`:设 Logo 替换全部 `rel=icon`、清空还原、`faviconFromLogo: false` 不动。
- `lib/logoCrop.ts`:取景框与缩放、平移的换算。

e2e(`web/e2e/config-center.spec.ts`):

- 改站点标题,预览即时变化,导航出现黄点,保存后侧栏标题更新。
- 上传 Logo,裁剪,预览出现,保存,刷新后侧栏与 favicon 是新 Logo。
- 有未保存改动时离开页面弹确认;`?tab=` 直达;页签导航可用键盘操作。
- 登录方式:设置面板配置第三方、机密不回显、清除;测试连接逐项显示。

## 11. 文档

- `site/zh/frontend/appearance.md`「Logo:一处配置,全站生效」:上传入口、正方形建议、favicon 行为与 `faviconFromLogo`;英文页同步。
- `npm run gen:api` 生成的 `schema.d.ts` 含新端点与新字段。
