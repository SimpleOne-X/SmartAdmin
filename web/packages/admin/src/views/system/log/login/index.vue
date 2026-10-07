<script setup lang="ts">
// 登录日志 = 只读 SmartTable:列驱动搜索(账号/结果)+ 分页;success 列显式红/绿 typeMap 保证「失败=红」。
// 工具栏一枚「清空」(硬删,不可恢复)走 useConfirm 二次确认(对齐 simpleadmin 日志页)。
import { h, ref } from 'vue'
import { NButton, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import { useConfirm } from '#/composables/useConfirm'
import { logApi } from '#/api'
import { translateError } from '#/utils/error'
import { fmtDateTime } from '#/utils/format'
import {
  SEARCH_ACTIONS,
  createFlatFilterSerializer,
  deriveHeaderFilters,
} from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import { uaSummary } from '#/utils/ua'
import type { SysLoginLog } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()
const tableRef = ref<SmartTableInst<SysLoginLog>>()

// 日期区间:条件构造器里是同一字段的两行(大于等于 + 小于等于),序列化器合并成 [起, 止],
// api 层 splitRange 再拆成 StartTime/EndTime 并给结束日补 23:59:59。
const filterSerializer = createFlatFilterSerializer({ ranges: ['createTime'] })

const columns: SmartTableColumn<SysLoginLog>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { key: 'account', title: () => t('log.account'), search: { actions: SEARCH_ACTIONS.fuzzy } },
  {
    key: 'success',
    title: () => t('log.result'),
    tag: true,
    search: { actions: SEARCH_ACTIONS.exact },
    options: [
      { label: () => t('log.success'), value: true, tagType: 'success' },
      { label: () => t('log.failed'), value: false, tagType: 'error' },
    ],
  },
  // 失败原因:裸渲染的 40004 对排查毫无用处——密码错/被锁/被停用三种情况的处置动作完全不同(改密/解锁/启用)。
  // 复用同列的 tag+options 机制翻成人话,文案直接指向已有的 error.auth.* 键(与登录页弹的提示同源)。
  // AuthService 实际只会落 0 与 40001–40005 这几个码;未命中的码由 SmartTable 回落成原始数字。
  {
    key: 'resultCode',
    title: () => t('log.resultCode'),
    width: 175,
    tag: true,
    options: [
      { label: () => t('log.success'), value: 0, tagType: 'success' },
      { label: () => t('error.auth.passwordWrong'), value: 40001, tagType: 'error' },
      { label: () => t('error.auth.captchaExpired'), value: 40002, tagType: 'error' },
      { label: () => t('error.auth.captchaWrong'), value: 40003, tagType: 'error' },
      { label: () => t('error.auth.accountLocked'), value: 40004, tagType: 'error' },
      { label: () => t('error.auth.accountDisabled'), value: 40005, tagType: 'error' },
    ],
  },
  // 姓名:后端按 userId 回填;登录失败/账号不存在的行无姓名 → 回落账号
  {
    key: 'name',
    title: () => t('log.name'),
    ellipsis: { tooltip: true },
    render: r => r.name || r.account || '—',
  },
  // IP 与时间用等宽 / 弱化色(.mono .muted .num 是全局工具类)
  {
    key: 'ip',
    title: () => t('log.ip'),
    render: r => h('span', { class: 'mono muted' }, r.ip || '—'),
  },
  // 设备:UA 解析成「浏览器 · 系统」(全未识别回落原始串);过长截断 + tooltip
  {
    key: 'device',
    title: () => t('log.device'),
    ellipsis: { tooltip: true },
    render: r => uaSummary(r.userAgent),
  },
  // 时间范围:"上周谁登录失败了"是登录日志最主要的用法
  {
    key: 'createTime',
    title: () => t('common.createTime'),
    render: r =>
      h('span', { class: 'num muted', style: 'white-space: nowrap' }, fmtDateTime(r.createTime)),
    search: { type: 'daterange', actions: SEARCH_ACTIONS.dayRange },
  },
]
deriveHeaderFilters(columns)

function clearLogs() {
  confirm({
    type: 'error',
    content: t('log.clearLoginConfirm'),
    action: () => logApi.loginClear(),
    successMsg: t('log.cleared'),
  }).then(ok => {
    if (ok) tableRef.value?.refresh()
  })
}
</script>

<template>
  <SmartTable
    ref="tableRef"
    :columns="columns"
    :fetcher="logApi.loginPage"
    :search="{ container: 'table' }"
    :filter-serializer="filterSerializer"
    :toolbar="TABLE_TOOLBAR"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-log-login"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button v-auth="'DELETE:/api/v1/sys/log/login'" type="error" secondary @click="clearLogs">
        <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
        {{ t('log.clear') }}
      </n-button>
    </template>
  </SmartTable>
</template>
