import { test, expect, type Locator, type Page } from '@playwright/test'
import {
  ADMIN_ACCOUNT,
  ADMIN_PASSWORD,
  SYSTEM_APP,
  enterApp,
  enterFirstAppIfNeeded,
  login,
  sidebarLeafNames,
} from './helpers'
import { apiAdminToken, apiCreateUser } from './api'

/**
 * 用户单独授权主流程:超管给一个没有任何角色的用户「允许」岗位管理页 → 该用户登录能看到 →
 * 改成「拒绝」→ 该用户再登录就没有任何应用可进。走真实的弹窗,不经接口直改。
 *
 * 两条用例有先后依赖(第二条改的是第一条授出去的那条记录),所以串行。
 */

const ACCOUNT = `e2e_grant_${Date.now().toString(36)}`
const PASSWORD = 'TestPass123!'
const PAGE_TITLE = /^(岗位管理|Positions)$/

async function logout(page: Page) {
  await page.evaluate(() => localStorage.clear())
  await page.goto('/login')
  await expect(page).toHaveURL(/\/login/)
}

/** 超管登录,进用户管理,按账号搜到这个用户,打开「授权菜单」弹窗。 */
async function openGrantSheet(page: Page): Promise<Locator> {
  await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
  // /system/* 只挂在「系统」应用下,不能指望登录后碰巧落在那儿
  await enterApp(page, SYSTEM_APP)
  await page.goto('/system/user')
  await expect(page.locator('.n-data-table')).toBeVisible({ timeout: 10_000 })
  const input = page.locator('.smart-table-cond input[placeholder="请输入"]')
  await input.fill(ACCOUNT)
  await input.press('Enter')
  const row = page.locator('.n-data-table-tr').filter({ hasText: ACCOUNT })
  await expect(row).toBeVisible({ timeout: 5_000 })
  await row.getByText(/更多|More/i).click()
  await page
    .locator('.n-dropdown-option')
    .filter({ hasText: /授权菜单|Grant menus/ })
    .click()
  const sheet = page.locator('.n-modal').filter({ has: page.locator('.ut-scroll') })
  await expect(sheet.locator('.ugr').first()).toBeVisible({ timeout: 10_000 })
  return sheet
}

/** 把岗位管理页那一行切到指定状态并保存。 */
async function setPageState(sheet: Locator, state: RegExp) {
  const row = sheet
    .locator('.ugr.is-page')
    .filter({ has: sheet.page().locator('.ugr-title', { hasText: PAGE_TITLE }) })
  await row.locator('.n-radio-button').filter({ hasText: state }).click()
  await sheet.getByRole('button', { name: /^(保存|Save)$/ }).click()
  await expect(sheet.page().locator('.n-message').first()).toContainText(
    /授权已保存|Grants saved/,
    { timeout: 5_000 },
  )
}

test.describe('用户单独授权', () => {
  test.describe.configure({ mode: 'serial' })

  test.beforeAll(async ({ request }) => {
    const token = await apiAdminToken(request)
    await apiCreateUser(request, token, {
      account: ACCOUNT,
      name: 'E2E 授权用户',
      password: PASSWORD,
      forceTotp: false,
    })
  })

  test('允许一个页面 → 该用户看得到', async ({ page }) => {
    const sheet = await openGrantSheet(page)
    await setPageState(sheet, /^(允许|Allow)$/)

    await logout(page)
    await login(page, ACCOUNT, PASSWORD)
    await enterFirstAppIfNeeded(page)
    expect((await sidebarLeafNames(page)).some(n => PAGE_TITLE.test(n))).toBe(true)
  })

  test('改成拒绝 → 该用户没有应用可进', async ({ page }) => {
    const sheet = await openGrantSheet(page)
    await setPageState(sheet, /^(拒绝|Deny)$/)

    await logout(page)
    await login(page, ACCOUNT, PASSWORD)
    await expect(page).toHaveURL(/\/module/)
    await expect(page.locator('.n-empty')).toContainText(/暂无可访问的应用|No accessible/)
  })
})
