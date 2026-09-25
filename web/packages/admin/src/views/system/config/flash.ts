// v-flash="值":值一变,元素闪一下橙框(样式 .cfg-flash 在 SectionLayout 的预览区里)。
// 用在预览里受某项设置影响的部分上,让用户看到"刚才改的东西落在这里"。
import type { Directive } from 'vue'

export const vFlash: Directive<HTMLElement, unknown> = {
  updated(el, binding) {
    if (binding.value === binding.oldValue) return
    el.classList.remove('cfg-flash')
    void el.offsetWidth // 重新触发动画
    el.classList.add('cfg-flash')
  },
}
