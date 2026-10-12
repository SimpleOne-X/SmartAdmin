import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// 钉住四个选择器数据源的路径、查询参数与返回形状:不带「查询」权限码的角色靠它们选机构 / 岗位 / 角色 / 人员。
const fetchMock = vi.fn(async (_req: Request) => new Response('{}', { status: 200 }))

vi.mock('#/stores/user', () => ({ useUserStore: () => ({ accessToken: '' }) }))

function reply(data: unknown) {
  fetchMock.mockResolvedValueOnce(
    new Response(JSON.stringify({ code: 0, data }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    }),
  )
}
const lastUrl = () => new URL(fetchMock.mock.calls.at(-1)![0].url)

// 客户端在模块加载时就取走了当时的 fetch,所以桩要先于 import 就位。
let api: typeof import('./index')
beforeEach(async () => {
  vi.resetModules()
  fetchMock.mockClear()
  vi.stubGlobal('fetch', fetchMock)
  api = await import('./index')
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('选择器 options 端点', () => {
  it('orgApi.options → GET /sys/org/options,无查询参数,返回平铺数组', async () => {
    const rows = [{ id: 1, parentId: 0, name: '总公司' }]
    reply(rows)
    await expect(api.orgApi.options()).resolves.toEqual(rows)
    expect(lastUrl().pathname).toBe('/api/v1/sys/org/options')
    expect(lastUrl().search).toBe('')
  })

  it('positionApi.options → GET /sys/position/options', async () => {
    const rows = [{ id: 2, name: '经理' }]
    reply(rows)
    await expect(api.positionApi.options()).resolves.toEqual(rows)
    expect(lastUrl().pathname).toBe('/api/v1/sys/position/options')
  })

  it('roleApi.options → GET /sys/role/options', async () => {
    const rows = [{ id: 3, name: '管理员' }]
    reply(rows)
    await expect(api.roleApi.options()).resolves.toEqual(rows)
    expect(lastUrl().pathname).toBe('/api/v1/sys/role/options')
  })

  it('userApi.options → GET /sys/user/options,page/pageSize 映射成 Current/Size,keyword/orgId 映射成 Keyword/OrgId', async () => {
    reply({ current: 2, size: 20, total: 1, items: [{ id: 4, name: '张三', account: 'zs' }] })
    const res = await api.userApi.options({ page: 2, pageSize: 20, keyword: '张', orgId: 5 })

    expect(res).toEqual({ items: [{ id: 4, name: '张三', account: 'zs' }], total: 1 })
    const url = lastUrl()
    expect(url.pathname).toBe('/api/v1/sys/user/options')
    expect(Object.fromEntries(url.searchParams)).toEqual({
      Current: '2',
      Size: '20',
      Keyword: '张',
      OrgId: '5',
    })
  })

  it('userApi.options 不传 keyword / orgId 时不带这两个参数', async () => {
    reply({ current: 1, size: 50, total: 0, items: [] })
    await api.userApi.options({ page: 1, pageSize: 50 })
    expect(Object.fromEntries(lastUrl().searchParams)).toEqual({ Current: '1', Size: '50' })
  })
})
