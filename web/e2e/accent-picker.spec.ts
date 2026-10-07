import { test, expect } from '@playwright/test'
import { login, enterApp, SYSTEM_APP } from './helpers'

/**
 * 外观设置的强调色:色值写在色名那一行(「蓝色  #0A84FF」),不能漏到色块行里。
 * 自定义取色器触发器里的色值文字必须隐藏,否则会露在彩虹圆点右侧。
 */
test('强调色:色值在名称行,取色触发器完全透明', async ({ page }) => {
  await login(page)
  await enterApp(page, SYSTEM_APP)

  await page.locator('.top-user').click()
  await page
    .getByText(/^(外观设置|系统设置|Appearance|Settings)$/)
    .first()
    .click()

  const card = page.locator('.accent-card')
  await expect(card).toBeVisible()
  await expect(card.locator('.name .hex')).toHaveText(/^#[0-9A-F]{6}$/)
  // 取色触发器盖在彩虹圆点上、必须完全透明(innerText 会算上 opacity:0 的文字,所以断言计算样式)
  const picker = card.locator('.row .n-color-picker')
  await expect(picker).toHaveCount(1)
  await expect(picker).toHaveCSS('opacity', '0')

  // 色值与色名同一行
  const nameBox = (await card.locator('.name b').boundingBox())!
  const hexBox = (await card.locator('.name .hex').boundingBox())!
  expect(Math.abs(nameBox.y - hexBox.y)).toBeLessThan(6)
})
