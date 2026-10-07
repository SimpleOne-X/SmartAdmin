<script setup lang="ts">
// 单个厂商卡片:三态视觉(启用/停用/待配置 Key)+ 启停开关 + 编辑入口 + 删除 + 测试连接。
// 待配置态判据:authScheme != 'none' 却没配 Key —— 没 Key 不可能真正启用成功(后端 SetEnabledAsync
// 会拒 49004),即便库里 enabled=true 也按「待配置」显示,三态互斥、待配置优先。
import { computed, ref } from 'vue'
import { NAlert, NButton, NCard, NPopover, NSpace, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useAuthStore } from '#/stores/auth'
import { aiProviderApi } from '#/api'
import { translateError } from '#/utils/error'
import type { AiProviderView, AiTestResult } from '#/types/api'
import { resolvePresetMeta } from '../presets'

const props = defineProps<{ provider: AiProviderView }>()
const emit = defineEmits<{ (e: 'edit'): void; (e: 'deleted'): void }>()

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()
const authStore = useAuthStore()

const meta = computed(() => resolvePresetMeta(props.provider.preset))
// 徽标:自定义厂商(含未登记的预设,resolvePresetMeta 兜底成 custom)画拼图图标,其余用 2 字缩写;
// 颜色只取强调色衍生(启用)或中性色,不为每个厂商写品牌色
const isCustom = computed(() => props.provider.preset === 'custom' || meta.value.abbr === 'Cu')
/** 没配 Key 就不可能真正启用成功,即便库里 enabled=true 也按「待配置」显示,优先于启用/停用视觉。 */
const pendingConfig = computed(
  () => !props.provider.hasApiKey && props.provider.authScheme !== 'none',
)
const cardState = computed(() => {
  if (pendingConfig.value) return 'pending'
  return props.provider.enabled ? 'enabled' : 'disabled'
})
// 展示:待配置卡开关显示为关并写「待配置」(只改展示,provider.enabled 的真实值与保存逻辑不变)
const switchText = computed(() =>
  pendingConfig.value
    ? t('aiModel.pendingConfig')
    : props.provider.enabled
      ? t('aiModel.enabled')
      : t('aiModel.disabled'),
)
const defaultModel = computed(() => props.provider.models.find(m => m.isDefault) ?? null)

const canUpdate = computed(() => authStore.hasPerm('PUT:/api/v1/sys/ai/provider/{id}'))

async function onDelete() {
  const ok = await confirm({
    type: 'warning',
    content: t('aiModel.deleteConfirm', { name: props.provider.name }),
    action: () => aiProviderApi.remove(props.provider.id),
    successMsg: t('aiModel.deleted'),
  })
  if (ok) emit('deleted')
}

// ── 测试连接:结果在返回体里(ok=false 不代表接口失败),不走 translateError,
//    只有网络层/其它异常才兜底 message.error。结果用 manual 触发的 popover 展示。 ──
const testing = ref(false)
const testResultShow = ref(false)
const testResult = ref<AiTestResult | null>(null)
async function onTest() {
  testing.value = true
  try {
    testResult.value = await aiProviderApi.test(props.provider.id)
    testResultShow.value = true
  } catch (e) {
    message.error(translateError(e))
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <n-card
    size="small"
    :bordered="true"
    content-style="padding: 12px 14px"
    class="a-card provider-card"
    :class="`provider-card--${cardState}`"
  >
    <div class="card-head">
      <span class="badge" aria-hidden="true">
        <AppIcon v-if="isCustom" icon="ph:puzzle-piece" :size="22" />
        <template v-else>{{ meta.abbr }}</template>
      </span>
      <div class="head-meta">
        <div class="name-row">
          <span class="name" :title="provider.name">{{ provider.name }}</span>
          <n-tag size="small" :bordered="false" class="protocol">{{ provider.protocol }}</n-tag>
        </div>
        <code class="code mono">{{ provider.code }}</code>
      </div>
    </div>

    <n-alert v-if="pendingConfig" type="warning" :bordered="false">
      {{ t('aiModel.pendingConfigTip') }}
    </n-alert>

    <div class="card-body">
      <div class="row">
        <span class="label faint">{{ t('aiModel.provider.baseUrl') }}</span>
        <span class="value ellipsis muted" :title="provider.baseUrl">{{ provider.baseUrl }}</span>
      </div>
      <div class="row">
        <span class="label faint">{{ t('aiModel.provider.apiKey') }}</span>
        <span class="value muted">
          {{
            provider.hasApiKey
              ? t('aiModel.provider.hasApiKeyYes')
              : t('aiModel.provider.hasApiKeyNo')
          }}
          <span v-if="provider.hasApiKey && provider.apiKeyHint" class="mono">
            **** {{ provider.apiKeyHint }}
          </span>
        </span>
      </div>
      <div class="row">
        <span class="label faint">{{ t('aiModel.model.title') }}</span>
        <span class="value ellipsis muted">
          {{ provider.models.length }} ·
          {{ defaultModel ? defaultModel.displayName : t('aiModel.model.noDefault') }}
        </span>
      </div>
    </div>

    <div class="card-actions">
      <div class="switch-group">
        <StatusSwitch
          v-if="canUpdate"
          :value="provider.enabled && !pendingConfig"
          :disabled="pendingConfig"
          :confirm="
            (next: boolean) =>
              next ? null : t('aiModel.provider.disableConfirm', { name: provider.name })
          "
          :request="(next: boolean) => aiProviderApi.setEnabled(provider.id, next)"
          @update:value="(v: boolean) => (provider.enabled = v)"
        />
        <n-tag
          v-else
          size="small"
          :bordered="false"
          :type="provider.enabled && !pendingConfig ? 'success' : 'default'"
        >
          {{ provider.enabled && !pendingConfig ? t('common.enabled') : t('common.disabled') }}
        </n-tag>
        <span class="switch-text muted">
          {{ switchText }}
        </span>
      </div>

      <n-space :size="2" :wrap-item="false">
        <n-popover
          trigger="manual"
          :show="testResultShow"
          placement="top"
          :width="260"
          @clickoutside="testResultShow = false"
        >
          <template #trigger>
            <n-button
              v-auth="'POST:/api/v1/sys/ai/provider/{id}/test'"
              size="small"
              quaternary
              :loading="testing"
              @click="onTest"
            >
              {{ testing ? t('aiModel.test.testing') : t('aiModel.test.action') }}
            </n-button>
          </template>
          <div v-if="testResult" class="test-result">
            <div class="test-head" :class="testResult.ok ? 'ok' : 'fail'">
              <AppIcon :icon="testResult.ok ? 'ph:check-circle' : 'ph:x-circle'" :size="17" />
              {{ testResult.ok ? t('aiModel.test.ok') : t('aiModel.test.failed') }}
            </div>
            <template v-if="testResult.ok">
              <div class="test-row">
                {{ t('aiModel.test.model') }}:
                <span class="mono">{{ testResult.model }}</span>
              </div>
              <div class="test-row">
                {{ t('aiModel.test.latency', { ms: testResult.latencyMs }) }}
              </div>
              <div v-if="testResult.usage" class="test-row">
                {{ t('aiModel.test.usage') }}: {{ t('aiModel.test.inputTokens') }}
                <span class="num">{{ testResult.usage.inputTokens }}</span>
                /
                {{ t('aiModel.test.outputTokens') }}
                <span class="num">{{ testResult.usage.outputTokens }}</span>
              </div>
            </template>
            <div v-else class="test-row test-error">
              {{ t('aiModel.test.error') }}:{{ testResult.error }}
            </div>
          </div>
        </n-popover>

        <n-button
          v-auth="'PUT:/api/v1/sys/ai/provider/{id}'"
          size="small"
          quaternary
          type="primary"
          @click="emit('edit')"
        >
          {{ t('common.edit') }}
        </n-button>
        <n-button
          v-auth="'DELETE:/api/v1/sys/ai/provider/{id}'"
          size="small"
          quaternary
          type="error"
          @click="onDelete"
        >
          {{ t('common.delete') }}
        </n-button>
      </n-space>
    </div>
  </n-card>
</template>

<style scoped>
.provider-card {
  display: flex;
  flex-direction: column;
  transition:
    border-color var(--transition-fast),
    background-color var(--transition-fast),
    opacity var(--transition-fast);
}
.provider-card :deep(.n-card-content) {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
}
/* 三态:状态着色必须压过 naive 的 .n-card.n-card--bordered(同权重,加载顺序不确定),
   故修饰类与 .provider-card 复合写,靠权重取胜。启用 = 选中态令牌(淡底 + 1px 描边),
   待配置 = 虚线警告色边框,停用 = 整卡 0.72 透明度。 */
.provider-card.provider-card--enabled {
  border: 1px solid var(--sel-ring);
  background-image: linear-gradient(var(--sel-bg), var(--sel-bg));
}
.provider-card.provider-card--disabled {
  opacity: 0.72;
}
.provider-card.provider-card--pending {
  border: 1px dashed var(--warn);
}
.card-head {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}
/* 徽标:缩写色块只取强调色衍生(启用)或中性色 */
.badge {
  flex: none;
  width: 42px;
  height: 42px;
  border-radius: var(--radius-lg);
  display: grid;
  place-items: center;
  font-size: 15px;
  font-weight: 600;
  letter-spacing: 0.02em;
  color: var(--text-2);
  background: var(--glass-strong);
  box-shadow: inset 0 0 0 1px var(--hairline);
}
.provider-card--enabled .badge {
  color: var(--sel-fg);
  background: var(--sel-bg);
  box-shadow: inset 0 0 0 1px var(--sel-ring);
}
.head-meta {
  min-width: 0;
  flex: 1;
}
.name-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.name {
  font-size: 15px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-1);
}
.protocol {
  flex: none;
}
.code {
  display: inline-block;
  margin-top: 4px;
  padding: 0 6px;
  border-radius: 6px;
  background: var(--fill);
  color: var(--text-2);
}
.card-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 14px;
}
.row {
  display: flex;
  gap: 8px;
  min-width: 0;
}
.label {
  flex: none;
  width: 66px;
}
.value {
  min-width: 0;
}
.value.ellipsis {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.card-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 4px 8px;
  margin-top: auto;
  padding-top: 10px;
  border-top: 1px dashed var(--hairline-strong);
}
.switch-group {
  display: flex;
  align-items: center;
  gap: 8px;
}
.switch-text {
  font-size: 13.5px;
}
.test-result {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 13.5px;
}
.test-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
}
.test-head.ok {
  color: var(--ok);
}
.test-head.fail {
  color: var(--err);
}
.test-error {
  color: var(--err);
}
</style>
