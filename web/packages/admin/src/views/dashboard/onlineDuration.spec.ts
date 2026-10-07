import { describe, expect, it } from 'vitest'
import { onlineDurationParts } from './onlineDuration'

describe('onlineDurationParts', () => {
  it('在线不到一小时只有分钟', () => {
    const login = '2026-09-15T08:00:00'
    const now = new Date('2026-09-15T08:25:00')
    expect(onlineDurationParts(login, now)).toEqual({ hours: 0, minutes: 25 })
  })

  it('在线超过一小时拆出小时和分钟', () => {
    const login = '2026-09-15T08:00:00'
    const now = new Date('2026-09-15T10:25:00')
    expect(onlineDurationParts(login, now)).toEqual({ hours: 2, minutes: 25 })
  })

  it('时钟异常(登录时间在未来)不返回负数', () => {
    const login = '2026-09-15T10:00:00'
    const now = new Date('2026-09-15T08:00:00')
    expect(onlineDurationParts(login, now)).toEqual({ hours: 0, minutes: 0 })
  })
})
