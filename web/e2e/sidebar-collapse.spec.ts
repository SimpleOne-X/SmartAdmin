import { test, expect } from '@playwright/test'
import { login, enterApp, SYSTEM_APP } from './helpers'

/**
 * 侧栏:品牌区只显示站点名(不带「应用编码 · 名称」小字);顶栏的折叠按钮把侧栏整个隐藏,内容区铺满,
 * 再点一次恢复。折叠状态持久化在 localStorage,每个用例新开 context,不会串。
 */
test('侧栏品牌只显示站点名;折叠按钮整个隐藏侧栏,再点恢复', async ({ page }) => {
  await login(page)
  await enterApp(page, SYSTEM_APP)

  const aside = page.locator('aside.aside')
  await expect(aside).toBeVisible()
  const brand = aside.locator('.brand-name')
  await expect(brand).toBeVisible()
  await expect(brand.locator('small')).toHaveCount(0)
  await expect(brand).not.toContainText('·')

  const content = page.locator('main.content')
  const widthWith = (await content.boundingBox())!.width

  const toggle = page.getByRole('button', { name: /折叠菜单|Collapse/ }).first()
  await toggle.click()
  // 侧栏不卸载(收起要有宽度过渡):动画走完宽度归零,Playwright 视为不可见;inert 让它退出 Tab 序列与无障碍树
  await expect(aside).toBeHidden()
  await expect(aside).toHaveJSProperty('inert', true)
  await expect.poll(async () => (await content.boundingBox())!.width).toBeGreaterThan(widthWith)

  // 侧栏没了,品牌回到顶栏
  await expect(page.locator('.bar .brand')).toBeVisible()

  await toggle.click()
  await expect(aside).toBeVisible()
  await expect(aside).toHaveJSProperty('inert', false)
  await expect.poll(async () => (await content.boundingBox())!.width).toBeCloseTo(widthWith, -1)
})
