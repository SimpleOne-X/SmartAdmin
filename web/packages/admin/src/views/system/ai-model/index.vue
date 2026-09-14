<script setup lang="ts">
// AI 模型管理:厂商是卡片网格,不是 SmartTable —— 厂商数量级很小(十几个封顶),卡片能把
// 徽标/协议/Base URL/模型数/启停/测试连接一次性摆开,比表格行更直观。
// 列表本身一次拉大页(pageSize 100)铺成网格,不做真分页;关键字过滤(厂商 code/name)交给
// 后端 keyword 参数,输入防抖 400ms 后自动重拉,回车立即拉一次。
// 交互都收进 ProviderCard(启停/编辑入口/删除/测试连接)与 ProviderForm(新增/编辑抽屉),
// 本页只管拉列表、开表单、增删改后 reload。
import { onMounted, ref } from 'vue'
import { NButton, NEmpty, NGi, NGrid, NInput, NSpin, useMessage } from 'naive-ui'
import { watchDebounced } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { aiProviderApi } from '#/api'
import { translateError } from '#/utils/error'
import type { AiProviderView } from '#/types/api'
import ProviderCard from './components/ProviderCard.vue'
import ProviderForm from './components/ProviderForm.vue'

const { t } = useI18n()
const message = useMessage()

const loading = ref(false)
const keyword = ref('')
const providers = ref<AiProviderView[]>([])

async function load() {
  loading.value = true
  try {
    const { items } = await aiProviderApi.page({
      page: 1,
      pageSize: 100,
      keyword: keyword.value.trim() || undefined,
    })
    providers.value = items
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}
onMounted(load)
// 关键字防抖重拉;回车走 @keyup.enter 立即触发,两者共用同一个 load()。
watchDebounced(keyword, load, { debounce: 400 })

// 新增/编辑抽屉:show/editing 走 v-model + prop,与 config 页 ConfigFormModal 同一协议
// (defineModel('show') + `editing: AiProviderView | null`,null = 新增)。
const formShow = ref(false)
const editing = ref<AiProviderView | null>(null)
function openAdd() {
  editing.value = null
  formShow.value = true
}
function openEdit(p: AiProviderView) {
  editing.value = p
  formShow.value = true
}
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <n-button quaternary :loading="loading" @click="load">
        <template #icon><AppIcon icon="ph:arrow-clockwise" :size="16" /></template>
      </n-button>
      <n-input
        v-model:value="keyword"
        clearable
        style="width: 240px"
        :placeholder="t('aiModel.searchPlaceholder')"
        @keyup.enter="load"
      >
        <template #prefix><AppIcon icon="ph:magnifying-glass" :size="14" /></template>
      </n-input>
      <div class="toolbar-spacer" />
      <n-button v-auth="'POST:/api/v1/sys/ai/provider/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('aiModel.addProvider') }}
      </n-button>
    </div>

    <n-spin :show="loading">
      <n-empty
        v-if="!loading && providers.length === 0"
        :description="t('common.noData')"
        class="empty"
      />
      <n-grid v-else :cols="'1 s:2 l:3'" responsive="screen" :x-gap="16" :y-gap="16">
        <n-gi v-for="p in providers" :key="p.id">
          <ProviderCard :provider="p" @edit="openEdit(p)" @deleted="load" />
        </n-gi>
      </n-grid>
    </n-spin>

    <ProviderForm v-model:show="formShow" :editing="editing" @saved="load" />
  </div>
</template>

<style scoped>
.view {
  display: flex;
  flex-direction: column;
  gap: var(--gap-card, 16px);
}
.toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}
.toolbar-spacer {
  flex: 1;
}
.empty {
  padding: 64px 0;
}
</style>
