import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, type App, type PropType } from 'vue'
import { createI18n } from 'vue-i18n'
import zhCN from '#/locales/zh-CN'
import { MenuType, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuEffective, type UserMenuGrantItem } from '#/types/api'

// SmartTable 在 happy-dom 里没有布局,虚拟滚动渲染不出行;换成按列定义逐格渲染的桩,
// 这样能直接检查这张表的行是怎么算出来的、每一列怎么显示。
interface StubColumn {
  key?: string
  render?: (row: Record<string, unknown>) => unknown
}
vi.mock('smart-naive-table', () => ({
  SmartTable: defineComponent({
    props: {
      columns: { type: Array as PropType<StubColumn[]>, required: true },
      data: { type: Array as PropType<Record<string, unknown>[]>, required: true },
    },
    setup: props => () =>
      h(
        'div',
        { class: 'rows' },
        props.data.map(row =>
          h(
            'div',
            { class: 'row' },
            props.columns.map(col =>
              h(
                'span',
                { class: `cell cell-${col.key}` },
                (col.render ? col.render(row) : (row[col.key ?? ''] ?? '')) as string,
              ),
            ),
          ),
        ),
      ),
  }),
}))

import UserGrantEffectiveTable from './UserGrantEffectiveTable.vue'

const node = (
  id: number,
  type: MenuType,
  title: string,
  children: MenuTreeNode[] = [],
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
})

// 先序:业务(1) → 订单(2) → 订单导出(3);业务 → 客户(4);另有顶级页面 工作台(5)
const TREE: MenuTreeNode[] = [
  node(1, MenuType.Catalog, '业务', [
    node(2, MenuType.Menu, '订单', [node(3, MenuType.Button, '订单导出')]),
    node(4, MenuType.Menu, '客户'),
  ]),
  node(5, MenuType.Menu, '工作台'),
]

const effNode = (menuId: number, patch: Partial<UserMenuEffective['nodes'][number]> = {}) => ({
  menuId,
  moduleId: 1,
  effective: false,
  roles: [] as string[],
  grant: null,
  expired: false,
  deniedByAncestor: false,
  grantable: true,
  leakedCodes: [],
  ...patch,
})

const effective: UserMenuEffective = {
  userId: 7,
  hasRoles: true,
  targetEditable: true,
  delegatedMaxDays: null,
  modules: [],
  nodes: [
    // 故意乱序:显示顺序应跟菜单树的先序,而不是接口返回的顺序
    effNode(5, { effective: true, roles: ['销售', '客服'] }),
    effNode(4, { effective: true, grant: UserMenuEffect.Allow }),
    effNode(3, { effective: false, grant: UserMenuEffect.Deny, deniedByAncestor: true }),
    effNode(2, { effective: true, roles: ['销售'] }),
    effNode(1, { effective: true }),
    // 既无效又没有单独授权:不列出
    effNode(999),
  ],
}

const grants: UserMenuGrantItem[] = [
  {
    menuId: 4,
    effect: UserMenuEffect.Allow,
    expireTime: '2099-01-01T08:30:00',
    remark: '临时对账',
    grantorName: '李管理',
    grantTime: '2026-10-09T09:00:00',
  },
  {
    menuId: 3,
    effect: UserMenuEffect.Deny,
    expireTime: null,
    remark: null,
    grantorName: null,
    grantTime: '2026-10-08T09:00:00',
  },
]

let app: App<Element> | undefined
function mount(eff: UserMenuEffective = effective) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({
    render: () => h(UserGrantEffectiveTable, { tree: TREE, effective: eff, grants }),
  })
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.mount(host)
  return host
}
const cells = (row: Element, key: string) => row.querySelector(`.cell-${key}`)?.textContent
afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('UserGrantEffectiveTable 行', () => {
  it('只列有效的和有单独授权的节点,顺序跟菜单树先序,路径带上目录前缀', () => {
    const rows = [...mount().querySelectorAll('.row')]
    expect(rows.map(r => cells(r, 'path'))).toEqual([
      '业务',
      '业务 / 订单',
      '业务 / 订单 / 订单导出',
      '业务 / 客户',
      '工作台',
    ])
  })

  it('来源:角色名在前,再接允许 / 拒绝、已过期、被上级拒绝', () => {
    const rows = [...mount().querySelectorAll('.row')]
    expect(rows.map(r => cells(r, 'source'))).toEqual([
      '—',
      '销售',
      '拒绝 · 被上级拒绝',
      '允许',
      '销售 · 客服',
    ])
  })

  it('单独授权带上授权人、授权时间、到期与理由;没有记录的节点都是破折号', () => {
    const rows = [...mount().querySelectorAll('.row')]
    const customer = rows[3]!
    expect(cells(customer, 'grantorName')).toBe('李管理')
    expect(cells(customer, 'expireTime')).toBe('2099-01-01 08:30:00')
    expect(cells(customer, 'remark')).toBe('临时对账')

    // 拒绝且长期:到期显示「长期」,没有授权人 / 理由显示破折号
    const denied = rows[2]!
    expect(cells(denied, 'expireTime')).toBe('长期')
    expect(cells(denied, 'grantorName')).toBe('—')
    expect(cells(denied, 'remark')).toBe('—')

    // 纯角色来源的节点:没有单独授权,到期也是破折号
    expect(cells(rows[1]!, 'expireTime')).toBe('—')
  })

  it('状态列区分有效与无效', () => {
    const rows = [...mount().querySelectorAll('.row')]
    expect(rows.map(r => cells(r, 'effective'))).toEqual(['有效', '有效', '无效', '有效', '有效'])
  })
})

describe('UserGrantEffectiveTable 树外节点', () => {
  it('不在菜单树里的节点排在最后,不打乱树的先序', () => {
    const orphan = effNode(888, { effective: true, roles: ['销售'] })
    const rows = [
      ...mount({ ...effective, nodes: [orphan, ...effective.nodes] }).querySelectorAll('.row'),
    ]
    expect(rows.map(r => cells(r, 'path'))).toEqual([
      '业务',
      '业务 / 订单',
      '业务 / 订单 / 订单导出',
      '业务 / 客户',
      '工作台',
      '888',
    ])
  })
})
