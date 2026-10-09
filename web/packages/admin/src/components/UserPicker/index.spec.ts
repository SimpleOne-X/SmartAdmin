import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { NMessageProvider } from 'naive-ui'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import zhCN from '#/locales/zh-CN'
import phSubset from '#/assets/icons/ph-subset.json'
import type { UserItem } from '#/types/api'

// translateError 要用 ApiError 做 instanceof 判断,mock 里得有它。
vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  orgApi: { list: vi.fn() },
  userApi: { page: vi.fn() },
}))

import { orgApi, userApi } from '#/api'
import UserPicker from './index.vue'

// 图标走离线子集,否则 Iconify 会去请求公网。
addCollection(phSubset as IconifyJSON)

const pageMock = vi.mocked(userApi.page)
const orgListMock = vi.mocked(orgApi.list)

function user(id: number, name: string, account = `acc${id}`): UserItem {
  return {
    id,
    account,
    name,
    orgName: '技术部',
    enabled: true,
    isSuperAdmin: false,
    createTime: '',
  }
}

const USERS = [user(1, '张三', 'zhangsan'), user(2, '李四', 'lisi'), user(3, '王五', 'wangwu')]

let app: App<Element> | undefined
let picker: InstanceType<typeof UserPicker>
const confirmed: number[][] = []

function mount(props: Record<string, unknown> = {}) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({
    render: () =>
      h(NMessageProvider, null, {
        default: () =>
          h(UserPicker, {
            ref: (r: unknown) => (picker = r as InstanceType<typeof UserPicker>),
            onConfirm: (ids: number[]) => confirmed.push(ids),
            ...props,
          }),
      }),
  })
  app.use(createPinia())
  app.use(
    createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: { 'zh-CN': zhCN },
    }),
  )
  app.mount(host)
}

/** 等 open() 里的异步取数、渲染落定。 */
async function settle() {
  for (let i = 0; i < 5; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

const q = <T extends Element = HTMLElement>(sel: string) => document.body.querySelector<T>(sel)
const qa = (sel: string) => [...document.body.querySelectorAll<HTMLElement>(sel)]

beforeEach(() => {
  confirmed.length = 0
  orgListMock.mockResolvedValue([])
  pageMock.mockResolvedValue({ items: USERS, total: 3 })
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
  vi.useRealTimers()
})

describe('UserPicker 选择与回显', () => {
  it('打开后列出可选用户,点整行即选中,右栏同步出现一条', async () => {
    mount()
    picker.open()
    await settle()

    const rows = qa('.user-row')
    expect(rows).toHaveLength(3)
    expect(qa('.selected-item')).toHaveLength(0)

    rows[0]!.click()
    await nextTick()

    expect(qa('.selected-item')).toHaveLength(1)
    expect(qa('.user-row')[0]!.getAttribute('aria-checked')).toBe('true')
  })

  it('再点一次已选行即取消选择', async () => {
    mount()
    picker.open()
    await settle()

    qa('.user-row')[1]!.click()
    await nextTick()
    qa('.user-row')[1]!.click()
    await nextTick()

    expect(qa('.selected-item')).toHaveLength(0)
    expect(qa('.user-row')[1]!.getAttribute('aria-checked')).toBe('false')
  })

  it('open(ids) 按 id 回显已选成员', async () => {
    mount()
    picker.open([2, 3])
    await settle()

    const names = qa('.selected-item').map(el => el.textContent ?? '')
    expect(names).toHaveLength(2)
    expect(names[0]).toContain('李四')
    expect(names[1]).toContain('王五')
    expect(qa('.user-row')[1]!.getAttribute('aria-checked')).toBe('true')
  })

  it('excludeIds 里的用户整行置灰,点了也不会被选中', async () => {
    mount({ excludeIds: [1] })
    picker.open()
    await settle()

    const first = qa('.user-row')[0]!
    expect(first.classList.contains('is-disabled')).toBe(true)
    first.click()
    await nextTick()
    expect(qa('.selected-item')).toHaveLength(0)
  })

  it('右栏的 ✕ 移除单个,清空移除全部', async () => {
    mount()
    picker.open([1, 2, 3])
    await settle()
    expect(qa('.selected-item')).toHaveLength(3)

    qa('.selected-item')[0]!.querySelector('button')!.click()
    await nextTick()
    expect(qa('.selected-item')).toHaveLength(2)

    q('.selected-clear')!.click()
    await nextTick()
    expect(qa('.selected-item')).toHaveLength(0)
  })

  it('全选本页:加上本页没选的人;全都选了再点就是取消本页', async () => {
    mount({ excludeIds: [3] })
    picker.open([1])
    await settle()

    q('.page-toggle')!.click()
    await nextTick()
    // 1 已选、2 新加、3 在排除名单里不加
    expect(qa('.selected-item')).toHaveLength(2)

    q('.page-toggle')!.click()
    await nextTick()
    expect(qa('.selected-item')).toHaveLength(0)
  })

  it('确认时按已选顺序发出 id 数组', async () => {
    mount()
    picker.open([3])
    await settle()
    qa('.user-row')[0]!.click()
    await nextTick()

    const confirmBtn = qa('.n-button--primary-type').find(b => b.closest('.n-card__footer'))!
    confirmBtn.click()
    await settle()

    expect(confirmed).toEqual([[3, 1]])
  })
})

describe('UserPicker 搜索与分页', () => {
  it('输入停顿后才查询,默认按姓名,并回到第 1 页', async () => {
    vi.useFakeTimers()
    mount()
    picker.open()
    await settle()
    pageMock.mockClear()

    const input = q<HTMLInputElement>('.picker-search input')!
    input.value = '张'
    input.dispatchEvent(new Event('input'))
    await nextTick()
    expect(pageMock).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(400)
    expect(pageMock).toHaveBeenCalledTimes(1)
    expect(pageMock.mock.calls[0]![0]).toMatchObject({ page: 1, name: '张', account: undefined })
  })

  it('翻页带着当前页码请求,「共 N 人」取自接口 total', async () => {
    pageMock.mockResolvedValue({ items: USERS, total: 45 })
    mount()
    picker.open()
    await settle()
    expect(q('.picker-total')!.textContent).toContain('45')

    pageMock.mockClear()
    q('.pager-next')!.click()
    await settle()
    expect(pageMock.mock.calls[0]![0]).toMatchObject({ page: 2 })
  })

  it('取数失败时列表为空而不是抛出', async () => {
    pageMock.mockRejectedValue(new Error('boom'))
    mount()
    picker.open()
    await settle()
    expect(qa('.user-row')).toHaveLength(0)
  })
})
