<script setup lang="ts">
import { onActivated, onMounted } from 'vue'
import { NButton, NCard, NDivider, NSpace } from 'naive-ui'
import { useClipboard } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import AppIcon from '#/components/AppIcon.vue'
import { runtime } from '#/lib/runtime'
import { useUserStore } from '#/stores/user'

const { t } = useI18n()
const { bp } = useShellBreakpoint()
const userStore = useUserStore()
const { copy, copied } = useClipboard()

const scalarUrl = `${runtime.apiBase}/scalar`

function copyToken() {
  copy(userStore.accessToken)
}

// 门闩要保的不变量:不管这是本次会话里第几次挂载,一次全新挂载只应该开一个标签页。页面经
// defineAsyncComponent 包装挂进 <keep-alive> 时,onMounted 和 onActivated 有可能在同一个 tick
// 里各调用一次 openDocs(不是只有"某一次特定的挂载"才会撞;不能假设第一次挂载就天然安全)。
// 用一个同步置位、下一个微任务清除的门闩吸收这种"同 tick 双触发":两次同步调用只开一次;
// 而真正的重新激活(用户切走再切回)发生在别的 tick,门闩早已清空,照样正常重开——这正是
// 重新激活必须保留的行为,不能被这里的去重顺带吞掉。
let openGuardActive = false

function openDocs() {
  if (openGuardActive) return
  openGuardActive = true
  window.open(scalarUrl, '_blank')
  Promise.resolve().then(() => {
    openGuardActive = false
  })
}

onMounted(openDocs)
onActivated(openDocs)
</script>

<template>
  <!-- 外壳卡片:页面不放菜单名标题;进入页面即自动新开 Scalar,这里只做提示 + 兜底入口 + 复制令牌 -->
  <NCard
    class="a-card docs-card"
    :class="{ 'docs-card--narrow': bp === 'narrow' }"
    content-style="padding: 24px"
  >
    <NSpace vertical :size="22">
      <div class="lead">
        <span class="sym"><AppIcon icon="ph:file-code" :size="20" /></span>
        <div class="lead-text">
          <div class="lead-title">{{ t('apiDocs.openedHint') }}</div>
          <div class="lead-url">{{ scalarUrl }}</div>
        </div>
      </div>
      <div class="actions">
        <NButton
          tag="a"
          :href="scalarUrl"
          target="_blank"
          secondary
          :size="bp === 'narrow' ? 'large' : 'medium'"
        >
          <template #icon><AppIcon icon="ph:arrow-square-out" :size="16" /></template>
          {{ t('apiDocs.fallbackLink') }}
        </NButton>
      </div>
      <NDivider class="split" />
      <div>
        <div class="actions copy-row">
          <NButton secondary :size="bp === 'narrow' ? 'large' : 'medium'" @click="copyToken">
            <template #icon><AppIcon icon="ph:copy" :size="16" /></template>
            {{ t('apiDocs.copyToken') }}
          </NButton>
          <span v-if="copied" class="copied">
            <AppIcon icon="ph:check-circle" :size="16" />
            {{ t('apiDocs.tokenCopied') }}
          </span>
        </div>
        <div class="hint">{{ t('apiDocs.copyTokenHint') }}</div>
        <div class="warn">
          <AppIcon icon="ph:shield-warning" :size="15" />
          <span>{{ t('apiDocs.tokenWarn') }}</span>
        </div>
      </div>
    </NSpace>
  </NCard>
</template>

<style scoped>
.docs-card {
  width: 100%;
  max-width: 760px;
  margin: 0 auto;
}
.lead {
  display: flex;
  align-items: center;
  gap: 14px;
}
/* 图标底块:中性玻璃 */
.sym {
  display: grid;
  place-items: center;
  flex-shrink: 0;
  width: 28px;
  height: 28px;
  border-radius: var(--radius-md);
  color: var(--text-1);
  background: var(--glass-strong);
  box-shadow:
    inset 0 0 0 1px var(--hairline),
    inset 0 1px 0 var(--edge-light);
}
.lead-text {
  min-width: 0;
}
.lead-title {
  font-size: 16px;
  font-weight: 600;
}
.lead-url {
  margin-top: 2px;
  font-family: var(--font-mono, ui-monospace, 'SFMono-Regular', Menlo, Consolas, monospace);
  font-size: 13.5px;
  color: var(--text-3);
  overflow-wrap: anywhere;
}
.split {
  margin: 0;
}
.copy-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}
.copied {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 14px;
  color: var(--ok);
}
.hint {
  margin-top: 12px;
  font-size: 14px;
  line-height: 1.7;
  color: var(--text-2);
}
.warn {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin-top: 6px;
  font-size: 13px;
  line-height: 1.6;
  color: var(--text-3);
}
.warn > :first-child {
  flex: none;
  margin-top: 3px;
}
/* 窄档(内容区 < 600):按钮铺满一行,好点 */
.docs-card--narrow .actions :deep(.n-button) {
  width: 100%;
}
.docs-card--narrow .copy-row {
  flex-direction: column;
  align-items: stretch;
}
</style>
