<script setup lang="ts">
// 第三方登录设置面板(居中的卡片式弹层):填连接信息、看回调地址、测试连接、保存、清除。
// 保存直接调后端,不进页面草稿也不走底部保存条:机密不该在页面上挂着,也不该出现在「离开页面」的未保存确认里。
// 机密只回「是否已配置 + 尾四位」,留空表示不改;改了决定请求去向的字段(OIDC 的 Authority)要重输机密。
import { computed, reactive, ref, watch } from 'vue'
import { NButton, NInput, NModal, NSwitch, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { externalAuthApi, type ExternalAuthTestResult } from '#/api'
import AppIcon from '#/components/AppIcon.vue'
import BrandIcon from '#/components/oauth/BrandIcon.vue'
import { useConfirm } from '#/composables/useConfirm'
import type { ConfigProviderRow } from '#/utils/oauthBrand'
import { translateError } from '#/utils/error'
import { useConfigDraft } from '../draft'
import {
  buildSaveBody,
  buildTestBody,
  callbackFor,
  createForm,
  endpointChanged,
  missingFields,
  secretRequired,
  type ProviderForm,
} from '../providerForm'

const show = defineModel<boolean>('show', { default: false })
/** 要设置的行(已配置的编辑,或已装未配置的首次配置);新增 OIDC 时为 null */
const props = defineProps<{ row: ConfigProviderRow | null; newType?: string }>()

const { t, te } = useI18n()
const message = useMessage()
const { ask } = useConfirm()
const draft = useConfigDraft()

const form = reactive<ProviderForm>({
  isNew: true,
  code: '',
  displayName: '',
  values: {},
  secrets: {},
})

const type = computed(() => props.row?.type || props.newType || '')
const typeInfo = computed(() => draft.catalog.value.types.find(x => x.type === type.value))
const fields = computed(() => typeInfo.value?.fields ?? [])
const isNew = computed(() => !props.row)
/** 库里已有的这条(代码注册的只读,不进面板) */
const saved = computed(() =>
  isNew.value
    ? undefined
    : draft.catalog.value.providers.find(p => p.source === 'db' && p.code === form.code),
)
/** 新增时输入的标识已被占用:面板不做「覆盖」,要改就去改那一行 */
const codeTaken = computed(
  () => isNew.value && draft.catalog.value.providers.some(p => p.code === form.code),
)

const result = ref<ExternalAuthTestResult | null>(null)
const testing = ref(false)
const saving = ref(false)

watch(show, open => {
  if (!open) return
  const identity = props.row
    ? { code: props.row.code, displayName: props.row.displayName }
    : { code: '', displayName: '' }
  const existing = draft.catalog.value.providers.find(
    p => p.source === 'db' && p.code === identity.code,
  )
  Object.assign(form, createForm(fields.value, existing, isNew.value, identity))
  result.value = null
})
// 输入变了,上一次的测试结果就不再对应当前表单
watch(form, () => (result.value = null), { deep: true })

const moved = computed(() => endpointChanged(fields.value, form, saved.value))
const missing = computed(() => missingFields(fields.value, form, saved.value))
const ephemeral = computed(() => draft.catalog.value.dataProtectionEphemeral)
const canSave = computed(
  () => !missing.value.length && !codeTaken.value && !ephemeral.value && !saving.value,
)
const canTest = computed(
  () => !missing.value.length && !codeTaken.value && !testing.value && !saving.value,
)
const callback = computed(() =>
  callbackFor(draft.catalog.value.callbackUriTemplate, form.code || props.row?.code || ''),
)
const codeInvalid = computed(
  () => isNew.value && !!form.code && (missing.value.includes('code') || codeTaken.value),
)

const label = (name: string) =>
  te(`config.externalAuth.field.${name}`) ? t(`config.externalAuth.field.${name}`) : name
const isBool = (name: string) => name === 'usePkce'
const isSecretRequired = (name: string) => {
  const f = fields.value.find(x => x.name === name)
  return !!f && secretRequired(f, saved.value, moved.value)
}
function placeholder(name: string) {
  const state = saved.value?.secrets[name]
  if (!state?.hasValue) return t('config.externalAuth.sheet.secretPlaceholder')
  const now = state.hint ? `••••${state.hint}` : t('config.externalAuth.sheet.secretSet')
  return moved.value
    ? t('config.externalAuth.sheet.secretRetype')
    : t('config.externalAuth.sheet.keepSecret', { now })
}

const boolOf = (name: string) => form.values[name] === 'true'
const setBool = (name: string, v: boolean) => (form.values[name] = String(v))

async function copy() {
  try {
    await navigator.clipboard.writeText(callback.value)
    message.success(t('common.copied'))
  } catch {
    message.error(t('config.externalAuth.sheet.copyFailed'))
  }
}

async function runTest() {
  testing.value = true
  result.value = null
  try {
    result.value = await externalAuthApi.testProvider(buildTestBody(type.value, form, saved.value))
  } catch (e) {
    message.error(translateError(e))
  } finally {
    testing.value = false
  }
}

async function save() {
  saving.value = true
  try {
    await externalAuthApi.saveProvider(form.code, buildSaveBody(type.value, form))
    message.success(t('config.externalAuth.sheet.saved'))
    await draft.refreshProviders()
    show.value = false
  } catch (e) {
    message.error(translateError(e))
  } finally {
    saving.value = false
  }
}

async function clear() {
  const name = form.displayName || form.code
  const ok = await ask({
    title: t('config.externalAuth.sheet.clearTitle', { name }),
    content: t('config.externalAuth.sheet.clearConfirm', { name }),
    type: 'error',
  })
  if (!ok) return
  saving.value = true
  try {
    await externalAuthApi.removeProvider(form.code)
    message.success(t('config.externalAuth.sheet.cleared'))
    await draft.refreshProviders()
    show.value = false
  } catch (e) {
    message.error(translateError(e))
  } finally {
    saving.value = false
  }
}

const checkLabel = (key: string) =>
  te(`config.externalAuth.check.${key}`) ? t(`config.externalAuth.check.${key}`) : key
const ICONS = {
  ok: 'ph:check-circle-fill',
  failed: 'ph:x-circle-fill',
  skipped: 'ph:minus-circle',
} as const
const iconOf = (status: string) => ICONS[status as keyof typeof ICONS] ?? ICONS.skipped
</script>

<template>
  <n-modal
    v-model:show="show"
    :mask-closable="!saving"
    :close-on-esc="!saving"
    transform-origin="center"
  >
    <div
      class="sheet"
      role="dialog"
      aria-modal="true"
      aria-labelledby="provider-sheet-title"
      data-testid="provider-sheet"
    >
      <header>
        <span class="av"><BrandIcon :code="form.code || type" :icon="row?.icon" :size="22" /></span>
        <div>
          <h3 id="provider-sheet-title">
            {{
              isNew
                ? t('config.externalAuth.sheet.addOidc')
                : t('config.externalAuth.sheet.title', { name: form.displayName })
            }}
          </h3>
          <small>
            {{
              isNew
                ? t('config.externalAuth.sheet.oidcHint')
                : t('config.externalAuth.sheet.secretNote')
            }}
          </small>
        </div>
      </header>

      <div class="body">
        <div class="list">
          <template v-if="isNew">
            <label class="fld" for="pf-name">
              <span class="k">
                {{ t('config.externalAuth.field.displayName') }}
                <i>*</i>
              </span>
              <n-input
                v-model:value="form.displayName"
                :input-props="{ id: 'pf-name' }"
                :placeholder="t('config.externalAuth.sheet.namePlaceholder')"
              />
            </label>
            <label class="fld" for="pf-code">
              <span class="k">
                {{ t('config.externalAuth.field.code') }}
                <i>*</i>
                <small :class="{ bad: codeTaken }">
                  {{
                    codeTaken
                      ? t('config.externalAuth.sheet.codeTaken')
                      : t('config.externalAuth.sheet.codeFixed')
                  }}
                </small>
              </span>
              <n-input
                v-model:value="form.code"
                :input-props="{ id: 'pf-code' }"
                :status="codeInvalid ? 'error' : undefined"
                :placeholder="t('config.externalAuth.sheet.codePlaceholder')"
              />
            </label>
          </template>
          <template v-for="f in fields" :key="f.name">
            <div v-if="isBool(f.name)" class="fld">
              <span class="k">{{ label(f.name) }}</span>
              <n-switch
                :value="boolOf(f.name)"
                :aria-label="label(f.name)"
                @update:value="v => setBool(f.name, v)"
              />
            </div>
            <label v-else class="fld" :for="`pf-${f.name}`">
              <span class="k">
                {{ label(f.name) }}
                <i v-if="f.secret ? isSecretRequired(f.name) : f.required">*</i>
              </span>
              <n-input
                v-if="f.secret"
                v-model:value="form.secrets[f.name]"
                type="password"
                :input-props="{ id: `pf-${f.name}`, autocomplete: 'new-password' }"
                :placeholder="placeholder(f.name)"
              />
              <n-input
                v-else
                v-model:value="form.values[f.name]"
                :input-props="{ id: `pf-${f.name}` }"
              />
            </label>
          </template>
        </div>

        <p v-if="moved" class="note warn" role="status">
          {{ t('config.externalAuth.sheet.endpointMoved') }}
        </p>
        <p v-if="ephemeral" class="note warn" role="alert" data-testid="ephemeral-note">
          {{ t('config.externalAuth.sheet.ephemeralKey') }}
        </p>

        <p class="note">
          {{
            t('config.externalAuth.sheet.callbackHint', {
              name: form.displayName || t('config.externalAuth.sheet.idp'),
            })
          }}
        </p>
        <div class="list">
          <div class="fld">
            <span class="k">{{ t('config.externalAuth.sheet.callback') }}</span>
            <div v-if="callback" class="cb">
              <code data-testid="callback-uri">{{ callback }}</code>
              <n-button size="small" @click="copy">{{ t('common.copy') }}</n-button>
            </div>
            <span v-else class="note warn inline">
              {{ t('config.externalAuth.sheet.callbackMissing') }}
            </span>
          </div>
        </div>

        <ul v-if="testing || result" class="results" aria-live="polite" data-testid="test-results">
          <li v-if="testing" class="spin">{{ t('config.externalAuth.sheet.testing') }}</li>
          <template v-else-if="result">
            <li v-for="c in result.checks" :key="c.key" :class="c.status">
              <AppIcon :icon="iconOf(c.status)" :size="16" />
              <span>
                {{ checkLabel(c.key) }} · {{ t(`config.externalAuth.checkStatus.${c.status}`) }}
                <small v-if="c.detail">{{ c.detail }}</small>
              </span>
            </li>
          </template>
        </ul>
      </div>

      <footer>
        <n-button v-if="saved" type="error" quaternary :disabled="saving" @click="clear">
          {{ t('config.externalAuth.sheet.clear') }}
        </n-button>
        <n-button
          :disabled="!canTest"
          :loading="testing"
          data-testid="provider-test"
          @click="runTest"
        >
          {{ t('config.externalAuth.sheet.test') }}
        </n-button>
        <span class="grow" />
        <n-button :disabled="saving" @click="show = false">{{ t('common.cancel') }}</n-button>
        <n-button
          type="primary"
          :disabled="!canSave"
          :loading="saving"
          :title="ephemeral ? t('config.externalAuth.sheet.ephemeralTitle') : undefined"
          data-testid="provider-save"
          @click="save"
        >
          {{ t('common.save') }}
        </n-button>
      </footer>
    </div>
  </n-modal>
</template>

<style scoped>
.sheet {
  display: flex;
  flex-direction: column;
  width: 520px;
  max-width: calc(100vw - 32px);
  max-height: calc(100vh - 40px);
  overflow: hidden;
  border-radius: 16px;
  background: var(--color-bg-elevated);
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.25);
}
header {
  display: flex;
  flex: none;
  align-items: center;
  gap: 10px;
  padding: 16px 20px 12px;
}
.av {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 34px;
  overflow: hidden;
  border-radius: 8px;
  background: var(--color-fill);
}
h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
}
header small {
  display: block;
  color: var(--color-text-tertiary);
}
.body {
  flex: 1;
  min-height: 0;
  padding: 0 20px 6px;
  overflow: auto;
}
.list {
  margin-bottom: 10px;
  border-radius: 10px;
  background: var(--color-fill);
}
.fld {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 42px;
  padding: 5px 12px;
}
.fld + .fld {
  border-top: 0.5px solid var(--color-border);
}
.k {
  flex: none;
  width: 120px;
  font-size: var(--font-size-base);
  color: var(--color-text-primary);
}
.k i {
  margin-left: 2px;
  font-style: normal;
  color: var(--color-danger);
}
.k small {
  display: block;
  font-size: var(--font-size-xs);
  color: var(--color-text-tertiary);
}
.k small.bad {
  color: var(--color-danger);
}
.fld :deep(.n-input) {
  flex: 1;
}
.note {
  margin: 0 0 8px;
  font-size: var(--font-size-sm);
  line-height: 1.5;
  color: var(--color-text-tertiary);
}
.note.warn {
  padding: 8px 10px;
  border-radius: 8px;
  color: var(--color-warning);
  background: var(--color-warning-bg);
}
.note.inline {
  flex: 1;
  margin: 0;
}
.cb {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.cb code {
  flex: 1;
  min-width: 0;
  font-size: var(--font-size-sm);
  word-break: break-all;
}
.results {
  padding: 0;
  margin: 0 0 10px;
  list-style: none;
}
.results li {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 4px 2px;
  font-size: var(--font-size-base);
}
.results li small {
  display: block;
  color: var(--color-text-tertiary);
}
.results .ok {
  color: var(--color-success);
}
.results .failed {
  color: var(--color-danger);
}
.results .skipped,
.results .spin {
  color: var(--color-text-tertiary);
}
footer {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  padding: 12px 20px 16px;
  border-top: 0.5px solid var(--color-border);
}
.grow {
  flex: 1;
}
</style>
