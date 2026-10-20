<script setup lang="ts">
// 「文件路径」页签:列表 + 新增/编辑抽屉。在线状态在一页取完后另外问一次,见 access.ts。
import { h, onMounted, ref } from 'vue'
import { NButton, NPopconfirm, NSpace, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import TableTotal from '#/components/TableTotal/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { filePathApi, roleApi, type FilePathUpdateInput, type FilePathView } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import { useAccessStatus } from '../access'
import { NAMING_RULE_LABEL_KEYS } from '../rules'
import PathForm from './PathForm.vue'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<FilePathView>>()
const status = useAccessStatus(filePathApi.status)

// 角色列只存 Id,名称取启用角色的下拉数据(登录即可调用);查不到的(已停用)显示 #Id
const roleNames = ref<Record<string, string>>({})
onMounted(async () => {
  try {
    roleNames.value = Object.fromEntries((await roleApi.options()).map(r => [String(r.id), r.name]))
  } catch {
    // 拿不到名称只是退化成显示 Id,不打断页面
  }
})
function rolesText(ids: (number | string)[]) {
  return ids.length === 0
    ? t('filePath.path.allLoggedIn')
    : ids.map(id => roleNames.value[String(id)] ?? `#${id}`).join('、')
}

// 行数据 → 更新入参:openEdit 回填与行内启停共用(后端没有独立启停端点,走全量 update)
const toInput = (r: FilePathView): FilePathUpdateInput => ({
  name: r.name,
  serverId: r.serverId,
  folder: r.folder,
  namingRule: r.namingRule,
  onConflict: r.onConflict,
  readOnly: r.readOnly,
  allowedExtensions: r.allowedExtensions,
  maxSizeMb: r.maxSizeMb,
  uploadRoleIds: r.uploadRoleIds,
  downloadRoleIds: r.downloadRoleIds,
  enabled: r.enabled,
  remark: r.remark,
})

const dash = () => h('span', { style: { color: 'var(--text-3)' } }, '—')

const columns: SmartTableColumn<FilePathView>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  // 只作搜索项,不进表格:后端的 keyword 同时匹配编码和名称
  {
    key: 'keyword',
    title: () => t('filePath.path.keyword'),
    hideInTable: true,
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    key: 'code',
    width: 130,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.code'),
    render: r => h('span', { class: 'mono muted' }, r.code),
  },
  { key: 'name', width: 100, ellipsis: { tooltip: true }, title: () => t('filePath.path.name') },
  {
    key: 'serverName',
    width: 100,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.server'),
    render: r => r.serverName ?? dash(),
  },
  {
    key: 'folder',
    width: 100,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.folder'),
    render: r => (r.folder ? h('span', { class: 'mono muted' }, r.folder) : dash()),
  },
  {
    key: 'namingRule',
    width: 124,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.namingRule'),
    render: r => t(NAMING_RULE_LABEL_KEYS[r.namingRule] ?? 'filePath.path.unknownRule'),
  },
  {
    // 只读的路径不设后缀和上限,在这一格标出「只读」
    key: 'allowedExtensions',
    width: 120,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.extensions'),
    render: r =>
      r.readOnly
        ? h(NTag, { size: 'small', bordered: false }, () => t('filePath.path.readOnly'))
        : r.allowedExtensions
          ? h('span', { class: 'mono muted' }, r.allowedExtensions)
          : dash(),
  },
  {
    key: 'maxSizeMb',
    width: 80,
    title: () => t('filePath.path.maxSize'),
    render: r =>
      r.maxSizeMb == null ? dash() : h('span', { class: 'num muted' }, `${r.maxSizeMb} MB`),
  },
  {
    key: 'status',
    width: 90,
    title: () => t('filePath.status.column'),
    render: r => status.render(r.id),
  },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 70,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !authStore.hasPerm('PUT:/api/v1/sys/file-path/{id}'),
        request: (next: boolean) =>
          filePathApi.update(Number(r.id), { ...toInput(r), enabled: next }),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      }),
  },
  {
    key: 'uploadRoleIds',
    width: 112,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.uploadRoles'),
    render: r => rolesText(r.uploadRoleIds),
  },
  {
    key: 'downloadRoleIds',
    width: 112,
    ellipsis: { tooltip: true },
    title: () => t('filePath.path.downloadRoles'),
    render: r => rolesText(r.downloadRoleIds),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 110,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 2, wrapItem: false, justify: 'end' }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/file-path/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/file-path/{id}')
          ? h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => filePathApi.remove(Number(r.id)), t('filePath.deleted')).then(ok => {
                    if (ok) tableRef.value?.refresh()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('filePath.path.deleteConfirm', { name: r.name }),
              },
            )
          : null,
      ]),
  },
]
deriveHeaderFilters(columns)

// ── 新增/编辑抽屉 ──
const formShow = ref(false)
const editing = ref<FilePathView | null>(null)
function openAdd() {
  editing.value = null
  formShow.value = true
}
function openEdit(r: FilePathView) {
  editing.value = r
  formShow.value = true
}
</script>

<template>
  <SmartTable
    ref="tableRef"
    :toolbar="TABLE_TOOLBAR"
    :columns="columns"
    :fetcher="filePathApi.page"
    :search="{ container: 'table' }"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-file-path"
    @loaded="rows => status.load(rows)"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button v-auth="'POST:/api/v1/sys/file-path/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
  </SmartTable>

  <PathForm v-model:show="formShow" :editing="editing" @saved="tableRef?.refresh()" />
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
