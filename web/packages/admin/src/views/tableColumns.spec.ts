/// <reference types="node" />
// 扫源码要读目录,tsconfig 的 types 只给 vite/client。
import { readdirSync, readFileSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * 表格单元格里的文字不能溢出到相邻列。
 *
 * 溢出的根子:SmartTable 是 `table-layout: fixed`,没写 `width` 的列各分到约 120~150px,内容更长就直接越界盖到隔壁列上
 * (角色编码 `scope_org_children`、操作日志的 `POST:/api/v1/...` 操作名、`2026-10-09 09:06:51` 时间都这么越过界)。
 * 又因为固定布局只认 `width`,写在列上的 `minWidth` 不起作用(`<col>` 上有 `min-width`,列实际宽度照旧),所以不能靠它兜底。
 *
 * 规则(都按列定义的源码扫,不跑页面——退回的后果不是报错而是界面悄悄变形):
 *   1. 每个数据列要么写 `width`(内容长度可预期),要么写 `ellipsis`(自由文本,超出省略 + 悬浮提示);标签列(`tag: true`)内容固定,不要求;
 *   2. 时间列必须写 `width`,且不小于 160(`YYYY-MM-DD HH:mm:ss` 在 15px 正文下约 135px 加左右内边距);
 *   3. 不写 `minWidth`;
 *   4. 树表(传了 `expanded-row-keys`)要有一列 `tree: true`,否则展开箭头和缩进落在 64px 的序号列上,层级一深序号就被挤没了。
 */
const SRC = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

function vueFiles(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const full = path.join(dir, name)
    if (statSync(full).isDirectory()) return vueFiles(full)
    return name.endsWith('.vue') ? [full] : []
  })
}

/** 取 `{` 起、配平的整个对象字面量。 */
function balanced(src: string, open: number): string {
  let depth = 0
  for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++
    else if (src[i] === '}' && --depth === 0) return src.slice(open, i + 1)
  }
  return src.slice(open)
}

/** 去掉对象里所有嵌套的 `{...}`,只留顶层字段(`search: {...}` / `ellipsis: {...}` 的内容不能算作列自己的字段)。 */
function topLevel(obj: string): string {
  let out = ''
  let depth = 0
  for (const ch of obj) {
    if (ch === '{') depth++
    else if (ch === '}') depth--
    else if (depth === 1) out += ch
  }
  return out
}

interface Col {
  file: string
  key: string
  obj: string
  top: string
}

/** 所有 SmartTable 页面里的数据列:含 `key: '…'` 和函数式 `title: () =>` 的最小对象(导出字段表的 title 不是函数,不会命中)。 */
function columnsOf(file: string, src: string): Col[] {
  const cols: Col[] = []
  const seen = new Set<number>()
  for (const m of src.matchAll(/\bkey:\s*'([^']+)'/g)) {
    const open = src.lastIndexOf('{', m.index)
    if (open < 0 || seen.has(open)) continue
    const obj = balanced(src, open)
    if (!/\btitle:\s*\(\)\s*=>/.test(topLevel(obj))) continue
    seen.add(open)
    cols.push({ file, key: m[1]!, obj, top: topLevel(obj) })
  }
  return cols
}

const files = vueFiles(SRC)
  .map(f => ({ rel: path.relative(SRC, f).replaceAll('\\', '/'), src: readFileSync(f, 'utf8') }))
  .filter(f => f.src.includes('<SmartTable'))

const allCols = files.flatMap(f => columnsOf(f.rel, f.src))
const dataCols = allCols.filter(
  c => !/\bhideInTable:\s*true/.test(c.top) && !/\btype:\s*'/.test(c.top),
)
const label = (c: Col) => `${c.file} → ${c.key}`
const isTime = (c: Col) => /format:\s*'datetime'|fmtDateTime\(/.test(c.obj)

describe('表格列:文字不溢出相邻列', () => {
  it('扫到的列不是空的(正则失灵时别静悄悄全绿)', () => {
    expect(files.length).toBeGreaterThan(15)
    expect(dataCols.length).toBeGreaterThan(100)
  })

  it('每个数据列要么写 width,要么写 ellipsis(标签列除外)', () => {
    const bad = dataCols
      .filter(
        c =>
          !/\bwidth:\s*\d+/.test(c.top) &&
          !/\bellipsis\b/.test(c.top) &&
          !/\btag:\s*true/.test(c.top),
      )
      .map(label)
    expect(bad, '没写宽度也没写省略号的列,内容一长就会越界盖到隔壁列').toEqual([])
  })

  it('时间列写 width 且不小于 160', () => {
    const bad = dataCols
      .filter(isTime)
      .filter(c => {
        const w = /\bwidth:\s*(\d+)/.exec(c.top)
        return !w || Number(w[1]) < 160
      })
      .map(label)
    expect(bad).toEqual([])
    expect(dataCols.filter(isTime).length, '时间列一个都没扫到,识别规则失灵了').toBeGreaterThan(10)
  })

  it('不写 minWidth(固定布局下不起作用,要宽度就写 width)', () => {
    expect(dataCols.filter(c => /\bminWidth:/.test(c.top)).map(label)).toEqual([])
  })

  it('树表要指定 tree: true 的展开列', () => {
    const treeTables = files.filter(f => f.src.includes(':expanded-row-keys'))
    expect(
      treeTables.map(f => f.rel).toSorted(),
      '树表集合变了,确认新页面也满足下面的断言',
    ).toEqual(['views/system/menu/index.vue', 'views/system/org/index.vue'])
    for (const f of treeTables) {
      expect(/\btree:\s*true/.test(f.src), `${f.rel} 没有 tree: true 的列`).toBe(true)
    }
  })
})
