import { test, expect, type APIRequestContext, type Browser, type Page } from '@playwright/test'
import { login, enterApp, SYSTEM_APP } from './helpers'
import { apiAdminToken, apiClearExternalProvider, apiConfigureWeCom } from './api'

/**
 * 配置中心:顶部图标页签 + 每个分类「左边设置、右边预览」+ 底部浮出的保存条统一保存。
 * 结构化表单认领的键在各自分类里改;其余配置行(含结构化分组里的零散键)都在「高级」里;
 * 「敏感操作」的增删即时生效,不走保存条。
 */

async function gotoConfig(page: Page, tab?: string) {
  await login(page)
  // /system/* 只挂在「系统」应用下,不能指望登录后碰巧落在那儿(默认应用是全局可变状态)
  await enterApp(page, SYSTEM_APP)
  await page.goto(tab ? `/system/config?tab=${tab}` : '/system/config')
  await page.waitForLoadState('networkidle')
  await expect(page.getByTestId('config-nav')).toBeVisible()
}

async function openTab(page: Page, tab: string) {
  await page.getByTestId('config-nav').locator(`[data-tab="${tab}"]`).click()
  await expect(page).toHaveURL(new RegExp(`tab=${tab}`))
}

/** 当前分类的预览栏(各分类以 v-show 常驻,只取可见的那一个)。 */
function preview(page: Page) {
  return page.getByTestId('config-preview').filter({ visible: true })
}

function saveBar(page: Page) {
  return page.getByTestId('config-savebar')
}

async function saveAll(page: Page) {
  await saveBar(page)
    .getByRole('button', { name: /保存|Save/ })
    .click()
  await expect(page.locator('.n-message').first()).toContainText(/保存成功|Saved/, {
    timeout: 10_000,
  })
  await expect(saveBar(page)).toBeHidden()
}

/** FormContainer 可能渲染成 modal 或 drawer,取当前可见的那个。 */
function dialog(page: Page) {
  return page.locator('.n-modal, .n-drawer').filter({ visible: true })
}

function fieldIn(scope: ReturnType<typeof dialog>, label: RegExp) {
  return scope.locator('.n-form-item').filter({ hasText: label }).locator('input, textarea').first()
}

/** 设置行里的输入框:标签写在 aria-label 上(分组列表的行没有 <label>)。 */
function formField(page: Page, label: RegExp) {
  return page
    .getByLabel(label)
    .and(page.locator('input, textarea'))
    .filter({ visible: true })
    .first()
}

/** 当前分类里某个设置行上的开关。 */
function rowSwitch(page: Page, row: string) {
  return page.locator(`${row} .n-switch`).filter({ visible: true }).first()
}

async function assertSplitLogin(
  page: Page,
  browser: Browser,
  expected: { head: string; highlight: string; features: string[] },
) {
  const context = await browser.newContext()
  try {
    const loginPage = await context.newPage()
    await loginPage.goto(new URL('/login?skin=split', page.url()).toString())
    await expect(loginPage.locator('.headline')).toContainText(expected.head)
    await expect(
      loginPage.locator('.headline').getByText(expected.highlight, { exact: true }),
    ).toBeVisible()
    await expect(loginPage.locator('.points li')).toHaveCount(expected.features.length)
    for (const feature of expected.features)
      await expect(loginPage.locator('.points')).toContainText(feature)
  } finally {
    await context.close()
  }
}

const apiBase = () => process.env.SMART_E2E_API_BASE ?? 'http://127.0.0.1:5101'

/** 以超管身份调后端,断言信封 code=0 并返回 data。 */
async function api<T = unknown>(
  request: APIRequestContext,
  method: 'get' | 'post' | 'put' | 'delete',
  path: string,
  data?: unknown,
): Promise<T> {
  const token = await apiAdminToken(request)
  const res = await request[method](`${apiBase()}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
    ...(data === undefined ? {} : { data }),
  })
  const env = (await res.json()) as { code: number; msg?: string; data?: T }
  expect(env.code, `${method.toUpperCase()} ${path} → code=${env.code} ${env.msg ?? ''}`).toBe(0)
  return env.data as T
}

const setConfig = (request: APIRequestContext, configKey: string, configValue: string) =>
  api(request, 'put', '/api/v1/sys/config/batch', [{ configKey, configValue }])

/** 1×1 的真 PNG:浏览器要能解码它才能裁剪。 */
const PNG_1PX = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
  'base64',
)

test.describe('配置中心', () => {
  test.describe.configure({ mode: 'serial' })

  test('站点品牌:改标题 → 预览即时变 → 导航黄点 → 保存后全站生效', async ({ page, request }) => {
    const TITLE = `E2E 后台 ${Date.now().toString(36)}`
    await gotoConfig(page)
    try {
      await formField(page, /站点标题|Site title/).fill(TITLE)
      await expect(preview(page).getByTestId('preview-brand')).toContainText(TITLE)
      await expect(page.locator('[data-tab="brand"] .dot')).toBeVisible()
      await expect(saveBar(page)).toContainText(/1/)

      await saveAll(page)
      await expect(page).toHaveTitle(new RegExp(TITLE))
      expect(
        await api<string | null>(request, 'get', '/api/v1/sys/config/value/sys.site.title'),
      ).toBe(TITLE)
    } finally {
      await setConfig(request, 'sys.site.title', 'SmartAdmin')
    }
  })

  test('页签导航可用键盘操作:方向键切换,选中项带 aria-selected', async ({ page }) => {
    await gotoConfig(page)
    const brand = page.getByRole('tab', { name: /品牌与登录页|Branding/ })
    await expect(brand).toHaveAttribute('aria-selected', 'true')
    await brand.focus()
    await page.keyboard.press('ArrowRight')
    await expect(page).toHaveURL(/tab=security/)
    await expect(page.getByRole('tab', { name: /安全策略|Security/ })).toBeFocused()
    await page.keyboard.press('End')
    await expect(page).toHaveURL(/tab=advanced/)
  })

  test('站点品牌:放弃修改还原草稿', async ({ page }) => {
    await gotoConfig(page)
    const field = formField(page, /站点标题|Site title/)
    const before = await field.inputValue()
    await field.fill('临时改动')
    await saveBar(page)
      .getByRole('button', { name: /放弃修改|Discard/ })
      .click()
    await expect(field).toHaveValue(before)
    await expect(saveBar(page)).toBeHidden()
  })

  test('Logo:上传 → 裁剪 → 预览 → 保存后侧栏与标签页图标换成新 Logo', async ({ page, request }) => {
    test.setTimeout(60_000)
    await gotoConfig(page)
    try {
      await page.getByTestId('logo-file').setInputFiles({
        name: 'brand.png',
        mimeType: 'image/png',
        buffer: PNG_1PX,
      })
      const crop = dialog(page)
      await expect(crop).toBeVisible()
      await crop.getByRole('button', { name: /设为新 Logo|Set new logo/ }).click()
      await expect(crop).toBeHidden()
      // 裁好的图只进草稿:预览里是 blob: 地址,还没上传
      await expect(preview(page).locator('[data-testid="preview-brand"] img')).toHaveAttribute(
        'src',
        /^blob:/,
      )

      await saveAll(page)
      const site = await api<{ logo: string }>(request, 'get', '/api/v1/sys/config/site')
      expect(site.logo).toMatch(/^\/api\/v1\/sys\/file\/\d+\/view\?sig=/)
      await expect(page.locator('.brand img.site-logo').first()).toHaveAttribute(
        'src',
        /\/view\?sig=/,
      )
      await expect(page.locator('link[rel~="icon"]').first()).toHaveAttribute(
        'href',
        /\/view\?sig=/,
      )
    } finally {
      await setConfig(request, 'sys.site.logo', '')
    }
  })

  test('有未保存改动时离开页面要确认', async ({ page }) => {
    await gotoConfig(page)
    await formField(page, /站点标题|Site title/).fill('离开前的改动')
    await page.evaluate(() => {
      // 走路由跳转(不是整页刷新),触发 onBeforeRouteLeave
      const app = document.querySelector('#app') as unknown as {
        __vue_app__: { config: { globalProperties: { $router: { push: (p: string) => void } } } }
      }
      app.__vue_app__.config.globalProperties.$router.push('/workbench')
    })
    const confirm = page.locator('.n-dialog')
    await expect(confirm).toContainText(/未保存|unsaved/i)
    await confirm.getByRole('button', { name: /取消|Cancel/ }).click()
    await expect(page).toHaveURL(/\/system\/config/)
    await saveBar(page)
      .getByRole('button', { name: /放弃修改|Discard/ })
      .click()
  })

  test('安全策略:?tab 直达,改值后规则预览跟着变,保存后写库', async ({ page, request }) => {
    const KEY = 'sys.security.password.historyCount'
    await gotoConfig(page, 'security')
    const field = formField(page, /不能与最近几次重复|No reuse of last/)
    await expect(field).toHaveValue('0')
    await expect(preview(page)).toContainText(/新密码可以和旧密码相同|may repeat/)

    try {
      await field.fill('3')
      await field.blur()
      await expect(preview(page)).toContainText(/最近 3 次|last 3/)
      await expect(page.locator('[data-tab="security"] .dot')).toBeVisible()
      await saveAll(page)
      expect(await api<string | null>(request, 'get', `/api/v1/sys/config/value/${KEY}`)).toBe('3')
    } finally {
      await setConfig(request, KEY, '0')
    }
  })

  test('登录页:Hero 预览即时显示,保存后分栏皮肤实际显示', async ({ page, request, browser }) => {
    test.setTimeout(60_000)
    const HEADLINE = `E2E 企业权限控制台 ${Date.now().toString(36)}`
    const HIGHLIGHT = '权限'
    const FEATURES = ['角色授权', '数据范围', '多应用门户']
    const KEYS = [
      'sys.login.hero.headline.zh-CN',
      'sys.login.hero.highlight.zh-CN',
      'sys.login.hero.features.zh-CN',
    ]

    await gotoConfig(page, 'brand')
    try {
      await page
        .getByTestId('login-locale')
        .getByRole('radio', { name: /^中文$|^Chinese$/ })
        .click()
      await formField(page, /主标题|Headline/).fill(HEADLINE)
      await formField(page, /强调词|Highlight/).fill(HIGHLIGHT)
      await formField(page, /卖点|Features/).fill(FEATURES.join('\n'))
      await expect(preview(page).locator('.headline')).toContainText(HEADLINE)
      await expect(preview(page).locator('.points li')).toHaveCount(FEATURES.length)

      await saveAll(page)
      await assertSplitLogin(page, browser, {
        head: HEADLINE,
        highlight: HIGHLIGHT,
        features: FEATURES,
      })
    } finally {
      await api(
        request,
        'put',
        '/api/v1/sys/config/batch',
        KEYS.map(configKey => ({ configKey, configValue: '' })),
      )
    }
  })

  test('登录方式:企业微信「按账号自动关联」打开要确认,保存后生效', async ({ page, request }) => {
    const KEY = 'sys.externalauth.wecom.linkByAccount'
    const ENABLED = 'sys.externalauth.wecom.enabled'
    const wasEnabled =
      (await api<string | null>(request, 'get', `/api/v1/sys/config/value/${ENABLED}`)) !== 'false'
    // 先在库里配一套假的企业微信应用;关联开关只在企业微信已配置且打开时可操作
    await apiConfigureWeCom(request, await apiAdminToken(request))
    await gotoConfig(page, 'signin')

    const enabled = rowSwitch(page, '[data-provider="wecom"]')
    if (!(await enabled.getAttribute('class'))?.includes('n-switch--active')) await enabled.click()
    const toggle = rowSwitch(page, '[data-link="wecom"]')
    await expect(toggle).not.toHaveClass(/n-switch--active/)

    try {
      // 取消确认:开关不动
      await toggle.click()
      const confirm = page.locator('.n-dialog')
      await expect(confirm).toContainText(/同一批人|same people/)
      await confirm.getByRole('button', { name: /取消|Cancel/ }).click()
      await expect(toggle).not.toHaveClass(/n-switch--active/)

      // 确认后打开,点保存才写库
      await toggle.click()
      await page
        .locator('.n-dialog')
        .getByRole('button', { name: /确认|确定|Confirm/ })
        .click()
      await expect(toggle).toHaveClass(/n-switch--active/)
      await saveAll(page)
      expect(await api<string | null>(request, 'get', `/api/v1/sys/config/value/${KEY}`)).toBe(
        'true',
      )
    } finally {
      await api(request, 'put', '/api/v1/sys/config/batch', [
        { configKey: KEY, configValue: 'false' },
        { configKey: ENABLED, configValue: String(wasEnabled) },
      ])
      await apiClearExternalProvider(request, await apiAdminToken(request), 'wecom')
    }
  })

  test('登录方式:在设置面板配置 GitHub,机密不回显,清除后回到未配置', async ({
    page,
    request,
  }) => {
    const SECRET = 'gh-e2e-secret-abcd1234'
    const row = page.locator('[data-provider="github"]')
    await gotoConfig(page, 'signin')
    await expect(row).toHaveAttribute('data-state', 'unconfigured')
    await expect(rowSwitch(page, '[data-provider="github"]')).toHaveClass(/n-switch--disabled/)

    try {
      await page.getByTestId('provider-set-github').click()
      const sheet = page.getByTestId('provider-sheet')
      await expect(sheet).toBeVisible()
      const save = sheet.getByTestId('provider-save')
      await expect(save).toBeDisabled()
      await expect(sheet.getByTestId('callback-uri')).toContainText(
        '/api/v1/auth/external/github/callback',
      )

      await sheet.locator('#pf-clientId').fill('e2e-client-id')
      await expect(save).toBeDisabled() // 机密还没填
      await sheet.locator('#pf-clientSecret').fill(SECRET)
      await expect(save).toBeEnabled()
      await save.click()
      await expect(sheet).toBeHidden()

      // 保存直接写库,不经过底部保存条;开关随之可用
      await expect(row).toHaveAttribute('data-state', 'configured')
      await expect(rowSwitch(page, '[data-provider="github"]')).not.toHaveClass(
        /n-switch--disabled/,
      )
      await expect(saveBar(page)).toBeHidden()

      // 接口与页面都拿不到明文,只有「已配置 + 尾四位」
      const catalog = await api(request, 'get', '/api/v1/sys/external-auth/providers')
      expect(JSON.stringify(catalog)).not.toContain(SECRET)
      expect(JSON.stringify(catalog)).toContain('1234')

      await page.reload()
      await page.waitForLoadState('networkidle')
      await page.getByTestId('provider-set-github').click()
      const again = page.getByTestId('provider-sheet')
      await expect(again.locator('#pf-clientId')).toHaveValue('e2e-client-id')
      await expect(again.locator('#pf-clientSecret')).toHaveValue('')
      await expect(again.locator('#pf-clientSecret')).toHaveAttribute('placeholder', /1234/)
      await expect(page.locator('body')).not.toContainText(SECRET)

      // 清除要二次确认;清除后回到未配置,开关又灰掉
      await again.getByRole('button', { name: /清除配置|Clear settings/ }).click()
      await page
        .locator('.n-dialog')
        .getByRole('button', { name: /确认|确定|Confirm/ })
        .click()
      await expect(again).toBeHidden()
      await expect(row).toHaveAttribute('data-state', 'unconfigured')
    } finally {
      await apiClearExternalProvider(request, await apiAdminToken(request), 'github')
    }
  })

  test('登录方式:测试连接把每一项检查的结果显示在面板里', async ({ page }) => {
    await gotoConfig(page, 'signin')
    await page.getByTestId('provider-set-github').click()
    const sheet = page.getByTestId('provider-sheet')
    await sheet.locator('#pf-clientId').fill('e2e-client-id')
    await sheet.locator('#pf-clientSecret').fill('gh-e2e-secret-abcd1234')

    // 只验面板怎么呈现:后端的检查逻辑有各自的单元测试,这里不真的去连 GitHub
    let sent: { code: string | null; values: Record<string, string>; secrets: Record<string, string> } | null =
      null
    await page.route('**/api/v1/sys/external-auth/providers/test', async route => {
      sent = route.request().postDataJSON()
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          code: 0,
          msgKey: 'ok',
          data: {
            ok: false,
            checks: [
              { key: 'reachable', status: 'ok', detail: null },
              { key: 'credentials', status: 'failed', detail: 'incorrect_client_credentials' },
            ],
          },
        }),
      })
    })
    await sheet.getByTestId('provider-test').click()

    const results = sheet.getByTestId('test-results')
    await expect(results.locator('li.ok')).toContainText(/连通性|Connectivity/)
    await expect(results.locator('li.failed')).toContainText(/凭据|Credentials/)
    await expect(results.locator('li.failed')).toContainText('incorrect_client_credentials')
    // 首次配置还没入库:不带 code,用表单里的值测
    expect(sent).toMatchObject({
      code: null,
      values: { clientId: 'e2e-client-id' },
      secrets: { clientSecret: 'gh-e2e-secret-abcd1234' },
    })

    // 改了输入,上一次的结果作废
    await sheet.locator('#pf-clientId').fill('e2e-client-id-2')
    await expect(results).toHaveCount(0)
  })

  test('登录方式:短信验证码登录从安全策略挪到这里,预览登录框出现短信入口', async ({
    page,
    request,
  }) => {
    const KEY = 'sys.security.smsLogin.enabled'
    await gotoConfig(page, 'security')
    await expect(formField(page, /短信验证码登录|SMS code sign-in/)).toHaveCount(0)
    await expect(page.locator('[data-tab="security"]')).toBeVisible()

    await openTab(page, 'signin')
    const sms = page
      .getByRole('switch', { name: /短信验证码登录|SMS code sign-in/ })
      .filter({ visible: true })
    await expect(preview(page).getByTestId('preview-sms')).toHaveCount(0)
    try {
      await sms.click()
      await expect(preview(page).getByTestId('preview-sms')).toBeVisible()
      await expect(page.locator('[data-tab="signin"] .dot')).toBeVisible()
      await expect(page.locator('[data-tab="security"] .dot')).toHaveCount(0)
      await saveAll(page)
      expect(await api<string | null>(request, 'get', `/api/v1/sys/config/value/${KEY}`)).toBe(
        'true',
      )
    } finally {
      await setConfig(request, KEY, 'false')
    }
  })

  test('敏感操作:追加自定义接口 → 预览验证框跟着变 → 删除,都即时生效', async ({
    page,
    request,
  }) => {
    const CODE = `POST:/api/v1/e2e/refund-${Date.now().toString(36)}`
    await gotoConfig(page, 'sensitive')
    const table = page.getByTestId('sensitive-table')
    await expect(table.getByRole('option').first()).toBeVisible()
    await expect(table).toContainText(/内置|Built-in/)

    try {
      await formField(page, /^权限码$|^Permission code$/).fill(CODE)
      await table.getByRole('button', { name: /新增|添加|Add/ }).click()
      const row = table.getByRole('option').filter({ hasText: CODE.slice(5) })
      await expect(row).toBeVisible()
      await expect(row).toHaveAttribute('aria-selected', 'true')
      await expect(preview(page)).toContainText(CODE)
      // 即时生效:不出保存条
      await expect(saveBar(page)).toBeHidden()

      await row.getByRole('button', { name: /删除|Delete/ }).click()
      await page
        .locator('.n-popconfirm')
        .getByRole('button', { name: /确认|确定|Confirm|OK/ })
        .click()
      await expect(row).toHaveCount(0)
    } finally {
      const data = await api<{ customs?: { id: number; permissionCode: string }[] }>(
        request,
        'get',
        '/api/v1/sys/mfa/high-sensitivity',
      )
      for (const c of data.customs ?? [])
        if (c.permissionCode === CODE)
          await api(request, 'delete', `/api/v1/sys/mfa/high-sensitivity/${c.id}`)
    }
  })

  test('高级:新增 → 编辑 → 删除', async ({ page }) => {
    const key = `e2e.other.${Date.now().toString(36)}`
    await gotoConfig(page)
    await openTab(page, 'advanced')

    await page.getByRole('button', { name: /新增|Add/ }).click()
    const add = dialog(page)
    await expect(add).toBeVisible()
    await fieldIn(add, /配置键|Key/).fill(key)
    await fieldIn(add, /配置名称|Name/).fill('E2E 自定义项')
    await fieldIn(add, /配置值|Value/).fill('v1')
    await fieldIn(add, /分组编码|Group/).fill('e2e')
    await add.getByRole('button', { name: /保存|Save/ }).click()
    await expect(add).toBeHidden({ timeout: 5_000 })

    const row = page.locator('.n-data-table-tr').filter({ hasText: key })
    await expect(row).toBeVisible({ timeout: 5_000 })
    await expect(row).toContainText('v1')

    await row.getByRole('button', { name: /编辑|Edit/ }).click()
    const edit = dialog(page)
    await expect(edit).toBeVisible()
    await fieldIn(edit, /配置值|Value/).fill('v2')
    await edit.getByRole('button', { name: /保存|Save/ }).click()
    await expect(edit).toBeHidden({ timeout: 5_000 })
    await expect(row).toContainText('v2', { timeout: 5_000 })

    await row.getByRole('button', { name: /删除|Delete/ }).click()
    await page
      .locator('.n-popconfirm')
      .getByRole('button', { name: /确认|确定|Confirm|OK/ })
      .click()
    await expect(row).toHaveCount(0, { timeout: 5_000 })
  })

  test('高级:列出结构化分组里的零散键,不列表单已认领的键', async ({ page, request }) => {
    const jobKey = `e2e.job.watermark.${Date.now().toString(36)}`
    const id = await api<number>(request, 'post', '/api/v1/sys/config', {
      configKey: jobKey,
      configValue: '2026-01-01',
      name: 'E2E 同步水位',
      groupCode: 'job',
      sort: 99,
    })
    try {
      await gotoConfig(page, 'advanced')
      const table = page.locator('.n-data-table')
      await expect(table.locator('.n-data-table-tr').filter({ hasText: jobKey })).toBeVisible()
      await expect(
        table.locator('.n-data-table-tr').filter({ hasText: 'sys.job.alertEmails' }),
      ).toHaveCount(0)
    } finally {
      await api(request, 'delete', `/api/v1/sys/config/${id}`)
    }
  })

  test('高级:新增表单已认领的键时提示它归哪个分类', async ({ page }) => {
    await gotoConfig(page, 'advanced')
    await page.getByRole('button', { name: /新增|Add/ }).click()
    const add = dialog(page)
    await expect(add).toBeVisible()

    await fieldIn(add, /配置键|Key/).fill('sys.site.title')
    await expect(add.locator('.n-alert')).toContainText(/品牌与登录页|Branding/)
    await fieldIn(add, /配置键|Key/).fill('e2e.free')
    await expect(add.locator('.n-alert')).toHaveCount(0)

    await add.getByRole('button', { name: /取消|Cancel/ }).click()
    await expect(add).toBeHidden()
  })
})
