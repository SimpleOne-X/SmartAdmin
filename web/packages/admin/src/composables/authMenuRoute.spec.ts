import { describe, expect, it } from 'vitest'
import { describeMenuRoute, menuHasPath, normalizeRoutePath } from './authMenuRoute'
import { MenuType, type MenuNode } from '#/types/menu'

function menu(overrides: Partial<MenuNode> = {}): MenuNode {
  return {
    id: 17,
    parentId: 0,
    type: MenuType.Menu,
    title: 'Broken page',
    path: '/system/broken',
    component: 'system/broken/index',
    icon: 'ph:warning',
    sort: 0,
    visible: true,
    children: [],
    ...overrides,
  }
}

describe('describeMenuRoute', () => {
  it('keeps a missing component route as a diagnosable page', () => {
    expect(describeMenuRoute(menu(), new Set())).toEqual({
      kind: 'missing',
      path: '/system/broken',
      name: 'menu-17',
      title: 'Broken page',
      icon: 'ph:warning',
      component: 'system/broken/index',
    })
  })

  it('preserves external, iframe, empty-component, and view behavior', () => {
    const paths = new Set(['system/user/index'])

    expect(
      describeMenuRoute(menu({ path: 'https://example.test', component: '' }), paths),
    ).toBeNull()
    expect(
      describeMenuRoute(menu({ component: 'https://example.test/docs' }), paths),
    ).toMatchObject({ kind: 'iframe' })
    expect(describeMenuRoute(menu({ component: '' }), paths)).toBeNull()
    expect(describeMenuRoute(menu({ component: 'system/user/index' }), paths)).toMatchObject({
      kind: 'view',
    })
  })

  it('component 带前导斜杠或 .vue 后缀时命中同一个 viewKey', () => {
    const paths = new Set(['system/user/index'])

    expect(describeMenuRoute(menu({ component: '/system/user/index.vue' }), paths)).toMatchObject({
      kind: 'view',
      viewKey: 'system/user/index',
    })
    expect(describeMenuRoute(menu({ component: 'system/user/index' }), paths)).toMatchObject({
      kind: 'view',
      viewKey: 'system/user/index',
    })
  })
})

describe('menuHasPath', () => {
  const keys = new Set(['workbench/index', 'system/user/index'])
  const tree: MenuNode[] = [
    menu({
      id: 1,
      type: MenuType.Catalog,
      path: '/sys',
      component: '',
      children: [
        menu({ id: 2, parentId: 1, path: '/workbench', component: 'workbench/index' }),
        menu({ id: 3, parentId: 1, path: 'system/user', component: 'system/user/index' }),
      ],
    }),
  ]

  it('命中任意层级的页面节点,路径缺前导斜杠时与注册路径同口径', () => {
    expect(menuHasPath(tree, '/workbench', keys)).toBe(true)
    expect(menuHasPath(tree, '/system/user', keys)).toBe(true)
  })

  it('目录节点自身的 path 不算页面;未出现的路径、路径前缀都不命中', () => {
    expect(menuHasPath(tree, '/sys', keys)).toBe(false)
    expect(menuHasPath(tree, '/workbench/typo', keys)).toBe(false)
    expect(menuHasPath(tree, '/work', keys)).toBe(false)
  })

  it('外链、无组件的节点不会注册成路由,也不命中', () => {
    const t = [
      menu({ id: 4, path: 'https://example.test', component: '' }),
      menu({ id: 5, path: '/empty', component: '' }),
    ]
    expect(menuHasPath(t, '/empty', keys)).toBe(false)
  })

  it('空树不命中', () => {
    expect(menuHasPath([], '/workbench', keys)).toBe(false)
  })
})

describe('normalizeRoutePath', () => {
  it('补前导斜杠、去末尾斜杠,全是斜杠时回到根', () => {
    expect(normalizeRoutePath('a')).toBe('/a')
    expect(normalizeRoutePath('/a')).toBe('/a')
    expect(normalizeRoutePath('/a/')).toBe('/a')
    expect(normalizeRoutePath('/a//')).toBe('/a')
    expect(normalizeRoutePath('/a/b/')).toBe('/a/b')
    expect(normalizeRoutePath('/')).toBe('/')
    expect(normalizeRoutePath('///')).toBe('/')
    expect(normalizeRoutePath('')).toBe('/')
  })

  it('地址里有大量连续斜杠时耗时与长度成线性,不会二次方回溯', () => {
    // 末尾不是斜杠的长串斜杠:`/\/+$/` 会在每个起点都扫完整段斜杠再失败,10 万个要几秒。
    // 地址栏里的路径是用户可控的输入,不能让它卡死页面。
    const path = `/${'/'.repeat(100_000)}a`
    const started = performance.now()
    expect(normalizeRoutePath(path)).toBe(path)
    expect(performance.now() - started).toBeLessThan(500)
  })
})
