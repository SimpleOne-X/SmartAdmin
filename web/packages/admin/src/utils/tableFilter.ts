// 条件构造器(smart-naive-table 3.0 的 search.container: 'table')与后端扁平参数之间的接缝。
//
// 3.0 的条件构造器产出 `filters: [{ field, logic, conditions: [{ action, value }] }]`,
// 而内核各分页端点收的是扁平参数(Account / Name / StartTime…,见 api/index.ts)。
// 后端能表达的条件远比 15 个比较符少 —— 文本只有 Contains,枚举与 id 只有相等,日期只有 >= / <=,
// 同一字段只收一个值 —— 所以这里不做通用翻译,只把「后端真正支持的那一小撮」翻成扁平键,
// api 层的参数形态因此保持扁平。当前限制:后端没有通用 filters 协议。
//
// 后端表达不了的组合(同一字段两个「包含」、日期区间用「或」连接……)不抛错:
// 3.0 在点击事件和请求前都会直接调用序列化器,抛错到不了页面的 @error,只会落到全局 errorHandler。
// 做法是按第一个能识别的条件生效,console.warn,并提示用户一次(同一问题不重复弹)。
import { createDiscreteApi } from 'naive-ui'
import { activeConditions, applyFilters } from 'smart-naive-table'
import type {
  FilterAction,
  FilterConfig,
  FilterSerializer,
  FilterState,
  SmartTableColumn,
  SmartTableFetcher,
  SmartTableInst,
} from 'smart-naive-table'
import { unref } from 'vue'
import { ApiError } from '#/api'
import { t } from '#/locales'

/**
 * 各类搜索项该开放的比较符 —— 取决于后端怎么过滤,而不是前端想开放什么:
 * 开放了后端做不到的比较符,界面就在说谎。
 */
export const SEARCH_ACTIONS: {
  fuzzy: FilterAction[]
  exact: FilterAction[]
  dayRange: FilterAction[]
} = {
  /** 后端 `Contains`(账号、名称、路径……) */
  fuzzy: ['contains'],
  /** 后端 `==`(枚举、状态、id) */
  exact: ['equal'],
  /** 后端 `>=` 起、`<=` 止;「等于」= 当天(与 api 层 dayRange 给结束日补 23:59:59 配合) */
  dayRange: ['gte', 'lte', 'equal'],
}

/** 后端表达不了的原因。 */
export type FilterProblem = 'multiple' | 'unsupported' | 'logic'

export interface FlatFilterOptions {
  /** 列 key → 后端参数名。条件构造器下 `search.key` 被忽略,列 key 与后端参数名不同的要在这里改名。 */
  rename?: Record<string, string>
  /** 日期区间字段(列 key):gte → 起、lte → 止、equal → 当天,合并成 `[起, 止]`。 */
  ranges?: readonly string[]
  /** 出现后端表达不了的条件时的通知;默认 console.warn + 一条 toast。测试里注入它。 */
  report?: (problems: { field: string; problem: FilterProblem }[]) => void
}

/** 标量字段(文本、枚举、id)后端只认这两个比较符。 */
const SCALAR_ACTIONS: readonly FilterAction[] = ['equal', 'contains']

const text = (v: unknown) => (v == null ? '' : String(v))

let toast: ReturnType<typeof createDiscreteApi<'message'>>['message'] | undefined
function defaultReport(problems: { field: string; problem: FilterProblem }[]) {
  console.warn('[SmartAdmin] 搜索条件超出后端能力,已按可识别的条件查询:', problems)
  toast ??= createDiscreteApi(['message']).message
  toast.warning(t('table.filterFallback'))
}

/**
 * 条件构造器的过滤态 → 后端扁平参数。每个表格(或全局)一个实例,实例内记住上一次上报的问题以去重。
 */
export function createFlatFilterSerializer(options: FlatFilterOptions = {}): FilterSerializer {
  const { rename = {}, ranges = [], report = defaultReport } = options
  let lastSignature = ''

  return (state: FilterState) => {
    const out: Record<string, unknown> = {}
    const problems: { field: string; problem: FilterProblem }[] = []

    for (const [field, value] of Object.entries(state)) {
      const conds = activeConditions(value)
      if (conds.length === 0) continue
      const key = rename[field] ?? field

      if (ranges.includes(field)) {
        let start: unknown
        let end: unknown
        for (const c of conds) {
          if (c.action === 'gte' && start === undefined) start = c.value
          else if (c.action === 'lte' && end === undefined) end = c.value
          else if (c.action === 'equal' && start === undefined && end === undefined) {
            start = c.value
            end = c.value
          } else problems.push({ field, problem: 'unsupported' })
        }
        if (conds.length > 1 && value.logic === 'or') problems.push({ field, problem: 'logic' })
        if (start !== undefined || end !== undefined) out[key] = [text(start), text(end)]
        continue
      }

      const usable = conds.filter(c => SCALAR_ACTIONS.includes(c.action))
      if (usable.length < conds.length) problems.push({ field, problem: 'unsupported' })
      if (usable.length > 1) problems.push({ field, problem: 'multiple' })
      if (usable[0]) {
        // 文本去掉首尾空格:条件构造器的序列化结果不经过库的 cleanParams(它才会 trim),
        // 粘贴进来的「 admin 」不 trim 就查不到。只有空白的值等同于没填。
        const v = typeof usable[0].value === 'string' ? usable[0].value.trim() : usable[0].value
        if (v !== '') out[key] = v
      }
    }

    const signature = problems.map(p => `${p.field}:${p.problem}`).join('|')
    if (signature && signature !== lastSignature) report(problems)
    lastSignature = signature
    return out
  }
}

/**
 * 表头漏斗(列头的过滤图标)全站统一开,但只开给「后端有对应参数」的列。
 *
 * 判据就是列上有没有 `search`:声明了 `search` 的列,后端一定认这个参数(条件构造器与漏斗写的是同一份过滤态,
 * 经同一个序列化器翻成扁平参数)。没有 `search` 的列后端不认,给它漏斗等于开放一个点了没反应的控件,所以不开。
 * 通用 `filters` 协议是后端另一项工作;等它落地,这里放宽判据即可,页面不用改。
 *
 * 各页在列数组声明之后调一次。**原地改并返回同一个数组**:列数组换了身份会让 SmartTable 当成列定义变了而重置状态。
 * 比较符沿用 `search.actions`(同样受后端能力约束);带字典选项的列强制单选,因为后端同一字段只收一个值。
 */
export function deriveHeaderFilters<C extends SmartTableColumn<any>>(columns: C[]): C[] {
  for (const col of columns) {
    const c = col as C & { search?: unknown; filter?: unknown; options?: unknown }
    // 特殊列(序号 / 勾选)没有 key;已显式写了 filter(包括 false)的以列上为准
    if (!('key' in c) || c.filter !== undefined) continue
    const search = c.search === true ? {} : c.search
    if (!search || typeof search !== 'object') continue
    const s = search as { actions?: FilterAction[]; type?: string; key?: string; render?: unknown }
    if (s.render) continue // 自定义控件进不了条件构造器,漏斗同样画不出来
    const filter: FilterConfig = {}
    if (s.key) filter.key = s.key
    if (s.actions) filter.actions = s.actions
    if (c.options) filter.multiple = false
    // 搜索控件 → 过滤值控件;switch 没有对应的,走库的缺省推断
    const type = {
      input: 'input',
      number: 'number',
      select: 'select',
      date: 'date',
      daterange: 'date',
    }[s.type ?? ''] as FilterConfig['type']
    if (type && type !== 'input' && type !== 'select') filter.type = type
    ;(c as { filter?: unknown }).filter = filter
  }
  return columns
}

/** 全局默认:不改名、没有日期区间字段。有日期区间或改名需求的页面自己 `createFlatFilterSerializer(...)` 后传给 `:filter-serializer`。 */
export const flatFilterSerializer: FilterSerializer = createFlatFilterSerializer()

/**
 * 当前搜索条件的扁平形态 = `inst.params`(外部联动参数、排序)叠上条件构造器的序列化结果。
 * 导出这类「带当前筛选」的场景用它,取到的键与列表请求一致(序列化器要与该表格的 `:filter-serializer` 是同一个,
 * 否则键会对不上)。
 * `filters` 在类型上是 Ref、通过组件实例读出来是值,`unref` 两种都吃。
 */
export function flatSearchOf(
  inst: Pick<SmartTableInst, 'params' | 'filters'> | undefined,
  serializer: FilterSerializer = flatFilterSerializer,
): Record<string, any> {
  if (!inst) return {}
  return { ...inst.params, ...serializer(unref(inst.filters) ?? {}) }
}

/**
 * 用路由 query 预置一个「等于」条件(角色反查、任务记录跳转)。
 * 值没变返回 false 且不写:实例就绪与 query 变化可能各触发一次 watch,第二次不该再发请求。
 * 远程模式下 `setFilter` 自己会回第 1 页重查,调用方不用再 `search()`。
 */
export function presetEqualFilter(
  inst: Pick<SmartTableInst, 'filters' | 'setFilter'>,
  key: string,
  value: number | undefined,
): boolean {
  const current = unref(inst.filters)?.[key]?.conditions[0]?.value
  if (current === value) return false
  inst.setFilter(
    key,
    value === undefined ? null : { logic: 'and', conditions: [{ action: 'equal', value }] },
  )
  return true
}

/** 取全量时的单页条数:后端 MaxPageSize 的默认上限。 */
const LOAD_ALL_SIZE = 10000

/**
 * 取「尽量多」的一页:先要 `size` 条;部署把后端 `MaxPageSize` 调得比它小时(48001,`args.max` 是服务端的实际上限),
 * 按服务端上限降级重试一次,而不是让搜索整体不可用。降级后取不全,调用方按 `total > items.length` 判断截断。
 * 其它错误、以及回报的上限并不比请求的小(再试也没用)的 48001 原样抛出。
 */
export async function loadWithinServerMax<T>(
  load: (p: { page: number; pageSize: number }) => Promise<{ items: T[]; total: number }>,
  size: number,
): Promise<{ items: T[]; total: number }> {
  try {
    return await load({ page: 1, pageSize: size })
  } catch (e) {
    const max = e instanceof ApiError && e.code === 48001 ? Number(e.args?.max) : NaN
    if (!Number.isFinite(max) || max <= 0 || max >= size) throw e
    return load({ page: 1, pageSize: max })
  }
}

/**
 * 把条件构造器的原始过滤态原样交给 fetcher(键 `__filters`),不摊平。
 * 给「后端没有过滤能力、由前端求值」的表格用,配合 createClientFilterFetcher。
 */
export const passthroughFilterSerializer: FilterSerializer = state => ({ __filters: state })

function defaultTruncated() {
  console.warn('[SmartAdmin] 数据超过 10000 条,搜索只覆盖前 10000 条')
  toast ??= createDiscreteApi(['message']).message
  toast.warning(t('table.filterTruncated'))
}

/**
 * 后端没有过滤能力的分页接口,也能用内置条件搜索:
 * - 没有生效条件:原样走服务端分页(不多取数据);
 * - 有条件:一次取全量(上限 10000),用 3.0 的 applyFilters 在前端求值,再切页。
 * 条件由前端求值,所以 15 个比较符都能用,不受后端能力限制。
 * 全量超过 10000 条时只能覆盖前 10000 条,此时调 onTruncated 提示用户。
 * 要配 `:filter-serializer="passthroughFilterSerializer"` 才能拿到原始过滤态。
 */
export function createClientFilterFetcher<T extends Record<string, any>>(
  load: (p: { page: number; pageSize: number }) => Promise<{ items: T[]; total: number }>,
  fields: readonly string[],
  onTruncated: () => void = defaultTruncated,
): SmartTableFetcher<T> {
  return async params => {
    const state = (params as { __filters?: FilterState }).__filters
    const active = !!state && Object.values(state).some(v => activeConditions(v).length > 0)
    if (!active) return load({ page: params.page, pageSize: params.pageSize })

    const all = await loadWithinServerMax(load, LOAD_ALL_SIZE)
    if (all.total > all.items.length) onTruncated()
    const rows = applyFilters(
      all.items,
      fields.map(field => ({ key: field, field })),
      state,
    )
    const start = (params.page - 1) * params.pageSize
    return { items: rows.slice(start, start + params.pageSize), total: rows.length }
  }
}
