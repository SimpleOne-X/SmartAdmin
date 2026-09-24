<script setup lang="ts">
// 配置行新增/编辑弹窗(「高级」页用)。
// FormContainer 接管 loading/关闭时机:校验失败 reject / API 失败 return false → 弹层不关。
import { computed, reactive, ref, watch } from 'vue'
import {
  NAlert,
  NInput,
  NInputNumber,
  NForm,
  NFormItem,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import FormContainer from '#/components/FormContainer/index.vue'
import { configApi } from '#/api'
import { translateError } from '#/utils/error'
import type { ConfigInput, SysConfig } from '#/types/api'
import { bumpConfigRevision, tabOfKey } from '../groups'
import { useConfigDraft } from '../draft'

const show = defineModel<boolean>('show', { default: false })
/** 要编辑的行;null = 新增。 */
const props = defineProps<{ row: SysConfig | null }>()

const { t } = useI18n()
const message = useMessage()
const formRef = ref<FormInst | null>(null)

const rules: FormRules = {
  configKey: {
    required: true,
    whitespace: true,
    message: () => t('config.keyRequired'),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('config.nameRequired'),
    trigger: ['input', 'blur'],
  },
}
const blank = (): ConfigInput => ({
  configKey: '',
  configValue: '',
  name: '',
  groupCode: '',
  sort: 0,
  remark: '',
})
const form = reactive<ConfigInput>(blank())

watch(show, v => {
  if (!v) return
  const r = props.row
  Object.assign(
    form,
    r
      ? {
          configKey: r.configKey,
          configValue: r.configValue ?? '',
          name: r.name,
          groupCode: r.groupCode ?? '',
          sort: r.sort,
          remark: r.remark ?? '',
        }
      : blank(),
  )
})

// 键已被某个结构化表单认领时,保存后它显示在那个分类的表单里、不在「高级」表里,先告诉用户去哪找
const draft = useConfigDraft()
const keyHint = computed(() => {
  const key = form.configKey.trim()
  return draft.claimedKeys.value.includes(key)
    ? t('config.keyManagedBy', { tab: t(`config.tab.${tabOfKey(key)}`) })
    : undefined
})

async function save() {
  await formRef.value?.validate()
  try {
    if (props.row === null) await configApi.add({ ...form })
    else await configApi.update(props.row.id, { ...form })
    message.success(t('config.saved'))
    // 用到这批行的列表(「其他配置」与各 Tab 的「本组其它配置」)都跟着版本号重拉
    bumpConfigRevision()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>

<template>
  <FormContainer
    v-model:show="show"
    :title="row === null ? t('config.addTitle') : t('config.editTitle')"
    :width="520"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="90">
      <n-alert v-if="keyHint" type="warning" :bordered="false" class="key-hint">
        {{ keyHint }}
      </n-alert>
      <n-form-item :label="t('config.key')" path="configKey">
        <n-input
          v-model:value="form.configKey"
          :placeholder="t('config.key')"
          :disabled="row !== null"
        />
      </n-form-item>
      <n-form-item :label="t('config.name')" path="name">
        <n-input v-model:value="form.name" :placeholder="t('config.name')" />
      </n-form-item>
      <n-form-item :label="t('config.value')">
        <n-input
          v-model:value="form.configValue as string"
          type="textarea"
          :autosize="{ minRows: 2 }"
        />
      </n-form-item>
      <n-form-item :label="t('config.group')">
        <n-input v-model:value="form.groupCode as string" :placeholder="t('config.group')" />
      </n-form-item>
      <n-form-item :label="t('config.sort')">
        <n-input-number v-model:value="form.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('config.remark')">
        <n-input v-model:value="form.remark as string" type="textarea" :autosize="{ minRows: 2 }" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
.key-hint {
  margin-bottom: 12px;
}
</style>
