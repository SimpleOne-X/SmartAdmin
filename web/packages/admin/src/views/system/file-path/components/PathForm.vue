<script setup lang="ts">
// 文件路径新增/编辑抽屉。show/editing 走 v-model + prop(null = 新增),与 AI 厂商的 ProviderForm 同一协议。
// 编码保存后不能改(业务拿它引用文件路径),所以编辑态只读。
import { computed, reactive, ref, watch } from 'vue'
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpace,
  NSwitch,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useMediaQuery } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import RoleSelect from '#/components/RoleSelect/index.vue'
import {
  fileServerApi,
  filePathApi,
  type FileAccessTestResult,
  type FilePathView,
  type FileServerOption,
} from '#/api'
import { translateError } from '#/utils/error'
import { reasonText } from '../access'
import {
  CODE_PATTERN,
  ConflictMode,
  NAMING_RULE_LABEL_KEYS,
  NamingRule,
  exampleFileName,
  folderPreview,
  normalizeExtensions,
  overwriteRisk,
} from '../rules'

const show = defineModel<boolean>('show', { default: false })
const props = defineProps<{ editing: FilePathView | null }>()
const emit = defineEmits<{ (e: 'saved'): void }>()

const { t } = useI18n()
const message = useMessage()
const formRef = ref<FormInst | null>(null)

// 窄屏标签放上方,底部抽屉里横排标签太挤
const narrowForm = useMediaQuery('(max-width: 640px)')
const formLayout = computed(() =>
  narrowForm.value
    ? { labelPlacement: 'top' as const, labelWidth: undefined }
    : { labelPlacement: 'left' as const, labelWidth: 120 },
)

interface PathFormState {
  code: string
  name: string
  /** 未选时为 null:空串会被 n-select 当成「选中了一个不存在的选项」而不显示占位符。 */
  serverId: number | null
  folder: string
  namingRule: number
  onConflict: number
  readOnly: boolean
  allowedExtensions: string
  maxSizeMb: number | null
  uploadRoleIds: number[]
  downloadRoleIds: number[]
  enabled: boolean
  remark: string
}
const blank = (): PathFormState => ({
  code: '',
  name: '',
  serverId: null,
  folder: '',
  namingRule: NamingRule.YearMonthOriginal,
  onConflict: ConflictMode.Reject,
  readOnly: false,
  allowedExtensions: '',
  maxSizeMb: null,
  uploadRoleIds: [],
  downloadRoleIds: [],
  enabled: true,
  remark: '',
})
const form = reactive<PathFormState>(blank())

// ── 文件服务器下拉:用 options(挂在文件路径的查询权限上),不用 page(那是文件服务器页签的权限) ──
const servers = ref<FileServerOption[]>([])
const serversLoading = ref(false)
async function loadServers() {
  serversLoading.value = true
  try {
    servers.value = await fileServerApi.options()
  } catch (e) {
    message.error(translateError(e))
  } finally {
    serversLoading.value = false
  }
}
// 已停用的服务器不能新选,但当前行已选的那个仍要显示出名字
const serverOptions = computed(() =>
  servers.value.map(s => ({
    label: s.enabled ? s.name : `${s.name} (${t('common.disabled')})`,
    value: Number(s.id),
    disabled: !s.enabled,
  })),
)
const rootPath = computed(
  () => servers.value.find(s => Number(s.id) === form.serverId)?.rootPath ?? '',
)
const preview = computed(() => (rootPath.value ? folderPreview(rootPath.value, form.folder) : ''))
const example = computed(() => exampleFileName(form.namingRule, new Date()))

const namingOptions = computed(() =>
  Object.entries(NAMING_RULE_LABEL_KEYS).map(([value, key]) => ({
    label: t(key),
    value: Number(value),
  })),
)

const showOverwriteRisk = computed(() => overwriteRisk(form))

const rules: FormRules = {
  code: {
    validator: (_r, v: string) => CODE_PATTERN.test(v) || new Error(t('filePath.path.codeInvalid')),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('filePath.path.nameRequired'),
    trigger: ['input', 'blur'],
  },
  serverId: {
    required: true,
    type: 'number',
    message: () => t('filePath.path.serverRequired'),
    trigger: ['change', 'blur'],
  },
  allowedExtensions: {
    validator: () => {
      if (form.readOnly) return true
      const normalized = normalizeExtensions(form.allowedExtensions)
      if (normalized === null) return new Error(t('filePath.path.extensionsInvalid'))
      return normalized !== '' || new Error(t('filePath.path.extensionsRequired'))
    },
    trigger: ['input', 'blur'],
  },
  maxSizeMb: {
    validator: () =>
      form.readOnly || (form.maxSizeMb ?? 0) > 0 || new Error(t('filePath.path.maxSizeRequired')),
    trigger: ['change', 'blur'],
  },
}

// 回填/重置:组件随父页常驻,每次打开都按 editing 重置,并重新拉服务器下拉(别的页签可能刚改过)。
watch(show, v => {
  if (!v) return
  testResult.value = null
  void loadServers()
  const e = props.editing
  Object.assign(
    form,
    e
      ? {
          code: e.code,
          name: e.name,
          serverId: Number(e.serverId),
          folder: e.folder ?? '',
          namingRule: e.namingRule,
          onConflict: e.onConflict,
          readOnly: e.readOnly,
          allowedExtensions: e.allowedExtensions ?? '',
          maxSizeMb: e.maxSizeMb == null ? null : Number(e.maxSizeMb),
          uploadRoleIds: e.uploadRoleIds.map(Number),
          downloadRoleIds: e.downloadRoleIds.map(Number),
          enabled: e.enabled,
          remark: e.remark ?? '',
        }
      : blank(),
  )
})

// ── 测试连接:用弹窗里当前选的服务器、子文件夹和读写方式,结果在返回体里(ok=false 不代表接口失败) ──
const testing = ref(false)
const testResult = ref<FileAccessTestResult | null>(null)
watch(
  () => [form.serverId, form.folder, form.readOnly],
  () => {
    testResult.value = null
  },
)
async function onTest() {
  if (form.serverId === null) return
  testing.value = true
  testResult.value = null
  try {
    testResult.value = await filePathApi.test({
      serverId: form.serverId,
      folder: form.folder.trim() || null,
      readOnly: form.readOnly,
    })
  } catch (e) {
    message.error(translateError(e))
  } finally {
    testing.value = false
  }
}

async function save() {
  await formRef.value?.validate()
  const serverId = form.serverId
  if (serverId === null) return false
  const body = {
    name: form.name.trim(),
    serverId,
    folder: form.folder.trim() || null,
    namingRule: form.namingRule,
    // 同名处理只对业务指定路径有意义,其余命名规则不会撞名
    onConflict: form.namingRule === NamingRule.BusinessPath ? form.onConflict : ConflictMode.Reject,
    readOnly: form.readOnly,
    allowedExtensions: form.readOnly ? null : normalizeExtensions(form.allowedExtensions),
    maxSizeMb: form.readOnly ? null : form.maxSizeMb,
    uploadRoleIds: form.uploadRoleIds,
    downloadRoleIds: form.downloadRoleIds,
    enabled: form.enabled,
    remark: form.remark.trim() || null,
  }
  try {
    if (props.editing === null) await filePathApi.add({ ...body, code: form.code.trim() })
    else await filePathApi.update(Number(props.editing.id), body)
    message.success(t('filePath.saved'))
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
    :title="editing === null ? t('filePath.path.addTitle') : t('filePath.path.editTitle')"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-alert v-if="editing" type="info" :bordered="false" class="tip">
      {{ t('filePath.path.moveTip') }}
    </n-alert>
    <n-form ref="formRef" :model="form" :rules="rules" v-bind="formLayout">
      <n-form-item :label="t('filePath.path.code')" path="code">
        <n-space vertical :size="2" style="width: 100%">
          <n-input
            v-model:value="form.code"
            maxlength="64"
            :disabled="editing !== null"
            :placeholder="t('filePath.path.codePlaceholder')"
          />
          <span v-if="editing" class="hint">{{ t('filePath.path.codeImmutableHint') }}</span>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('filePath.path.name')" path="name">
        <n-input v-model:value="form.name" maxlength="64" :placeholder="t('filePath.path.name')" />
      </n-form-item>
      <n-form-item :label="t('filePath.path.server')" path="serverId">
        <n-select
          v-model:value="form.serverId"
          :options="serverOptions"
          :loading="serversLoading"
          filterable
          :placeholder="t('filePath.path.serverPlaceholder')"
        />
      </n-form-item>
      <n-form-item :label="t('filePath.path.folder')">
        <n-space vertical :size="2" style="width: 100%">
          <n-input
            v-model:value="form.folder"
            maxlength="260"
            :placeholder="t('filePath.path.folderPlaceholder')"
          />
          <span v-if="preview" class="hint">
            {{ t('filePath.path.fullFolder') }}
            <code class="mono">{{ preview }}</code>
          </span>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('filePath.path.namingRule')">
        <n-space vertical :size="2" style="width: 100%">
          <n-select v-model:value="form.namingRule" :options="namingOptions" />
          <span class="hint">
            {{ t('filePath.path.example') }}
            <code class="mono">{{ example }}</code>
          </span>
        </n-space>
      </n-form-item>
      <n-form-item
        v-if="form.namingRule === NamingRule.BusinessPath"
        :label="t('filePath.path.onConflict')"
      >
        <n-space vertical :size="6" style="width: 100%">
          <n-radio-group v-model:value="form.onConflict">
            <n-radio-button :value="ConflictMode.Reject">
              {{ t('filePath.onConflict.reject') }}
            </n-radio-button>
            <n-radio-button :value="ConflictMode.Overwrite">
              {{ t('filePath.onConflict.overwrite') }}
            </n-radio-button>
          </n-radio-group>
          <n-alert v-if="showOverwriteRisk" type="warning" :bordered="false">
            {{ t('filePath.path.overwriteRisk') }}
          </n-alert>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('filePath.path.readOnly')">
        <n-space vertical :size="2" style="width: 100%">
          <n-switch v-model:value="form.readOnly" />
          <span class="hint">{{ t('filePath.path.readOnlyHint') }}</span>
        </n-space>
      </n-form-item>
      <template v-if="!form.readOnly">
        <n-form-item :label="t('filePath.path.extensions')" path="allowedExtensions">
          <n-space vertical :size="2" style="width: 100%">
            <n-input
              v-model:value="form.allowedExtensions"
              maxlength="500"
              :placeholder="t('filePath.path.extensionsPlaceholder')"
            />
            <span class="hint">{{ t('filePath.path.extensionsHint') }}</span>
          </n-space>
        </n-form-item>
        <n-form-item :label="t('filePath.path.maxSize')" path="maxSizeMb">
          <n-input-number
            v-model:value="form.maxSizeMb"
            :min="1"
            :precision="0"
            style="width: 200px"
          >
            <template #suffix>MB</template>
          </n-input-number>
        </n-form-item>
      </template>
      <n-form-item :label="t('filePath.path.uploadRoles')">
        <RoleSelect
          v-model:value="form.uploadRoleIds"
          multiple
          :placeholder="t('filePath.path.rolesPlaceholder')"
        />
      </n-form-item>
      <n-form-item :label="t('filePath.path.downloadRoles')">
        <RoleSelect
          v-model:value="form.downloadRoleIds"
          multiple
          :placeholder="t('filePath.path.rolesPlaceholder')"
        />
      </n-form-item>
      <n-form-item :label="t('filePath.test.action')">
        <n-space :size="12" align="center">
          <n-button
            v-auth="'POST:/api/v1/sys/file-path/test'"
            size="small"
            :loading="testing"
            :disabled="form.serverId === null"
            @click="onTest"
          >
            {{ t('filePath.test.action') }}
          </n-button>
          <span v-if="testResult" class="test-result" :class="testResult.ok ? 'ok' : 'fail'">
            <AppIcon :icon="testResult.ok ? 'ph:check-circle' : 'ph:x-circle'" :size="16" />
            {{
              testResult.ok
                ? t('filePath.test.ok', { ms: testResult.latencyMs })
                : t('filePath.test.failed', { reason: reasonText(testResult.reason) })
            }}
          </span>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('common.enabled')">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>
      <n-form-item :label="t('filePath.remark')">
        <n-input
          v-model:value="form.remark"
          type="textarea"
          maxlength="512"
          :autosize="{ minRows: 2 }"
        />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
.tip {
  margin-bottom: 12px;
}
.hint {
  font-size: 13px;
  color: var(--text-3);
}
.mono {
  font-family: var(--font-mono);
  font-size: 13px;
  word-break: break-all;
}
.test-result {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 13px;
}
.test-result.ok {
  color: var(--ok);
}
.test-result.fail {
  color: var(--err);
}
</style>
