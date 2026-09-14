<script setup lang="ts">
// 厂商新增/编辑抽屉(FormContainer variant=drawer,计划 §9 明确要求)。
// show/editing 走 v-model + prop(defineModel('show') + `editing: AiProviderView | null`,
// null = 新增),与 config 页 ConfigFormModal 同一协议——见 onConfirm 协议注释。
import { computed, onMounted, reactive, ref, watch } from 'vue'
import {
  NButton,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NSwitch,
  NTag,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import { aiProviderApi } from '#/api'
import { translateError } from '#/utils/error'
import type { AiModelInput, AiProviderPreset, AiProviderView } from '#/types/api'

const show = defineModel<boolean>('show', { default: false })
const props = defineProps<{ editing: AiProviderView | null }>()
const emit = defineEmits<{ (e: 'saved'): void }>()

const { t } = useI18n()
const message = useMessage()
const formRef = ref<FormInst | null>(null)

const rules: FormRules = {
  code: {
    required: true,
    whitespace: true,
    message: () => t('aiModel.provider.codeRequired'),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('aiModel.provider.nameRequired'),
    trigger: ['input', 'blur'],
  },
  baseUrl: {
    required: true,
    whitespace: true,
    message: () => t('aiModel.provider.baseUrlRequired'),
    trigger: ['input', 'blur'],
  },
}

interface ProviderFormState {
  code: string
  name: string
  /** 新增态未选时为 null——空字符串会被 n-select 误判成"已选中一个不存在的选项"而不显示占位符。 */
  preset: string | null
  baseUrl: string
  authScheme: string
  apiKey: string
  sort: number
  remark: string
  models: AiModelInput[]
}
const blank = (): ProviderFormState => ({
  code: '',
  name: '',
  preset: null,
  baseUrl: '',
  authScheme: '',
  apiKey: '',
  sort: 0,
  remark: '',
  models: [],
})
const form = reactive<ProviderFormState>(blank())

// 回填/重置:show 变 true 时按 editing 是否为空决定新增/编辑态,ProviderForm 组件本身随父页
// 常驻(不随抽屉开关销毁),表单状态必须在这里显式重置,不能指望组件重新挂载帮忙清空。
watch(show, v => {
  if (!v) return
  const e = props.editing
  if (e) {
    Object.assign(form, {
      code: e.code,
      name: e.name,
      preset: e.preset,
      baseUrl: e.baseUrl,
      authScheme: e.authScheme,
      apiKey: '',
      sort: e.sort,
      remark: e.remark ?? '',
      models: e.models.map(m => ({
        id: m.id,
        name: m.name,
        displayName: m.displayName,
        enabled: m.enabled,
        isDefault: m.isDefault,
        contextWindow: m.contextWindow,
        inputPrice: m.inputPrice,
        outputPrice: m.outputPrice,
      })),
    })
  } else {
    Object.assign(form, blank())
  }
})

// ── 厂商预设:挂载时拉一次并缓存,不用每次打开都重拉(ProviderForm 组件本身随父页常驻)。──
const presets = ref<AiProviderPreset[]>([])
const presetsLoading = ref(false)
onMounted(async () => {
  presetsLoading.value = true
  try {
    presets.value = await aiProviderApi.presets()
  } catch (e) {
    message.error(translateError(e))
  } finally {
    presetsLoading.value = false
  }
})
const presetOptions = computed(() => presets.value.map(p => ({ label: p.name, value: p.code })))

/** 新增态按 form.preset 找;编辑态 preset/protocol 不可改,直接用 editing 上的值,不依赖预设是否已拉到。 */
const currentPreset = computed<AiProviderPreset | null>(() => {
  const code = props.editing ? props.editing.preset : form.preset
  return presets.value.find(p => p.code === code) ?? null
})
const presetDisplayName = computed(() => currentPreset.value?.name ?? props.editing?.preset ?? '')
const protocolDisplay = computed(() =>
  props.editing ? props.editing.protocol : (currentPreset.value?.protocol ?? ''),
)

/**
 * 选预设自动带出 name/baseUrl/authScheme/起步模型清单(计划 §9);authScheme 全程只读、由预设决定,
 * 不接受用户直接改——11 个预设里鉴权方式与预设一一对应,允许单独改鉴权方式只会拼出后端拒绝的无效组合。
 */
function onPresetChange(code: string) {
  form.preset = code
  const preset = presets.value.find(p => p.code === code)
  if (!preset) return
  form.name = preset.name
  form.baseUrl = preset.baseUrl
  form.authScheme = preset.authScheme
  form.models = preset.models.map((m, idx) => ({
    id: null,
    name: m.name,
    displayName: m.displayName,
    enabled: true,
    isDefault: idx === 0,
    contextWindow: m.contextWindow ?? null,
    inputPrice: null,
    outputPrice: null,
  }))
}

// ── 模型清单:可勾选/可增删,isDefault 前端就近保证互斥单选(后端 SyncModelsAsync 也会兜底只认第一个 true,双保险)。──
function addModel() {
  form.models.push({
    id: null,
    name: '',
    displayName: '',
    enabled: true,
    isDefault: form.models.length === 0,
    contextWindow: null,
    inputPrice: null,
    outputPrice: null,
  })
}
/** 有 id 的行(编辑态已存在的模型)从提交数组里去掉即可——后端 SyncModelsAsync 按「入参未提及」软删,无需额外标记。 */
function removeModel(idx: number) {
  const removed = form.models[idx]
  form.models.splice(idx, 1)
  if (removed?.isDefault && form.models.length > 0) form.models[0]!.isDefault = true
}
function setDefault(idx: number) {
  form.models.forEach((m, i) => {
    m.isDefault = i === idx
  })
}

async function save() {
  await formRef.value?.validate()
  if (form.models.length === 0) {
    message.error(t('aiModel.model.atLeastOne'))
    return false
  }
  for (const m of form.models) {
    if (!m.name.trim() || !m.displayName.trim()) {
      message.error(t('aiModel.model.nameRequired'))
      return false
    }
  }
  const names = form.models.map(m => m.name.trim())
  if (new Set(names).size !== names.length) {
    message.error(t('aiModel.model.duplicateName'))
    return false
  }

  const models: AiModelInput[] = form.models.map(m => ({
    ...m,
    name: m.name.trim(),
    displayName: m.displayName.trim(),
  }))
  try {
    if (props.editing === null) {
      // 直接在使用点前判空(而不是函数顶部合并条件判断),让 TS 能把 form.preset 窄化成 string 再传给 add()。
      if (!form.preset) {
        message.error(t('aiModel.provider.presetPlaceholder'))
        return false
      }
      await aiProviderApi.add({
        code: form.code.trim(),
        name: form.name.trim(),
        preset: form.preset,
        baseUrl: form.baseUrl.trim(),
        authScheme: form.authScheme,
        apiKey: form.apiKey || null,
        sort: form.sort,
        remark: form.remark.trim() || null,
        models,
      })
    } else {
      await aiProviderApi.update(props.editing.id, {
        code: form.code.trim(),
        name: form.name.trim(),
        baseUrl: form.baseUrl.trim(),
        authScheme: form.authScheme,
        apiKey: form.apiKey || null,
        sort: form.sort,
        remark: form.remark.trim() || null,
        models,
      })
    }
    message.success(t('aiModel.saved'))
    emit('saved')
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>

<template>
  <FormContainer
    v-model:show="show"
    variant="drawer"
    :width="640"
    :title="editing === null ? t('aiModel.addProvider') : t('aiModel.editProvider')"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="100">
      <n-form-item v-if="editing === null" :label="t('aiModel.provider.preset')" path="preset">
        <n-space vertical :size="4" style="width: 100%">
          <n-select
            :value="form.preset"
            :options="presetOptions"
            :loading="presetsLoading"
            :placeholder="t('aiModel.provider.presetPlaceholder')"
            filterable
            @update:value="onPresetChange"
          />
          <n-tag v-if="form.preset === 'custom'" size="small" :bordered="false" type="warning">
            {{ t('aiModel.provider.presetCustom') }}
          </n-tag>
        </n-space>
      </n-form-item>
      <n-form-item v-else :label="t('aiModel.provider.preset')">
        <n-input :value="presetDisplayName" disabled />
      </n-form-item>

      <n-form-item :label="t('aiModel.provider.protocol')">
        <n-input :value="protocolDisplay" disabled />
      </n-form-item>

      <n-form-item :label="t('aiModel.provider.code')" path="code">
        <n-input
          v-model:value="form.code"
          :disabled="editing !== null"
          :placeholder="t('aiModel.provider.codePlaceholder')"
        />
      </n-form-item>
      <n-form-item :label="t('aiModel.provider.name')" path="name">
        <n-input v-model:value="form.name" :placeholder="t('aiModel.provider.name')" />
      </n-form-item>
      <n-form-item :label="t('aiModel.provider.baseUrl')" path="baseUrl">
        <n-input v-model:value="form.baseUrl" placeholder="https://" />
      </n-form-item>
      <n-form-item :label="t('aiModel.provider.authScheme')">
        <n-input :value="form.authScheme" disabled />
      </n-form-item>
      <n-form-item :label="t('aiModel.provider.apiKey')">
        <n-space vertical :size="2" style="width: 100%">
          <n-input
            v-model:value="form.apiKey"
            type="password"
            show-password-on="click"
            :placeholder="t('aiModel.provider.apiKeyPlaceholder')"
          />
          <span class="hint">
            {{ t('aiModel.provider.apiKeyHint') }}
            <template v-if="editing?.apiKeyHint">· **** {{ editing.apiKeyHint }}</template>
          </span>
        </n-space>
      </n-form-item>

      <div class="form-section form-section--panel">
        <div class="form-section__title">{{ t('aiModel.model.title') }}</div>

        <div v-for="(m, idx) in form.models" :key="idx" class="model-row">
          <div class="model-row-head">
            <span class="model-index">#{{ idx + 1 }}</span>
            <n-tag v-if="m.isDefault" size="small" type="primary" :bordered="false">
              {{ t('aiModel.model.defaultTag') }}
            </n-tag>
            <n-button v-else size="tiny" quaternary @click="setDefault(idx)">
              {{ t('aiModel.model.setDefault') }}
            </n-button>
            <div class="model-row-spacer" />
            <n-switch v-model:value="m.enabled" size="small" />
            <span class="mini-label">{{ t('aiModel.model.enabled') }}</span>
            <n-button size="tiny" quaternary type="error" @click="removeModel(idx)">
              <template #icon><AppIcon icon="ph:x" :size="12" /></template>
              {{ t('aiModel.model.remove') }}
            </n-button>
          </div>
          <div class="model-row-fields">
            <div class="field">
              <span class="mini-label">{{ t('aiModel.model.name') }}</span>
              <n-input v-model:value="m.name" size="small" :placeholder="t('aiModel.model.name')" />
            </div>
            <div class="field">
              <span class="mini-label">{{ t('aiModel.model.displayName') }}</span>
              <n-input
                v-model:value="m.displayName"
                size="small"
                :placeholder="t('aiModel.model.displayName')"
              />
            </div>
          </div>
          <div class="model-row-fields">
            <div class="field">
              <span class="mini-label">{{ t('aiModel.model.contextWindow') }}</span>
              <n-input-number
                v-model:value="m.contextWindow"
                :min="0"
                clearable
                size="small"
                :placeholder="t('aiModel.model.contextWindowPlaceholder')"
                style="width: 100%"
              />
            </div>
            <div class="field">
              <span class="mini-label">{{ t('aiModel.model.inputPrice') }}</span>
              <n-input-number
                v-model:value="m.inputPrice"
                :min="0"
                clearable
                size="small"
                :placeholder="t('aiModel.model.pricePlaceholder')"
                style="width: 100%"
              />
            </div>
            <div class="field">
              <span class="mini-label">{{ t('aiModel.model.outputPrice') }}</span>
              <n-input-number
                v-model:value="m.outputPrice"
                :min="0"
                clearable
                size="small"
                :placeholder="t('aiModel.model.pricePlaceholder')"
                style="width: 100%"
              />
            </div>
          </div>
        </div>

        <n-button dashed block size="small" style="margin-top: 8px" @click="addModel">
          <template #icon><AppIcon icon="ph:plus" :size="14" /></template>
          {{ t('aiModel.model.add') }}
        </n-button>
      </div>

      <n-form-item :label="t('aiModel.provider.sort')">
        <n-input-number v-model:value="form.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('aiModel.provider.remark')">
        <n-input v-model:value="form.remark" type="textarea" :autosize="{ minRows: 2 }" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
.hint {
  font-size: 12px;
  color: var(--color-text-tertiary, #999);
}
.model-row {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  padding: 10px 12px;
}
.model-row + .model-row {
  margin-top: 8px;
}
.model-row-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.model-index {
  font-size: 12px;
  font-weight: 600;
  color: var(--color-text-tertiary);
}
.model-row-spacer {
  flex: 1;
}
.model-row-fields {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}
.field {
  flex: 1;
  min-width: 0;
}
.mini-label {
  display: block;
  margin-bottom: 2px;
  font-size: 11px;
  color: var(--color-text-tertiary);
}
</style>
