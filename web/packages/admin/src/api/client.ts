import createClient, { type Client, type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import { useUserStore } from '#/stores/user'
import { runtime } from '#/lib/runtime'
import { isSessionDeadCode } from '#/router/bootFailure'
import { REFRESH_SUPERSEDED_CODE, endLocalSession } from '#/stores/sessionSync'
import { REAUTH_REQUIRED_CODE, REAUTH_RETRY_HEADER, requestReauth } from './reauthGate'

// 默认空 apiBase = 同源:schema 的 path 键已含 /api/v1,dev 下由 Vite proxy 反代到后端(默认 :5100),
// 生产下由 nginx 反代或后端自己托管 dist(见文档站部署指南的路线 A / B)。
// 只有前端与 API 真的不同源(CDN / 独立域名)时才需要 createSmartAdmin({ apiBase: 'https://api.example.com' }),
// 此时后端还必须配 SmartAdmin:Api:Cors:AllowedOrigins(默认 deny-all)。

/**
 * 请求超时:没有超时的话,后端卡死或链路半开时 fetch 会一直挂着 —— 页面永远转圈,
 * 按钮的 loading 永不结束,用户只能刷新。
 * 默认 30 秒;上传/分片/导出/导入提交这类天然慢的调用在 api 层显式传 `timeout: LONG_TIMEOUT_MS`。
 */
export const DEFAULT_TIMEOUT_MS = 30_000
export const LONG_TIMEOUT_MS = 10 * 60_000

/** 与后端 AuthCookieNames 对齐(禁硬编码纪律的前端镜像)。 */
const CSRF_COOKIE = 'smart_csrf'
const CSRF_HEADER = 'X-Smart-CSRF'

/**
 * 读 document.cookie 中的可读 Cookie(双提交 CSRF 的 smart_csrf)。
 * HttpOnly 的 smart_rt 读不到,只能靠 credentials 自动携带。
 */
export function readCookie(name: string): string {
  if (typeof document === 'undefined') return ''
  const parts = document.cookie.split(';')
  for (const part of parts) {
    const i = part.indexOf('=')
    if (i < 0) continue
    const k = part.slice(0, i).trim()
    if (k === name) return decodeURIComponent(part.slice(i + 1).trim())
  }
  return ''
}

/** 写请求是否需要附 CSRF(有可读 CSRF Cookie 即附;无 Cookie 时不附)。 */
function isMutating(method: string): boolean {
  const m = method.toUpperCase()
  return m !== 'GET' && m !== 'HEAD' && m !== 'OPTIONS'
}

function attachCsrf(headers: Headers) {
  const csrf = readCookie(CSRF_COOKIE)
  if (csrf) headers.set(CSRF_HEADER, csrf)
}

/**
 * 给每个请求装超时闸。必须**最先**注册:后面的重放中间件要克隆最终的 Request,顺序反了就克隆到旧的那份。
 *
 * 调用方传的 `signal` 依然生效 —— 这里用 `AbortSignal.any` 把它和超时信号并起来,谁先响算谁,
 * 不是把调用方的取消能力顶掉。单次调用要放宽时限就多传一个 `timeout`(毫秒),
 * openapi-fetch 会把它这类自定义键原样挂到 Request 上(见其 InitParam 的 `[key: string]: unknown`)。
 */
const timeoutMiddleware: Middleware = {
  onRequest({ request }) {
    const ms = (request as Request & { timeout?: unknown }).timeout
    const limit = typeof ms === 'number' && ms > 0 ? ms : DEFAULT_TIMEOUT_MS
    return new Request(request, {
      signal: AbortSignal.any([request.signal, AbortSignal.timeout(limit)]),
    })
  },
}

/** 请求前注入 Bearer + 写操作 CSRF。请求时读 store,始终拿最新令牌。 */
const authMiddleware: Middleware = {
  async onRequest({ request }) {
    const token = useUserStore().accessToken
    if (token) request.headers.set('Authorization', `Bearer ${token}`)
    if (isMutating(request.method)) attachCsrf(request.headers)
    return request
  },
}

// 令牌过期重放:Request 的 body 是一次性流,首次 fetch 就被消费,直接重放会丢 body
//(GET 无 body 不受影响,但令牌恰好在一次 POST/PUT 时过期就会丢请求体)。
// 发出前克隆一份副本(clone 会 tee body 流,与原请求各读各的),重放时用这份未消费的副本。
// 键是发出前的 Request 实例(openapi-fetch 把它一路带到 onResponse),WeakMap 随请求回收自动清理。
const replayable = new WeakMap<Request, Request>()

/**
 * 一次续期的结果:
 * - `ok`:换发成功,令牌已写入 store;
 * - `dead`:服务端明确说会话失效(401 / 403,或业务码为令牌失效、账号停用),调用方应清会话回登录页;
 * - `transient`:网络错误、超时、5xx、429 等暂时失败,会话可能仍有效,调用方保留会话、下次请求再续。
 */
export type RefreshOutcome = 'ok' | 'dead' | 'transient'

/**
 * 续期遇到暂时失败时,让触发续期的那次请求以此失败。刻意不是 ApiError:调用方把它当网络错误处理
 * (启动守卫转可重试的错误页,普通页面本次请求失败),不会像 401 的 40006 信封那样被判成会话失效。
 * `name` 供 translateError 认出并给本地化文案。
 */
export class RefreshUnavailableError extends Error {
  constructor() {
    super('Session renewal is temporarily unavailable')
    this.name = 'RefreshUnavailableError'
  }
}

// 并发 401 合流到同一次刷新。
let refreshing: Promise<RefreshOutcome> | null = null

/** 单飞静默续期并返回分诊结果;路由守卫 F5 重建会话时也调用它。 */
export function refreshSession(): Promise<RefreshOutcome> {
  refreshing ??= doRefresh().finally(() => {
    refreshing = null
  })
  return refreshing
}

/** 单飞静默刷新,成功与否的布尔视图(要区分失败原因用 `refreshSession`)。 */
export async function refreshOnce(): Promise<boolean> {
  return (await refreshSession()) === 'ok'
}

/**
 * 确保有可用 accessToken:已有则 true;Cookie 会话或 body refresh 则尝试静默刷新。
 * 供路由守卫在 F5/深链时恢复内存令牌。
 */
export async function ensureAccessToken(): Promise<boolean> {
  const user = useUserStore()
  if (user.accessToken) return true
  if (!user.cookieSession && !user.refreshToken) return false
  return refreshOnce()
}

// 40037:刷新令牌刚被同一浏览器的另一标签页轮换。body 模式等它把新令牌写进 localStorage,
// Cookie 模式浏览器 Cookie 已是新的,重发即可。
const SIBLING_POLL_MS = 200
const SIBLING_WAIT_MS = 5_000
const COOKIE_RETRY_MS = 500
const COOKIE_RETRY_MAX = 3

const sleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms))

type RefreshEnvelope = { code?: number; data?: unknown }

/** 发一次刷新请求;网络错误、超时闸中止、响应体读不出来返回 null(服务端没给出结论)。 */
async function postRefresh(user: ReturnType<typeof useUserStore>) {
  const headers: Record<string, string> = {}
  if (user.cookieSession || readCookie(CSRF_COOKIE)) {
    const csrf = readCookie(CSRF_COOKIE)
    if (csrf) headers[CSRF_HEADER] = csrf
  }
  try {
    const result = await bare.POST('/api/v1/auth/refresh', {
      // Cookie 模式刷新令牌在 HttpOnly Cookie 里,body 的 refreshToken 留空:服务端 body 为空才读 Cookie
      body: user.cookieSession ? { refreshToken: '' } : { refreshToken: user.refreshToken },
      headers,
    })
    return {
      response: result.response,
      env: (result.data ?? result.error) as RefreshEnvelope | undefined,
    }
  } catch {
    return null
  }
}

/**
 * body 模式 40037:每 200ms 看一次,最多 5 秒;别的标签页的新令牌已落到本页(内存里的刷新令牌不再是
 * 发出去的那个,可能是 storage 事件抢先接手的)或从存储里接手到,即成功,等不到按暂时失败处理。
 */
async function awaitSiblingTokens(
  user: ReturnType<typeof useUserStore>,
  sent: string,
): Promise<RefreshOutcome> {
  for (let waited = 0; ; waited += SIBLING_POLL_MS) {
    if ((user.refreshToken && user.refreshToken !== sent) || user.adoptFromStorage()) return 'ok'
    if (waited >= SIBLING_WAIT_MS) return 'transient'
    await sleep(SIBLING_POLL_MS)
  }
}

async function doRefresh(): Promise<RefreshOutcome> {
  const user = useUserStore()
  // 别的标签页已经续过期:接手它写下的新令牌,不再拿本页的旧令牌去换(旧令牌会撞上重放检测)。
  if (!user.cookieSession && user.adoptFromStorage()) return 'ok'
  // Cookie 会话:body 不带 refresh(服务端读 smart_rt);body 模式:必须有 refreshToken
  if (!user.cookieSession && !user.refreshToken) return 'dead'

  const sent = user.refreshToken
  let res = await postRefresh(user)
  // 40037 的判断必须排在 dead 判定之前:它与 40007 的 HTTP 状态相同,顺序反了会把「只是慢了一步」当成凭证无效清掉会话。
  if (res?.env?.code === REFRESH_SUPERSEDED_CODE) {
    if (!user.cookieSession) return awaitSiblingTokens(user, sent)
    for (let i = 0; i < COOKIE_RETRY_MAX && res?.env?.code === REFRESH_SUPERSEDED_CODE; i++) {
      await sleep(COOKIE_RETRY_MS)
      res = await postRefresh(user)
    }
  }
  if (!res) return 'transient' // 网络错误、超时、响应体读不出来:服务端没给出结论
  const { response, env } = res
  const code = env?.code

  // 刷新是匿名端点:业务失败走 HTTP 200 + 信封 code;401 来自网关,403 只会是 CSRF 无效(重试不会好)。
  if (response.status === 401 || response.status === 403 || isSessionDeadCode(code)) return 'dead'
  if (response.ok && code === 0 && env?.data) {
    user.setSession(env.data as Parameters<typeof user.setSession>[0])
    return 'ok'
  }
  return 'transient' // 5xx、429、其它业务码(含重试后仍是 40037):会话可能仍有效,留给下一次请求再续
}

/** 401 → 刷新一次并重放原请求;会话失效 → 清会话 + 跳登录;暂时失败 → 保留会话、本次请求以 RefreshUnavailableError 失败。 */
const refreshMiddleware: Middleware = {
  onRequest({ request }) {
    // 带 body 的写请求发出前存一份可重放副本(GET/HEAD 无 body,不必克隆)。
    if (request.method !== 'GET' && request.method !== 'HEAD')
      replayable.set(request, request.clone())
    return request
  },
  async onResponse({ request, response }) {
    if (response.status !== 401) return response
    const url = request.url
    if (url.includes('/api/v1/auth/refresh') || url.includes('/api/v1/auth/login')) return response

    const outcome = await refreshSession()
    if (outcome === 'transient') throw new RefreshUnavailableError()
    if (outcome === 'dead') {
      await endLocalSession()
      return response
    }
    // 重放:优先用发出前的克隆副本(body 未被消费),无副本的 GET 直接用原请求;裸 fetch 绕过中间件,手动补新令牌 + CSRF。
    const base = replayable.get(request) ?? request
    const retry = new Request(base, { headers: new Headers(base.headers), credentials: 'include' })
    retry.headers.set('Authorization', `Bearer ${useUserStore().accessToken}`)
    if (isMutating(retry.method)) attachCsrf(retry.headers)
    return fetch(retry)
  },
}

/**
 * 403 + 40024(ReauthRequired) → 弹再认证 → 成功后重放一次。
 * 与 refresh 同形:依赖 onRequest 预克隆 body;重试头防死循环。
 */
const reauthMiddleware: Middleware = {
  async onResponse({ request, response }) {
    if (response.status !== 403) return response
    if (request.headers.get(REAUTH_RETRY_HEADER) === '1') return response
    if (request.url.includes('/api/v1/auth/reauth') || request.url.includes('/api/v1/auth/login')) {
      return response
    }

    let code: number | undefined
    try {
      const body = (await response.clone().json()) as { code?: number }
      code = body?.code
    } catch {
      return response
    }
    if (code !== REAUTH_REQUIRED_CODE) return response

    const granted = await requestReauth()
    if (!granted) return response

    const base = replayable.get(request) ?? request
    const retry = new Request(base, { headers: new Headers(base.headers), credentials: 'include' })
    const token = useUserStore().accessToken
    if (token) retry.headers.set('Authorization', `Bearer ${token}`)
    if (isMutating(retry.method)) attachCsrf(retry.headers)
    retry.headers.set(REAUTH_RETRY_HEADER, '1')
    return fetch(retry)
  },
}

/** 内核同款中间件链:超时必须最先(后面两个要克隆最终 Request 做重放)→ Bearer + CSRF → 401 刷新重放 → 40024 再认证。 */
function attachMiddlewares<P extends {}>(c: Client<P>): Client<P> {
  c.use(timeoutMiddleware)
  c.use(authMiddleware)
  c.use(refreshMiddleware)
  c.use(reauthMiddleware)
  return c
}

/**
 * 按调用时的 runtime.apiBase 取客户端。客户端大多在模块顶层创建(`export const client = createApiClient()`),
 * 那时 createSmartAdmin 可能还没跑、apiBase 尚未落定;所以每次调用前核对一次,变了就按新地址重建,
 * 调用方后加的中间件(use)一并带过去。
 */
function followApiBase<P extends {}>(build: (baseUrl: string) => Client<P>): Client<P> {
  let base = runtime.apiBase
  let inner = build(base)
  const added: Middleware[] = []
  const current = () => {
    if (base !== runtime.apiBase) {
      base = runtime.apiBase
      inner = build(base)
      if (added.length) inner.use(...added)
    }
    return inner
  }
  return new Proxy({} as Client<P>, {
    get(_target, key) {
      if (key === 'use')
        return (...mw: Middleware[]) => {
          added.push(...mw)
          current().use(...mw)
        }
      if (key === 'eject')
        return (...mw: Middleware[]) => {
          for (const m of mw) {
            const i = added.indexOf(m)
            if (i >= 0) added.splice(i, 1)
          }
          current().eject(...mw)
        }
      const c = current() as unknown as Record<PropertyKey, unknown>
      const v = c[key]
      return typeof v === 'function' ? (v as (...args: unknown[]) => unknown).bind(c) : v
    },
  })
}

/**
 * 消费方自己端点的类型化客户端:与内核 client 同 apiBase、同一条中间件链,只是 paths 换成自己生成的 schema。
 * credentials:'include'——Cookie 会话(Session:CookieMode)静默刷新/登出依赖 HttpOnly refresh Cookie;
 * 同源默认也会带 Cookie,显式 include 覆盖 apiBase 跨源场景(需后端 CORS AllowCredentials)。
 */
export function createApiClient<P extends {}>(): Client<P> {
  return followApiBase(baseUrl =>
    attachMiddlewares(createClient<P>({ baseUrl, credentials: 'include' })),
  )
}

/** 内核客户端(内核端点的 paths)。 */
export const client = createApiClient<paths>()

// 刷新专用客户端:不挂刷新中间件,避免刷新自身 401 触发递归;
// 但也要有超时闸:它挂住时路由守卫的静默续期会一起卡住,整页停在白屏。
const bare = followApiBase(baseUrl => {
  const c = createClient<paths>({ baseUrl, credentials: 'include' })
  c.use(timeoutMiddleware)
  return c
})
