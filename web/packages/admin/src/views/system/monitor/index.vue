<script setup lang="ts">
// 服务器监控 = 卡片 + 手动刷新(不轮询:CPU 快照后端每次采样 500ms,自动轮询会持续占用)。
import { computed, onMounted, ref } from 'vue'
import { NCard, NProgress, NButton, NSpin, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import { monitorApi } from '#/api'
import { translateError } from '#/utils/error'
import { fmtBytes } from '#/utils/format'
import type { ServerInfoOutput } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const info = ref<ServerInfoOutput | null>(null)
const loading = ref(false)

// 栅格按内容区宽度分档(壳层 604 在这里量的是不含留白的 content box,折算为 600):
// < 600 一列,600–999 两列,>= 1000 四列。运行时卡片占 2 列(一列时占 1),磁盘卡片两列档下占满一行
const { width: pw, bp } = useShellBreakpoint()
const cols = computed(() => (pw.value < 600 ? 1 : pw.value < 1000 ? 2 : 4))
const gridStyle = computed(() => ({ gridTemplateColumns: `repeat(${cols.value}, minmax(0, 1fr))` }))
const runtimeSpan = computed(() => ({ gridColumn: `span ${cols.value === 1 ? 1 : 2}` }))
const diskSpan = computed(() => ({ gridColumn: `span ${cols.value === 2 ? 2 : 1}` }))

async function load() {
  loading.value = true
  try {
    info.value = await monitorApi.server()
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}
onMounted(load)

/** 运行秒数 → d/h/m。 */
function fmtUptime(s: number): string {
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  return [
    d ? `${d}${t('monitor.days')}` : '',
    h ? `${h}${t('monitor.hours')}` : '',
    `${m}${t('monitor.minutes')}`,
  ]
    .filter(Boolean)
    .join(' ')
}
/** 内存/磁盘占用百分比(整数,0–100)。 */
function pct(used: number, total: number): number {
  return total > 0 ? Math.round((used / total) * 100) : 0
}
function usageStatus(p: number): 'success' | 'warning' | 'error' {
  return p >= 90 ? 'error' : p >= 75 ? 'warning' : 'success'
}
</script>

<template>
  <div class="monitor">
    <!-- 顶部工具条:右对齐,只有一个「刷新」(无权限码、无确认) -->
    <div class="bar">
      <n-button
        secondary
        :size="bp === 'narrow' ? 'large' : 'medium'"
        :loading="loading"
        @click="load"
      >
        <template #icon><AppIcon icon="ph:arrow-clockwise" :size="16" /></template>
        {{ t('monitor.refresh') }}
      </n-button>
    </div>

    <n-spin :show="loading" class="monitor-spin">
      <div v-if="info" class="cards" :style="gridStyle">
        <!-- CPU:当前进程占用,不是整机 CPU -->
        <n-card :title="t('monitor.cpu')" class="a-card">
          <div class="stat-val num">
            {{ info.processCpuPercent.toFixed(1) }}
            <small>%</small>
          </div>
          <n-progress
            class="usage"
            type="line"
            :percentage="Math.round(info.processCpuPercent)"
            :status="usageStatus(Math.round(info.processCpuPercent))"
            :show-indicator="false"
            :height="6"
          />
          <div class="sub">
            {{ t('monitor.processorCount') }}: {{ info.processorCount }} ·
            {{ t('monitor.threadCount') }}: {{ info.threadCount }}
          </div>
        </n-card>

        <!-- 内存:大字是字节数,百分比只在进度条指示器上 -->
        <n-card :title="t('monitor.memory')" class="a-card">
          <div class="stat-val num">{{ fmtBytes(info.processWorkingSetBytes) }}</div>
          <n-progress
            class="usage"
            type="line"
            :percentage="pct(info.processWorkingSetBytes, info.totalAvailableMemoryBytes)"
            :status="usageStatus(pct(info.processWorkingSetBytes, info.totalAvailableMemoryBytes))"
            :height="6"
          />
          <div class="sub">
            {{ t('monitor.gcHeap') }}: {{ fmtBytes(info.gcHeapBytes) }} ·
            {{ t('monitor.totalMemory') }}: {{ fmtBytes(info.totalAvailableMemoryBytes) }}
          </div>
        </n-card>

        <!-- 运行时 -->

        <n-card :title="t('monitor.runtime')" class="a-card" :style="runtimeSpan">
          <div class="rows">
            <div class="row">
              <span class="k">{{ t('monitor.machineName') }}</span>
              <span class="v">{{ info.machineName }}</span>
            </div>
            <div class="row">
              <span class="k">{{ t('monitor.framework') }}</span>
              <span class="v">{{ info.frameworkDescription }}</span>
            </div>
            <div class="row">
              <span class="k">{{ t('monitor.os') }}</span>
              <span class="v">{{ info.osDescription }}</span>
            </div>
            <div class="row">
              <span class="k">{{ t('monitor.arch') }}</span>
              <span class="v">{{ info.processArchitecture }}</span>
            </div>
            <div class="row">
              <span class="k">{{ t('monitor.uptime') }}</span>
              <span class="v">{{ fmtUptime(info.processUptimeSeconds) }}</span>
            </div>
          </div>
        </n-card>

        <!-- 磁盘(过滤掉容量为 0 的未就绪/映射盘,免渲染一张全 0 卡片) -->
        <n-card
          v-for="disk in info.disks.filter(d => d.totalBytes > 0)"
          :key="disk.name"
          :title="`${t('monitor.disk')} ${disk.name}`"
          class="a-card"
          :style="diskSpan"
        >
          <div class="disk-val num">
            {{ fmtBytes(disk.totalBytes - disk.freeBytes) }} / {{ fmtBytes(disk.totalBytes) }}
          </div>
          <n-progress
            class="usage"
            type="line"
            :percentage="pct(disk.totalBytes - disk.freeBytes, disk.totalBytes)"
            :status="usageStatus(pct(disk.totalBytes - disk.freeBytes, disk.totalBytes))"
            :height="6"
          />
          <div class="sub">{{ t('monitor.free') }}: {{ fmtBytes(disk.freeBytes) }}</div>
        </n-card>
      </div>
      <!-- 首次加载前没有数据:栅格不渲染,只留一块加载区给 n-spin 盖 -->
      <div v-else :style="{ minHeight: loading ? '180px' : '0' }"></div>
    </n-spin>
  </div>
</template>

<style scoped>
.monitor {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.bar {
  display: flex;
  justify-content: flex-end;
  padding: 2px 4px 0;
}
.cards {
  display: grid;
  gap: 12px;
}
.monitor-spin {
  flex-shrink: 0;
}
/* 大字:28px 加粗、字距收紧、等宽数字;单位缩小并退到三级文字色 */
.stat-val {
  color: var(--text-1);
  font-size: 28px;
  line-height: 34px;
  font-weight: 600;
  letter-spacing: -0.04em;
}
.stat-val small {
  margin-left: 4px;
  font-size: 16px;
  color: var(--text-3);
  letter-spacing: 0;
}
.disk-val {
  color: var(--text-1);
  font-size: 18px;
  line-height: 26px;
  font-weight: 600;
}
.num {
  font-variant-numeric: tabular-nums;
}
.usage {
  margin: 12px 0 8px;
}
.sub {
  font-size: 13px;
  color: var(--text-2);
}
.rows .row {
  display: grid;
  grid-template-columns: 88px minmax(0, 1fr);
  gap: 12px;
  padding: 3px 0;
  line-height: 22px;
}
.rows .k {
  font-size: 13px;
  color: var(--text-2);
}
.rows .v {
  overflow-wrap: anywhere;
}
</style>
