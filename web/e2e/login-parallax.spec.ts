import { test, expect, type Locator } from '@playwright/test'

/**
 * 登录页的指针交互(色场与卡片的反向视差、卡片面上跟着指针走的柔光)不跟随系统的「减少动态效果」:
 * Windows 关了「动画效果」、RDP 会话、省电模式下浏览器都会报 reduce,
 * 这时它仍是这页唯一的鼠标反馈,关掉就是一张不理人的静态页。
 * 所以 reduce 与 no-preference 两种偏好下都要:位移发生、柔光点亮、位移带缓动
 * (全局 reduce 规则会把 transition 压到 0.01ms,位移就成了瞬跳)。
 */

// translate 在被关掉时是 'none',parseFloat 得 NaN,统一当 0
const shift = (loc: Locator) =>
  loc.evaluate(el => Math.abs(parseFloat(getComputedStyle(el).translate) || 0))
const duration = (loc: Locator) =>
  loc.evaluate(el => parseFloat(getComputedStyle(el).transitionDuration))

for (const motion of ['no-preference', 'reduce'] as const) {
  test.describe(`系统动效偏好 ${motion}`, () => {
    test.use({ reducedMotion: motion })

    test('鼠标移动:卡片与色场位移、柔光点亮、位移带缓动', async ({ page }) => {
      await page.goto('/login')
      const card = page.locator('.desk-card')
      const desk = page.locator('.desk')
      await expect(card).toBeVisible()

      const size = page.viewportSize()!
      await page.mouse.move(size.width * 0.9, size.height * 0.15, { steps: 8 })

      await expect.poll(() => shift(card), { message: '卡片应朝指针方向位移' }).toBeGreaterThan(5)
      await expect.poll(() => shift(desk), { message: '色场应反向位移' }).toBeGreaterThan(5)

      const glowDisplay = await card.evaluate(el => getComputedStyle(el, '::after').display)
      expect(glowDisplay, '柔光不能被 display:none 掉').not.toBe('none')
      await expect
        .poll(() => card.evaluate(el => Number(getComputedStyle(el, '::after').opacity)))
        .toBe(1)

      expect(await duration(card), '卡片位移要有缓动,不能被压成瞬跳').toBeGreaterThan(0.1)
      expect(await duration(desk), '色场位移要有缓动,不能被压成瞬跳').toBeGreaterThan(0.1)
    })
  })
}
