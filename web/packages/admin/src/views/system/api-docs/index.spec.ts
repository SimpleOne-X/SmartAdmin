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

    expect(openSpy).toHaveBeenCalledWith('https://api.example.test/scalar', '_blank')
    expect(host.querySelector('a')?.getAttribute('href')).toBe('https://api.example.test/scalar')
    expect(host.textContent).toContain('接口文档已在新标签页打开')
  })
})
