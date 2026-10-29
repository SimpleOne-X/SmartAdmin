// 登录后回到原地址:被守卫 / 会话失效送去登录页时带上 `?redirect=`,登录页进入时暂存到 sessionStorage,
// 登录成功后取出跳回。暂存而不是只读 query,是因为登录会绕路(SSO 往返、MFA 绑定页)回到登录页,query 早已丢掉。
//
// redirect 来自 URL,不可信:只认站内绝对路径,否则就是开放重定向(`//evil.com` 会被浏览器当成协议相对地址)。
import type { RouteLocationRaw } from 'vue-router'

const STORAGE_KEY = 'smart-admin:login-redirect'

/** 站内可回跳的路径(含查询串);外站地址、协议相对地址、控制字符、登录页本身一律返回 null。 */
export function safeRedirect(raw: unknown): string | null {
  if (typeof raw !== 'string' || !raw.startsWith('/')) return null
  // 紧跟的 `/` `\` 及其百分号编码都拒绝:`//evil.com` 是协议相对地址,编码变体经路由解码后同样指向另一个路径。
  if (/^\/(?:[/\\]|%2f|%5c)/i.test(raw)) return null
  if (raw.startsWith('/login')) return null
  // 浏览器解析 URL 时会吃掉制表符 / 换行,`/\t/evil.com` 会变成 `//evil.com`。
  for (let i = 0; i < raw.length; i++) if (raw.charCodeAt(i) < 0x20) return null
  return raw
}

/** 暂存回跳地址;不安全的地址不写入,也不覆盖已暂存的。 */
export function rememberLoginRedirect(raw: unknown): void {
  const target = safeRedirect(raw)
  if (!target) return
  try {
    sessionStorage.setItem(STORAGE_KEY, target)
  } catch {
    // 存储不可用(隐私模式等)时退化为登录后回首页
  }
}

/** 取出暂存的回跳地址并删除;没有或不安全返回 null。 */
export function takeLoginRedirect(): string | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    sessionStorage.removeItem(STORAGE_KEY)
    return safeRedirect(raw)
  } catch {
    return null
  }
}

/** 去登录页的路由位置:当前地址不是首页就带上 redirect。 */
export function loginLocation(fullPath: string): RouteLocationRaw {
  return fullPath === '/'
    ? { path: '/login', replace: true }
    : { path: '/login', query: { redirect: fullPath }, replace: true }
}
