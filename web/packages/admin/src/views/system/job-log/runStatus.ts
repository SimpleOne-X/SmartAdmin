// 执行记录的「运行状态」共用件:主表、详情抽屉、尝试子表(components/AttemptTable.vue)三处用同一套。
import { JobRunStatus, type SysJobLog } from '#/types/api'

/** 运行中(endTime 空)的行还没有耗时;已结束的显示毫秒。 */
export const isAlive = (r: SysJobLog) => r.endTime == null

export const runStatusTagType = {
  [JobRunStatus.Running]: 'info',
  [JobRunStatus.Success]: 'success',
  [JobRunStatus.Failed]: 'error',
  [JobRunStatus.Timeout]: 'warning',
  [JobRunStatus.Cancelled]: 'default',
  [JobRunStatus.Skipped]: 'default',
} as const

const RUN_STATUS_KEY: Record<number, string> = {
  1: 'running',
  2: 'success',
  3: 'failed',
  4: 'timeout',
  5: 'cancelled',
  6: 'skipped',
}

/** 状态文案;`t` 由调用方传入(这里不绑定 i18n 实例)。 */
export function runStatusLabel(t: (key: string) => string, s: JobRunStatus): string {
  const key = RUN_STATUS_KEY[s]
  return key ? t(`job.log.${key}`) : String(s)
}
