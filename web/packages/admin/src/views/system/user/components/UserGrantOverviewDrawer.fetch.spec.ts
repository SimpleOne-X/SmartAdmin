import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import { createI18n } from 'vue-i18n'
import { NMessageProvider } from 'naive-ui'
import zhCN from '#/locales/zh-CN'
import type { UserMenuGrantPageItem } from '#/types/api'

// 这份用真的 SmartTable:取数、竞态、失败提示都是表格与抽屉接在一起之后才有的行为。
// happy-dom 没有布局,虚拟滚动渲染不出行,所以只看分页栏上的「共 N 条」(它跟着最近一次成功的响应走)。
// 行与列的渲染见 UserGrantOverviewDrawer.spec.ts(那份把 SmartTable 换成了桩)。

// translateError 要用 ApiError 做 instanceof 判断,mock 里得有它。
vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  userApi: { menuGrantPage: vi.fn() },
}))

import { userApi } from '#/api'
import UserGrantOverviewDrawer from './UserGrantOverviewDrawer.vue'

const pageMock = vi.mocked(userApi.menuGrantPage)
type Page = { items: UserMenuGrantPageItem[]; total: number }

function deferred() {
  let resolve!: (v: Page) => void
  const promise = new Promise<Page>(r => (resolve = r))
  return { promise, resolve }
}

let app: App<Element> | undefined
let warnings: string[] = []

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
          }),
      }),
  })
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.config.warnHandler = msg => warnings.push(msg)
  app.mount(host)
}

async function settle() {
  for (let i = 0; i < 8; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

const qa = (sel: string) => [...document.body.querySelectorAll<HTMLElement>(sel)]
const totalText = () => qa('.table-total')[0]?.textContent
/** 分页栏的「下一页」:简洁分页里第二个按钮形态的页码项(第一个是「上一页」)。 */
const nextPage = () => qa('.n-pagination-item--button')[1]!
/** 工具栏的刷新图标。翻页期间分页栏是禁用的,刷新钮不会被禁用,所以「请求在途时再发一次」只能从它来。 */
const refreshButton = () =>
  qa('button[aria-label]').find(b => /^(刷新|Refresh)$/.test(b.getAttribute('aria-label')!))!

beforeEach(() => {
  warnings = []
  // 清掉上一条用例没消费完的 mockXxxOnce 队列,否则它会漏进下一条
  pageMock.mockReset()
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('UserGrantOverviewDrawer 取数', () => {
  it('打开就按第 1 页取一次,「共 N 条」取后端的 total,模板里没有未注册的组件', async () => {
    pageMock.mockResolvedValue({ items: [], total: 250 })
    mount()
    await settle()

    expect(pageMock).toHaveBeenCalledTimes(1)
    expect(pageMock.mock.calls[0]![0]).toMatchObject({ page: 1 })
    expect(totalText()).toBe('共 250 条')
    expect(warnings.filter(w => w.includes('Failed to resolve component'))).toEqual([])
  })

  it('翻页的请求还在途中又点刷新,慢的旧响应晚到,不覆盖最新一次', async () => {
    const paging = deferred()
    const refreshing = deferred()
    pageMock
      .mockResolvedValueOnce({ items: [], total: 300 })
      .mockReturnValueOnce(paging.promise)
      .mockReturnValueOnce(refreshing.promise)
    mount()
    await settle()
    expect(totalText()).toBe('共 300 条')

    nextPage().click()
    await settle()
    refreshButton().click()
    await settle()
    expect(pageMock.mock.calls.map(c => c[0].page)).toEqual([1, 2, 2])

    refreshing.resolve({ items: [], total: 333 })
    await settle()
    expect(totalText()).toBe('共 333 条')

    // 翻页那次的响应最后才到:不能把总数改回去
    paging.resolve({ items: [], total: 222 })
    await settle()
    expect(totalText()).toBe('共 333 条')
  })

  it('取数失败:弹出错误提示,抽屉与分页栏仍在,不抛未处理异常', async () => {
    pageMock.mockRejectedValue(new Error('网络开小差了'))
    mount()
    await settle()

    expect(qa('.n-message').map(m => m.textContent)).toContain('网络开小差了')
    expect(qa('.n-drawer')).toHaveLength(1)
    expect(totalText()).toBe('共 0 条')
    expect(warnings).toEqual([])
  })
})
