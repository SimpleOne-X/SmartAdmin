<script setup lang="ts">
// AI 用量统计 = 顶部统一筛选(日期/厂商/模型/场景)+ 四格指标 + 分组维度 Tab/表 + 趋势图 + 明细表。
// 筛选只在 filterParams 这一处收口,四个区块都从它派生,避免各自拼参数导致竞态或口径不一致
// (SmartTable 走 :params 自动联动,summary/trend 走 watch(filterParams, ...) 手动重拉 + 请求号防竞态)。
// 本页只有一颗查询权限(GET:/api/v1/sys/ai/usage/summary),菜单可见性已由后端权限树控管,不需要 v-auth。
// 版式:块顺序为 筛选 → 指标 → 趋势 → 分组 Tab → 明细;块间距 8px、指标格带图标底块、四格列数按内容区宽度分档(容器查询,窄 1 / 中 2 / 宽 4),
// 分组表与明细表都是 SmartTable(分组表走静态数据模式),窄档都用内置的 card-on-narrow 卡片列表。图表用 ECharts(LineChart)。
import { computed, h, onMounted, ref, watch } from 'vue'
import {
  NCard,
  NSkeleton,
  NNumberAnimation,
  NDatePicker,
  NSelect,
  NInput,
  NButton,
  NTabs,
  NTabPane,
  NProgress,
  NTag,
  useMessage,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import LineChart from '#/components/Chart/LineChart.vue'
import { aiUsageApi, aiProviderApi } from '#/api'
import {
  AI_USAGE_GROUP_BY,
  AI_USAGE_SOURCE,
  type AiUsageGroupBy,
  type AiUsageGroupRow,
  type AiUsageSummary,
  type AiUsageTrendPoint,
  type SysAiUsageLog,
} from '#/types/api'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'

const { t } = useI18n()
const message = useMessage()

// ── 日期范围:三个快捷按钮直接设值(shortcuts prop 类型较绕,按钮法更直白),默认近 7 天 ──
function fmtDate(d: Date): string {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}
function daysAgo(n: number): Date {
  const d = new Date()
  d.setDate(d.getDate() - n)
  return d
}
function quickRangeValue(days: number): [string, string] {
  return [fmtDate(daysAgo(days - 1)), fmtDate(daysAgo(0))]
}

const dateRange = ref<[string, string]>(quickRangeValue(7))
const activeQuickRange = ref<number | null>(7)

function setQuickRange(days: number) {
  dateRange.value = quickRangeValue(days)
  activeQuickRange.value = days
}
// 不加 clearable:没有清空按钮,formatted-value 就不会回传 null,ref 全程非空,少一层判空。
function onDateRangeUpdate(v: [string, string] | null) {
  if (!v) return
  dateRange.value = v
  activeQuickRange.value = null // 手动挑日期后取消快捷按钮高亮
}

// ── 厂商 / 模型 / 场景筛选 ──
// 厂商:只读消费 aiProviderApi 拉一页选项(不改厂商模块任何文件)。模型/场景后端无枚举接口,
// 模型做成 filterable+tag 的自由输入 select(参考 menu/MenuFormModal 的 component 字段写法),
// 场景干脆用文本框模糊过滤——两者都是自由字符串,不是字典。
const providerCode = ref<string | null>(null)
const model = ref<string | null>(null)
const scene = ref<string | null>(null)

const providerOptions = ref<{ label: string; value: string }[]>([])
onMounted(async () => {
  try {
    const { items } = await aiProviderApi.page({ page: 1, pageSize: 100 })
    providerOptions.value = items.map(p => ({ label: p.name, value: p.code }))
  } catch {
    // 静默:厂商下拉是筛选辅助,拉取失败不打断本页其余区块
  }
})

/**
 * 统一收口的筛选参数——四格指标 / 分组表 / 趋势图 / 明细表都从这一份派生,不各自拼 from/to,
 * 避免四处逻辑不一致或漏改。结束日补到 23:59:59(同 job-log/操作日志页 daterange 的规矩),
 * 否则"筛到今天"会把今天已发生的调用漏掉(纯日期串等价于当天 00:00:00)。
 */
const filterParams = computed(() => {
  const p: {
    from: string
    to: string
    providerCode?: string
    model?: string
    scene?: string
  } = {
    from: `${dateRange.value[0]}T00:00:00`,
    to: `${dateRange.value[1]}T23:59:59`,
  }
  if (providerCode.value) p.providerCode = providerCode.value
  if (model.value) p.model = model.value
  if (scene.value) p.scene = scene.value
  return p
})

// ── 分组维度 Tab ──
const groupTabs: { value: AiUsageGroupBy; labelKey: 'provider' | 'model' | 'scene' | 'user' }[] = [
  { value: AI_USAGE_GROUP_BY.Provider, labelKey: 'provider' },
  { value: AI_USAGE_GROUP_BY.Model, labelKey: 'model' },
  { value: AI_USAGE_GROUP_BY.Scene, labelKey: 'scene' },
  { value: AI_USAGE_GROUP_BY.User, labelKey: 'user' },
]
const activeGroupBy = ref<AiUsageGroupBy>(AI_USAGE_GROUP_BY.Provider)

// ── summary(四格指标 totals + 当前维度 groups,同一次请求共用)──
const summary = ref<AiUsageSummary | null>(null)
const summaryLoading = ref(true)
let summaryReq = 0
async function fetchSummary() {
  const reqId = ++summaryReq
  summaryLoading.value = true
  try {
    const res = await aiUsageApi.summary({ ...filterParams.value, groupBy: activeGroupBy.value })
    if (reqId !== summaryReq) return // 旧请求的响应,丢弃(防竞态:快速切筛选/切 Tab 时先发的可能后回)
    summary.value = res
  } catch (e) {
    if (reqId !== summaryReq) return
    message.error(translateError(e))
  } finally {
    if (reqId === summaryReq) summaryLoading.value = false
  }
}
watch([filterParams, activeGroupBy], fetchSummary, { immediate: true })

const totals = computed(() => summary.value?.totals ?? null)
const groups = computed(() => summary.value?.groups ?? [])

/**
 * 平均耗时卡片的算法选择:后端 AiUsageTotals 没有整体的 avgLatencyMs 字段,只有当前 groupBy
 * 维度下每组的 avgLatencyMs。这里按各组 callCount 加权平均还原总体平均耗时——分组维度对区间内
 * 全部调用做了完整划分(每次调用恰好落在一个分组里,无重叠无遗漏),所以
 * sum(avgLatencyMs_i * callCount_i) / sum(callCount_i) 就精确等于对全部原始调用耗时取平均,
 * 不是近似值(除各组均值本身的取整/浮点误差外)。选它而不是"失败率"是因为卡片标题已经明确是
 * aiUsage.metric.avgLatency(平均耗时),不宜文不对题;比另开一个后端聚合字段/接口更省事,
 * 也顺带让这张卡片跟着 groupBy 切换、筛选变化实时更新,不需要额外请求。
 */
const avgLatencyMs = computed(() => {
  const totalCalls = groups.value.reduce((sum, g) => sum + g.callCount, 0)
  if (!totalCalls) return null
  const weighted = groups.value.reduce((sum, g) => sum + g.avgLatencyMs * g.callCount, 0)
  return weighted / totalCalls
})

const metrics = computed(() => [
  {
    key: 'totalTokens',
    icon: 'ph:coins',
    value: totals.value?.totalTokens ?? 0,
    empty: !totals.value,
  },
  {
    key: 'totalCalls',
    icon: 'ph:lightning',
    value: totals.value?.callCount ?? 0,
    empty: !totals.value,
  },
  {
    key: 'failureCount',
    icon: 'ph:warning-circle',
    value: totals.value?.failureCount ?? 0,
    empty: !totals.value,
  },
  {
    key: 'avgLatency',
    icon: 'ph:timer',
    value: avgLatencyMs.value != null ? Math.round(avgLatencyMs.value) : 0,
    suffix: 'ms',
    empty: avgLatencyMs.value == null,
  },
])

// ── 分组表(四个 Tab 共用同一张表,只是绑定的 groups/列标题随 activeGroupBy 变)──
const groupDimensionLabel = computed(() => {
  const tab = groupTabs.find(g => g.value === activeGroupBy.value)
  return t(`aiUsage.groupBy.${tab?.labelKey ?? 'provider'}`)
})

const numCell = (n: number) => h('span', { class: 'num' }, n.toLocaleString())

const groupColumns = computed<SmartTableColumn<AiUsageGroupRow>[]>(() => [
  {
    title: () => groupDimensionLabel.value,
    key: 'label',
    minWidth: 160,
    search: {}, // 静态数据:条件由 SmartTable 前端求值
    // 厂商 / 模型 / 场景是编码类文本,等宽;用户维度是姓名,保持正文
    render: r =>
      h(
        'span',
        { class: activeGroupBy.value === AI_USAGE_GROUP_BY.User ? '' : 'mono' },
        r.label ?? r.key,
      ),
  },
  {
    title: () => t('aiUsage.trend.inputTokens'),
    key: 'inputTokens',
    width: 120,
    align: 'right',
    render: r => numCell(r.inputTokens),
  },
  {
    title: () => t('aiUsage.trend.outputTokens'),
    key: 'outputTokens',
    width: 120,
    align: 'right',
    render: r => numCell(r.outputTokens),
  },
  {
    title: () => t('aiUsage.metric.totalCalls'),
    key: 'callCount',
    width: 110,
    align: 'right',
    render: r => numCell(r.callCount),
  },
  {
    title: () => t('aiUsage.table.latency'),
    key: 'avgLatencyMs',
    width: 100,
    align: 'right',
    render: r => h('span', { class: 'num' }, `${Math.round(r.avgLatencyMs)} ms`),
  },
  {
    title: () => t('aiUsage.share'),
    key: 'sharePercent',
    minWidth: 200,
    render: r =>
      h('div', { class: 'share-cell' }, [
        h('div', { class: 'share-bar' }, [
          h(NProgress, {
            type: 'line',
            percentage: Math.round(r.sharePercent),
            showIndicator: false,
            height: 6,
          }),
        ]),
        h('span', { class: 'share-text num muted' }, `${r.sharePercent.toFixed(1)}%`),
      ]),
  },
])

// ── 趋势图 ──
const trendPoints = ref<AiUsageTrendPoint[]>([])
const trendLoading = ref(true)
let trendReq = 0
async function fetchTrend() {
  const reqId = ++trendReq
  trendLoading.value = true
  try {
    const res = await aiUsageApi.trend(filterParams.value)
    if (reqId !== trendReq) return
    trendPoints.value = res
  } catch (e) {
    if (reqId !== trendReq) return
    message.error(translateError(e))
  } finally {
    if (reqId === trendReq) trendLoading.value = false
  }
}
watch(filterParams, fetchTrend, { immediate: true })

const trendCategories = computed(() => trendPoints.value.map(p => p.date))
const trendSeries = computed(() => [
  { name: t('aiUsage.trend.inputTokens'), data: trendPoints.value.map(p => p.inputTokens) },
  { name: t('aiUsage.trend.outputTokens'), data: trendPoints.value.map(p => p.outputTokens) },
])

// ── 明细表(SmartTable;日期/厂商/模型/场景已在页面顶部统一做并经 :params 联动,
// 内置条件搜索只放顶部没有的「结果」,避免同一个条件两处入口)──
const detailColumns: SmartTableColumn<SysAiUsageLog>[] = [
  {
    key: 'createTime',
    title: () => t('aiUsage.table.createTime'),
    width: 170,
    render: r =>
      h('span', { class: 'num muted', style: 'white-space: nowrap' }, fmtDateTime(r.createTime)),
  },
  {
    key: 'providerCode',
    title: () => t('aiUsage.table.provider'),
    width: 120,
    render: r => h('span', { class: 'mono' }, r.providerCode),
  },
  {
    key: 'model',
    title: () => t('aiUsage.table.model'),
    width: 140,
    ellipsis: { tooltip: true },
    render: r => h('span', { class: 'mono' }, r.model),
  },
  {
    key: 'scene',
    title: () => t('aiUsage.table.scene'),
    width: 120,
    render: r => h('span', { class: 'mono muted' }, r.scene || '—'),
  },
  {
    key: 'userId',
    title: () => t('aiUsage.table.user'),
    width: 90,
    render: r => h('span', { class: 'num' }, r.userId != null ? String(r.userId) : '—'),
  },
  {
    key: 'inputTokens',
    title: () => t('aiUsage.table.inputTokens'),
    width: 110,
    align: 'right',
    render: r => numCell(r.inputTokens),
  },
  {
    key: 'outputTokens',
    title: () => t('aiUsage.table.outputTokens'),
    width: 110,
    align: 'right',
    render: r => numCell(r.outputTokens),
  },
  {
    key: 'totalTokens',
    title: () => t('aiUsage.table.totalTokens'),
    width: 110,
    align: 'right',
    render: r => numCell(r.totalTokens),
  },
  {
    key: 'latencyMs',
    title: () => t('aiUsage.table.latency'),
    width: 100,
    align: 'right',
    render: r => h('span', { class: 'num' }, `${r.latencyMs.toLocaleString()} ms`),
  },
  {
    key: 'success',
    title: () => t('aiUsage.table.status'),
    width: 90,
    // options 只给搜索控件当下拉选项(单元格仍走下面的 render);后端 Success == 精确匹配,所以只开「等于」
    options: [
      { label: () => t('aiUsage.table.success'), value: true },
      { label: () => t('aiUsage.table.failed'), value: false },
    ],
    search: { actions: SEARCH_ACTIONS.exact },
    // 失败行用原生 title 兜底展示 errorMessage,不必为一个只读明细表再引入 NTooltip
    render: r =>
      h('span', { title: !r.success && r.errorMessage ? r.errorMessage : undefined }, [
        h(NTag, { size: 'small', bordered: false, type: r.success ? 'success' : 'error' }, () =>
          r.success ? t('aiUsage.table.success') : t('aiUsage.table.failed'),
        ),
      ]),
  },
  {
    key: 'streamed',
    title: () => t('aiUsage.table.streamed'),
    width: 80,
    render: r =>
      h(NTag, { size: 'small', bordered: false }, () =>
        r.streamed ? t('common.yes') : t('common.no'),
      ),
  },
  {
    key: 'usageSource',
    title: () => t('aiUsage.table.usageSource'),
    width: 110,
    render: r =>
      r.usageSource === AI_USAGE_SOURCE.Reported
        ? t('aiUsage.table.usageSourceReported')
        : t('aiUsage.table.usageSourceMissing'),
  },
]
deriveHeaderFilters(detailColumns)
</script>

<template>
  <div class="view">
    <!-- 筛选区:日期范围 + 快捷按钮 + 厂商/模型/场景,变化后经 filterParams 联动下面四块 -->
    <n-card class="a-card" size="small" :bordered="true" content-style="padding: 12px 14px">
      <div class="filter-bar">
        <div class="filter-item filter-item--range">
          <span class="filter-label faint">{{ t('aiUsage.filter.dateRange') }}</span>
          <div class="date-filter-row">
            <n-date-picker
              :formatted-value="dateRange"
              type="daterange"
              value-format="yyyy-MM-dd"
              class="date-picker"
              @update:formatted-value="onDateRangeUpdate"
            />
            <div class="quick-range">
              <n-button
                size="small"
                quaternary
                :type="activeQuickRange === 1 ? 'primary' : 'default'"
                @click="setQuickRange(1)"
              >
                {{ t('aiUsage.filter.today') }}
              </n-button>
              <n-button
                size="small"
                quaternary
                :type="activeQuickRange === 7 ? 'primary' : 'default'"
                @click="setQuickRange(7)"
              >
                {{ t('aiUsage.filter.last7Days') }}
              </n-button>
              <n-button
                size="small"
                quaternary
                :type="activeQuickRange === 30 ? 'primary' : 'default'"
                @click="setQuickRange(30)"
              >
                {{ t('aiUsage.filter.last30Days') }}
              </n-button>
            </div>
          </div>
        </div>
        <div class="filter-item">
          <span class="filter-label faint">{{ t('aiUsage.filter.provider') }}</span>
          <n-select
            v-model:value="providerCode"
            :options="providerOptions"
            clearable
            filterable
            class="filter-ctl"
            :placeholder="t('aiUsage.filter.providerAll')"
          />
        </div>
        <div class="filter-item">
          <span class="filter-label faint">{{ t('aiUsage.filter.model') }}</span>
          <n-select
            v-model:value="model"
            :options="[]"
            filterable
            tag
            clearable
            class="filter-ctl"
            :placeholder="t('aiUsage.filter.modelAll')"
          />
        </div>
        <div class="filter-item">
          <span class="filter-label faint">{{ t('aiUsage.filter.scene') }}</span>
          <n-input
            v-model:value="scene"
            clearable
            class="filter-ctl"
            :placeholder="t('aiUsage.filter.scenePlaceholder')"
          />
        </div>
      </div>
    </n-card>

    <!-- 四格指标:totals 与分组表共用同一次 summary 请求,不重复请求 -->
    <div class="metrics">
      <n-card
        v-for="m in metrics"
        :key="m.key"
        class="a-card"
        size="small"
        :bordered="true"
        content-style="padding: 12px 14px"
      >
        <div class="metric-body">
          <div class="metric-main">
            <n-skeleton v-if="summaryLoading" text width="72px" height="34px" />
            <div v-else class="metric-val tabular">
              <template v-if="m.empty">—</template>
              <template v-else>
                <n-number-animation :from="0" :to="m.value" show-separator />
                <small v-if="m.suffix" class="metric-suffix">{{ ' ' + m.suffix }}</small>
              </template>
            </div>
            <div class="metric-label muted">{{ t(`aiUsage.metric.${m.key}`) }}</div>
          </div>
          <div class="metric-icon" aria-hidden="true"><AppIcon :icon="m.icon" :size="15" /></div>
        </div>
      </n-card>
    </div>

    <!-- 趋势图:区间内无数据的日期后端已补 0,直接画连续折线 -->
    <n-card
      :title="t('aiUsage.trend.title')"
      class="a-card"
      size="small"
      :bordered="true"
      content-style="padding: 8px 14px 12px"
    >
      <n-skeleton v-if="trendLoading" height="320px" :sharp="false" />
      <LineChart v-else :categories="trendCategories" :series="trendSeries" />
    </n-card>

    <!-- 分组维度 Tab + 分组表:切 Tab 换 groupBy 重新拉 summary;窄档换成卡片列表 -->
    <n-card class="a-card" size="small" :bordered="true" content-style="padding: 4px 14px 12px">
      <n-tabs v-model:value="activeGroupBy" type="line" animated>
        <n-tab-pane
          v-for="tab in groupTabs"
          :key="tab.value"
          :name="tab.value"
          :tab="t(`aiUsage.groupBy.${tab.labelKey}`)"
          display-directive="show:lazy"
        >
          <SmartTable
            :columns="groupColumns"
            :data="groups"
            row-key="key"
            :loading="summaryLoading"
            :search="{ container: 'table' }"
            :toolbar="TABLE_TOOLBAR"
            :card-props="{ bordered: false }"
            :scroll-x="720"
            card-on-narrow
            storage-key="sys-ai-usage-group"
          >
            <template #pagination-prefix="{ itemCount }">
              <TableTotal :count="itemCount" />
            </template>
          </SmartTable>
        </n-tab-pane>
      </n-tabs>
    </n-card>

    <!-- 明细表:顶部筛选走 :params(与条件构造器的 success 键不冲突),「结果」走内置条件搜索 -->
    <SmartTable
      :columns="detailColumns"
      :fetcher="aiUsageApi.page"
      :params="filterParams"
      :search="{ container: 'table' }"
      :toolbar="TABLE_TOOLBAR"
      :default-page-size="20"
      card-on-narrow
      storage-key="sys-ai-usage"
      @error="(e: unknown) => message.error(translateError(e))"
    >
      <template #pagination-prefix="{ itemCount }">
        <TableTotal :count="itemCount" />
      </template>
    </SmartTable>
  </div>
</template>

<style scoped>
/* 容器查询的容器:四格指标的列数按内容区宽度分档(窄 1 / 中 2 / 宽 4),不看视口 */
.view {
  display: flex;
  flex-direction: column;
  gap: var(--gap-card, 8px);
  container-type: inline-size;
}
.filter-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 12px 20px;
}
.filter-item {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}
.filter-label {
  font-size: 13px;
}
.date-filter-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
.date-picker {
  width: 290px;
}
.filter-ctl {
  width: 180px;
}
.quick-range {
  display: flex;
  gap: 4px;
}
/* 窄档:每个筛选项独占一行,控件撑满 */
@container (max-width: 603px) {
  .filter-item {
    flex: 1 1 100%;
  }
  .date-picker,
  .filter-ctl {
    width: 100%;
  }
  .date-picker {
    flex: 1 1 100%;
  }
}
.metrics {
  display: grid;
  gap: 8px;
  grid-template-columns: minmax(0, 1fr);
}
@container (min-width: 480px) {
  .metrics {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
@container (min-width: 1000px) {
  .metrics {
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }
}
.metric-body {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}
.metric-main {
  min-width: 0;
}
.metric-val {
  font-size: 28px;
  font-weight: 600;
  letter-spacing: -0.04em;
  line-height: 1;
  color: var(--text-1);
  white-space: nowrap;
}
.metric-suffix {
  margin-left: 4px;
  font-size: 15px;
  letter-spacing: 0;
  color: var(--text-3);
}
.metric-label {
  margin-top: 6px;
  font-size: 14px;
}
/* 指标图标底块:中性玻璃小方块 */
.metric-icon {
  flex: none;
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: var(--radius-md);
  color: var(--text-1);
  background: var(--glass-strong);
  box-shadow: inset 0 0 0 1px var(--hairline);
}
.share-cell {
  display: flex;
  align-items: center;
  gap: 8px;
}
.share-bar {
  flex: 1;
  min-width: 60px;
}
.share-text {
  flex: none;
  width: 58px;
  text-align: right;
  font-size: 13px;
  white-space: nowrap;
}
</style>
