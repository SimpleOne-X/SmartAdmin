import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, type App } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { runtime } from '#/lib/runtime'
import { useUserStore } from '#/stores/user'
import ApiDocsPage from './index.vue'

let app: App<Element> | undefined

afterEach(() => {
  app?.unmount()
  app = undefined
  runtime.apiBase = ''
})

describe('ApiDocsPage', () => {
  it('挂载即在新标签页打开 /scalar,兜底链接指向同一地址', () => {
    runtime.apiBase = 'https://api.example.test'
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)

    const pinia = createPinia()
    setActivePinia(pinia)
    const userStore = useUserStore()
    userStore.accessToken = 'test-access-token'

    const i18n = createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: {
        'zh-CN': {
          apiDocs: {
            title: '接口文档',
            openedHint: '接口文档已在新标签页打开',
            fallbackLink: '没有自动打开?点此手动打开',
            copyToken: '复制我的接口令牌',
            copyTokenHint: '在新标签页里点右上角 Authentication → Bearer,粘贴这里复制的令牌',
            tokenCopied: '已复制',
          },
        },
      },
    })

    const host = document.createElement('div')
    app = createApp(ApiDocsPage)
    app.use(pinia)
    app.use(i18n)
    app.mount(host)

    // 打开动作挂在 onMounted 上(真实挂载会触发),不是 setup 顶层——否则被 <keep-alive> 缓存后再次进入不会重开。
    // 另一半 onActivated 只有真实 keep-alive 复用缓存实例时才触发,这一层测不到,不为它硬造装置。
    expect(openSpy).toHaveBeenCalledTimes(1)
    expect(openSpy).toHaveBeenCalledWith('https://api.example.test/scalar', '_blank')
    expect(host.querySelector('a')?.getAttribute('href')).toBe('https://api.example.test/scalar')
    expect(host.textContent).toContain('接口文档已在新标签页打开')
  })
})
