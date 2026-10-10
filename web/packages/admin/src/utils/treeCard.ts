import { h, type VNodeChild } from 'vue'

/**
 * 窄档卡片里没有树的缩进:按 `flattenTree` 给的 depth 给标题缩进,层级一眼可辨。
 * 宽档的行没有 depth,原样返回,由表格自己画缩进与展开箭头。
 */
export const indentByDepth = (row: object, content: VNodeChild): VNodeChild => {
  const depth = (row as { depth?: number }).depth
  return depth
    ? h('span', { style: { paddingInlineStart: `${depth * 16}px` } }, [content])
    : content
}
