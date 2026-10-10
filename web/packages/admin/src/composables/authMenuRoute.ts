import { isHttpUrl } from '#/utils/url'
import { flattenTree } from '#/utils/tree'
import { menuTitleKey } from '#/locales/menuTitle'
import { MenuType, type MenuNode } from '#/types/menu'

/**
 * 菜单节点的可物化结果。三种变体把 iframe/view/missing 各自所需的数据收窄，调用方无需再次
 * 解释后端的 `component` 字符串。
 */
export type MenuRouteDescriptor =
  | { kind: 'iframe'; path: string; name: string; title: string; icon?: string; iframeSrc: string }
  | { kind: 'view'; path: string; name: string; title: string; icon?: string; viewKey: string }
  | { kind: 'missing'; path: string; name: string; title: string; icon?: string; component: string }

/**
 * 只负责菜单路由决策，让分支逻辑能脱离 vue-router 做单测；路由注册与组件物化仍由
 * `useAuthMenu` 负责。`viewKeys` 必须来自同一张页面 glob，避免存在性判断与 loader 来源漂移。
 */
export function describeMenuRoute(
  node: MenuNode,
  viewKeys: ReadonlySet<string>,
): MenuRouteDescriptor | null {
  if (node.type !== MenuType.Menu || !node.path || isHttpUrl(node.path)) return null

  const path = node.path.startsWith('/') ? node.path : `/${node.path}`
  const name = `menu-${node.id}`
  const component = node.component
  if (component && isHttpUrl(component)) {
    return {
      kind: 'iframe',
      path,
      name,
      title: menuTitleKey(node.title, node.path),
      icon: node.icon,
      iframeSrc: component,
    }
  }
  if (!component) return null

  // 页面表的 key 形如 system/user/index;容忍存量配置带前导斜杠或 .vue 后缀
  const viewKey = component.replace(/^\/+/, '').replace(/\.vue$/, '')
  if (viewKeys.has(viewKey))
    return {
      kind: 'view',
      path,
      name,
      title: menuTitleKey(node.title, node.path),
      icon: node.icon,
      viewKey,
    }
  return {
    kind: 'missing',
    path,
    name,
    title: menuTitleKey(node.title, node.path),
    icon: node.icon,
    component,
  }
}

/**
 * 比对路由路径的口径:补前导斜杠、去末尾斜杠(vue-router 非严格模式下 /a/ 与 /a 是同一条路由)。
 * 末尾斜杠用循环从后往前数:路径来自地址栏,是用户可控的输入,`/\/+$/` 这类正则遇到一长串斜杠会二次方回溯。
 */
export function normalizeRoutePath(path: string): string {
  const withSlash = path.startsWith('/') ? path : `/${path}`
  let end = withSlash.length
  while (end > 1 && withSlash.charCodeAt(end - 1) === 47 /* '/' */) end--
  return withSlash.slice(0, end)
}

/**
 * 这棵菜单树会不会注册出 `path` 这条路由。判据就是 describeMenuRoute 的产出,所以与真正注册时同口径:
 * 目录、外链、没配组件的节点不算。只认整条路径相等 —— 前缀相同不代表是同一个页面。
 */
export function menuHasPath(
  tree: MenuNode[],
  path: string,
  viewKeys: ReadonlySet<string>,
): boolean {
  const target = normalizeRoutePath(path)
  return flattenTree(tree).some(node => {
    const route = describeMenuRoute(node, viewKeys)
    return route !== null && normalizeRoutePath(route.path) === target
  })
}
