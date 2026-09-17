<script setup lang="ts">
// 复用 MenuSearch.vue 的"搜索+标题翻译+高亮候选"思路,但不是同一个组件——MenuSearch 是全局命令面板,
// 点了就跳转;这里点了是回调 pick(仅置顶,不导航),排除列表也不一样(排除已置顶,不是排除当前页)。
import { computed, nextTick, ref, watch } from 'vue'
import { NModal, NCard, NInput, NEmpty, NScrollbar } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useMenuFlat, type MenuLeaf } from '#/composables/useMenuFlat'
import { translateMenuTitle } from '#/locales/menuTitle'
import AppIcon from '#/components/AppIcon.vue'

const props = defineProps<{ excludePaths: string[] }>()
const emit = defineEmits<{ pick: [path: string] }>()
const show = defineModel<boolean>('show', { default: false })

const { t } = useI18n()
const leaves = useMenuFlat()
const keyword = ref('')
const inputRef = ref<InstanceType<typeof NInput>>()

watch(show, v => {
  if (v) {
    keyword.value = ''
    nextTick(() => inputRef.value?.focus())
  }
})

function label(item: MenuLeaf) {
  return translateMenuTitle(item.title)
}

const candidates = computed(() => {
  const excluded = new Set(props.excludePaths)
  const kw = keyword.value.trim().toLowerCase()
  return leaves.value
    .filter(l => !excluded.has(l.path))
    .map(l => ({ ...l, display: label(l) }))
    .filter(l => !kw || l.display.toLowerCase().includes(kw))
})

function pick(path: string) {
  emit('pick', path)
  show.value = false
}
</script>

<template>
  <n-modal v-model:show="show">
    <n-card
      style="width: 420px"
      :title="t('biz.quickAdd')"
      :bordered="false"
      size="small"
      role="dialog"
    >
      <n-input ref="inputRef" v-model:value="keyword" :placeholder="t('common.search')" clearable />
      <n-scrollbar style="max-height: 320px; margin-top: 12px">
        <n-empty v-if="!candidates.length" class="empty" />
        <div v-for="c in candidates" :key="c.path" class="row" @click="pick(c.path)">
          <AppIcon :icon="c.icon || 'ph:squares-four'" :size="16" />
          <span>{{ c.display }}</span>
        </div>
      </n-scrollbar>
    </n-card>
  </n-modal>
</template>

<style scoped>
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: var(--radius-md);
  cursor: pointer;
}
.row:hover {
  background: var(--color-fill-hover);
}
.empty {
  padding: 24px 0;
}
</style>
