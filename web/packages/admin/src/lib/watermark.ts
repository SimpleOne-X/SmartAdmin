// 水印:配置(sys.watermark.*)→ 设置对象 → NWatermark 的入参。
// 管理员只决定「印哪几项、长什么样」,每个人具体印什么由前端按登录用户拼,所以同一份设置在每台浏览器上内容不同。
// 取值规则与后端 ConfigService.LoadWatermarkAsync 一致:任何一项缺失、写错或越界都收口成默认值 / 边界,
// 这样配置中心的草稿预览(读键值表)和线上真水印(读站点信息)走同一套收口。

export const WATERMARK_FIELDS = ['name', 'account', 'org', 'phone', 'time', 'text'] as const
export type WatermarkField = (typeof WATERMARK_FIELDS)[number]

export const WATERMARK_TIME_FORMATS = [
  'YYYY-MM-DD',
  'YYYY-MM-DD HH:mm',
  'YYYY-MM-DD HH:mm:ss',
  'MM-DD HH:mm',
] as const
export type WatermarkTimeFormat = (typeof WATERMARK_TIME_FORMATS)[number]

export type WatermarkLayout = 'single' | 'multi'
export type WatermarkDensity = 'sparse' | 'normal' | 'dense'

export interface WatermarkSettings {
  enabled: boolean
  /** 已按 WATERMARK_FIELDS 的固定顺序排好 */
  fields: WatermarkField[]
  /** 内容项含 text 时才印 */
  text: string
  timeFormat: WatermarkTimeFormat
  layout: WatermarkLayout
  /** px,12–28 */
  fontSize: number
  /** 不透明度百分比,2–30 */
  opacity: number
  /** 度,-45–45 */
  rotate: number
  density: WatermarkDensity
  /** 相邻两行错开半格 */
  cross: boolean
}

export const WATERMARK_LIMITS = {
  fontSize: { min: 12, max: 28 },
  opacity: { min: 2, max: 30 },
  rotate: { min: -45, max: 45 },
  text: 40,
} as const

export const DEFAULT_WATERMARK: Readonly<WatermarkSettings> = Object.freeze({
  enabled: false,
  fields: ['name', 'account', 'time'] as WatermarkField[],
  text: '',
  timeFormat: 'YYYY-MM-DD HH:mm',
  layout: 'single',
  fontSize: 14,
  opacity: 8,
  rotate: -20,
  density: 'normal',
  cross: true,
})

/** 配置键(与后端 ConfigSeed 的 WATERMARK_*_KEY 一一对应)。 */
export const WATERMARK_KEY = {
  enabled: 'sys.watermark.enabled',
  fields: 'sys.watermark.fields',
  text: 'sys.watermark.text',
  timeFormat: 'sys.watermark.timeFormat',
  layout: 'sys.watermark.layout',
  fontSize: 'sys.watermark.fontSize',
  opacity: 'sys.watermark.opacity',
  rotate: 'sys.watermark.rotate',
  density: 'sys.watermark.density',
  cross: 'sys.watermark.cross',
} as const
export const WATERMARK_KEYS: readonly string[] = Object.values(WATERMARK_KEY)

function clampInt(raw: unknown, fallback: number, min: number, max: number) {
  const n = typeof raw === 'number' ? raw : Number(raw)
  if (raw === '' || raw === null || raw === undefined || !Number.isFinite(n)) return fallback
  return Math.min(max, Math.max(min, Math.trunc(n)))
}

const oneOf = <T extends string>(v: unknown, all: readonly T[], fallback: T): T =>
  all.includes(v as T) ? (v as T) : fallback

/** 内容项:接受数组或逗号分隔串;丢掉未知项、去重、按固定顺序排。 */
export function normalizeWatermarkFields(raw: unknown): WatermarkField[] {
  const list = Array.isArray(raw)
    ? raw.map(String)
    : typeof raw === 'string'
      ? raw.split(',').map(s => s.trim())
      : null
  if (!list) return [...DEFAULT_WATERMARK.fields]
  return WATERMARK_FIELDS.filter(f => list.includes(f))
}

/** 站点信息里的 watermark 节点(或任何半成品)→ 合法设置。 */
export function normalizeWatermark(raw?: Partial<Record<keyof WatermarkSettings, unknown>> | null) {
  const d = DEFAULT_WATERMARK
  const r = raw ?? {}
  const text = typeof r.text === 'string' ? r.text.trim() : ''
  return {
    enabled: r.enabled === true,
    fields: r.fields === undefined ? [...d.fields] : normalizeWatermarkFields(r.fields),
    text: text.slice(0, WATERMARK_LIMITS.text),
    timeFormat: oneOf(r.timeFormat, WATERMARK_TIME_FORMATS, d.timeFormat),
    layout: oneOf(r.layout, ['single', 'multi'] as const, d.layout),
    fontSize: clampInt(
      r.fontSize,
      d.fontSize,
      WATERMARK_LIMITS.fontSize.min,
      WATERMARK_LIMITS.fontSize.max,
    ),
    opacity: clampInt(
      r.opacity,
      d.opacity,
      WATERMARK_LIMITS.opacity.min,
      WATERMARK_LIMITS.opacity.max,
    ),
    rotate: clampInt(r.rotate, d.rotate, WATERMARK_LIMITS.rotate.min, WATERMARK_LIMITS.rotate.max),
    density: oneOf(r.density, ['sparse', 'normal', 'dense'] as const, d.density),
    cross: r.cross !== false,
  } satisfies WatermarkSettings
}

/** 配置中心草稿(键值全是字符串)→ 合法设置。键不存在 = 沿用默认;空串的 fields = 一项都没勾。 */
export function watermarkFromValues(values: Record<string, string | undefined>): WatermarkSettings {
  const v = (k: keyof typeof WATERMARK_KEY) => values[WATERMARK_KEY[k]]
  return normalizeWatermark({
    enabled: v('enabled') === 'true',
    fields: v('fields'),
    text: v('text'),
    timeFormat: v('timeFormat'),
    layout: v('layout'),
    fontSize: v('fontSize'),
    opacity: v('opacity'),
    rotate: v('rotate'),
    density: v('density'),
    cross: v('cross') !== 'false',
  })
}

/** 用户自己的那部分内容;缺哪项那一项就不印。 */
export interface WatermarkWho {
  name?: string | null
  account?: string | null
  org?: string | null
  /** 手机号后四位(整号不进浏览器存储) */
  phoneTail?: string | null
}

const pad = (n: number) => String(n).padStart(2, '0')

/** 按格式串格式化时间:只认 YYYY MM DD HH mm ss。 */
export function formatWatermarkTime(format: string, d: Date): string {
  const map: Record<string, string> = {
    YYYY: String(d.getFullYear()),
    MM: pad(d.getMonth() + 1),
    DD: pad(d.getDate()),
    HH: pad(d.getHours()),
    mm: pad(d.getMinutes()),
    ss: pad(d.getSeconds()),
  }
  return format.replace(/YYYY|MM|DD|HH|mm|ss/g, t => map[t]!)
}

/** 内容项 → 要印出来的几段文字(按固定顺序;值为空的项跳过)。 */
export function watermarkParts(s: WatermarkSettings, who: WatermarkWho, now: Date): string[] {
  const value: Record<WatermarkField, string | null | undefined> = {
    name: who.name,
    account: who.account,
    org: who.org,
    phone: who.phoneTail ? `*${who.phoneTail}` : null,
    time: formatWatermarkTime(s.timeFormat, now),
    text: s.text,
  }
  return s.fields.map(f => value[f]?.trim()).filter((x): x is string => !!x)
}

/** NWatermark 的入参子集。 */
export interface WatermarkProps {
  content: string
  cross: boolean
  fontSize: number
  lineHeight: number
  rotate: number
  width: number
  height: number
  xGap: number
  yGap: number
  xOffset: number
  yOffset: number
  fontColor: string
}

const GAP: Record<WatermarkDensity, number> = { sparse: 120, normal: 64, dense: 24 }

/**
 * 设置 + 当前用户 + 此刻 → NWatermark 入参;没启用、或没有任何可印的内容时返回 null。
 *
 * NWatermark 的画法是:一格画布 (width + xGap) × (height + yGap),**先把整个画布绕左上角旋转**,
 * 再在旋转后的坐标系里从 (xOffset, yOffset) 起画字。所以倾斜后文字会冲出画布的上沿或左沿被裁掉
 * (负角度向上冲、正角度向左冲),光加间距救不了,必须用 xOffset / yOffset 把旋转后的包围盒平移回画布内:
 * 先算文字块 w × h 旋转后的包围盒,平移量取「让包围盒左上角落在 (0,0)」,再换算回旋转前的坐标系。
 * 一格的大小 = 包围盒 + 疏密档位的间距。
 *
 * 文字块的宽按最长一行估算(汉字一个字号宽,其余 0.58,再放 6% 余量),高多留一点放下降部。
 */
export function buildWatermarkProps(
  s: WatermarkSettings,
  who: WatermarkWho,
  dark: boolean,
  now: Date = new Date(),
): WatermarkProps | null {
  if (!s.enabled) return null
  const parts = watermarkParts(s, who, now)
  if (!parts.length) return null
  const lines = s.layout === 'multi' ? parts : [parts.join(' · ')]
  const lineHeight = Math.round(s.fontSize * 1.4)
  const textWidth = (line: string) =>
    [...line].reduce((w, c) => w + (c.charCodeAt(0) > 255 ? s.fontSize : s.fontSize * 0.58), 0)
  const width = Math.ceil(Math.max(...lines.map(textWidth)) * 1.06)
  const height = lineHeight * lines.length + Math.ceil(s.fontSize * 0.3)

  const rad = (s.rotate * Math.PI) / 180
  const cos = Math.cos(rad)
  const sin = Math.sin(rad)
  const corners = [
    [0, 0],
    [width, 0],
    [0, height],
    [width, height],
  ].map(([x, y]) => [x! * cos - y! * sin, x! * sin + y! * cos] as const)
  const xs = corners.map(c => c[0])
  const ys = corners.map(c => c[1])
  const minX = Math.min(...xs)
  const minY = Math.min(...ys)
  const boxW = Math.ceil(Math.max(...xs) - minX)
  const boxH = Math.ceil(Math.max(...ys) - minY)
  // 画布坐标系里要平移 (-minX, -minY);画字用的偏移在旋转后的坐标系里,换算 R(-θ)·T
  const tx = -minX
  const ty = -minY
  const gap = GAP[s.density]
  const alpha = s.opacity / 100
  return {
    content: lines.join('\n'),
    cross: s.cross,
    fontSize: s.fontSize,
    lineHeight,
    rotate: s.rotate,
    width,
    height,
    xGap: boxW - width + gap,
    yGap: boxH - height + gap,
    xOffset: tx * cos + ty * sin + 0, // + 0:不倾斜时把 -0 收成 0
    yOffset: -tx * sin + ty * cos + 0,
    fontColor: dark ? `rgba(255,255,255,${alpha})` : `rgba(0,0,0,${alpha})`,
  }
}
