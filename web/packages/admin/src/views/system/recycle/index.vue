<script setup lang="ts">
import { h, ref } from 'vue'
import { NButton, NSpace, NTabs, NTabPane, NPopconfirm, useMessage } from 'naive-ui'
import { useMediaQuery } from '@vueuse/core'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import { useI18n } from 'vue-i18n'
import { useConfirm } from '#/composables/useConfirm'
import { useAuthStore } from '#/stores/auth'
import { recycleApi, type RecycleBinItem } from '#/api'
import { translateError } from '#/utils/error'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import {
  createClientFilterFetcher,
  passthroughFilterSerializer,
  deriveHeaderFilters,
} from '#/utils/tableFilter'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()

const types = [
  'user',
  'role',
  'org',
  'position',
  'module',
  'config',
  'dict',
  'menu',
  'job',
] as const
type RecycleType = (typeof types)[number]
const activeTab = ref<RecycleType>('user')
// 窄屏页签放大到触控尺寸
const narrow = useMediaQuery('(max-width: 604px)')
// 回收站端点没有过滤能力,搜索由前端求值;每个标签的接口不同,各配一个取数器。
// fields 要与下面声明了 search 的列 key 一致
const clientFetchers = Object.fromEntries(
  types.map(type => [type, createClientFilterFetcher(recycleApi.page(type), ['name', 'code'])]),
) as Record<RecycleType, ReturnType<typeof createClientFilterFetcher<RecycleBinItem>>>
const tableRefs = ref<Record<string, SmartTableInst<RecycleBinItem>>>({})

function setTableRef(type: string) {
  // 函数 ref 的参数类型是宽泛的组件实例,收窄为 SmartTableInst 后存入映射
  return (el: unknown) => {
    if (el) tableRefs.value[type] = el as SmartTableInst<RecycleBinItem>
  }
}

function refreshTab(type: string) {
  tableRefs.value[type]?.refresh()
}

const columns: SmartTableColumn<RecycleBinItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { key: 'name', ellipsis: { tooltip: true }, title: () => t('recycle.name'), search: {} },
  {
    key: 'code',
    width: 180,
    ellipsis: { tooltip: true },
    title: () => t('recycle.code'),
    search: {},
    // 编码可能为空(如岗位):空值画灰色的「—」
    render: r =>
      r.code ? h('span', { class: 'mono muted' }, r.code) : h('span', { class: 'faint' }, '—'),
  },
  { key: 'deletedAt', title: () => t('recycle.deletedAt'), format: 'datetime', width: 180 },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 180,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 2, justify: 'end', wrapItem: false }, () => [
        authStore.hasPerm('POST:/api/v1/sys/recycle/{type}/{id}/restore')
          ? h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => recycleApi.restore(activeTab.value, r.id), t('recycle.restored')).then(
                    ok => {
                      if (ok) refreshTab(activeTab.value)
                    },
                  ),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'primary' }, () =>
                    t('recycle.restore'),
                  ),
                default: () => t('recycle.restoreConfirm', { name: r.name }),
              },
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/recycle/{type}/{id}')
          ? h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => recycleApi.purge(activeTab.value, r.id), t('recycle.purged')).then(
                    ok => {
                      if (ok) refreshTab(activeTab.value)
                    },
                  ),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('recycle.purge'),
                  ),
                default: () => t('recycle.purgeConfirm', { name: r.name }),
              },
            )
          : null,
      ]),
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <div class="view fill-page">
    <!-- 形状 4:.fill-tabs 把 n-tabs → 面板容器 → .n-tab-pane 一路拉成 flex 列,
         面板里的 SmartTable 就接上高度链了(见 styles/layout.css ⑧)。 -->
    <!-- 9 个资源类型用 macOS 分段控件(轨道 + 滑块)切换:控件按内容收窄、靠左,不撑满整行;
         窄屏放不下时轨道自己横向滑动。轨道 / 滑块色走主题的 --seg-track / --seg-thumb。 -->
    <n-tabs
      v-model:value="activeTab"
      type="segment"
      animated
      class="fill-tabs recycle-seg"
      :size="narrow ? 'large' : 'medium'"
      :pane-style="{ padding: 0 }"
    >
      <n-tab-pane v-for="type in types" :key="type" :name="type" :tab="t(`recycle.tabs.${type}`)">
        <SmartTable
          :ref="setTableRef(type)"
          :columns="columns"
          :fetcher="clientFetchers[type]"
          :toolbar="TABLE_TOOLBAR"
          :search="{ container: 'table' }"
          :filter-serializer="passthroughFilterSerializer"
          fill-height
          :min-row-height="TABLE_MIN_ROW_HEIGHT"
          card-on-narrow
          :storage-key="`sys-recycle-${type}`"
          @error="(e: unknown) => message.error(translateError(e))"
        >
          <template #pagination-prefix="{ itemCount }">
            <TableTotal :count="itemCount" />
          </template>
        </SmartTable>
      </n-tab-pane>
    </n-tabs>
  </div>
</template>

<style scoped>
/* display / flex-direction / 整屏高度链都在 styles/layout.css 的 .fill-page,这里只留卡片间距。 */
.view {
  gap: var(--gap-card);
}

/* 分段控件:naive 的 segment 轨道默认 width:100% 且各格等分,9 格会被撑成一条长带。
   这里让轨道按内容收窄、靠左;格子按文字自适应宽度(滑块位置由 naive 按实际格宽量,不受影响)。 */
.recycle-seg :deep(.n-tabs-nav) {
  align-self: flex-start;
  max-width: 100%;
  margin-bottom: var(--gap-card);
}
.recycle-seg :deep(.n-tabs-rail) {
  width: auto;
  overflow-x: auto;
  scrollbar-width: none;
}
.recycle-seg :deep(.n-tabs-rail::-webkit-scrollbar) {
  display: none;
}
.recycle-seg :deep(.n-tabs-tab-wrapper) {
  flex: 0 0 auto;
}
.recycle-seg :deep(.n-tabs-tab) {
  padding: 4px 14px;
  white-space: nowrap;
}

/* 辅助字样(表格单元格由库渲染,要 :deep) */
:deep(.mono) {
  font-family: var(--font-mono);
  font-size: 13.5px;
  letter-spacing: 0;
}
:deep(.muted) {
  color: var(--text-2);
}
:deep(.faint) {
  color: var(--text-3);
}
</style>
