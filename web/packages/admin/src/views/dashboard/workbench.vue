<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { NCard, NSkeleton } from 'naive-ui'
import { useElementSize } from '@vueuse/core'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useUserStore } from '#/stores/user'
import { dashboardApi } from '#/api'
import type { DashboardSummary } from '#/types/api'
import LineChart from '#/components/Chart/LineChart.vue'
import PieChart from '#/components/Chart/PieChart.vue'

const { t } = useI18n()
const user = useUserStore()

const summary = ref<DashboardSummary | null>(null)
const loading = ref(true)

onMounted(async () => {
  try {
    summary.value = await dashboardApi.summary()
  } catch {
    // 首页不该因为一张统计图挂了就糊用户一脸红:统计取不到就显示 —,页面其余部分照常可用。
  } finally {
    loading.value = false
  }
})

/** 计数未到位时显示 —,而不是先闪个 0 再跳到真值。 */
function num(v: number | undefined) {
  return v === undefined ? '—' : v
}

const stats = computed(() => [
  {
    key: 'roles',
    icon: 'ph:shield',
    value: num(summary.value?.roles),
    color: 'var(--chart-1)',
  },
  {
    key: 'users',
    icon: 'ph:users',
    value: num(summary.value?.users),
    color: 'var(--chart-2)',
  },
  {
    key: 'perms',
    icon: 'ph:key',
    value: num(summary.value?.perms),
    color: 'var(--chart-3)',
  },
  {
    key: 'online',
    icon: 'ph:broadcast',
    value: num(summary.value?.onlineSessions),
    color: 'var(--chart-4)',
  },
])

const trendDays = computed(() => summary.value?.trendDays ?? [])
const trendSeries = computed(() => [
  { name: t('workbench.trendLogins'), data: summary.value?.trendLogins ?? [] },
  { name: t('workbench.trendActive'), data: summary.value?.trendActiveUsers ?? [] },
])
// 资源分布就是前三个计数,不必让后端再返一份
const distribution = computed(() => [
  { name: t('workbench.roles'), value: summary.value?.roles ?? 0 },
  { name: t('workbench.users'), value: summary.value?.users ?? 0 },
  { name: t('workbench.perms'), value: summary.value?.perms ?? 0 },
])

// 栅格按内容区宽度分档(不是视口:侧栏展开 / 收起会改变可用宽度)。
// 统计卡 <520 一列 / <1000 两列 / 否则四列;图表卡 <1000 一列 / 否则并排。宽度还没量到(0)时按宽档,免得首帧闪成单列。
const viewRef = ref<HTMLElement | null>(null)
const { width: pw } = useElementSize(viewRef)
const statCols = computed(() => (pw.value === 0 ? 4 : pw.value < 520 ? 1 : pw.value < 1000 ? 2 : 4))
const chartCols = computed(() => (pw.value === 0 || pw.value >= 1000 ? 2 : 1))
</script>

<template>
  <div ref="viewRef" class="view">
    <!-- 欢迎横幅:浅主题色底 + 渐变头像,不受接口失败影响 -->
    <n-card class="a-card banner-card" :bordered="true" content-style="padding: 18px 20px">
      <div class="banner">
        <div class="avatar">
          <Icon icon="ph:user" :width="26" />
        </div>
        <div class="banner-body">
          <div class="hi">{{ t('workbench.welcome', { name: user.userInfo?.name ?? '' }) }}</div>
          <div class="tip">{{ t('workbench.subtitle') }}</div>
        </div>
      </div>
    </n-card>

    <div class="grid" :style="{ gridTemplateColumns: `repeat(${statCols}, minmax(0, 1fr))` }">
      <n-card
        v-for="s in stats"
        :key="s.key"
        class="a-card"
        :bordered="true"
        content-style="display: flex; align-items: center; gap: 16px; padding: 14px 16px"
      >
        <div class="stat-ico" :style="{ color: s.color }">
          <Icon :icon="s.icon" :width="28" />
        </div>
        <div class="stat-body">
          <!-- 骨架而非先闪 0 再跳真值:数字位宽固定,到数时不抖 -->
          <n-skeleton v-if="loading" text width="72px" height="34px" />
          <div v-else class="stat-val tabular">{{ s.value }}</div>
          <div class="stat-label">{{ t(`workbench.${s.key}`) }}</div>
        </div>
      </n-card>
    </div>

    <div class="grid" :style="{ gridTemplateColumns: `repeat(${chartCols}, minmax(0, 1fr))` }">
      <n-card class="a-card" :title="t('workbench.trendTitle')" :bordered="true">
        <n-skeleton v-if="loading" height="320px" :sharp="false" />
        <LineChart v-else :categories="trendDays" :series="trendSeries" />
      </n-card>
      <n-card class="a-card" :title="t('workbench.distributionTitle')" :bordered="true">
        <n-skeleton v-if="loading" height="320px" :sharp="false" />
        <PieChart v-else :data="distribution" />
      </n-card>
    </div>
  </div>
</template>

<style scoped>
.view {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--gap-card);
  align-content: start;
}
.grid {
  display: grid;
  gap: var(--gap-card);
}
/* 横幅卡底色取选中态令牌(主色 6% / 暗色 14%),与业务工作台同一套 */
.banner-card {
  background-image: linear-gradient(var(--sel-bg), var(--sel-bg));
}
.banner {
  display: flex;
  align-items: center;
  gap: 16px;
}
.avatar {
  flex: none;
  width: 52px;
  height: 52px;
  border-radius: 50%;
  display: grid;
  place-items: center;
  color: var(--on-acc);
  background-image: linear-gradient(135deg, var(--signal), var(--login-accent-2));
  box-shadow: 0 8px 20px -8px var(--signal-glow);
}
.banner-body {
  min-width: 0;
  flex: 1;
}
.hi {
  font-size: 18px;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--text-1);
}
.tip {
  margin-top: 2px;
  font-size: 14px;
  color: var(--text-2);
}
/* 图标底盘:取当前语义色 12% 作浅底圆角 */
.stat-ico {
  flex-shrink: 0;
  width: 48px;
  height: 48px;
  border-radius: var(--radius-lg);
  display: grid;
  place-items: center;
  background: color-mix(in srgb, currentColor 12%, transparent);
}
/* 骨架与真值换位时高度一致(34px),卡片不跳 */
.stat-body {
  min-width: 0;
}
.stat-val {
  font-size: 28px;
  line-height: 34px;
  font-weight: 600;
  letter-spacing: -0.04em;
  color: var(--text-1);
}
.stat-label {
  font-size: 14px;
  color: var(--text-2);
}
</style>
