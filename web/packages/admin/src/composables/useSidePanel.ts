// 带左侧面板(分组 / 机构树…)的页面:内容区窄于阈值时,面板不再占一列,收进左侧抽屉。
//
// 判据是壳层内容区宽度(`useShellBreakpoint().width`,不是视口):侧栏展开 / 收起、抽屉开合都会改变它。
// 面板占掉的宽度本来就会让右侧表格变窄,收进抽屉后表格得到整行宽度;表格自己的窄档(卡片列表)由库按表格宽度另行判定。
//
// 用法(范例 views/system/user):
//   const { compact, drawerOpen } = useSidePanel()
//   <aside v-if="!compact" class="side-filter">…面板…</aside>
//   <SmartTable>  … #toolbar 里放一个按钮:`@click="drawerOpen = true"` …</SmartTable>
//   <SidePanelDrawer v-model:show="drawerOpen" :title="…">…同一份面板内容…</SidePanelDrawer>
// 面板内容只写一份用 VueUse 的 createReusableTemplate,宽屏放进 aside、窄屏放进抽屉。
import { computed, ref, watch, type ComputedRef, type Ref } from 'vue'
import { SHELL_PANEL_DRAWER_MAX, useShellBreakpoint } from './useShellBreakpoint'

export interface SidePanelState {
  /** 内容区宽度 < 阈值:面板该收进抽屉。 */
  compact: ComputedRef<boolean>
  /** 抽屉开合;宽度回到不紧凑时自动关,别留一个对不上布局的遮罩。 */
  drawerOpen: Ref<boolean>
}

/** @param max 内容区窄于它时面板收进抽屉;默认 `SHELL_PANEL_DRAWER_MAX`(1000)。 */
export function useSidePanel(max: number = SHELL_PANEL_DRAWER_MAX): SidePanelState {
  const { width } = useShellBreakpoint()
  const compact = computed(() => width.value < max)
  const drawerOpen = ref(false)
  watch(compact, isCompact => {
    if (!isCompact) drawerOpen.value = false
  })
  return { compact, drawerOpen }
}
