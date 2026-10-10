import { describe, it, expect } from 'vitest'
import {
  buildTree,
  filterTree,
  collectSubtreeIds,
  expandableIds,
  flattenTree,
  rootExpandableIds,
} from './tree'

describe('buildTree', () => {
  it('两层挂载 + parentId 指向不存在的父时孤儿兜底当根不丢数据', () => {
    const flat = [
      { id: 1, parentId: 0 },
      { id: 2, parentId: 1 },
      { id: 3, parentId: 99 }, // 99 不存在于集合内 → 孤儿,兜底当根
    ]
    const tree = buildTree(flat)
    expect(tree).toHaveLength(2)
    expect(tree[0]!.id).toBe(1)
    expect(tree[0]!.children).toHaveLength(1)
    expect(tree[0]!.children![0]!.id).toBe(2)
    expect(tree[1]!.id).toBe(3) // 孤儿没丢
  })

  it('输出保持输入原序', () => {
    // id2 在数组里排第一但父是 id1(排第二);根按各自在原数组中的出场顺序入列。
    const flat = [
      { id: 10, parentId: 0 },
      { id: 11, parentId: 10 },
      { id: 12, parentId: 0 },
      { id: 13, parentId: 10 },
    ]
    const tree = buildTree(flat)
    expect(tree.map(n => n.id)).toEqual([10, 12])
    expect(tree[0]!.children!.map(n => n.id)).toEqual([11, 13])
  })
})

describe('filterTree', () => {
  it('命中节点保留整棵子树且是原对象引用、仅因后代命中而保留的祖先是浅拷贝', () => {
    type Node = { id: number; parentId: number; children?: Node[] }
    const leaf: Node = { id: 5, parentId: 4 }
    const dirMatch: Node = { id: 4, parentId: 2, children: [leaf] }
    const branch: Node = { id: 2, parentId: 1, children: [dirMatch] }
    const root: Node = { id: 1, parentId: 0, children: [branch] }

    const result = filterTree([root], n => n.id === 4)

    expect(result).toHaveLength(1)
    // 祖先(root、branch)未命中,仅因后代命中而保留 → 浅拷贝,不是原对象
    expect(result[0]).not.toBe(root)
    expect(result[0]!.children![0]).not.toBe(branch)
    // dirMatch 本身命中 → 原对象引用,整棵子树(含 leaf)零拷贝带出
    expect(result[0]!.children![0]!.children![0]).toBe(dirMatch)
    expect(result[0]!.children![0]!.children![0]!.children).toBe(dirMatch.children)
  })
})

describe('flattenTree', () => {
  it('深度优先平铺:父在前、子紧随其后,带 depth,children 置空', () => {
    const tree = buildTree([
      { id: 1, parentId: 0 },
      { id: 2, parentId: 1 },
      { id: 3, parentId: 2 },
      { id: 4, parentId: 1 },
      { id: 5, parentId: 0 },
    ])
    const rows = flattenTree(tree)
    expect(rows.map(r => [r.id, r.depth])).toEqual([
      [1, 0],
      [2, 1],
      [3, 2],
      [4, 1],
      [5, 0],
    ])
    expect(rows.every(r => r.children?.length === 0)).toBe(true)
  })

  it('不改入参:原树节点仍带 children,也不被写上 depth', () => {
    const tree = buildTree([
      { id: 1, parentId: 0 },
      { id: 2, parentId: 1 },
    ])
    flattenTree(tree)
    expect(tree[0]!.children).toHaveLength(1)
    expect('depth' in tree[0]!).toBe(false)
  })

  it('空树得空数组', () => {
    expect(flattenTree([])).toEqual([])
  })
})

describe('rootExpandableIds', () => {
  it('只收有子节点的根:根展开露出第二层,更深层级与没有子节点的根都不收', () => {
    const tree = buildTree([
      { id: 1, parentId: 0 },
      { id: 2, parentId: 1 },
      { id: 3, parentId: 2 }, // 第三层:id2 有子节点,但它不是根,不收
      { id: 4, parentId: 1 },
      { id: 5, parentId: 0 }, // 没有子节点的根:无需展开
      { id: 6, parentId: 0 },
      { id: 7, parentId: 6 },
    ])
    expect(rootExpandableIds(tree)).toEqual([1, 6])
  })

  it('空树得空数组', () => {
    expect(rootExpandableIds([])).toEqual([])
  })
})

describe('collectSubtreeIds / expandableIds', () => {
  it('乱序平铺多层子树全收集(含自身),expandableIds 只收有子节点的 id', () => {
    // 刻意打乱顺序:id3 的父 id2 排在它后面,验证不动点循环能收全。
    const flat = [
      { id: 3, parentId: 2 },
      { id: 5, parentId: 0 }, // 无关兄弟根,不应被收进 rootId=1 的子树
      { id: 1, parentId: 0 },
      { id: 4, parentId: 1 },
      { id: 2, parentId: 1 },
    ]
    const ids = collectSubtreeIds(flat, 1)
    expect(ids).toEqual(new Set([1, 4, 2, 3]))
    expect(ids.has(5)).toBe(false)

    const tree = buildTree(flat)
    expect(expandableIds(tree)).toEqual([1, 2])
  })
})
