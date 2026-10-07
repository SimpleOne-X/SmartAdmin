<script setup lang="ts">
// 岗位管理 = 普通 SmartTable CRUD + 行内启停。岗位与机构无关联(PositionInput 无 orgId)。
// 后端无独立启停端点 → StatusSwitch 走全量 update(同 module 页);code 编辑禁用。
import { h, reactive, ref } from 'vue'
import {
  NButton,
  NSpace,
  NInput,
  NInputNumber,
  NPopconfirm,
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
import { positionApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import type { PositionInput, SysPosition } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<SysPosition>>()

// 行数据 → 完整入参:openEdit 回填与 StatusSwitch 行内改状态共用(后端无独立启停端点,均走全量 update)。
const toInput = (r: SysPosition): PositionInput => ({
  name: r.name,
  code: r.code,
  sort: r.sort,
  enabled: r.enabled,
})

const columns: SmartTableColumn<SysPosition>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    key: 'name',
    title: () => t('position.name'),
    search: { actions: SEARCH_ACTIONS.fuzzy },
    sorter: true,
  },
  {
    key: 'code',
    title: () => t('position.code'),
    sorter: true,
    render: r => h('span', { class: 'mono muted' }, r.code),
  },
  {
    key: 'sort',
    title: () => t('position.sort'),
    width: 80,
    sorter: true,
    render: r => h('span', { class: 'num muted' }, r.sort),
  },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 90,
    sorter: true,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !authStore.hasPerm('PUT:/api/v1/sys/position/{id}'),
        request: (next: boolean) => positionApi.update(r.id, { ...toInput(r), enabled: next }),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      }),
  },
  { key: 'createTime', title: () => t('common.createTime'), format: 'datetime', sorter: true },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 140,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 2, wrapItem: false, justify: 'end' }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/position/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/position/{id}')
          ? h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => positionApi.remove(r.id), t('position.deleted')).then(ok => {
                    if (ok) tableRef.value?.refresh()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('position.deleteConfirm', { name: r.name }),
              },
            )
          : null,
      ]),
  },
]
deriveHeaderFilters(columns)

// ── 新增/编辑弹窗 ──
const show = ref(false)
const formRef = ref<FormInst | null>(null)
const editingId = ref<number | null>(null)
const rules: FormRules = {
  name: {
    required: true,
    whitespace: true,
    message: () => t('position.nameRequired'),
    trigger: ['input', 'blur'],
  },
}
const blank = (): PositionInput => ({ name: '', code: '', sort: 0, enabled: true })
const form = reactive<PositionInput>(blank())

function openAdd() {
  editingId.value = null
  Object.assign(form, blank())
  show.value = true
}
function openEdit(r: SysPosition) {
  editingId.value = r.id
  Object.assign(form, toInput(r))
  show.value = true
}
async function save() {
  await formRef.value?.validate()
  try {
    if (editingId.value === null) await positionApi.add({ ...form })
    else await positionApi.update(editingId.value, { ...form })
    message.success(t('position.saved'))
    await tableRef.value?.refresh()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>

<template>
  <SmartTable
    :toolbar="TABLE_TOOLBAR"
    ref="tableRef"
    :columns="columns"
    :fetcher="positionApi.page"
    :search="{ container: 'table' }"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-position"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button v-auth="'POST:/api/v1/sys/position/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
  </SmartTable>

  <FormContainer
    v-model:show="show"
    :title="editingId === null ? t('position.addTitle') : t('position.editTitle')"
    :width="480"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="80">
      <n-form-item :label="t('position.name')" path="name">
        <n-input v-model:value="form.name" :placeholder="t('position.name')" />
      </n-form-item>
      <n-form-item :label="t('position.code')" path="code">
        <n-input
          v-model:value="form.code"
          :placeholder="t('position.codePlaceholder')"
          :disabled="editingId !== null"
        />
        <template v-if="editingId !== null" #feedback>
          <span class="form-hint">{{ t('position.codeImmutableHint') }}</span>
        </template>
      </n-form-item>
      <n-form-item :label="t('position.sort')">
        <n-input-number v-model:value="form.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
/* 编码「创建后不可修改」提示,挂在表单项的 feedback 位 */
.form-hint {
  font-size: 13px;
  color: var(--text-3);
}
</style>
