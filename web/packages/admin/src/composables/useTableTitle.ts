import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { translateMenuTitle } from '#/locales/menuTitle'

/**
 * 当前页面的菜单标题。表格工具栏不放标题,内核页面不调用它;保留给下游应用在页面自己需要标题文案时使用。
 * 与侧栏 / 面包屑 / 多标签走同一个翻译入口,切语言即时生效。
 * 同页多张表需要各自的标题时,用各自已有的 i18n 文案,不要共用这个值。
 */
export function useTableTitle() {
  const route = useRoute()
  return computed(() => translateMenuTitle(String(route.meta.title ?? ''), route.path))
}
