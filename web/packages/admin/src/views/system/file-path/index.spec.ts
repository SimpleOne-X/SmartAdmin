import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { NMessageProvider } from 'naive-ui'
import zhCN from '#/locales/zh-CN'

// 只验证页签按权限显示、默认落在第一个看得见的页签;两个页签的内容换成桩。
vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  filePathApi: {
    environment: async () => ({ allowedRoots: ['D:\\shares'], supportsCredentials: true }),
  },
}))

const { stub } = vi.hoisted(() => ({
  stub: (cls: string) => async () => {
    const { defineComponent, h: el } = await import('vue')
    return { default: defineComponent({ setup: () => () => el('div', { class: cls }) }) }
  },
}))
vi.mock('./components/PathTab.vue', stub('stub-path'))
vi.mock('./components/ServerTab.vue', stub('stub-server'))

import { useAuthStore } from '#/stores/auth'
import FilePathPage from './index.vue'

const PATH_PAGE = 'GET:/api/v1/sys/file-path/page'
const SERVER_PAGE = 'GET:/api/v1/sys/file-server/page'

let app: App<Element> | undefined

async function mount(perms: string[]) {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().$patch({ permissionsLoaded: true, permissionCodes: perms })
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({ render: () => h(NMessageProvider, null, { default: () => h(FilePathPage) }) })
  app.use(pinia)
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.mount(host)
  for (let i = 0; i < 4; i++) await nextTick()
}

// segment 型页签另有一个不带文字的占位格,只取有文字的
const tabs = () =>
  [...document.body.querySelectorAll('.n-tabs-tab')]
    .map(el => el.textContent?.trim())
    .filter(Boolean)
const shown = (cls: string) => document.body.querySelector(`.${cls}`) !== null

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('文件路径配置页的页签', () => {
  it('shows_both_tabs_and_opens_the_path_tab_first', async () => {
    await mount([PATH_PAGE, SERVER_PAGE])

    expect(tabs()).toEqual([zhCN.filePath.tabs.path, zhCN.filePath.tabs.server])
    expect(shown('stub-path')).toBe(true)
    expect(shown('stub-server')).toBe(false)
  })

  it('hides_the_path_tab_without_its_page_permission_and_opens_the_server_tab', async () => {
    await mount([SERVER_PAGE])

    expect(tabs()).toEqual([zhCN.filePath.tabs.server])
    expect(shown('stub-path')).toBe(false)
    expect(shown('stub-server')).toBe(true)
  })

  it('hides_the_server_tab_without_its_page_permission', async () => {
    await mount([PATH_PAGE])

    expect(tabs()).toEqual([zhCN.filePath.tabs.path])
    expect(shown('stub-path')).toBe(true)
  })
})
