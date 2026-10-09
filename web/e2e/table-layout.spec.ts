import { expect, test, type Locator, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'

/**
 * 表格统一标准的布局回归:所有列表页是同一个样子 —— 卡片顶部单行三段式,
 * 左半 = 条件构造器(无标题),右半 = 业务按钮 + 内置图标。
 *
 * 守卫 `views/listSearch.spec.ts` 扫源码保证「写了什么」,这里跑真实页面保证「渲染出来是什么」:
 * 业务按钮写进 `#toolbar`(左半)时源码守卫能抓到,但内置图标有没有真的全开、搜索是不是真的靠左,只有渲染后才知道。
 *
 * 用 1920 宽:库的单行三段式要求**表格容器** ≥ 约 1280px,1920 视口扣掉侧栏与边距后约 1650px 才满足;
 * 1440 视口扣完只剩约 1150px,库会按「中档」换成两行(第一行标题 + 右侧工具栏,第二行条件构造器),那是库的设计,不在这里断言。
 */

test.use({ viewport: { width: 1920, height: 1080 } })

/**
 * 所有整页列表。`icons` 是内置图标个数:刷新 / 放大还原 / 列设置共 3 个(密度按钮不放在表格上,见 utils/tableToolbar.ts);
 * 静态数据的表格(模块管理)3.0 会自己把刷新藏起来,只有 2 个;
 * 字典管理的第一张表是左栏窄列表,放大没有意义(登记在 listSearch.spec 的 MAXIMIZE_OFF_OK),也是 2 个。
 * `business` 是该页右半区应有的业务按钮(没有就不断言)。
 */
const PAGES: {
  path: string
  name: string
  icons?: number
  business?: RegExp
}[] = [
  { path: '/system/role', name: '角色管理', business: /新增|Add/ },
  { path: '/system/user', name: '用户管理', business: /新增|Add/ },
  { path: '/system/position', name: '岗位管理', business: /新增|Add/ },
  { path: '/system/org', name: '机构管理', business: /新增|Add/ },
  { path: '/system/menu', name: '菜单管理', business: /新增|Add/ },
  { path: '/system/module', name: '模块管理', icons: 2, business: /新增|Add/ },
  { path: '/system/dict', name: '字典管理', icons: 2, business: /新增|Add/ },
  { path: '/system/notice', name: '消息通知(管理)' },
  { path: '/system/file', name: '文件管理' },
  { path: '/system/recycle', name: '回收站' },
  { path: '/system/session', name: '在线会话' },
  { path: '/system/job', name: '定时任务', business: /新增|Add/ },
  { path: '/system/job-log', name: '执行记录' },
  { path: '/system/log/op', name: '操作日志' },
  { path: '/system/log/login', name: '登录日志' },
  { path: '/system/log/exception', name: '异常日志' },
  { path: '/personal/notice', name: '我的通知' },
]

/** 取元素的包围盒;没渲染出来就直接让用例失败(带着说明)。 */
async function boxOf(loc: Locator) {
  const box = await loc.boundingBox()
  expect(box, '元素没渲染出来').not.toBeNull()
  return box!
}

async function openList(page: Page, path: string) {
  await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
  // /system/* 只挂在「系统」应用下,不能指望登录后碰巧落在那儿
  await enterApp(page, SYSTEM_APP)
  await page.goto(path)
  await expect(page.locator('.smart-table-toolbar').first()).toBeVisible({ timeout: 10_000 })
}

test.describe('表格统一布局', () => {
  for (const { path, name, icons: iconCount = 3, business } of PAGES) {
    test(`${name}:搜索靠左,业务按钮 + 内置图标靠右`, async ({ page }) => {
      await openList(page, path)

      const bar = page.locator('.smart-table-toolbar').first()
      const right = bar.locator('.smart-table-toolbar-right')

      // 左半:没有标题(设计定稿:页签和面包屑已标明所在页,工具栏不放标题,见 COMPONENTS.md「不放标题」),有条件构造器
      await expect(page.locator('.smart-table-title')).toHaveCount(0)
      await expect(bar.locator('.smart-table-cond').first()).toBeVisible()

      // 右半:内置图标(刷新、放大还原、列设置;静态表没有刷新),没有密度按钮
      const icons = right.locator('.smart-table-toolbar-icons')
      await expect(icons.locator('button')).toHaveCount(iconCount)
      await expect(icons.getByRole('button', { name: /密度|Density/ })).toHaveCount(0)

      // 左右关系用真正的控件来比(条件构造器的包装元素在中档宽度下会横跨整行,不能拿它的右缘比):
      // 字段下拉 → 「查询」按钮 → 业务按钮 → 内置图标,x 坐标必须依次递增。
      const field = await boxOf(bar.locator('.smart-table-cond .n-base-selection').first())
      const query = await boxOf(
        bar.locator('.smart-table-cond').getByRole('button', { name: /^(查询|Search)$/ }),
      )
      const iconBox = await boxOf(icons)
      expect(field.x + field.width).toBeLessThanOrEqual(query.x)
      expect(query.x + query.width).toBeLessThanOrEqual(iconBox.x)

      // 业务按钮在右半(位于「查询」之后、内置图标之前),不在搜索那一半
      if (business) {
        const biz = await boxOf(right.getByRole('button', { name: business }))
        expect(biz.x).toBeGreaterThanOrEqual(query.x + query.width)
        expect(biz.x + biz.width).toBeLessThanOrEqual(iconBox.x)
      }
    })
  }

  test('条件搜索:加载中与加载后搜索框同宽(查询按钮的转圈不占位)', async ({ page }) => {
    await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
    await enterApp(page, SYSTEM_APP)

    // 拦住列表接口,让页面稳定停在「加载中」,再放行
    let release!: () => void
    const gate = new Promise<void>(resolve => (release = resolve))
    await page.route('**/api/v1/sys/log/exception/page*', async route => {
      await gate
      await route.continue()
    })
    await page.goto('/system/log/exception')

    const input = page.locator('.smart-table-filter-value').first()
    const spinner = page.locator('.smart-table-cond .n-button__icon > .n-base-loading')
    await expect(input).toBeVisible({ timeout: 10_000 })
    await expect(spinner.first()).toBeAttached() // 确实处在加载态
    const loading = await boxOf(input)

    release()
    await expect(spinner).toHaveCount(0) // 加载结束,转圈槽已卸掉(含离场动画)
    const loaded = await boxOf(input)

    // 转圈槽一旦占位,按钮会先撑宽 24px、结束再收回,输入框跟着变窄再变宽
    expect(Math.abs(loaded.width - loading.width)).toBeLessThan(1)
    expect(Math.abs(loaded.x - loading.x)).toBeLessThan(1)
  })

  test('机构树表:名称列拖到最窄,省略号不越出单元格', async ({ page }) => {
    await openList(page, '/system/org')
    const rows = page.locator('.n-data-table-tbody .n-data-table-tr')
    await expect(rows.nth(3)).toBeVisible({ timeout: 10_000 }) // 种子机构至少 4 行,含 3 层

    // 全站默认开着列宽拖拽,用户把名称列往窄了拖是正常操作;列被钳在 minWidth,正是内容放不下的最坏状态
    const th = page.locator('thead th[data-col-key="name"]')
    const thBox = await boxOf(th)
    const handle = await boxOf(th.locator('.n-data-table-resize-button'))
    const y = handle.y + handle.height / 2
    await page.mouse.move(handle.x + handle.width / 2, y)
    await page.mouse.down()
    await page.mouse.move(thBox.x, y, { steps: 10 })
    await page.mouse.up()
    await expect.poll(async () => (await boxOf(th)).width).toBeLessThan(thBox.width - 100)

    // 树单元格 = 缩进 + 展开箭头 + 省略号,省略号的 max-width 只扣了 100%、没扣前面两样,
    // 内容放不下时「…」会画到列边界外面,盖到隔壁列上
    const cells = page.locator('.n-data-table-tbody td[data-col-key="name"]')
    const count = await cells.count()
    for (let i = 0; i < count; i++) {
      const td = await boxOf(cells.nth(i))
      const ell = await boxOf(cells.nth(i).locator('.n-ellipsis'))
      expect(ell.x + ell.width, `第 ${i + 1} 行的省略号越出了名称列`).toBeLessThanOrEqual(
        td.x + td.width,
      )
    }
  })

  test('字典管理:字典类型在上,字典项在下,两块同宽、竖直排列', async ({ page }) => {
    await openList(page, '/system/dict')

    // 选中一行字典类型,下面的字典项表才出现
    await page.locator('.smart-table .n-data-table-tbody .n-data-table-tr').first().click()
    // 字典类型表与字典项表都是 SmartTable(项表是静态数据模式),各自就是一张卡片、并列在 .dict-layout 下
    const tables = page.locator('.dict-layout > .smart-table')
    await expect(tables).toHaveCount(2)
    const typeTable = tables.nth(0)
    const itemCard = tables.nth(1)
    // SmartTable 自己就是卡片:它外面不能再套一层卡片(卡中卡)
    await expect(page.locator('.n-card .smart-table')).toHaveCount(0)

    const top = await boxOf(typeTable)
    const bottom = await boxOf(itemCard)
    expect(top.y + top.height).toBeLessThanOrEqual(bottom.y) // 上下,不是左右
    expect(Math.abs(top.x - bottom.x)).toBeLessThan(2) // 左缘对齐
    expect(Math.abs(top.width - bottom.width)).toBeLessThan(2) // 同宽
    // 各占一半,且分界线不随内容变化:flex-basis 退回 auto 时上栏按内容分,下栏一出现、数据一加载就整体下移
    expect(Math.abs(top.height - bottom.height)).toBeLessThan(8)
    await page.waitForTimeout(600)
    const settled = await boxOf(typeTable)
    expect(Math.abs(settled.height - top.height)).toBeLessThan(2)
  })
})
