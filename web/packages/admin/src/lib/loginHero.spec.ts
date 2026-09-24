import { describe, expect, it } from 'vitest'
import { normalizeLoginHero, resolveLoginHero, splitLoginHeroFeatures } from './loginHero'

describe('splitLoginHeroFeatures', () => {
  it('trims blank lines and keeps at most five entries', () => {
    expect(
      splitLoginHeroFeatures(' RBAC \n\n 多机构数据范围 \r\n多应用门户\n第四条\n第五条\n第六条'),
    ).toEqual(['RBAC', '多机构数据范围', '多应用门户', '第四条', '第五条'])
  })
})

describe('normalizeLoginHero', () => {
  it('keeps locale objects and drops malformed entries', () => {
    expect(
      normalizeLoginHero({
        'zh-CN': { headline: '自定义标题', highlight: '标题', features: ['一', 2, '二'] },
        bad: 'x',
      }),
    ).toEqual({
      'zh-CN': { headline: '自定义标题', highlight: '标题', features: ['一', '二'] },
    })
  })
})

describe('resolveLoginHero', () => {
  const fallback = {
    headline: '内置标题',
    highlight: '标题',
    features: ['内置卖点一', '内置卖点二'],
  }

  it('falls back field by field', () => {
    expect(resolveLoginHero({ headline: '自定义标题' }, fallback)).toEqual({
      headline: '自定义标题',
      highlight: '标题',
      features: fallback.features,
    })
  })

  it('hides features independently of the copy', () => {
    expect(resolveLoginHero(undefined, fallback, false).features).toEqual([])
  })
})
