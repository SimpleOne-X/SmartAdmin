import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createI18n } from 'vue-i18n'
import { NMessageProvider } from 'naive-ui'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import zhCN from '#/locales/zh-CN'
import phSubset from '#/assets/icons/ph-subset.json'
import { NoticeType } from '#/types/api'

// 弹层换成直接渲染面板内容的桩,另给一个按钮模拟"点开铃铛"。
vi.mock('naive-ui', async importOriginal => {
  const { defineComponent, h: el } = await import('vue')
  return {
    ...(await importOriginal<typeof import('naive-ui')>()),
    NPopover: defineComponent({
      emits: ['update:show'],
      setup(_, { slots, emit }) {
        return () =>
          el('div', [
            el('button', { class: 'open-stub', onClick: () => emit('update:show', true) }),
            slots.default?.(),
          ])
      },
    }),
  }
})
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))
vi.mock('#/composables/useNoticeAction', () => ({
  useNoticeAction: () => ({ run: vi.fn(), buttonType: () => 'default', label: () => '' }),
}))
vi.mock('#/components/MarkdownEditor/MarkdownView.vue', () => ({ default: { render: () => null } }))
vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  noticeApi: { unreadCount: vi.fn(), mine: vi.fn(), markRead: vi.fn(), markAllRead: vi.fn() },
}))

import { noticeApi } from '#/api'
import NoticeBell from './NoticeBell.vue'

// 图标走离线子集,否则 Iconify 会去请求公网。
addCollection(phSubset as IconifyJSON)

const unreadMock = vi.mocked(noticeApi.unreadCount)
const mineMock = vi.mocked(noticeApi.mine)
const markAllMock = vi.mocked(noticeApi.markAllRead)

const MESSAGE = [NoticeType.Message]
const NOTICE = [NoticeType.Notice, NoticeType.Announcement]

let app: App<Element> | undefined

// 未读数按 types 分别给:无 types 是总数,其余按页签。
function setUnread(message: number, notice: number) {
  unreadMock.mockImplementation(async types =>
    !types ? message + notice : types.includes(NoticeType.Message) ? message : notice,
  )
}

async function settle() {
  for (let i = 0; i < 8; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

async function open() {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({ render: () => h(NMessageProvider, () => h(NoticeBell)) })
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.mount(host)
  await settle()
  document.body.querySelector<HTMLButtonElement>('.open-stub')!.click()
  await settle()
}

const lastMineTypes = () => mineMock.mock.calls.at(-1)![0].types
const button = (text: string) =>
  [...document.body.querySelectorAll<HTMLButtonElement>('button')].find(b =>
    b.textContent?.includes(text),
  )!

beforeEach(() => {
  unreadMock.mockReset()
  mineMock.mockReset()
  markAllMock.mockReset()
  mineMock.mockResolvedValue({ items: [], total: 0 })
  markAllMock.mockResolvedValue(true)
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('NoticeBell 页签', () => {
  it('打开时停在第一个有未读的页签', async () => {
    setUnread(0, 2)
    await open()
    expect(lastMineTypes()).toEqual(NOTICE)
  })

  it('两个页签都有未读时停在消息页签', async () => {
    setUnread(1, 2)
    await open()
    expect(lastMineTypes()).toEqual(MESSAGE)
  })

  it('都没有未读时停在消息页签,全部已读不可点', async () => {
    setUnread(0, 0)
    await open()
    expect(lastMineTypes()).toEqual(MESSAGE)
    expect(button('全部已读').disabled).toBe(true)
  })

  it('全部已读只标当前页签的类型', async () => {
    setUnread(0, 3)
    await open()
    button('全部已读').click()
    await settle()
    expect(markAllMock).toHaveBeenCalledWith(NOTICE)
  })
})
