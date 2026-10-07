import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import type { FilterAction, FilterState, SmartTableColumn } from 'smart-naive-table'
import { ApiError } from '#/api'
import {
  SEARCH_ACTIONS,
  createClientFilterFetcher,
  createFlatFilterSerializer,
  deriveHeaderFilters,
  flatSearchOf,
  loadWithinServerMax,
  passthroughFilterSerializer,
  presetEqualFilter,
} from './tableFilter'

/** 一个字段上的单条件过滤值(条件构造器单行就是这个形状)。 */
const one = (action: FilterAction, value: unknown) => ({
  logic: 'and' as const,
  conditions: [{ action, value }],
})

describe('SEARCH_ACTIONS', () => {
  it('只开放后端真实支持的比较符', () => {
    expect(SEARCH_ACTIONS.fuzzy).toEqual(['contains'])
    expect(SEARCH_ACTIONS.exact).toEqual(['equal'])
    expect(SEARCH_ACTIONS.dayRange).toEqual(['gte', 'lte', 'equal'])
  })
})

const setupScalar = (opts: Parameters<typeof createFlatFilterSerializer>[0] = {}) => {
  const report = vi.fn()
  return { serialize: createFlatFilterSerializer({ report, ...opts }), report }
}

describe('createFlatFilterSerializer · 标量字段', () => {
  it('contains / equal 摊平成同名扁平键', () => {
    const { serialize, report } = setupScalar()
    expect(serialize({ account: one('contains', '张'), roleId: one('equal', 7) })).toEqual({
      account: '张',
      roleId: 7,
    })
    expect(report).not.toHaveBeenCalled()
  })

  it('false 与 0 是有效值,不能被当空值丢掉', () => {
    const { serialize } = setupScalar()
    expect(serialize({ success: one('equal', false), sort: one('equal', 0) })).toEqual({
      success: false,
      sort: 0,
    })
  })

  it('空串条件不产出键', () => {
    const { serialize, report } = setupScalar()
    expect(serialize({ name: one('contains', '') })).toEqual({})
    expect(report).not.toHaveBeenCalled()
  })

  it('文本值去掉首尾空格(2.1.1 的搜索表单会 trim,粘贴进来的空格不能让查询落空)', () => {
    const { serialize } = setupScalar()
    expect(serialize({ account: one('contains', '  admin ') })).toEqual({ account: 'admin' })
  })

  it('只有空白的文本值等同于没填', () => {
    const { serialize, report } = setupScalar()
    expect(serialize({ account: one('contains', '   ') })).toEqual({})
    expect(report).not.toHaveBeenCalled()
  })

  it('rename 把列 key 改成后端参数名', () => {
    const { serialize } = setupScalar({ rename: { startTime: 'startRange' } })
    expect(serialize({ startTime: one('equal', 'x') })).toEqual({ startRange: 'x' })
  })

  it('同一字段多个条件:取第一个,上报 multiple', () => {
    const { serialize, report } = setupScalar()
    const state: FilterState = {
      name: {
        logic: 'or',
        conditions: [
          { action: 'contains', value: 'a' },
          { action: 'contains', value: 'b' },
        ],
      },
    }
    expect(serialize(state)).toEqual({ name: 'a' })
    expect(report).toHaveBeenCalledTimes(1)
    expect(report).toHaveBeenCalledWith([{ field: 'name', problem: 'multiple' }])
  })

  it('不在开放集里的比较符:忽略并上报 unsupported,不抛错', () => {
    const { serialize, report } = setupScalar()
    expect(() => serialize({ name: one('notEqual', 'a') })).not.toThrow()
    expect(serialize({ name: one('notEqual', 'a') })).toEqual({})
    expect(report).toHaveBeenCalledWith([{ field: 'name', problem: 'unsupported' }])
  })

  it('同样的问题不重复上报,问题消失后再出现会再报', () => {
    const { serialize, report } = setupScalar()
    const bad: FilterState = { name: one('notEqual', 'a') }
    serialize(bad)
    serialize(bad)
    expect(report).toHaveBeenCalledTimes(1)
    serialize({})
    serialize(bad)
    expect(report).toHaveBeenCalledTimes(2)
  })
})

const setupRange = () => {
  const report = vi.fn()
  return {
    serialize: createFlatFilterSerializer({ report, ranges: ['createTime'] }),
    report,
  }
}
const range = (...conditions: { action: FilterAction; value: unknown }[]) => ({
  logic: 'and' as const,
  conditions,
})

describe('createFlatFilterSerializer · 日期区间字段', () => {
  it('gte + lte 合并成 [起, 止]', () => {
    const { serialize, report } = setupRange()
    const out = serialize({
      createTime: range(
        { action: 'gte', value: '2026-01-01' },
        { action: 'lte', value: '2026-01-31' },
      ),
    })
    expect(out).toEqual({ createTime: ['2026-01-01', '2026-01-31'] })
    expect(report).not.toHaveBeenCalled()
  })

  it('只填一端:另一端是空串(后端只带 StartTime 或只带 EndTime)', () => {
    const { serialize } = setupRange()
    expect(serialize({ createTime: range({ action: 'gte', value: '2026-01-01' }) })).toEqual({
      createTime: ['2026-01-01', ''],
    })
    expect(serialize({ createTime: range({ action: 'lte', value: '2026-01-31' }) })).toEqual({
      createTime: ['', '2026-01-31'],
    })
  })

  it('equal = 当天:起止同一天', () => {
    const { serialize } = setupRange()
    expect(serialize({ createTime: range({ action: 'equal', value: '2026-01-05' }) })).toEqual({
      createTime: ['2026-01-05', '2026-01-05'],
    })
  })

  it('用「或」连接 gte 与 lte:仍按且合并,并上报 logic', () => {
    const { serialize, report } = setupRange()
    const out = serialize({
      createTime: {
        logic: 'or',
        conditions: [
          { action: 'gte', value: '2026-01-01' },
          { action: 'lte', value: '2026-01-31' },
        ],
      },
    })
    expect(out).toEqual({ createTime: ['2026-01-01', '2026-01-31'] })
    expect(report).toHaveBeenCalledWith([{ field: 'createTime', problem: 'logic' }])
  })

  it('重复的 gte:第一个生效,上报 unsupported', () => {
    const { serialize, report } = setupRange()
    const out = serialize({
      createTime: range(
        { action: 'gte', value: '2026-01-01' },
        { action: 'gte', value: '2026-02-01' },
      ),
    })
    expect(out).toEqual({ createTime: ['2026-01-01', ''] })
    expect(report).toHaveBeenCalledWith([{ field: 'createTime', problem: 'unsupported' }])
  })

  it('gt / lt 后端没有对应写法:没有可用条件时不产出键,并上报', () => {
    const { serialize, report } = setupRange()
    expect(serialize({ createTime: range({ action: 'gt', value: '2026-01-01' }) })).toEqual({})
    expect(report).toHaveBeenCalledWith([{ field: 'createTime', problem: 'unsupported' }])
  })

  it('rename 与 ranges 同时给:改名后的键承载区间', () => {
    const serialize = createFlatFilterSerializer({
      report: vi.fn(),
      ranges: ['startTime'],
      rename: { startTime: 'startRange' },
    })
    expect(serialize({ startTime: range({ action: 'gte', value: '2026-01-01' }) })).toEqual({
      startRange: ['2026-01-01', ''],
    })
  })
})

describe('flatSearchOf', () => {
  it('旧的 params(外部联动参数、排序)叠上条件序列化结果', () => {
    const inst = {
      params: { orgId: 3, sortField: 'account' },
      filters: ref<FilterState>({ account: one('contains', 'a') }),
    }
    const serialize = createFlatFilterSerializer({ report: vi.fn() })
    expect(flatSearchOf(inst, serialize)).toEqual({ orgId: 3, sortField: 'account', account: 'a' })
  })

  it('实例还没就绪:返回空对象', () => {
    expect(flatSearchOf(undefined)).toEqual({})
  })
})

const makeInst = (filters: FilterState = {}) => ({
  filters: ref<FilterState>(filters),
  setFilter: vi.fn(),
})

describe('presetEqualFilter', () => {
  it('值变了:按 equal 条件写入并返回 true', () => {
    const inst = makeInst()
    expect(presetEqualFilter(inst, 'roleId', 7)).toBe(true)
    expect(inst.setFilter).toHaveBeenCalledWith('roleId', {
      logic: 'and',
      conditions: [{ action: 'equal', value: 7 }],
    })
  })

  it('值没变:不写、返回 false(防止实例就绪与 query 变化各触发一次造成重复请求)', () => {
    const inst = makeInst({ roleId: one('equal', 7) })
    expect(presetEqualFilter(inst, 'roleId', 7)).toBe(false)
    expect(inst.setFilter).not.toHaveBeenCalled()
  })

  it('undefined:清掉该条件', () => {
    const inst = makeInst({ roleId: one('equal', 7) })
    expect(presetEqualFilter(inst, 'roleId', undefined)).toBe(true)
    expect(inst.setFilter).toHaveBeenCalledWith('roleId', null)
  })

  it('本来就没有条件且目标是 undefined:不写', () => {
    const inst = makeInst()
    expect(presetEqualFilter(inst, 'roleId', undefined)).toBe(false)
    expect(inst.setFilter).not.toHaveBeenCalled()
  })
})

const clientRows = Array.from({ length: 5 }, (_, i) => ({ id: i + 1, account: `user${i + 1}` }))
const makeLoad = (all = clientRows) =>
  vi.fn(async (p: { page: number; pageSize: number }) => ({
    items: all.slice((p.page - 1) * p.pageSize, p.page * p.pageSize),
    total: all.length,
  }))
const accountCond = (action: FilterAction, value: unknown) => ({
  account: { logic: 'and' as const, conditions: [{ action, value }] },
})

describe('createClientFilterFetcher', () => {
  it('没有生效条件:原样走服务端分页,不取全量', async () => {
    const load = makeLoad()
    const fetcher = createClientFilterFetcher(load, ['account'])
    const out = await fetcher({ page: 2, pageSize: 2, __filters: {} })
    expect(load).toHaveBeenCalledTimes(1)
    expect(load).toHaveBeenCalledWith({ page: 2, pageSize: 2 })
    expect(out.items.map(r => r.id)).toEqual([3, 4])
    expect(out.total).toBe(5)
  })

  it('有条件:取全量,前端求值后再分页', async () => {
    const load = makeLoad()
    const fetcher = createClientFilterFetcher(load, ['account'])
    const out = await fetcher({
      page: 2,
      pageSize: 2,
      __filters: accountCond('contains', 'user'),
    })
    expect(load).toHaveBeenCalledWith({ page: 1, pageSize: 10000 })
    expect(out.total).toBe(5)
    expect(out.items.map(r => r.id)).toEqual([3, 4])
  })

  it('条件把结果收窄:total 是过滤后的条数', async () => {
    const fetcher = createClientFilterFetcher(makeLoad(), ['account'])
    const out = await fetcher({
      page: 1,
      pageSize: 100,
      __filters: accountCond('equal', 'user3'),
    })
    expect(out.items.map(r => r.id)).toEqual([3])
    expect(out.total).toBe(1)
  })

  it('服务端条数多于取到的条数:通知截断', async () => {
    const onTruncated = vi.fn()
    const load = vi.fn(async () => ({ items: clientRows.slice(0, 3), total: 5 }))
    const fetcher = createClientFilterFetcher(load, ['account'], onTruncated)
    await fetcher({ page: 1, pageSize: 100, __filters: accountCond('contains', 'user') })
    expect(onTruncated).toHaveBeenCalledTimes(1)
  })

  it('passthroughFilterSerializer 把过滤态原样放进 __filters', () => {
    const state = accountCond('equal', 1)
    expect(passthroughFilterSerializer(state)).toEqual({ __filters: state })
  })
})

const pageSizeExceeded = (size: number, max: number) =>
  new ApiError(48001, 'error.request.pageSizeExceeded', { size, max })

describe('loadWithinServerMax', () => {
  it('服务端放得下:一次取到', async () => {
    const load = makeLoad()
    const out = await loadWithinServerMax(load, 10000)
    expect(load).toHaveBeenCalledTimes(1)
    expect(out.total).toBe(5)
  })

  it('服务端上限更低(48001):按它回报的 max 降级重试一次', async () => {
    const load = vi.fn(async (p: { page: number; pageSize: number }) => {
      if (p.pageSize > 200) throw pageSizeExceeded(p.pageSize, 200)
      return { items: clientRows.slice(0, p.pageSize), total: 5 }
    })
    const out = await loadWithinServerMax(load, 10000)
    expect(load).toHaveBeenNthCalledWith(1, { page: 1, pageSize: 10000 })
    expect(load).toHaveBeenNthCalledWith(2, { page: 1, pageSize: 200 })
    expect(out.items).toHaveLength(5)
  })

  it('不是 48001 的错误原样抛出,不重试', async () => {
    const load = vi.fn(async () => {
      throw new ApiError(40006, 'error.auth.unauthorized')
    })
    await expect(loadWithinServerMax(load, 10000)).rejects.toThrow()
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('48001 但回报的 max 不比请求的小:原样抛出,避免死循环', async () => {
    const load = vi.fn(async (p: { page: number; pageSize: number }) => {
      throw pageSizeExceeded(p.pageSize, p.pageSize)
    })
    await expect(loadWithinServerMax(load, 100)).rejects.toThrow()
    expect(load).toHaveBeenCalledTimes(1)
  })
})

describe('createClientFilterFetcher · 服务端上限低于 10000', () => {
  it('有条件时降级到服务端上限取数,并因没取全而提示截断', async () => {
    const onTruncated = vi.fn()
    const load = vi.fn(async (p: { page: number; pageSize: number }) => {
      if (p.pageSize > 3) throw pageSizeExceeded(p.pageSize, 3)
      return { items: clientRows.slice(0, p.pageSize), total: 5 }
    })
    const fetcher = createClientFilterFetcher(load, ['account'], onTruncated)
    const out = await fetcher({
      page: 1,
      pageSize: 100,
      __filters: accountCond('contains', 'user'),
    })
    expect(out.items.map(r => r.id)).toEqual([1, 2, 3])
    expect(onTruncated).toHaveBeenCalledTimes(1)
  })
})

const cols = (...c: SmartTableColumn[]) => c

describe('deriveHeaderFilters:表头漏斗只开给「后端有对应参数」的列', () => {
  it('声明了 search 的列补上 filter,比较符沿用 search.actions', () => {
    const [col] = deriveHeaderFilters(
      cols({ key: 'account', search: { actions: SEARCH_ACTIONS.fuzzy } }),
    )
    expect((col as any).filter).toEqual({ actions: ['contains'] })
  })

  it('没有 search 的列不出漏斗(后端没参数,开了就是在说谎)', () => {
    const [a, b] = deriveHeaderFilters(
      cols({ key: 'remark' }, { type: 'index' } as SmartTableColumn),
    )
    expect((a as any).filter).toBeUndefined()
    expect((b as any).filter).toBeUndefined()
  })

  it('列上已写 filter(含 false)的不覆盖', () => {
    const own = { actions: ['equal' as const] }
    const [a, b] = deriveHeaderFilters(
      cols({ key: 'a', search: {}, filter: own }, { key: 'b', search: {}, filter: false as any }),
    )
    expect((a as any).filter).toBe(own)
    expect((b as any).filter).toBe(false)
  })

  it('带字典选项的列强制单选:后端同一字段只收一个值,多选会被降级成 multiple', () => {
    const [col] = deriveHeaderFilters(
      cols({
        key: 'enabled',
        options: [{ label: '启用', value: true }],
        search: { actions: SEARCH_ACTIONS.exact },
      } as SmartTableColumn),
    )
    expect((col as any).filter).toEqual({ actions: ['equal'], multiple: false })
  })

  it('日期区间搜索 → 日期型漏斗;search.key 改名要同步到过滤态的键', () => {
    const [d, k] = deriveHeaderFilters(
      cols(
        { key: 'createTime', search: { type: 'daterange', actions: SEARCH_ACTIONS.dayRange } },
        { key: 'orgName', search: { key: 'orgId' } },
      ),
    )
    expect((d as any).filter).toEqual({ actions: ['gte', 'lte', 'equal'], type: 'date' })
    expect((k as any).filter).toEqual({ key: 'orgId' })
  })

  it('带自定义控件(search.render)的列进不了条件构造器,也不出漏斗', () => {
    const [col] = deriveHeaderFilters(cols({ key: 'x', search: { render: () => null } }))
    expect((col as any).filter).toBeUndefined()
  })

  it('原地改并返回同一个数组:列声明处的引用不变,SmartTable 不会因列数组换了身份而重置状态', () => {
    const input = cols({ key: 'name', search: {} })
    expect(deriveHeaderFilters(input)).toBe(input)
  })

  it('幂等:调两次结果一样', () => {
    const input = cols({ key: 'name', search: { actions: SEARCH_ACTIONS.fuzzy } })
    const once = JSON.stringify(deriveHeaderFilters(input))
    expect(JSON.stringify(deriveHeaderFilters(input))).toBe(once)
  })
})
