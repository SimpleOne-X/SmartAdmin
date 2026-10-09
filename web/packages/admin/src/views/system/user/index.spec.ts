import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import zhCN from '#/locales/zh-CN'
import phSubset from '#/assets/icons/ph-subset.json'

// 只验证用户页把「单独授权一览」接起来的那几根线:入口、授权弹窗保存后的刷新、「去调整」。
// 页面自己的大块(列表列、机构树、新增编辑)不在这里测,所以表格与各子弹窗都换成桩。
const spies = vi.hoisted(() => ({
  tableRefresh: vi.fn(),
  overviewRefresh: vi.fn(),
  sheetOpen: vi.fn(),
}))

// 页面在 watch 里读路由;路径不是 /system/user 时它直接返回,不去预置角色条件。
vi.mock('vue-router', async importOriginal => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => ({ path: '/spec', query: {} }),
}))

vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  mfaApi: { clear: async () => true },
  positionApi: { page: async () => ({ items: [], total: 0 }) },
  roleApi: { page: async () => ({ items: [], total: 0 }) },
  orgApi: { list: async () => [] },
  userApi: { page: async () => ({ items: [], total: 0 }) },
}))

// 表格桩:把工具栏「更多」里的项渲染成按钮,点了就发 more-select;刷新计数。
vi.mock('smart-naive-table', async importOriginal => {
  const { defineComponent, h: el } = await import('vue')
  return {
    ...(await importOriginal<typeof import('smart-naive-table')>()),
    SmartTable: defineComponent({
      props: { toolbar: { type: Object, default: () => ({}) } },
      emits: ['more-select'],
      setup(props, { emit, expose }) {
        expose({ refresh: spies.tableRefresh })
        return () =>
          el(
            'div',
            { class: 'stub-table' },
            ((props.toolbar as { more?: { label: string; key: string }[] }).more ?? []).map(o =>
              el(
                'button',
                { class: 'more-item', onClick: () => emit('more-select', o.key) },
                o.label,
              ),
            ),
          )
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
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
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
