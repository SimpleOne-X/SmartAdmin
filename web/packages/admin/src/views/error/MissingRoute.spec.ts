import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, type App } from 'vue'
import { createI18n } from 'vue-i18n'

const route = vi.hoisted(() => ({
  meta: { title: '用户管理', missingComponent: 'system/user/missing' } as Record<string, unknown>,
}))

vi.mock('vue-router', () => ({ useRoute: () => route }))
// 图标不在离线子集里时 Iconify 会去请求公共 API;本用例只看文案,换成空渲染避免走网络
vi.mock('@iconify/vue', () => ({ Icon: { render: () => null } }))

import MissingRoute from './MissingRoute.vue'

let app: App<Element> | undefined

afterEach(() => {
  app?.unmount()
  app = undefined
})

describe('MissingRoute', () => {
  it('用当前语言显示菜单标题和缺失的组件路径', () => {
    const host = document.createElement('div')
    const i18n = createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: {
        'zh-CN': {
          missingRoute: {
            title: '菜单页面无法加载',
            message: '菜单“{title}”配置的组件路径不存在，请检查组件路径：{component}',
          },
        },
      },
    })

    app = createApp(MissingRoute)
    app.use(i18n)
    app.mount(host)

    expect(host.textContent).toContain('菜单页面无法加载')
    expect(host.textContent).toContain('菜单“用户管理”配置的组件路径不存在')
    expect(host.textContent).toContain('system/user/missing')
  })
})
