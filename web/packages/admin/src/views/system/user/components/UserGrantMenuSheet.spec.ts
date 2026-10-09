import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { NMessageProvider } from 'naive-ui'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import zhCN from '#/locales/zh-CN'
import phSubset from '#/assets/icons/ph-subset.json'
import { MenuType, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuEffective, type UserMenuGrantItem } from '#/types/api'
import { formatDate, maxExpireDate } from './userGrantState'

// translateError 要用 ApiError 做 instanceof 判断,mock 里得有它。
vi.mock('#/api', () => ({
  ApiError: class ApiError extends Error {},
  menuApi: { tree: vi.fn() },
  userApi: { getMenuGrants: vi.fn(), getEffectiveMenus: vi.fn(), setMenuGrants: vi.fn() },
}))

import { menuApi, userApi } from '#/api'
import UserGrantMenuSheet from './UserGrantMenuSheet.vue'

// 图标走离线子集,否则 Iconify 会去请求公网。
addCollection(phSubset as IconifyJSON)

const treeMock = vi.mocked(menuApi.tree)
const grantsMock = vi.mocked(userApi.getMenuGrants)
const effectiveMock = vi.mocked(userApi.getEffectiveMenus)
const saveMock = vi.mocked(userApi.setMenuGrants)

const node = (
  id: number,
  type: MenuType,
  title: string,
  permission = '',
  children: MenuTreeNode[] = [],
): MenuTreeNode => ({
  id,
  parentId: 0,
  type,
  title,
  permission,
  sort: 0,
  enabled: true,
  visible: true,
  moduleId: 1,
  path: type === MenuType.Menu ? `/p${id}` : null,
  children,
})

// 目录「业务」→ 页面「订单」(按钮「查看」「导出」共用接口 GET:/shared)、页面「客户」(按钮「客户查看」也挂 GET:/shared)
const TREE: MenuTreeNode[] = [
  node(100, MenuType.Catalog, '业务', '', [
    node(110, MenuType.Menu, '订单', '', [
      node(111, MenuType.Button, '订单查看', 'GET:/order;GET:/shared'),
      node(112, MenuType.Button, '订单导出', 'GET:/export'),
    ]),
    node(120, MenuType.Menu, '客户', '', [node(121, MenuType.Button, '客户查看', 'GET:/shared')]),
  ]),
]

const effectiveOf = (patch: Partial<UserMenuEffective> = {}): UserMenuEffective => ({
  userId: 7,
  hasRoles: true,
  targetEditable: true,
  readOnlyReason: null,
  delegatedMaxDays: null,
  modules: [{ id: 1, title: '业务应用', delegatable: true }],
  nodes: [100, 110, 111, 112, 120, 121].map(menuId => ({
    menuId,
    moduleId: 1,
    effective: true,
    roles: ['销售'],
    grant: null,
    expired: false,
    deniedByAncestor: false,
    grantable: true,
    leakedCodes: [],
  })),
  ...patch,
})

let app: App<Element> | undefined
let sheet: InstanceType<typeof UserGrantMenuSheet>
let saved = 0
let warnings: string[] = []

function mount() {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({
    render: () =>
      h(NMessageProvider, null, {
        default: () =>
          h(UserGrantMenuSheet, {
            ref: (r: unknown) => (sheet = r as InstanceType<typeof UserGrantMenuSheet>),
            onSaved: () => saved++,
          }),
      }),
  })
  app.use(createPinia())
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.config.warnHandler = msg => warnings.push(msg)
  app.mount(host)
}

/** 等 open() 里的异步取数、渲染落定。 */
async function settle() {
  for (let i = 0; i < 8; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

const qa = (sel: string, root: ParentNode = document.body) => [
  ...root.querySelectorAll<HTMLElement>(sel),
]
const rowOf = (title: string) =>
  qa('.ugr').find(r => r.querySelector('.ugr-title')?.textContent === title)!
/** 点某行三态里的某个选项。 */
async function pick(title: string, label: string) {
  const radio = qa('.n-radio-button', rowOf(title)).find(r => r.textContent?.trim() === label)!
  radio.click()
  await settle()
}
const saveButton = () => qa('.n-button').find(b => b.textContent?.trim() === '保存')

async function openSheet(
  opts: { effective?: UserMenuEffective; grants?: UserMenuGrantItem[] } = {},
) {
  treeMock.mockResolvedValue(TREE)
  grantsMock.mockResolvedValue(opts.grants ?? [])
  effectiveMock.mockResolvedValue(opts.effective ?? effectiveOf())
  mount()
  sheet.open({ id: 7, name: '张三' })
  await settle()
}

beforeEach(() => {
  saved = 0
  warnings = []
  saveMock.mockResolvedValue(true)
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('UserGrantMenuSheet 打开与渲染', () => {
  it('拉齐三份数据,标题带用户名,每个节点一行三态,模板里没有未注册的组件', async () => {
    await openSheet()

    expect(document.body.textContent).toContain('授权菜单 · 张三')
    expect(userApi.getMenuGrants).toHaveBeenCalledWith(7)
    expect(userApi.getEffectiveMenus).toHaveBeenCalledWith(7)
    // 目录 1 + 页面 2 + 按钮 3
    expect(qa('.ugr')).toHaveLength(6)
    expect(qa('.n-radio-button', rowOf('订单'))).toHaveLength(3)
    expect(warnings.filter(w => w.includes('Failed to resolve component'))).toEqual([])
  })

  it('没有任何角色时顶部提示数据范围为仅本人', async () => {
    await openSheet({ effective: effectiveOf({ hasRoles: false }) })
    expect(document.body.textContent).toContain('该用户没有任何角色')
  })

  it('目标用户不可编辑:整体只读、说明原因、没有保存钮', async () => {
    await openSheet({ effective: effectiveOf({ targetEditable: false, readOnlyReason: 42007 }) })

    expect(document.body.textContent).toContain('只能查看,不能修改:超级管理员受保护,禁止此操作')
    expect(saveButton()).toBeUndefined()
    const radios = qa('.n-radio-button')
    expect(radios.length).toBeGreaterThan(0)
    expect(radios.every(r => r.classList.contains('n-radio-button--disabled'))).toBe(true)
  })

  it('取数失败:弹出错误提示,不渲染任何节点行', async () => {
    treeMock.mockRejectedValue(new Error('boom'))
    grantsMock.mockResolvedValue([])
    effectiveMock.mockResolvedValue(effectiveOf())
    mount()
    sheet.open({ id: 7, name: '张三' })
    await settle()

    expect(qa('.ugr')).toHaveLength(0)
    expect(document.body.textContent).toContain('boom')
  })
})

describe('UserGrantMenuSheet 有效权限页签', () => {
  it('切到「有效权限」:表头渲染出来,单独授权页签只是隐藏、不卸载', async () => {
    await openSheet()

    const tab = qa('.n-tabs-tab').find(el => el.textContent?.includes('有效权限'))!
    tab.click()
    await settle()

    const panes = qa('.n-tab-pane')
    expect(panes).toHaveLength(2)
    expect(panes[0]!.style.display).toBe('none')
    expect(panes[0]!.querySelectorAll('.ugr').length).toBe(6)
    expect(panes[1]!.textContent).toContain('授权人')
    expect(warnings.filter(w => w.includes('Failed to resolve component'))).toEqual([])
  })
})

describe('UserGrantMenuSheet 三态与保存', () => {
  it('选「拒绝」:接口还由别的有效节点携带时就地列出来;保存只提交这一条', async () => {
    await openSheet()

    await pick('订单查看', '拒绝')
    const leak = rowOf('订单查看').querySelector('.ugr-leak')
    expect(leak?.textContent).toContain('GET:/shared')
    expect(leak?.textContent).toContain('客户查看')
    // 没有共用的 GET:/order 不出现
    expect(leak?.textContent).not.toContain('GET:/order')
    expect(document.body.textContent).toContain('允许 0 · 拒绝 1')

    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledWith(
      7,
      [{ menuId: 111, effect: UserMenuEffect.Deny, expireTime: null, remark: null }],
      [],
    )
    expect(saved).toBe(1)
  })

  it('受限的普通管理员选「允许」:默认到期日 = 今天 + 最长天数,提交当天最后一秒', async () => {
    await openSheet({ effective: effectiveOf({ delegatedMaxDays: 30 }) })

    await pick('订单导出', '允许')
    const expected = maxExpireDate(30, new Date())!
    expect(qa('.n-date-picker input', rowOf('订单导出'))[0]!.getAttribute('value')).toBe(expected)

    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledWith(
      7,
      [
        {
          menuId: 112,
          effect: UserMenuEffect.Allow,
          expireTime: `${expected}T23:59:59`,
          remark: null,
        },
      ],
      [],
    )
  })

  it('回显已有记录:拒绝改回「跟随角色」进 removes;没改动直接保存不发请求', async () => {
    const grants: UserMenuGrantItem[] = [
      {
        menuId: 120,
        effect: UserMenuEffect.Deny,
        expireTime: null,
        remark: '先停用',
        grantTime: `${formatDate(new Date())}T09:00:00`,
      },
    ]
    await openSheet({ grants })

    const input = qa('input', rowOf('客户')).find(i => i.getAttribute('value') === '先停用')
    expect(input).toBeTruthy()

    saveButton()!.click()
    await settle()
    expect(saveMock).not.toHaveBeenCalled()

    // 弹窗在无改动保存后关闭;重新打开再改
    sheet.open({ id: 7, name: '张三' })
    await settle()
    await pick('客户', '跟随角色')
    expect(document.body.textContent).toContain('有未保存的修改')
    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledWith(7, [], [120])
  })
})
