<script setup lang="ts">
// 操作日志 = 只读 SmartTable + 详情抽屉。后端无 op/{id},分页项已含全字段 → 抽屉直接用行数据。
// 搜索区要能答审计的三个问题:谁(操作人)、什么时候(时间范围)、干了什么(操作名/路径/成败)。
// paramJson 走 CodeBlock(json 高亮 + 复制;美化是 parse→stringify,失败原样);异常堆栈保持危险色 <pre>——堆栈非代码,高亮无意义。
// 导出:ExportColumnsModal 选列 + 当前筛选条件。
import { computed, h, ref } from 'vue'
import { useElementSize } from '@vueuse/core'
import {
  NButton,
  NTag,
  NDrawer,
  NDrawerContent,
  NDescriptions,
  NDescriptionsItem,
  useMessage,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import CodeBlock from '#/components/CodeBlock/index.vue'
import ExportColumnsModal from '#/components/ExportColumnsModal/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { logApi, userApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import {
  SEARCH_ACTIONS,
  createFlatFilterSerializer,
  flatSearchOf,
  loadWithinServerMax,
  deriveHeaderFilters,
} from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import { fmtDateTime, operatorText } from '#/utils/format'
import { triggerBlobDownload } from '#/utils/download'
import type { ExportColumnDef, SysOpLog } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<SysOpLog>>()

const exportShow = ref(false)
const exporting = ref(false)

// 详情抽屉的窄档判据:直接读表格根节点的宽度,与库的窄档(容器 < 600,卡片列表)同源同值,
// 不用视口近似。窄档详情从底部抽屉弹出(宽档右侧抽屉)。未量到宽度(0)时按宽档处理。
const tableEl = computed(
  () => (tableRef.value as unknown as { $el?: HTMLElement } | undefined)?.$el,
)
const { width: tableWidth } = useElementSize(tableEl)
const isNarrow = computed(() => tableWidth.value > 0 && tableWidth.value < 600)

// 导出收进表格内置的「更多」菜单;没有导出权限时 more 为空数组,按钮不出现。
const toolbarMore = computed(() =>
  authStore.hasPerm('GET:/api/v1/sys/log/op/export')
    ? [{ label: t('export.button'), key: 'export' }]
    : [],
)
// 引用稳定的 toolbar 配置:不要把 `{ ...TABLE_TOOLBAR, more }` 直接写进模板,那样每次重渲染都会新建一个对象
const toolbar = computed(() => ({ ...TABLE_TOOLBAR, more: toolbarMore.value }))
function onMoreSelect(key: string | number) {
  if (key === 'export') exportShow.value = true
}

/** 与后端 OpLogExportProfile.Columns 对齐。 */
const opExportColumns: ExportColumnDef[] = [
  { key: 'Title', title: t('log.opName') },
  { key: 'HttpMethod', title: t('log.method') },
  { key: 'Path', title: t('log.path') },
  { key: 'ResultCode', title: t('log.resultCode') },
  { key: 'Success', title: t('log.success') },
  { key: 'OperatorName', title: t('log.operator') },
  { key: 'Ip', title: t('log.ip') },
  { key: 'ElapsedMs', title: t('log.elapsed') },
  { key: 'CreateTime', title: t('common.createTime') },
  { key: 'ExceptionMessage', title: t('log.exceptionMessage'), defaultSelected: false },
]

async function onExport(keys: string[]) {
  // 搜索值在过滤态里,flatSearchOf 用同一个序列化器还原成与列表请求一致的扁平键
  const p = flatSearchOf(tableRef.value, filterSerializer)
  exporting.value = true
  try {
    const blob = await logApi.opExport({
      title: p.title || undefined,
      success: p.success,
      operatorId: p.operatorId != null ? Number(p.operatorId) : undefined,
      path: p.path || undefined,
      createTime: p.createTime ?? null,
      columns: keys.join(','),
    })
    triggerBlobDownload(blob, t('logOp.fileName'))
    exportShow.value = false
    message.success(t('export.done'))
  } catch (e) {
    message.error(translateError(e))
  } finally {
    exporting.value = false
  }
}

// 日期区间:条件构造器里是同一字段的两行(大于等于 + 小于等于),序列化器合并成 [起, 止],
// api 层 splitRange 再拆成 StartTime/EndTime 并给结束日补 23:59:59。
const filterSerializer = createFlatFilterSerializer({ ranges: ['createTime'] })

// 操作人下拉的选项源:取前 1000 个用户。
// 用选项列而不是 UserSelect(远程搜索):条件构造器放不下自定义控件。
// 当前限制:用户超过 1000 时,排在后面的人在这里选不到。
const operatorOptions = ref<{ label: string; value: number }[]>([])
async function loadOperatorOptions() {
  try {
    // 应用把 MaxPageSize 调到 1000 以下时,loadWithinServerMax 按后端回的上限重取一次
    const { items } = await loadWithinServerMax(userApi.page, 1000)
    operatorOptions.value = items.map(u => ({ label: `${u.name}(${u.account})`, value: u.id }))
  } catch {
    // 静默:人员下拉是筛选辅助,拉取失败不打断列表
  }
}
void loadOperatorOptions()

const columns: SmartTableColumn<SysOpLog>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { key: 'title', title: () => t('log.opName'), search: { actions: SEARCH_ACTIONS.fuzzy } },
  { key: 'httpMethod', title: () => t('log.method'), width: 90 },
  {
    key: 'path',
    title: () => t('log.path'),
    ellipsis: { tooltip: true },
    search: { actions: SEARCH_ACTIONS.fuzzy },
    render: r => h('span', { class: 'mono muted' }, r.path),
  },
  {
    key: 'success',
    title: () => t('log.result'),
    tag: true,
    search: { actions: SEARCH_ACTIONS.exact },
    options: [
      { label: () => t('log.success'), value: true, tagType: 'success' },
      { label: () => t('log.failed'), value: false, tagType: 'error' },
    ],
  },
  {
    key: 'resultCode',
    title: () => t('log.resultCode'),
    width: 100,
    render: r => h('span', { class: 'num' }, String(r.resultCode)),
  },
  {
    key: 'operator',
    title: () => t('log.operator'),
    width: 120,
    ellipsis: { tooltip: true },
    render: r => operatorText(r),
  },
  // 操作人搜索项:日志只存 OperatorId(姓名是读取时回填的),按人筛必须是精确 Id(后端 OperatorId ==)。
  // 只作搜索项,不进表格也不进列设置;展示列是上面的 operator。
  {
    key: 'operatorId',
    title: () => t('log.operator'),
    hideInTable: true,
    options: operatorOptions,
    search: { actions: SEARCH_ACTIONS.exact, props: { clearable: true, filterable: true } },
  },
  {
    key: 'elapsedMs',
    title: () => t('log.elapsed'),
    width: 100,
    render: r => h('span', { class: 'num' }, `${r.elapsedMs} ms`),
  },
  {
    key: 'ip',
    title: () => t('log.ip'),
    render: r => h('span', { class: 'mono muted' }, r.ip || '—'),
  },
  // 时间范围是审计最常用的一刀("上周三下午谁删了那批数据");daterange 回传 ['YYYY-MM-DD','YYYY-MM-DD'],
  // api 层拆成 StartTime/EndTime 并把结束日补到 23:59:59。
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
const detailRow = ref<SysOpLog | null>(null)
function openDetail(r: SysOpLog) {
  detailRow.value = r
  showDetail.value = true
}
/** paramJson 美化:parse→stringify(2);非法 JSON 原样返回。空值占位由模板判空。 */
function prettyParam(json: string) {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}

function clearLogs() {
  confirm({
    type: 'error',
    content: t('log.clearOpConfirm'),
    action: () => logApi.opClear(),
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
    :fetcher="logApi.opPage"
    :search="{ container: 'table' }"
    :filter-serializer="filterSerializer"
    :toolbar="toolbar"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-log-op"
    @more-select="onMoreSelect"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button v-auth="'DELETE:/api/v1/sys/log/op'" type="error" secondary @click="clearLogs">
        <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
        {{ t('log.clear') }}
      </n-button>
    </template>
  </SmartTable>

  <ExportColumnsModal
    v-model:show="exportShow"
    :columns="opExportColumns"
    :loading="exporting"
    @confirm="onExport"
  />

  <!-- 详情抽屉:宽档右侧 560,窄档底部抽屉(.st-sheet 由全局样式补圆角与滚动链);没有响应体,只有脱敏后的入参 -->
  <n-drawer
    v-model:show="showDetail"
    :placement="isNarrow ? 'bottom' : 'right'"
    :width="isNarrow ? undefined : 'min(560px, 100vw)'"
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
        <n-descriptions-item :label="t('log.opName')">
          <span class="break">{{ detailRow.title }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.method')">
          {{ detailRow.httpMethod }}
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.path')">
          <span class="mono break">{{ detailRow.path }}</span>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.result')">
          <n-tag :type="detailRow.success ? 'success' : 'error'" size="small" :bordered="false">
            {{ detailRow.success ? t('log.success') : t('log.failed') }}
          </n-tag>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.resultCode')">
          <span class="num">{{ detailRow.resultCode }}</span>
        </n-descriptions-item>
        <n-descriptions-item v-if="detailRow.exceptionMessage" :label="t('log.exception')">
          <pre class="exception">{{ detailRow.exceptionMessage }}</pre>
        </n-descriptions-item>
        <n-descriptions-item :label="t('log.elapsed')">
          <span class="num">{{ detailRow.elapsedMs }} ms</span>
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
        <n-descriptions-item :label="t('log.param')">
          <CodeBlock v-if="detailRow.paramJson" :code="prettyParam(detailRow.paramJson)" />
          <span v-else>—</span>
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
