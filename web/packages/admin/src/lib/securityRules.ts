// 安全策略预览:把 sys.security.* 的草稿值翻成"用户会遇到的规则"。
// 短信验证码登录是登录方式,不在这里(见配置中心「登录方式」页)。
// 只产出 i18n 键 + 参数,文案在 locales 里;纯函数,便于单测。

export interface RuleLine {
  icon: string
  /** 关闭的规则灰显划线,仍列出来让人知道有这一项 */
  on: boolean
  key: string
  params?: Record<string, string | number>
}

export interface Duration {
  key: 'config.preview.minutes' | 'config.preview.hours' | 'config.preview.days'
  n: number
}

const round = (v: number) => Math.round(v * 10) / 10
const k = (s: string) => `sys.security.${s}`

/** 分钟数取最合适的单位,最多一位小数。 */
export function humanizeMinutes(minutes: number): Duration {
  if (minutes >= 1440) return { key: 'config.preview.days', n: round(minutes / 1440) }
  if (minutes >= 60) return { key: 'config.preview.hours', n: round(minutes / 60) }
  return { key: 'config.preview.minutes', n: minutes }
}

type Values = Record<string, string | undefined>

const num = (v: Values, key: string, fallback: number) => {
  const n = Number(v[key])
  return v[key] === undefined || v[key] === '' || Number.isNaN(n) ? fallback : n
}
const on = (v: Values, key: string) => v[key] === 'true'

/** 规则里嵌的短语由调用方按当前语言渲染。 */
export interface RuleFormatters {
  duration: (d: Duration) => string
  /** upper / lower / digit / special */
  requirement: (code: string) => string
  /** char / path / math */
  captchaType: (code: string) => string
  /** 把若干短语连成一句("、" 或 ", ") */
  join: (items: string[]) => string
}

export function securityRules(v: Values, f: RuleFormatters): RuleLine[] {
  const requirements = [
    on(v, k('password.requireUpper')) && 'upper',
    on(v, k('password.requireLower')) && 'lower',
    on(v, k('password.requireDigit')) && 'digit',
    on(v, k('password.requireSpecial')) && 'special',
  ].filter((r): r is string => !!r)
  const expireDays = num(v, k('password.expireDays'), 0)
  const history = num(v, k('password.historyCount'), 0)
  const maxFail = num(v, k('loginLock.maxFailCount'), 0)
  const totp = on(v, k('totp.enabled'))

  return [
    maxFail > 0
      ? {
          icon: 'ph:lock-key',
          on: true,
          key: 'config.preview.lock',
          params: { count: maxFail, minutes: num(v, k('loginLock.lockMinutes'), 1) },
        }
      : { icon: 'ph:lock-key', on: false, key: 'config.preview.lockOff' },
    {
      icon: 'ph:textbox',
      on: on(v, k('captcha.enabled')),
      key: 'config.preview.captcha',
      params: { type: f.captchaType(v[k('captcha.type')] || 'char') },
    },
    {
      icon: 'ph:device-mobile',
      on: totp,
      key:
        totp && on(v, k('totp.requireForSuperAdmin'))
          ? 'config.preview.totpSuper'
          : 'config.preview.totp',
    },
    { icon: 'ph:chat-circle-dots', on: on(v, k('mfa.enabled')), key: 'config.preview.smsMfa' },
    requirements.length
      ? {
          icon: 'ph:password',
          on: true,
          key: 'config.preview.password',
          params: {
            min: num(v, k('password.minLength'), 1),
            reqs: f.join(requirements.map(f.requirement)),
          },
        }
      : {
          icon: 'ph:password',
          on: true,
          key: 'config.preview.passwordMin',
          params: { min: num(v, k('password.minLength'), 1) },
        },
    expireDays > 0
      ? {
          icon: 'ph:hourglass',
          on: true,
          key: 'config.preview.expire',
          params: { days: expireDays },
        }
      : { icon: 'ph:hourglass', on: true, key: 'config.preview.noExpire' },
    history > 0
      ? {
          icon: 'ph:clock-counter-clockwise',
          on: true,
          key: 'config.preview.history',
          params: { n: history },
        }
      : { icon: 'ph:clock-counter-clockwise', on: false, key: 'config.preview.historyOff' },
    {
      icon: 'ph:timer',
      on: true,
      key: 'config.preview.session',
      params: {
        access: f.duration(humanizeMinutes(num(v, k('session.accessMinutes'), 30))),
        refresh: f.duration(humanizeMinutes(num(v, k('session.refreshMinutes'), 10080))),
      },
    },
    {
      icon: 'ph:traffic-signal',
      on: on(v, k('rateLimit.enabled')),
      key: 'config.preview.rateLimit',
      params: {
        seconds: num(v, k('rateLimit.windowSeconds'), 1),
        permit: num(v, k('rateLimit.permitPerWindow'), 0),
      },
    },
  ]
}
