<script setup lang="ts">
// 单独授权一览:全系统的例外放在一张表里给超管复核;普通管理员只看得到自己数据范围内用户的记录(后端收口)。
// 行操作「去调整」把用户交给父页,打开授权菜单弹窗。
// 取数全交给 SmartTable:翻页、改筛选时它自己丢弃慢的旧响应;抽屉关闭即卸载,下次打开重新取最新数据。
import { computed, h } from 'vue'
import { NButton, NDrawer, NDrawerContent, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import TableTotal from '#/components/TableTotal/index.vue'
import { userApi } from '#/api'
import { translateMenuTitle } from '#/locales/menuTitle'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import { UserMenuEffect, UserMenuGrantStatus, type UserMenuGrantPageItem } from '#/types/api'

const show = defineModel<boolean>('show', { default: false })
const emit = defineEmits<{ (e: 'adjust', user: { id: number; name: string }): void }>()
const { t } = useI18n()
const message = useMessage()

const effectOptions = computed(() => [
  { label: t('userGrant.allow'), value: UserMenuEffect.Allow },
  { label: t('userGrant.deny'), value: UserMenuEffect.Deny },
])
const statusOptions = computed(() => [
  { label: t('userGrant.statusActive'), value: UserMenuGrantStatus.Active },
  { label: t('userGrant.statusExpiring'), value: UserMenuGrantStatus.Expiring },
  { label: t('userGrant.statusExpired'), value: UserMenuGrantStatus.Expired },
])
const STATUS_TAG: Record<UserMenuGrantStatus, 'success' | 'warning' | 'default'> = {
  [UserMenuGrantStatus.Active]: 'success',
  [UserMenuGrantStatus.Expiring]: 'warning',
  [UserMenuGrantStatus.Expired]: 'default',
}
const statusLabel = (s: UserMenuGrantStatus) =>
  statusOptions.value.find(o => o.value === s)?.label ?? ''
const dash = () => h('span', { class: 'faint' }, '—')

const columns: SmartTableColumn<UserMenuGrantPageItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  // 只作搜索项:目标用户账号或姓名
  {
    key: 'user',
    title: () => t('userGrant.colUser'),
    hideInTable: true,
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    key: 'userName',
    title: () => t('userGrant.colUser'),
    ellipsis: { tooltip: true },
    card: 'title',
    render: r => `${r.userName}(${r.userAccount})`,
  },
  {
    key: 'menuTitle',
    title: () => t('userGrant.colMenu'),
    ellipsis: { tooltip: true },
    render: r =>
      [r.moduleTitle ? translateMenuTitle(r.moduleTitle) : null, translateMenuTitle(r.menuTitle)]
        .filter(Boolean)
        .join(' · '),
  },
  {
    key: 'effect',
    title: () => t('userGrant.colEffect'),
    width: 90,
    options: effectOptions,
    search: { actions: SEARCH_ACTIONS.exact, props: { clearable: true } },
    render: r =>
      h(
        NTag,
        {
          size: 'small',
          bordered: false,
          type: r.effect === UserMenuEffect.Allow ? 'success' : 'error',
        },
        () => t(r.effect === UserMenuEffect.Allow ? 'userGrant.allow' : 'userGrant.deny'),
      ),
  },
  {
    key: 'status',
    title: () => t('userGrant.colStatus'),
    width: 110,
    options: statusOptions,
    search: { actions: SEARCH_ACTIONS.exact, props: { clearable: true } },
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: STATUS_TAG[r.status] }, () =>
        statusLabel(r.status),
      ),
  },
  {
    key: 'expireTime',
    title: () => t('userGrant.colExpire'),
    width: 170,
    render: r => (r.expireTime ? r.expireTime.replace('T', ' ') : t('userGrant.expireLongTerm')),
  },
  // 只作搜索项:授权人账号或姓名
  {
    key: 'grantor',
    title: () => t('userGrant.colGrantor'),
    hideInTable: true,
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    key: 'grantorName',
    title: () => t('userGrant.colGrantor'),
    width: 120,
    ellipsis: { tooltip: true },
    render: r => r.grantorName || dash(),
  },
  { key: 'grantTime', title: () => t('userGrant.colGrantTime'), width: 170, format: 'datetime' },
  {
    key: 'remark',
    title: () => t('userGrant.colRemark'),
    ellipsis: { tooltip: true },
    render: r => r.remark || dash(),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 100,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(
        NButton,
        {
          size: 'small',
          quaternary: true,
          type: 'primary',
          onClick: () => emit('adjust', { id: r.userId, name: r.userName }),
        },
        () => t('userGrant.adjust'),
      ),
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <n-drawer v-model:show="show" placement="right" width="min(1120px, 96vw)">
    <n-drawer-content
      :title="t('userGrant.overview')"
      closable
      :body-content-style="{ height: '100%', display: 'flex', flexDirection: 'column' }"
    >
      <SmartTable
        :columns="columns"
        :fetcher="userApi.menuGrantPage"
        :search="{ container: 'table' }"
        :toolbar="TABLE_TOOLBAR"
        fill-height
        :min-row-height="TABLE_MIN_ROW_HEIGHT"
        card-on-narrow
        storage-key="sys-user-menu-grants"
        @error="e => message.error(translateError(e))"
      >
        <template #pagination-prefix="{ itemCount }">
          <TableTotal :count="itemCount" />
        </template>
      </SmartTable>
    </n-drawer-content>
  </n-drawer>
</template>
