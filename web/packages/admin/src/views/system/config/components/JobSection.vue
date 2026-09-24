<script setup lang="ts">
// 定时任务:执行日志保留天数 + 失败告警邮箱。
// 预览:一条时间轴标出日志保留到哪天,再给一封失败告警邮件的样子(收件人读草稿);不填邮箱就不发。
import { computed } from 'vue'
import { NDynamicTags, NInputNumber } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { JOB_ALERT_KEY, JOB_RETENTION_KEY } from '../groups'
import { useConfigDraft, useDraftFields } from '../draft'
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
            class="chip"
            :aria-pressed="days === d"
            @click="days = d"
          >
            {{ d }}
          </button>
        </span>
        <n-input-number
          v-model:value="days"
          class="num"
          size="small"
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
  font-size: var(--font-size-sm);
  color: var(--color-text-secondary);
}
.presets {
  display: inline-flex;
  gap: 4px;
}
.chip {
  min-width: 34px;
  padding: 2px 6px;
  border: 0;
  border-radius: 6px;
  background: var(--color-fill);
  font: inherit;
  font-size: var(--font-size-sm);
  line-height: 20px;
  color: var(--color-text-secondary);
  cursor: pointer;
}
.chip[aria-pressed='true'] {
  background: var(--color-primary);
  color: #fff;
}
.chip:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: 1px;
}
.mails :deep(.ctl) {
  flex: 1.4;
}
.mails :deep(.n-dynamic-tags) {
  justify-content: flex-end;
}
.card {
  border-radius: 12px;
  background: var(--color-bg-container);
}
.tl {
  padding: 14px 16px 12px;
}
.tl-text {
  margin-bottom: 8px;
  font-size: var(--font-size-base);
  font-weight: 500;
}
.bar {
  position: relative;
  height: 26px;
  overflow: hidden;
  border-radius: 7px;
  background: repeating-linear-gradient(-45deg, var(--color-fill) 0 6px, transparent 6px 12px);
}
.keep {
  position: absolute;
  top: 0;
  right: 0;
  bottom: 0;
  display: grid;
  place-items: center;
  border-radius: 7px;
  background: var(--color-primary);
  color: #fff;
  font-size: var(--font-size-sm);
  white-space: nowrap;
  transition: width 0.25s;
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
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.mail {
  overflow: hidden;
  font-size: var(--font-size-base);
}
.mh {
  display: grid;
  gap: 3px;
  padding: 10px 14px;
  border-bottom: 0.5px solid var(--color-border);
  font-size: var(--font-size-sm);
  color: var(--color-text-secondary);
}
.mh b {
  font-weight: 500;
  color: var(--color-text-primary);
}
.mb {
  padding: 12px 14px;
}
.err {
  margin-top: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--color-danger-bg);
  color: var(--color-danger);
  font:
    var(--font-size-sm) ui-monospace,
    SFMono-Regular,
    Menlo,
    Consolas,
    monospace;
}
.none {
  padding: 28px 16px;
  text-align: center;
  color: var(--color-text-secondary);
}
</style>
