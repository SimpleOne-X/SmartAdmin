import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineAsyncComponent, h, KeepAlive, ref, type App } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { runtime } from '#/lib/runtime'
import { useUserStore } from '#/stores/user'
import ApiDocsPage from './index.vue'

const apps: App<Element>[] = []

// defineAsyncComponent 的 resolve→重渲染→onMounted(post-flush 回调)要经过好几跳微任务
// (loader 的 Promise then、触发重渲染的 scheduler flush、mount 后置回调各一跳),跳数会因
// Vue 内部调度是否已经在 flush 中而略有出入,固定次数的 nextTick() 并不总够。用一个宏任务
// 边界把这些微任务一次性排空,比数 nextTick 次数稳。
function flushAsync() {
  return new Promise<void>(resolve => setTimeout(resolve, 0))
}

function createTestI18n() {
  return createI18n({
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
}

afterEach(() => {
  while (apps.length) apps.pop()?.unmount()
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

    const i18n = createTestI18n()

    const host = document.createElement('div')
    const app = createApp(ApiDocsPage)
    apps.push(app)
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

  // 复刻 router/namedPage.ts 的真实结构:页面并不是 <keep-alive> 的直接子节点,而是先经过
  // defineAsyncComponent 包一层再交给 <keep-alive> 渲染——这正是触发回归的组合(实测确认:
  // 不加去重时,只要页面是从这层异步组件包装下挂载的,同一次挂载里 onMounted 与 onActivated
  // 会在同一个 tick 各调用一次 openDocs,一次挂载开出两个标签页;不管这是这次会话里第几次挂载)。
  // 用"卸载再重新挂载"模拟关闭标签页重新打开:同一个 asyncPage 定义对象在两次挂载间复用,
  // 与 namedPage() 按路由名把异步组件定义缓存在模块级、反复用于多次挂载的做法一致。
  // 断言的不变量是:不管挂载几次,每次都应该只开一个标签页——不是"第一次没事,第二次才炸"。
  it('页面经 defineAsyncComponent 包装挂进 <keep-alive> 时,每次全新挂载只应开一个标签页', async () => {
    runtime.apiBase = 'https://api.example.test'
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)

    const pinia = createPinia()
    setActivePinia(pinia)
    const userStore = useUserStore()
    userStore.accessToken = 'test-access-token'

    const asyncPage = defineAsyncComponent(() => Promise.resolve(ApiDocsPage))

    async function mountSession() {
      const host = document.createElement('div')
      const shown = ref(true)
      const i18n = createTestI18n()
      const sessionApp = createApp({
        setup() {
          return () => h(KeepAlive, () => (shown.value ? h(asyncPage) : null))
        },
      })
      apps.push(sessionApp)
      sessionApp.use(pinia)
      sessionApp.use(i18n)
      sessionApp.mount(host)
      await flushAsync()
      return sessionApp
    }

    // 第一次挂载(相当于本次会话里第一次打开这个页面)。
    const session1 = await mountSession()
    expect(openSpy).toHaveBeenCalledTimes(1)

    // 模拟"关闭标签页再重新打开":销毁这个 keep-alive 实例。asyncPage 定义对象本身没有被重建,
    // 它内部的 resolved 缓存留着——这就是 namedPage() 在真实路由里的行为。
    session1.unmount()
    openSpy.mockClear()

    // 第二次全新挂载(关闭标签页重开 / F5 / 切模块再切回都是这种场景)。
    // 没有去重修复的话,onMounted + onActivated 会在这个 tick 里各调用一次 openDocs → 2 次。
    await mountSession()
    expect(openSpy).toHaveBeenCalledTimes(1)
  })

  it('同一个 keep-alive 会话内真正切走再切回:去重窗口已过,onActivated 应该重新打开标签页', async () => {
    runtime.apiBase = 'https://api.example.test'
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)

    const pinia = createPinia()
    setActivePinia(pinia)
    const userStore = useUserStore()
    userStore.accessToken = 'test-access-token'
    const i18n = createTestI18n()

    const asyncPage = defineAsyncComponent(() => Promise.resolve(ApiDocsPage))
    const shown = ref(true)
    const host = document.createElement('div')
    const app = createApp({
      setup() {
        return () => h(KeepAlive, () => (shown.value ? h(asyncPage) : null))
      },
    })
    apps.push(app)
    app.use(pinia)
    app.use(i18n)
    app.mount(host)
    await flushAsync()
    expect(openSpy).toHaveBeenCalledTimes(1)

    // 切走(deactivated)再切回(activated)——这一次不是同一个 tick 内的双触发,
    // 去重门闩早在上一轮的微任务里就清空了,理应正常重开一个新标签页。
    shown.value = false
    await flushAsync()
    shown.value = true
    await flushAsync()

    expect(openSpy).toHaveBeenCalledTimes(2)
  })
})
