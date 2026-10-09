import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, nextTick, ref, type App, type PropType } from 'vue'
import { createI18n } from 'vue-i18n'
import { NMessageProvider } from 'naive-ui'
import zhCN from '#/locales/zh-CN'
import { UserMenuEffect, UserMenuGrantStatus, type UserMenuGrantPageItem } from '#/types/api'

// SmartTable 在 happy-dom 里没有布局,虚拟滚动渲染不出行;换成最小桩:挂载时调一次 fetcher,
// 成功就按列定义逐格渲染,失败就 emit('error'),并把 #pagination-prefix 插槽渲染出来。
// 列定义存下来,用来检查「搜索列的 key 就是接口的参数名」这条接缝。
interface StubColumn {
  key?: string
  type?: string
  hideInTable?: boolean
  search?: unknown
  render?: (row: Record<string, unknown>) => unknown
}
const captured = vi.hoisted(() => ({ columns: [] as StubColumn[] }))
vi.mock('smart-naive-table', async importOriginal => ({
  ...(await importOriginal<typeof import('smart-naive-table')>()),
  SmartTable: defineComponent({
    props: {
      columns: { type: Array as PropType<StubColumn[]>, required: true },
      fetcher: {
        type: Function as PropType<
          (p: { page: number; pageSize: number }) => Promise<{
            items: Record<string, unknown>[]
            total: number
          }>
        >,
        required: true,
      },
    },
    emits: ['error'],
    setup(props, { emit, slots }) {
      captured.columns = props.columns
      const rows = ref<Record<string, unknown>[]>([])
      const total = ref(0)
      props.fetcher({ page: 1, pageSize: 20 }).then(
        r => {
          rows.value = r.items
          total.value = r.total
        },
        e => emit('error', e),
      )
      return () =>
        h('div', { class: 'stub-table' }, [
          ...rows.value.map(row =>
            h(
              'div',
              { class: 'row' },
              props.columns
                .filter(col => col.key && !col.hideInTable)
                .map(col =>
                  h(
                    'span',
                    { class: `cell cell-${col.key}` },
                    (col.render ? col.render(row) : (row[col.key!] ?? '')) as string,
                  ),
                ),
            ),
          ),
          h('div', { class: 'prefix' }, slots['pagination-prefix']?.({ itemCount: total.value })),
        ])
    },
  }),
}))

// translateError 要用 ApiError 做 instanceof 判断,mock 里得有它。
vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  userApi: { menuGrantPage: vi.fn() },
}))

import { userApi } from '#/api'
import { flatFilterSerializer } from '#/utils/tableFilter'
import UserGrantOverviewDrawer from './UserGrantOverviewDrawer.vue'

const pageMock = vi.mocked(userApi.menuGrantPage)

const item = (over: Partial<UserMenuGrantPageItem> = {}): UserMenuGrantPageItem => ({
  id: 1,
  userId: 7,
  userAccount: 'zhangsan',
  userName: '张三',
  menuId: 110,
  menuTitle: '订单',
  moduleId: 1,
  moduleTitle: '业务应用',
  effect: UserMenuEffect.Allow,
  expireTime: '2099-01-01T08:30:00',
  status: UserMenuGrantStatus.Active,
  grantorId: 2,
  grantorName: '李管理',
  grantTime: '2026-10-09T09:00:00',
  remark: '临时对账',
  ...over,
})

let app: App<Element> | undefined
let warnings: string[] = []
let adjusted: { id: number; name: string }[] = []

function mount() {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const show = ref(true)
  app = createApp({
    render: () =>
      h(NMessageProvider, null, {
        default: () =>
          h(UserGrantOverviewDrawer, {
            show: show.value,
            'onUpdate:show': (v: boolean) => (show.value = v),
            onAdjust: (u: { id: number; name: string }) => adjusted.push(u),
          }),
      }),
  })
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.config.warnHandler = msg => warnings.push(msg)
  app.mount(host)
}

/** 等取数、渲染落定。 */
async function settle() {
  for (let i = 0; i < 8; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

const qa = (sel: string, root: ParentNode = document.body) => [
  ...root.querySelectorAll<HTMLElement>(sel),
]
const rows = () => qa('.stub-table .row')
const cell = (row: Element, key: string) => row.querySelector(`.cell-${key}`)?.textContent
/** 条件构造器里某一列的单个条件。 */
const cond = (action: string, value: unknown) => ({
  logic: 'and' as const,
  conditions: [{ action, value }],
})

async function openDrawer(items: UserMenuGrantPageItem[]) {
  pageMock.mockResolvedValue({ items, total: items.length })
  mount()
  await settle()
}

beforeEach(() => {
  warnings = []
  adjusted = []
  captured.columns = []
  pageMock.mockReset()
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('UserGrantOverviewDrawer 渲染', () => {
  it('标题、共 N 条、每条授权一行,模板里没有未注册的组件', async () => {
    await openDrawer([item(), item({ id: 2, userId: 8, userName: '王五', userAccount: 'wangwu' })])

    expect(document.body.textContent).toContain('单独授权一览')
    expect(rows()).toHaveLength(2)
    expect(qa('.stub-table .prefix')[0]!.textContent).toBe('共 2 条')
    expect(pageMock).toHaveBeenCalledTimes(1)
    expect(warnings.filter(w => w.includes('Failed to resolve component'))).toEqual([])
  })

  it('用户带账号,菜单带应用前缀,没有应用标题就只显示菜单', async () => {
    await openDrawer([item(), item({ id: 2, moduleTitle: null })])
    const [first, second] = rows()
    expect(cell(first!, 'userName')).toBe('张三(zhangsan)')
    expect(cell(first!, 'menuTitle')).toBe('业务应用 · 订单')
    expect(cell(second!, 'menuTitle')).toBe('订单')
  })

  it('效果、有效期状态、到期、授权人、授权理由', async () => {
    await openDrawer([
      item(),
      item({
        id: 2,
        effect: UserMenuEffect.Deny,
        status: UserMenuGrantStatus.Expiring,
        expireTime: null,
        grantorName: null,
        remark: null,
      }),
      item({ id: 3, status: UserMenuGrantStatus.Expired }),
    ])
    const [allow, deny, expired] = rows()
    expect(cell(allow!, 'effect')).toBe('允许')
    expect(cell(allow!, 'status')).toBe('生效中')
    expect(cell(allow!, 'expireTime')).toBe('2099-01-01 08:30:00')
    expect(cell(allow!, 'grantorName')).toBe('李管理')
    expect(cell(allow!, 'remark')).toBe('临时对账')

    expect(cell(deny!, 'effect')).toBe('拒绝')
    expect(cell(deny!, 'status')).toBe('7 天内到期')
    // 长期没有到期时间;没有授权人、没有理由都是破折号
    expect(cell(deny!, 'expireTime')).toBe('长期')
    expect(cell(deny!, 'grantorName')).toBe('—')
    expect(cell(deny!, 'remark')).toBe('—')

    expect(cell(expired!, 'status')).toBe('已过期')
  })
})

describe('UserGrantOverviewDrawer 去调整', () => {
  it('点「去调整」把该行用户的 id 与姓名交给父页', async () => {
    await openDrawer([item(), item({ id: 2, userId: 8, userName: '王五', userAccount: 'wangwu' })])
    const button = qa('.n-button', rows()[1]).find(b => b.textContent?.trim() === '去调整')!
    button.click()
    await settle()
    expect(adjusted).toEqual([{ id: 8, name: '王五' }])
  })
})

describe('UserGrantOverviewDrawer 取数失败', () => {
  it('弹出错误提示,抽屉与表格仍在,不抛未处理异常', async () => {
    pageMock.mockRejectedValue(new Error('网络开小差了'))
    mount()
    await settle()

    expect(qa('.n-message').map(m => m.textContent)).toContain('网络开小差了')
    expect(qa('.stub-table')).toHaveLength(1)
    expect(rows()).toHaveLength(0)
    expect(warnings).toEqual([])
  })
})

describe('UserGrantOverviewDrawer 搜索列', () => {
  it('搜索列的 key 就是 menuGrantPage 的参数名,序列化后原样交给它', async () => {
    await openDrawer([item()])
    const searchKeys = captured.columns.filter(c => c.search).map(c => c.key)
    expect(searchKeys.toSorted()).toEqual(['effect', 'grantor', 'status', 'user'])

    // 条件构造器的过滤态经全局默认序列化器摊平后,键就是列 key,也就是接口参数名
    const flat = flatFilterSerializer({
      user: cond('contains', '张'),
      grantor: cond('contains', '李'),
      effect: cond('equal', UserMenuEffect.Deny),
      status: cond('equal', UserMenuGrantStatus.Expiring),
    } as never)
    expect(flat).toEqual({
      user: '张',
      grantor: '李',
      effect: UserMenuEffect.Deny,
      status: UserMenuGrantStatus.Expiring,
    })
  })

  it('用户、授权人两个搜索列只作搜索项,不进表格', async () => {
    await openDrawer([item()])
    const hidden = captured.columns.filter(c => c.hideInTable).map(c => c.key)
    expect(hidden).toEqual(['user', 'grantor'])
  })
})
