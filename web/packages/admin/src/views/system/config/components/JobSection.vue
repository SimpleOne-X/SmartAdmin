<script setup lang="ts">
// 定时任务:执行日志保留天数 + 失败告警邮箱。
// 预览:一条时间轴标出日志保留到哪天,再给一封失败告警邮件的样子(收件人读草稿);不填邮箱就不发。
import { computed } from 'vue'
import { NDynamicTags, NInputNumber } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { JOB_ALERT_KEY, JOB_RETENTION_KEY } from '../groups'
import { useConfigDraft, useDraftFields } from '../draft'
import { useConfigLayout } from '../layoutMode'
import { vFlash } from '../flash'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'

const PRESETS = [7, 30, 90, 180]
/** 时间轴的总跨度(天);超过的按满格画 */
const AXIS_DAYS = 180

const { t } = useI18n()
const { values } = useConfigDraft()
const { num, str } = useDraftFields()
const { ctl, nar } = useConfigLayout()
const days = num(JOB_RETENTION_KEY, 30)
const emails = str(JOB_ALERT_KEY)
const recipients = computed({
  get: () =>
    emails.value
      .split(',')
      .map(e => e.trim())
      .filter(Boolean),
  set: (list: string[]) => {
    emails.value = [...new Set(list.map(e => e.trim()).filter(Boolean))].join(',')
  },
})
const siteTitle = computed(() => values['sys.site.title']?.trim() || 'SmartAdmin')
const keepWidth = computed(() => `${Math.min(100, (Math.max(days.value, 1) / AXIS_DAYS) * 100)}%`)
</script>

<template>
  <SectionLayout
    :title="t('config.tab.job')"
    :desc="t('config.job.desc')"
    :preview-title="t('config.job.preview')"
    layout="slim"
    preview-align="start"
  >
    <CfgGroup :title="t('config.job.groupLog')">
      <CfgRow
        :label="t('config.job.logRetentionDays')"
        :hint="t('config.job.logRetentionHint')"
        :keys="[JOB_RETENTION_KEY]"
      >
        <span class="presets">
          <button
            v-for="d in PRESETS"
            :key="d"
            type="button"
            :class="['chip', { lg: nar }]"
            :aria-pressed="days === d"
            @click="days = d"
          >
            {{ d }}
          </button>
        </span>
        <n-input-number
          v-model:value="days"
          class="num"
          :size="ctl"
          :min="1"
          :show-button="false"
          :input-props="{ 'aria-label': t('config.job.logRetentionDays') }"
        />
        <span class="unit">{{ t('config.security.unit.days') }}</span>
      </CfgRow>
    </CfgGroup>
    <CfgGroup :title="t('config.job.groupAlert')">
      <CfgRow
        :label="t('config.job.alertEmails')"
        :hint="t('config.job.alertEmailsHint')"
        :keys="[JOB_ALERT_KEY]"
        tall
        class="mails"
      >
        <n-dynamic-tags v-model:value="recipients" size="small" data-testid="alert-emails" />
      </CfgRow>
    </CfgGroup>

    <template #preview>
      <div v-flash="days" class="card tl">
        <div class="tl-text">{{ t('config.job.previewRetention', { days }) }}</div>
        <div class="bar">
          <div class="keep" :style="{ width: keepWidth }">
            <span v-if="days >= 20">{{ t('config.job.previewKeep', { days }) }}</span>
          </div>
        </div>
        <div class="axis">
          <span>{{ t('config.job.previewAxisStart', { days: AXIS_DAYS }) }}</span>
          <span>{{ t('config.job.previewToday') }}</span>
        </div>
      </div>
      <div v-flash="recipients.join(',')" class="card mail">
        <template v-if="recipients.length">
          <div class="mh">
            <span>
              {{ t('config.job.previewTo') }}
              <b>{{ recipients.join(t('config.preview.sep')) }}</b>
            </span>
            <span>
              {{ t('config.job.previewSubject') }}
              <b>{{ t('config.job.previewSubjectText', { site: siteTitle }) }}</b>
            </span>
          </div>
          <div class="mb">
            {{ t('config.job.previewBody') }}
            <div class="err">TimeoutException: {{ t('config.job.previewError') }}</div>
          </div>
        </template>
        <div v-else class="none">{{ t('config.job.previewNoAlert') }}</div>
      </div>
    </template>
  </SectionLayout>
</template>

<style scoped>
.num {
  width: 72px;
}
.num :deep(input) {
  text-align: right;
}
.unit {
  flex: none;
  min-width: 32px;
  font-size: 13px;
  color: var(--text-2);
}
.presets {
  display: inline-flex;
  flex: none;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 5px;
}
/* 保留天数的预设小标签:外观是可勾选 Tag(未选玻璃底、选中强调色实底) */
.chip {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 34px;
  height: 24px;
  padding: 0 10px;
  border: 0;
  border-radius: 6px;
  background: var(--glass-strong);
  font: inherit;
  font-size: 14px;
  color: var(--text-2);
  cursor: pointer;
}
.chip.lg {
  min-height: 32px;
}
.chip[aria-pressed='true'] {
  background: var(--acc-solid);
  color: var(--on-acc);
}
.chip:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 1px;
}
.mails :deep(.ctl) {
  flex: 1.4 1 0;
}
.mails :deep(.n-dynamic-tags) {
  justify-content: flex-end;
}
.card {
  padding: 14px;
  border-radius: var(--radius-lg);
  background: var(--cfg-card);
  font-size: 14px;
}
.tl-text {
  margin-bottom: 12px;
}
.bar {
  position: relative;
  height: 22px;
  overflow: hidden;
  border-radius: 6px;
  background: var(--glass-strong);
}
.keep {
  position: absolute;
  top: 0;
  right: 0;
  bottom: 0;
  display: grid;
  min-width: 4px;
  place-items: center;
  border-radius: 6px;
  background: var(--acc-solid);
  color: var(--on-acc);
  font-size: 12px;
  white-space: nowrap;
  transition: width 0.25s var(--ease);
}
@media (prefers-reduced-motion: reduce) {
  .keep {
    transition: none;
  }
}
.axis {
  display: flex;
  justify-content: space-between;
  margin-top: 6px;
  font-size: 12.5px;
  color: var(--text-3);
}
.mh {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--separator);
  font-size: 13px;
  color: var(--text-3);
}
.mh b {
  font-weight: 500;
  color: var(--text-1);
  word-break: break-all;
}
.mb {
  padding-top: 10px;
  line-height: 1.6;
}
.err {
  margin-top: 8px;
  padding: 6px 10px;
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--err) 10%, transparent);
  font-family: var(--font-mono);
  font-size: 12.5px;
  color: var(--err);
}
.none {
  color: var(--text-3);
}
</style>
