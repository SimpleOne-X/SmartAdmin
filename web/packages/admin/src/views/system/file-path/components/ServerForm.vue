<script setup lang="ts">
// 文件服务器新增/编辑抽屉。show/editing 走 v-model + prop(null = 新增),与 AI 厂商的 ProviderForm 同一协议。
// 密码留空表示不改;换了主机或账号而密码仍留空,后端会清掉旧密码,所以这种情况表单不让过。
import { computed, reactive, ref, watch } from 'vue'
import {
  NAlert,
  NAutoComplete,
  NButton,
  NForm,
  NFormItem,
  NInput,
  NSpace,
  NSwitch,
  NTag,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useMediaQuery } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import {
  fileServerApi,
  type FileAccessTestResult,
  type FilePathEnvironment,
  type FileServerSaveInput,
  type FileServerView,
} from '#/api'
import { translateError } from '#/utils/error'
import { reasonText } from '../access'
import { passwordReentryRequired } from '../rules'

const show = defineModel<boolean>('show', { default: false })
const props = defineProps<{
  editing: FileServerView | null
  /** 白名单根目录与平台能力;拿不到时(没有查询权限)按「能用账号」、没有候选处理。 */
  environment: FilePathEnvironment | null
}>()
const emit = defineEmits<{ (e: 'saved'): void }>()

const { t } = useI18n()
const message = useMessage()
const formRef = ref<FormInst | null>(null)

// 窄屏标签放上方,底部抽屉里横排标签太挤
const narrowForm = useMediaQuery('(max-width: 640px)')
const formLayout = computed(() =>
  narrowForm.value
    ? { labelPlacement: 'top' as const, labelWidth: undefined }
    : { labelPlacement: 'left' as const, labelWidth: 100 },
)

const supportsCredentials = computed(() => props.environment?.supportsCredentials ?? true)
const rootOptions = computed(() =>
  (props.environment?.allowedRoots ?? []).map(r => ({ label: r, value: r })),
)

interface ServerFormState {
  name: string
  rootPath: string
  userName: string
  password: string
  enabled: boolean
  remark: string
}
const blank = (): ServerFormState => ({
  name: '',
  rootPath: '',
  userName: '',
  password: '',
  enabled: true,
  remark: '',
})
const form = reactive<ServerFormState>(blank())

// 换主机或账号且已设置过密码时必须重填。不支持账号密码的平台(Linux)上不要求:那里换主机同样会让这条判断成立,
// 但后端不用账号密码连接,密码框也是置灰的,所以由 supportsCredentials 直接挡掉。
const reentry = computed(
  () =>
    props.editing !== null &&
    supportsCredentials.value &&
    passwordReentryRequired(props.editing, form.rootPath, form.userName),
)

const rules: FormRules = {
  name: {
    required: true,
    whitespace: true,
    message: () => t('filePath.server.nameRequired'),
    trigger: ['input', 'blur'],
  },
  rootPath: {
    required: true,
    whitespace: true,
    message: () => t('filePath.server.rootPathRequired'),
    trigger: ['input', 'blur'],
  },
  password: {
    validator: () =>
      reentry.value && !form.password ? new Error(t('filePath.server.passwordReentry')) : true,
    trigger: ['input', 'blur'],
  },
}

// 回填/重置:组件随父页常驻,每次打开都按 editing 重置,不能指望重新挂载清空。
watch(show, v => {
  if (!v) return
  testResult.value = null
  const e = props.editing
  Object.assign(
    form,
    e
      ? {
          name: e.name,
          rootPath: e.rootPath,
          userName: e.userName ?? '',
          password: '',
          enabled: e.enabled,
          remark: e.remark ?? '',
        }
      : blank(),
  )
})

// 放在回填之后注册:打开抽屉时先按 editing 回填,再判断要不要重填,不拿上一次的表单值去校验。
// 主机或账号改过之后要不要重填密码可能变了:重新校验密码框,改回原值时过时的「请重新填写密码」随之消失
watch(reentry, () => {
  formRef.value?.validate(undefined, ['password']).catch(() => undefined)
})

// ── 测试连接:用表单里当前填的值,结果在返回体里(ok=false 不代表接口失败) ──
const testing = ref(false)
const testResult = ref<FileAccessTestResult | null>(null)
watch(
  () => [form.rootPath, form.userName, form.password],
  () => {
    testResult.value = null
  },
)
async function onTest() {
  testing.value = true
  testResult.value = null
  try {
    testResult.value = await fileServerApi.test({
      id: props.editing ? Number(props.editing.id) : null,
      rootPath: form.rootPath.trim(),
      userName: form.userName.trim() || null,
      password: form.password || null,
    })
  } catch (e) {
    message.error(translateError(e))
  } finally {
    testing.value = false
  }
}

async function save() {
  await formRef.value?.validate()
  const body: FileServerSaveInput = {
    name: form.name.trim(),
    rootPath: form.rootPath.trim(),
    userName: form.userName.trim() || null,
    password: form.password || null,
    enabled: form.enabled,
    remark: form.remark.trim() || null,
  }
  try {
    if (props.editing === null) await fileServerApi.add(body)
    else await fileServerApi.update(Number(props.editing.id), body)
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
    :title="editing === null ? t('filePath.server.addTitle') : t('filePath.server.editTitle')"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-alert v-if="!supportsCredentials" type="info" :bordered="false" class="tip">
      {{ t('filePath.server.credentialsUnsupported') }}
    </n-alert>
    <n-form ref="formRef" :model="form" :rules="rules" v-bind="formLayout">
      <n-form-item :label="t('filePath.server.name')" path="name">
        <n-input
          v-model:value="form.name"
          maxlength="64"
          :placeholder="t('filePath.server.name')"
        />
      </n-form-item>
      <n-form-item :label="t('filePath.server.rootPath')" path="rootPath">
        <n-auto-complete
          v-model:value="form.rootPath"
          :options="rootOptions"
          :get-show="() => true"
          :input-props="{ maxlength: 260 }"
          :placeholder="t('filePath.server.rootPathPlaceholder')"
        />
      </n-form-item>
      <n-form-item :label="t('filePath.server.userName')">
        <n-input
          v-model:value="form.userName"
          maxlength="128"
          :disabled="!supportsCredentials"
          :placeholder="t('filePath.server.userNamePlaceholder')"
          :input-props="{ autocomplete: 'off' }"
        />
      </n-form-item>
      <n-form-item :label="t('filePath.server.password')" path="password">
        <n-space vertical :size="2" style="width: 100%">
          <n-space :size="8" align="center" :wrap="false">
            <n-input
              v-model:value="form.password"
              type="password"
              show-password-on="click"
              :disabled="!supportsCredentials"
              :placeholder="t('filePath.server.passwordPlaceholder')"
              :input-props="{ autocomplete: 'new-password' }"
              style="min-width: 220px"
            />
            <n-tag v-if="editing?.hasPassword" size="small" type="success" :bordered="false">
              {{ t('filePath.server.passwordSet') }}
            </n-tag>
          </n-space>
          <span class="hint">{{ t('filePath.server.passwordHint') }}</span>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('filePath.test.action')">
        <n-space :size="12" align="center">
          <n-button
            v-auth="'POST:/api/v1/sys/file-server/test'"
            size="small"
            :loading="testing"
            :disabled="!form.rootPath.trim()"
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
