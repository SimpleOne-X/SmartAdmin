import { describe, expect, it } from 'vitest'
import { describeMenuRoute, menuHasPath } from './authMenuRoute'
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
