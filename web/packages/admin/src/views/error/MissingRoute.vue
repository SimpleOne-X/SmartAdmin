<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { NCard } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'

const route = useRoute()
const { t } = useI18n()
const component = computed(() => route.meta.missingComponent as string | undefined)
const message = computed(() =>
  t('missingRoute.message', {
    title: String(route.meta.title ?? ''),
    component: component.value ?? '',
  }),
)
</script>

<template>
  <!-- 给运维 / 配菜单的人看的诊断页:撑满内容区,居中块放进卡片里 -->
  <n-card class="missing-card" content-style="padding: 28px 20px">
    <div class="missing-route" role="alert">
      <div class="badge"><Icon icon="ph:puzzle-piece" :width="28" /></div>
      <h1>{{ t('missingRoute.title') }}</h1>
      <p>{{ message }}</p>
      <code v-if="component">{{ component }}</code>
    </div>
  </n-card>
</template>

<style scoped>
.missing-card {
  min-height: 60vh;
  height: 100%;
}
.missing-card :deep(.n-card__content) {
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
}
.missing-route {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 14px;
  width: 100%;
  max-width: 620px;
  min-width: 0;
  text-align: center;
}
.badge {
  display: grid;
  place-items: center;
  width: 56px;
  height: 56px;
  border-radius: var(--radius-lg);
  color: var(--warn);
  background: var(--fill);
  box-shadow: inset 0 0 0 1px var(--hairline);
}
h1 {
  margin: 0;
  color: var(--text-1);
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.02em;
}
p {
  margin: 0;
  max-width: 100%;
  color: var(--text-2);
  font-size: 14.5px;
  line-height: 1.7;
}
code {
  max-width: 100%;
  padding: 6px 8px;
  border: 1px solid var(--hairline-strong);
  border-radius: 4px;
  color: var(--err);
  font-family: var(--font-mono);
  font-size: 13.5px;
  word-break: break-all;
}
</style>
