import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, type App } from 'vue'
import { createI18n } from 'vue-i18n'
import { createPinia, setActivePinia } from 'pinia'
import type { AppModule } from '#/types/menu'
import zhCN from '#/locales/zh-CN'

const route = vi.hoisted(() => ({ path: '/workbench', fullPath: '/workbench?tab=1' }))
const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }))
const module_ = vi.hoisted(() => ({ findOwnerModule: vi.fn(), switchModule: vi.fn() }))
const message = vi.hoisted(() => ({ error: vi.fn() }))

vi.mock('vue-router', async importOriginal => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => route,
  useRouter: () => router,
}))
vi.mock('#/composables/useModule', () => ({ useModule: () => module_ }))
vi.mock('naive-ui', async importOriginal => ({
  ...(await importOriginal<typeof import('naive-ui')>()),
  useMessage: () => message,
}))

import NotFound from './404.vue'
import { useAuthStore } from '#/stores/auth'

function mod(id: number, title: string): AppModule {
  return { id, code: `m${id}`, title, sort: 0 }
}

let app: App<Element> | undefined

function mount(): HTMLElement {
  const host = document.createElement('div')
  document.body.append(host)
  app = createApp(NotFound)
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.mount(host)
  return host
}

const buttons = (host: HTMLElement) => [...host.querySelectorAll('button')]
const buttonWith = (host: HTMLElement, text: string) =>
  buttons(host).find(b => b.textContent?.includes(text))

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  Object.assign(route, { path: '/workbench', fullPath: '/workbench?tab=1' })
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('404 页', () => {
  it('只有一个应用:没有"别的应用"可言,不反查、不提应用名、不给切换入口', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1, '系统')]
    auth.currentModuleId = 1

    const host = mount()
    await vi.waitFor(() => expect(host.textContent).toContain('页面不存在'))

    expect(module_.findOwnerModule).not.toHaveBeenCalled()
    expect(host.textContent).not.toContain('当前应用')
    expect(buttonWith(host, '切换应用')).toBeUndefined()
  })

  it('多个应用、地址确实不存在:提示当前在哪个应用,并给切换应用的入口', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1, '系统'), mod(2, '业务中心')]
    auth.currentModuleId = 2
    module_.findOwnerModule.mockResolvedValue(null)

    const host = mount()
    await vi.waitFor(() => expect(module_.findOwnerModule).toHaveBeenCalledWith('/workbench'))

    expect(host.textContent).toContain('页面不存在')
    expect(host.textContent).toContain('当前应用：业务中心')
    expect(host.textContent).not.toContain('属于')

    buttonWith(host, '切换应用')!.click()
    expect(router.push).toHaveBeenCalledWith('/module')
  })

  it('地址属于另一个应用:说明它属于哪个应用,一键切过去并打开原地址(含查询串)', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1, '系统'), mod(2, '业务中心')]
    auth.currentModuleId = 2
    module_.findOwnerModule.mockResolvedValue(auth.modules[0])
    module_.switchModule.mockResolvedValue(undefined)

    const host = mount()
    await vi.waitFor(() => expect(host.textContent).toContain('该页面属于「系统」应用'))
    expect(host.textContent).toContain('当前应用：业务中心')

    buttonWith(host, '切换到「系统」并打开')!.click()

    await vi.waitFor(() => expect(module_.switchModule).toHaveBeenCalledWith(1, '/workbench?tab=1'))
    expect(message.error).not.toHaveBeenCalled()
  })

  it('一键切换失败:弹出错误,按钮恢复可点,不卡在加载态', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1, '系统'), mod(2, '业务中心')]
    auth.currentModuleId = 2
    module_.findOwnerModule.mockResolvedValue(auth.modules[0])
    module_.switchModule.mockRejectedValue(new Error('boom'))

    const host = mount()
    await vi.waitFor(() => expect(buttonWith(host, '切换到「系统」并打开')).toBeDefined())
    buttonWith(host, '切换到「系统」并打开')!.click()

    await vi.waitFor(() => expect(message.error).toHaveBeenCalledTimes(1))
    await vi.waitFor(() =>
      expect(
        buttonWith(host, '切换到「系统」并打开')!.classList.contains('n-button--loading'),
      ).toBe(false),
    )
  })

  it('"返回工作台"始终可用,回当前应用首页', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1, '系统'), mod(2, '业务中心')]
    auth.currentModuleId = 2
    module_.findOwnerModule.mockResolvedValue(auth.modules[0])

    const host = mount()
    await vi.waitFor(() => expect(host.textContent).toContain('该页面属于'))
    buttonWith(host, '返回工作台')!.click()

    expect(router.replace).toHaveBeenCalledWith('/')
  })
})
