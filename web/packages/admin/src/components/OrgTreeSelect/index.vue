<script setup lang="ts">
// 机构树下拉:onMounted 拉 orgApi.options()(平铺,不占机构页权限码)→ buildTree 拼树;value/placeholder/size… 一切经 $attrs 透传 n-tree-select。
// excludeSubtreeOf:编辑机构选上级时剪掉自身子树(防把自己挂到自己后代下成环)——用户页传 null(不剪),机构页传 editingId。
import { computed, onMounted, ref } from 'vue'
import { NEmpty, NTreeSelect } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { orgApi } from '#/api'
import { buildTree, collectSubtreeIds } from '#/utils/tree'
import type { OrgOption } from '#/types/api'

defineOptions({ inheritAttrs: false })
const props = defineProps<{ excludeSubtreeOf?: number | null }>()

const { t } = useI18n()
const flat = ref<OrgOption[]>([])
const loading = ref(false)
// 失败不弹 message(本组件不依赖 message provider),但下拉空态要说明是加载失败,不能让人当成「没有机构」
const loadFailed = ref(false)
onMounted(async () => {
  loading.value = true
  try {
    flat.value = await orgApi.options()
  } catch {
    loadFailed.value = true
  } finally {
    loading.value = false
  }
})

const options = computed(() => {
  const rows =
    props.excludeSubtreeOf == null
      ? flat.value
      : (() => {
          const excluded = collectSubtreeIds(flat.value, props.excludeSubtreeOf!)
          return flat.value.filter(o => !excluded.has(o.id))
        })()
  return buildTree(rows)
})
</script>

<template>
  <n-tree-select
    :options="options"
    :loading="loading"
    key-field="id"
    label-field="name"
    children-field="children"
    clearable
    v-bind="$attrs"
  >
    <template v-if="loadFailed" #empty>
      <n-empty :description="t('common.loadFailed')" />
    </template>
  </n-tree-select>
</template>
