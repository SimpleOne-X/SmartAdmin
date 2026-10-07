<script setup lang="ts">
// 在线会话 = 只读 SmartTable + 行内「强制下线」。无表单。踢人走 useConfirm 二次确认(warning),
// 踢「当前这一条」会话置灰(防误踢自己下线)。后端仅按 UserId 过滤、没有条件搜索能力,
// 所以搜索由前端求值:无条件时仍走服务端分页,有条件时取全量再在前端过滤(见 createClientFilterFetcher)。
import { computed, h, ref } from 'vue'
import { NButton, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import { useConfirm } from '#/composables/useConfirm'
import { useAuthStore } from '#/stores/auth'
import { useUserStore } from '#/stores/user'
import { sessionApi } from '#/api'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import { jwtClaim } from '#/utils/jwt'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import {
  createClientFilterFetcher,
  passthroughFilterSerializer,
  deriveHeaderFilters,
} from '#/utils/tableFilter'
import { uaSummary } from '#/utils/ua'
import type { OnlineSessionItem } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()
const authStore = useAuthStore()
const userStore = useUserStore()
const tableRef = ref<SmartTableInst<OnlineSessionItem>>()
// fields 要与下面声明了 search 的列 key 一致
const fetcher = createClientFilterFetcher(sessionApi.online, ['account', 'ip'])

function kick(r: OnlineSessionItem) {
  confirm({
    type: 'warning',
    content: t('session.kickConfirm', { account: r.account }),
    action: () => sessionApi.kick(r.sessionId),
    successMsg: t('session.kicked'),
  }).then(ok => {
    if (ok) tableRef.value?.refresh()
  })
}

// computed:令牌会变(Cookie 会话 F5 后要等静默刷新完成才有 access,刷新后也会换新令牌),sid 要跟着读
const currentSid = computed(() => jwtClaim(userStore.accessToken, 'sid'))

const columns: SmartTableColumn<OnlineSessionItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { key: 'account', title: () => t('session.account'), search: {} },
  {
    key: 'ip',
    title: () => t('session.ip'),
    search: {},
    render: r => h('span', { class: 'mono muted' }, r.ip || '—'),
  },
  // 设备:同一账号多端在线时靠它分辨要踢哪台(UA 解析成「浏览器 · 系统」)
  {
    key: 'device',
    title: () => t('session.device'),
    ellipsis: { tooltip: true },
    render: r => uaSummary(r.userAgent),
  },
  {
    key: 'loginTime',
    title: () => t('session.loginTime'),
    render: r =>
      h('span', { class: 'num muted', style: 'white-space: nowrap' }, fmtDateTime(r.loginTime)),
  },
  {
    key: 'expiresAt',
    title: () => t('session.expiresAt'),
    render: r =>
      h('span', { class: 'num muted', style: 'white-space: nowrap' }, fmtDateTime(r.expiresAt)),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 120,
    fixed: 'right',
    hideInSetting: true,
    render: r => {
      if (!authStore.hasPerm('DELETE:/api/v1/sys/session/{sessionid}')) return null
      // 只置灰当前这一条会话:同一账号在别的设备上的会话是可以(也经常需要)被踢掉的。
      // 当前会话 id 取自访问令牌的 sid 声明。极少数读不到时(比如 F5 后 Cookie 会话还没静默刷新完、令牌还没回来),
      // 退回保守规则(同账号的会话一律置灰),宁可少踢也不误踢自己。
      const isSelf = currentSid.value
        ? r.sessionId === currentSid.value
        : r.userId === userStore.userInfo?.userId
      return h(
        NButton,
        {
          size: 'small',
          quaternary: true,
          type: 'error',
          // 踢自己的会话置灰:防误把自己下线
          disabled: isSelf,
          onClick: () => kick(r),
        },
        () => (isSelf ? t('session.self') : t('session.kick')),
      )
    },
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <SmartTable
    ref="tableRef"
    :columns="columns"
    :fetcher="fetcher"
    :toolbar="TABLE_TOOLBAR"
    :search="{ container: 'table' }"
    :filter-serializer="passthroughFilterSerializer"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-session"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
  </SmartTable>
</template>
