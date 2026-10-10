<script setup lang="ts">
// 任务监控 = 4 张 stat 卡 + 近 14 日成败趋势 + 即将执行 + 集群节点,整页吃一个 dashboard 端点。
// 15 秒轮询:页面被 keep-alive,切走的标签没必要继续打点 → onDeactivated 也要停,回来再启;
// onUnmounted 兜底清定时器,防组件销毁后计时器泄漏还在发请求。
import { computed, h, onActivated, onDeactivated, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NSpin, NTag, useMessage } from 'naive-ui'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import { Icon } from '@iconify/vue'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import { useI18n } from 'vue-i18n'
import BaseChart from '#/components/Chart/index.vue'
import type { EChartsOption } from 'echarts'
import { jobApi } from '#/api'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import type { JobDashboard, JobNodeItem, JobUpcomingItem } from '#/types/api'

const { t } = useI18n()
const message = useMessage()

// 栅格按内容区宽度分档(不是视口)
const { bp, width: pw } = useShellBreakpoint()
const narrow = computed(() => bp.value === 'narrow')
const statCols = computed(() => (pw.value < 520 ? 1 : pw.value < 1000 ? 2 : 4))
// 窄档必须显式给单列:网格默认的 auto 列会被表格的最小内容宽撑到 600,而库判卡片的条件是表格根宽 < 600,
// 于是表格永远不换成卡片、页面横向溢出,还自己维持住这个状态。
const NARROW_TABLES_STYLE = { gridTemplateColumns: 'minmax(0, 1fr)' }
const tablesStyle = computed(() => {
  const stacked = pw.value < 1000
  const basis = stacked ? 488 : 240
  return {
    flex: `1 1 ${basis}px`,
    minHeight: `${basis}px`,
    gridTemplateColumns: stacked ? 'minmax(0, 1fr)' : 'repeat(2, minmax(0, 1fr))',
    gridTemplateRows: stacked ? 'repeat(2, minmax(0, 1fr))' : 'minmax(0, 1fr)',
  }
})

const data = ref<JobDashboard | null>(null)
const loading = ref(true)

async function load(silent = true) {
  try {
    data.value = await jobApi.dashboard()
  } catch (e) {
    // 首次失败要让用户知道;轮询失败静默(网络抖一下不必每 15 秒糊一脸红)
    if (!silent) message.error(translateError(e))
  } finally {
    loading.value = false
  }
}

let timer: number | null = null
function start() {
  if (timer != null) return
  timer = window.setInterval(() => void load(), 15_000)
}
function stop() {
  if (timer != null) {
    window.clearInterval(timer)
    timer = null
  }
}
onMounted(() => {
  void load(false)
  start()
})
onActivated(start)
onDeactivated(stop)
onUnmounted(stop)

/** 计数未到位时显示 —,而不是先闪个 0。 */
const num = (v: number | undefined) => (v === undefined ? '—' : v)
const stats = computed(() => [
  {
    key: 'todaySuccess',
    icon: 'ph:check-circle',
    value: num(data.value?.todaySuccess),
    color: 'var(--ok)',
  },
  {
    key: 'todayFailed',
    icon: 'ph:x-circle',
    value: num(data.value?.todayFailed),
    color: 'var(--err)',
  },
  {
    key: 'running',
    icon: 'ph:circle-notch',
    value: num(data.value?.running),
    color: 'var(--color-info)',
  },
  {
    key: 'totalJobs',
    icon: 'ph:clock-countdown',
    value: num(data.value?.totalJobs),
    color: 'var(--signal)',
  },
])

// 柱线组合:成功是当日总量,用柱;失败通常小一个量级,压在柱上会看不见,用线单独描出来。
const trendOption = computed<EChartsOption>(() => {
  const trend = data.value?.trend ?? []
  return {
    tooltip: { trigger: 'axis' },
    // 图例位置写死在顶部:留给默认值时它会落到绘图区底部、压在 x 轴日期标签上。
    // grid.top 的 32 就是给它留的位置。
    legend: { top: 0 },
    grid: { left: 8, right: 16, bottom: 8, top: 32, containLabel: true },
    xAxis: { type: 'category', data: trend.map(p => p.date.slice(5)) }, // MM-dd 足够,14 天跨年概率忽略
    yAxis: { type: 'value', minInterval: 1 },
    series: [
      { name: t('job.monitor.trendSuccess'), type: 'bar', data: trend.map(p => p.success) },
      {
        name: t('job.monitor.trendFailed'),
        type: 'line',
        smooth: true,
        data: trend.map(p => p.failed),
      },
    ],
  }
})

/** 最后心跳的相对时间(轮询 15 秒一刷,精确到秒/分/时够用)。 */
function relative(iso: string): string {
  const diff = Math.max(0, Date.now() - new Date(iso).getTime())
  const sec = Math.floor(diff / 1000)
  if (sec < 10) return t('job.monitor.justNow')
  if (sec < 60) return t('job.monitor.secondsAgo', { n: sec })
  if (sec < 3600) return t('job.monitor.minutesAgo', { n: Math.floor(sec / 60) })
  return t('job.monitor.hoursAgo', { n: Math.floor(sec / 3600) })
}

const upcomingColumns: SmartTableColumn<JobUpcomingItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    title: () => t('job.monitor.upcomingJob'),
    key: 'name',
    ellipsis: { tooltip: true },
    search: {}, // 静态数据:条件由 SmartTable 前端求值
  },
  {
    title: () => t('job.monitor.upcomingTime'),
    key: 'nextRunTime',
    width: 170,
    render: r => h('span', { class: 'tabular' }, fmtDateTime(r.nextRunTime, { empty: '—' })),
  },
]

const nodeColumns: SmartTableColumn<JobNodeItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    title: () => t('job.monitor.nodeName'),
    key: 'nodeName',
    ellipsis: { tooltip: true },
    search: {},
  },
  {
    title: () => t('job.monitor.role'),
    key: 'isLeader',
    width: 90,
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: r.isLeader ? 'primary' : 'default' }, () =>
        t(r.isLeader ? 'job.monitor.leader' : 'job.monitor.standby'),
      ),
  },
  {
    title: () => t('job.monitor.lastHeartbeat'),
    key: 'lastHeartbeat',
    width: 110,
    render: r => relative(r.lastHeartbeat),
  },
  { title: () => t('job.monitor.workerId'), key: 'workerId', width: 90, align: 'center' },
  { title: () => t('job.monitor.pid'), key: 'pid', width: 90, align: 'center' },
]
</script>

<template>
  <!-- 窄档(内容区 < 600)整页自然滚动,不锁一屏;表格换成卡片列表 -->
  <div class="view" :class="{ 'fill-page fill-page--soft': !narrow }">
    <!-- 统计卡按内容区宽度分档:< 520 一列,< 1000 两列,否则四列 -->
    <div class="stats" :style="{ gridTemplateColumns: `repeat(${statCols}, minmax(0, 1fr))` }">
      <n-card
        v-for="s in stats"
        :key="s.key"
        :bordered="true"
        class="a-card"
        content-style="padding: 14px 16px"
      >
        <div class="stat">
          <div class="stat-ico" :style="{ color: s.color }">
            <Icon :icon="s.icon" :width="28" />
          </div>
          <div>
            <div class="stat-val tabular">{{ s.value }}</div>
            <div class="stat-label">{{ t(`job.monitor.${s.key}`) }}</div>
          </div>
        </div>
      </n-card>
    </div>

    <n-card :title="t('job.monitor.trendTitle')" :bordered="true" class="a-card">
      <n-spin :show="loading">
        <BaseChart :option="trendOption" />
      </n-spin>
    </n-card>

    <!-- 统计条与趋势图按内容定高(.fill-page > * 已是 flex-shrink:0),
         底部这一栏吃掉剩余高度,两张表各自在卡片里滚。
         这页是仪表盘不是列表页:统计条 + 320px 趋势图不参与瓜分,1080p 上给两张表还剩 200 来 px,
         768p 上就只剩一条缝了 —— 所以外壳叠 .fill-page--soft,再给这一栏一个下限(内容区 < 1000 时两张表
         上下排,下限 488 = 每张 240 + 间距;>= 1000 并排,下限 240)。 -->
    <div
      class="job-tables"
      :class="{ 'fill-main': !narrow }"
      :style="narrow ? NARROW_TABLES_STYLE : tablesStyle"
    >
      <!-- 两张表都是 SmartTable 静态数据模式;不放表名(设计:表格工具栏没有标题),列头已能分辨。
           窄档(内容区 < 600)整页自然滚动,卡片列表由 card-on-narrow 内置。
           card-on-narrow 跟页面窄档走而不是让库按表格自身宽度判:宽屏下两张表并排各约半宽,会被误判成窄 -->
      <div class="job-table" :class="{ 'fill-main': !narrow }">
        <SmartTable
          :columns="upcomingColumns"
          :data="data?.upcoming ?? []"
          :row-key="(r: JobUpcomingItem) => `${r.jobId}-${r.nextRunTime}`"
          :loading="loading"
          :search="{ container: 'table' }"
          :toolbar="TABLE_TOOLBAR"
          :fill-height="!narrow"
          :min-row-height="TABLE_MIN_ROW_HEIGHT"
          :card-on-narrow="narrow"
          storage-key="sys-job-monitor-upcoming"
        >
          <template #pagination-prefix="{ itemCount }">
            <TableTotal :count="itemCount" />
          </template>
        </SmartTable>
      </div>
      <div class="job-table" :class="{ 'fill-main': !narrow }">
        <SmartTable
          :columns="nodeColumns"
          :data="data?.nodes ?? []"
          row-key="nodeName"
          :loading="loading"
          :search="{ container: 'table' }"
          :toolbar="TABLE_TOOLBAR"
          :fill-height="!narrow"
          :min-row-height="TABLE_MIN_ROW_HEIGHT"
          :card-on-narrow="narrow"
          storage-key="sys-job-monitor-nodes"
        >
          <template #pagination-prefix="{ itemCount }">
            <TableTotal :count="itemCount" />
          </template>
        </SmartTable>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* display / flex-direction / 整屏高度链都在 styles/layout.css 的 .fill-page,这里只留卡片间距。 */
.view {
  gap: var(--gap-card);
}

/*
 * .fill-page--soft 把「恰好一屏」放宽成「至少一屏」,这里再给两张表所在的那一栏一个下限:
 * 卡片头尾约 62px + small 表头 34px + 4 行 x 34px ≈ 240px,少于这个就不叫表了。
 * 高屏上剩余高度本来就大于下限,页面仍然恰好一屏;矮屏上顶到下限后由 .page 正常出滚动条。
 * 容器和栅格项都要给:容器是 flex 子项(.fill-main 带 min-height:0,不设下限会被压过内容),
 * 栅格项则决定单列(l 断点以下两张表上下排)时每张表各自的下限。
 */
.stats {
  display: grid;
  gap: var(--gap-card);
  flex-shrink: 0;
}
.job-tables {
  display: grid;
  gap: var(--gap-card);
}
.stat {
  display: flex;
  align-items: center;
  gap: 16px;
}
/* 图标底盘:取语义色 12% 作浅底,同工作台手法,避免图标"裸浮" */
.stat-ico {
  flex-shrink: 0;
  width: 48px;
  height: 48px;
  border-radius: var(--radius-lg);
  display: flex;
  align-items: center;
  justify-content: center;
  background: color-mix(in srgb, currentColor 12%, transparent);
}
.stat-val {
  font-size: 28px;
  font-weight: 600;
  letter-spacing: -0.04em;
  color: var(--text-1);
  line-height: 34px;
}
.stat-label {
  font-size: 14px;
  color: var(--text-2);
}
</style>
