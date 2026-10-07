import { expect, test, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'

/**
 * 卡片间距只有一个值:`--gap-card`(8px),等于壳层面板之间的间距(见 skills/create-crud-frontend.md「列表页默认形状」第 7 条)。
 *
 * 两件事用真实渲染量,源码扫描抓不到:
 *   - 顶栏到首张卡片的距离:自然滚动页和满屏列表页必须一样。自然滚动页(工作台等)的 .page 不能多给顶部留白,
 *     否则从列表页切到工作台首张卡片会下跳一截;
 *   - 相邻卡片之间的间距:上下排、左右并排都是同一个值,不是各页写各页的 10 / 12 / 16。
 *
 * 只数「卡片」(.n-card、.side-filter、.dict-pane)之间的距离,卡片内部的 padding 不算。
 */

test.use({ viewport: { width: 1920, height: 1080 } })

const GAP = 8

/** 覆盖四种页面形状:自然滚动页(含统计卡网格)、满屏列表、左右分栏、上下分栏,外加个人中心的外壳。 */
const PAGES: { path: string; name: string }[] = [
  { path: '/workbench', name: '工作台(自然滚动 + 统计卡网格)' },
  { path: '/system/role', name: '角色管理(满屏列表)' },
  { path: '/system/user', name: '用户管理(左右分栏)' },
  { path: '/system/dict', name: '字典管理(上下分栏)' },
  { path: '/system/job-monitor', name: '任务监控(统计卡 + 双表)' },
  { path: '/system/ai-usage', name: 'AI 用量(自然滚动)' },
  { path: '/personal/profile', name: '个人资料(个人中心外壳)' },
]

/** 页面里最外层卡片的位置关系:顶栏到首卡的距离 + 所有相邻卡片间距里不等于 GAP 的那些。 */
async function measure(page: Page) {
  return page.evaluate(gap => {
    const sel = '.n-card:not(.n-modal), .side-filter, .dict-pane'
    // 整页刷新后外壳要等路由与菜单就位才出现:没出现就按「还没量到」返回,交给轮询重试,别在这里抛
    const headEl = document.querySelector('.shell-head')
    const root = document.querySelector('.page-view') ?? document.querySelector('.page')
    if (!headEl || !root) return { cards: 0, headToFirst: -1, badGaps: [] as number[] }
    const head = headEl.getBoundingClientRect()
    const rects = [...root.querySelectorAll<HTMLElement>(sel)]
      .filter(el => !el.parentElement!.closest(sel) && el.offsetParent !== null)
      .map(el => el.getBoundingClientRect())
      .filter(r => r.width > 60 && r.height > 30)
    if (!rects.length) return { cards: 0, headToFirst: -1, badGaps: [] as number[] }
    const bad = new Set<number>()
    for (let i = 0; i < rects.length; i++) {
      for (let j = i + 1; j < rects.length; j++) {
        const a = rects[i]!
        const b = rects[j]!
        // 只看相邻(< 48px)的两张;重叠方向 > 8px 才算「并排 / 上下排」
        const vOverlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top)
        const hOverlap = Math.min(a.right, b.right) - Math.max(a.left, b.left)
        if (vOverlap > 8) {
          const g = Math.max(a.left - b.right, b.left - a.right)
          if (g >= 0 && g < 48 && Math.round(g) !== gap) bad.add(Math.round(g))
        }
        if (hOverlap > 8) {
          const g = Math.max(a.top - b.bottom, b.top - a.bottom)
          if (g >= 0 && g < 48 && Math.round(g) !== gap) bad.add(Math.round(g))
        }
      }
    }
    return {
      cards: rects.length,
      headToFirst: Math.round(Math.min(...rects.map(r => r.top)) - head.bottom),
      badGaps: [...bad].toSorted((x, y) => x - y),
    }
  }, GAP)
}

test.describe('卡片间距', () => {
  for (const { path, name } of PAGES) {
    test(`${name}:顶栏到首卡 ${GAP}px,卡片之间都是 ${GAP}px`, async ({ page }) => {
      await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
      await enterApp(page, SYSTEM_APP)
      await page.goto(path)
      await expect(page.locator('.shell-head')).toBeVisible({ timeout: 15_000 })
      // 页面切换有 ~0.24s 的进场动画、数据加载会改变卡片出现的时机,轮询到稳定再断言
      await expect
        .poll(() => measure(page), { timeout: 10_000, message: `${path} 的卡片间距` })
        .toMatchObject({ headToFirst: GAP, badGaps: [] })
    })
  }
})
