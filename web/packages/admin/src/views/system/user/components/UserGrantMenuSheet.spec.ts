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
  // 业务应用的模块号(种子里 2)。不能用 1:那是内置系统应用,它的内核目录对单独授权只剩用户管理 / 角色管理
  moduleId: 2,
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
  delegatedMaxDate: null,
  modules: [{ id: 2, title: '业务应用', delegatable: true }],
  nodes: [100, 110, 111, 112, 120, 121].map(menuId => ({
    menuId,
    moduleId: 2,
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

  it('系统应用里只列出用户管理、角色管理(含按钮);机构、岗位、系统运维等超管专属节点不出现,业务应用照常', async () => {
    const sys = (...args: Parameters<typeof node>): MenuTreeNode => ({
      ...node(...args),
      moduleId: 1,
    })
    const SYSTEM_TREE: MenuTreeNode[] = [
      sys(200, MenuType.Catalog, '组织管理', '', [
        sys(210, MenuType.Menu, '机构管理', '', [
          sys(211, MenuType.Button, '机构查询', 'GET:/org'),
        ]),
        sys(230, MenuType.Menu, '用户管理', '', [
          sys(231, MenuType.Button, '用户查询', 'GET:/user'),
        ]),
        sys(240, MenuType.Menu, '角色管理', '', [
          sys(241, MenuType.Button, '角色查询', 'GET:/role'),
        ]),
      ]),
      sys(300, MenuType.Catalog, '系统运维', '', [sys(310, MenuType.Menu, '系统配置')]),
    ]
    const ids = [200, 210, 211, 230, 231, 240, 241, 300, 310]
    const effective = effectiveOf({
      modules: [
        { id: 1, title: '系统', delegatable: false },
        { id: 2, title: '业务应用', delegatable: true },
      ],
      nodes: ids.map(menuId => ({
        menuId,
        moduleId: 1,
        effective: true,
        roles: ['系统管理员'],
        grant: null,
        expired: false,
        deniedByAncestor: false,
        grantable: true,
        leakedCodes: [],
      })),
    })
    treeMock.mockResolvedValue([...SYSTEM_TREE, ...TREE])
    grantsMock.mockResolvedValue([])
    effectiveMock.mockResolvedValue(effective)
    mount()
    sheet.open({ id: 7, name: '张三' })
    await settle()

    const titles = qa('.ugr .ugr-title').map(el => el.textContent)
    expect(titles).toEqual(['组织管理', '用户管理', '用户查询', '角色管理', '角色查询'])
    expect(titles).not.toContain('机构管理')
    expect(titles).not.toContain('系统运维')
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

  it('服务端给了最晚到期日:默认到期日与提交都按它,不按浏览器今天加天数', async () => {
    // 2099 年远在浏览器今天 + 30 天之后:默认值若还是浏览器算的,这里一眼就是错的
    expect(maxExpireDate(30, new Date())).not.toBe('2099-03-04')
    await openSheet({
      effective: effectiveOf({ delegatedMaxDays: 30, delegatedMaxDate: '2099-03-04' }),
    })

    await pick('订单导出', '允许')
    expect(qa('.n-date-picker input', rowOf('订单导出'))[0]!.getAttribute('value')).toBe(
      '2099-03-04',
    )

    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledWith(
      7,
      [
        {
          menuId: 112,
          effect: UserMenuEffect.Allow,
          expireTime: '2099-03-04T23:59:59',
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

interface Deferred<T> {
  promise: Promise<T>
  resolve: (v: T) => void
  reject: (e: unknown) => void
}
function deferred<T>(): Deferred<T> {
  let resolve!: (v: T) => void
  let reject!: (e: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

const cancelButton = () => qa('.n-button').find(b => b.textContent?.trim() === '取消')
/** 某行里值为 text 的输入框(备注)。 */
const inputOf = (title: string, text: string) =>
  qa('input', rowOf(title)).find(i => i.getAttribute('value') === text)
const grantOf = (menuId: number, effect: UserMenuEffect, remark: string): UserMenuGrantItem => ({
  menuId,
  effect,
  expireTime: null,
  remark,
  grantTime: '2026-10-09T09:00:00',
})

/** 用户 7 的三个请求由测试手动放行,用户 8 的立刻返回。 */
function slowUserSeven(grantsOf8: UserMenuGrantItem[] = [], effectiveOf8 = effectiveOf()) {
  const slow = {
    tree: deferred<MenuTreeNode[]>(),
    grants: deferred<UserMenuGrantItem[]>(),
    effective: deferred<UserMenuEffective>(),
  }
  treeMock.mockReturnValueOnce(slow.tree.promise).mockResolvedValueOnce(TREE)
  grantsMock.mockImplementation(id => (id === 7 ? slow.grants.promise : Promise.resolve(grantsOf8)))
  effectiveMock.mockImplementation(id =>
    id === 7 ? slow.effective.promise : Promise.resolve(effectiveOf8),
  )
  return slow
}

describe('UserGrantMenuSheet 快速切换用户', () => {
  it('用户 A 的请求慢于 B:最终展示 B 的数据,保存提交 B 的 userId,A 的记录不会混进变更集', async () => {
    const slow = slowUserSeven(
      [grantOf(112, UserMenuEffect.Allow, 'B的备注')],
      effectiveOf({ userId: 8 }),
    )
    mount()
    sheet.open({ id: 7, name: '张三' })
    await settle()
    cancelButton()!.click() // 加载期间允许关
    await settle()
    sheet.open({ id: 8, name: '李四' })
    await settle()
    expect(inputOf('订单导出', 'B的备注')).toBeTruthy()

    // A 的响应此时才到
    slow.tree.resolve(TREE)
    slow.grants.resolve([grantOf(120, UserMenuEffect.Deny, 'A的备注')])
    slow.effective.resolve(effectiveOf({ userId: 7, hasRoles: false }))
    await settle()

    expect(document.body.textContent).toContain('授权菜单 · 李四')
    expect(inputOf('订单导出', 'B的备注')).toBeTruthy()
    expect(inputOf('客户', 'A的备注')).toBeUndefined()
    expect(document.body.textContent).not.toContain('该用户没有任何角色')

    // 若 A 的草稿混进来,「客户」会是拒绝,改回跟随就会把 120 当 removes 交给 B
    await pick('客户', '跟随角色')
    await pick('订单查看', '拒绝')
    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledTimes(1)
    expect(saveMock).toHaveBeenCalledWith(
      8,
      [{ menuId: 111, effect: UserMenuEffect.Deny, expireTime: null, remark: null }],
      [],
    )
  })

  it('旧请求失败:不弹错误、不关掉新用户的弹窗', async () => {
    const slow = slowUserSeven()
    mount()
    sheet.open({ id: 7, name: '张三' })
    await settle()
    cancelButton()!.click()
    await settle()
    sheet.open({ id: 8, name: '李四' })
    await settle()
    expect(qa('.ugr')).toHaveLength(6)

    slow.tree.reject(new Error('旧请求失败'))
    slow.grants.resolve([])
    slow.effective.resolve(effectiveOf())
    await settle()

    expect(document.body.textContent).not.toContain('旧请求失败')
    expect(document.body.textContent).toContain('授权菜单 · 李四')
    expect(qa('.ugr')).toHaveLength(6)
    expect(saveButton()).toBeTruthy()
  })

  it('旧请求先回来而新用户还在加载:既不显示旧数据,也不提前结束加载态', async () => {
    const slow = slowUserSeven()
    grantsMock.mockImplementation(id => (id === 7 ? slow.grants.promise : new Promise(() => {})))
    mount()
    sheet.open({ id: 7, name: '张三' })
    await settle()
    cancelButton()!.click()
    await settle()
    sheet.open({ id: 8, name: '李四' })
    await settle()

    slow.tree.resolve(TREE)
    slow.grants.resolve([grantOf(120, UserMenuEffect.Deny, 'A的备注')])
    slow.effective.resolve(effectiveOf({ userId: 7 }))
    await settle()

    expect(qa('.ugr')).toHaveLength(0)
    expect(document.querySelector('.ugs-spin .n-spin-content--spinning')).toBeTruthy()
  })

  it('打开新用户时立刻清掉上一个用户的草稿:加载期间底栏回到 0,不残留未保存提示', async () => {
    await openSheet({ grants: [grantOf(120, UserMenuEffect.Deny, '先停用')] })
    await pick('订单导出', '允许')
    expect(document.body.textContent).toContain('允许 1 · 拒绝 1')
    expect(document.body.textContent).toContain('有未保存的修改')
    cancelButton()!.click()
    await settle()

    grantsMock.mockImplementation(() => new Promise(() => {}))
    effectiveMock.mockImplementation(() => new Promise(() => {}))
    treeMock.mockImplementation(() => new Promise(() => {}))
    sheet.open({ id: 8, name: '李四' })
    await settle()

    expect(document.body.textContent).toContain('允许 0 · 拒绝 0')
    expect(document.body.textContent).not.toContain('有未保存的修改')
  })

  it('上一个用户把记录全改回跟随角色后取消,再打开没有记录的用户:不残留「有未保存的修改」', async () => {
    await openSheet({ grants: [grantOf(120, UserMenuEffect.Deny, '先停用')] })
    await pick('客户', '跟随角色')
    expect(document.body.textContent).toContain('有未保存的修改')
    cancelButton()!.click()
    await settle()

    treeMock.mockResolvedValue(TREE)
    grantsMock.mockResolvedValue([])
    effectiveMock.mockResolvedValue(effectiveOf({ userId: 8 }))
    sheet.open({ id: 8, name: '李四' })
    await settle()

    expect(qa('.ugr')).toHaveLength(6)
    expect(document.body.textContent).not.toContain('有未保存的修改')
  })
})

describe('UserGrantMenuSheet 只读说明', () => {
  it.each([
    ['没有给原因码', null],
    ['原因码没有对应文案', 12345],
  ])('%s:退到不带冒号的通用提示', async (_name, readOnlyReason) => {
    await openSheet({ effective: effectiveOf({ targetEditable: false, readOnlyReason }) })

    const text = document.body.textContent ?? ''
    expect(text).toContain('只能查看,不能修改')
    expect(text).not.toContain('只能查看,不能修改:')
  })
})

describe('UserGrantMenuSheet 保存的守卫', () => {
  /** 往某行的备注框里输入。 */
  async function typeRemark(title: string, value: string) {
    const input = qa('input', rowOf(title)).find(
      i => i.getAttribute('placeholder') === '授权理由(选填)',
    ) as HTMLInputElement
    input.value = value
    input.dispatchEvent(new Event('input', { bubbles: true }))
    await settle()
  }
  const invalidHint = '有 1 条记录的到期日缺失或超出可选范围'

  it('受限管理员改一条没有到期日的「允许」:提示到期日缺失,不发请求', async () => {
    await openSheet({
      effective: effectiveOf({ delegatedMaxDays: 30 }),
      grants: [grantOf(112, UserMenuEffect.Allow, '长期')],
    })
    await typeRemark('订单导出', '改个备注')

    saveButton()!.click()
    await settle()
    expect(document.body.textContent).toContain(invalidHint)
    expect(saveMock).not.toHaveBeenCalled()
    expect(saved).toBe(0)
  })

  it('到期日超出上限同样拦下', async () => {
    await openSheet({
      effective: effectiveOf({ delegatedMaxDays: 30 }),
      grants: [
        { ...grantOf(112, UserMenuEffect.Allow, '太远'), expireTime: '2099-01-01T23:59:59' },
      ],
    })
    await typeRemark('订单导出', '改个备注')

    saveButton()!.click()
    await settle()
    expect(document.body.textContent).toContain(invalidHint)
    expect(saveMock).not.toHaveBeenCalled()
  })

  it('保存校验按服务端给的最晚到期日:当天放行(浏览器算的上限早得多)', async () => {
    await openSheet({
      effective: effectiveOf({ delegatedMaxDays: 30, delegatedMaxDate: '2099-03-04' }),
      grants: [
        { ...grantOf(112, UserMenuEffect.Allow, '长期'), expireTime: '2099-03-04T23:59:59' },
      ],
    })
    await typeRemark('订单导出', '改个备注')

    saveButton()!.click()
    await settle()
    expect(document.body.textContent).not.toContain(invalidHint)
    expect(saveMock).toHaveBeenCalledTimes(1)
  })

  it('保存校验按服务端给的最晚到期日:晚一天拦下', async () => {
    await openSheet({
      effective: effectiveOf({ delegatedMaxDays: 30, delegatedMaxDate: '2099-03-04' }),
      grants: [
        { ...grantOf(112, UserMenuEffect.Allow, '太远'), expireTime: '2099-03-05T23:59:59' },
      ],
    })
    await typeRemark('订单导出', '改个备注')

    saveButton()!.click()
    await settle()
    expect(document.body.textContent).toContain(invalidHint)
    expect(saveMock).not.toHaveBeenCalled()
  })

  it('保存失败:弹错误,弹窗与草稿保留,可以再点一次保存', async () => {
    await openSheet()
    await pick('订单查看', '拒绝')
    saveMock.mockRejectedValueOnce(new Error('保存失败了'))

    saveButton()!.click()
    await settle()
    expect(document.body.textContent).toContain('保存失败了')
    expect(saved).toBe(0)
    expect(qa('.ugr')).toHaveLength(6)
    expect(document.body.textContent).toContain('允许 0 · 拒绝 1')
    expect(document.body.textContent).toContain('有未保存的修改')

    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledTimes(2)
    expect(saveMock).toHaveBeenLastCalledWith(7, saveMock.mock.calls[0]![1], [])
    expect(saved).toBe(1)
  })

  it('保存进行中再点保存:只提交一次', async () => {
    await openSheet()
    await pick('订单查看', '拒绝')
    const pending = deferred<boolean>()
    saveMock.mockReturnValueOnce(pending.promise)

    saveButton()!.click()
    await settle()
    saveButton()!.click()
    await settle()
    expect(saveMock).toHaveBeenCalledTimes(1)

    pending.resolve(true)
    await settle()
    expect(saved).toBe(1)
  })
})
