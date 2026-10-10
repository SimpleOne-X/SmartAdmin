import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import zhCN from '#/locales/zh-CN'
import phSubset from '#/assets/icons/ph-subset.json'

// 只验证用户页的两处接线:「单独授权一览」(入口、授权弹窗保存后的刷新、「去调整」)和左侧机构分组栏
// (默认展开层级、展开全部切换、搜索框出现时机、收起与恢复)。
// 页面自己的大块(列表列、新增编辑)不在这里测,所以表格与各子弹窗都换成桩。
const spies = vi.hoisted(() => ({
  tableRefresh: vi.fn(),
  overviewRefresh: vi.fn(),
  sheetOpen: vi.fn(),
  orgList: vi.fn(),
}))

// 页面在 watch 里读路由;路径不是 /system/user 时它直接返回,不去预置角色条件。
vi.mock('vue-router', async importOriginal => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => ({ path: '/spec', query: {} }),
}))

// 页面按内容区宽度决定是否把机构树收进抽屉;happy-dom 没有布局,宽度由用例直接设定(0 = 量不到,按宽屏渲染)。
const layout = vi.hoisted(() => ({ width: { value: 0 } }))
vi.mock('@vueuse/core', async importOriginal => {
  const { ref } = await import('vue')
  const width = ref(0)
  layout.width = width
  return {
    ...(await importOriginal<typeof import('@vueuse/core')>()),
    useElementSize: () => ({ width, height: ref(0) }),
  }
})

vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  mfaApi: { clear: async () => true },
  positionApi: { page: async () => ({ items: [], total: 0 }) },
  roleApi: { page: async () => ({ items: [], total: 0 }) },
  orgApi: { list: () => spies.orgList() },
  userApi: { page: async () => ({ items: [], total: 0 }) },
}))

// 表格桩:把工具栏「更多」里的项渲染成按钮,点了就发 more-select;刷新计数;工具栏最左的 #toolbar 插槽原样渲染。
vi.mock('smart-naive-table', async importOriginal => {
  const { defineComponent, h: el } = await import('vue')
  return {
    ...(await importOriginal<typeof import('smart-naive-table')>()),
    SmartTable: defineComponent({
      props: { toolbar: { type: Object, default: () => ({}) } },
      emits: ['more-select'],
      setup(props, { emit, expose, slots }) {
        expose({ refresh: spies.tableRefresh })
        return () =>
          el('div', { class: 'stub-table' }, [
            slots.toolbar?.(),
            ...((props.toolbar as { more?: { label: string; key: string }[] }).more ?? []).map(o =>
              el(
                'button',
                { class: 'more-item', onClick: () => emit('more-select', o.key) },
                o.label,
              ),
            ),
          ])
      },
    }),
  }
})

// 不相干的子弹窗换成什么都不渲染的空组件(vi.mock 会提升到文件顶部,工厂要经 vi.hoisted 才拿得到)
const { emptyStub } = vi.hoisted(() => ({
  emptyStub: async () => {
    const { defineComponent } = await import('vue')
    return { default: defineComponent({ setup: () => () => null }) }
  },
}))
vi.mock('./components/UserFormModal.vue', emptyStub)
vi.mock('./components/ResetPasswordModal.vue', emptyStub)
vi.mock('#/components/ImportWizard/index.vue', emptyStub)
vi.mock('#/components/ExportColumnsModal/index.vue', emptyStub)

// 授权弹窗桩:open 计数;点按钮模拟保存成功。
vi.mock('./components/UserGrantMenuSheet.vue', async () => {
  const { defineComponent, h: el } = await import('vue')
  return {
    default: defineComponent({
      emits: ['saved'],
      setup(_, { emit, expose }) {
        expose({ open: spies.sheetOpen })
        return () =>
          el('button', { class: 'sheet-save', onClick: () => emit('saved') }, 'sheet-save')
      },
    }),
  }
})

// 一览抽屉桩:show 打在 data 属性上;refresh 计数;两个按钮模拟「去调整」和关闭。
vi.mock('./components/UserGrantOverviewDrawer.vue', async () => {
  const { defineComponent, h: el } = await import('vue')
  return {
    default: defineComponent({
      props: { show: { type: Boolean, default: false } },
      emits: ['update:show', 'adjust'],
      setup(props, { emit, expose }) {
        expose({ refresh: spies.overviewRefresh })
        return () =>
          el('div', { class: 'overview', 'data-show': String(props.show) }, [
            el(
              'button',
              {
                class: 'overview-adjust',
                onClick: () => emit('adjust', { id: 7, name: '张三' }),
              },
              'adjust',
            ),
            el(
              'button',
              { class: 'overview-close', onClick: () => emit('update:show', false) },
              'x',
            ),
          ])
      },
    }),
  }
})

import { useAuthStore } from '#/stores/auth'
import UserPage from './index.vue'

addCollection(phSubset as IconifyJSON)

const OVERVIEW_PERM = 'GET:/api/v1/sys/user/menu-grants/page'

let app: App<Element> | undefined
let warnings: string[] = []

async function settle() {
  for (let i = 0; i < 8; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

async function mount(perms: string[]) {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().$patch({ permissionsLoaded: true, permissionCodes: perms })
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({
    render: () =>
      h(NMessageProvider, null, {
        default: () => h(NDialogProvider, null, { default: () => h(UserPage) }),
      }),
  })
  app.use(pinia)
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.config.warnHandler = msg => warnings.push(msg)
  app.mount(host)
  await settle()
}

const q = (sel: string) => document.body.querySelector<HTMLElement>(sel)
const moreItems = () =>
  [...document.body.querySelectorAll('.more-item')].map(b => b.textContent?.trim())
const overviewShown = () => q('.overview')?.getAttribute('data-show')
async function click(sel: string) {
  q(sel)!.click()
  await settle()
}
const openOverview = async () => {
  const item = [...document.body.querySelectorAll<HTMLElement>('.more-item')].find(
    b => b.textContent?.trim() === '单独授权一览',
  )!
  item.click()
  await settle()
}

beforeEach(() => {
  warnings = []
  for (const spy of Object.values(spies)) spy.mockReset()
  spies.orgList.mockResolvedValue([])
  layout.width.value = 0
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
  localStorage.clear()
})

describe('用户页 单独授权一览入口', () => {
  it('有一览的权限码:「更多」里出现「单独授权一览」,点它打开抽屉', async () => {
    await mount([OVERVIEW_PERM])
    expect(moreItems()).toEqual(['单独授权一览'])
    expect(overviewShown()).toBe('false')

    await openOverview()
    expect(overviewShown()).toBe('true')
  })

  it('没有一览的权限码:「更多」里没有这一项', async () => {
    await mount([])
    expect(moreItems()).toEqual([])
  })

  it('抽屉里点「去调整」,把用户交给授权菜单弹窗', async () => {
    await mount([OVERVIEW_PERM])
    await openOverview()
    await click('.overview-adjust')
    expect(spies.sheetOpen).toHaveBeenCalledTimes(1)
    expect(spies.sheetOpen).toHaveBeenCalledWith({ id: 7, name: '张三' })
  })
})

describe('用户页 授权弹窗保存后的刷新', () => {
  it('一览抽屉打开着:用户表和一览都刷新一次', async () => {
    await mount([OVERVIEW_PERM])
    await openOverview()

    await click('.sheet-save')
    expect(spies.tableRefresh).toHaveBeenCalledTimes(1)
    expect(spies.overviewRefresh).toHaveBeenCalledTimes(1)
  })

  it('一览抽屉没打开过:只刷新用户表,不碰一览', async () => {
    await mount([OVERVIEW_PERM])

    await click('.sheet-save')
    expect(spies.tableRefresh).toHaveBeenCalledTimes(1)
    expect(spies.overviewRefresh).not.toHaveBeenCalled()
  })

  it('一览抽屉打开后又关上:保存只刷新用户表', async () => {
    await mount([OVERVIEW_PERM])
    await openOverview()
    await click('.overview-close')
    expect(overviewShown()).toBe('false')

    await click('.sheet-save')
    expect(spies.tableRefresh).toHaveBeenCalledTimes(1)
    expect(spies.overviewRefresh).not.toHaveBeenCalled()
  })

  it('页面渲染没有未注册组件的警告', async () => {
    await mount([OVERVIEW_PERM])
    expect(warnings.filter(w => w.includes('Failed to resolve component'))).toEqual([])
  })
})

// ── 左侧机构分组栏 ──
// 总公司(1) ─ 技术部(2) ─ 前端组(4)
//           └ 产品部(3)
const org = (id: number, parentId: number, name: string) => ({
  id,
  parentId,
  name,
  code: `c${id}`,
})
const THREE_LEVELS = [
  org(1, 0, '总公司'),
  org(2, 1, '技术部'),
  org(3, 1, '产品部'),
  org(4, 2, '前端组'),
]
/** n 个节点的平铺机构:一个根 + (n-1) 个直属子节点,只有两层 */
const flatOrgs = (n: number) => [
  org(1, 0, '总公司'),
  ...Array.from({ length: n - 1 }, (_, i) => org(i + 2, 1, `部门${i + 2}`)),
]
const treeLabels = () =>
  [...document.body.querySelectorAll('.side-tree .n-tree-node-content__text')].map(e =>
    e.textContent?.trim(),
  )
// 处于展开态的节点名。收起时 NTree 走离场过渡,happy-dom 里过渡不结束,被收起的子节点还留在 DOM 里,
// 所以「收起」要看展开态本身,不能看还剩哪些文字。
const expandedLabels = () =>
  [...document.body.querySelectorAll('.side-tree .n-tree-node-switcher--expanded')].map(e =>
    e.closest('.n-tree-node')?.querySelector('.n-tree-node-content__text')?.textContent?.trim(),
  )
const HIDDEN_KEY = 'sa-user-org-panel-hidden'

describe('用户页 机构分组栏', () => {
  it('默认只展开一级:根展开露出第二层,更深的层级收起', async () => {
    spies.orgList.mockResolvedValue(THREE_LEVELS)
    await mount([])
    expect(treeLabels()).toEqual(['总公司', '技术部', '产品部'])
    expect(expandedLabels()).toEqual(['总公司'])
  })

  it('头部「展开全部」在「全部展开」与「折叠到一级」之间切换', async () => {
    spies.orgList.mockResolvedValue(THREE_LEVELS)
    await mount([])

    await click('.side-filter__toggle')
    expect(expandedLabels()).toEqual(['总公司', '技术部'])
    expect(treeLabels()).toEqual(['总公司', '技术部', '前端组', '产品部'])

    await click('.side-filter__toggle')
    expect(expandedLabels()).toEqual(['总公司'])
  })

  it('没有比第一层更深的层级时,不出「展开全部」按钮', async () => {
    spies.orgList.mockResolvedValue(flatOrgs(3))
    await mount([])
    expect(q('.side-filter__toggle')).toBeNull()
  })

  it('机构数不超过 15 时没有搜索框', async () => {
    spies.orgList.mockResolvedValue(flatOrgs(15))
    await mount([])
    expect(q('.side-filter input')).toBeNull()
  })

  it('机构数超过 15 时出现搜索框', async () => {
    spies.orgList.mockResolvedValue(flatOrgs(16))
    await mount([])
    expect(q('.side-filter input')).not.toBeNull()
  })

  it('点「收起」:机构栏收起且不可聚焦,工具栏最左出恢复按钮,状态记进 localStorage', async () => {
    spies.orgList.mockResolvedValue(THREE_LEVELS)
    await mount([])
    expect(q('.side-filter.is-hidden')).toBeNull()
    expect(q('.org-trigger')).toBeNull()

    await click('.side-filter__hide')
    expect(q('.side-filter.is-hidden')).not.toBeNull()
    expect(q('.side-filter')!.hasAttribute('inert')).toBe(true)
    expect(q('.org-trigger')).not.toBeNull()
    expect(localStorage.getItem(HIDDEN_KEY)).toBe('true')
  })

  it('点恢复按钮:机构栏回来,恢复按钮消失', async () => {
    spies.orgList.mockResolvedValue(THREE_LEVELS)
    await mount([])
    await click('.side-filter__hide')

    await click('.org-trigger')
    expect(q('.side-filter.is-hidden')).toBeNull()
    expect(q('.side-filter')!.hasAttribute('inert')).toBe(false)
    expect(q('.org-trigger')).toBeNull()
  })

  it('窄屏机构树进抽屉:抽屉里没有分组小标题与「收起」按钮,只留「展开全部」', async () => {
    layout.width.value = 800
    spies.orgList.mockResolvedValue(THREE_LEVELS)
    await mount([])
    expect(q('.side-filter')).toBeNull()

    await click('.org-trigger')
    expect(q('.n-drawer .side-tree')).not.toBeNull()
    expect(q('.n-drawer .side-filter__title')).toBeNull()
    expect(q('.n-drawer .side-filter__hide')).toBeNull()
    expect(q('.n-drawer .side-filter__toggle')).not.toBeNull()
  })

  it('上次收起过:再进页面机构栏仍是收起的,恢复按钮上显示当前机构筛选', async () => {
    localStorage.setItem(HIDDEN_KEY, 'true')
    spies.orgList.mockResolvedValue(THREE_LEVELS)
    await mount([])
    expect(q('.side-filter.is-hidden')).not.toBeNull()
    expect(q('.org-trigger')!.textContent).toContain('机构')

    // 收起期间选中的机构名要看得见:筛选还在生效,不能让人看不出
    q('.side-tree .n-tree-node-content')!.click()
    await settle()
    expect(q('.org-trigger')!.textContent).toContain('总公司')
  })
})
