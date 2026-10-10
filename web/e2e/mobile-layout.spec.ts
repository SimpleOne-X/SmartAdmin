import { expect, test } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'

/**
 * 手机宽度(390×844)下每个页面都能完整查看。
 *
 * 壳层窄档(内容区 < 600)是「整页自然滚动」:页面容器不定高,滚动全在 `.page` 上,表格是卡片列表、
 * 内容多长页面就多长。一旦哪块把自己钉成固定高度(flex 均分、虚拟滚动的 fill-height),
 * 数据就被关进一个小框里,框里再滚 —— 用户只看得见一两张卡片,页面却还有大片空白。
 * 这类问题在宽屏上完全看不出来,所以要在窄视口跑真实页面。
 *
 * 判据与数据多少无关:
 *   1. `.page` 不出现横向滚动(没有内容被推到视口右侧);
 *   2. 表格内部没有被裁住的纵向滚动区(scrollHeight > clientHeight)。
 *      窄档表格该整页自然滚,任何一个「框里还要再滚」的区域都是被关小了。
 */

test.use({ viewport: { width: 390, height: 844 } })

/** 路由表里除登录 / 应用选择 / 绑定回调之外的全部页面。 */
const PAGES = [
  '/workbench',
  '/personal/profile',
  '/personal/password',
  '/personal/security',
  '/personal/bindings',
  '/personal/sessions',
  '/personal/notice',
  '/system/user',
  '/system/role',
  '/system/org',
  '/system/position',
  '/system/menu',
  '/system/module',
  '/system/dict',
  '/system/config',
  '/system/notice',
  '/system/session',
  '/system/recycle',
  '/system/file',
  '/system/job',
  '/system/job-log',
  '/system/job-monitor',
  '/system/log/op',
  '/system/log/login',
  '/system/log/exception',
  '/system/monitor',
  '/system/cache',
  '/system/ai-model',
  '/system/ai-usage',
  '/system/api-docs',
]

/** 在页面里量:横向溢出的像素数,和表格内部被裁住的纵向滚动区。 */
function measure() {
  const pageEl = document.querySelector('.page')
  const clipped: string[] = []
  for (const el of document.querySelectorAll('.page-view .smart-table *')) {
    if (el.matches('textarea, input')) continue
    const overflowY = getComputedStyle(el).overflowY
    if (overflowY !== 'auto' && overflowY !== 'scroll') continue
    if (el.clientHeight > 0 && el.scrollHeight > el.clientHeight + 2) {
      clipped.push(
        `${el.tagName.toLowerCase()}.${[...el.classList].slice(0, 2).join('.')} 可视 ${el.clientHeight}px / 内容 ${el.scrollHeight}px`,
      )
    }
  }
  return { overflowX: pageEl ? pageEl.scrollWidth - pageEl.clientWidth : -1, clipped }
}

test.describe('手机宽度布局', () => {
  test('所有页面:不横向溢出,表格不被关进小框', async ({ page }) => {
    test.setTimeout(240_000)
    await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
    await enterApp(page, SYSTEM_APP)

    const problems: string[] = []
    for (const path of PAGES) {
      await page.goto(path, { waitUntil: 'domcontentloaded' })
      await expect(page.locator('.page-view').first()).toBeVisible({ timeout: 15_000 })
      await page.waitForLoadState('networkidle')

      const { overflowX, clipped } = await page.evaluate(measure)
      if (overflowX > 1) problems.push(`${path}: 页面横向溢出 ${overflowX}px`)
      for (const c of clipped) problems.push(`${path}: 表格内有被裁住的滚动区 ${c}`)
    }
    expect(problems, '手机宽度下这些页面看不全').toEqual([])
  })

  test('树表(机构、菜单):手机宽度下每个节点都有一张卡片', async ({ page }) => {
    // 库的卡片模式只画传入的这一层行、不认 children;页面不自己把树摊平,子节点在手机上就消失了
    test.setTimeout(120_000)
    await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
    await enterApp(page, SYSTEM_APP)

    const TREES = [
      { path: '/system/org', expandAll: false }, // 机构进门就是全展开
      { path: '/system/menu', expandAll: true }, // 菜单进门全折叠,要点「展开全部」
    ]
    for (const { path, expandAll } of TREES) {
      // 桌面宽度 + 超高视口:定高表格的虚拟滚动会把全部行渲染出来,行数才是节点总数
      await page.setViewportSize({ width: 1400, height: 3000 })
      await page.goto(path)
      const rows = page.locator('.n-data-table-tbody .n-data-table-tr')
      await expect(rows.first()).toBeVisible({ timeout: 15_000 })
      if (expandAll) {
        await page.getByRole('button', { name: /展开全部|Expand all/ }).click()
        await page.waitForLoadState('networkidle')
      }
      const nodes = await rows.count()
      expect(nodes, `${path} 桌面树表应有多层节点`).toBeGreaterThan(3)

      await page.setViewportSize({ width: 390, height: 844 })
      await page.goto(path)
      await expect(
        page.locator('.smart-table-cards > *'),
        `${path} 手机卡片数应等于节点数`,
      ).toHaveCount(nodes, { timeout: 15_000 })
    }
  })
})

/**
 * 窄档壳层里的定高页与表单弹窗。
 *
 * 内核页面窄档下的表全是卡片列表(有自然高度),所以几处「只在特定形状下才出毛病」的 CSS 没有现成的页面可测:
 * 这里在真实页面上手动补上那个形状(给 `.page` 加 `bp-narrow`、给分栏容器加 `--row`、给弹窗卡片加全屏类),
 * 断言的是最终的版面几何,不是类名本身。
 */

test.describe('窄档壳层:定高页与表单弹窗', () => {
  test('定高页:表格仍是表格形态时,窄档壳层保持定高,表体不塌', async ({ page }) => {
    test.setTimeout(120_000)
    await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
    await enterApp(page, SYSTEM_APP)
    // 宽视口里表格是表格形态;再给 .page 补上 bp-narrow,等于「壳层窄档 + 没开 card-on-narrow 的 fill-height 表」
    await page.setViewportSize({ width: 1400, height: 900 })
    await page.goto('/system/recycle') // .fill-page > .fill-tabs 形状
    await expect(page.locator('.n-data-table-base-table-body').first()).toBeVisible({
      timeout: 15_000,
    })
    await page.waitForLoadState('networkidle')
    const body = () => page.locator('.n-data-table-base-table-body').first().boundingBox()
    expect((await body())!.height, '宽档基线:表体本来有高度').toBeGreaterThan(300)

    await page.evaluate(() => document.querySelector('.page')!.classList.add('bp-narrow'))

    await expect
      .poll(async () => (await body())?.height ?? 0, { message: '窄档壳层下表体塌了' })
      .toBeGreaterThan(300)
  })

  test('.fill-split 窄档:左右分栏也叠成上下,两栏时每栏有定高', async ({ page }) => {
    await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
    await enterApp(page, SYSTEM_APP)
    await page.goto('/system/dict')
    await expect(page.locator('.fill-split').first()).toBeVisible({ timeout: 15_000 })
    await page.waitForLoadState('networkidle')
    // 字典页窄档只渲染上栏(下栏走底部抽屉);克隆出第二栏、补上 --row 变体,模拟「两栏左右分栏」的页面
    await page.evaluate(() => {
      const split = document.querySelector('.fill-split')!
      split.classList.add('fill-split--row')
      split.appendChild(split.firstElementChild!.cloneNode(true))
    })

    const geometry = () =>
      page.evaluate(() => {
        const split = document.querySelector('.fill-split')!
        const [a, b] = [...split.children].map(c => c.getBoundingClientRect())
        return {
          direction: getComputedStyle(split).flexDirection,
          firstH: Math.round(a.height),
          secondH: Math.round(b.height),
          stacked: b.top >= a.bottom - 1, // 第二栏在第一栏下面,不是并排
          viewportH: innerHeight,
        }
      })
    await expect.poll(async () => (await geometry()).direction).toBe('column')
    const g = await geometry()
    expect(g.stacked, '--row 在窄档也该叠成上下').toBe(true)
    // 每栏定高 max(440px, 72dvh):上栏不会无限长、把下栏挤到很远的地方
    const cap = Math.max(440, g.viewportH * 0.72)
    expect(g.firstH).toBeCloseTo(cap, -1)
    expect(g.secondH).toBeCloseTo(cap, -1)
  })
})
