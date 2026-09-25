// 配置中心的分类与键归属。顶部导航每一项是一个分类,分类名就是它管的那一类东西;
// 结构化表单认领的键由这里集中声明,
// 草稿只装这些键,「高级」页据此排除它们(同一个键不会在两处都能改)。
import { ref } from 'vue'

export const CONFIG_TABS = [
  'brand',
  'security',
  'signin',
  'sensitive',
  'upload',
  'job',
  'advanced',
] as const
export type ConfigTab = (typeof CONFIG_TABS)[number]

/** 顶部导航分组:站点与安全 | 系统,组间留空。 */
export const TAB_GROUPS: readonly (readonly ConfigTab[])[] = [
  ['brand', 'security', 'signin', 'sensitive'],
  ['upload', 'job', 'advanced'],
]

/** 导航图标 */
export const TAB_ICONS: Record<ConfigTab, string> = {
  brand: 'ph:browser',
  security: 'ph:shield-check',
  signin: 'ph:sign-in',
  sensitive: 'ph:lock-key',
  upload: 'ph:cloud-arrow-up',
  job: 'ph:clock',
  advanced: 'ph:sliders-horizontal',
}

export const LOGO_KEY = 'sys.site.logo'
export const BRAND_KEYS = [
  'sys.site.title',
  LOGO_KEY,
  'sys.site.copyright',
  'sys.site.copyrightUrl',
] as const

export const LOGIN_LOCALES = ['zh-CN', 'en-US'] as const
export type LoginLocale = (typeof LOGIN_LOCALES)[number]
export const heroKeys = (locale: LoginLocale) => ({
  headline: `sys.login.hero.headline.${locale}`,
  highlight: `sys.login.hero.highlight.${locale}`,
  features: `sys.login.hero.features.${locale}`,
})
export const SUBTITLE_KEY = 'sys.site.subtitle'
export const SHOW_FEATURES_KEY = 'sys.login.hero.showFeatures'
export const LOGIN_KEYS = [
  SUBTITLE_KEY,
  SHOW_FEATURES_KEY,
  ...LOGIN_LOCALES.flatMap(l => Object.values(heroKeys(l))),
]

// 数值项的下限:锁定阈值不为负、时长至少 1 分钟,避免存 0 变成永不过期/永久锁定。
// 0 在少数项上是合法的"关闭"语义(永不过期、不防重用、不限流)。
export const SECURITY_NUMBERS: Readonly<Record<string, number>> = {
  'sys.security.loginLock.maxFailCount': 0,
  'sys.security.loginLock.lockMinutes': 1,
  'sys.security.password.minLength': 1,
  'sys.security.password.expireDays': 0,
  'sys.security.password.historyCount': 0,
  'sys.security.session.accessMinutes': 1,
  'sys.security.session.refreshMinutes': 1,
  'sys.security.rateLimit.windowSeconds': 1,
  'sys.security.rateLimit.permitPerWindow': 0,
  'sys.security.rateLimit.authPermitPerWindow': 0,
}
/** 短信验证码登录是一种登录方式,在「登录方式」页,不在安全策略 */
export const SMS_LOGIN_KEY = 'sys.security.smsLogin.enabled'
export const SECURITY_SWITCHES = [
  'sys.security.password.requireUpper',
  'sys.security.password.requireLower',
  'sys.security.password.requireDigit',
  'sys.security.password.requireSpecial',
  'sys.security.password.forceChangeOnFirstLogin',
  'sys.security.captcha.enabled',
  'sys.security.totp.enabled',
  'sys.security.totp.requireForSuperAdmin',
  'sys.security.mfa.enabled',
  'sys.security.rateLimit.enabled',
] as const
export const CAPTCHA_TYPE_KEY = 'sys.security.captcha.type'
export const SECURITY_KEYS = [
  ...Object.keys(SECURITY_NUMBERS),
  ...SECURITY_SWITCHES,
  CAPTCHA_TYPE_KEY,
  SMS_LOGIN_KEY,
]

export const UPLOAD_MAX_KEY = 'sys.upload.maxSizeMb'
export const UPLOAD_EXTS_KEY = 'sys.upload.allowedExtensions'
export const JOB_RETENTION_KEY = 'sys.job.logRetentionDays'
export const JOB_ALERT_KEY = 'sys.job.alertEmails'

/** 固定认领的键(第三方登录的键按已注册的方式动态认领,见 draft.ts)。 */
export const STATIC_CLAIMED_KEYS: readonly string[] = [
  ...BRAND_KEYS,
  ...LOGIN_KEYS,
  ...SECURITY_KEYS,
  UPLOAD_MAX_KEY,
  UPLOAD_EXTS_KEY,
  JOB_RETENTION_KEY,
  JOB_ALERT_KEY,
]

/** 结构化表单从这些分组取值。 */
export const STRUCTURED_GROUPS = ['sys', 'login', 'security', 'upload', 'job'] as const

/** 键 → 所属分类,用于导航上的"有未保存改动"标记。 */
export function tabOfKey(key: string): ConfigTab {
  if (key.startsWith('sys.site.') || key.startsWith('sys.login.')) return 'brand'
  if (key === SMS_LOGIN_KEY || key.startsWith('sys.externalauth.')) return 'signin'
  if (key.startsWith('sys.security.')) return 'security'
  if (key.startsWith('sys.upload.')) return 'upload'
  if (key.startsWith('sys.job.')) return 'job'
  return 'advanced'
}

/** 配置行增删改的版本号:保存或在「高级」页改了行之后 bump,「高级」表据此重拉。 */
export const configRevision = ref(0)

export function bumpConfigRevision() {
  configRevision.value++
}
