<script setup lang="ts" generic="T extends string">
// 分段控件(仿 macOS):一组互斥选项,选中的一格浮起。语义上是 radiogroup,方向键切换。
const props = defineProps<{
  options: readonly { value: T; label: string }[]
  label?: string
}>()
const model = defineModel<T>({ required: true })

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
  <span class="seg" role="radiogroup" :aria-label="label" @keydown="onKey">
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
  border-radius: 8px;
  background: var(--color-fill);
}
button {
  padding: 2px 12px;
  border: 0;
  border-radius: 6px;
  background: none;
  font: inherit;
  font-size: var(--font-size-sm);
  line-height: 20px;
  color: var(--color-text-secondary);
  cursor: pointer;
  white-space: nowrap;
}
button.on {
  background: var(--color-bg-container);
  color: var(--color-text-primary);
  font-weight: 500;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.12);
}
button:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: 1px;
}
</style>
