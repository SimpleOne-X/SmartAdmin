// 配置中心的页面级草稿:进页面一次载入所有结构化表单的值,各分类只读写这份草稿,
// 底部保存条统一保存。切换顶部导航不会丢改动;离开页面草稿随组件销毁。
import { computed, inject, provide, reactive, ref, type InjectionKey } from 'vue'
import {
  configApi,
  externalAuthApi,
  type ExternalAuthCatalog,
  type ExternalProviderAdmin,
} from '#/api'
import { loadSite } from '#/composables/useSite'
import { splitLoginHeroFeatures } from '#/lib/loginHero'
import { buildConfigProviderRows, LINK_BY_ACCOUNT_CODES } from '#/utils/oauthBrand'
import {
  bumpConfigRevision,
  heroKeys,
  LOGIN_LOCALES,
  LOGO_KEY,
  STATIC_CLAIMED_KEYS,
  STRUCTURED_GROUPS,
  tabOfKey,
  type ConfigTab,
  type LoginLocale,
} from './groups'

export const enabledKey = (code: string) => `sys.externalauth.${code}.enabled`
export const linkKey = (code: string) => `sys.externalauth.${code}.linkByAccount`
export const hasLinkSwitch = (code: string) => LINK_BY_ACCOUNT_CODES.includes(code)

/** 与原值不同的键(按字符串比较;配置值一律以字符串落库)。 */
export function diffKeys(original: Record<string, string>, values: Record<string, string>) {
  return Object.keys(values).filter(key => values[key] !== original[key])
}

/** 保存前的校验结论:error 阻止保存并跳到对应分类,warnings 只提示。 */
export interface DraftCheck {
  error?: { tab: ConfigTab; message: string; locale?: LoginLocale }
  warnings: { locale: LoginLocale; message: string }[]
}

/** 登录页 Hero 的规则:卖点最多 5 条(阻止);强调词不在主标题里(提示,照常保存)。 */
export function checkDraft(values: Record<string, string>): DraftCheck {
  const warnings: DraftCheck['warnings'] = []
  for (const locale of LOGIN_LOCALES) {
    const keys = heroKeys(locale)
    if (splitLoginHeroFeatures(values[keys.features], Number.MAX_SAFE_INTEGER).length > 5)
      return { error: { tab: 'brand', locale, message: 'config.login.tooManyFeatures' }, warnings }
    const headline = values[keys.headline]?.trim() ?? ''
    const highlight = values[keys.highlight]?.trim() ?? ''
    if (highlight && headline && !headline.includes(highlight))
      warnings.push({ locale, message: 'config.login.highlightMissing' })
  }
  return { warnings }
}

export function createConfigDraft() {
  const original = reactive<Record<string, string>>({})
  const values = reactive<Record<string, string>>({})
  const providers = ref<ExternalProviderAdmin[]>([])
  const catalog = ref<ExternalAuthCatalog>({
    dataProtectionEphemeral: false,
    callbackBaseUrlMissing: false,
    callbackUriTemplate: '',
    types: [],
    providers: [],
  })
  const loading = ref(true)
  const saving = ref(false)
  /** 裁剪好、还没上传的 Logo;预览期间 values[LOGO_KEY] 是它的 blob: 地址,保存时才上传 */
  const pendingLogo = ref<Blob | null>(null)

  const providerRows = computed(() => buildConfigProviderRows(catalog.value, providers.value))
  const dirtyKeys = computed(() => diffKeys(original, values))
  const dirtyTabs = computed(() => new Set(dirtyKeys.value.map(tabOfKey)))
  /** 结构化表单认领的全部键:「高级」页排除它们。未注册的第三方登录方式也算,它们的开关在卡片上。 */
  const claimedKeys = computed(() => [
    ...STATIC_CLAIMED_KEYS,
    ...providerRows.value.flatMap(p =>
      hasLinkSwitch(p.code) ? [enabledKey(p.code), linkKey(p.code)] : [enabledKey(p.code)],
    ),
  ])

  function reset(next: Record<string, string>) {
    for (const key of Object.keys(original)) delete original[key]
    for (const key of Object.keys(values)) delete values[key]
    Object.assign(original, next)
    Object.assign(values, next)
  }

  async function load() {
    loading.value = true
    try {
      const [groups, list, cat] = await Promise.all([
        Promise.all(STRUCTURED_GROUPS.map(g => configApi.listByGroup(g))),
        externalAuthApi.providersAll(),
        externalAuthApi.catalog(),
      ])
      providers.value = list
      catalog.value = cat
      const claimed = new Set(STATIC_CLAIMED_KEYS)
      const next: Record<string, string> = {}
      // 库里没有的键按空串起步:表单显示默认值,不动它就不算改动,也不会被写回
      for (const key of STATIC_CLAIMED_KEYS) next[key] = ''
      for (const row of groups.flat())
        if (claimed.has(row.configKey)) next[row.configKey] = row.configValue ?? ''
      // 第三方登录只收已配置的方式:没配好的开关不可打开,永远不写
      Object.assign(next, providerSwitchValues())
      reset(next)
    } finally {
      loading.value = false
    }
  }

  /** 已配置的第三方各自的开关值(enabled / linkByAccount)。 */
  function providerSwitchValues() {
    const out: Record<string, string> = {}
    for (const p of providerRows.value.filter(row => row.registered)) {
      out[enabledKey(p.code)] = String(p.enabled)
      if (hasLinkSwitch(p.code)) out[linkKey(p.code)] = String(p.linkByAccount)
    }
    return out
  }

  /**
   * 设置面板保存或清除之后重读第三方目录。已在草稿里的开关不动(可能有未保存的改动),
   * 新配好的补进来,被清掉的移出——没有配置的方式,开关没有意义。
   */
  async function refreshProviders() {
    const [list, cat] = await Promise.all([
      externalAuthApi.providersAll(),
      externalAuthApi.catalog(),
    ])
    providers.value = list
    catalog.value = cat
    const fresh = providerSwitchValues()
    for (const [key, value] of Object.entries(fresh)) {
      if (key in original) continue
      original[key] = value
      values[key] = value
    }
    for (const row of providerRows.value.filter(r => !r.registered)) {
      for (const key of [enabledKey(row.code), linkKey(row.code)]) {
        delete original[key]
        delete values[key]
      }
    }
  }

  function setPendingLogo(blob: Blob | null, previewUrl: string) {
    revokePreview()
    pendingLogo.value = blob
    values[LOGO_KEY] = previewUrl
  }

  function revokePreview() {
    if (values[LOGO_KEY]?.startsWith('blob:')) URL.revokeObjectURL(values[LOGO_KEY])
  }

  function discard() {
    revokePreview()
    pendingLogo.value = null
    Object.assign(values, original)
  }

  /** 保存全部改动;有待传的 Logo 先上传换成签名直链,再一次批量写库。 */
  async function save() {
    saving.value = true
    try {
      if (pendingLogo.value) {
        const file = new File([pendingLogo.value], 'logo.png', { type: 'image/png' })
        const uploaded = await configApi.uploadLogo(file)
        revokePreview()
        values[LOGO_KEY] = uploaded.viewUrl ?? ''
        // 传完就清掉:后面批量保存失败时重试不会再传一遍
        pendingLogo.value = null
      }
      for (const key of dirtyKeys.value) values[key] = values[key].trim()
      const items = diffKeys(original, values).map(configKey => ({
        configKey,
        configValue: values[configKey],
      }))
      if (items.length) await configApi.saveBatch(items)
      Object.assign(original, values)
      bumpConfigRevision()
      // 即时生效:侧栏、顶栏、登录页、浏览器标签页的品牌随之刷新
      await loadSite(true)
    } finally {
      saving.value = false
    }
  }

  return {
    original,
    values,
    providers,
    catalog,
    providerRows,
    loading,
    saving,
    pendingLogo,
    dirtyKeys,
    dirtyTabs,
    claimedKeys,
    load,
    refreshProviders,
    discard,
    save,
    setPendingLogo,
  }
}

export type ConfigDraft = ReturnType<typeof createConfigDraft>

const DRAFT_KEY: InjectionKey<ConfigDraft> = Symbol('config-draft')

export function provideConfigDraft(draft: ConfigDraft) {
  provide(DRAFT_KEY, draft)
}

export function useConfigDraft(): ConfigDraft {
  const draft = inject(DRAFT_KEY)
  if (!draft) throw new Error('useConfigDraft() 只能在配置中心页面内使用')
  return draft
}

/** 字符串 / 数值 / 开关三种双向绑定,模板里直接 v-model。 */
export function useDraftFields() {
  const { values } = useConfigDraft()
  return {
    str: (key: string) =>
      computed({
        get: () => values[key] ?? '',
        set: v => {
          values[key] = v ?? ''
        },
      }),
    num: (key: string, fallback: number) =>
      computed<number>({
        get: () => {
          const n = Number(values[key])
          return values[key] === '' || values[key] === undefined || Number.isNaN(n) ? fallback : n
        },
        set: v => {
          values[key] = String(v ?? fallback)
        },
      }),
    bool: (key: string) =>
      computed<boolean>({
        get: () => values[key] === 'true',
        set: v => {
          values[key] = v ? 'true' : 'false'
        },
      }),
  }
}
