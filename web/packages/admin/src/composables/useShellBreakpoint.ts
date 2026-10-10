// 壳层档位:按「内容区」宽度判(不是视口),侧栏展开 / 收起、抽屉开合都会改变档位。
//
//   wide   ≥ 1400   统计卡等页面栅格铺开
//   mid    600–1399 栅格收窄;带左侧面板的页面在内容区 < 1000 时面板收进抽屉(useSidePanel + SidePanelDrawer)
//   narrow < 600    整页自然滚动,不锁一屏高度;统计卡两列;设置行上下排
//
// 阈值 600 = smart-naive-table 的窄档(表格根节点宽 < 600 换卡片)。壳层 narrow 必须和它对齐:
// 壳层进入「整页自然滚动」时表格一定已是卡片列表;壳层不是 narrow 时表格一定仍是表格。
// 两层阈值错开会出现一段宽度:页面不定高、表格却还在 fill-height,表体塌成最小高度。
// 这条对齐只对开了 card-on-narrow 的表成立;没开的表(表格形态的 fill-height 表)由 layout.css ①′ 让那一页保持定高兜底。
// 阈值直接取 600:量的是 `.page` 的 content box(ResizeObserver 的 contentRect 本身已扣掉内边距),
// 留白不进数字,日后改 `.page` 的内边距不用回来改阈值。
//
// 用法:default.vue 调 provideShellBreakpoint(el) 绑定被量的元素;页面里调 useShellBreakpoint()
// 取 { bp, width }(没有壳层时,如单测或独立挂载,退回按视口宽度估算)。
import {
  computed,
  inject,
  onScopeDispose,
  provide,
  ref,
  watch,
  type InjectionKey,
  type Ref,
} from 'vue'
import { useWindowSize } from '@vueuse/core'

export type ShellBreakpoint = 'wide' | 'mid' | 'narrow'

/** 壳层 narrow 的上界(不含)。与表格窄档 600 对齐,见文件头。 */
export const SHELL_NARROW_MAX = 600
/** 壳层 wide 的下界(含)。 */
export const SHELL_WIDE_MIN = 1400
/** 带左侧面板的页面:内容区窄于此值时面板收进抽屉。 */
export const SHELL_PANEL_DRAWER_MAX = 1000

/** 纯判定,便于单测。 */
export function shellBreakpointOf(width: number): ShellBreakpoint {
  return width >= SHELL_WIDE_MIN ? 'wide' : width >= SHELL_NARROW_MAX ? 'mid' : 'narrow'
}

export interface ShellBreakpointState {
  /** 内容区(`.page` content box)宽度,px */
  width: Ref<number>
  bp: Readonly<Ref<ShellBreakpoint>>
}

const KEY: InjectionKey<ShellBreakpointState> = Symbol('shellBreakpoint')

/** 壳层调用:盯住 el 的 content box,把 { width, bp } 提供给后代。 */
export function provideShellBreakpoint(el: Ref<HTMLElement | undefined>): ShellBreakpointState {
  const width = ref(SHELL_WIDE_MIN) // 首帧按宽档,避免挂载瞬间闪一下 narrow 的自然滚动布局
  const bp = computed(() => shellBreakpointOf(width.value))
  const state: ShellBreakpointState = { width, bp }
  provide(KEY, state)

  let ro: ResizeObserver | undefined
  watch(
    el,
    (node, _old, onCleanup) => {
      if (!node || typeof ResizeObserver === 'undefined') return
      ro = new ResizeObserver(entries => {
        const w = entries[0]?.contentRect.width
        if (w !== undefined) width.value = w
      })
      ro.observe(node)
      onCleanup(() => ro?.disconnect())
    },
    { flush: 'post' },
  )
  onScopeDispose(() => ro?.disconnect())
  return state
}

/** 页面 / 组件调用:取壳层档位。壳层之外退回视口宽度(近似,够单测与独立挂载用)。 */
export function useShellBreakpoint(): ShellBreakpointState {
  const injected = inject(KEY, null)
  if (injected) return injected
  const { width } = useWindowSize()
  return { width, bp: computed(() => shellBreakpointOf(width.value)) }
}
