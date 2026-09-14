<script setup lang="ts">
// 单个厂商卡片:三态视觉(启用/停用/待配置 Key)+ 启停开关 + 编辑入口 + 删除 + 测试连接。
// 待配置态判据:authScheme != 'none' 却没配 Key —— 没 Key 不可能真正启用成功(后端 SetEnabledAsync
// 会拒 49004),即便库里 enabled=true 也按「待配置」显示,三态互斥、待配置优先。
import { computed, ref } from 'vue'
import { NButton, NCard, NPopover, NSpace, NTag, useMessage } from 'naive-ui'
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
/** 没配 Key 就不可能真正启用成功,即便库里 enabled=true 也按「待配置」显示,优先于启用/停用视觉。 */
const pendingConfig = computed(
  () => !props.provider.hasApiKey && props.provider.authScheme !== 'none',
)
const cardState = computed(() => {
  if (pendingConfig.value) return 'pending'
  return props.provider.enabled ? 'enabled' : 'disabled'
})
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
    class="provider-card"
    :class="`provider-card--${cardState}`"
  >
    <div class="card-head">
      <span class="badge" :style="{ color: meta.color }">
        <AppIcon :icon="meta.icon" :size="22" />
      </span>
      <div class="head-meta">
        <div class="name-row">
          <span class="name">{{ provider.name }}</span>
          <n-tag size="small" :bordered="false">{{ provider.protocol }}</n-tag>
        </div>
        <code class="code">{{ provider.code }}</code>
      </div>
    </div>

    <div v-if="pendingConfig" class="pending-tip">
      <AppIcon icon="ph:warning-circle-duotone" :size="14" />
      {{ t('aiModel.pendingConfigTip') }}
    </div>

    <div class="card-body">
      <div class="row">
        <span class="label">{{ t('aiModel.provider.baseUrl') }}</span>
        <span class="value ellipsis" :title="provider.baseUrl">{{ provider.baseUrl }}</span>
      </div>
      <div class="row">
        <span class="label">{{ t('aiModel.provider.apiKey') }}</span>
        <span class="value">
          {{
            provider.hasApiKey
              ? t('aiModel.provider.hasApiKeyYes')
              : t('aiModel.provider.hasApiKeyNo')
          }}
          <span v-if="provider.hasApiKey && provider.apiKeyHint" class="mask">
            **** {{ provider.apiKeyHint }}
          </span>
        </span>
      </div>
      <div class="row">
        <span class="label">{{ t('aiModel.model.title') }}</span>
        <span class="value">
          {{ provider.models.length }} ·
          {{ defaultModel ? defaultModel.displayName : t('aiModel.model.noDefault') }}
        </span>
      </div>
    </div>

    <div class="card-actions">
      <div class="switch-group">
        <StatusSwitch
          v-if="canUpdate"
          :value="provider.enabled"
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
          :type="provider.enabled ? 'success' : 'default'"
        >
          {{ provider.enabled ? t('common.enabled') : t('common.disabled') }}
        </n-tag>
        <span class="switch-text">
          {{ provider.enabled ? t('aiModel.enabled') : t('aiModel.disabled') }}
        </span>
      </div>

      <n-space :size="4" :wrap-item="false">
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
              <template #icon><AppIcon icon="ph:lightning" :size="14" /></template>
              {{ testing ? t('aiModel.test.testing') : t('aiModel.test.action') }}
            </n-button>
          </template>
          <div v-if="testResult" class="test-result">
            <div class="test-head" :class="testResult.ok ? 'ok' : 'fail'">
              <AppIcon
                :icon="testResult.ok ? 'ph:check-circle-duotone' : 'ph:x-circle-duotone'"
                :size="16"
              />
              {{ testResult.ok ? t('aiModel.test.ok') : t('aiModel.test.failed') }}
            </div>
            <template v-if="testResult.ok">
              <div class="test-row">{{ t('aiModel.test.model') }}:{{ testResult.model }}</div>
              <div class="test-row">
                {{ t('aiModel.test.latency', { ms: testResult.latencyMs }) }}
              </div>
              <div v-if="testResult.usage" class="test-row">
                {{ t('aiModel.test.usage') }}: {{ t('aiModel.test.inputTokens') }}
                {{ testResult.usage.inputTokens }} / {{ t('aiModel.test.outputTokens') }}
                {{ testResult.usage.outputTokens }}
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
  border-radius: var(--radius-lg);
  transition:
    border-color 0.15s,
    background-color 0.15s,
    opacity 0.15s;
}
.provider-card :deep(.n-card-content) {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
/* 状态着色必须压过 naive 的 .n-card.n-card--bordered(同权重,加载顺序不确定),
   故修饰类与 .provider-card 复合写,靠权重取胜。 */
.provider-card.provider-card--enabled {
  border-color: color-mix(in srgb, var(--color-primary) 45%, var(--color-border));
  background: color-mix(in srgb, var(--color-primary) 4%, var(--color-bg-container));
}
.provider-card.provider-card--disabled {
  opacity: 0.72;
}
.provider-card.provider-card--pending {
  border-style: dashed;
  border-color: color-mix(in srgb, var(--color-warning) 55%, var(--color-border));
}
.card-head {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  min-width: 0;
}
.badge {
  flex-shrink: 0;
  width: 42px;
  height: 42px;
  border-radius: var(--radius-md);
  display: flex;
  align-items: center;
  justify-content: center;
  background: color-mix(in srgb, currentColor 14%, transparent);
}
.head-meta {
  min-width: 0;
  flex: 1;
}
.name-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.name {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-text-primary);
  line-height: 1.3;
}
.code {
  display: inline-block;
  margin-top: 4px;
  font-size: 12px;
  color: var(--color-text-tertiary);
  background: var(--color-fill);
  padding: 1px 6px;
  border-radius: 4px;
}
.pending-tip {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-radius: var(--radius-sm);
  background: var(--color-warning-bg);
  color: var(--color-warning);
  font-size: 12px;
}
.card-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 12px;
  min-width: 0;
}
.label {
  flex-shrink: 0;
  color: var(--color-text-tertiary);
  width: 66px;
}
.value {
  min-width: 0;
  color: var(--color-text-secondary);
}
.value.ellipsis {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  display: block;
}
.mask {
  margin-left: 4px;
  color: var(--color-text-tertiary);
}
.card-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
  padding-top: 8px;
  border-top: 1px dashed var(--color-border);
}
.switch-group {
  display: flex;
  align-items: center;
  gap: 6px;
}
.switch-text {
  font-size: 12px;
  color: var(--color-text-tertiary);
}
.test-result {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
}
.test-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  margin-bottom: 2px;
}
.test-head.ok {
  color: var(--color-success);
}
.test-head.fail {
  color: var(--color-danger);
}
.test-row {
  color: var(--color-text-secondary);
}
.test-error {
  color: var(--color-danger);
}
</style>
