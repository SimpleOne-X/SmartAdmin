import { describe, it, expect } from 'vitest'
import { MenuType, type MenuTreeNode } from '#/types/menu'
import {
  buildGroups,
  collectChecked,
  countGrants,
  groupCount,
  menusState,
  menuButtonCount,
  recomputeGroup,
  setButtonChecked,
  setGroupChecked,
  setMenuChecked,
} from './grantMenuGroups'

const node = (
  id: number,
  type: MenuType,
  title: string,
  children: MenuTreeNode[] = [],
  extra: Partial<MenuTreeNode> = {},
): MenuTreeNode => ({
  id,
  parentId: 0,
  type,
  title,
  permission: '',
  sort: 0,
  enabled: true,
  visible: true,
  children,
  ...extra,
})

const ANCHOR = '接口权限(无页面)'

// 系统运维目录:一个页面(带两个按钮)+ 一个直挂目录的权限锚点(ping) —— 种子里就是这个形状
const tree: MenuTreeNode[] = [
  node(300, MenuType.Catalog, '系统运维', [
    node(350, MenuType.Menu, '消息通知', [
      node(351, MenuType.Button, '通知-查询'),
      node(352, MenuType.Button, '通知-发布'),
    ]),
    node(301, MenuType.Button, '连通性探针', [], { permission: 'GET:/api/v1/ping' }),
  ]),
  node(100, MenuType.Menu, '工作台'),
]

describe('buildGroups', () => {
  it('目录直属按钮渲染成一行 anchor 行', () => {
    const [ops] = buildGroups(tree, new Set(), ANCHOR)
    expect(ops.menus.map(m => m.title)).toEqual([ANCHOR, '消息通知'])
    const anchor = ops.menus[0]
    expect(anchor.anchor).toBe(true)
    expect(anchor.id).toBe(300)
    expect(anchor.buttons.map(b => b.id)).toEqual([301])
  })

  it('顶级页面自成一组;根级直挂按钮并进合成组', () => {
    const groups = buildGroups(
      [...tree, node(7, MenuType.Button, '根级锚点')],
      new Set([7]),
      ANCHOR,
    )
    expect(groups.map(g => g.title)).toEqual(['系统运维', '工作台', ANCHOR])
    const synthetic = groups[2]
    expect(synthetic.synthetic).toBe(true)
    expect(synthetic.checked).toBe(true)
  })

  it('嵌套目录的页面与锚点都能走到,标题带父目录前缀', () => {
    const nested: MenuTreeNode[] = [
      node(1, MenuType.Catalog, '外层', [
        node(2, MenuType.Catalog, '内层', [
          node(3, MenuType.Menu, '页面'),
          node(4, MenuType.Button, '内层锚点'),
        ]),
      ]),
    ]
    const [g] = buildGroups(nested, new Set(), ANCHOR)
    expect(g.menus.map(m => m.title)).toEqual([`内层 / ${ANCHOR}`, '内层 / 页面'])
  })

  it('已授权的锚点按钮回显为勾选;目录半勾', () => {
    const [ops] = buildGroups(tree, new Set([301]), ANCHOR)
    expect(ops.menus[0].checked).toBe(true)
    expect(ops.indeterminate).toBe(true)
    expect(ops.checked).toBe(false)
  })
})

describe('collectChecked', () => {
  it('anchor 行与合成组自身不进结果,按钮 id 照常提交', () => {
    const groups = buildGroups(
      [...tree, node(7, MenuType.Button, '根级锚点')],
      new Set([301, 7]),
      ANCHOR,
    )
    expect(collectChecked(groups).toSorted((a, b) => a - b)).toEqual([7, 300, 301])
  })

  it('全勾一个目录:目录、页面、按钮、锚点按钮一起提交', () => {
    const groups = buildGroups(tree, new Set(), ANCHOR)
    const ops = groups[0]
    for (const m of ops.menus) {
      m.checked = true
      for (const b of m.buttons) b.checked = true
    }
    recomputeGroup(ops)
    expect(ops.checked).toBe(true)
    expect(collectChecked(groups).toSorted((a, b) => a - b)).toEqual([300, 301, 350, 351, 352])
  })
})

// ── 勾选联动(原先写在 GrantMenuTable.vue 里,抽成纯函数后用这组用例锁住行为)──
describe('勾选联动', () => {
  it('勾目录:其下页面与按钮全勾,取消则全清', () => {
    const [ops] = buildGroups(tree, new Set(), ANCHOR)
    setGroupChecked(ops, true)
    expect(ops.checked).toBe(true)
    expect(ops.indeterminate).toBe(false)
    expect(ops.menus.every(m => m.buttons.every(b => b.checked))).toBe(true)
    setGroupChecked(ops, false)
    expect(collectChecked([ops])).toEqual([])
  })

  it('勾页面:按钮跟着勾,目录变半勾', () => {
    const [ops] = buildGroups(tree, new Set(), ANCHOR)
    const page = ops.menus[1]
    setMenuChecked(ops, page, true)
    expect(page.buttons.map(b => b.checked)).toEqual([true, true])
    expect(ops.indeterminate).toBe(true)
  })

  it('按钮全勾时页面自动勾上;取消一个按钮页面保持已勾', () => {
    const [ops] = buildGroups(tree, new Set(), ANCHOR)
    const page = ops.menus[1]
    setButtonChecked(ops, page, page.buttons[0], true)
    expect(page.checked).toBe(false)
    setButtonChecked(ops, page, page.buttons[1], true)
    expect(page.checked).toBe(true)
    setButtonChecked(ops, page, page.buttons[1], false)
    // 与原实现一致:页面已勾时,单个按钮取消不回收页面权限
    expect(page.checked).toBe(true)
  })

  it('anchor 行的勾选态恒等于「按钮是否全勾」', () => {
    const [ops] = buildGroups(tree, new Set(), ANCHOR)
    const anchor = ops.menus[0]
    setButtonChecked(ops, anchor, anchor.buttons[0], true)
    expect(anchor.checked).toBe(true)
    setButtonChecked(ops, anchor, anchor.buttons[0], false)
    expect(anchor.checked).toBe(false)
  })
})

describe('搜索视图', () => {
  it('目录勾选只作用于传入的可见行,但目录状态按全部行重算', () => {
    const [ops] = buildGroups(tree, new Set(), ANCHOR)
    const visible = [ops.menus[1]]
    setGroupChecked(ops, true, visible)
    expect(ops.menus[1].checked).toBe(true)
    expect(ops.menus[0].checked).toBe(false)
    expect(ops.indeterminate).toBe(true)
    // 视图(可见行)自己的状态是全勾
    expect(menusState(visible)).toEqual({ checked: true, indeterminate: false })
    expect(menusState(ops.menus)).toEqual({ checked: false, indeterminate: true })
  })

  it('顶级页面标记 standalone,目录与合成组不标', () => {
    const groups = buildGroups([...tree, node(7, MenuType.Button, '根级锚点')], new Set(), ANCHOR)
    expect(groups.map(g => !!g.standalone)).toEqual([false, true, false])
  })
})

describe('计数与展示字段', () => {
  const withMeta: MenuTreeNode[] = [
    node(300, MenuType.Catalog, '系统运维', [
      node(
        350,
        MenuType.Menu,
        '消息通知',
        [
          node(351, MenuType.Button, '通知-查询', [], { permission: 'GET:/api/v1/notice' }),
          node(352, MenuType.Button, '通知-发布'),
        ],
        { path: '/ops/notice' },
      ),
      node(301, MenuType.Button, '连通性探针'),
    ]),
    node(100, MenuType.Menu, '工作台'),
  ]

  it('页面带 path、按钮带权限码;空值不带字段', () => {
    const [ops] = buildGroups(withMeta, new Set(), ANCHOR)
    const page = ops.menus[1]
    expect(page.path).toBe('/ops/notice')
    expect(page.buttons[0].permission).toBe('GET:/api/v1/notice')
    expect(page.buttons[1].permission).toBeUndefined()
    expect(ops.menus[0].path).toBeUndefined()
  })

  it('groupCount / menuButtonCount', () => {
    const [ops] = buildGroups(withMeta, new Set([350, 351]), ANCHOR)
    // 条目 = 探针按钮(anchor 行只数按钮)+ 页面 + 2 个按钮
    expect(groupCount(ops)).toEqual({ on: 2, total: 4 })
    expect(menuButtonCount(ops.menus[1])).toEqual({ on: 1, total: 2 })
  })

  it('countGrants:总数、已授权、页面数、按钮数(anchor 行自身不算页面)', () => {
    const groups = buildGroups(withMeta, new Set([350, 351, 301, 100]), ANCHOR)
    expect(countGrants(groups)).toEqual({ on: 4, total: 5, pages: 2, buttons: 2 })
  })
})
