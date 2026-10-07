import { describe, expect, it } from 'vitest'
import { filterTreeByParams, hasTreeCondition } from './treeFilter'

interface N {
  id: number
  name: string
  code: string
  children?: N[]
}

// 总部(HQ)
// ├─ 研发中心(RD)
// │   ├─ 前端组(FE)
// │   └─ 后端组(BE)
// └─ 财务部(FIN)
const fe: N = { id: 4, name: '前端组', code: 'FE' }
const be: N = { id: 5, name: '后端组', code: 'BE' }
const rd: N = { id: 2, name: '研发中心', code: 'RD', children: [fe, be] }
const fin: N = { id: 3, name: '财务部', code: 'FIN' }
const hq: N = { id: 1, name: '总部', code: 'HQ', children: [rd, fin] }
const tree = [hq]

const fields = {
  name: (n: N) => n.name,
  code: (n: N) => n.code,
}

describe('hasTreeCondition', () => {
  it('只认声明过的字段;空串、纯空白、null、undefined 都不算条件', () => {
    expect(hasTreeCondition({}, fields)).toBe(false)
    expect(hasTreeCondition({ name: '', code: '   ' }, fields)).toBe(false)
    expect(hasTreeCondition({ name: null, code: undefined }, fields)).toBe(false)
    // page / pageSize 等库自带参数不是条件
    expect(hasTreeCondition({ page: 1, pageSize: 100 }, fields)).toBe(false)
    expect(hasTreeCondition({ name: '前端' }, fields)).toBe(true)
  })
})

describe('filterTreeByParams', () => {
  it('无条件:整棵树原样返回(节点是原引用),但数组是新的,保证表格每次都收到新结果', () => {
    const out = filterTreeByParams(tree, { page: 1, pageSize: 100 }, fields)
    expect(out).toEqual(tree)
    expect(out).not.toBe(tree)
    expect(out[0]).toBe(hq)
  })

  it('命中子节点:祖先链保留,无关兄弟被剪掉', () => {
    const out = filterTreeByParams(tree, { name: '前端' }, fields)
    expect(out.map(n => n.id)).toEqual([1])
    expect(out[0]!.children!.map(n => n.id)).toEqual([2])
    expect(out[0]!.children![0]!.children!.map(n => n.id)).toEqual([4])
  })

  it('命中父节点:整棵子树保留,哪怕后代自己不匹配', () => {
    const out = filterTreeByParams(tree, { name: '研发' }, fields)
    expect(out[0]!.children!.map(n => n.id)).toEqual([2])
    // 研发中心命中 → 原对象引用,前端组 / 后端组一并带出
    expect(out[0]!.children![0]).toBe(rd)
    expect(out[0]!.children![0]!.children!.map(n => n.id)).toEqual([4, 5])
  })

  it('大小写不敏感,前后空白忽略', () => {
    const out = filterTreeByParams(tree, { code: '  fe ' }, fields)
    expect(out[0]!.children![0]!.children!.map(n => n.id)).toEqual([4])
  })

  it('多个条件是「且」:每个字段都得命中同一个节点', () => {
    // 名称含「组」且编码含 BE → 只有后端组
    const hit = filterTreeByParams(tree, { name: '组', code: 'be' }, fields)
    expect(hit[0]!.children![0]!.children!.map(n => n.id)).toEqual([5])
    // 名称含「财务」且编码含 BE → 没有任何节点同时满足
    expect(filterTreeByParams(tree, { name: '财务', code: 'be' }, fields)).toEqual([])
  })

  it('没有任何节点命中:返回空数组', () => {
    expect(filterTreeByParams(tree, { name: '不存在' }, fields)).toEqual([])
  })

  it('未声明的参数键被忽略,不会把整棵树过滤空', () => {
    expect(filterTreeByParams(tree, { other: 'x' }, fields)).toEqual(tree)
  })

  it('字段取值函数可返回 undefined / null(可空列),当作空串不命中', () => {
    type P = { id: number; path?: string | null; children?: P[] }
    const rows: P[] = [
      { id: 1, path: undefined },
      { id: 2, path: '/sys/user' },
      { id: 3, path: null },
    ]
    const out = filterTreeByParams(rows, { path: 'user' }, { path: n => n.path })
    expect(out.map(r => r.id)).toEqual([2])
  })

  it('不修改传入的树', () => {
    const before = JSON.stringify(tree)
    filterTreeByParams(tree, { name: '前端' }, fields)
    expect(JSON.stringify(tree)).toBe(before)
  })
})
