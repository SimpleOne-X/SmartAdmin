import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  listByGroup: vi.fn(),
  saveBatch: vi.fn(),
  uploadLogo: vi.fn(),
  providersAll: vi.fn(),
  catalog: vi.fn(),
  loadSite: vi.fn(),
}))
vi.mock('#/api', () => ({
  configApi: {
    listByGroup: api.listByGroup,
    saveBatch: api.saveBatch,
    uploadLogo: api.uploadLogo,
  },
  externalAuthApi: { providersAll: api.providersAll, catalog: api.catalog },
}))
vi.mock('#/composables/useSite', () => ({ loadSite: api.loadSite }))
vi.mock('#/utils/oauthBrand', () => ({
  LINK_BY_ACCOUNT_CODES: ['wecom'],
  // 行只由目录里「已配置」的项决定;开关值取自管理端列表
  buildConfigProviderRows: (
    catalog: { providers: { code: string; configured: boolean }[] },
    admin: { code: string; enabled: boolean; linkByAccount?: boolean }[],
  ) =>
    catalog.providers.map(p => {
      const a = admin.find(x => x.code === p.code)
      return {
        code: p.code,
        registered: p.configured,
        enabled: p.configured && !!a?.enabled,
        linkByAccount: p.configured && !!a?.linkByAccount,
        displayName: p.code,
        icon: '',
      }
    }),
}))

import { checkDraft, createConfigDraft, diffKeys } from './draft'
import { tabOfKey } from './groups'

const catalogOf = (configured: string[], unconfigured: string[] = []) => ({
  dataProtectionEphemeral: false,
  callbackBaseUrlMissing: false,
  callbackUriTemplate: '',
  types: [],
  providers: [
    ...configured.map(code => ({ code, configured: true })),
    ...unconfigured.map(code => ({ code, configured: false })),
  ],
})

const rows: Record<string, { configKey: string; configValue: string }[]> = {
  sys: [
    { configKey: 'sys.site.title', configValue: 'SmartAdmin' },
    { configKey: 'sys.site.logo', configValue: '' },
    { configKey: 'sys.site.icp', configValue: '未认领的键' },
  ],
  login: [],
  security: [{ configKey: 'sys.security.password.minLength', configValue: '8' }],
  upload: [],
  job: [],
}

beforeEach(() => {
  vi.clearAllMocks()
  api.listByGroup.mockImplementation((g: string) => Promise.resolve(rows[g] ?? []))
  api.providersAll.mockResolvedValue([{ code: 'wecom', enabled: true, linkByAccount: false }])
  api.catalog.mockResolvedValue(catalogOf(['wecom']))
  api.saveBatch.mockResolvedValue(true)
  api.loadSite.mockResolvedValue(undefined)
})

describe('diffKeys / tabOfKey', () => {
  it('只报值变了的键', () => {
    expect(diffKeys({ a: '1', b: '2' }, { a: '1', b: '3' })).toEqual(['b'])
  })

  it('站点品牌与登录页的键同归 brand 页签', () => {
    expect(tabOfKey('sys.site.subtitle')).toBe('brand')
    expect(tabOfKey('sys.login.hero.showFeatures')).toBe('brand')
    expect(tabOfKey('sys.site.logo')).toBe('brand')
    expect(tabOfKey('sys.externalauth.wecom.enabled')).toBe('signin')
    // 短信验证码登录是登录方式,归「登录方式」而不是安全策略
    expect(tabOfKey('sys.security.smsLogin.enabled')).toBe('signin')
    expect(tabOfKey('sys.security.mfa.enabled')).toBe('security')
    expect(tabOfKey('biz.whatever')).toBe('advanced')
  })
})

describe('checkDraft', () => {
  it('卖点超过 5 条阻止保存并指向登录页所在页签', () => {
    const check = checkDraft({ 'sys.login.hero.features.en-US': 'a\nb\nc\nd\ne\nf' })
    expect(check.error).toMatchObject({ tab: 'brand', locale: 'en-US' })
  })

  it('强调词不在主标题里只是提示', () => {
    const check = checkDraft({
      'sys.login.hero.headline.zh-CN': '企业控制台',
      'sys.login.hero.highlight.zh-CN': '权限',
    })
    expect(check.error).toBeUndefined()
    expect(check.warnings).toEqual([{ locale: 'zh-CN', message: 'config.login.highlightMissing' }])
  })
})

describe('createConfigDraft', () => {
  it('只装认领的键;库里没有的键按空串起步,第三方登录按已注册方式装载', async () => {
    const d = createConfigDraft()
    await d.load()
    expect(d.values['sys.site.title']).toBe('SmartAdmin')
    expect(d.values['sys.site.icp']).toBeUndefined()
    expect(d.values['sys.job.alertEmails']).toBe('')
    expect(d.values['sys.externalauth.wecom.enabled']).toBe('true')
    expect(d.values['sys.externalauth.wecom.linkByAccount']).toBe('false')
    expect(d.dirtyKeys.value).toEqual([])
    expect(d.claimedKeys.value).toContain('sys.externalauth.wecom.linkByAccount')
  })

  it('设置面板保存后重读目录:新配好的开关补进草稿,已有的(可能有未保存改动)不动', async () => {
    const d = createConfigDraft()
    await d.load()
    d.values['sys.externalauth.wecom.enabled'] = 'false'
    api.catalog.mockResolvedValue(catalogOf(['wecom', 'github']))
    api.providersAll.mockResolvedValue([
      { code: 'wecom', enabled: true, linkByAccount: false },
      { code: 'github', enabled: false, linkByAccount: false },
    ])
    await d.refreshProviders()
    expect(d.values['sys.externalauth.github.enabled']).toBe('false')
    expect(d.original['sys.externalauth.github.enabled']).toBe('false')
    // 企业微信的未保存改动还在
    expect(d.values['sys.externalauth.wecom.enabled']).toBe('false')
    expect(d.dirtyKeys.value).toEqual(['sys.externalauth.wecom.enabled'])
  })

  it('清除配置后重读目录:它的开关移出草稿,不留孤键', async () => {
    const d = createConfigDraft()
    await d.load()
    api.catalog.mockResolvedValue(catalogOf([], ['wecom']))
    api.providersAll.mockResolvedValue([])
    await d.refreshProviders()
    expect('sys.externalauth.wecom.enabled' in d.values).toBe(false)
    expect('sys.externalauth.wecom.linkByAccount' in d.original).toBe(false)
    expect(d.dirtyKeys.value).toEqual([])
  })

  it('改动计入脏键和分类,放弃即还原', async () => {
    const d = createConfigDraft()
    await d.load()
    d.values['sys.site.title'] = 'New'
    d.values['sys.security.password.minLength'] = '10'
    expect(d.dirtyKeys.value.toSorted()).toEqual([
      'sys.security.password.minLength',
      'sys.site.title',
    ])
    expect([...d.dirtyTabs.value].toSorted()).toEqual(['brand', 'security'])
    d.discard()
    expect(d.dirtyKeys.value).toEqual([])
  })

  it('保存只发改过的键(去首尾空格),随后刷新站点信息', async () => {
    const d = createConfigDraft()
    await d.load()
    d.values['sys.site.title'] = '  New  '
    await d.save()
    expect(api.saveBatch).toHaveBeenCalledWith([
      { configKey: 'sys.site.title', configValue: 'New' },
    ])
    expect(api.loadSite).toHaveBeenCalledWith(true)
    expect(d.dirtyKeys.value).toEqual([])
  })

  it('有待传 Logo 时先上传,把签名直链写进 sys.site.logo 再批量保存', async () => {
    api.uploadLogo.mockResolvedValue({ id: 9, viewUrl: '/api/v1/sys/file/9/view?sig=s' })
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    const d = createConfigDraft()
    await d.load()
    d.setPendingLogo(new Blob(['png']), 'blob:preview')
    await d.save()
    expect(api.uploadLogo).toHaveBeenCalledOnce()
    expect(api.saveBatch).toHaveBeenCalledWith([
      { configKey: 'sys.site.logo', configValue: '/api/v1/sys/file/9/view?sig=s' },
    ])
    expect(revoke).toHaveBeenCalledWith('blob:preview')
    expect(d.pendingLogo.value).toBeNull()
  })

  it('批量保存失败后重试不会再传一次 Logo', async () => {
    api.uploadLogo.mockResolvedValue({ id: 9, viewUrl: '/v' })
    api.saveBatch.mockRejectedValueOnce(new Error('boom'))
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    const d = createConfigDraft()
    await d.load()
    d.setPendingLogo(new Blob(['png']), 'blob:preview')
    await expect(d.save()).rejects.toThrow('boom')
    await d.save()
    expect(api.uploadLogo).toHaveBeenCalledOnce()
    expect(api.saveBatch).toHaveBeenLastCalledWith([
      { configKey: 'sys.site.logo', configValue: '/v' },
    ])
  })
})
