import { solid } from './mix'

/**
 * 6 个强调色候选,全部取自 macOS 系统色:
 * 蓝 #0A84FF(默认)、靛蓝 #5856D6、紫 #AF52DE、深青 #30B0C7、墨绿 #30D158、石墨 #8E8E93。
 *
 * **取值标准是原值亮度落在 0.14–0.36**,不是看色相好不好看:这一段 `solid()` 之后色相不跑偏,
 * 而且六个色的主按钮一律深底白字。亮度 > 0.5 的色(薄荷 #63E6E2、亮青 #64D2FF、黄 #FFD60A)
 * 会走 `solid()`「保留原色配深字」的分支,换个强调色主按钮的字色就从白翻成黑,所以一个都不收。
 * 加新候选色前先算 `solid()` 的结果,别只看原始色相。
 *
 * 浅色 / 深色共用同一个值。品牌 Logo 的固定靛蓝不在这里(Logo 不随用户换色)。
 */
export const ACCENTS = ['#0A84FF', '#5856D6', '#AF52DE', '#30B0C7', '#30D158', '#8E8E93'] as const

export type Accent = (typeof ACCENTS)[number]

export interface AccentMeta {
  color: Accent
  /** 语言包键(settings.accentName.<key>);深青 / 墨绿压暗幅度大,色块里看到的不是 macOS 系统设置里那两个亮色,所以不叫青色 / 绿色。 */
  key: 'blue' | 'indigo' | 'purple' | 'teal' | 'green' | 'graphite'
  /** 色块预览用的颜色 = `solid(color)`:和开关 / 主按钮等实心控件同一套压暗规则,预览才和实际效果对得上。 */
  dot: string
}

/** 外观面板的色块清单(顺序同 ACCENTS)。`dot` 在定义时预计算。 */
export const ACCENT_META: readonly AccentMeta[] = (
  [
    ['#0A84FF', 'blue'],
    ['#5856D6', 'indigo'],
    ['#AF52DE', 'purple'],
    ['#30B0C7', 'teal'],
    ['#30D158', 'green'],
    ['#8E8E93', 'graphite'],
  ] as const
).map(([color, key]) => ({ color, key, dot: solid(color) }))
