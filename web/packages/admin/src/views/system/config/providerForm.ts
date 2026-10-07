// 第三方登录设置面板的表单规则(纯函数,vitest 直接驱动)。
// 字段清单来自后端类型目录:非机密字段回显,机密字段只有「已配置 + 尾四位」,留空表示不改。
import type {
  ExternalAuthField,
  ExternalAuthProvider,
  ExternalAuthProviderSaveInput,
  ExternalAuthProviderTestInput,
} from '#/api'

/** 新增 OIDC 时标识的写法:小写字母开头,字母数字与连字符,2–32 位。与后端规则一致。 */
export const CODE_PATTERN = /^[a-z][a-z0-9-]{1,31}$/

export interface ProviderForm {
  /** 新增(标识可填)还是编辑(标识固定) */
  isNew: boolean
  code: string
  displayName: string
  /** 非机密字段当前输入 */
  values: Record<string, string>
  /** 机密字段当前输入;空串 = 不改 */
  secrets: Record<string, string>
}

/** 用目录里已有的配置(或类型缺省值)起一份表单;`identity` 是还没存过时的标识与展示名。 */
export function createForm(
  fields: readonly ExternalAuthField[],
  saved: ExternalAuthProvider | undefined,
  isNew: boolean,
  identity: { code: string; displayName: string },
): ProviderForm {
  const values: Record<string, string> = {}
  const secrets: Record<string, string> = {}
  for (const f of fields) {
    if (f.secret) secrets[f.name] = ''
    else values[f.name] = saved?.values[f.name] ?? f.default ?? ''
  }
  return {
    isNew,
    code: saved?.code ?? identity.code,
    displayName: saved?.displayName ?? identity.displayName,
    values,
    secrets,
  }
}

const normEndpoint = (v: string | undefined) => (v ?? '').trim().replace(/\/+$/, '')

/** 改了「决定请求发往哪里」的字段(如 OIDC 的 Authority,忽略末尾 `/`)。新增不算。 */
export function endpointChanged(
  fields: readonly ExternalAuthField[],
  form: ProviderForm,
  saved: ExternalAuthProvider | undefined,
): boolean {
  if (form.isNew || !saved) return false
  return fields.some(
    f =>
      f.definesEndpoint && normEndpoint(form.values[f.name]) !== normEndpoint(saved.values[f.name]),
  )
}

/** 这个机密字段现在必须填吗:还没配过,或者改了端点(防止把已保存的密钥发到新地址)。 */
export function secretRequired(
  field: ExternalAuthField,
  saved: ExternalAuthProvider | undefined,
  endpointMoved: boolean,
): boolean {
  if (!field.secret) return false
  return !saved?.secrets[field.name]?.hasValue || endpointMoved
}

/** 缺哪些必填项(字段名列表);空数组 = 可以保存。 */
export function missingFields(
  fields: readonly ExternalAuthField[],
  form: ProviderForm,
  saved: ExternalAuthProvider | undefined,
): string[] {
  const moved = endpointChanged(fields, form, saved)
  const missing: string[] = []
  if (form.isNew) {
    if (!form.displayName.trim()) missing.push('displayName')
    if (!CODE_PATTERN.test(form.code)) missing.push('code')
  }
  for (const f of fields) {
    if (f.secret) {
      if (secretRequired(f, saved, moved) && !form.secrets[f.name]?.trim()) missing.push(f.name)
    } else if (f.required && !form.values[f.name]?.trim()) {
      missing.push(f.name)
    }
  }
  return missing
}

const trimmed = (src: Record<string, string>) =>
  Object.fromEntries(Object.entries(src).map(([k, v]) => [k, v.trim()]))

/** 保存入参:非机密进 values,机密进 secrets(后端按名字对操作日志打码,别混放)。 */
export function buildSaveBody(type: string, form: ProviderForm): ExternalAuthProviderSaveInput {
  return {
    type,
    displayName: form.displayName.trim() || null,
    icon: null,
    values: trimmed(form.values),
    secrets: trimmed(form.secrets),
  }
}

/** 测试入参:用表单里的值;库里已有这条时带上 code,机密留空则取已保存的。 */
export function buildTestBody(
  type: string,
  form: ProviderForm,
  saved: ExternalAuthProvider | undefined,
): ExternalAuthProviderTestInput {
  return {
    code: saved ? form.code : null,
    type,
    values: trimmed(form.values),
    secrets: trimmed(form.secrets),
  }
}

/**
 * 要填到厂商后台的回调地址:后端给的模板里把 `{code}` 换成标识(新增时标识没填,保留占位)。
 * 没配回调基址时模板为空串,返回空串,由页面提示。
 */
export function callbackFor(template: string, code: string): string {
  return template.replace('{code}', code || '{code}')
}
