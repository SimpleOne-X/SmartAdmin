import { describe, expect, it } from 'vitest'
import { humanizeMinutes, securityRules, type RuleFormatters } from './securityRules'

const f: RuleFormatters = {
  duration: d => `${d.n}${d.key.split('.').pop()}`,
  requirement: code => code,
  captchaType: code => `type:${code}`,
  join: items => items.join('+'),
}
const byKey = (v: Record<string, string>) =>
  Object.fromEntries(securityRules(v, f).map(r => [r.key, r]))

describe('humanizeMinutes', () => {
  it('按量级选单位,最多一位小数', () => {
    expect(humanizeMinutes(30)).toEqual({ key: 'config.preview.minutes', n: 30 })
    expect(humanizeMinutes(90)).toEqual({ key: 'config.preview.hours', n: 1.5 })
    expect(humanizeMinutes(10080)).toEqual({ key: 'config.preview.days', n: 7 })
  })
})

describe('securityRules', () => {
  it('锁定阈值为 0 时显示"不锁定"并灰显', () => {
    const r = byKey({ 'sys.security.loginLock.maxFailCount': '0' })
    expect(r['config.preview.lockOff'].on).toBe(false)
    expect(r['config.preview.lock']).toBeUndefined()
  })

  it('锁定规则带次数和分钟', () => {
    const r = byKey({
      'sys.security.loginLock.maxFailCount': '5',
      'sys.security.loginLock.lockMinutes': '30',
    })
    expect(r['config.preview.lock'].params).toEqual({ count: 5, minutes: 30 })
  })

  it('密码要求按开关拼接,全关时只说长度', () => {
    const on = byKey({
      'sys.security.password.minLength': '8',
      'sys.security.password.requireUpper': 'true',
      'sys.security.password.requireDigit': 'true',
    })
    expect(on['config.preview.password'].params).toEqual({ min: 8, reqs: 'upper+digit' })
    const off = byKey({ 'sys.security.password.minLength': '6' })
    expect(off['config.preview.passwordMin'].params).toEqual({ min: 6 })
  })

  it('开关关闭的规则仍列出但标记 off', () => {
    const r = byKey({
      'sys.security.captcha.enabled': 'false',
      'sys.security.captcha.type': 'math',
    })
    expect(r['config.preview.captcha']).toMatchObject({ on: false, params: { type: 'type:math' } })
  })

  it('超管强制动态口令只在总开关开启时出现', () => {
    expect(
      byKey({ 'sys.security.totp.requireForSuperAdmin': 'true' })['config.preview.totp'].on,
    ).toBe(false)
    expect(
      byKey({
        'sys.security.totp.enabled': 'true',
        'sys.security.totp.requireForSuperAdmin': 'true',
      })['config.preview.totpSuper'].on,
    ).toBe(true)
  })

  it('会话时长换算成可读单位', () => {
    const r = byKey({
      'sys.security.session.accessMinutes': '30',
      'sys.security.session.refreshMinutes': '1440',
    })
    expect(r['config.preview.session'].params).toEqual({ access: '30minutes', refresh: '1days' })
  })
})
