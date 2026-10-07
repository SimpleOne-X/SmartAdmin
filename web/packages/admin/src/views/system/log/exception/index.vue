<script setup lang="ts">
// 异常日志 = 只读 SmartTable + 详情抽屉。后端无 exception/{id},分页项已含全字段 → 抽屉直接用行数据。
// 只记未捕获异常(程序缺陷);业务异常不进本表。堆栈保持危险色 <pre>——堆栈非代码,高亮无意义。
import { computed, h, ref } from 'vue'
import { useElementSize } from '@vueuse/core'
import {
  NButton,
  NDrawer,
  NDrawerContent,
  NDescriptions,
  NDescriptionsItem,
  useMessage,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import { useConfirm } from '#/composables/useConfirm'
import { logApi } from '#/api'
import { translateError } from '#/utils/error'
import {
  SEARCH_ACTIONS,
  createFlatFilterSerializer,
  deriveHeaderFilters,
} from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import { fmtDateTime, operatorText } from '#/utils/format'
import type { SysExceptionLog } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()
const tableRef = ref<SmartTableInst<SysExceptionLog>>()

// 详情抽屉的窄档判据:直接读表格根节点的宽度,与库的窄档(容器 < 600,卡片列表)同源同值,
// 不用视口近似。窄档详情从底部抽屉弹出(宽档右侧抽屉)。未量到宽度(0)时按宽档处理。
const tableEl = computed(
  () => (tableRef.value as unknown as { $el?: HTMLElement } | undefined)?.$el,
)
const { width: tableWidth } = useElementSize(tableEl)
const isNarrow = computed(() => tableWidth.value > 0 && tableWidth.value < 600)

// 日期区间:条件构造器里是同一字段的两行(大于等于 + 小于等于),序列化器合并成 [起, 止],
// api 层 splitRange 再拆成 StartTime/EndTime 并给结束日补 23:59:59。
const filterSerializer = createFlatFilterSerializer({ ranges: ['createTime'] })

const columns: SmartTableColumn<SysExceptionLog>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    key: 'exceptionType',
    title: () => t('log.exceptionType'),
    ellipsis: { tooltip: true },
    search: { actions: SEARCH_ACTIONS.fuzzy },
    render: r => h('span', { class: 'mono' }, r.exceptionType),
  },
  {
    key: 'message',
    title: () => t('log.exceptionMessage'),
    ellipsis: { tooltip: true },
    render: r => r.message || '—',
  },
  { key: 'httpMethod', title: () => t('log.method'), width: 90 },
  {
    key: 'path',
    title: () => t('log.path'),
    ellipsis: { tooltip: true },
    search: { actions: SEARCH_ACTIONS.fuzzy },
    render: r => h('span', { class: 'mono muted' }, r.path),
  },
  {
    key: 'operator',
    title: () => t('log.operator'),
    width: 120,
    ellipsis: { tooltip: true },
    render: r => operatorText(r),
  },
  {
    key: 'ip',
    title: () => t('log.ip'),
    render: r => h('span', { class: 'mono muted' }, r.ip || '—'),
  },
  {
    key: 'createTime',
    title: () => t('common.createTime'),
    render: r =>
      h('span', { class: 'num muted', style: 'white-space: nowrap' }, fmtDateTime(r.createTime)),
    search: { type: 'daterange', actions: SEARCH_ACTIONS.dayRange },
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 90,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(
        NButton,
        { size: 'small', quaternary: true, type: 'primary', onClick: () => openDetail(r) },
        () => t('log.detail'),
      ),
  },
]
deriveHeaderFilters(columns)

// ── 详情抽屉(只读,行数据直填)──
const showDetail = ref(false)
const detailRow = ref<SysExceptionLog | null>(null)
function openDetail(r: SysExceptionLog) {
  detailRow.value = r
  showDetail.value = true
}
function clearLogs() {
  confirm({
    type: 'error',
    content: t('log.clearExceptionConfirm'),
    action: () => logApi.exceptionClear(),
    successMsg: t('log.cleared'),
  }).then(ok => {
    if (ok) tableRef.value?.refresh()
  })
}
</script>

<template>
  <SmartTable
    ref="tableRef"
    :columns="columns"
    :fetcher="logApi.exceptionPage"
    :search="{ container: 'table' }"
    :filter-serializer="filterSerializer"
    :toolbar="TABLE_TOOLBAR"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-log-exception"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button
        v-auth="'DELETE:/api/v1/sys/log/exception'"
        type="error"
        secondary
        @click="clearLogs"
      >
        <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
        {{ t('log.clear') }}
      </n-button>
    </template>
  </SmartTable>

  <!-- 详情抽屉:宽档右侧 620,窄档底部抽屉(.st-sheet 由全局样式补圆角与滚动链);无底部按钮 -->
  <n-drawer
    v-model:show="showDetail"
    :placement="isNarrow ? 'bottom' : 'right'"
    :width="isNarrow ? undefined : 'min(620px, 100vw)'"
    :height="isNarrow ? 'auto' : undefined"
    :class="isNarrow ? 'st-sheet' : ''"
    :style="isNarrow ? 'max-height: 85vh' : ''"
  >
    <n-drawer-content :title="t('log.detail')" closable :native-scrollbar="isNarrow">
      <n-descriptions
        v-if="detailRow"
        label-placement="left"
        :column="1"
        bordered
        size="small"
        label-style="width: 96px"
      >
        <n-descriptions-item :label="t('log.exceptionType')">
          <span class="mono break">{{ detailRow.exceptionType }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.method')">
          {{ detailRow.httpMethod }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.path')">
          <span class="mono break">{{ detailRow.path }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.traceId')">
          <span class="mono break">{{ detailRow.traceId || '—' }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.operator')">
          {{ operatorText(detailRow) }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.ip')">
          <span class="mono">{{ detailRow.ip || '—' }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.userAgent')">
          <span class="muted break">{{ detailRow.userAgent || '—' }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('common.createTime')">
          <span class="num">{{ fmtDateTime(detailRow.createTime) }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.exceptionMessage')">
          <pre class="exception">{{ detailRow.message || '—' }}</pre>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.stackTrace')">
          <pre class="exception">{{ detailRow.stackTrace || '—' }}</pre>
        </n-descriptions-item>
      </n-descriptions>
    </n-drawer-content>
  </n-drawer>
</template>

<style scoped>
.break {
  word-break: break-all;
}
.exception {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-all;
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 13px;
  line-height: 1.5;
  color: var(--err, var(--color-danger));
}
</style>
