import { describe, expect, it } from 'vitest'
import { createApp } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { i18n } from '#/locales'
import { useTableTitle } from './useTableTitle'

async function setup(meta: Record<string, unknown>) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/x', component: { render: () => null }, meta }],
  })
  const app = createApp({ render: () => null })
  app.use(router)
  await router.push('/x')
  await router.isReady()
  return app.runWithContext(() => useTableTitle())
}

describe('useTableTitle', () => {
  it('取当前路由的菜单标题,走与侧栏相同的翻译入口', async () => {
    i18n.global.locale.value = 'zh-CN'
    const title = await setup({ title: 'common.search' })
    expect(title.value).toBe(i18n.global.t('common.search'))
  })

  it('切语言后标题跟着变', async () => {
    i18n.global.locale.value = 'zh-CN'
    const title = await setup({ title: 'common.search' })
    const zh = title.value
    i18n.global.locale.value = 'en-US'
    expect(title.value).not.toBe(zh)
    expect(title.value).toBe(i18n.global.t('common.search'))
    i18n.global.locale.value = 'zh-CN'
  })

  it('标题不含 key(存量库里的中文标题)时原样返回', async () => {
    const title = await setup({ title: '角色管理' })
    expect(title.value).toBe('角色管理')
  })

  it('路由没有 meta.title:返回空串而不是 undefined', async () => {
    const title = await setup({})
    expect(title.value).toBe('')
  })
})
