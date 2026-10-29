// 同一浏览器的多个标签页共用 localStorage 里的那份会话(默认 body 模式)。刷新令牌每次续期都轮换,
// 所以别的标签页续期后,本页内存里的旧令牌必须跟上;别的标签页退出后,本页也要跟着回登录页。
import { loginLocation, safeRedirect } from '#/router/loginRedirect'
import { USER_STORAGE_KEY, parseStoredUser, useUserStore } from './user'

/** 服务端:刷新令牌刚被轮换(同一浏览器另一标签页抢先续期),本次只拒绝、不吊销会话(ErrorCode.RefreshTokenSuperseded)。 */
export const REFRESH_SUPERSEDED_CODE = 40037

// 这三个页面是中转落点:本身不是登录后该回的地方,回跳地址取它们自带的 redirect(没有就回首页)。
const TRANSIT_PATHS = new Set(['/boot-error', '/oauth/callback', '/mfa/bind'])

/**
 * 清本地会话并回登录页(惰性引入 auth / router,避免与 api/client 形成静态循环依赖)。
 * 令牌被服务端判失效、或别的标签页已退出登录时调用。
 */
export async function endLocalSession(): Promise<void> {
  useUserStore().clear()
  const { useAuthStore } = await import('#/stores/auth')
  useAuthStore().reset()
  const { router, resetRouter } = await import('#/router')
  resetRouter()
  const current = router.currentRoute.value
  if (current.path === '/login') return
  const back = TRANSIT_PATHS.has(current.path)
    ? (safeRedirect(current.query.redirect) ?? '/')
    : current.fullPath
  router.replace(loginLocation(back))
}

function onStorage(e: StorageEvent) {
  if (e.key !== USER_STORAGE_KEY) return
  const saved = parseStoredUser(e.newValue)
  if (!saved) return
  const user = useUserStore()
  const signedOut =
    !saved.accessToken && !saved.refreshToken && !saved.userInfo && !saved.cookieSession
  if (!signedOut) {
    // 候选取事件自带的 newValue,不重读存储:存储可能已被本页或别的标签页又改写过。
    // 候选比本页旧、本页持有更新的令牌时,把本页的会话写回,让存储最终落在最新令牌上。
    if (!user.adoptFromStorage(saved) && user.refreshToken && saved.issuedAt < user.issuedAt) {
      user.$persist()
    }
    return
  }
  // 本页本来就没有会话(停在公开页)时不去动它。
  if (user.accessToken || user.refreshToken || user.cookieSession) void endLocalSession()
}

let installed = false

/** 监听其它标签页对会话的改动(幂等,整个应用只装一次)。 */
export function installSessionSync(): void {
  if (installed || typeof window === 'undefined') return
  installed = true
  window.addEventListener('storage', onStorage)
}
