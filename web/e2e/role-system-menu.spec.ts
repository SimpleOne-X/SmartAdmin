import { test, expect, type Locator, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'
import { apiAdminToken, apiCreateRole } from './api'

/**
 * 系统自带的菜单只能授给内置角色(「用户管理」「角色管理」两个页面除外,普通管理员靠它们维护用户与角色):
 * 角色页的「授权菜单」弹窗对非内置角色,在「系统」应用下只给「组织管理」目录里的这两个页面,
 * 不展示其余内核目录与页面,并用一行说明告诉超管为什么;内置角色(种子里的「系统管理员」)不受此限。
 *
 * 角色由接口新建,种子之外新建的角色 Id 落在内置区间之外,天然是非内置角色。
 */

const SUFFIX = Date.now().toString(36)
const CUSTOM_ROLE = `E2E 自定义角色 ${SUFFIX}`
const BUILTIN_ROLE = '系统管理员'

const SYSTEM_MODULE = /^(系统|System)$/
const BUSINESS_MODULE = /^(业务中心|Business Center)$/
const HINT =
  /系统自带的菜单只能授给内置角色|Menus that ship with the system can only be granted to built-in roles/
/** 放开的两个页面:非内置角色的弹窗里要看得到。 */
const OPEN_PAGES = [/用户管理|Users/, /角色管理|Roles/]
/** 其余内核目录与页面,任何一个出现在非内置角色的弹窗里都是泄漏。 */
const HIDDEN_SYSTEM_MENUS =
  /系统运维|任务调度|日志审计|文件管理|机构管理|岗位管理|System Operations|Organizations|Positions/

/** 超管登录,进角色管理,打开指定角色(整行文字包含 roleName)的「授权菜单」弹窗。 */
async function openRoleGrantSheet(page: Page, roleName: string): Promise<Locator> {
  await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
  // /system/* 只挂在「系统」应用下,不能指望登录后碰巧落在那儿
  await enterApp(page, SYSTEM_APP)
  await page.goto('/system/role')
  await expect(page.locator('.n-data-table')).toBeVisible({ timeout: 10_000 })
  const row = page.locator('.n-data-table-tr').filter({ hasText: roleName })
  await expect(row, `角色列表里应恰有一行包含「${roleName}」`).toHaveCount(1, { timeout: 5_000 })
  await row.getByText(/更多|More/i).click()
  await page
    .locator('.n-dropdown-option')
    .filter({ hasText: /授权菜单|Grant menus/ })
    .click()
  const sheet = page.locator('.n-modal').filter({ has: page.locator('.gt-scroll') })
  await expect(sheet.locator('.gt-scroll .gt-group').first()).toBeVisible({ timeout: 10_000 })
  return sheet
}

/** 展开「应用」下拉,读出全部选项文字,再收起。 */
async function moduleOptionLabels(sheet: Locator): Promise<string[]> {
  const trigger = sheet.locator('.gt-module')
  const options = sheet.page().locator('.n-base-select-option')
  await trigger.click()
  await expect(options.first()).toBeVisible({ timeout: 5_000 })
  const labels = (await options.allInnerTexts()).map(s => s.trim()).filter(Boolean)
  await trigger.click()
  await expect(options.first()).toBeHidden({ timeout: 5_000 })
  return labels
}

/** 在「应用」下拉里选中指定应用,等列表换成该应用的内容。 */
async function pickModule(sheet: Locator, name: RegExp) {
  const options = sheet.page().locator('.n-base-select-option')
  await sheet.locator('.gt-module').click()
  await options.filter({ hasText: name }).click()
  await expect(options.first()).toBeHidden({ timeout: 5_000 })
  await expect(sheet.locator('.gt-scroll .gt-group').first()).toBeVisible({ timeout: 5_000 })
}

test.describe('系统自带的菜单只授内置角色', () => {
  test.beforeAll(async ({ request }) => {
    const token = await apiAdminToken(request)
    await apiCreateRole(request, token, { name: CUSTOM_ROLE, code: `e2e_custom_${SUFFIX}` })
  })

  test('非内置角色:「系统」应用里只有用户管理、角色管理,其余系统自带的菜单看不到,弹窗里有说明', async ({
    page,
  }) => {
    const sheet = await openRoleGrantSheet(page, CUSTOM_ROLE)

    // 「系统」应用还在(里面有放开的两个页面),业务中心也在
    const labels = await moduleOptionLabels(sheet)
    expect(
      labels.some(l => SYSTEM_MODULE.test(l)),
      `应用下拉应含「系统」(用户管理、角色管理在它下面),实际 [${labels.join(', ')}]`,
    ).toBe(true)
    expect(
      labels.some(l => BUSINESS_MODULE.test(l)),
      `应用下拉应含「业务中心」,实际 [${labels.join(', ')}]`,
    ).toBe(true)

    // 切到「系统」:放开的两个页面都在,其余内核目录与页面一个都没有
    await pickModule(sheet, SYSTEM_MODULE)
    const list = sheet.locator('.gt-scroll')
    for (const title of OPEN_PAGES) await expect(list).toContainText(title)
    await expect(list).not.toContainText(HIDDEN_SYSTEM_MENUS)

    await expect(sheet.getByText(HINT)).toBeVisible()
  })

  test('内置角色「系统管理员」:应用下拉有「系统」,没有那行说明', async ({ page }) => {
    const sheet = await openRoleGrantSheet(page, BUILTIN_ROLE)

    const labels = await moduleOptionLabels(sheet)
    expect(
      labels.some(l => SYSTEM_MODULE.test(l)),
      `应用下拉应含「系统」,实际 [${labels.join(', ')}]`,
    ).toBe(true)

    await expect(sheet.getByText(HINT)).toHaveCount(0)
  })
})
