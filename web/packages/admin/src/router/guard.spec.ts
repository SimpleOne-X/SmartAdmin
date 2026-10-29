import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

// 全局路由守卫的分流:F5 恢复遇到暂时失败时的放行、未登录送登录页带 redirect、整屏页不经选应用页。
// 网络层(refreshSession / enterInitial)桩掉,路由表用真的静态路由;各页面组件换成空壳,免得拉起整套 UI。
const refreshSession = vi.hoisted(() => vi.fn())
const enterInitial = vi.hoisted(() => vi.fn())

vi.mock('#/api/client', () => ({ refreshSession }))
vi.mock('#/api', () => ({
  personalApi: { recordShortcutVisit: () => Promise.resolve() },
  // 与真实 ApiError 同形:构造参数第一个是业务码。
  ApiError: class ApiError extends Error {
    constructor(public code: number) {
      super(`Error ${code}`)
    }
  },
}))
vi.mock('#/composables/useModule', () => ({ useModule: () => ({ enterInitial }) }))
const stubView = { default: { render: () => null } }
vi.mock('#/views/login/index.vue', () => stubView)
vi.mock('#/views/error/BootError.vue', () => stubView)
vi.mock('#/views/error/404.vue', () => stubView)
vi.mock('#/views/module/index.vue', () => stubView)
vi.mock('#/layouts/default.vue', () => ({ default: { template: '<router-view />' } }))

// happy-dom 在 Node 22+ 下 sessionStorage 可能为 undefined,与 loginRedirect.spec 同款内存桩。
function makeMemoryStorage(): Storage {
  const data = new Map<string, string>()
  return {
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
    clear: () => data.clear(),
    key: (i: number) => Array.from(data.keys())[i] ?? null,
    get length() {
      return data.size
    },
  } as Storage
}

const BOARD = '/fullscreen/eam/board/maintenance-board?department=X'

async function load() {
  vi.resetModules()
  window.history.replaceState(null, '', '/')
  setActivePinia(createPinia())
  const { router } = await import('./index')
  const { useUserStore } = await import('#/stores/user')
  const { takeLoginRedirect } = await import('./loginRedirect')
  const { ApiError } = await import('#/api')
  // 消费方的整屏看板路由:顶级静态路由、非 public(与 createSmartAdmin({ routes }) 注册的同形)。
  router.addRoute({ path: '/fullscreen/:rest(.*)', component: { render: () => null } })
  router.addRoute({
    path: '/open-page',
    component: { render: () => null },
    meta: { public: true },
  })
  return { router, user: useUserStore(), takeLoginRedirect, ApiError }
}

// 路由图的冷导入要几百毫秒到一两秒,先在 hook 里导一遍(走 hook 超时),首个用例不必背这笔成本。
beforeAll(async () => {
  await import('./index')
})

beforeEach(() => {
  vi.stubGlobal('sessionStorage', makeMemoryStorage())
  refreshSession.mockReset()
  enterInitial.mockReset()
})

describe('F5 恢复遇到暂时失败', () => {
  it('目标是登录页时放行,会话保留', async () => {
    const { router, user } = await load()
    user.refreshToken = 'rt'
    refreshSession.mockResolvedValue('transient')

    await router.push('/login')

    expect(router.currentRoute.value.path).toBe('/login')
    expect(user.refreshToken).toBe('rt')
  })

  it('目标是 meta.public 的页面时放行,会话保留', async () => {
    const { router, user } = await load()
    user.refreshToken = 'rt'
    refreshSession.mockResolvedValue('transient')

    await router.push('/open-page')

    expect(router.currentRoute.value.path).toBe('/open-page')
    expect(user.refreshToken).toBe('rt')
  })

  it('目标是需要会话的页面时转到可重试的错误页', async () => {
    const { router, user } = await load()
    user.refreshToken = 'rt'
    refreshSession.mockResolvedValue('transient')

    await router.push('/system/user?x=1')

    expect(router.currentRoute.value.path).toBe('/boot-error')
    expect(router.currentRoute.value.query.redirect).toBe('/system/user?x=1')
    expect(user.refreshToken).toBe('rt')
  })

  it('会话已失效时清掉本地残留,按未登录处理', async () => {
    const { router, user } = await load()
    user.refreshToken = 'rt'
    refreshSession.mockResolvedValue('dead')

    await router.push(BOARD)

    expect(user.refreshToken).toBe('')
    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.redirect).toBe(BOARD)
  })
})

describe('登录回跳', () => {
  it('未登录访问深链:送登录页并带 redirect', async () => {
    const { router } = await load()

    await router.push(BOARD)

    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.redirect).toBe(BOARD)
  })

  it('未登录访问首页:送登录页,不带 redirect', async () => {
    const { router } = await load()

    await router.push('/')

    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.redirect).toBeUndefined()
  })

  it('进入登录页时暂存 redirect,不安全的地址丢弃', async () => {
    const { router, takeLoginRedirect } = await load()

    await router.push({ path: '/login', query: { redirect: '//evil.com' } })
    expect(takeLoginRedirect()).toBeNull()

    await router.push({ path: '/login', query: { redirect: BOARD } })
    expect(takeLoginRedirect()).toBe(BOARD)
  })

  it('已登录再访问登录页:回到暂存的地址,没有则回首页', async () => {
    const { router, user } = await load()
    user.accessToken = 'at'
    enterInitial.mockResolvedValue({ chooser: true })

    await router.push({ path: '/login', query: { redirect: BOARD } })
    expect(router.currentRoute.value.fullPath).toBe(BOARD)

    await router.push('/login')
    expect(router.currentRoute.value.path).toBe('/module')
  })

  it('重建期间会话失效:清会话、送登录页并带 redirect', async () => {
    const { router, user, ApiError } = await load()
    user.accessToken = 'at'
    user.refreshToken = 'rt'
    enterInitial.mockRejectedValue(new ApiError(40007))

    await router.push('/system/user')

    expect(user.accessToken).toBe('')
    expect(router.currentRoute.value.path).toBe('/login')
    expect(router.currentRoute.value.query.redirect).toBe('/system/user')
  })
})

describe('整屏页与选应用页', () => {
  it('需要选应用时整屏页直接放行', async () => {
    const { router, user } = await load()
    user.accessToken = 'at'
    enterInitial.mockResolvedValue({ chooser: true })

    await router.push(BOARD)

    expect(router.currentRoute.value.fullPath).toBe(BOARD)
  })

  it('需要选应用时普通页面仍转到选应用页', async () => {
    const { router, user } = await load()
    user.accessToken = 'at'
    enterInitial.mockResolvedValue({ chooser: true })

    await router.push('/system/user')

    expect(router.currentRoute.value.path).toBe('/module')
  })
})
