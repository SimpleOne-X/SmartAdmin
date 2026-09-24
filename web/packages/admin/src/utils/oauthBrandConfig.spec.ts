import { describe, expect, it } from 'vitest'
import { buildConfigProviderRows, LINK_BY_ACCOUNT_CODES, OFFICIAL_TYPES } from './oauthBrand'

const type = (t: string) => ({ type: t })
const entry = (
  over: Partial<Parameters<typeof buildConfigProviderRows>[0]['providers'][number]>,
) => ({
  code: 'github',
  type: 'github',
  displayName: 'GitHub',
  icon: null,
  source: 'db',
  installed: true,
  configured: true,
  ...over,
})

describe('buildConfigProviderRows', () => {
  it('lists the four official vendors even when nothing is installed, and no Gitee / QQ', () => {
    const rows = buildConfigProviderRows({ types: [], providers: [] }, [])
    expect(rows.map(r => r.code)).toEqual([...OFFICIAL_TYPES])
    expect(rows.every(r => r.state === 'notInstalled' && !r.registered && !r.enabled)).toBe(true)
  })

  it('an installed type without a saved config is unconfigured', () => {
    const rows = buildConfigProviderRows({ types: [type('github')], providers: [] }, [])
    const gh = rows.find(r => r.code === 'github')!
    expect(gh.state).toBe('unconfigured')
    expect(gh.source).toBe('none')
    expect(gh.registered).toBe(false)
    expect(rows.find(r => r.code === 'wecom')!.state).toBe('notInstalled')
  })

  it('a configured provider is registered and carries its switches', () => {
    const rows = buildConfigProviderRows(
      {
        types: [type('wecom')],
        providers: [entry({ code: 'wecom', type: 'wecom', displayName: '企微' })],
      },
      [{ code: 'wecom', enabled: true, linkByAccount: true }],
    )
    const wc = rows.find(r => r.code === 'wecom')!
    expect(wc).toMatchObject({
      state: 'configured',
      source: 'db',
      registered: true,
      enabled: true,
      linkByAccount: true,
      displayName: '企微',
    })
  })

  it('keeps the switches off for anything that is not configured', () => {
    // 开关值来自运营键;没配好的方式即使键里是 true,行上也不能亮
    const rows = buildConfigProviderRows(
      {
        types: [type('github')],
        providers: [entry({ configured: false })],
      },
      [{ code: 'github', enabled: true, linkByAccount: true }],
    )
    const gh = rows.find(r => r.code === 'github')!
    expect(gh.state).toBe('unconfigured')
    expect(gh.enabled).toBe(false)
    expect(gh.linkByAccount).toBe(false)
  })

  it('a stored row whose package is not installed shows as not installed', () => {
    const rows = buildConfigProviderRows(
      { types: [], providers: [entry({ installed: false, configured: false })] },
      [],
    )
    expect(rows.find(r => r.code === 'github')!.state).toBe('notInstalled')
  })

  it('offers the link-by-account switch only where the external id is an enterprise account', () => {
    // 企业微信的 userid 由企业统一分配;钉钉 / 微信 / GitHub 的标识对不上本地账号
    expect([...LINK_BY_ACCOUNT_CODES]).toEqual(['wecom'])
  })

  it('appends OIDC entries and code-registered providers after the official four', () => {
    const rows = buildConfigProviderRows(
      {
        types: [type('oidc')],
        providers: [
          entry({ code: 'corp-sso', type: 'oidc', displayName: '公司 SSO' }),
          entry({ code: 'ldap', type: '', displayName: 'LDAP', source: 'code' }),
        ],
      },
      [
        { code: 'corp-sso', enabled: true },
        { code: 'ldap', enabled: true },
      ],
    )
    expect(rows.map(r => r.code)).toEqual([...OFFICIAL_TYPES, 'corp-sso', 'ldap'])
    const sso = rows.find(r => r.code === 'corp-sso')!
    expect(sso).toMatchObject({ state: 'configured', source: 'db', enabled: true })
    const ldap = rows.find(r => r.code === 'ldap')!
    expect(ldap).toMatchObject({ state: 'configured', source: 'code', registered: true })
  })

  it('a code-registered provider with an official code wins over the package state', () => {
    const rows = buildConfigProviderRows(
      { types: [], providers: [entry({ type: '', source: 'code' })] },
      [{ code: 'github', enabled: true }],
    )
    const gh = rows.find(r => r.code === 'github')!
    expect(gh).toMatchObject({ state: 'configured', source: 'code', enabled: true })
    expect(rows.filter(r => r.code === 'github')).toHaveLength(1)
  })
})
