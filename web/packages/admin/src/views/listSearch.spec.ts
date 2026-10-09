/// <reference types="node" />
// 扫源码要读目录,tsconfig 的 types 只给 vite/client。
import { readdirSync, readFileSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * 所有用了 SmartTable 的表格是同一个样子:卡片顶部单行三段式,
 * 左半 = 内置条件构造器(`:search="{ container: 'table' }"`),不放标题(页签和面包屑已经说明了在哪一页),
 * 右半 = 业务按钮(`#toolbar-right`)+ 内置图标(`:toolbar="TABLE_TOOLBAR"`,刷新 / 放大还原 / 列设置),分页栏左侧显示「共 N 条」。
 * 原因:smart-naive-table 的 `#toolbar` 插槽是工具栏左半,业务按钮写在那里会挤到搜索框左边。
 *
 * 这条守卫防的是:新页面或改动后的页面又退回独立搜索表单、或者各页工具栏各长各的。
 * 它扫源码而不是跑页面,因为退回的后果不是报错,而是界面悄悄变得不一致。
 */
const SRC = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

/** 嵌在弹窗 / 抽屉里的小表:不套用整页列表的工具栏标准(弹窗里切密度没有意义)。 */
const EMBEDDED = new Set([
  // 执行记录详情抽屉里的「各次尝试」子表(一次触发下个位数行),搜索 / 工具栏对它没有意义
  'views/system/job-log/components/AttemptTable.vue',
  // 导入向导「预览改错」步骤里的表:单元格就是输入框 / 字典下拉,嵌在向导弹窗里
  'components/ImportWizard/index.vue',
  // 用户授权弹窗「有效权限」页签里的只读表:随弹窗定高,搜索 / 工具栏对它没有意义
  'views/system/user/components/UserGrantEffectiveTable.vue',
])

/** `#toolbar`(左半)里放的不是按钮,而是限定数据范围的控件,允许留在左半。 */
const LEFT_SLOT_OK: Record<string, string> = {
  'views/system/menu/index.vue': '应用选择器:决定看哪个应用的菜单树,不是业务按钮',
  'views/system/user/index.vue':
    '限定数据范围的控件(窄档的机构按钮,打开左侧机构树抽屉),不是业务按钮',
}

/** 放大还原统一显示;这里登记确有理由关掉的页面。 */
const MAXIMIZE_OFF_OK: Record<string, string> = {
  'views/system/dict/index.vue': '左侧窄栏的字典类型列表,放大后只剩一个窄列表,没有意义',
}

function vueFiles(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const full = path.join(dir, name)
    if (statSync(full).isDirectory()) return vueFiles(full)
    return name.endsWith('.vue') ? [full] : []
  })
}

/** `<SmartTable ...>` 开标签。属性里有 `=>`(箭头函数),所以 `=>` 要先于 `[^>]` 匹配。 */
const TAG = /<SmartTable\b(?:=>|[^>])*>/g

const files = [...vueFiles(path.join(SRC, 'views')), ...vueFiles(path.join(SRC, 'components'))].map(
  full => ({
    rel: path.relative(SRC, full).replaceAll('\\', '/'),
    // 先去掉模板里的 HTML 注释:注释里提到 `<SmartTable>` 不是一张表,不能参与扫描
    text: readFileSync(full, 'utf8').replace(/<!--[\s\S]*?-->/g, ''),
  }),
)

/** 每个非嵌入表格的开标签,连同它所在的文件。 */
const tables = files
  .filter(f => !EMBEDDED.has(f.rel))
  .flatMap(f => (f.text.match(TAG) ?? []).map(tag => ({ rel: f.rel, text: f.text, tag })))

describe('表格统一标准', () => {
  it('扫到了足够多的 SmartTable(防止正则失效后空转通过)', () => {
    const count = files.reduce((n, f) => n + (f.text.match(TAG)?.length ?? 0), 0)
    expect(count).toBeGreaterThanOrEqual(15)
  })

  it("每个表格都声明 container: 'table',搜索在表格卡片里(嵌入弹窗的除外)", () => {
    const offenders = tables.filter(x => !/container:\s*'table'/.test(x.tag)).map(x => x.rel)
    expect(offenders, '请加 :search="{ container: \'table\' }"').toEqual([])
  })

  it('源码里没有 search: true、layout: \'inline\'、:search="false"', () => {
    const offenders: string[] = []
    for (const { rel, text } of files) {
      if (/\bsearch:\s*true\b/.test(text)) offenders.push(`${rel}: search: true`)
      if (/layout:\s*'inline'/.test(text)) offenders.push(`${rel}: layout: 'inline'`)
      if (/:search="false"/.test(text)) offenders.push(`${rel}: :search="false"`)
    }
    expect(
      offenders,
      '搜索列要写 search: { actions: SEARCH_ACTIONS.xxx }(或 search: {});不要关掉搜索、不要用独立搜索表单',
    ).toEqual([])
  })

  it('每个表格都传 TABLE_TOOLBAR,内置按钮全部显示(嵌入弹窗的除外)', () => {
    // 带「更多」菜单的页面用 computed(`:toolbar="toolbar"`,其中展开了 `...TABLE_TOOLBAR`),
    // 所以标签里直接写 TABLE_TOOLBAR,或标签里有 :toolbar 且文件里有 `...TABLE_TOOLBAR` 都算数。
    const offenders = tables
      .filter(
        x =>
          !/TABLE_TOOLBAR/.test(x.tag) &&
          !(/:toolbar=/.test(x.tag) && /\.\.\.TABLE_TOOLBAR/.test(x.text)),
      )
      .map(x => x.rel)
    expect(offenders, '请加 :toolbar="TABLE_TOOLBAR"(或 computed 里展开 ...TABLE_TOOLBAR)').toEqual(
      [],
    )
  })

  // 表格统一标准:页面、表格、表单上方不放菜单名,页签和面包屑已经有了;工具栏没有标题,包括主从页右侧的表。
  // 这条守卫锁「不传标题」,防止有页面把表名塞回工具栏左边、把条件构造器往右挤。
  it('表格工具栏里没有标题(不传 :title、不写 #title)', () => {
    const offenders = files
      .filter(f => /<SmartTable\b/.test(f.text))
      .filter(
        f =>
          (f.text.match(TAG) ?? []).some(tag => /\s:?title=/.test(tag)) ||
          /<template #title>/.test(f.text),
      )
      .map(f => f.rel)
    expect(offenders, '工具栏没有标题:删掉 :title / #title(页签和面包屑已经标明所在页)').toEqual([])
  })

  it('TABLE_TOOLBAR 开放大(maximize),页面不得各自关掉', () => {
    const text = readFileSync(path.join(SRC, 'utils/tableToolbar.ts'), 'utf8')
    expect(text).toMatch(/maximize:\s*true/)
    // 放大还原是统一入口:页面不得自己关掉(确有理由的登记在 MAXIMIZE_OFF_OK)
    const offenders = files
      .filter(f => /<SmartTable\b/.test(f.text) && /maximize:\s*false/.test(f.text))
      .filter(f => !(f.rel in MAXIMIZE_OFF_OK))
      .map(f => f.rel)
    expect(offenders, '放大还原要统一显示;确有理由关的登记到 MAXIMIZE_OFF_OK').toEqual([])
  })

  it('无数据用表格内置的空状态(不写 #empty 插槽、不传 empty-text)', () => {
    const offenders = files
      .filter(f => /<SmartTable\b/.test(f.text))
      .filter(f => /<template #empty>/.test(f.text) || /\sempty-text=/.test(f.text))
      .map(f => f.rel)
    expect(offenders, '删掉自己写的空状态,统一用表格内置的').toEqual([])
  })

  it('定高表格(fill-height)都传 :min-row-height="TABLE_MIN_ROW_HEIGHT",否则滚到底少渲染最后几行', () => {
    const offenders = tables
      .filter(x => /fill-height/.test(x.tag) && !/min-row-height=/.test(x.tag))
      .map(x => x.rel)
    expect(
      offenders,
      '请加 :min-row-height="TABLE_MIN_ROW_HEIGHT"(见 utils/tableToolbar.ts)',
    ).toEqual([])
  })

  it('每张分页表格都在 #pagination-prefix 里显示「共 N 条」(分页关掉的树表、嵌入弹窗的除外)', () => {
    const offenders = tables
      .filter(x => !/:pagination="false"/.test(x.tag))
      .filter(x => !/#pagination-prefix/.test(x.text))
      .map(x => x.rel)
    expect(
      offenders,
      '请加 <template #pagination-prefix="{ itemCount }"><TableTotal :count="itemCount" /></template>',
    ).toEqual([])
  })

  it('业务按钮放 #toolbar-right,不放 #toolbar(搜索靠左、工具栏靠右)', () => {
    const offenders = files
      .filter(
        f => !EMBEDDED.has(f.rel) && !(f.rel in LEFT_SLOT_OK) && /<template #toolbar>/.test(f.text),
      )
      .map(f => f.rel)
    expect(
      offenders,
      '#toolbar 是工具栏左半,会把业务按钮挤到搜索框左边;改用 #toolbar-right',
    ).toEqual([])
  })

  it('例外清单里的页面确实存在(防止改名后例外悄悄失效)', () => {
    const existing = new Set(files.map(f => f.rel))
    const stale = [...EMBEDDED, ...Object.keys(LEFT_SLOT_OK)].filter(rel => !existing.has(rel))
    expect(stale).toEqual([])
  })
})
