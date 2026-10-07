import { filterTree } from './tree'

/**
 * 树形表格的「条件构造器 → 过滤整棵树」。
 *
 * 为什么不用 SmartTable 的静态模式过滤:3.0 静态模式的 `applyFilters` 是平铺的 `rows.filter(...)`,
 * 不递归 `children`,在树上只会命中顶层行。所以树表走远程模式 —— fetcher 里调这里,
 * 条件构造器只负责收集条件(经全局序列化器摊平成 `{ 列key: 值 }` 传进来),过滤由我们自己递归做。
 */

/** 参数键 → 取该节点对应文本的函数。键必须与搜索列的 key 一致(条件构造器下参数名就是列 key)。 */
export type TreeSearchFields<T> = Record<string, (node: T) => string | null | undefined>

/** 取出生效的条件:`[键, 小写化去空白的搜索词]`。空串 / 纯空白 / null / undefined 不算条件;未声明的键忽略(page、pageSize 等)。 */
function activeNeedles<T>(
  params: Record<string, unknown>,
  fields: TreeSearchFields<T>,
): [string, string][] {
  const out: [string, string][] = []
  for (const key of Object.keys(fields)) {
    const raw = params[key]
    const needle = raw == null ? '' : String(raw).trim().toLowerCase()
    if (needle) out.push([key, needle])
  }
  return out
}

/** 当前参数里有没有生效的树搜索条件。 */
export function hasTreeCondition<T>(
  params: Record<string, unknown>,
  fields: TreeSearchFields<T>,
): boolean {
  return activeNeedles(params, fields).length > 0
}

/**
 * 按条件过滤整棵树:命中节点连同整棵子树保留,仅因后代命中的祖先作为祖先链保留(契约见 `filterTree`)。
 * 多个条件是「且」:同一个节点的每个字段都得包含对应搜索词(大小写不敏感)。
 * 没有条件时返回整棵树的一份浅拷贝数组(节点仍是原引用)—— 数组必须是新的,
 * 否则表格内部按引用判断「数据没变」,不会再发 `loaded`,依赖它的展开状态就不会更新。
 */
export function filterTreeByParams<T extends { children?: T[] }>(
  nodes: T[],
  params: Record<string, unknown>,
  fields: TreeSearchFields<T>,
): T[] {
  const needles = activeNeedles(params, fields)
  if (needles.length === 0) return [...nodes]
  return filterTree(nodes, node =>
    needles.every(([key, needle]) => (fields[key]!(node) ?? '').toLowerCase().includes(needle)),
  )
}
