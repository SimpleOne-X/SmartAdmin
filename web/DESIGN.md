# SmartAdmin 设计系统规范(DESIGN.md)

> 整体设计依据见 `../docs/rebuild-design.md`。**tokens 单源:[`packages/admin/src/styles/tokens.css`](packages/admin/src/styles/tokens.css)** —— 一切颜色/字号/间距/圆角/阴影都从那里的 CSS 变量取,本文件只做规范说明与「token → Naive UI」映射。
> **令牌名**:页面里写 `var(--text-3)` 这类基础令牌名即可;`--color-*` 等旧命名保留为兼容别名(见 §2.2),只为仍在用旧名的代码保底,新代码不要再写。
> **字体**:**不引入** Google Fonts 等外部字体,字体族走系统字体栈(`-apple-system, BlinkMacSystemFont, 'SF Pro Text', 'Segoe UI', 'PingFang SC', 'Microsoft YaHei', …`,等宽走 `'SF Mono', ui-monospace, Menlo, Consolas, …`),字号 / 字重见 §2.1。

---

## 1. 设计基调

macOS 风格的企业后台:固定壁纸画布(浅色与登录页同味:底色 `#e9f0fd` 上铺登录页同一组强调色色场 `--desk-1..4`,玻璃与阴影带冷蓝调,托住纯白卡片;暗色为深蓝黑墨色加鲜明的极光柔光,卡片 `#101828` 带冷调高光边缘;暗色另有强调色辉光点缀:主按钮)+ 内容卡片**不透明实底**(玻璃只给侧栏 / 顶栏 / 弹层 / 登录卡,内容层不用玻璃),整站不画网格;线条款图标(Phosphor `ph:xxx`,不用 `-duotone`);控件小而密(默认高 30px,正文 15/24)。默认强调色是 macOS 系统蓝 `#0A84FF`,用户可在外观面板换成另外 5 个(§5)。明暗双主题对等。

## 2. Design Tokens(概览,权威值见 `tokens.css`)

**业务只消费角色令牌层,不直接写十六进制色值。**

### 2.1 令牌分层

| 层 | 令牌 | 说明 |
|---|---|---|
| 画布 / 容器 | `--bg-app`(Naive bodyColor)/ `--wallpaper`(body 实际背景:多层渐变)/ `--bg-sidebar` / `--bg-header`(半透明玻璃)/ `--bg-card`(内容卡片实底)/ `--bg-card-glass` / `--bg-elevated`(弹层 / 抽屉,配 `backdrop-filter`)/ `--input` / `--input-disabled` | 禁用框和可填框必须是两个令牌,同色会长一个样 |
| 文字 | `--text-1`(主 / 标题)`--text-2`(次级)`--text-3`(辅助 / 占位)`--text-4`(禁用) | 四级 |
| 线 / 填充 | `--border`(输入描边)`--separator`(分隔线)`--hairline` / `--hairline-strong`(发丝线)`--fill` / `--fill-strong`(灰按钮 / 嵌入底)`--hover` / `--pressed` / `--mask` / `--seg-track` / `--seg-thumb` / `--switch-off` | |
| 玻璃 | `--glass` / `--glass-strong` / `--glass-panel` / `--glass-border` / `--glass-shadow` / `--glass-shadow-soft` / `--glass-solid` / `--edge-top` `--edge-mid` `--edge-bottom` `--edge-light`(卡片发丝描边与内顶边高光) | 弹层统一玻璃 |
| 语义色 | `--ok` / `--warn` / `--err` / `--info` + `-bg`(`color-mix` 12% 透明浅底) | 语义色文字是 AA-large 档,正文级用途改用 `--text-2` |
| 阴影 | `--shadow-1`(卡片)`--shadow-2`(hover 上浮)`--shadow-3`(弹窗 / 抽屉) | 暗色更重 |
| **强调色(运行时写)** | `--accent` / `--signal`(信号色 = 强调色本色:图标 / 聚焦环 / 数据线)`--signal-glow` / `--acc-solid`(实心控件底色)`--on-acc`(压在其上的字色)`--acc-hover` / `--acc-pressed` / `--sel-bg` `--sel-ring` `--sel-halo` `--sel-fg`(选中态)`--tab-active-*` / `--tab-on-*`(页签)`--ambient-1..4`(应用内环境光)`--desk-1..4`(登录页色场)`--login-accent-2` | `:root` 里的值只是默认蓝的初始展示;`useTheme` 按当前 accent 在运行时算好写到 `<html>` 的 style 上(§5、§7.1) |
| 字号 | `--font-size-{xs,sm,base,md,lg,xl}` = 13/14/15/16/20/24,配 `--line-height-*`;字重 `--font-weight-{normal,medium,semibold}` = 400/500/600 | 正文 base 15/24 |
| 控件 / 行高 | `--control-h-{mini,tiny,sm}` = 20/22/24,`--control-h` = 30(默认),`--control-h-lg` = 42(只给登录页与触屏窄档),`--control-h-huge` = 46;`--sidebar-row-h` = 32,`--table-row-h` = 32 | |
| 间距 | `--space-{4,8,12,16,24,32}` | 4px 基准网格 |
| 圆角 | `--radius-xs` 5(复选框)/ `-sm` 6(标签 / 小控件)/ `-md` 9(按钮 / 输入 / 菜单项)/ `-lg` 14(卡片 / 面板 / 气泡 / 侧栏 / 顶栏)/ `-xl` 18(对话框 / 底部抽屉上沿)/ `-2xl` 22(登录卡 / 大面板) | 页面里的单值圆角只能取这个尺度(2–4 留给滚动条 / 进度条类细小元素,999 是胶囊),优先写 `var(--radius-*)`;`macosMotion.spec.ts` 钉着 |
| 过渡 | `--transition-fast`(0.15s,悬停 / 颜色类)/ `--transition-base`(0.2s)/ `--ease`(位移 / 展开收起,0.22–0.28s)/ `--ease-sheet`(面板滑动) | macOS 手感:减速落定、不回弹(`cubic-bezier` 的 y 不出 0–1)、悬停不位移上浮(只改底色 / 投影)、退场比进场快;`macosMotion.spec.ts` 钉着 |

**明暗切换**:`<html data-theme="dark">` 即暗色,不打即亮色。画布 / 文字 / 线条 / 玻璃 / 阴影 / 语义色整体在 `:root[data-theme='dark']` 下翻转,度量层(字号 / 间距 / 圆角 / 控件高)与兼容别名不用重复,别名引用的是基础令牌,自然跟着翻。

### 2.2 兼容别名 → 基础令牌

别名在 `tokens.css` 末尾,**一律引用基础令牌**(不另存一份值),改一处全站同步。

| 别名 | 基础令牌 |
|---|---|
| `--color-primary` / `-hover` / `-pressed` / `-light` | `--acc-solid` / `--acc-hover` / `--acc-pressed` / `--sel-bg` |
| `--color-success` / `-warning` / `-danger` / `-info`(及各自 `-bg`) | `--ok` / `--warn` / `--err` / `--info`(及 `-bg`) |
| `--color-bg-body` / `-app` / `-container` / `-container-solid` / `-elevated` / `-input` | `--bg-app` / `--wallpaper` / `--bg-card` / `--bg-card` / `--glass-solid` / `--input` |
| `--color-text-primary` / `-secondary` / `-tertiary` / `-disabled` | `--text-1` / `--text-2` / `--text-3` / `--text-4` |
| `--color-border` / `-border-strong` | `--separator` / `--border` |
| `--color-fill` / `-fill-hover` / `-fill-disabled` / `--color-mask` | `--fill` / `--fill-strong` / `--input-disabled` / `--mask` |
| `--color-header-bg` | `--bg-header` |
| `--font-family-base` / `--font-family-mono` | `--font` / `--font-mono` |
| `--color-gray-50 … 900`、`--color-white` | 原语灰阶,固定值不随主题翻转(没有对应新名,保留) |

注意几处**不是同义映射**:`--color-primary-light` 对应半透明的 `--sel-bg`,不是不透明实色;`--color-border` 对应淡的分隔线 `--separator`,要明显描边用 `--border`;圆角一律取 `--radius-*` 的尺度(5 / 6 / 9 / 14 / 18 / 22),不要心算别的数字。

## 3. 布局

竖向侧边栏骨架:左侧可折叠侧边栏(展开 `--sidebar-w` **236** / 收起 `--sidebar-w-collapsed` **76**,收起仅留图标)+ 顶栏(高 `--header-h` **62**,`--bg-header` 玻璃 + `backdrop-filter`)+ 内容区(可选多页签 Tabs)。当前菜单项底 `--sel-bg`,文字 / 图标 `--sel-fg`。

页面三档(按内容区宽度,`useShellBreakpoint`):宽 ≥ 1400 / 中 600–1399 / 窄 < 600。窄档整页自然滚动,表格换成卡片列表,表单抽屉换成底部抽屉。

## 4. 核心页面形态

- **列表页**:一个卡片,顶部单行三段式:左半是 SmartTable 的条件构造器(**不放标题**),右半是业务按钮 + 内置图标(刷新 / 放大还原 / 列设置);勾选后工具栏换成批量栏。规则见 `COMPONENTS.md`「表格统一标准」。
- **树 + 表联动**:左树(机构 / 菜单)+ 右表,树选中过滤右表;内容区 < 1000 时左侧面板收进抽屉。
- **表单**:`FormContainer` 形态跟随全局偏好 `app.formStyle`(外观设置「表单形态」,**默认弹窗**,可切抽屉);抽屉形态下宽 / 中档是右侧抽屉,窄档是底部抽屉高 92%;取消钮 `secondary`。个别页面按实例写死 `variant`,不受偏好影响。
- **角色授权面板**:可勾选菜单权限树(目录 / 页面 / 按钮三级,父子联动)+ 数据范围单选(全部 / 本机构 / 本机构及以下 / 仅本人 / 自定义,选自定义展开机构多选树)。
- **工作台**:统计卡片行(`--shadow-1`)+ 最近登录日志 / 快捷入口,克制不堆图表。

## 5. 组件规范

- **按钮层级**:主按钮 `--acc-solid`(hover → `--acc-hover`,pressed → `--acc-pressed`,字色 `--on-acc`)/ 次要 `secondary`(`--fill` 灰底、无描边)/ 文本 / 危险 `--err` / 禁用。没写 `type` 的默认按钮(如表格库里的「搜索」「更多」)在主题覆盖里渲染成与 `secondary` 同色同无描边,整站只剩一种次按钮。默认高 30(`--control-h`)。
- **表格密度**:舒适 / 紧凑两档(运行时可切,存 `app.density`,打到 `<html data-density>`);紧凑档收紧页内边距与行高(`--row-h` = `--table-row-h` 32);卡片间距不随密度变(见下条);数值列 `tabular-nums`。
- **卡片间距**:全站只有一个值 `--gap-card` = 8px,等于壳层面板(侧栏 / 顶栏 / 内容区)之间的间距,对齐 macOS 的 8pt 网格。顶栏到首张卡片、卡片与卡片(上下、左右)、左右分栏之间都用它;页面根容器不加顶部留白,所以自然滚动页和满屏列表页的首张卡片位置一致,切换页面不跳。卡片内部 padding 不用这个令牌。
- **状态反馈**:标签 = 语义色文字 + `-bg` 浅底 + 圆点;空 / 加载 / 错误态统一走 Naive 内建占位。
- **强调色(6 个,运行时可换)**:全部取自 macOS 系统色,存 `app.accent`(持久化值不在候选内时,afterHydrate 回落默认),候选清单与显示名在 `theme/accents.ts`:

  | 名称 | 值 |
  |---|---|
  | 蓝色(默认) | `#0A84FF` |
  | 靛蓝 | `#5856D6` |
  | 紫色 | `#AF52DE` |
  | 深青 | `#30B0C7` |
  | 墨绿 | `#30D158` |
  | 石墨 | `#8E8E93` |

  **取值标准是原值亮度落在 0.14–0.36**,不是看色相好不好看:这一段 `solid()` 之后色相不跑偏,而且 6 个色的主按钮一律深底白字。亮度 > 0.5 的色(薄荷、亮青、黄)会走「保留原色配深字」的分支,换强调色主按钮字色就从白翻成黑,所以一个都不收。**加新候选色前先算 `solid()` 的结果**。色块预览取 `solid(color)`(与开关 / 主按钮同一套压暗规则),深青 / 墨绿压暗幅度大,所以显示名不叫青色 / 绿色。浅色 / 深色共用同一个强调色值;品牌 Logo 的固定靛蓝 `#646CFF` 不随强调色变。
  - 石墨(近中性)只让控件去色:色场(登录页 `--desk-*`、应用内 `--ambient-*`)退回默认蓝,否则整屏灰成一块;但 `--login-accent-2` 仍走石墨本色。
- **英雄元素(仅登录页 / 欢迎横幅 / 头像)**:主按钮渐变 `btnGrad` + 发光 `glowSh`;**应用内常规按钮走 Naive 平面主色**,不满屏渐变。

## 6. 可访问性

- 文字对比度:主文字 `--text-1`、次文字 `--text-2` 在白底卡片上过 AA(≥ 4.5:1);`--text-2` 压在饱和色场上不够 4.5:1,色场上的正文用 `--text-1`。`--text-3`(辅助 / 占位)浅色 `#586479`、暗色 `#98a6c4` 作辅助用途;`--text-4` 是禁用态。**亮色与登录页保持同一冷蓝调**(文字 `#101828` 起的冷灰阶,分隔线 / 填充是带一点蓝的灰 alpha,阴影沿用登录页的蓝调光晕;清晰度靠加深文字与加实分隔线,不靠换成纯中性灰),**暗色走冷调墨蓝**(文字 `#eef3ff` 起,分隔线 / 描边 / 填充是带蓝的 alpha,以此出科技感);带色相的还有强调色、语义色和壁纸的柔光。侧栏标签用 `--text-1`,不是次级灰。
- **实心控件白字 ≥ 4.5:1**:`solid()` 把强调色压暗到白字对比度 ≥ 4.5:1 才作为 `--acc-solid`(主按钮 / 开关 / 勾选 / 进度的底);强调色当**文字**用时走 `readable()`(`--sel-fg`、链接、侧栏图标:浅色混黑到对白底 ≥ 4.5:1,深色往白提亮 35%)。
- 语义色文字(`--ok` / `--warn` / `--err` / `--info`)已按可读性加深,仍属 AA-large 档,仅用于提示 / 大字 / 按钮 / 标签;放 14px 正文级用途改用 `--text-2`。
- 焦点态保留可见描边(聚焦环 `0 0 0 3px rgba(signal, α)`);交互控件键盘可达;语义色不作唯一区分手段(配文字 / 图标)。

---

## 7. token → Naive UI `GlobalThemeOverrides` 映射

`packages/admin/src/theme/naive-theme.ts` 的 `buildThemeOverrides` 把 tokens 喂给 `n-config-provider :theme-overrides`:强调色相关的值由 `theme/accentTokens.ts` 的 `deriveAccentTokens(accent, dark)` 算,其余值用 `getComputedStyle(document.documentElement).getPropertyValue(...)` 读当前 CSS 变量。切换 `data-theme` / accent 后重算即得新主题对象。核心 `common` 段:

| Naive `common.*` | token |
|---|---|
| `primaryColor` | `--acc-solid`(= `derivePrimary().primary`) |
| `primaryColorHover` / `primaryColorSuppl` | `--acc-hover` |
| `primaryColorPressed` | `--acc-pressed` |
| `successColor` / `warningColor` / `errorColor` / `infoColor` | `--ok` / `--warn` / `--err` / `--info`(hover = 往白提 12%,pressed = 往黑压 12%,suppl = hover) |
| `bodyColor` | `--bg-app` |
| `cardColor` | `--bg-card-glass` |
| `tableColor` / `tableHeaderColor` | `transparent`(表格底 / 表头由卡片与 `--th-bg` 系列承担) |
| `modalColor` / `popoverColor` | `--bg-elevated` |
| `textColorBase` / `textColor1` | `--text-1` |
| `textColor2` | `--text-2` |
| `textColor3` / `placeholderColor` / `clearColor` | `--text-3` |
| `textColorDisabled` | `--text-4` |
| `borderColor` | `--border` |
| `dividerColor` | `--separator` |
| `inputColor` / `inputColorDisabled` | `--input` / `--input-disabled`(必须两个令牌) |
| `actionColor` | `--fill` |
| `hoverColor` / `pressedColor` | `--hover` / `--pressed` |
| `borderRadius` / `borderRadiusSmall` | `--radius-md` / `--radius-sm` |
| `fontFamily` / `fontFamilyMono` | `--font` / `--font-mono` |
| `boxShadow1` / `boxShadow2` / `boxShadow3` | `--shadow-1` / `-2` / `-3` |

### 7.1 派生规则(权威;`web/packages/admin/src/theme/mix.ts` 逐行实现,`mix.spec.ts` 钉着输出)

`mix(a, b, t)` = 两色按 `t∈[0,1]` 线性插值(sRGB 分量)。`luminance` / `contrast` 是 WCAG 相对亮度与对比度。以 accent(6 候选之一)为输入:

| 函数 | 规则 |
|---|---|
| `readable(c, bg='#FFF')` | 在 bg 上把 c 逐步(每步 0.02)混黑,直到对比度 ≥ 4.5:1,色相不变。强调色当文字用的可读版 |
| `solid(c)` | 亮度 > 0.5 的亮色原样用(配深字);其余走 `readable(c)` 压暗到白字 ≥ 4.5:1。**浅色 / 深色同值**,切外观模式强调色不变 |
| **`derivePrimary(accent, dark, darkContainer)`** | `primary = solid(accent)`(统一小写);`hover` = 往白提 10%(亮色底改往黑压 8%);`pressed` = 往黑压 12%;`light` = 浅色 `mix(primary,#FFF,.90)` / 暗色 `mix(primary, darkContainer, .82)`(实色版,裸 CSS 的选中底改用半透明 `--sel-bg`);`on` = 深底 `#ffffff`、亮底 `#0a0a0b` |
| `deriveAccentTokens` 的派生值 | `activeFg` / `linkFg`:浅色 `readable(signal)`、暗色 `mix(signal,#FFF,.35)`;聚焦环 `0 0 0 3px rgba(signal, 暗.35 / 亮.22)`;`--sel-bg` 透明度 亮.06 / 暗.14,`--sel-ring` 亮.5 / 暗.6 |
| `fieldOf(c)` / `shift(c, dh, dl, ds)` | 色场四团光 = 在 HSL 上做邻近色位移(`FIELD` 四组 `[Δ色相, Δ明度, Δ饱和]`),`--ambient-*` 与 `--desk-*` 同源、各自乘不同不透明度。近中性强调色(饱和度 < 12%,石墨)经 `fieldSrc` 退回默认蓝 |
| `btnGrad` / `glowSh` | 英雄专用(仅登录页 / 欢迎横幅 / 头像,自定义 CSS,不入 Naive 覆写):`linear-gradient(135deg, accent 0%, mix(accent,#8B5CF6,.55) 55%, mix(accent,#EC4899,.62) 100%)` / `0 6px 20px rgba(accent,.42)` |

**要点**:暗色下不对 accent 另做提亮,亮 / 暗同值;语义浅底统一走 CSS 的 `color-mix`(`--ok-bg` 等),不逐项用 `mix` 计算。**派生只在 `mix.ts` 一处**:`naive-theme.ts`(喂 Naive overrides)与 `accentTokens.ts`(写 CSS 变量)都调它,两边各算一份就会让裸 CSS 与 Naive 组件的主色不同步而没有任何东西报错。

> Naive `common.borderRadius` ← `--radius-md`(9)覆盖多数控件;卡片圆角(lg = 14)、`DataTable` 的表头 / 斑马纹 / hover 底色、`Button` 的默认款 / secondary 款在 `naive-theme.ts` 里按组件覆写(`Card` / `DataTable` / `Button`)。
