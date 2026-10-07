import { describe, expect, it } from 'vitest'
import { ACCENTS, ACCENT_META } from './accents'
import { deriveAccentTokens } from './accentTokens'
import {
  chartPalette,
  contrast,
  derivePrimary,
  fieldOf,
  hexToHsl,
  luminance,
  mix,
  solid,
} from './mix'
import { buildThemeOverrides } from './naive-theme'

/**
 * 期望值一律取自**被测链路之外**(输入本身、分量大小方向、混合端点、已定稿的色值),
 * 不拿 `mix()` 再算一遍当期望——那是回声,恒真。所以这里不钉每一档的魔数,只钉「派生规则」本身:
 * 哪一档该往白走、哪一档该往黑走、实心色必须压到白字可读。规则被改坏时这些会红,而重新调档位(魔数微调)不会误红。
 * 已定稿的色值(solid() / fieldOf() 对候选强调色的输出)属于外部约定,可以钉。
 */

/** 取 sRGB 分量,用于判方向(变亮 = 分量整体上升)。 */
const rgb = (hex: string): [number, number, number] => {
  const n = Number.parseInt(hex.replace('#', ''), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}
const sum = (hex: string) => rgb(hex).reduce((a, b) => a + b, 0)
const dist = (a: string, b: string) => rgb(a).reduce((s, v, i) => s + Math.abs(v - rgb(b)[i]!), 0)

const alpha = (v: string) => Number(/,\s*([\d.]+)\)$/.exec(v)![1])

const INDIGO = '#5856D6' // macOS 靛蓝:亮度 0.136,solid() 原样保留

describe('derivePrimary', () => {
  it('solid() 不需要压暗的强调色原样保留,只做小写归一', () => {
    // 期望值来自输入:靛蓝对白字已经 ≥ 4.5,不该被动。同时钉「大小写不随强调色漂移」。
    expect(derivePrimary(INDIGO, false).primary).toBe(INDIGO.toLowerCase())
  })

  it('浅色 / 深色同一个值,深色不先把 accent 提亮', () => {
    for (const accent of ACCENTS) {
      expect(derivePrimary(accent, true).primary).toBe(derivePrimary(accent, false).primary)
    }
  })

  it('六个候选色的实心色一律压到白字 ≥ 4.5:1,主按钮一律深底白字', () => {
    for (const accent of ACCENTS) {
      const p = derivePrimary(accent, false)
      expect(contrast(p.primary, '#FFFFFF')).toBeGreaterThanOrEqual(4.5)
      expect(p.on).toBe('#ffffff')
    }
  })

  it('压暗只改明度不改色相:solid() 之后色相与原色相差不超过 3 度', () => {
    for (const accent of ACCENTS.filter(a => a !== '#8E8E93')) {
      const dh = Math.abs(hexToHsl(accent)[0] - hexToHsl(derivePrimary(accent, false).primary)[0])
      expect(Math.min(dh, 360 - dh)).toBeLessThan(3)
    }
  })

  it('文档登记的 solid() 输出', () => {
    // 候选强调色经 solid() 压暗后的定稿色值;改了派生规则就要有意识地同步调整这张表。
    const expected: Record<string, string> = {
      '#0A84FF': '#0974e0',
      '#5856D6': '#5856d6', // 原样保留
      '#AF52DE': '#a54dd1',
      '#30B0C7': '#237f8f',
      '#30D158': '#1f8638',
      '#8E8E93': '#747479',
    }
    for (const [accent, want] of Object.entries(expected)) {
      expect(derivePrimary(accent, false).primary).toBe(want)
    }
  })

  it('亮度 > 0.5 的亮色保留原色并配深字(候选里一个都没有,留给自定义强调色)', () => {
    const yellow = '#FFD60A'
    const p = derivePrimary(yellow, false)
    expect(p.primary).toBe(yellow.toLowerCase())
    expect(p.on).toBe('#0a0a0b')
    expect(sum(p.hover)).toBeLessThan(sum(p.primary)) // 亮底上悬停往黑压,而不是往白提
  })

  it('hover 往白走、pressed 往黑走(明暗两档都成立)', () => {
    for (const dark of [false, true]) {
      const p = derivePrimary(INDIGO, dark)
      expect(sum(p.hover)).toBeGreaterThan(sum(p.primary))
      expect(sum(p.pressed)).toBeLessThan(sum(p.primary))
    }
  })

  it('light 亮色下往白靠、暗色下往容器色靠(而非往白靠)', () => {
    expect(sum(derivePrimary(INDIGO, false).light)).toBeGreaterThan(sum(INDIGO))

    // 暗色浅底若还往白走,会在暗底上白得刺眼——钉住它靠向传入的容器色。
    const container = '#1F2229'
    const light = derivePrimary(INDIGO, true, container).light
    const primary = derivePrimary(INDIGO, true).primary
    expect(sum(light)).toBeLessThan(sum(primary))
    expect(sum(light)).toBeGreaterThan(sum(container))
  })

  it('容器色缺省时用内置暗底,不炸', () => {
    expect(derivePrimary(INDIGO, true, undefined).light).toBe(
      derivePrimary(INDIGO, true, '#1F2229').light,
    )
  })
})

describe('solid / luminance', () => {
  it('候选色的原值亮度都 ≤ 0.5,走 solid() 的「压暗」这一支(主按钮深底白字,字色不翻转)', () => {
    // 亮度:蓝 0.238 / 靛蓝 0.136 / 紫 0.204 / 深青 0.358 / 墨绿 0.469 / 石墨 0.272。
    // 墨绿 0.469 与靛蓝 0.136 略出 0.14–0.36 的取值区间,但都没越过 0.5 的分支阈值,不影响结论。
    for (const accent of ACCENTS) expect(luminance(accent)).toBeLessThanOrEqual(0.5)
  })

  it('亮度 > 0.5 的色直接用', () => {
    expect(solid('#FFD60A')).toBe('#FFD60A')
  })
})

describe('fieldOf(色场四团光)', () => {
  it('默认蓝的四团与文档里写明的值一致', () => {
    expect(fieldOf('#0A84FF').map(c => c.toLowerCase())).toEqual([
      '#0a84ff',
      '#4c71fa',
      '#17aff7',
      '#6f62f8',
    ])
  })

  it('强调色本身是紫色时四团仍然层次分明(混向写死的紫会塌成三团同色)', () => {
    const f = fieldOf('#AF52DE')
    for (let i = 0; i < f.length; i++)
      for (let j = i + 1; j < f.length; j++) expect(dist(f[i]!, f[j]!)).toBeGreaterThan(30)
  })

  it('近中性的强调色(石墨)色场退回默认蓝', () => {
    expect(fieldOf('#8E8E93')).toEqual(fieldOf('#0A84FF'))
  })
})

describe('deriveAccentTokens', () => {
  it('实心色明暗同值;选中态淡底暗色比亮色更浓', () => {
    for (const accent of ACCENTS) {
      const light = deriveAccentTokens(accent, false)
      const dark = deriveAccentTokens(accent, true)
      expect(dark.css['--acc-solid']).toBe(light.css['--acc-solid'])
      expect(alpha(dark.css['--sel-bg']!)).toBeGreaterThan(alpha(light.css['--sel-bg']!))
    }
  })

  it('石墨:色场退回蓝,但登录页第二色仍走石墨本色(否则会冒出蓝色登录按钮)', () => {
    const t = deriveAccentTokens('#8E8E93', false)
    expect(t.css['--desk-1']).toBe(deriveAccentTokens('#0A84FF', false).css['--desk-1'])
    expect(hexToHsl(t.css['--login-accent-2']!)[1]).toBeLessThan(0.3) // 近中性,不是蓝
  })

  it('浅色下选中文字取可读版:对白底 ≥ 4.5:1;深色往白提亮', () => {
    for (const accent of ACCENTS) {
      expect(
        contrast(deriveAccentTokens(accent, false).activeFg, '#FFFFFF'),
      ).toBeGreaterThanOrEqual(4.5)
      expect(sum(deriveAccentTokens(accent, true).activeFg)).toBeGreaterThan(sum(accent))
    }
  })
})

describe('ACCENT_META', () => {
  it('色块预览显示 solid() 处理后的颜色,不是原始色相', () => {
    expect(ACCENT_META.map(m => m.color)).toEqual([...ACCENTS])
    for (const m of ACCENT_META)
      expect(m.dot.toLowerCase()).toBe(derivePrimary(m.color, false).primary)
  })
})

/**
 * 这一条钉的**不是色值**(那会是回声),而是**接线**:喂 Naive 的那份主色必须来自 `derivePrimary`,
 * 而不是某处又抄了一份魔数。要防的正是「两份实现,改一处忘另一处,
 * 裸 CSS 与 Naive 组件主色不同步,而没有任何东西会报错」——有了这条,再抄一份就会报错。
 * 写 CSS 变量的另一半(`useTheme.applyAccentVars`)与 Naive 共用 `deriveAccentTokens`。
 */
describe('buildThemeOverrides 与裸 CSS 共用同一份主色派生', () => {
  it.each([
    ['light', false],
    ['dark', true],
  ])('%s 模式下 Naive 主色三态取自 derivePrimary,裸 CSS 的 --acc-solid 与之同源', (_name, dark) => {
    const accent = '#AF52DE'
    const p = derivePrimary(accent, dark)
    const common = buildThemeOverrides({ dark, accent }).common!
    expect(common.primaryColor).toBe(p.primary)
    expect(common.primaryColorHover).toBe(p.hover)
    expect(common.primaryColorPressed).toBe(p.pressed)
    expect(deriveAccentTokens(accent, dark).css['--acc-solid']).toBe(p.primary)
  })
})

describe('mix', () => {
  it('t=0 取 a、t=1 取 b(端点不插值)', () => {
    // 端点期望值是两个入参本身,与插值实现无关。
    expect(mix('#FF0000', '#00FF00', 0)).toBe('#ff0000')
    expect(mix('#FF0000', '#00FF00', 1)).toBe('#00ff00')
  })

  it('三位简写与六位等价', () => {
    expect(mix('#F00', '#FFFFFF', 0.5)).toBe(mix('#FF0000', '#FFFFFF', 0.5))
  })
})

describe('chartPalette:图表数据色', () => {
  const CARD = { light: '#FFFFFF', dark: '#1b2842' }

  it('每个强调色、明暗两档:6 个数据色互不相同,且对卡片底对比度 ≥ 3:1(图形对象的 WCAG 下限)', () => {
    for (const accent of ACCENTS) {
      for (const dk of [false, true]) {
        const colors = chartPalette(accent, dk)
        expect(new Set(colors).size, `${accent} dark=${dk}`).toBe(6)
        for (const c of colors) {
          expect(
            contrast(c, dk ? CARD.dark : CARD.light),
            `${accent} dark=${dk} ${c}`,
          ).toBeGreaterThanOrEqual(3)
        }
      }
    }
  })

  it('第 1 色是强调色本色的色相(折线第一条线跟强调色走)', () => {
    const [h] = hexToHsl(chartPalette('#0A84FF', false)[0]!)
    const [h0] = hexToHsl('#0A84FF')
    expect(Math.abs(h - h0)).toBeLessThan(3)
  })

  it('不出现红 / 橙(色相 345°–45°):那是状态语义色,画数据会暗示出错 / 警告', () => {
    for (const accent of ACCENTS) {
      for (const dk of [false, true]) {
        for (const c of chartPalette(accent, dk)) {
          const [h, s] = hexToHsl(c)
          const inRedOrange = (h >= 345 || h <= 45) && s > 0.15
          expect(inRedOrange, `${accent} dark=${dk} ${c} h=${Math.round(h)}`).toBe(false)
        }
      }
    }
  })

  it('石墨(近中性)强调色退回蓝的色场取色:数据色仍然是彩色,不是一排灰', () => {
    const colors = chartPalette('#8E8E93', false)
    expect(colors.every(c => hexToHsl(c)[1] > 0.3)).toBe(true)
  })
})
