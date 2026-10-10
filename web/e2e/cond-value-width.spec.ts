import { expect, test, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'

/**
 * 条件搜索栏的「值」输入框有宽度上限,不随窗口 / 分辨率无限变长。
 *
 * 条件构造器一行是 [字段 136][比较符 112][值 flex:1][»][查询][重置],值输入框吃掉一行里的剩余宽度,
 * 上限由 smart-naive-table 给:.smart-table-cond__main .smart-table-filter-value 的
 * max-width: var(--smart-table-cond-value-max-width, 320px)。SmartAdmin 不改这个变量,
 * 下面的 MAX_VALUE_WIDTH 与库的默认值同值,库调整默认值时同步改。
 *
 * 窄档(卡片 < 600)是另一套结构:输入框 + 「筛选」按钮,输入框铺满整行,不在限宽范围内,这里一并锁住。
 *
 * 用真实视口宽度扫一遍,卡片宽由页面自己的布局决定,档位以渲染出来的工具栏类名为准。
 * 档位阈值(600 / 1280)是库的约定,这里只用它判断读数是否已经跟上当前宽度:ResizeObserver 与 Vue 更新
 * 晚于视口变化,不等读数追上就量,会把上一档的布局当成当前的。
 */

/** 与库给值输入框的默认上限(--smart-table-cond-value-max-width 的缺省值)同值。 */
const MAX_VALUE_WIDTH = 320
/** 库给值输入框的下限(.smart-table-cond__main .smart-table-filter-value 的 min-width)。 */
const MIN_VALUE_WIDTH = 100
/** 窄档里输入框与「筛选」按钮之间的间距。 */
const NARROW_GAP = 8

type Tier = 'wide' | 'mid' | 'narrow'

interface Reading {
  tier: Tier | null
  /** 档位是否已经与卡片宽度对应上(库:< 600 窄,< 1280 中,其余宽)。 */
  settled: boolean
  cardWidth: number
  cardLeft: number
  cardRight: number
  /** 工具栏横向溢出的像素数(scrollWidth - clientWidth)。 */
  barOverflow: number
  /** 值输入框外壳(.smart-table-filter-value)的宽度。 */
  valueWidth: number
  /** 一行里最靠右的控件(» / 查询 / 重置;窄档是「筛选」)的右缘。 */
  lastControlRight: number
  /** 窄档:条件行的右缘、「筛选」按钮的左缘与宽度、输入框的右缘。 */
  narrow: { condRight: number; moreLeft: number; moreRight: number; valueRight: number } | null
}

/** 在页面里量第一张表格的条件栏。没渲染出来返回 null,交给轮询重试。 */
function readCond(): Reading | null {
  const table = document.querySelector<HTMLElement>('.smart-table')
  const bar = table?.querySelector<HTMLElement>('.smart-table-toolbar')
  if (!table || !bar) return null
  const tier = (bar.className.match(/smart-table-toolbar--(wide|mid|narrow)\b/)?.[1] ??
    null) as Tier | null
  const width = table.clientWidth
  const expected = width === 0 ? 'wide' : width < 600 ? 'narrow' : width < 1280 ? 'mid' : 'wide'
  const card = table.getBoundingClientRect()

  const narrowRoot = bar.querySelector('.smart-table-cond--narrow')
  const mainRoot = bar.querySelector('.smart-table-cond__main')
  const value = (mainRoot ?? narrowRoot)
    ?.querySelector('.smart-table-filter-value')
    ?.getBoundingClientRect()
  if (!value) return null

  // 一行里的控件:字段 / 比较符 / 值之后的 » 、查询、重置(窄档只有「筛选」)
  const controls = narrowRoot
    ? [...narrowRoot.querySelectorAll('.smart-table-cond__more')]
    : [...(mainRoot?.querySelectorAll(':scope > .n-button, .smart-table-cond__more') ?? [])]
  if (!controls.length) return null
  const lastControlRight = Math.max(...controls.map(el => el.getBoundingClientRect().right))

  const more = narrowRoot?.querySelector('.smart-table-cond__more')?.getBoundingClientRect()
  const cond = narrowRoot?.getBoundingClientRect()
  return {
    tier,
    settled: tier === expected,
    cardWidth: Math.round(card.width * 10) / 10,
    cardLeft: card.left,
    cardRight: card.right,
    barOverflow: bar.scrollWidth - bar.clientWidth,
    valueWidth: Math.round(value.width * 10) / 10,
    lastControlRight,
    narrow:
      narrowRoot && more && cond
        ? {
            condRight: cond.right,
            moreLeft: more.left,
            moreRight: more.right,
            valueRight: value.right,
          }
        : null,
  }
}

/** 改视口宽度,等条件栏的档位和值输入框宽度追上卡片宽(连续两次读数一致才算稳)。 */
async function settledReading(page: Page, label: string): Promise<Reading> {
  let previous = ''
  let reading: Reading | null = null
  await expect
    .poll(
      async () => {
        const next = await page.evaluate(readCond)
        const key = JSON.stringify(next)
        const stable = !!next?.settled && key === previous
        previous = key
        if (stable) reading = next
        return stable
      },
      { timeout: 10_000, intervals: [100, 150, 200], message: `${label} 的条件栏没有稳定下来` },
    )
    .toBe(true)
  return reading!
}

/**
 * 两张形状不同的页:用户管理(左侧机构筛选栏占 256,表格卡片 = 视口 - 约 516,窄档 / 中档 / 宽档都扫得到)
 * 和角色管理(整页就是一张表格,卡片 = 视口 - 约 260)。
 */
const PAGES: { path: string; name: string; expectTiers: Tier[] }[] = [
  { path: '/system/user', name: '用户管理', expectTiers: ['narrow', 'mid', 'wide'] },
  { path: '/system/role', name: '角色管理', expectTiers: ['mid', 'wide'] },
]

/** 视口宽度:笔记本到超宽屏。卡片宽取决于页面,各档位是否扫到由 expectTiers 守住。 */
const VIEWPORT_WIDTHS = [390, 580, 700, 1100, 1300, 1440, 1700, 1920, 2560, 3440]

test.describe('条件搜索栏值输入框限宽', () => {
  for (const { path, name, expectTiers } of PAGES) {
    test(`${name}:值输入框 ≤ ${MAX_VALUE_WIDTH}px、工具栏不溢出;窄档输入框仍铺满`, async ({
      page,
    }) => {
      test.setTimeout(120_000)
      await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
      await enterApp(page, SYSTEM_APP)
      await page.setViewportSize({ width: VIEWPORT_WIDTHS[0]!, height: 1080 })
      await page.goto(path)
      await expect(page.locator('.smart-table-toolbar').first()).toBeVisible({ timeout: 15_000 })

      const problems: string[] = []
      const seen = new Set<Tier>()
      const lines: string[] = []

      for (const width of VIEWPORT_WIDTHS) {
        await page.setViewportSize({ width, height: 1080 })
        const r = await settledReading(page, `${path} @ 视口 ${width}`)
        seen.add(r.tier!)
        lines.push(
          `视口 ${width} → 卡片 ${r.cardWidth} ${r.tier} 档:值输入框 ${r.valueWidth}px,横向溢出 ${r.barOverflow}px`,
        )
        const at = `视口 ${width}(卡片 ${r.cardWidth},${r.tier} 档)`

        if (r.barOverflow > 0) problems.push(`${at}:工具栏横向溢出 ${r.barOverflow}px`)
        if (r.lastControlRight > r.cardRight)
          problems.push(
            `${at}:最右的控件越出卡片右缘 ${Math.round(r.lastControlRight - r.cardRight)}px`,
          )

        if (r.tier === 'narrow') {
          // 窄档:输入框 + 「筛选」铺满整行,不受限宽影响
          const n = r.narrow!
          if (Math.abs(n.condRight - n.moreRight) > 1)
            problems.push(`${at}:「筛选」按钮没有贴着条件行右缘`)
          if (Math.abs(n.moreLeft - n.valueRight - NARROW_GAP) > 1)
            problems.push(
              `${at}:窄档输入框没有铺满到「筛选」按钮(空了 ${Math.round(n.moreLeft - n.valueRight - NARROW_GAP)}px,输入框 ${r.valueWidth}px)`,
            )
        } else {
          if (r.valueWidth > MAX_VALUE_WIDTH)
            problems.push(`${at}:值输入框 ${r.valueWidth}px,超过上限 ${MAX_VALUE_WIDTH}px`)
          if (r.valueWidth < MIN_VALUE_WIDTH)
            problems.push(`${at}:值输入框 ${r.valueWidth}px,低于库的下限 ${MIN_VALUE_WIDTH}px`)
        }
      }

      // 没有把三个档位都扫到,上面的断言就是空转
      for (const tier of expectTiers) {
        expect(
          seen.has(tier),
          `${path} 的视口扫描里没有出现 ${tier} 档:\n${lines.join('\n')}`,
        ).toBe(true)
      }
      expect(problems, `${path} 的扫描读数:\n${lines.join('\n')}`).toEqual([])
    })
  }
})
