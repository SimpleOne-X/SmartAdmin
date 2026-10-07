import { describe, expect, it } from 'vitest'
import { buildShortcutGroups } from './shortcutGroups'
import type { UserShortcutItem } from '#/types/api'
import type { MenuLeaf } from '#/composables/useMenuFlat'

const leaves: MenuLeaf[] = [
  { title: '用户管理', path: '/system/user', icon: 'ph:users-duotone', breadcrumb: [] },
  { title: '角色管理', path: '/system/role', icon: 'ph:shield-check-duotone', breadcrumb: [] },
]

describe('buildShortcutGroups', () => {
  it('置顶的进 pinned,未置顶的进 suggested,标题/图标取自当前菜单叶子而不是后端原文', () => {
    const shortcuts: UserShortcutItem[] = [
      { menuPath: '/system/user', pinned: true, title: '数据库里的旧标题', icon: 'ph:old-icon' },
      { menuPath: '/system/role', pinned: false, title: '数据库里的旧标题', icon: 'ph:old-icon' },
    ]
    const { pinned, suggested } = buildShortcutGroups(shortcuts, leaves)
    expect(pinned).toEqual([
      { path: '/system/user', title: '用户管理', icon: 'ph:users-duotone', pinned: true },
    ])
    expect(suggested).toEqual([
      { path: '/system/role', title: '角色管理', icon: 'ph:shield-check-duotone', pinned: false },
    ])
  })

  it('菜单在当前应用不可见(已被禁用/无权限/删除)时,对应快捷方式被过滤掉', () => {
    const shortcuts: UserShortcutItem[] = [
      { menuPath: '/system/user', pinned: true, title: '用户管理', icon: 'ph:users-duotone' },
      { menuPath: '/system/gone', pinned: true, title: '已删除的页面', icon: null },
    ]
    const { pinned } = buildShortcutGroups(shortcuts, leaves)
    expect(pinned).toHaveLength(1)
    expect(pinned[0].path).toBe('/system/user')
  })

  it('菜单叶子的 icon 缺省时兜底成通用图标', () => {
    const leavesNoIcon: MenuLeaf[] = [{ title: '用户管理', path: '/system/user', breadcrumb: [] }]
    const shortcuts: UserShortcutItem[] = [
      { menuPath: '/system/user', pinned: false, title: '用户管理', icon: null },
    ]
    const { suggested } = buildShortcutGroups(shortcuts, leavesNoIcon)
    expect(suggested[0].icon).toBe('ph:squares-four')
  })
})
