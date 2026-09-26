// 角色授权列表的分组与勾选联动逻辑(纯函数,GrantMenuTable.vue 只管渲染)。
// 三层:目录 → 页面 → 按钮。按钮正常挂在页面下;直接挂在目录下的按钮是"无页面权限项"
//(只给移动端 / 第三方调的接口,没有对应页面),渲染成该目录组里的一行 anchor 行,否则它们勾不到。
import { MenuType, type MenuTreeNode } from '#/types/menu'

export interface ButtonItem {
  id: number
  title: string
  checked: boolean
  /** 权限码(多条以 ; 连接),只作悬停提示,不参与勾选与提交。 */
  permission?: string
}
export interface MenuRow {
  id: number
  title: string
  checked: boolean
  buttons: ButtonItem[]
  /** 页面路由,只作副标题;anchor 行没有。 */
  path?: string
  /** 无页面权限项行:承载目录直属按钮。id 是所属目录的 id,只作 key,不进 collectChecked。 */
  anchor?: boolean
}
export interface CatalogGroup {
  id: number
  title: string
  checked: boolean
  indeterminate: boolean
  menus: MenuRow[]
  /** 根级直挂按钮的合成分组(id=0),自身不是可授权节点,不进 collectChecked。 */
  synthetic?: boolean
  /** 顶级页面(如工作台)自成的一组:界面上不画目录头,直接是一行。 */
  standalone?: boolean
}

function toButtons(nodes: MenuTreeNode[], grantedSet: Set<number>): ButtonItem[] {
  return nodes
    .filter(b => b.type === MenuType.Button)
    .map(b => ({
      id: b.id,
      title: b.title,
      checked: grantedSet.has(b.id),
      permission: b.permission || undefined,
    }))
}

function toMenuRow(m: MenuTreeNode, grantedSet: Set<number>, prefix = ''): MenuRow {
  return {
    id: m.id,
    title: prefix + m.title,
    checked: grantedSet.has(m.id),
    buttons: toButtons(m.children, grantedSet),
    path: m.path || undefined,
  }
}

function anchorRow(
  ownerId: number,
  buttons: ButtonItem[],
  anchorTitle: string,
  prefix = '',
): MenuRow {
  return {
    id: ownerId,
    title: prefix + anchorTitle,
    checked: buttons.length > 0 && buttons.every(b => b.checked),
    buttons,
    anchor: true,
  }
}

/**
 * 一个目录的全部行:直属按钮 → anchor 行(有才出);直属页面 → 各一行;嵌套目录 → 递归,标题带上父目录前缀。
 */
function catalogRows(
  node: MenuTreeNode,
  grantedSet: Set<number>,
  anchorTitle: string,
  prefix = '',
): MenuRow[] {
  const rows: MenuRow[] = []
  const buttons = toButtons(node.children, grantedSet)
  if (buttons.length) rows.push(anchorRow(node.id, buttons, anchorTitle, prefix))
  for (const child of node.children) {
    if (child.type === MenuType.Menu) rows.push(toMenuRow(child, grantedSet, prefix))
    else if (child.type === MenuType.Catalog)
      rows.push(...catalogRows(child, grantedSet, anchorTitle, `${prefix}${child.title} / `))
  }
  return rows
}

/** 一个目录下的可勾选条目:页面 + 按钮;anchor 行自身不是节点,只数它的按钮。 */
function rowItems(menus: MenuRow[]): (MenuRow | ButtonItem)[] {
  return menus.flatMap(m => (m.anchor ? m.buttons : [m, ...m.buttons]))
}

/** 一组行的勾选态(全勾 / 半勾)。搜索时目录头按可见行算,落库的目录状态按全部行算。 */
export function menusState(menus: MenuRow[]): { checked: boolean; indeterminate: boolean } {
  const items = rowItems(menus)
  const checkedCount = items.filter(x => x.checked).length
  return {
    checked: checkedCount > 0 && checkedCount === items.length,
    indeterminate: checkedCount > 0 && checkedCount < items.length,
  }
}

export function recomputeGroup(group: CatalogGroup) {
  Object.assign(group, menusState(group.menus))
}

// ── 勾选联动 ──

/** 勾/取消整个目录:其下页面与按钮一并跟随。搜索过滤时 rows 传可见行,不动被过滤掉的行。 */
export function setGroupChecked(group: CatalogGroup, val: boolean, rows: MenuRow[] = group.menus) {
  for (const m of rows) {
    m.checked = val
    for (const b of m.buttons) b.checked = val
  }
  recomputeGroup(group)
}

/** 勾/取消一个页面(或 anchor 行):其按钮一并跟随。 */
export function setMenuChecked(group: CatalogGroup, menu: MenuRow, val: boolean) {
  menu.checked = val
  for (const b of menu.buttons) b.checked = val
  recomputeGroup(group)
}

/**
 * 勾/取消一个按钮。按钮全勾时顺手把页面勾上;页面已勾时取消单个按钮不回收页面权限
 * (页面权限与按钮权限各自授予)。anchor 行自身不是节点,勾选态就等于「按钮是否全勾」。
 */
export function setButtonChecked(
  group: CatalogGroup,
  menu: MenuRow,
  button: ButtonItem,
  val: boolean,
) {
  button.checked = val
  const allChecked = menu.buttons.length > 0 && menu.buttons.every(b => b.checked)
  if (allChecked || menu.anchor) menu.checked = allChecked
  recomputeGroup(group)
}

// ── 计数(仅用于展示) ──

export interface Count {
  on: number
  total: number
}

export function groupCount(group: Pick<CatalogGroup, 'menus'>): Count {
  const items = rowItems(group.menus)
  return { on: items.filter(x => x.checked).length, total: items.length }
}

export function menuButtonCount(menu: MenuRow): Count {
  return { on: menu.buttons.filter(b => b.checked).length, total: menu.buttons.length }
}

/** 全部分组的授权概况:on / total 含页面与按钮;pages / buttons 是已授权的两类各多少。 */
export type GrantCounts = Count & { pages: number; buttons: number }

/** 授权列表对外汇报的概况:计数 + 相对打开时是否有未保存的改动。 */
export type GrantSummary = GrantCounts & { dirty: boolean }

export function countGrants(groups: CatalogGroup[]): GrantCounts {
  let on = 0
  let total = 0
  let pages = 0
  let buttons = 0
  for (const g of groups) {
    for (const item of rowItems(g.menus)) {
      total++
      if (!item.checked) continue
      on++
      if ('buttons' in item) pages++
      else buttons++
    }
  }
  return { on, total, pages, buttons }
}

/**
 * 顶层节点 → 分组。顶级页面(如工作台)自成一组;顶级目录一组;根级直挂按钮并进一个合成组。
 * @param anchorTitle 无页面权限项行的显示文案(i18n 由调用方给)
 */
export function buildGroups(
  tree: MenuTreeNode[],
  grantedSet: Set<number>,
  anchorTitle: string,
): CatalogGroup[] {
  const groups: CatalogGroup[] = []
  for (const node of tree) {
    if (node.type === MenuType.Button) continue
    const menus =
      node.type === MenuType.Menu
        ? [toMenuRow(node, grantedSet)]
        : catalogRows(node, grantedSet, anchorTitle)
    const group: CatalogGroup = {
      id: node.id,
      title: node.title,
      checked: grantedSet.has(node.id),
      indeterminate: false,
      menus,
      standalone: node.type === MenuType.Menu,
    }
    recomputeGroup(group)
    groups.push(group)
  }
  const rootButtons = toButtons(tree, grantedSet)
  if (rootButtons.length) {
    const group: CatalogGroup = {
      id: 0,
      title: anchorTitle,
      checked: false,
      indeterminate: false,
      menus: [anchorRow(0, rootButtons, anchorTitle)],
      synthetic: true,
    }
    recomputeGroup(group)
    groups.push(group)
  }
  return groups
}

/** 勾选态 → 要提交的菜单 id:目录(全勾或半勾)、页面、按钮;anchor 行与合成组自身不算节点。 */
export function collectChecked(groups: CatalogGroup[]): number[] {
  const ids: number[] = []
  for (const g of groups) {
    if (!g.synthetic && (g.checked || g.indeterminate)) ids.push(g.id)
    for (const m of g.menus) {
      if (!m.anchor && m.checked) ids.push(m.id)
      for (const b of m.buttons) if (b.checked) ids.push(b.id)
    }
  }
  return ids
}
