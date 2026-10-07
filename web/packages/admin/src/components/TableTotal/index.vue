<script setup lang="ts">
// 分页栏左侧的「共 N 条」:写在 SmartTable 的 #pagination-prefix 插槽里,数字取插槽给的 itemCount,
// 不要自己再数一遍(远程表格里 itemCount 是后端的 total,不是当前页行数)。
import { useI18n } from 'vue-i18n'

defineProps<{ count?: number }>()
const { t } = useI18n()
</script>

<template>
  <span class="table-total tabular">{{ t('table.total', { n: count ?? 0 }) }}</span>
</template>

<style scoped>
.table-total {
  color: var(--text-2);
  white-space: nowrap;
}
</style>
