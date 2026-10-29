import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, type App } from 'vue'
import { createI18n } from 'vue-i18n'
import { createPinia, setActivePinia } from 'pinia'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import phSubset from '#/assets/icons/ph-subset.json'
import zhCN from '#/locales/zh-CN'

const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const router = vi.hoisted(() => ({ replace: vi.fn() }))

vi.mock('vue-router', async importOriginal => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => route,
  useRouter: () => router,
}))
vi.mock('#/router', () => ({ resetRouter: vi.fn() }))

import BootError from './BootError.vue'

// 图标走离线子集,否则 Iconify 会发网络请求、在 teardown 时被中止
addCollection(phSubset as IconifyJSON)

let app: App<Element> | undefined

function mount(redirect: string): HTMLElement {
  route.query = { redirect }
  const host = document.createElement('div')
  document.body.append(host)
  app = createApp(BootError)
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.mount(host)
  return host
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  vi.useFakeTimers()
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
  vi.useRealTimers()
})

describe('启动错误页', () => {
  it('整屏页每 30 秒自动重试原地址,并提示会自动重试', () => {
    const host = mount('/fullscreen/demo/board?line=A')
    expect(host.textContent).toContain('每 30 秒自动重试')

    vi.advanceTimersByTime(29_999)
    expect(router.replace).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(router.replace).toHaveBeenCalledWith('/fullscreen/demo/board?line=A')
    vi.advanceTimersByTime(30_000)
    expect(router.replace).toHaveBeenCalledTimes(2)
  })

  it('离开错误页后不再重试', () => {
    mount('/fullscreen/demo/board')
    app!.unmount()
    app = undefined
    vi.advanceTimersByTime(120_000)
    expect(router.replace).not.toHaveBeenCalled()
  })

  it('普通页面不自动重试,也不出提示', () => {
    const host = mount('/system/user')
    expect(host.textContent).not.toContain('自动重试')
    vi.advanceTimersByTime(120_000)
    expect(router.replace).not.toHaveBeenCalled()
  })
})
