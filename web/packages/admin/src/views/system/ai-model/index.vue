<script setup lang="ts">
// AI 模型管理:厂商是卡片网格,不是 SmartTable —— 厂商数量级很小(十几个封顶),卡片能把
// 徽标/协议/Base URL/模型数/启停/测试连接一次性摆开,比表格行更直观。
// 列表本身一次拉大页(pageSize 100)铺成网格,不做真分页;关键字过滤(厂商 code/name)交给
// 后端 keyword 参数,输入防抖 400ms 后自动重拉,回车立即拉一次。
// 交互都收进 ProviderCard(启停/编辑入口/删除/测试连接)与 ProviderForm(新增/编辑抽屉),
// 本页只管拉列表、开表单、增删改后 reload。
// 版式:一行工具条卡片(刷新 + 关键字 + 新增)+ 卡片网格(列数按内容区宽度分档:窄 1 / 中 2 / 宽 3,
// 用容器查询而不是视口断点,侧栏展开收起时跟着变)。
import { onMounted, ref } from 'vue'
import { NButton, NCard, NEmpty, NInput, NSpin, useMessage } from 'naive-ui'
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
    <n-card class="a-card" size="small" content-style="padding: 10px 12px">
      <div class="toolbar">
        <n-button
          quaternary
          circle
          :aria-label="t('table.refresh')"
          :loading="loading"
          @click="load"
        >
          <template #icon><AppIcon icon="ph:arrow-clockwise" :size="16" /></template>
        </n-button>
        <n-input
          v-model:value="keyword"
          clearable
          class="toolbar-search"
          :placeholder="t('aiModel.searchPlaceholder')"
          @keyup.enter="load"
        >
          <template #prefix>
            <AppIcon icon="ph:magnifying-glass" :size="15" class="faint" />
          </template>
        </n-input>
        <div class="toolbar-spacer" />
        <n-button v-auth="'POST:/api/v1/sys/ai/provider/add'" type="primary" @click="openAdd">
          <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
          {{ t('aiModel.addProvider') }}
        </n-button>
      </div>
    </n-card>

    <n-spin :show="loading">
      <n-empty
        v-if="!loading && providers.length === 0"
        :description="t('common.noData')"
        class="empty"
      />
      <div v-else class="grid">
        <ProviderCard
          v-for="p in providers"
          :key="p.id"
          :provider="p"
          @edit="openEdit(p)"
          @deleted="load"
        />
      </div>
    </n-spin>

    <ProviderForm v-model:show="formShow" :editing="editing" @saved="load" />
  </div>
</template>

<style scoped>
/* 容器查询的容器:内容区宽度分档(narrow < 604 / wide ≥ 1400),不看视口 */
.view {
  display: flex;
  flex-direction: column;
  gap: var(--gap-card, 8px);
  container-type: inline-size;
}
.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.toolbar-search {
  flex: 0 1 240px;
  min-width: 0;
}
.toolbar-spacer {
  flex: 1;
}
.empty {
  padding: 64px 0;
}
.grid {
  display: grid;
  gap: 8px;
  grid-template-columns: minmax(0, 1fr);
}
@container (min-width: 604px) {
  .grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
@container (min-width: 1400px) {
  .grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}
/* 窄档:搜索框占满一行剩余宽度,新增按钮靠右 */
@container (max-width: 603px) {
  .toolbar-search {
    flex: 1 1 0;
  }
  .toolbar-spacer {
    display: none;
  }
}
</style>
