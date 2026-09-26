<script setup lang="ts">
// 把 text 里第一处命中 query(小写)的片段包成 <mark>。query 为空或没命中就原样输出。
import { computed } from 'vue'

const props = defineProps<{ text: string; query: string }>()

const parts = computed(() => {
  const i = props.query ? props.text.toLowerCase().indexOf(props.query) : -1
  if (i < 0) return null
  return [
    props.text.slice(0, i),
    props.text.slice(i, i + props.query.length),
    props.text.slice(i + props.query.length),
  ] as const
})
</script>

<template>
  <!-- 必须单行:插值与 <mark> 之间的换行会被 Vue 压成空格,高亮词前后就多出空白 -->
  <!-- prettier-ignore -->
  <template v-if="parts">{{ parts[0] }}<mark class="grant-mark">{{ parts[1] }}</mark>{{ parts[2] }}</template>
  <template v-else>{{ text }}</template>
</template>

<style scoped>
.grant-mark {
  background: rgba(255, 204, 0, 0.4);
  color: inherit;
  border-radius: 3px;
  padding: 0 1px;
}
</style>
