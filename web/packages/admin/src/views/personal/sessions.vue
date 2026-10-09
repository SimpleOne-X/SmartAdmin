<script setup lang="ts">
// 我的会话(个人视角的"登录设备"):[ActiveSession] 人人可用,静态路由不进菜单(理由同 notice)。
// 数据量受会话并发上限约束(个位数)→ onMounted 拉一次,交给 SmartTable 的静态数据模式
// (前端分页 / 前端求值搜索 / 窄档卡片列表都是它内置的)。
// 踢当前设备置灰:那等于自杀,请走"退出登录";管理端全量视角在 system/session。
import { h, onMounted, ref } from 'vue'
import { NButton, NTag, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import { personalApi } from '#/api'
import type { MySessionItem } from '#/types/api'
import { uaSummary } from '#/utils/ua'
import { useConfirm } from '#/composables/useConfirm'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'

import { deriveHeaderFilters } from '#/utils/tableFilter'
const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()

const loading = ref(true)
const rows = ref<MySessionItem[]>([])

async function load() {
  loading.value = true
  try {
    rows.value = await personalApi.sessions()
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}
onMounted(load)

function kick(r: MySessionItem) {
  confirm({
    type: 'warning',
    content: t('session.kickMineConfirm'),
    action: () => personalApi.kickSession(r.sessionId),
    successMsg: t('session.kicked'),
  }).then(ok => {
    if (ok) load()
  })
}

const columns: SmartTableColumn<MySessionItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    // 搜索按原始 UA 字符串「包含」求值(输入 Chrome / Windows 都能命中);单元格显示解析后的「浏览器 · 系统」
    key: 'userAgent',
    title: () => t('session.device'),
    width: 240,
    search: {},
    render: r =>
      h('div', { class: 'device-cell' }, [
        h('span', { class: 'device-text', title: uaSummary(r.userAgent) }, uaSummary(r.userAgent)),
        r.isCurrent
          ? h(NTag, { type: 'success', size: 'small', bordered: false }, () => t('session.current'))
          : null,
      ]),
  },
  { key: 'ip', title: () => t('session.ip'), width: 140, search: {}, render: r => r.ip || '—' },
  {
    key: 'loginTime',
    title: () => t('session.loginTime'),
    width: 180,
    render: r => fmtDateTime(r.loginTime),
  },
  {
    key: 'expiresAt',
    title: () => t('session.expiresAt'),
    width: 180,
    render: r => fmtDateTime(r.expiresAt),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 110,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(
        NButton,
        {
          size: 'small',
          quaternary: true,
          type: 'error',
          disabled: r.isCurrent,
          onClick: () => kick(r),
        },
        () => t('session.kick'),
      ),
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <!-- .fill-page:高度链在 styles/layout.css,卡片吃满内容区,滚动只发生在表体里。 -->
  <div class="fill-page">
    <SmartTable
      :columns="columns"
      :data="rows"
      row-key="sessionId"
      :loading="loading"
      :toolbar="TABLE_TOOLBAR"
      :search="{ container: 'table' }"
      fill-height
      :min-row-height="TABLE_MIN_ROW_HEIGHT"
      card-on-narrow
      storage-key="personal-sessions"
    >
      <template #pagination-prefix="{ itemCount }">
        <TableTotal :count="itemCount" />
      </template>
    </SmartTable>
  </div>
</template>

<style scoped>
:deep(.device-cell) {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
/* 设备名放不下时收成省略号,「当前设备」标签始终完整显示 */
:deep(.device-text) {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
:deep(.device-cell .n-tag) {
  flex: none;
}
</style>
