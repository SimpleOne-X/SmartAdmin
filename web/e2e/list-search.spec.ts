import { expect, test, type Page } from '@playwright/test'
import { ADMIN_ACCOUNT, ADMIN_PASSWORD, SYSTEM_APP, enterApp, login } from './helpers'

/**
 * 列表页搜索回归:锁住「搜索条件 → 后端扁平查询参数」这条契约。
 *
 * smart-naive-table 3.0 的条件构造器默认产出 `filters`,而内核各分页端点收的是 Account / Title 这类扁平参数
 * (见 packages/admin/src/api/index.ts)。这条契约不随表格库版本变化,所以这里只断言发给后端的查询串,
 * 不断言搜索区长什么样:搜索入口集中在 searchBy 一个函数里,界面形态变了只改它。
 */

const USER_PAGE = /\/api\/v1\/sys\/user\/page\?/
const OP_LOG_PAGE = /\/api\/v1\/sys\/log\/op\/page\?/

async function openList(page: Page, path: string) {
  await login(page, ADMIN_ACCOUNT, ADMIN_PASSWORD)
  // /system/* 只挂在「系统」应用下,不能指望登录后碰巧落在那儿
  await enterApp(page, SYSTEM_APP)
  await page.goto(path)
  await expect(page.locator('.n-data-table')).toBeVisible({ timeout: 10_000 })
}

/**
 * 搜索入口:条件构造器(并入表格卡片的一行「字段 + 比较符 + 值」)。
 * 默认字段是第一个声明了 search 的列、默认比较符是 actions 的第一个(用户页是「账号 包含」,操作日志是「操作名 包含」),
 * 所以只要往值输入框填内容再回车。`_label` 参数不参与定位,只用来在调用处标明要搜的字段。
 */
async function searchBy(page: Page, _label: RegExp, value: string) {
  const input = page.locator('.smart-table-cond input[placeholder="请输入"]')
  await input.fill(value)
  await input.press('Enter')
}

/** 触发搜索后,等到「带有 must」的那次分页请求,返回它的 URL。 */
async function queryAfter(page: Page, pattern: RegExp, must: string, trigger: () => Promise<void>) {
  const pending = page.waitForRequest(r => pattern.test(r.url()) && r.url().includes(must))
  await trigger()
  return new URL((await pending).url())
}

test.describe('列表页搜索 → 扁平参数', () => {
  test('用户列表:按账号搜索带 Account,每页 100,不带 filters', async ({ page }) => {
    await openList(page, '/system/user')
    const url = await queryAfter(page, USER_PAGE, 'Account=', () =>
      searchBy(page, /账号|Account/i, ADMIN_ACCOUNT),
    )
    expect(url.searchParams.get('Account')).toBe(ADMIN_ACCOUNT)
    expect(url.searchParams.get('Size')).toBe('100')
    expect(url.searchParams.has('filters')).toBe(false)
  })

  test('操作日志:按操作名搜索带 Title,不带 filters', async ({ page }) => {
    await openList(page, '/system/log/op')
    const url = await queryAfter(page, OP_LOG_PAGE, 'Title=', () =>
      searchBy(page, /操作名|Operation/i, '登录'),
    )
    expect(url.searchParams.get('Title')).toBe('登录')
    expect(url.searchParams.has('filters')).toBe(false)
  })
})
