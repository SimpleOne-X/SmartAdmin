// 颜色派生规则(DESIGN.md 所述规则的唯一实现)。纯函数,无框架依赖。
// 改规则时同步改 DESIGN.md,mix.spec.ts 钉着 solid() / fieldOf() 的几组既定输出。

function clampByte(n: number): number {
  return Math.max(0, Math.min(255, Math.round(n)))
}

function parseHex(hex: string): [number, number, number] {
  let h = hex.replace('#', '').trim()
  if (h.length === 3)
    h = h
      .split('')
      .map(c => c + c)
      .join('')
  const n = Number.parseInt(h, 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

function toHex(r: number, g: number, b: number): string {
  return '#' + [r, g, b].map(v => clampByte(v).toString(16).padStart(2, '0')).join('')
}

/** 在 a、b 之间按 t∈[0,1] 线性插值(sRGB 分量)。t=0→a,t=1→b。 */
export function mix(a: string, b: string, t: number): string {
  const [ar, ag, ab] = parseHex(a)
  const [br, bg, bb] = parseHex(b)
  return toHex(ar + (br - ar) * t, ag + (bg - ag) * t, ab + (bb - ab) * t)
}

/** WCAG 相对亮度(0 黑 … 1 白)。 */
export function luminance(hex: string): number {
  const [r, g, b] = parseHex(hex).map(v => {
    const c = v / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

/** WCAG 对比度(1…21)。 */
export function contrast(a: string, b: string): number {
  const x = luminance(a)
  const y = luminance(b)
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05)
}

/**
 * 把强调色当文字用时的可读版:在 bg 上逐步混黑,直到对比度 ≥ 4.5:1,色相不变。
 * 浅色下的链接 / 选中文字 / 侧栏图标用它;深色背景上不要用(混黑只会更糟),深色走「往白提亮」。
 */
export function readable(c: string, bg = '#FFFFFF'): string {
  let t = 0
  let r = c
  while (contrast(r, bg) < 4.5 && t < 1) {
    t += 0.02
    r = mix(c, '#000000', t)
  }
  return r
}

/**
 * 实心控件(主按钮、勾选框、开关、进度条)的底色:亮度 > 0.5 的亮色直接用(配深字),
 * 其余压暗到白字 ≥ 4.5:1。**浅色 / 深色用同一个值**,切换外观模式时强调色不变。
 * 6 个候选色的亮度都落在 0.14–0.36,所以实际都走「压暗」这一支、主按钮一律深底白字。
 */
export function solid(c: string): string {
  return luminance(c) > 0.5 ? c : readable(c)
}

export interface PrimaryRamp {
  /** 实心主色(= `solid(accent)`,明暗同值)。 */
  primary: string
  hover: string
  pressed: string
  /** 浅底(选中背景 / 标签底)的**实色**版;裸 CSS 里的选中底改用半透明的 `--sel-bg`,这里保留给调用方自取。 */
  light: string
  /** 压在 `primary` 上的字色:深底白字,极端情况(亮度 > 0.5)配近黑。 */
  on: string
}

/**
 * accent → 主色四态 + 字色。规则:
 *   primary = solid(accent);hover 往白提 10%(亮色底改往黑压 8%);pressed 往黑压 12%。
 * 浅色 / 深色同一个值,深色下不对 accent 另做提亮。
 *
 * 派生只在这一处:`naive-theme.ts`(喂 Naive overrides)与 `accentTokens.ts`(写 CSS 变量)都调它。
 * 两边各算一份的话,改一处忘另一处 = 裸 CSS 与 Naive 组件主色不同步,**而没有任何东西会报错**。
 *
 * `darkContainer` 只在暗色下参与 `light`(浅底要往容器色靠,否则在暗底上白得刺眼);亮色下忽略。
 */
export function derivePrimary(
  accent: string,
  dark: boolean,
  darkContainer = '#1F2229',
): PrimaryRamp {
  // 统一转小写:solid() 在亮色分支里原样返回入参,而色板里的 accent 是大写字面量,
  // 其余三态都经 `toHex` 出小写。不归一的话输出的大小写会随强调色变化,
  // 任何拿 `===` 直接比的地方就会得到一个只在某些强调色下失败的不一致。CSS 不区分大小写,归一无副作用。
  const primary = solid(accent).toLowerCase()
  const bright = luminance(primary) > 0.5
  return {
    primary,
    hover: bright ? mix(primary, '#000000', 0.08) : mix(primary, '#FFFFFF', 0.1),
    pressed: mix(primary, '#000000', 0.12),
    light: dark ? mix(primary, darkContainer, 0.82) : mix(primary, '#FFFFFF', 0.9),
    on: bright ? '#0a0a0b' : '#ffffff',
  }
}

/** accent → rgba(...,alpha),用于发光 / 渐变 / 选中淡底。 */
export function rgba(hex: string, alpha: number): string {
  const [r, g, b] = parseHex(hex)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

/** 英雄主按钮渐变(仅登录页 / 欢迎横幅 / 头像)。 */
export function btnGrad(accent: string): string {
  return `linear-gradient(135deg, ${accent} 0%, ${mix(accent, '#8B5CF6', 0.55)} 55%, ${mix(accent, '#EC4899', 0.62)} 100%)`
}

/** 英雄按钮发光阴影。 */
export function glowSh(accent: string): string {
  return `0 6px 20px ${rgba(accent, 0.42)}`
}

/* ─── HSL 与色场:色场推导要按色相走,混向一个写死的颜色做不到 ─── */

/** hex → [色相 0–360, 饱和度 0–1, 明度 0–1]。 */
export function hexToHsl(hex: string): [number, number, number] {
  const [r, g, b] = parseHex(hex).map(v => v / 255) as [number, number, number]
  const mx = Math.max(r, g, b)
  const mn = Math.min(r, g, b)
  const d = mx - mn
  const l = (mx + mn) / 2
  let h = 0
  let s = 0
  if (d) {
    s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn)
    h =
      (mx === r ? (g - b) / d + (g < b ? 6 : 0) : mx === g ? (b - r) / d + 2 : (r - g) / d + 4) * 60
  }
  return [h, s, l]
}

export function hslToHex(h: number, s: number, l: number): string {
  const hh = ((h % 360) + 360) % 360
  const ss = Math.max(0, Math.min(1, s))
  const ll = Math.max(0, Math.min(1, l))
  const c = (1 - Math.abs(2 * ll - 1)) * ss
  const x = c * (1 - Math.abs(((hh / 60) % 2) - 1))
  const m = ll - c / 2
  const t =
    hh < 60
      ? [c, x, 0]
      : hh < 120
        ? [x, c, 0]
        : hh < 180
          ? [0, c, x]
          : hh < 240
            ? [0, x, c]
            : hh < 300
              ? [x, 0, c]
              : [c, 0, x]
  return toHex((t[0]! + m) * 255, (t[1]! + m) * 255, (t[2]! + m) * 255)
}

/** 邻近色位移:色相窄幅旋转 + 提亮 + 轻微降饱和。色场四团光和应用内环境光都用它从强调色推导。 */
export function shift(c: string, dh: number, dl: number, ds: number): string {
  const [h, s, l] = hexToHsl(c)
  return hslToHex(h + dh, s + ds, l + dl)
}

/**
 * 色场四团光相对强调色的位移量 [Δ色相, Δ明度, Δ饱和]。这四组数按默认蓝下的目标色反解得到,
 * 所以默认蓝的观感是定稿的(与目标色逐团差 1–2 个 RGB 单位),换成别的强调色才会有区别。
 * 按色相位移、而不是 mix 向写死的颜色(如 #8B5CF6 / #22D3EE),是为了紫色等强调色不会塌成一片同色(混向固定色时四团里三团同色)。
 */
export const FIELD: ReadonlyArray<readonly [number, number, number]> = [
  [0, 0, 0],
  [17, 0.12, -0.05],
  [-11, 0.01, -0.07],
  [35, 0.16, -0.08],
]

/** 色场的默认取色源(默认强调色蓝)。 */
const FIELD_FALLBACK = '#0A84FF'

/**
 * 色场的取色源:近中性的强调色(石墨,饱和度 2%)旋转色相没有意义,转出来还是灰,整屏会变成一块死灰,
 * 退回默认蓝——这也正是 macOS 的行为:石墨只把控件去色,桌面壁纸照旧有颜色。
 * 注意只有色场退回,`--login-accent-2`(登录页主按钮渐变、标语高亮)仍走石墨本色,不然会冒出一个蓝按钮。
 */
export function fieldSrc(c: string): string {
  return hexToHsl(c)[1] < 0.12 ? FIELD_FALLBACK : c
}

/** accent → 色场四个色(登录页 `--desk-1..4` 与应用内 `--ambient-1..4` 同源,各自再乘自己的不透明度)。 */
export function fieldOf(c: string): string[] {
  const src = fieldSrc(c)
  return FIELD.map(([dh, dl, ds]) => shift(src, dh, dl, ds))
}

/* ─── 图表数据色:由强调色推导的一组邻近色相 ─── */

/**
 * 数据色相对强调色的 [色相偏移(度), 饱和度偏移]。顺序 = 系列顺序:第 1 个就是强调色本色,
 * 第 2、3 个往两侧拉开最大(折线只有两个系列时要一眼分得开),后面依次外扩。
 * 偏移控制在 ±115° 内,落在蓝 → 青 / 靛 → 紫 / 绿这一带,不会出现红 / 橙:
 * 红橙是状态语义色(危险 / 警告),拿来画「权限点」「在线会话」会让人以为出了问题。
 * 实心主色饱和度很高(默认蓝约 0.9),原样转色相会得到荧光品红 / 荧光绿,和柔和的玻璃界面打架,
 * 所以偏得越远降得越多。
 */
export const CHART_STEPS: ReadonlyArray<readonly [number, number]> = [
  [0, 0],
  [48, -0.16],
  [-38, -0.14],
  [96, -0.46],
  [-76, -0.3],
  [-112, -0.36],
]

/** 色相落在红 / 橙一带(345°–45°):状态语义色的地盘,数据色要避开。 */
function inWarmZone(h: number): boolean {
  const x = ((h % 360) + 360) % 360
  return x >= 345 || x <= 45
}

/** 图形对象(折线、色块)与相邻底色的对比度下限,WCAG 1.4.11。 */
const CHART_MIN_CONTRAST = 3

/** 把 c 的明度往「离底色更远」的方向推,直到对底色对比度 ≥ min(最多推 24 步,够用且保证终止)。 */
function ensureContrast(c: string, bg: string, min: number, lighten: boolean): string {
  let out = c
  for (let i = 0; i < 24 && contrast(out, bg) < min; i++)
    out = shift(out, 0, lighten ? 0.02 : -0.02, 0)
  return out
}

/**
 * accent → 6 个图表数据色。和色场同源:石墨(近中性)的强调色转色相没有意义,退回默认蓝(见 fieldSrc)。
 * 基准色取实心主色(明暗同值),再逐个压到对卡片底 ≥ 3:1;`darkCard` 是暗色卡片底色(`--bg-card`)。
 */
export function chartPalette(accent: string, dark: boolean, darkCard = '#1b2842'): string[] {
  const base = derivePrimary(fieldSrc(accent), dark).primary
  const bg = dark ? darkCard : '#FFFFFF'
  const h0 = hexToHsl(base)[0]
  return CHART_STEPS.map(([dh, ds]) => {
    // 固定偏移在个别强调色下会转进红橙区(墨绿 −112° → 橙棕,靛蓝 +96° → 粉红):落进去就反向取
    const d = inWarmZone(h0 + dh) ? -dh : dh
    return ensureContrast(shift(base, d, 0, ds), bg, CHART_MIN_CONTRAST, dark)
  })
}
