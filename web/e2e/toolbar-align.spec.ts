import { expect, test, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'

/**
 * 条件搜索栏与右侧操作区(新增 / 更多 / 图标)按可用宽度自适应:放得下就在同一行对齐,放不下才折成两行。
 *
 * 这是 smart-naive-table 条件栏所在工具栏在宽档与中档(表格卡片 ≥ 600)的布局契约,SmartAdmin 的列表页直接依赖它,
 * 这里用真实页面锁住:条件栏以 COND_MIN_WIDTH 为基准宽,同一行放得下就并排,
 * 放不下操作区整块折到条件栏上方(「操作区在上、条件栏在下」);头部(#toolbar 插槽)没有内容时不占位,
 * 条件栏左缘与表格左缘贴齐。
 * 基准宽按「并排时值输入框至少还剩 MIN_INLINE_VALUE_WIDTH」定,而不是按库的下限 100px:刚好放得下就并排,
 * 会把输入框挤到几乎没法输入(收起机构栏、头部多出一个按钮时尤其明显),宁可折成两行让输入框宽松。
 * 库调整这两个数时,下面两个常量同步改。
 *
 * 不写死分辨率:每个视口宽度读到的布局,都按「这一行的可用宽度够不够」来判断该并排还是该折行,
 * 够不够由页面里实际量到的操作区宽度与头部宽度算出来,不假设某个页面有几个按钮。
 * 档位阈值(600 / 1280)是库的约定,这里只用它判断读数是否已经跟上当前宽度(ResizeObserver 晚于视口变化)。
 */

/** 与库里条件栏的基准宽同值:并排所需的条件栏最小宽度(值输入框以外的部分中文约 476、英文约 501,再留 200 给输入框,取 710)。 */
const COND_MIN_WIDTH = 710
/** 并排时值输入框至少要有的宽度:再窄就挤得没法输入,应当折行。 */
const MIN_INLINE_VALUE_WIDTH = 200
/** 工具栏里各块之间的间距(库的 gap)。 */
const GAP = 12
/** 垂直中心相差不超过它,算在同一行对齐。 */
const SAME_ROW_TOLERANCE = 2

type Tier = 'wide' | 'mid' | 'narrow'

interface Reading {
  tier: Tier | null
  settled: boolean
  cardWidth: number
  barWidth: number
  barOverflow: number
  headWidth: number
  rightWidth: number
  /** 值输入框外壳(.smart-table-filter-value)的宽度。 */
  valueWidth: number
  /** 条件栏一行(.smart-table-cond__main)与右侧操作区的垂直中心。 */
  condCenterY: number
  rightCenterY: number
  /** 条件栏第一个控件(字段下拉)的左缘与工具栏的左缘:头部没有内容时两者应贴齐。 */
  condLeft: number
  barLeft: number
  /** 条件栏最右控件的右缘、操作区的左缘:并排时前者不能压到后者上。 */
  condRight: number
  rightLeft: number
}

function readBar(): Reading | null {
  const table = document.querySelector<HTMLElement>('.smart-table')
  const bar = table?.querySelector<HTMLElement>('.smart-table-toolbar')
  const cond = bar?.querySelector<HTMLElement>('.smart-table-cond__main')
  const right = bar?.querySelector<HTMLElement>('.smart-table-toolbar-right')
  if (!table || !bar || !cond || !right) return null
  const tier = (bar.className.match(/smart-table-toolbar--(wide|mid|narrow)\b/)?.[1] ??
    null) as Tier | null
  const width = table.clientWidth
  const expected = width === 0 ? 'wide' : width < 600 ? 'narrow' : width < 1280 ? 'mid' : 'wide'
  const head = bar.querySelector<HTMLElement>('.smart-table-toolbar-head')
  const c = cond.getBoundingClientRect()
  const r = right.getBoundingClientRect()
  const controls = [...cond.querySelectorAll(':scope > .n-button, .smart-table-cond__more')]
  return {
    tier,
    settled: tier === expected,
    cardWidth: Math.round(table.getBoundingClientRect().width * 10) / 10,
    barWidth: bar.clientWidth,
    barOverflow: bar.scrollWidth - bar.clientWidth,
    // 头部的内容宽度(子元素宽度之和):它在中档网格里会被拉伸到整列,自身宽度不是它要占的位置
    headWidth: [...(head?.children ?? [])].reduce(
      (sum, k) => sum + k.getBoundingClientRect().width,
      0,
    ),
    rightWidth: r.width,
    valueWidth: cond.querySelector('.smart-table-filter-value')?.getBoundingClientRect().width ?? 0,
    condCenterY: c.top + c.height / 2,
    rightCenterY: r.top + r.height / 2,
    condLeft: (bar.querySelector('.smart-table-cond__field') ?? cond).getBoundingClientRect().left,
    barLeft: bar.getBoundingClientRect().left,
    condRight: Math.max(c.right, ...controls.map(el => el.getBoundingClientRect().right)),
    rightLeft: r.left,
  }
}

async function settledReading(page: Page, label: string): Promise<Reading> {
  let previous = ''
  let reading: Reading | null = null
  await expect
    .poll(
      async () => {
        const next = await page.evaluate(readBar)
        const key = JSON.stringify(next)
        const stable = !!next?.settled && key === previous
        previous = key
        if (stable) reading = next
        return stable
      },
      { timeout: 10_000, intervals: [100, 150, 200], message: `${label} 的工具栏没有稳定下来` },
    )
    .toBe(true)
  return reading!
}

/** 视口宽度:笔记本到超宽屏。卡片宽取决于页面;中档「放得下」与「放不下」两种情形是否都扫到由下面的断言守住。 */
const VIEWPORT_WIDTHS = [700, 1100, 1200, 1300, 1440, 1600, 1700, 1920, 2560]

/** 用户管理(左侧机构栏占 256,卡片 = 视口 - 约 516)与角色管理(整页一张表,卡片 = 视口 - 约 260)。 */
const PAGES: { path: string; name: string; hidePanel?: boolean; expectStacked: boolean }[] = [
  { path: '/system/user', name: '用户管理', expectStacked: false },
  // 收起机构栏后头部多出「机构」按钮:头部、条件栏、操作区三块要么同一行,要么操作区在上、头部与条件栏在下
  { path: '/system/user', name: '用户管理(收起机构栏)', hidePanel: true, expectStacked: true },
  { path: '/system/role', name: '角色管理', expectStacked: true },
]

test.describe('条件搜索栏与工具栏对齐', () => {
  for (const { path, name, hidePanel, expectStacked } of PAGES) {
    test(`${name}:放得下就同一行对齐,放不下折成两行且不溢出`, async ({ page }) => {
      test.setTimeout(120_000)
      await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
      await enterApp(page, SYSTEM_APP)
      // 机构栏的收起状态记在 localStorage:在页面脚本之前写好,进页面就是收起的
      if (hidePanel)
        await page.addInitScript(() => localStorage.setItem('sa-user-org-panel-hidden', 'true'))
      await page.setViewportSize({ width: VIEWPORT_WIDTHS[0]!, height: 1080 })
      await page.goto(path)
      await expect(page.locator('.smart-table-toolbar').first()).toBeVisible({ timeout: 15_000 })

      const problems: string[] = []
      const lines: string[] = []
      let sawMidSameRow = false
      let sawMidStacked = false

      for (const width of VIEWPORT_WIDTHS) {
        await page.setViewportSize({ width, height: 1080 })
        const r = await settledReading(page, `${path} @ 视口 ${width}`)
        if (r.tier === 'narrow') {
          lines.push(`视口 ${width} → 卡片 ${r.cardWidth} narrow 档(另一套结构,不在本用例范围)`)
          continue
        }
        const at = `视口 ${width}(卡片 ${r.cardWidth},${r.tier} 档)`
        const gaps = r.headWidth > 0 ? GAP * 2 : GAP
        const fits = r.barWidth >= COND_MIN_WIDTH + r.rightWidth + r.headWidth + gaps
        const dy = r.condCenterY - r.rightCenterY
        lines.push(
          `${at}:工具栏 ${r.barWidth},操作区 ${r.rightWidth},` +
            `${fits ? '放得下' : '放不下'},头部 ${Math.round(r.headWidth)},值输入框 ${Math.round(r.valueWidth)},条件栏相对操作区垂直 ${Math.round(dy)}px,左缘缩进 ${Math.round(r.condLeft - r.barLeft)}px,溢出 ${r.barOverflow}px`,
        )

        if (r.barOverflow > 0) problems.push(`${at}:工具栏横向溢出 ${r.barOverflow}px`)
        // 头部没有内容(空元素)时不占位:条件栏的左缘与工具栏(也就是下面表格)的左缘贴齐
        if (r.headWidth === 0 && Math.abs(r.condLeft - r.barLeft) > 1)
          problems.push(
            `${at}:条件栏左缘比工具栏左缘缩进 ${Math.round(r.condLeft - r.barLeft)}px,与表格没有对齐`,
          )

        if (fits) {
          if (r.tier === 'mid') sawMidSameRow = true
          // 并排只在输入框还够用的时候才成立:刚好放得下就并排会把输入框挤到几乎没法输入。
          // 宽档同理:头部有按钮(收起机构栏)时,库的两列等分会把输入框挤到一百多像素
          if (r.valueWidth < MIN_INLINE_VALUE_WIDTH - 1)
            problems.push(
              `${at}:并排时值输入框只剩 ${Math.round(r.valueWidth)}px,低于 ${MIN_INLINE_VALUE_WIDTH}px,应当折行或让出空间`,
            )
          if (Math.abs(dy) > SAME_ROW_TOLERANCE)
            problems.push(`${at}:放得下却没有同一行对齐,条件栏与操作区垂直相差 ${Math.round(dy)}px`)
          if (r.condRight > r.rightLeft + 0.5)
            problems.push(
              `${at}:并排时条件栏压到了操作区上(${Math.round(r.condRight - r.rightLeft)}px)`,
            )
        } else {
          sawMidStacked = true
          // 放不下:操作区在上、条件栏在下,与原来的中档次序一致
          if (!(r.rightCenterY < r.condCenterY - 10))
            problems.push(
              `${at}:放不下折行时,操作区应在条件栏上方,实际垂直相差 ${Math.round(dy)}px`,
            )
        }
      }

      // 两种情形都扫到才算守住,否则上面的断言有一半在空转
      expect(sawMidSameRow, `${name} 的扫描没有出现「中档且放得下」:\n${lines.join('\n')}`).toBe(
        true,
      )
      if (expectStacked)
        expect(sawMidStacked, `${name} 的扫描没有出现「中档且放不下」:\n${lines.join('\n')}`).toBe(
          true,
        )
      expect(problems, `${name} 的扫描读数:\n${lines.join('\n')}`).toEqual([])
    })
  }
})
