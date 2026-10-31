import { test, expect, type Page } from '@playwright/test'
import {
  apiAdminSession,
  apiAdminToken,
  apiClearExternalProvider,
  apiConfigureWeCom,
  apiFirstModuleId,
  type AdminSession,
} from './api'
import { enterFirstAppIfNeeded } from './helpers'

/**
 * 企业微信客户端里打开登录页:前端直接发起企业微信登录,后端按 UA 给网页授权地址(扫码页在客户端里用不了)。
 * 用例开头通过管理接口在库里配一套假的企业微信应用,结束后清掉。
 * 只有 UA 带 wxwork 时登录页才会自动跳,其余用例用的是默认 UA,不受影响。
 */

// 企业微信客户端的 UA 带 wxwork,也带微信的 MicroMessenger
const WECOM_UA =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) ' +
  'Mobile/15E148 wxwork/4.1.20 MicroMessenger/7.0.1 Language/zh'

test.use({ userAgent: WECOM_UA })

test.beforeAll(async ({ playwright }) => {
  const request = await playwright.request.newContext()
  try {
    await apiConfigureWeCom(request, await apiAdminToken(request))
  } finally {
    await request.dispose()
  }
})

test.afterAll(async ({ playwright }) => {
  const request = await playwright.request.newContext()
  try {
    await apiClearExternalProvider(request, await apiAdminToken(request), 'wecom')
  } finally {
    await request.dispose()
  }
})

test('企业微信客户端里打开登录页 → 直接走网页授权;同一会话回到登录页不再自动跳', async ({
  page,
}) => {
  // 真打到后端拿它的 302,但不让浏览器跟去企业微信:page.route 只拦重定向链的第一跳,
  // 拦 302 的目标拦不住,只能在发起授权这一跳上截住
  let authorizeHits = 0
  let location = ''
  await page.route('**/api/v1/auth/external/wecom/authorize', async route => {
    authorizeHits++
    const resp = await route.fetch({ maxRedirects: 0 })
    location = resp.headers()['location'] ?? ''
    await route.fulfill({ status: 200, contentType: 'text/html', body: '<p>wecom</p>' })
  })

  await page.goto('/login')
  await expect.poll(() => location, { timeout: 10_000 }).not.toBe('')

  const url = new URL(location)
  expect(`${url.origin}${url.pathname}`).toBe('https://open.weixin.qq.com/connect/oauth2/authorize')
  expect(url.searchParams.get('appid')).toBe('ww-e2e-corp')
  expect(url.searchParams.get('agentid')).toBe('1000002')
  expect(url.searchParams.get('response_type')).toBe('code')
  expect(url.searchParams.get('scope')).toBe('snsapi_base')
  expect(url.searchParams.get('state')).toMatch(/^[A-Za-z0-9]{1,128}$/)
  expect(url.searchParams.get('redirect_uri')).toMatch(
    /\/api\/v1\/auth\/external\/wecom\/callback$/,
  )
  expect(url.hash).toBe('#wechat_redirect')

  // 等页面自己发起的跳转落定(被 route.fulfill 成占位页)再 goto:读到 location 的那一刻
  // 浏览器还在往授权地址导航,此时立刻 goto 会撞上它,报 interrupted by another navigation / ERR_ABORTED
  await page.waitForURL(/\/api\/v1\/auth\/external\/wecom\/authorize/)
  await expect(page.getByText('wecom')).toBeVisible()

  // 登录失败或主动退出后回到登录页:停在这里让人自己选,不来回跳
  await page.goto('/login')
  await expect(page.getByPlaceholder(/账号|account/i)).toBeVisible()
  await page.waitForLoadState('networkidle')
  expect(authorizeHits).toBe(1)
})

/** pinia 持久化(stores/user.ts)落在 localStorage 'user' 里的会话形状。 */
function storedUser(s: AdminSession | null) {
  return {
    accessToken: s?.accessToken ?? 'invalid-access-token',
    refreshToken: s?.refreshToken ?? '',
    cookieSession: false,
    userInfo: s
      ? {
          userId: Number(s.userId),
          account: s.account,
          name: s.name,
          mustChangePassword: false,
          isSuperAdmin: !!s.isSuperAdmin,
        }
      : { userId: 1, account: 'stale', name: 'stale', mustChangePassword: false },
    issuedAt: Date.now(),
  }
}

/**
 * 每个文档加载前把会话写进 localStorage,但只写一次:页面里的退出或会话失效清掉它之后,
 * 后面的整页跳转(自动登录会顶层导航)不能再把它写回来。
 */
async function seedSession(page: Page, session: AdminSession | null) {
  await page.addInitScript(user => {
    if (sessionStorage.getItem('e2e-session-seeded')) return
    localStorage.setItem('user', JSON.stringify(user))
    sessionStorage.setItem('e2e-session-seeded', '1')
  }, storedUser(session))
}

/** 截住发起授权这一跳并计数;redirectTo 给了就回 302,否则回占位页。 */
async function countAuthorize(page: Page, redirectTo?: string) {
  const hits = { count: 0 }
  await page.route('**/api/v1/auth/external/wecom/authorize', async route => {
    hits.count++
    if (redirectTo) await route.fulfill({ status: 302, headers: { location: redirectTo } })
    else await route.fulfill({ status: 200, contentType: 'text/html', body: '<p>wecom</p>' })
  })
  return hits
}

test('未登录打开深链 → 静默登录后回到原页面', async ({ page, request, baseURL }) => {
  const session = await apiAdminSession(request)
  const hits = await countAuthorize(page, `${baseURL}/oauth/callback?ticket=e2e`)
  // 企业微信那边换到的会话由后端签发;这里换成一份真实会话,回调页落地后才能进业务页
  let exchanges = 0
  await page.route('**/api/v1/auth/external/exchange', route => {
    exchanges++
    return route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ code: 0, data: session }),
    })
  })
  // 登录态过期的老用户:浏览器里没有会话,但记得上次在哪个应用(多应用且没设默认时,没有它会落到应用选择页)
  const moduleId = await apiFirstModuleId(request, session.accessToken)
  await page.addInitScript(id => {
    localStorage.setItem('auth', JSON.stringify({ currentModuleId: id }))
  }, moduleId)

  await page.goto('/personal/notice')

  // 不能靠 waitForURL:goto 返回时地址本来就是 /personal/notice,守卫还没把它送去登录页。
  // 顶栏只有进了应用壳才有,等到它再看地址,才说明是回调之后落到的原页面。
  await expect(page.locator('.top-user')).toBeVisible({ timeout: 15_000 })
  expect(new URL(page.url()).pathname).toBe('/personal/notice')
  expect(hits.count).toBe(1)
  expect(exchanges).toBe(1)
})

test('没记住应用的用户点卡片深链 → 选应用后落到卡片指向的页面', async ({
  page,
  request,
  baseURL,
}) => {
  const session = await apiAdminSession(request)
  await countAuthorize(page, `${baseURL}/oauth/callback?ticket=e2e`)
  await page.route('**/api/v1/auth/external/exchange', route =>
    route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ code: 0, data: session }),
    }),
  )

  await page.goto('/personal/notice')

  await page.locator('.mod-card').first().waitFor({ timeout: 15_000 })
  await page.locator('.mod-card').first().click()

  await expect(page.locator('.top-user')).toBeVisible({ timeout: 15_000 })
  await expect.poll(() => new URL(page.url()).pathname).toBe('/personal/notice')
})

test('同一浏览器一页退出 → 另一页留在登录页,不被自动登录拉回', async ({ context, request }) => {
  const session = await apiAdminSession(request)
  const leaving = await context.newPage()
  const staying = await context.newPage()
  const hits = await countAuthorize(staying)
  await seedSession(leaving, session)

  for (const page of [leaving, staying]) {
    await page.goto('/')
    await page.locator('.top-user, .mod-card').first().waitFor()
    await enterFirstAppIfNeeded(page)
    await expect(page.locator('.top-user')).toBeVisible()
  }

  // 登录方式接口回来后紧跟着就是自动登录的判断
  const providersLoaded = staying.waitForResponse(
    r => r.request().method() === 'GET' && r.url().includes('/api/v1/auth/external/providers'),
  )
  await leaving.locator('.top-user').click()
  await leaving
    .locator('.n-dropdown-option')
    .filter({ hasText: /退出登录|Log out/ })
    .click()

  await providersLoaded
  // 跳转紧跟在接口返回之后发起,留一小段时间:这段时间里一次授权请求都没有才算没跳
  await staying.waitForTimeout(500)
  expect(hits.count).toBe(0)
  await expect(staying).toHaveURL(/\/login/)
  await expect(staying.getByPlaceholder(/账号|account/i)).toBeVisible()
})

test('本地有登录态、没进过登录页时主动退出 → 不再自动登录', async ({ page, request }) => {
  const hits = await countAuthorize(page)
  await seedSession(page, await apiAdminSession(request))

  await page.goto('/')
  // goto 在 load 就返回,守卫里重建路由还没落定:等到顶栏或应用选择页任一个出现,再决定要不要选应用
  await page.locator('.top-user, .mod-card').first().waitFor()
  await enterFirstAppIfNeeded(page)
  await page.locator('.top-user').click()
  // 退出是页内路由切换,没有新文档,networkidle 在这里是空等;改为等登录方式接口回来——自动登录的判断紧随其后
  const providersLoaded = page.waitForResponse(
    r => r.request().method() === 'GET' && r.url().includes('/api/v1/auth/external/providers'),
  )
  await page
    .locator('.n-dropdown-option')
    .filter({ hasText: /退出登录|Log out/ })
    .click()

  await providersLoaded
  // 跳转紧跟在接口返回之后发起,留一小段时间:这段时间里一次授权请求都没有才算没跳
  await page.waitForTimeout(500)
  expect(hits.count).toBe(0)
  await expect(page).toHaveURL(/\/login/)
  await expect(page.getByPlaceholder(/账号|account/i)).toBeVisible()
  await expect(page.locator('[href*="/wecom/authorize"]').first()).toBeVisible()
})

test('登录态失效回到登录页 → 仍会自动登录', async ({ page }) => {
  const hits = await countAuthorize(page)
  await seedSession(page, null)

  await page.goto('/personal/notice')

  await expect.poll(() => hits.count, { timeout: 15_000 }).toBe(1)
  await page.waitForLoadState('networkidle')
  expect(hits.count).toBe(1)
})
