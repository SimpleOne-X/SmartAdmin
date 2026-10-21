<script setup lang="ts">
// 「文件服务器」页签:列表 + 新增/编辑抽屉。在线状态在一页取完后另外问一次,见 access.ts。
import { h, ref } from 'vue'
import { NButton, NPopconfirm, NSpace, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import TableTotal from '#/components/TableTotal/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { fileServerApi, type FilePathEnvironment, type FileServerView } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import { useAccessStatus } from '../access'
import ServerForm from './ServerForm.vue'

defineProps<{ environment: FilePathEnvironment | null }>()

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<FileServerView>>()
const status = useAccessStatus(fileServerApi.status)

const dash = () => h('span', { style: { color: 'var(--text-3)' } }, '—')

const columns: SmartTableColumn<FileServerView>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  // 只作搜索项,不进表格:后端的 keyword 同时匹配名称和根路径
  {
    key: 'keyword',
    title: () => t('filePath.server.keyword'),
    hideInTable: true,
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  { key: 'name', ellipsis: { tooltip: true }, title: () => t('filePath.server.name') },
  {
    key: 'rootPath',
    ellipsis: { tooltip: true },
    title: () => t('filePath.server.rootPath'),
    render: r => h('span', { class: 'mono muted' }, r.rootPath),
  },
  {
    key: 'userName',
    width: 140,
    ellipsis: { tooltip: true },
    title: () => t('filePath.server.userName'),
    render: r => r.userName || dash(),
  },
  {
    key: 'hasPassword',
    width: 100,
    title: () => t('filePath.server.password'),
    render: r =>
      r.hasPassword
        ? h(NTag, { size: 'small', bordered: false, type: 'success' }, () =>
            t('filePath.server.passwordSet'),
          )
        : dash(),
  },
  {
    key: 'status',
    width: 100,
    title: () => t('filePath.status.column'),
    render: r => status.render(r.id),
  },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 80,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !authStore.hasPerm('PUT:/api/v1/sys/file-server/{id}'),
        // 密码留空 = 不改;主机和账号没动,后端不会清密码
        request: (next: boolean) =>
          fileServerApi.update(Number(r.id), {
            name: r.name,
            rootPath: r.rootPath,
            userName: r.userName,
            password: null,
            enabled: next,
            remark: r.remark,
          }),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      }),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 140,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 2, wrapItem: false, justify: 'end' }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/file-server/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/file-server/{id}')
          ? h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => fileServerApi.remove(Number(r.id)), t('filePath.deleted')).then(ok => {
                    if (ok) tableRef.value?.refresh()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('filePath.server.deleteConfirm', { name: r.name }),
              },
            )
          : null,
      ]),
  },
]
deriveHeaderFilters(columns)

// ── 新增/编辑抽屉 ──
const formShow = ref(false)
const editing = ref<FileServerView | null>(null)
function openAdd() {
  editing.value = null
  formShow.value = true
}
function openEdit(r: FileServerView) {
  editing.value = r
  formShow.value = true
}
</script>

<template>
  <SmartTable
    ref="tableRef"
    :toolbar="TABLE_TOOLBAR"
    :columns="columns"
    :fetcher="fileServerApi.page"
    :search="{ container: 'table' }"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-file-server"
    @loaded="rows => status.load(rows)"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button v-auth="'POST:/api/v1/sys/file-server/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
  </SmartTable>

  <ServerForm
    v-model:show="formShow"
    :editing="editing"
    :environment="environment"
    @saved="tableRef?.refresh()"
  />
</template>

<style scoped>
/* 表格单元格由库渲染,要 :deep */
:deep(.mono) {
  font-family: var(--font-mono);
  font-size: 13.5px;
  letter-spacing: 0;
}
:deep(.muted) {
  color: var(--text-2);
}
</style>
