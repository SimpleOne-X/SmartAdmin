import { test, expect } from '@playwright/test'
import { login, enterApp } from './helpers'

/**
 * 在应用 A 里直接访问应用 B 的页面地址(书签、第三方后台配的主页):动态路由只注册当前应用的菜单,
 * 这个地址落到通配 404。404 页要把「页面属于别的应用」和「地址不存在」分开,并能一键切过去。
 *
 * 必须真浏览器:切换后能不能落到目标地址,取决于新应用的动态路由建好之后 router.replace 能否解析,
 * 单测里 router 是 mock 的,证明不了这一点。
 *
 * 前置:默认种子自带的「系统」(挂着 /workbench)与「业务中心」两个应用,超管账号可登录。
 */
const BUSINESS_APP = /^(业务中心|Business Center)$/

test('跨应用地址:404 页说明页面属于「系统」,一键切换后落在该地址', async ({ page }) => {
  await login(page)
  await enterApp(page, BUSINESS_APP)

  // 整页加载:守卫按持久化的当前应用(业务中心)重建路由,/workbench 在其中不存在
  await page.goto('/workbench')
  const notFound = page.locator('.nf')
  await expect(notFound).toContainText('404')
  await expect(notFound).toContainText(/该页面属于「系统」应用|belongs to the "System" app/)
  await expect(notFound).toContainText(/当前应用：业务中心|Current app: Business Center/)

  await notFound
    .getByRole('button', { name: /切换到「系统」并打开|Switch to "System" and open it/ })
    .click()

  await expect(page).toHaveURL(/\/workbench$/)
  await expect(page.locator('.stat-val'), '切到「系统」后工作台没渲染出来').toHaveCount(4)
  await expect(page.locator('.nf')).toHaveCount(0)
})

test('地址真的不存在:仍是 404,只提示当前应用并给切换应用的入口', async ({ page }) => {
  await login(page)
  await enterApp(page, BUSINESS_APP)

  await page.goto('/no-such-page')
  const notFound = page.locator('.nf')
  await expect(notFound).toContainText('404')
  await expect(notFound).toContainText(/页面不存在|Page not found/)
  await expect(notFound).toContainText(/当前应用：业务中心|Current app: Business Center/)
  // 反查要等所有其它应用的菜单回来才有结论;给它一个落定的机会,再断"没有归属提示"
  await expect(notFound.getByRole('button', { name: /切换应用|Switch app/ })).toBeVisible()
  await expect(notFound).not.toContainText(/该页面属于|belongs to the/)

  await notFound.getByRole('button', { name: /切换应用|Switch app/ }).click()
  await expect(page).toHaveURL(/\/module/)
})
