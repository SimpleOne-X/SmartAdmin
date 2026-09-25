<script setup lang="ts">
// 高级:给开发者看的原始键值表。列出所有没被结构化表单认领的配置行——
// 结构化分组里的零散键、业务模块自己加的配置都在这里;认领清单来自草稿(draft.claimedKeys)。
// 列驱动搜索/分页/竞态交 SmartTable,新增/编辑弹窗是 ConfigFormModal。
import { h, ref, watch } from 'vue'
import { NButton, NSpace, NPopconfirm, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useAuthStore } from '#/stores/auth'
import { configApi } from '#/api'
import { translateError } from '#/utils/error'
import type { SysConfig } from '#/types/api'
import ConfigFormModal from './ConfigFormModal.vue'
import SectionLayout from './SectionLayout.vue'
import { bumpConfigRevision, configRevision } from '../groups'
import { useConfigDraft } from '../draft'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const draft = useConfigDraft()
const tableRef = ref<SmartTableInst<SysConfig>>()

// 在库里排除已认领的键,分页总数才准
const fetchConfigs = (params: Parameters<typeof configApi.page>[0]) =>
  configApi.page({ ...params, excludedKeys: draft.claimedKeys.value })

// 保存或增删改了配置行都刷当前页(不回第 1 页)
watch(configRevision, () => tableRef.value?.refresh())

const columns: SmartTableColumn<SysConfig>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { key: 'configKey', title: () => t('config.key'), search: true, ellipsis: { tooltip: true } },
  { key: 'name', title: () => t('config.name'), search: true, ellipsis: { tooltip: true } },
  {
    key: 'configValue',
    title: () => t('config.value'),
    ellipsis: { tooltip: true },
    render: r => r.configValue || '—',
  },
  {
    key: 'groupCode',
    title: () => t('config.group'),
    search: true,
    render: r => r.groupCode || '—',
  },
  { key: 'sort', title: () => t('config.sort'), width: 80 },
  { key: 'createTime', title: () => t('common.createTime'), format: 'datetime' },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 140,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 4, wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/config/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/config/{id}')
          ? h(
              NPopconfirm,
              {
                // popconfirm 当触发器,「执行→toast」交给 useConfirm().run(与 module 页一致)。
                onPositiveClick: () =>
                  run(() => configApi.remove(r.id), t('config.deleted')).then(ok => {
                    if (ok) bumpConfigRevision()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('config.deleteConfirm', { name: r.name }),
              },
            )
          : null,
      ]),
  },
]

const show = ref(false)
const editing = ref<SysConfig | null>(null)
function openAdd() {
  editing.value = null
  show.value = true
}
function openEdit(r: SysConfig) {
  editing.value = r
  show.value = true
}
</script>

<template>
  <SectionLayout :title="t('config.tab.advanced')" :desc="t('config.advanced.desc')">
    <!-- .fill-main 接上内核的高度链(styles/layout.css):页面不滚,只有表体滚 -->
    <div class="fill-main">
      <SmartTable
        flex-height
        virtual-scroll
        :default-page-size="100"
        ref="tableRef"
        :columns="columns"
        :fetcher="fetchConfigs"
        storage-key="sys-config"
        @error="e => message.error(translateError(e))"
      >
        <template #toolbar>
          <n-button v-auth="'POST:/api/v1/sys/config'" type="primary" @click="openAdd">
            <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
            {{ t('common.add') }}
          </n-button>
        </template>
      </SmartTable>
    </div>
  </SectionLayout>

  <ConfigFormModal v-model:show="show" :row="editing" />
</template>
