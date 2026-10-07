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
  min-height: 28px;
  margin: 0 0 4px 12px;
  font-size: 13px;
  font-weight: 500;
  color: var(--text-2);
}
/* 列表卡:不描边、不投影,靠半透明填充底和卡片拉开层次 */
.cfg-list {
  border-radius: var(--radius-lg);
  background: var(--fill);
}
</style>
