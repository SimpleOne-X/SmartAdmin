import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import type { UserProfile } from '#/types/api'
import type { AppModule } from '#/types/menu'

vi.mock('#/api', () => ({
  personalApi: {
    modules: vi.fn(),
    permissions: vi.fn(),
    profile: vi.fn(),
    setDefaultModule: vi.fn(),
    menu: vi.fn(),
  },
}))
vi.mock('./useAuthMenu', () => ({ buildRoutesForModule: vi.fn() }))
vi.mock('#/router', () => ({
  router: {
    hasRoute: vi.fn(() => false),
    removeRoute: vi.fn(),
    push: vi.fn(),
    replace: vi.fn(),
    currentRoute: { value: { path: '/' } },
  },
}))

import { personalApi } from '#/api'
import { router } from '#/router'
import { buildRoutesForModule } from './useAuthMenu'
import { useModule } from './useModule'
import { useAuthStore } from '#/stores/auth'
import { useUserStore } from '#/stores/user'
import { MenuType, type MenuNode } from '#/types/menu'

const modulesMock = vi.mocked(personalApi.modules)
const permissionsMock = vi.mocked(personalApi.permissions)
const profileMock = vi.mocked(personalApi.profile)
const menuMock = vi.mocked(personalApi.menu)
const buildRoutesMock = vi.mocked(buildRoutesForModule)

function mod(id: number, defaultRoute?: string): AppModule {
  return { id, code: `m${id}`, title: `M${id}`, sort: 0, defaultRoute }
}
function page(id: number, path: string): MenuNode {
  return {
    id,
    parentId: 0,
    type: MenuType.Menu,
    title: `P${id}`,
    path,
    component: 'x/index',
    sort: 0,
    visible: true,
    children: [],
  }
}
function profile(overrides: Partial<UserProfile> = {}): UserProfile {
  return { id: 1, account: 'a', name: 'A', isSuperAdmin: false, avatar: null, ...overrides }
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  permissionsMock.mockResolvedValue(['GET:/x'])
  profileMock.mockResolvedValue(profile())
})

describe('useModule().enterInitial', () => {
  it('modules 空 → {chooser:true},权限码/超管/默认模块落 auth store,不因 userInfo=null 崩溃', async () => {
    modulesMock.mockResolvedValue({ modules: [], defaultModuleId: null })
    profileMock.mockResolvedValue(profile({ isSuperAdmin: true }))

    const auth = useAuthStore()
    const res = await useModule().enterInitial()

    expect(res).toEqual({ chooser: true })
    expect(auth.permissionCodes).toEqual(['GET:/x'])
    expect(auth.permissionsLoaded).toBe(true)
    expect(auth.isSuperAdmin).toBe(true)
    expect(auth.defaultModuleId).toBeNull()
    expect(useUserStore().userInfo).toBeNull() // 未登录场景,回填头像分支被跳过,不炸
  })

  it('持久化的 currentModuleId 命中 → enter(remembered) 优先于 defaultModuleId', async () => {
    modulesMock.mockResolvedValue({ modules: [mod(1), mod(2)], defaultModuleId: 2 })
    const auth = useAuthStore()
    auth.currentModuleId = 1 // 模拟 F5 后持久化字段已恢复

    const res = await useModule().enterInitial()

    expect(buildRoutesMock).toHaveBeenCalledWith(1)
    expect(res).toEqual({ chooser: false, moduleId: 1 })
  })

  it('单模块直进', async () => {
    modulesMock.mockResolvedValue({ modules: [mod(7)], defaultModuleId: null })

    const res = await useModule().enterInitial()

    expect(buildRoutesMock).toHaveBeenCalledWith(7)
    expect(res).toEqual({ chooser: false, moduleId: 7 })
  })

  it('多模块:defaultModuleId 命中进默认;不命中弹 chooser', async () => {
    modulesMock.mockResolvedValue({ modules: [mod(1), mod(2)], defaultModuleId: 2 })
    let res = await useModule().enterInitial()
    expect(buildRoutesMock).toHaveBeenCalledWith(2)
    expect(res).toEqual({ chooser: false, moduleId: 2 })

    buildRoutesMock.mockClear()
    setActivePinia(createPinia()) // 新会话:currentModuleId 无残留
    modulesMock.mockResolvedValue({ modules: [mod(1), mod(2)], defaultModuleId: 99 }) // 99 不在 modules 里
    res = await useModule().enterInitial()
    expect(buildRoutesMock).not.toHaveBeenCalled()
    expect(res).toEqual({ chooser: true })
  })

  // F5/深链时守卫会被并发导航各调一次;buildRoutesForModule 起手 resetRouter(),
  // 两次并发会互相把对方刚注册的动态路由摘掉 —— 偶发 404/白屏就是这么来的。
  it('并发调用合流成一次,结束后锁释放', async () => {
    modulesMock.mockResolvedValue({ modules: [mod(7)], defaultModuleId: null })
    const m = useModule()

    const [a, b] = await Promise.all([m.enterInitial(), m.enterInitial()])

    expect(modulesMock).toHaveBeenCalledTimes(1)
    expect(buildRoutesMock).toHaveBeenCalledTimes(1)
    expect(a).toEqual({ chooser: false, moduleId: 7 })
    expect(b).toEqual(a)

    await m.enterInitial() // 上一轮已结束,再进门要真的再拉一次
    expect(modulesMock).toHaveBeenCalledTimes(2)
  })

  it('permissions() reject → permissionsLoaded=false 且不阻断;profile() reject → isSuperAdmin=false', async () => {
    modulesMock.mockResolvedValue({ modules: [], defaultModuleId: null })
    permissionsMock.mockRejectedValue(new Error('boom'))
    profileMock.mockRejectedValue(new Error('boom'))

    const auth = useAuthStore()
    const res = await useModule().enterInitial()

    expect(res).toEqual({ chooser: true }) // 未被两个 reject 阻断
    expect(auth.permissionsLoaded).toBe(false)
    expect(auth.permissionCodes).toEqual([])
    expect(auth.isSuperAdmin).toBe(false)
  })
})

// 动态路由只注册当前应用的菜单,直接访问别的应用的页面地址会落到通配 404。
// findOwnerModule 回答「这个地址是不是别的应用里的页面」,404 页据此提示并一键切过去。
describe('useModule().findOwnerModule', () => {
  function setup(current: number | null = 1) {
    const auth = useAuthStore()
    auth.modules = [mod(1), mod(2), mod(3)]
    auth.currentModuleId = current
    return auth
  }

  it('在其它应用的菜单里找到该路径 → 返回那个应用;当前应用不重复拉取', async () => {
    setup(1)
    menuMock.mockImplementation(async id =>
      id === 2 ? [page(10, '/workbench')] : [page(11, '/other')],
    )

    const owner = await useModule().findOwnerModule('/workbench')

    expect(owner?.id).toBe(2)
    expect(menuMock).not.toHaveBeenCalledWith(1)
  })

  it('任何应用里都没有 → null', async () => {
    setup(1)
    menuMock.mockResolvedValue([page(10, '/other')])

    expect(await useModule().findOwnerModule('/nope')).toBeNull()
  })

  it('末尾斜杠不影响比对(vue-router 非严格模式下 /a/ 与 /a 是同一条路由)', async () => {
    setup(1)
    menuMock.mockImplementation(async id => (id === 3 ? [page(10, '/workbench')] : []))

    expect((await useModule().findOwnerModule('/workbench/'))?.id).toBe(3)
  })

  it('应用的 defaultRoute 命中就直接返回,不必拉任何菜单', async () => {
    setup(1)
    useAuthStore().modules = [mod(1), mod(2, '/workbench'), mod(3)]

    const owner = await useModule().findOwnerModule('/workbench')

    expect(owner?.id).toBe(2)
    expect(menuMock).not.toHaveBeenCalled()
  })

  it('某个应用的菜单拉取失败不影响其它应用的结果', async () => {
    setup(1)
    menuMock.mockImplementation(async id => {
      if (id === 2) throw new Error('boom')
      return [page(10, '/workbench')]
    })

    expect((await useModule().findOwnerModule('/workbench'))?.id).toBe(3)
  })

  it('只有一个应用时没有"别的应用",不发请求', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1)]
    auth.currentModuleId = 1

    expect(await useModule().findOwnerModule('/workbench')).toBeNull()
    expect(menuMock).not.toHaveBeenCalled()
  })
})

describe('useModule().switchModule', () => {
  it('不带目标 → 落新应用首页(原行为)', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1), mod(2, '/home-2')]
    buildRoutesMock.mockImplementation(async id => {
      auth.currentModuleId = id
    })

    await useModule().switchModule(2)

    expect(router.replace).toHaveBeenCalledWith('/home-2')
  })

  it('带目标 → 建好新应用路由后落到该地址,而不是首页', async () => {
    const auth = useAuthStore()
    auth.modules = [mod(1), mod(2, '/home-2')]
    buildRoutesMock.mockImplementation(async id => {
      auth.currentModuleId = id
    })

    await useModule().switchModule(2, '/workbench?tab=1')

    expect(buildRoutesMock).toHaveBeenCalledWith(2)
    expect(router.replace).toHaveBeenCalledWith('/workbench?tab=1')
  })
})
