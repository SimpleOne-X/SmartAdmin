// 配置中心的版面分档(只管样式,不碰业务):由 index.vue 量自己卡片的宽度,经 provide 下发给各分类。
// 三档:
//   nar  卡片 < 600:整页自然滚动,设置行上下排(标签一行、控件占满一行),预览收成可展开的一条并排到最前;
//   side 卡片 ≥ 896:左边设置、右边预览,两栏各自在列内滚;
//   其余(600 到 896):单栏,预览叠在设置下面,整块在内容区里滚。
// 600 与表格窄档对齐(SmartTable 容器宽 < 600 即卡片列表),这样页面进入窄档时,「高级」页的表一定是卡片列表。
import { computed, inject, provide, ref, type ComputedRef, type InjectionKey, type Ref } from 'vue'

/** 卡片(边框盒)宽度低于它算窄档 */
export const NARROW_MAX = 600
/** 卡片宽度达到它才左右分栏 */
export const SIDE_MIN = 896

export type ControlSize = 'medium' | 'large'
export type SmallControlSize = 'small' | 'large'

export interface ConfigLayout {
  nar: ComputedRef<boolean>
  side: ComputedRef<boolean>
  /** 输入框 / 数字框:窄档放大成 large,方便手指点 */
  ctl: ComputedRef<ControlSize>
  /** 行内的小按钮、分段控件 */
  sm: ComputedRef<SmallControlSize>
  /** 保存条、弹窗里的主按钮 */
  btn: ComputedRef<ControlSize>
  /** 窄档下预览栏是否展开(各分类共用,切页签不收回去) */
  previewOpen: Ref<boolean>
}

export function createConfigLayout(width: Ref<number>): ConfigLayout {
  // 宽度还没量到(0)时按宽屏处理,免得首帧闪一下窄档
  const nar = computed(() => width.value > 0 && width.value < NARROW_MAX)
  const side = computed(() => width.value === 0 || width.value >= SIDE_MIN)
  return {
    nar,
    side,
    ctl: computed(() => (nar.value ? 'large' : 'medium')),
    sm: computed(() => (nar.value ? 'large' : 'small')),
    btn: computed(() => (nar.value ? 'large' : 'medium')),
    previewOpen: ref(false),
  }
}

const KEY: InjectionKey<ConfigLayout> = Symbol('config-layout')

export function provideConfigLayout(layout: ConfigLayout) {
  provide(KEY, layout)
}

/** 没有祖先提供时(单独挂载某个分类)按宽屏处理 */
export function useConfigLayout(): ConfigLayout {
  return inject(KEY, () => createConfigLayout(ref(0)), true)
}
