import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createPersistedState } from 'pinia-plugin-persistedstate'
import { runtime } from '#/lib/runtime'
import { isSessionDead } from '#/router/bootFailure'
import { installSessionSync } from '#/stores/sessionSync'
import { useUserStore } from '#/stores/user'
import { createApiClient } from './client'

// 同一浏览器两个标签页同时续期:真实的 user store + persist + sessionSync + client,只桩路由、授权 store 和 fetch。
// 「别的标签页」由测试直接改共享存储并派发 storage 事件来扮演,本页是被测的落败方。
const routerMock = vi.hoisted(() => ({
  router: {
    currentRoute: { value: { path: '/board', fullPath: '/board', query: {} } },
    replace: vi.fn(),
  },
  resetRouter: vi.fn(),
}))
vi.mock('#/stores/auth', () => ({ useAuthStore: () => ({ reset: vi.fn() }) }))
vi.mock('#/router', () => routerMock)

// happy-dom 在 Node 22+ 下 window.localStorage 可能为 undefined,与其它 store 用例同款内存 Storage 桩。
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

const T_LOCAL = 1_000_000 // 本页登录时刻(假时钟起点)
const T_SIBLING = 2_000_000 // 赢家标签页续期的签发戳
const T_STALE = 500_000 // 落后标签页内存里那份旧令牌的戳

const stored = (accessToken: string, refreshToken: string, issuedAt: number) =>
  JSON.stringify({
    accessToken,
    refreshToken,
    cookieSession: false,
    userInfo: { userId: 1, account: 'admin', name: 'Admin', mustChangePassword: false },
    issuedAt,
  })

const envelope = (code: number) =>
  new Response(JSON.stringify({ code }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })

const fetchMock = vi.fn()
let storage: Storage
let user: ReturnType<typeof useUserStore>
let api: { GET: (path: string) => Promise<{ response: Response }> }

/** 别的标签页写了 user 键:改共享存储,再给本页派发 storage 事件。 */
function otherTabWrites(value: string) {
  storage.setItem('user', value)
  window.dispatchEvent(new StorageEvent('storage', { key: 'user', newValue: value }))
}

/** 业务请求带 at2 才放行;续期请求交给 `refresh`。 */
function stubBackend(refresh: () => Response | Promise<Response>) {
  fetchMock.mockImplementation(async (req: Request) => {
    if (req.url.includes('/api/v1/auth/refresh')) return refresh()
    const ok = req.headers.get('Authorization') === 'Bearer at2'
    return new Response('{}', { status: ok ? 200 : 401 })
  })
}

const refreshCalls = () =>
  fetchMock.mock.calls.filter(([req]) => (req as Request).url.includes('/api/v1/auth/refresh'))
    .length

/** 取请求的失败原因(成功则 undefined);先挂上处理,假时钟推进时的 reject 才不会被当成未处理。 */
const failureOf = (request: Promise<unknown>) =>
  request.then(
    () => undefined,
    (e: unknown) => e,
  )

beforeEach(async () => {
  vi.useFakeTimers()
  vi.setSystemTime(T_LOCAL)
  storage = makeMemoryStorage()
  vi.stubGlobal('localStorage', storage)
  vi.stubGlobal('fetch', fetchMock)
  fetchMock.mockReset()
  routerMock.router.replace.mockClear()

  const pinia = createPinia()
  pinia.use(createPersistedState({ storage }))
  createApp({}).use(pinia)
  setActivePinia(pinia)
  runtime.apiBase = 'https://api.example.test'
  installSessionSync()

  user = useUserStore()
  user.setSession({
    accessToken: 'at1',
    expiresAt: '2099-01-01T00:00:00Z',
    refreshToken: 'rt1',
    refreshExpiresAt: '2099-01-02T00:00:00Z',
    userId: 1,
    account: 'admin',
    name: 'Admin',
    mustChangePassword: false,
    sessionMode: 'body',
  })
  await vi.advanceTimersByTimeAsync(0) // 让 persist 把登录态落盘
  api = createApiClient<Record<string, never>>() as unknown as typeof api
})

afterEach(() => {
  vi.useRealTimers()
})

describe('两个标签页同时续期,落败方不掉线', () => {
  it('storage 事件先到、40037 后到:不等 5 秒,用新令牌重放原请求', async () => {
    stubBackend(() => {
      otherTabWrites(stored('at2', 'rt2', T_SIBLING)) // 刷新请求在途时赢家写入新令牌,本页事件先到
      return envelope(40037)
    })

    let done = false
    const pending = api.GET('/api/v1/ping').then(r => {
      done = true
      return r
    })
    await vi.advanceTimersByTimeAsync(100)

    expect(done).toBe(true)
    expect((await pending).response.status).toBe(200)
    expect(user.refreshToken).toBe('rt2')
    expect(refreshCalls()).toBe(1)
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })

  it('40037 先到、storage 事件后到:等到新令牌后重放原请求', async () => {
    stubBackend(() => envelope(40037))
    setTimeout(() => otherTabWrites(stored('at2', 'rt2', T_SIBLING)), 300)

    const pending = api.GET('/api/v1/ping')
    await vi.advanceTimersByTimeAsync(1000)

    expect((await pending).response.status).toBe(200)
    expect(user.refreshToken).toBe('rt2')
    expect(refreshCalls()).toBe(1) // 旧令牌不重发
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })

  it('5 秒等不到新令牌:会话保留,原请求以非 ApiError 失败(不会被判成会话失效)', async () => {
    stubBackend(() => envelope(40037))

    const failed = failureOf(api.GET('/api/v1/ping'))
    await vi.advanceTimersByTimeAsync(6000)
    const error = await failed

    expect(error).toMatchObject({ name: 'RefreshUnavailableError' })
    expect(isSessionDead(error)).toBe(false)
    expect(user.accessToken).toBe('at1')
    expect(user.refreshToken).toBe('rt1')
    expect(JSON.parse(storage.getItem('user')!).refreshToken).toBe('rt1')
    expect(refreshCalls()).toBe(1)
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })
})

describe('落后标签页写回旧令牌', () => {
  it('旧令牌不被其它标签页接手,存储最终回到新令牌', async () => {
    otherTabWrites(stored('at2', 'rt2', T_SIBLING)) // 赢家续期
    expect(user.refreshToken).toBe('rt2')
    await vi.advanceTimersByTimeAsync(0) // 本页接手后的 persist 先落定,旧写入是之后才到的

    // 另一个落后的标签页因与令牌无关的变更(回填头像等)把内存里的旧令牌整份落盘
    otherTabWrites(stored('at1', 'rt1', T_STALE))
    await vi.advanceTimersByTimeAsync(0)

    expect(user.refreshToken).toBe('rt2')
    const saved = JSON.parse(storage.getItem('user')!)
    expect(saved.refreshToken).toBe('rt2')
    expect(saved.issuedAt).toBe(T_SIBLING)
  })

  it('本页自己先把旧令牌写回存储,迟到的新令牌事件仍能接手', async () => {
    // 赢家已把 rt2 写进存储,本页的事件还在队列里;本页先因回填头像把 rt1 整份落盘
    storage.setItem('user', stored('at2', 'rt2', T_SIBLING))
    user.userInfo!.avatar = 'a.png'
    await vi.advanceTimersByTimeAsync(0)
    expect(JSON.parse(storage.getItem('user')!).refreshToken).toBe('rt1')

    // 迟到的事件带着 rt2
    window.dispatchEvent(
      new StorageEvent('storage', { key: 'user', newValue: stored('at2', 'rt2', T_SIBLING) }),
    )
    await vi.advanceTimersByTimeAsync(0)

    expect(user.refreshToken).toBe('rt2')
    expect(JSON.parse(storage.getItem('user')!).refreshToken).toBe('rt2')
  })
})
