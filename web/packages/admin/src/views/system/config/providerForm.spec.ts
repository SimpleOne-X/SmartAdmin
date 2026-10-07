import { describe, expect, it } from 'vitest'
import type { ExternalAuthField, ExternalAuthProvider } from '#/api'
import {
  buildSaveBody,
  buildTestBody,
  callbackFor,
  CODE_PATTERN,
  createForm,
  endpointChanged,
  missingFields,
  secretRequired,
} from './providerForm'

const OIDC: ExternalAuthField[] = [
  { name: 'authority', secret: false, required: true, default: null, definesEndpoint: true },
  { name: 'clientId', secret: false, required: true, default: null, definesEndpoint: false },
  { name: 'clientSecret', secret: true, required: true, default: null, definesEndpoint: false },
  {
    name: 'scopes',
    secret: false,
    required: false,
    default: 'openid profile email',
    definesEndpoint: false,
  },
  { name: 'usePkce', secret: false, required: false, default: 'true', definesEndpoint: false },
]

const saved = (over: Partial<ExternalAuthProvider> = {}): ExternalAuthProvider => ({
  code: 'corp-sso',
  type: 'oidc',
  displayName: '公司 SSO',
  icon: null,
  source: 'db',
  installed: true,
  configured: true,
  values: {
    authority: 'https://idp.example.com/',
    clientId: 'cid',
    scopes: 'openid',
    usePkce: 'true',
  },
  secrets: { clientSecret: { hasValue: true, hint: '1a2b' } },
  callbackUri: 'https://a.example.com/api/v1/auth/external/corp-sso/callback',
  ...over,
})

const blank = { code: '', displayName: '' }

describe('createForm', () => {
  it('新增:非机密取字段缺省值,机密为空', () => {
    const f = createForm(OIDC, undefined, true, blank)
    expect(f.values).toMatchObject({
      authority: '',
      scopes: 'openid profile email',
      usePkce: 'true',
    })
    expect(f.secrets).toEqual({ clientSecret: '' })
  })

  it('编辑:回显已存的非机密值,机密永远为空(接口不回明文)', () => {
    const f = createForm(OIDC, saved(), false, blank)
    expect(f.code).toBe('corp-sso')
    expect(f.values.authority).toBe('https://idp.example.com/')
    expect(f.secrets.clientSecret).toBe('')
  })

  it('官方厂商首次配置:标识取类型名', () => {
    const f = createForm(OIDC, undefined, false, { code: 'github', displayName: 'GitHub' })
    expect(f).toMatchObject({ isNew: false, code: 'github', displayName: 'GitHub' })
  })
})

describe('endpointChanged / secretRequired', () => {
  it('Authority 改了(忽略末尾斜杠)才算改端点', () => {
    const f = createForm(OIDC, saved(), false, blank)
    expect(endpointChanged(OIDC, f, saved())).toBe(false)
    f.values.authority = 'https://idp.example.com'
    expect(endpointChanged(OIDC, f, saved())).toBe(false)
    f.values.authority = 'https://evil.example.com/'
    expect(endpointChanged(OIDC, f, saved())).toBe(true)
  })

  it('新增与还没存过的不算改端点', () => {
    const f = createForm(OIDC, undefined, true, blank)
    f.values.authority = 'https://x'
    expect(endpointChanged(OIDC, f, undefined)).toBe(false)
  })

  it('机密:没配过必填;配过则留空即可,改了端点又必填', () => {
    const secret = OIDC[2]
    expect(secretRequired(secret, undefined, false)).toBe(true)
    expect(secretRequired(secret, saved(), false)).toBe(false)
    expect(secretRequired(secret, saved(), true)).toBe(true)
    expect(secretRequired(OIDC[0], undefined, true)).toBe(false)
  })
})

describe('missingFields', () => {
  it('新增 OIDC:名称、标识、Authority、Client ID、Client Secret 都要', () => {
    const f = createForm(OIDC, undefined, true, blank)
    expect(missingFields(OIDC, f, undefined)).toEqual([
      'displayName',
      'code',
      'authority',
      'clientId',
      'clientSecret',
    ])
    f.displayName = '公司 SSO'
    f.code = 'corp-sso'
    f.values.authority = 'https://idp.example.com'
    f.values.clientId = 'cid'
    f.secrets.clientSecret = 'shh'
    expect(missingFields(OIDC, f, undefined)).toEqual([])
  })

  it('编辑已配置的:机密留空可保存;改了 Authority 就必须重输机密', () => {
    const f = createForm(OIDC, saved(), false, blank)
    expect(missingFields(OIDC, f, saved())).toEqual([])
    f.values.authority = 'https://other.example.com'
    expect(missingFields(OIDC, f, saved())).toEqual(['clientSecret'])
    f.secrets.clientSecret = 'new'
    expect(missingFields(OIDC, f, saved())).toEqual([])
  })

  it('纯空白不算填了', () => {
    const f = createForm(OIDC, saved(), false, blank)
    f.values.clientId = '   '
    expect(missingFields(OIDC, f, saved())).toEqual(['clientId'])
  })

  it('标识的写法与后端一致:小写字母开头,2 到 32 位', () => {
    expect(CODE_PATTERN.test('corp-sso')).toBe(true)
    expect(CODE_PATTERN.test('a')).toBe(false)
    expect(CODE_PATTERN.test('Corp')).toBe(false)
    expect(CODE_PATTERN.test('1abc')).toBe(false)
    expect(CODE_PATTERN.test('a'.repeat(33))).toBe(false)
  })
})

describe('buildSaveBody / buildTestBody', () => {
  it('机密只走 secrets,非机密只走 values,都去首尾空格', () => {
    const f = createForm(OIDC, undefined, true, blank)
    f.displayName = ' 公司 SSO '
    f.values.authority = ' https://idp.example.com '
    f.secrets.clientSecret = ' shh '
    const body = buildSaveBody('oidc', f)
    expect(body.type).toBe('oidc')
    expect(body.displayName).toBe('公司 SSO')
    expect(body.values?.authority).toBe('https://idp.example.com')
    expect(body.values).not.toHaveProperty('clientSecret')
    expect(body.secrets).toEqual({ clientSecret: 'shh' })
  })

  it('留空的机密照样带上空串(后端据此判定「不改」)', () => {
    const f = createForm(OIDC, saved(), false, blank)
    expect(buildSaveBody('oidc', f).secrets).toEqual({ clientSecret: '' })
  })

  it('测试:库里有这条才带 code,新增或首次配置不带', () => {
    const f = createForm(OIDC, saved(), false, blank)
    expect(buildTestBody('oidc', f, saved()).code).toBe('corp-sso')
    const g = createForm(OIDC, undefined, false, { code: 'github', displayName: 'GitHub' })
    expect(buildTestBody('github', g, undefined).code).toBeNull()
  })
})

describe('callbackFor', () => {
  it('把模板里的 {code} 换成标识;没填时保留占位;没配基址时为空', () => {
    const tpl = 'https://a.example.com/api/v1/auth/external/{code}/callback'
    expect(callbackFor(tpl, 'github')).toBe(
      'https://a.example.com/api/v1/auth/external/github/callback',
    )
    expect(callbackFor(tpl, '')).toBe('https://a.example.com/api/v1/auth/external/{code}/callback')
    expect(callbackFor('', 'github')).toBe('')
  })
})
