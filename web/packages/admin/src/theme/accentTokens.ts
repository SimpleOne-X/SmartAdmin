import { chartPalette, derivePrimary, fieldOf, mix, readable, rgba, shift, FIELD } from './mix'

/**
 * 强调色 → 一整套随强调色变化的令牌。
 *
 * 两个消费者共用这一份结果,不各算一遍:
 *   - `useTheme.applyAccentVars` 把 `css` 逐项写到 `<html>` 的 style 上(裸 CSS 与页面 scoped 里的 var(--sel-bg) 等换色);
 *   - `naive-theme.ts` 取 `signal` / `acc` / `onAcc` / `hover` / `pressed` / `linkFg` / `activeFg` / `ring` 喂 Naive overrides。
 * 两边各算一份的话,改一处忘另一处就是裸 CSS 与 Naive 组件的强调色不同步,而没有任何东西会报错。
 */
export interface AccentDerived {
  /** 原始强调色(信号色 = 强调色:图标、聚焦环、数据线、菜单选中底)。 */
  signal: string
  /** 实心控件底色 = `solid(signal)`,明暗同值。 */
  acc: string
  /** 压在 `acc` 上的字色。 */
  onAcc: string
  hover: string
  pressed: string
  /** 强调色当「选中文字 / 侧栏图标」用的可读版:浅色压暗到对白底 ≥ 4.5:1,深色往白提亮 35%。 */
  activeFg: string
  /** 文字按钮 / 行内操作按钮 / 链接的字色。 */
  linkFg: string
  /** 聚焦环 `0 0 0 3px rgba(signal, α)`。 */
  ring: string
  /** 要写到 `<html>` 的 CSS 变量(名 → 值)。 */
  css: Record<string, string>
}

/** 应用内环境光四团的不透明度(深色底更吃得住颜色,但噪点和玻璃叠上去会再提一档,所以比浅色略收)。 */
const AMBIENT_OP = {
  light: [0.2, 0.15, 0.13, 0.14],
  dark: [0.17, 0.13, 0.11, 0.12],
} as const

/** 登录页色场四团的不透明度:比应用内浓得多,还会漂。 */
const DESK_OP = {
  light: [0.55, 0.45, 0.5, 0.34],
  dark: [0.85, 0.7, 0.6, 0.55],
} as const

export function deriveAccentTokens(accent: string, dark: boolean): AccentDerived {
  const signal = accent
  const p = derivePrimary(accent, dark)
  const acc = p.primary

  // 深色:往白提亮 35%;浅色:压暗到可读。activeFg(侧栏 / 选中)与 linkFg(文字按钮)当前取值相同,
  // 保留两个出口,以后要分化时不用动调用方。
  const activeFg = dark ? mix(signal, '#FFFFFF', 0.35) : readable(signal)
  const linkFg = dark ? mix(signal, '#FFFFFF', 0.35) : readable(signal)
  const ring = `0 0 0 3px ${rgba(signal, dark ? 0.35 : 0.22)}`

  const field = fieldOf(signal)
  const ambientOp = dark ? AMBIENT_OP.dark : AMBIENT_OP.light
  const deskOp = dark ? DESK_OP.dark : DESK_OP.light

  const css: Record<string, string> = {
    '--accent': signal,
    '--signal': signal,
    '--signal-glow': rgba(signal, dark ? 0.55 : 0.35),
    '--acc-solid': acc,
    '--on-acc': p.on,
    '--acc-hover': p.hover,
    '--acc-pressed': p.pressed,
    // 选中态令牌:模块卡片 / 表格选中行等「当前项」统一用,颜色跟信号色走
    '--sel-bg': rgba(signal, dark ? 0.14 : 0.06),
    '--sel-ring': rgba(signal, dark ? 0.6 : 0.5),
    '--sel-halo': rgba(signal, dark ? 0.16 : 0.1),
    '--sel-fg': activeFg,
    // 页签 / 列表当前项
    '--tab-active-bg': rgba(signal, dark ? 0.18 : 0.08),
    '--tab-active-fg': activeFg,
    '--tab-active-ring': rgba(signal, dark ? 0.35 : 0.22),
    // 顶部页签激活态。深色三项是固定的蓝(rgba(59,155,255,.2) / #FFF / rgba(130,190,255,.65)),不随强调色走;
    // 浅色三项随强调色。
    '--tab-on-bg': dark ? 'rgba(59, 155, 255, 0.2)' : rgba(signal, 0.1),
    '--tab-on-fg': dark ? '#ffffff' : readable(signal),
    '--tab-on-ring': dark ? 'rgba(130, 190, 255, 0.65)' : rgba(signal, 0.5),
    // 登录页主按钮渐变 / 标语高亮的第二色:走强调色本色位移,石墨时不退回蓝(见 fieldSrc)
    '--login-accent-2': shift(signal, FIELD[1][0], FIELD[1][1], FIELD[1][2]),
  }
  field.forEach((c, i) => {
    css[`--ambient-${i + 1}`] = rgba(c, ambientOp[i]!)
    css[`--desk-${i + 1}`] = rgba(c, deskOp[i]!)
  })
  // 图表数据色(折线 / 柱 / 饼的系列色,工作台统计卡的图标色也用它):强调色的邻近色相,不复用状态语义色
  chartPalette(signal, dark).forEach((c, i) => {
    css[`--chart-${i + 1}`] = c
  })

  return {
    signal,
    acc,
    onAcc: p.on,
    hover: p.hover,
    pressed: p.pressed,
    activeFg,
    linkFg,
    ring,
    css,
  }
}
