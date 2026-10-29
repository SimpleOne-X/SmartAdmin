import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Middleware } from 'openapi-fetch'

// 客户端多在模块顶层创建,那时 createSmartAdmin 可能还没写入 apiBase;
// 钉住「调用时按当时的 apiBase 发请求」,以及地址变了重建后,调用方后加的中间件还在。
const fetchMock = vi.fn(async (_req: Request) => new Response('{}', { status: 200 }))

const user = vi.hoisted(() => ({
  accessToken: '',
  refreshToken: '',
  cookieSession: false,
  setSession: vi.fn(),
  clear: vi.fn(),
  adoptFromStorage: vi.fn(),
}))
const routerMock = vi.hoisted(() => ({
  router: {
    currentRoute: { value: { path: '/board', fullPath: '/board?department=X' } },
    replace: vi.fn(),
  },
  resetRouter: vi.fn(),
}))

vi.mock('#/stores/user', () => ({ useUserStore: () => user }))
vi.mock('#/stores/auth', () => ({ useAuthStore: () => ({ reset: vi.fn() }) }))
vi.mock('#/router', () => routerMock)

beforeEach(() => {
  vi.resetModules()
  fetchMock.mockReset()
  fetchMock.mockImplementation(async () => new Response('{}', { status: 200 }))
  routerMock.router.replace.mockClear()
  routerMock.router.currentRoute.value.path = '/board'
  user.accessToken = ''
  user.refreshToken = ''
  user.cookieSession = false
  user.setSession.mockReset()
  user.setSession.mockImplementation((d: { accessToken: string; refreshToken: string }) => {
    user.accessToken = d.accessToken
    user.refreshToken = d.refreshToken
  })
  user.adoptFromStorage.mockReset()
  user.adoptFromStorage.mockReturnValue(false)
  user.clear.mockReset()
  user.clear.mockImplementation(() => {
    user.accessToken = ''
    user.refreshToken = ''
    user.cookieSession = false
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function load() {
  const { runtime } = await import('#/lib/runtime')
  const { createApiClient } = await import('./client')
  return { runtime, createApiClient }
}

describe('createApiClient', () => {
  it('创建之后才设定的 apiBase,调用时生效', async () => {
    const { runtime, createApiClient } = await load()
    runtime.apiBase = ''
    const api = createApiClient<Record<string, never>>()

    runtime.apiBase = 'https://api.example.test'
    await (api as unknown as { GET: (p: string) => Promise<unknown> }).GET('/api/v1/ping')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]![0].url).toBe('https://api.example.test/api/v1/ping')
  })

  it('apiBase 变了重建后,调用方加的中间件仍然生效', async () => {
    const { runtime, createApiClient } = await load()
    runtime.apiBase = ''
    const api = createApiClient<Record<string, never>>()
    const seen: string[] = []
    const mw: Middleware = {
      onRequest({ request }) {
        seen.push(request.url)
        return request
      },
    }
    api.use(mw)

    runtime.apiBase = 'https://api.example.test'
    await (api as unknown as { GET: (p: string) => Promise<unknown> }).GET('/api/v1/ping')

    expect(seen).toEqual(['https://api.example.test/api/v1/ping'])
  })
})

const NEW_ACCESS = 'new-access'

/** 取请求的失败原因(成功则 undefined);先挂上处理,假时钟推进时的 reject 才不会被当成未处理。 */
const failureOf = (request: Promise<unknown>) =>
  request.then(
    () => undefined,
    (e: unknown) => e,
  )

const envelope = (code: number, data?: unknown, status = 200) =>
  new Response(JSON.stringify({ code, msgKey: `error.code.${code}`, data }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
const renewed = () => envelope(0, { accessToken: NEW_ACCESS, refreshToken: 'new-refresh' })

// 访问令牌过期(业务请求 401)→ 续期。只有服务端明说会话失效才清会话;网络错误、超时、5xx、429
// 都保留会话,触发续期的那次请求以非 ApiError 的错误失败(调用方当网络错误处理,不会被判成会话失效),
// 看板这类长开页面在后端重启期间只是取数失败,恢复后下一次请求再续。
describe('续期失败分诊', () => {
  /** 业务请求带旧令牌 → 401;带续期后的新令牌 → 200。续期请求交给 `refresh`。 */
  function stubBackend(refresh: (req: Request) => Response | Promise<Response>) {
    fetchMock.mockImplementation(async req => {
      if (req.url.includes('/api/v1/auth/refresh')) return refresh(req)
      const authorized = req.headers.get('Authorization') === `Bearer ${NEW_ACCESS}`
      return new Response('{}', { status: authorized ? 200 : 401 })
    })
  }

  async function pingClient() {
    const { runtime, createApiClient } = await load()
    runtime.apiBase = 'https://api.example.test'
    return createApiClient<Record<string, never>>() as unknown as {
      GET: (p: string) => Promise<{ response: Response }>
    }
  }

  async function ping() {
    return (await (await pingClient()).GET('/api/v1/ping')).response
  }

  beforeEach(() => {
    user.accessToken = 'old-access'
    user.refreshToken = 'old-refresh'
  })

  function expectSessionKept(failure: unknown) {
    expect(failure).toMatchObject({ name: 'RefreshUnavailableError' })
    expect(user.accessToken).toBe('old-access')
    expect(user.refreshToken).toBe('old-refresh')
    expect(user.clear).not.toHaveBeenCalled()
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  }

  function expectSessionCleared() {
    expect(user.accessToken).toBe('')
    // 回登录页时带上当前地址,重新登录后回到原看板。
    expect(routerMock.router.replace).toHaveBeenCalledWith(
      expect.objectContaining({ path: '/login', query: { redirect: '/board?department=X' } }),
    )
  }

  it('刷新接口网络错误时保留会话、原请求以非 ApiError 失败', async () => {
    stubBackend(() => {
      throw new TypeError('Failed to fetch')
    })
    expectSessionKept(await failureOf(ping()))
  })

  it('刷新接口 503 时保留会话', async () => {
    stubBackend(() => new Response('<html>Service Unavailable</html>', { status: 503 }))
    expectSessionKept(await failureOf(ping()))
  })

  it('刷新接口 429 时保留会话', async () => {
    stubBackend(() => envelope(40008, undefined, 429))
    expectSessionKept(await failureOf(ping()))
  })

  it('刷新接口超时保留会话', async () => {
    // 假时钟驱动不了 AbortSignal.timeout;超时闸触发后 fetch 就是以 TimeoutError reject,直接模拟这个结果。
    stubBackend(() => {
      throw new DOMException('signal timed out', 'TimeoutError')
    })
    expectSessionKept(await failureOf(ping()))
  })

  it('刷新返回 40007 时清会话并跳登录', async () => {
    stubBackend(() => envelope(40007))
    expect((await ping()).status).toBe(401)
    expectSessionCleared()
  })

  it('刷新返回 40005 时清会话并跳登录', async () => {
    stubBackend(() => envelope(40005))
    expect((await ping()).status).toBe(401)
    expectSessionCleared()
  })

  it('刷新返回 HTTP 401 时清会话并跳登录', async () => {
    stubBackend(() => new Response('{}', { status: 401 }))
    expect((await ping()).status).toBe(401)
    expectSessionCleared()
  })

  it('刷新返回 403 时清会话并跳登录', async () => {
    stubBackend(() => envelope(40023, undefined, 403))
    expect((await ping()).status).toBe(401)
    expectSessionCleared()
  })

  it('刷新成功时换发令牌并重放原请求', async () => {
    stubBackend(() => renewed())
    expect((await ping()).status).toBe(200)
    expect(user.accessToken).toBe(NEW_ACCESS)
    expect(user.clear).not.toHaveBeenCalled()
  })

  it('暂时失败后,后端恢复时下一次请求再续期成功', async () => {
    let up = false
    stubBackend(() => (up ? renewed() : new Response('Bad Gateway', { status: 502 })))
    expectSessionKept(await failureOf(ping()))

    up = true
    expect((await ping()).status).toBe(200)
    expect(user.accessToken).toBe(NEW_ACCESS)
  })

  // 同一浏览器多个标签页共用一份会话:别的标签页先续期并把新令牌写进 localStorage(adoptFromStorage 接手),
  // 本页不能再拿旧刷新令牌去换(服务端对刚被轮换的令牌回 40037,只拒绝、不吊销)。
  describe('多标签页共用会话', () => {
    const SUPERSEDED = 40037

    function countRefreshCalls() {
      return fetchMock.mock.calls.filter(([req]) => req.url.includes('/api/v1/auth/refresh')).length
    }

    /** 模拟其它标签页的续期落地:本页内存换成新令牌。 */
    function adoptSiblingTokens() {
      user.accessToken = NEW_ACCESS
      user.refreshToken = 'sibling-refresh'
      return true
    }

    it('续期前先接手存储里更新的令牌,不再发刷新请求', async () => {
      user.adoptFromStorage.mockImplementation(adoptSiblingTokens)
      stubBackend(() => renewed())

      expect((await ping()).status).toBe(200)
      expect(countRefreshCalls()).toBe(0)
      expect(user.refreshToken).toBe('sibling-refresh')
    })

    it('body 模式刷新返回 40037 时等其它标签页写入新令牌', async () => {
      let siblingWrote = false
      user.adoptFromStorage.mockImplementation(() => siblingWrote && adoptSiblingTokens())
      stubBackend(() => envelope(SUPERSEDED))
      const api = await pingClient()

      vi.useFakeTimers()
      setTimeout(() => (siblingWrote = true), 300)
      const pending = api.GET('/api/v1/ping')
      await vi.advanceTimersByTimeAsync(1000)

      expect((await pending).response.status).toBe(200)
      expect(countRefreshCalls()).toBe(1) // 旧令牌不重发
      expect(user.clear).not.toHaveBeenCalled()
    })

    it('body 模式 40037 后 5 秒没等到新令牌按暂时失败处理', async () => {
      stubBackend(() => envelope(SUPERSEDED))
      const api = await pingClient()

      vi.useFakeTimers()
      const failed = failureOf(api.GET('/api/v1/ping'))
      await vi.advanceTimersByTimeAsync(6000)

      expectSessionKept(await failed)
      expect(countRefreshCalls()).toBe(1)
    })

    it('Cookie 模式刷新返回 40037 时重试刷新', async () => {
      user.cookieSession = true
      user.refreshToken = ''
      let calls = 0
      // HTTP 状态取 401:40037 的判断必须排在「HTTP 401 即会话失效」之前,否则会被误清会话。
      stubBackend(() => (++calls === 1 ? envelope(SUPERSEDED, undefined, 401) : renewed()))
      const api = await pingClient()

      vi.useFakeTimers()
      const pending = api.GET('/api/v1/ping')
      await vi.advanceTimersByTimeAsync(600)

      expect((await pending).response.status).toBe(200)
      expect(countRefreshCalls()).toBe(2)
      expect(user.clear).not.toHaveBeenCalled()
    })

    it('Cookie 模式 40037 重试 3 次仍无果按暂时失败处理', async () => {
      user.cookieSession = true
      user.refreshToken = ''
      stubBackend(() => envelope(SUPERSEDED))
      const api = await pingClient()

      vi.useFakeTimers()
      const failed = failureOf(api.GET('/api/v1/ping'))
      await vi.advanceTimersByTimeAsync(3000)

      expect(await failed).toMatchObject({ name: 'RefreshUnavailableError' })
      expect(countRefreshCalls()).toBe(4) // 首发 + 3 次重试
      expect(user.clear).not.toHaveBeenCalled()
      expect(routerMock.router.replace).not.toHaveBeenCalled()
    })
  })
})
