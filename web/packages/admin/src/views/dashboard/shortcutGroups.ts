import type { UserShortcutItem } from '#/types/api'
import type { MenuLeaf } from '#/composables/useMenuFlat'

export interface ShortcutDisplayItem {
  path: string
  /** i18n key 或原文,口径与 MenuLeaf.title 一致——渲染时要经 translateMenuTitle(title),不能直接展示。 */
  title: string
  icon: string
  pinned: boolean
}

/**
 * 后端 personalApi.shortcuts() 已经按"置顶优先 + 高频补位"排好序、按 cap 截断,这里只做两件事:
 * 按 pinned 拆成两组、用当前应用可见的菜单叶子重新取标题/图标(而不是信后端 title——那是数据库原文,
 * 不带 i18n;菜单被禁用/删除/无权限后不在 menuLeaves 里,对应快捷方式自动消失,不用后端联动清理)。
 */
export function buildShortcutGroups(
  shortcuts: UserShortcutItem[],
  menuLeaves: MenuLeaf[],
): { pinned: ShortcutDisplayItem[]; suggested: ShortcutDisplayItem[] } {
  const leafByPath = new Map(menuLeaves.map(l => [l.path, l]))
  const toDisplay = (s: UserShortcutItem): ShortcutDisplayItem | null => {
    const leaf = leafByPath.get(s.menuPath)
    if (!leaf) return null
    return {
      path: s.menuPath,
      title: leaf.title,
      icon: leaf.icon || 'ph:squares-four',
      pinned: s.pinned,
    }
  }
  const display = (list: UserShortcutItem[]) =>
    list.map(toDisplay).filter((x): x is ShortcutDisplayItem => x !== null)
  return {
    pinned: display(shortcuts.filter(s => s.pinned)),
    suggested: display(shortcuts.filter(s => !s.pinned)),
  }
}
