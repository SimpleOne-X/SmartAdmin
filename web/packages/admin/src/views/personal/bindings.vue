<script setup lang="ts">
// 账号绑定(个人中心 + 品牌化):启用 providers ∪ 已绑定停用项;卡片网格与配置 Tab 同风。
import { computed, onMounted, ref } from 'vue'
import { NAlert, NButton, NCard, NEmpty, NSpin, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { externalAuthApi, type ExternalProvider, type ExternalBinding } from '#/api'
import { useConfirm } from '#/composables/useConfirm'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import { mergeBindingRows, type BindingRow } from '#/utils/oauthBrand'
import BrandIcon from '#/components/oauth/BrandIcon.vue'
import AppIcon from '#/components/AppIcon.vue'

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()

const loading = ref(true)
const providers = ref<ExternalProvider[]>([])
const bindings = ref<ExternalBinding[]>([])
const busyCode = ref<string | null>(null)

const rows = computed(() => mergeBindingRows(providers.value, bindings.value))
const boundCount = computed(() => rows.value.filter(r => !!r.binding).length)

async function load() {
  loading.value = true
  try {
    const [ps, bs] = await Promise.all([externalAuthApi.providers(), externalAuthApi.bindings()])
    providers.value = ps
    bindings.value = bs
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function bind(row: BindingRow) {
  if (!row.enabled || busyCode.value) return
  busyCode.value = row.code
  try {
    const { authorizeUrl } = await externalAuthApi.bindStart(row.code)
    window.location.href = authorizeUrl
  } catch (e) {
    message.error(translateError(e))
    busyCode.value = null
  }
}

function unbind(row: BindingRow) {
  if (busyCode.value) return
  confirm({
    type: 'warning',
    content: t('oauth.unbindConfirm', { name: row.displayName }),
    action: () => externalAuthApi.unbind(row.code),
    successMsg: t('oauth.unbound'),
  }).then(ok => {
    if (ok) load()
  })
}

/** 三种绑定状态各一个修饰类,着色写在样式里(free = naive 默认卡片)。 */
function cardClass(row: BindingRow) {
  if (!row.enabled) return 'bind-card--disabled'
  if (row.binding) return 'bind-card--bound'
  return 'bind-card--free'
}
</script>

<template>
  <div class="bind-page">
    <header class="bind-header">
      <div class="bind-header-text">
        <!-- 页头只放说明 + 计数胶囊(左侧子导航已标明当前页),标题保留给读屏用 -->
        <h2 class="bind-title">{{ t('oauth.bindingsTitle') }}</h2>
        <p class="bind-hint">{{ t('oauth.bindingsHint') }}</p>
      </div>
      <n-tag
        v-if="!loading && rows.length"
        round
        :bordered="false"
        size="medium"
        class="bind-summary"
      >
        <span class="bind-summary-num">{{ boundCount }}</span>
        / {{ rows.length }} {{ t('oauth.boundCountLabel') }}
      </n-tag>
    </header>

    <n-alert type="info" :bordered="false" class="bind-alert">
      {{ t('oauth.bindingsTip') }}
    </n-alert>

    <n-spin :show="loading">
      <n-empty
        v-if="!loading && !rows.length"
        :description="t('oauth.noProviders')"
        class="bind-empty"
      />

      <div v-else-if="rows.length" class="bind-grid">
        <n-card
          v-for="row in rows"
          :key="row.code"
          size="small"
          :bordered="true"
          class="a-card bind-card"
          :class="cardClass(row)"
        >
          <div class="bind-card-main">
            <span class="bind-avatar" :class="{ 'bind-avatar--bound': !!row.binding }">
              <BrandIcon :code="row.code" :icon="row.icon" :size="44" />
              <span v-if="row.binding" class="bind-check" aria-hidden="true">
                <AppIcon icon="ph:check" :size="12" />
              </span>
            </span>
            <div class="bind-meta">
              <div class="bind-title-row">
                <span class="bind-name">{{ row.displayName }}</span>
                <n-tag v-if="!row.enabled" size="small" type="warning" :bordered="false" round>
                  {{ t('oauth.disabled') }}
                </n-tag>
                <n-tag v-else-if="row.binding" size="small" type="success" :bordered="false" round>
                  {{ t('oauth.bound') }}
                </n-tag>
                <n-tag v-else size="small" :bordered="false" round>
                  {{ t('oauth.notBound') }}
                </n-tag>
              </div>
              <code class="bind-code">{{ row.code }}</code>
            </div>
          </div>
          <p v-if="row.binding" class="bind-time">
            {{ t('oauth.boundAt', { time: fmtDateTime(row.binding.boundAt) }) }}
          </p>
          <p v-else-if="!row.enabled" class="bind-disabled-tip">
            {{ t('oauth.disabledTip') }}
          </p>
          <p v-else class="bind-free-tip">{{ t('oauth.bindTip') }}</p>

          <div class="bind-actions">
            <n-button
              v-if="row.binding"
              size="small"
              quaternary
              type="error"
              :disabled="!!busyCode"
              @click="unbind(row)"
            >
              <template #icon><AppIcon icon="ph:link-break" :size="15" /></template>
              {{ t('oauth.unbind') }}
            </n-button>
            <n-button
              v-else-if="row.enabled"
              size="small"
              type="primary"
              secondary
              :loading="busyCode === row.code"
              :disabled="!!busyCode && busyCode !== row.code"
              @click="bind(row)"
            >
              <template #icon><AppIcon icon="ph:link" :size="15" /></template>
              {{ t('oauth.bind') }}
            </n-button>
            <span v-else class="bind-na">{{ t('oauth.cannotBind') }}</span>
          </div>
        </n-card>
      </div>
    </n-spin>
  </div>
</template>

<style scoped>
.bind-page {
  max-width: 920px;
  width: 100%;
}
.bind-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}
/* 标题仅供读屏 */
.bind-title {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  padding: 0;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
  border: 0;
}
.bind-hint {
  margin: 0;
  max-width: 520px;
  font-size: 14px;
  line-height: 1.6;
  color: var(--text-2);
}
.bind-summary {
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}
.bind-summary-num {
  font-size: 18px;
  font-weight: 600;
  color: var(--signal);
}
.bind-alert {
  margin-bottom: 12px;
}
.bind-empty {
  padding: 48px 0;
}
.bind-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(280px, 100%), 1fr));
  gap: 12px;
  min-height: 120px;
}
.bind-card :deep(.n-card__content) {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px 16px;
}
/* 状态着色必须压过 naive 的 `.n-card.n-card--bordered`(两个类=同权重,加载顺序不确定),
   故修饰类与 .bind-card 复合写,靠权重取胜,而不是靠先后。 */
.bind-card.bind-card--bound {
  border-color: color-mix(in srgb, var(--ok) 40%, transparent);
  background: color-mix(in srgb, var(--ok) 4%, var(--bg-card));
}
.bind-card.bind-card--disabled {
  opacity: 0.78;
  background: var(--fill);
  border: 1px dashed var(--hairline);
}
.bind-card-main {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}
.bind-avatar {
  position: relative;
  display: inline-flex;
  width: 48px;
  height: 48px;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  overflow: visible;
  background: var(--fill);
  color: var(--text-1);
}
.bind-avatar :deep(.oauth-brand-badge),
.bind-avatar :deep(img) {
  border-radius: 50%;
  overflow: hidden;
}
.bind-avatar--bound {
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--ok) 35%, transparent);
}
.bind-check {
  position: absolute;
  right: -2px;
  bottom: -2px;
  display: inline-flex;
  width: 18px;
  height: 18px;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  background: var(--ok);
  color: var(--bg-card);
  box-shadow: 0 0 0 2px var(--bg-card);
  line-height: 0;
}
.bind-meta {
  min-width: 0;
  flex: 1;
}
.bind-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.bind-name {
  font-size: 15px;
  font-weight: 600;
  color: var(--text-1);
}
.bind-code {
  display: inline-block;
  margin-top: 4px;
  padding: 0 6px;
  border-radius: 6px;
  background: var(--fill);
  color: var(--text-2);
  font-family: var(--font-mono);
  font-size: 13.5px;
  letter-spacing: 0;
}
/* 说明行放到头像行下面,整卡一条通栏 */
.bind-time,
.bind-free-tip,
.bind-disabled-tip {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-3);
}
.bind-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 10px;
  border-top: 1px dashed var(--hairline);
  min-height: 36px;
}
.bind-na {
  font-size: 13px;
  color: var(--text-3);
}
</style>
