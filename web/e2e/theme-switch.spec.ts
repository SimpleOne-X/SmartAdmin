import { test, expect } from '@playwright/test'
import { login, enterApp, SYSTEM_APP } from './helpers'

/**
 * 明暗切换与提示气泡:
 *  1) 亮色下 Tooltip 的底色与文字色必须不同(防白底白字:全局 Popover 覆盖会压过 Tooltip 自带的底色);
 *  2) 明暗翻面走 View Transition 交叉淡入,不给每个元素挂颜色过渡(暗→亮时输入框边框层会闪线)。
 */
test('亮色提示气泡文字可读;明暗切换走整屏交叉淡入', async ({ page }) => {
  // 记录 startViewTransition 的调用次数(真实调用照常放行,快照与动画照常发生)
  await page.addInitScript(() => {
    const w = window as unknown as { __vt: number }
    w.__vt = 0
    const orig = document.startViewTransition?.bind(document)
    if (orig) {
      document.startViewTransition = ((cb: never) => {
        w.__vt++
        return orig(cb)
      }) as typeof document.startViewTransition
    }
    localStorage.setItem('app', JSON.stringify({ themeScheme: 'light' }))
  })

  await login(page)
  await enterApp(page, SYSTEM_APP)
  const html = page.locator('html')
  await expect(html).not.toHaveAttribute('data-theme', 'dark')

  // 1) 亮色:悬停切换主题按钮,气泡底色与文字色不同,且底色不是白
  await page
    .getByRole('button', { name: /^(亮色|浅色|Light)/ })
    .first()
    .hover()
  const tip = page.locator('.n-popover.n-tooltip, .n-tooltip').last()
  await expect(tip).toBeVisible()
  const { bg, fg } = await tip.evaluate(el => {
    const cs = getComputedStyle(el)
    return { bg: cs.backgroundColor, fg: cs.color }
  })
  expect(bg, '气泡底色与文字色不能相同').not.toBe(fg)
  expect(bg, '亮色气泡底色不能是白').not.toMatch(
    /^rgb\(255, 255, 255\)$|^rgba\(255, 255, 255, 1\)$/,
  )

  // 2) 切到暗色再切回:两次翻面各走一次 View Transition,最终 data-theme 正确
  const toggle = page.getByRole('button', { name: /^(亮色|浅色|暗色|深色|Light|Dark)/ }).first()
  await toggle.click()
  await expect(html).toHaveAttribute('data-theme', 'dark')
  await toggle.click()
  await expect(html).not.toHaveAttribute('data-theme', 'dark')
  expect(await page.evaluate(() => (window as unknown as { __vt: number }).__vt)).toBe(2)
})
