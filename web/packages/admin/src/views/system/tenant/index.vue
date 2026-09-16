<script setup lang="ts">
// 租户管理:标准 SmartTable CRUD + 行内启停,多一个"隔离模式"分段控件(一期只有共享库可选)
// + 创建态才出现的初始管理员两个字段(编辑不改已有租户的管理员)。
import { h, reactive, ref } from 'vue'
import {
  NButton,
  NSpace,
  NInput,
  NDatePicker,
  NPopconfirm,
  NTag,
  NForm,
  NFormItem,
  NSwitch,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { tenantApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import type { TenantCreateInput, TenantInput, SysTenant } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<SysTenant>>()

// 可空字段落到 '' 而不是 null/undefined:n-input 的 v-model 才能直接绑,不用逐处 as string(同 role.vue 惯例)。
const toInput = (r: SysTenant): TenantInput => ({
  code: r.code,
  name: r.name,
  contactName: r.contactName ?? '',
  contactPhone: r.contactPhone ?? '',
  expireTime: r.expireTime,
  isolationMode: r.isolationMode,
  enabled: r.enabled,
  remark: r.remark ?? '',
})

const columns: SmartTableColumn<SysTenant>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  // 编码列不开搜索:后端 TenantPageInput 只支持按 Name 过滤,开了也是个不生效的搜索框。
  { key: 'code', title: () => t('tenant.code') },
  { key: 'name', title: () => t('tenant.name'), search: true },
  {
    key: 'isolationMode',
    title: () => t('tenant.isolationMode'),
    width: 100,
    render: r =>
      h(NTag, { size: 'small', type: r.isolationMode === 1 ? 'info' : 'default' }, () =>
        r.isolationMode === 1
          ? t('tenant.isolationModeShared')
          : t('tenant.isolationModeStandalone'),
      ),
  },
  { key: 'contactName', title: () => t('tenant.contactName') },
  { key: 'contactPhone', title: () => t('tenant.contactPhone') },
  { key: 'expireTime', title: () => t('tenant.expireTime'), format: 'date' },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 90,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !authStore.hasPerm('PUT:/api/v1/sys/tenant/{id}'),
        request: (next: boolean) => tenantApi.update(r.id, { ...toInput(r), enabled: next }),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      }),
  },
  { key: 'createTime', title: () => t('common.createTime'), format: 'datetime' },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 120,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 4, wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/tenant/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/tenant/{id}')
          ? h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => tenantApi.remove(r.id), t('tenant.deleted')).then(ok => {
                    if (ok) tableRef.value?.refresh()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('tenant.deleteConfirm', { name: r.name }),
              },
            )
          : null,
      ]),
  },
]

// ── 新增/编辑弹窗 ──
const show = ref(false)
const formRef = ref<FormInst | null>(null)
const editingId = ref<number | null>(null)
const rules: FormRules = {
  code: {
    required: true,
    whitespace: true,
    message: () => t('tenant.codeRequired'),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('tenant.nameRequired'),
    trigger: ['input', 'blur'],
  },
  adminAccount: {
    required: true,
    whitespace: true,
    message: () => t('tenant.adminAccountRequired'),
    trigger: ['input', 'blur'],
  },
  adminPassword: {
    required: true,
    whitespace: true,
    message: () => t('tenant.adminPasswordRequired'),
    trigger: ['input', 'blur'],
  },
}
const blank = (): TenantCreateInput => ({
  code: '',
  name: '',
  contactName: '',
  contactPhone: '',
  isolationMode: 1,
  enabled: true,
  remark: '',
  adminAccount: '',
  adminPassword: '',
})
const form = reactive<TenantCreateInput>(blank())

function openAdd() {
  editingId.value = null
  Object.assign(form, blank())
  show.value = true
}
function openEdit(r: SysTenant) {
  editingId.value = r.id
  Object.assign(form, toInput(r), { adminAccount: '', adminPassword: '' })
  show.value = true
}
async function save() {
  await formRef.value?.validate()
  try {
    if (editingId.value === null) await tenantApi.add({ ...form })
    else await tenantApi.update(editingId.value, { ...form })
    message.success(t('tenant.saved'))
    await tableRef.value?.refresh()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>

<template>
  <SmartTable
    ref="tableRef"
    :default-page-size="100"
    :columns="columns"
    :fetcher="tenantApi.page"
    flex-height
    virtual-scroll
    storage-key="sys-tenant"
    @error="e => message.error(translateError(e))"
  >
    <template #toolbar>
      <n-button v-auth="'POST:/api/v1/sys/tenant/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
  </SmartTable>

  <FormContainer
    v-model:show="show"
    :title="editingId === null ? t('tenant.addTitle') : t('tenant.editTitle')"
    :width="480"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="90">
      <n-form-item :label="t('tenant.code')" path="code">
        <n-input
          v-model:value="form.code"
          :disabled="editingId !== null"
          :placeholder="t('tenant.codePlaceholder')"
        />
      </n-form-item>
      <n-form-item :label="t('tenant.name')" path="name">
        <n-input v-model:value="form.name" :placeholder="t('tenant.name')" />
      </n-form-item>

      <!-- 隔离模式:一期只有"共享库"可选,"独立库"置灰并标"二期开放"——见设计稿的路线图展示 -->
      <n-form-item :label="t('tenant.isolationMode')">
        <n-space vertical style="width: 100%">
          <div
            class="iso-option"
            :class="{ selected: form.isolationMode === 1 }"
            @click="form.isolationMode = 1"
          >
            <div class="iso-title">{{ t('tenant.isolationModeShared') }}</div>
            <div class="iso-desc">{{ t('tenant.isolationModeSharedDesc') }}</div>
          </div>
          <div class="iso-option disabled" :title="t('tenant.isolationModePhase2')">
            <div class="iso-title">
              {{ t('tenant.isolationModeStandalone') }}
              <n-tag size="tiny" type="warning" style="margin-left: 6px">
                {{ t('tenant.isolationModePhase2') }}
              </n-tag>
            </div>
            <div class="iso-desc">{{ t('tenant.isolationModeStandaloneDesc') }}</div>
          </div>
        </n-space>
      </n-form-item>

      <n-form-item :label="t('tenant.contactName')">
        <n-input v-model:value="form.contactName as string" />
      </n-form-item>
      <n-form-item :label="t('tenant.contactPhone')">
        <n-input v-model:value="form.contactPhone as string" />
      </n-form-item>
      <n-form-item :label="t('tenant.expireTime')">
        <n-date-picker
          v-model:formatted-value="form.expireTime"
          value-format="yyyy-MM-dd"
          type="date"
          style="width: 100%"
        />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>

      <!-- 初始管理员账号:只在新增时出现,编辑已有租户不改其管理员 -->
      <template v-if="editingId === null">
        <div class="form-section">
          <div class="form-section__title">{{ t('tenant.initialAdmin') }}</div>
        </div>
        <n-form-item :label="t('tenant.adminAccount')" path="adminAccount">
          <n-input
            v-model:value="form.adminAccount"
            :placeholder="t('tenant.adminAccountPlaceholder')"
          />
        </n-form-item>
        <n-form-item :label="t('tenant.adminPassword')" path="adminPassword">
          <n-input
            v-model:value="form.adminPassword"
            type="password"
            show-password-on="click"
            :placeholder="t('tenant.adminPasswordPlaceholder')"
          />
        </n-form-item>
      </template>

      <n-form-item :label="t('tenant.remark')">
        <n-input v-model:value="form.remark as string" type="textarea" :rows="2" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
.iso-option {
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md, 10px);
  padding: 10px 12px;
  cursor: pointer;
}
.iso-option.selected {
  border-color: var(--color-primary);
  background: var(--color-primary-light);
}
.iso-option.disabled {
  cursor: not-allowed;
  opacity: 0.6;
  background: var(--color-fill);
}
.iso-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-primary);
  display: flex;
  align-items: center;
}
.iso-desc {
  font-size: 11.5px;
  color: var(--color-text-tertiary);
  margin-top: 3px;
  line-height: 1.5;
}
</style>
