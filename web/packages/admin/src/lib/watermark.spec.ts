import { describe, expect, it } from 'vitest'
import {
  buildWatermarkProps,
  DEFAULT_WATERMARK,
  formatWatermarkTime,
  normalizeWatermark,
  normalizeWatermarkFields,
  watermarkFromValues,
  watermarkParts,
  type WatermarkSettings,
} from './watermark'

const NOW = new Date(2026, 9, 6, 14, 5, 9) // 2026-10-06 14:05:09 本地时间
const WHO = { name: 'Andy Zhong', account: 'andy', org: '研发中心', phoneTail: '8888' }
const on = (patch: Partial<WatermarkSettings> = {}): WatermarkSettings => ({
  ...DEFAULT_WATERMARK,
  fields: [...DEFAULT_WATERMARK.fields],
  enabled: true,
  ...patch,
})

describe('formatWatermarkTime', () => {
  it('四种预设格式', () => {
    expect(formatWatermarkTime('YYYY-MM-DD', NOW)).toBe('2026-10-06')
    expect(formatWatermarkTime('YYYY-MM-DD HH:mm', NOW)).toBe('2026-10-06 14:05')
    expect(formatWatermarkTime('YYYY-MM-DD HH:mm:ss', NOW)).toBe('2026-10-06 14:05:09')
    expect(formatWatermarkTime('MM-DD HH:mm', NOW)).toBe('10-06 14:05')
  })
})

describe('normalizeWatermarkFields', () => {
  it('逗号串、数组都认;未知项丢掉;按固定顺序排;去重', () => {
    expect(normalizeWatermarkFields('time, text ,name,unknown,name')).toEqual([
      'name',
      'time',
      'text',
    ])
    expect(normalizeWatermarkFields(['phone', 'org'])).toEqual(['org', 'phone'])
  })
  it('空串 = 一项都没勾,不回退默认;不是串也不是数组才回退默认', () => {
    expect(normalizeWatermarkFields('')).toEqual([])
    expect(normalizeWatermarkFields(undefined)).toEqual(['name', 'account', 'time'])
  })
})

describe('normalizeWatermark', () => {
  it('空输入 = 默认(关)', () => {
    expect(normalizeWatermark(null)).toEqual(DEFAULT_WATERMARK)
    expect(normalizeWatermark({})).toEqual(DEFAULT_WATERMARK)
  })
  it('越界与写错的值收口到边界 / 默认,与后端口径一致', () => {
    const w = normalizeWatermark({
      enabled: 'yes',
      text: '字'.repeat(60),
      timeFormat: 'dd/MM/yyyy',
      layout: 'diagonal',
      fontSize: 999,
      opacity: 0,
      rotate: 'abc',
      density: 'huge',
      cross: 'maybe',
    })
    expect(w.enabled).toBe(false)
    expect(w.text).toHaveLength(40)
    expect(w.timeFormat).toBe('YYYY-MM-DD HH:mm')
    expect(w.layout).toBe('single')
    expect(w.fontSize).toBe(28)
    expect(w.opacity).toBe(2)
    expect(w.rotate).toBe(-20)
    expect(w.density).toBe('normal')
    expect(w.cross).toBe(true)
  })
})

describe('watermarkFromValues', () => {
  it('配置中心草稿的字符串键值 → 设置', () => {
    const w = watermarkFromValues({
      'sys.watermark.enabled': 'true',
      'sys.watermark.fields': 'time,name',
      'sys.watermark.layout': 'multi',
      'sys.watermark.fontSize': '20',
      'sys.watermark.cross': 'false',
    })
    expect(w).toMatchObject({
      enabled: true,
      fields: ['name', 'time'],
      layout: 'multi',
      fontSize: 20,
      cross: false,
    })
  })
  it('库里没有这些键(老库未种子)= 默认且关', () => {
    expect(watermarkFromValues({})).toEqual(DEFAULT_WATERMARK)
  })
})

describe('watermarkParts', () => {
  it('按固定顺序拼,手机只印尾号,缺的项和空的自定义文字跳过', () => {
    const s = on({ fields: ['name', 'account', 'org', 'phone', 'time', 'text'], text: '内部资料' })
    expect(watermarkParts(s, WHO, NOW)).toEqual([
      'Andy Zhong',
      'andy',
      '研发中心',
      '*8888',
      '2026-10-06 14:05',
      '内部资料',
    ])
    expect(
      watermarkParts(on({ fields: ['name', 'org', 'phone', 'text'] }), { name: 'A' }, NOW),
    ).toEqual(['A'])
  })
})

// 复刻 NWatermark 的画法(Watermark.mjs):先把整个画布绕左上角旋转 rotate 度,
// 再在旋转后的坐标系里把文字块画在 (xOffset, yOffset) 起的 width × height 里。
// 文字块的四个角落到画布上之后,必须全在 (width + xGap) × (height + yGap) 里,否则就是被裁掉了半截。
const cornersOnCanvas = (p: NonNullable<ReturnType<typeof buildWatermarkProps>>) => {
  const r = (p.rotate * Math.PI) / 180
  return [
    [0, 0],
    [p.width, 0],
    [0, p.height],
    [p.width, p.height],
  ].map(([x, y]) => {
    const rx = p.xOffset + x!
    const ry = p.yOffset + y!
    return [rx * Math.cos(r) - ry * Math.sin(r), rx * Math.sin(r) + ry * Math.cos(r)] as const
  })
}

describe('buildWatermarkProps', () => {
  it('没启用 / 没有任何可印的内容 → null', () => {
    expect(buildWatermarkProps({ ...on(), enabled: false }, WHO, false, NOW)).toBeNull()
    expect(buildWatermarkProps(on({ fields: [] }), WHO, false, NOW)).toBeNull()
    expect(buildWatermarkProps(on({ fields: ['text'], text: '' }), WHO, false, NOW)).toBeNull()
  })

  it('单行用 · 连接,多行每项一行', () => {
    const single = buildWatermarkProps(on({ fields: ['name', 'account'] }), WHO, false, NOW)!
    expect(single.content).toBe('Andy Zhong · andy')
    const multi = buildWatermarkProps(
      on({ fields: ['name', 'account'], layout: 'multi' }),
      WHO,
      false,
      NOW,
    )!
    expect(multi.content).toBe('Andy Zhong\nandy')
    expect(multi.height).toBeGreaterThanOrEqual(multi.lineHeight * 2)
    expect(multi.height).toBeGreaterThan(single.height) // 两行比一行高
  })

  it('浓淡按亮暗取色:浅色印黑、深色印白', () => {
    expect(buildWatermarkProps(on({ opacity: 12 }), WHO, false, NOW)!.fontColor).toBe(
      'rgba(0,0,0,0.12)',
    )
    expect(buildWatermarkProps(on({ opacity: 12 }), WHO, true, NOW)!.fontColor).toBe(
      'rgba(255,255,255,0.12)',
    )
  })

  it.each([-45, -30, -20, -5, 0, 5, 20, 45])(
    '倾斜 %i° 时文字块整个落在一格画布里(不会被裁掉)',
    rotate => {
      for (const layout of ['single', 'multi'] as const) {
        const p = buildWatermarkProps(
          on({ rotate, layout, fields: ['name', 'account', 'org', 'time'] }),
          WHO,
          false,
          NOW,
        )!
        const cw = p.width + p.xGap
        const ch = p.height + p.yGap
        for (const [x, y] of cornersOnCanvas(p)) {
          expect(x).toBeGreaterThanOrEqual(-0.5)
          expect(y).toBeGreaterThanOrEqual(-0.5)
          expect(x).toBeLessThanOrEqual(cw + 0.5)
          expect(y).toBeLessThanOrEqual(ch + 0.5)
        }
      }
    },
  )

  it('不倾斜时没有偏移,间距就是疏密档位;疏 > 适中 > 密', () => {
    const at = (density: WatermarkSettings['density']) =>
      buildWatermarkProps(on({ rotate: 0, density }), WHO, false, NOW)!
    const normal = at('normal')
    expect([normal.xOffset, normal.yOffset]).toEqual([0, 0])
    expect([normal.xGap, normal.yGap]).toEqual([64, 64])
    expect(at('sparse').xGap).toBeGreaterThan(normal.xGap)
    expect(at('dense').xGap).toBeLessThan(normal.xGap)
  })

  it('倾斜之后一格会变大:包围盒比文字块大,多出来的并进间距', () => {
    const flat = buildWatermarkProps(on({ rotate: 0 }), WHO, false, NOW)!
    const tilted = buildWatermarkProps(on({ rotate: -45 }), WHO, false, NOW)!
    expect(tilted.yGap).toBeGreaterThan(flat.yGap)
  })

  it('一格宽跟着文字长度和字号走:汉字按一个字号宽', () => {
    const s = on({ fields: ['text'], text: '内部资料', fontSize: 20, rotate: 0 })
    // 汉字 4 个 × 20px = 80,再放 6% 余量
    expect(buildWatermarkProps(s, WHO, false, NOW)!.width).toBe(Math.ceil(80 * 1.06))
  })
})
