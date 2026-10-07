<script setup lang="ts" generic="T extends string">
// 分段控件(仿 macOS):一组互斥选项,选中的一格浮起。语义上是 radiogroup,方向键切换。
// 外观是 Radio 按钮款的分段控件(轨道 + 滑块);窄档放大到手指好点的高度。
import { useConfigLayout } from '../layoutMode'

const props = defineProps<{
  options: readonly { value: T; label: string }[]
  label?: string
}>()
const model = defineModel<T>({ required: true })
const { nar } = useConfigLayout()

function onKey(e: KeyboardEvent) {
  const step =
    e.key === 'ArrowRight' || e.key === 'ArrowDown'
      ? 1
      : e.key === 'ArrowLeft' || e.key === 'ArrowUp'
        ? -1
        : 0
  if (!step) return
  e.preventDefault()
  const i = props.options.findIndex(o => o.value === model.value)
  const next = props.options[(i + step + props.options.length) % props.options.length]
  model.value = next.value
  ;(e.currentTarget as HTMLElement)
    .querySelector<HTMLElement>(`[data-value="${next.value}"]`)
    ?.focus()
}
</script>

<template>
  <span :class="['seg', { large: nar }]" role="radiogroup" :aria-label="label" @keydown="onKey">
    <button
      v-for="o in options"
      :key="o.value"
      type="button"
      role="radio"
      :aria-checked="model === o.value"
      :tabindex="model === o.value ? 0 : -1"
      :data-value="o.value"
      :class="{ on: model === o.value }"
      @click="model = o.value"
    >
      {{ o.label }}
    </button>
  </span>
</template>

<style scoped>
.seg {
  display: inline-flex;
  flex: none;
  padding: 2px;
  border-radius: var(--radius-md);
  background: var(--cfg-seg-track, var(--fill));
}
button {
  padding: 0 12px;
  border: 0;
  border-radius: 6px;
  background: none;
  font: inherit;
  font-size: 13px;
  line-height: 24px;
  color: var(--text-2);
  cursor: pointer;
  white-space: nowrap;
}
button:hover {
  color: var(--text-1);
}
button.on {
  background: var(--cfg-seg-thumb, var(--color-bg-container-solid));
  color: var(--text-1);
  font-weight: 500;
}
button:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 1px;
}
.seg.large button {
  padding: 0 16px;
  font-size: 14px;
  line-height: 36px;
}
</style>
