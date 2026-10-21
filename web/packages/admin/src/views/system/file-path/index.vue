<script setup lang="ts">
// 文件路径配置页:「文件路径」「文件服务器」两个页签,各一张列表加一个编辑抽屉。
// 页签按各自列表的查询权限显示,默认打开第一个看得见的。
// 环境信息(白名单根目录、平台能不能用账号)挂载时取一次,白名单为空时顶部提示,服务器页签用它给根路径候选。
import { computed, onMounted, ref } from 'vue'
import { NTabPane, NTabs, useMessage } from 'naive-ui'
import { useMediaQuery } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { filePathApi, type FilePathEnvironment } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import PathTab from './components/PathTab.vue'
import ServerTab from './components/ServerTab.vue'

const { t } = useI18n()
const message = useMessage()
const authStore = useAuthStore()

const canPath = computed(() => authStore.hasPerm('GET:/api/v1/sys/file-path/page'))
const canServer = computed(() => authStore.hasPerm('GET:/api/v1/sys/file-server/page'))
const activeTab = ref<'path' | 'server'>(canPath.value || !canServer.value ? 'path' : 'server')
// 窄屏页签放大到触控尺寸
const narrow = useMediaQuery('(max-width: 604px)')

// 没有查询权限的账号拿不到环境信息:按「没有候选、能用账号」处理,不弹错
const environment = ref<FilePathEnvironment | null>(null)
onMounted(async () => {
  if (!authStore.hasPerm('GET:/api/v1/sys/file-path/environment')) return
  try {
    environment.value = await filePathApi.environment()
  } catch (e) {
    message.error(translateError(e))
  }
})
</script>

<template>
  <div class="view fill-page">
    <!-- 形状 4:.fill-tabs 把 n-tabs → 面板容器 → .n-tab-pane 一路拉成 flex 列,
         面板里的 SmartTable 就接上高度链了(见 styles/layout.css ⑧)。 -->
    <n-tabs
      v-model:value="activeTab"
      type="segment"
      animated
      class="fill-tabs file-path-seg"
      :size="narrow ? 'large' : 'medium'"
      :pane-style="{ padding: 0 }"
    >
      <n-tab-pane v-if="canPath" name="path" :tab="t('filePath.tabs.path')">
        <PathTab />
      </n-tab-pane>
      <n-tab-pane v-if="canServer" name="server" :tab="t('filePath.tabs.server')">
        <ServerTab :environment="environment" />
      </n-tab-pane>
    </n-tabs>
  </div>
</template>

<style scoped>
/* display / flex-direction / 整屏高度链都在 styles/layout.css 的 .fill-page,这里只留卡片间距。 */
.view {
  gap: var(--gap-card);
}

/* 分段控件:naive 的 segment 轨道默认 width:100% 且各格等分,两格会被撑成一条长带。
   这里让轨道按内容收窄、靠左。 */
.file-path-seg :deep(.n-tabs-nav) {
  align-self: flex-start;
  max-width: 100%;
  margin-bottom: var(--gap-card);
}
.file-path-seg :deep(.n-tabs-rail) {
  width: auto;
  overflow-x: auto;
  scrollbar-width: none;
}
.file-path-seg :deep(.n-tabs-rail::-webkit-scrollbar) {
  display: none;
}
.file-path-seg :deep(.n-tabs-tab-wrapper) {
  flex: 0 0 auto;
}
.file-path-seg :deep(.n-tabs-tab) {
  padding: 4px 14px;
  white-space: nowrap;
}
</style>
