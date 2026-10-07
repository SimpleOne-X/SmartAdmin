<script setup lang="ts">
// 执行记录详情抽屉里的「各次尝试」子表:同一次触发(fireInstanceId)下的首跑与各次重试。
// 一次触发的尝试数个位数,静态数据模式即可;窄档的卡片列表是 SmartTable 内置的(card-on-narrow)。
// 这是嵌在抽屉里的小表,不套整页列表的工具栏 / 条件构造器标准(同 UserPicker,见 listSearch.spec.ts 的 EMBEDDED)。
import { h } from 'vue'
import { NTag } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import { fmtDateTime } from '#/utils/format'
import type { SysJobLog } from '#/types/api'
import { isAlive, runStatusLabel, runStatusTagType } from '../runStatus'

defineProps<{ rows: SysJobLog[]; loading?: boolean }>()
const { t } = useI18n()

const columns: SmartTableColumn<SysJobLog>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    title: () => t('job.log.retryIndex'),
    key: 'retryIndex',
    width: 110,
    render: r =>
      r.retryIndex === 0 ? t('job.log.firstTry') : t('job.log.retryN', { n: r.retryIndex }),
  },
  {
    title: () => t('job.log.runStatus'),
    key: 'runStatus',
    width: 90,
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: runStatusTagType[r.runStatus] }, () =>
        runStatusLabel(t, r.runStatus),
      ),
  },
  { title: () => t('job.log.startTime'), key: 'startTime', render: r => fmtDateTime(r.startTime) },
  {
    title: () => t('job.log.elapsed'),
    key: 'elapsedMs',
    width: 100,
    render: r => (isAlive(r) ? '—' : `${r.elapsedMs} ms`),
  },
]
</script>

<template>
  <SmartTable
    :columns="columns"
    :data="rows"
    row-key="id"
    :loading="loading"
    :toolbar="false"
    :pagination="false"
    card-on-narrow
  />
</template>
