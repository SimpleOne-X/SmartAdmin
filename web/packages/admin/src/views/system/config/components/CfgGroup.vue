<script setup lang="ts">
// 圆角分组列表(仿 macOS「系统设置」):组名在卡片外的左上方,#extra 放在组名右侧(如语言切换)。
defineProps<{ title?: string }>()
</script>

<template>
  <div class="cfg-group">
    <h4 v-if="title || $slots.extra">
      <span>{{ title }}</span>
      <slot name="extra" />
    </h4>
    <div class="cfg-list">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.cfg-group {
  flex: none;
  min-width: 0;
}
h4 {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 22px;
  margin: 0 0 6px 12px;
  font-size: var(--font-size-sm);
  font-weight: 500;
  color: var(--color-text-secondary);
}
.cfg-list {
  border-radius: 12px;
  background: var(--color-bg-container);
  border: 1px solid var(--color-border);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.04);
}
</style>
