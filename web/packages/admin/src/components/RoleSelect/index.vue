<script setup lang="ts">
// 角色选择器:基于 ApiSelect,fetch=roleApi.options(不占角色页权限码)。只列启用角色,label=角色名,
// value=角色 id。多选常用(用户分配角色);multiple/placeholder 等经 $attrs 透传。
// options 不带搜索与分页:一次取回,pageSize 只限制下拉最多列出多少个,按名称搜索交给 n-select 本地过滤。
import type { SelectOption } from 'naive-ui'
import ApiSelect from '#/components/ApiSelect/index.vue'
import { roleApi } from '#/api'

defineOptions({ inheritAttrs: false })
const props = withDefaults(defineProps<{ pageSize?: number }>(), { pageSize: 100 })

async function fetch(): Promise<SelectOption[]> {
  const items = await roleApi.options()
  return items.slice(0, props.pageSize).map(r => ({ label: r.name, value: r.id }))
}
</script>

<template>
  <ApiSelect :fetch="fetch" :remote="false" v-bind="$attrs" />
</template>
