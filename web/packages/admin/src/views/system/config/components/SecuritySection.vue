<script setup lang="ts">
// 安全策略:登录保护、会话、密码、请求限流。后端经 ISecurityPolicyProvider 读这些键强制执行,保存即生效。
// 设置项多,按 3:2 分:左边两列分组列表,右边把草稿翻成"用户会遇到的规则",再给一个密码试填框,
// 当场看出新规则下什么样的密码能过。
import { computed, ref } from 'vue'
import { NInputNumber, NSwitch, NTooltip } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { humanizeMinutes, securityRules } from '#/lib/securityRules'
import { CAPTCHA_TYPE_KEY, SECURITY_NUMBERS } from '../groups'
import { useConfigDraft, useDraftFields } from '../draft'
import { vFlash } from '../flash'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'
import CfgSegment from './CfgSegment.vue'

const { t } = useI18n()
const { values } = useConfigDraft()
const { num, bool, str } = useDraftFields()

const s = (k: string) => `sys.security.${k}`
/** 'loginLock.maxFailCount' → 数值绑定 + 下限 + 单位 */
const numField = (k: string, unit: string) => ({
  key: s(k),
  label: t(`config.security.${k}`),
  unit: t(`config.security.unit.${unit}`),
  min: SECURITY_NUMBERS[s(k)],
  model: num(s(k), SECURITY_NUMBERS[s(k)]),
})

const lockCount = numField('loginLock.maxFailCount', 'times')
const lockMinutes = numField('loginLock.lockMinutes', 'minutes')
const captchaOn = bool(s('captcha.enabled'))
const captchaType = str(CAPTCHA_TYPE_KEY)
const captchaOptions = computed(() =>
  (['char', 'path', 'math'] as const).map(v => ({
    value: v,
    label: t(`config.security.captcha.types.${v}`),
  })),
)
const captchaTypeModel = computed({
  get: () => (captchaType.value || 'char') as 'char' | 'path' | 'math',
  set: v => (captchaType.value = v),
})
const totp = bool(s('totp.enabled'))
const totpSuper = bool(s('totp.requireForSuperAdmin'))
const mfa = bool(s('mfa.enabled'))

const access = numField('session.accessMinutes', 'minutes')
const refresh = numField('session.refreshMinutes', 'minutes')

const minLength = numField('password.minLength', 'chars')
const REQS = ['Upper', 'Lower', 'Digit', 'Special'] as const
const reqs = REQS.map(r => ({
  key: s(`password.require${r}`),
  label: t(`config.security.password.chip${r}`),
  name: t(`config.security.password.require${r}`),
  model: bool(s(`password.require${r}`)),
}))
const expireDays = numField('password.expireDays', 'days')
const history = numField('password.historyCount', 'times')

const rateOn = bool(s('rateLimit.enabled'))
const rate = [
  numField('rateLimit.windowSeconds', 'seconds'),
  numField('rateLimit.permitPerWindow', 'times'),
  numField('rateLimit.authPermitPerWindow', 'times'),
]

const duration = (minutes: number) => {
  const d = humanizeMinutes(minutes)
  return t(d.key, { n: d.n })
}
const rules = computed(() =>
  securityRules(values, {
    duration: d => t(d.key, { n: d.n }),
    requirement: code => t(`config.preview.req.${code}`),
    captchaType: code => t(`config.security.captcha.types.${code}`),
    join: items => items.join(t('config.preview.sep')),
  }),
)

// 密码试填:只在本页做规则比对,不提交
const tryPassword = ref('Admin123')
const checks = computed(() => {
  const pw = tryPassword.value
  const list = [
    {
      label: t('config.security.tryMin', { n: minLength.model.value }),
      ok: pw.length >= minLength.model.value,
    },
  ]
  const test: Record<(typeof REQS)[number], RegExp> = {
    Upper: /[A-Z]/,
    Lower: /[a-z]/,
    Digit: /\d/,
    Special: /[^A-Za-z0-9]/,
  }
  REQS.forEach((r, i) => {
    if (reqs[i].model.value)
      list.push({ label: t(`config.preview.req.${r.toLowerCase()}`), ok: test[r].test(pw) })
  })
  return list
})
const passes = computed(() => checks.value.every(c => c.ok))
</script>

<template>
  <SectionLayout
    :title="t('config.tab.security')"
    :desc="t('config.security.desc')"
    :preview-title="t('config.security.preview')"
    layout="wide"
    preview-align="start"
  >
    <div class="cols">
      <div class="col">
        <CfgGroup :title="t('config.security.groupLogin')">
          <CfgRow
            v-for="f in [lockCount, lockMinutes]"
            :key="f.key"
            :label="f.label"
            :hint="f === lockCount ? t('config.security.zeroNoLock') : undefined"
            :keys="[f.key]"
          >
            <n-input-number
              v-model:value="f.model.value"
              class="num"
              size="small"
              :min="f.min"
              :show-button="false"
              :input-props="{ 'aria-label': f.label }"
            />
            <span class="unit">{{ f.unit }}</span>
          </CfgRow>
          <CfgRow :label="t('config.security.captcha.enabled')" :keys="[s('captcha.enabled')]">
            <n-switch
              v-model:value="captchaOn"
              :aria-label="t('config.security.captcha.enabled')"
            />
          </CfgRow>
          <CfgRow
            :label="t('config.security.captcha.type')"
            :keys="[CAPTCHA_TYPE_KEY]"
            :disabled="!captchaOn"
            sub
          >
            <CfgSegment
              v-model="captchaTypeModel"
              :options="captchaOptions"
              :label="t('config.security.captcha.type')"
            />
          </CfgRow>
          <CfgRow :keys="[s('totp.enabled')]">
            <template #label>
              {{ t('config.security.totp.enabled') }}
              <n-tooltip :style="{ maxWidth: '320px' }">
                <template #trigger>
                  <button type="button" class="tip" :aria-label="t('config.security.totp.hint')">
                    <AppIcon icon="ph:question" :size="12" />
                  </button>
                </template>
                {{ t('config.security.totp.hint') }}
              </n-tooltip>
            </template>
            <n-switch v-model:value="totp" :aria-label="t('config.security.totp.enabled')" />
          </CfgRow>
          <CfgRow
            :label="t('config.security.totp.requireForSuperAdmin')"
            :keys="[s('totp.requireForSuperAdmin')]"
            :disabled="!totp"
            sub
          >
            <n-switch
              v-model:value="totpSuper"
              :aria-label="t('config.security.totp.requireForSuperAdmin')"
            />
          </CfgRow>
          <CfgRow :keys="[s('mfa.enabled')]">
            <template #label>
              {{ t('config.security.mfa.enabled') }}
              <n-tooltip :style="{ maxWidth: '320px' }">
                <template #trigger>
                  <button type="button" class="tip" :aria-label="t('config.security.sms.hint')">
                    <AppIcon icon="ph:question" :size="12" />
                  </button>
                </template>
                {{ t('config.security.sms.hint') }}
              </n-tooltip>
            </template>
            <n-switch v-model:value="mfa" :aria-label="t('config.security.mfa.enabled')" />
          </CfgRow>
        </CfgGroup>

        <CfgGroup :title="t('config.security.groupSession')">
          <CfgRow
            v-for="f in [access, refresh]"
            :key="f.key"
            :label="f.label"
            :hint="`= ${duration(f.model.value)}`"
            :keys="[f.key]"
          >
            <n-input-number
              v-model:value="f.model.value"
              class="num"
              size="small"
              :min="f.min"
              :show-button="false"
              :input-props="{ 'aria-label': f.label }"
            />
            <span class="unit">{{ f.unit }}</span>
          </CfgRow>
        </CfgGroup>
      </div>

      <div class="col">
        <CfgGroup :title="t('config.security.password.title')">
          <CfgRow :label="minLength.label" :keys="[minLength.key]">
            <n-input-number
              v-model:value="minLength.model.value"
              class="num"
              size="small"
              :min="minLength.min"
              :show-button="false"
              :input-props="{ 'aria-label': minLength.label }"
            />
            <span class="unit">{{ minLength.unit }}</span>
          </CfgRow>
          <CfgRow :label="t('config.security.password.mustContain')" :keys="reqs.map(r => r.key)">
            <span class="chips">
              <button
                v-for="r in reqs"
                :key="r.key"
                type="button"
                class="chip"
                :aria-pressed="r.model.value"
                :aria-label="r.name"
                :title="r.name"
                @click="r.model.value = !r.model.value"
              >
                {{ r.label }}
              </button>
            </span>
          </CfgRow>
          <CfgRow
            v-for="f in [expireDays, history]"
            :key="f.key"
            :label="f.label"
            :hint="t(f === expireDays ? 'config.security.zeroNever' : 'config.security.zeroOff')"
            :keys="[f.key]"
          >
            <n-input-number
              v-model:value="f.model.value"
              class="num"
              size="small"
              :min="f.min"
              :show-button="false"
              :input-props="{ 'aria-label': f.label }"
            />
            <span class="unit">{{ f.unit }}</span>
          </CfgRow>
        </CfgGroup>

        <CfgGroup :title="t('config.security.groupRate')">
          <CfgRow :label="t('config.security.rateLimit.enabled')" :keys="[s('rateLimit.enabled')]">
            <n-switch v-model:value="rateOn" :aria-label="t('config.security.rateLimit.enabled')" />
          </CfgRow>
          <CfgRow
            v-for="f in rate"
            :key="f.key"
            :label="f.label"
            :keys="[f.key]"
            :disabled="!rateOn"
            sub
          >
            <n-input-number
              v-model:value="f.model.value"
              class="num"
              size="small"
              :min="f.min"
              :show-button="false"
              :input-props="{ 'aria-label': f.label }"
            />
            <span class="unit">{{ f.unit }}</span>
          </CfgRow>
        </CfgGroup>
      </div>
    </div>

    <template #preview>
      <ul class="rules" data-testid="security-rules">
        <li
          v-for="r in rules"
          :key="r.icon"
          v-flash="`${r.on}|${t(r.key, r.params ?? {})}`"
          :class="{ off: !r.on }"
        >
          <span class="ic"><AppIcon :icon="r.icon" :size="14" /></span>
          <span class="txt">{{ t(r.key, r.params ?? {}) }}</span>
          <span v-if="!r.on" class="sr-only">{{ t('config.preview.off') }}</span>
        </li>
      </ul>
      <div class="try">
        <label>
          <span>{{ t('config.security.tryLabel') }}</span>
          <input v-model="tryPassword" autocomplete="off" spellcheck="false" />
        </label>
        <ul>
          <li v-for="c in checks" :key="c.label" :class="{ ok: c.ok }">{{ c.label }}</li>
          <li :class="['verdict', { ok: passes }]">
            {{ passes ? t('config.security.tryPass') : t('config.security.tryFail') }}
          </li>
        </ul>
      </div>
    </template>
  </SectionLayout>
</template>

<style scoped>
.cols {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
  align-items: start;
}
.col {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-width: 0;
}
.num {
  width: 96px;
}
.num :deep(input) {
  text-align: right;
}
.unit {
  flex: none;
  width: 32px;
  font-size: var(--font-size-sm);
  color: var(--color-text-secondary);
}
.tip {
  display: inline-grid;
  place-items: center;
  width: 16px;
  height: 16px;
  padding: 0;
  border: 0;
  border-radius: 50%;
  background: var(--color-fill);
  color: var(--color-text-secondary);
  cursor: help;
}
.tip:focus-visible {
  outline: 2px solid var(--color-primary);
}
.chips {
  display: inline-flex;
  flex: none;
  gap: 5px;
}
.chip {
  padding: 2px 8px;
  border: 0;
  border-radius: 6px;
  background: var(--color-fill);
  font:
    var(--font-size-sm) ui-monospace,
    SFMono-Regular,
    Menlo,
    Consolas,
    monospace;
  line-height: 20px;
  color: var(--color-text-secondary);
  white-space: nowrap;
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
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}
.rules {
  margin: 0;
  padding: 2px 0;
  list-style: none;
  border-radius: 12px;
  background: var(--color-bg-container);
}
.rules li {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 7px 14px;
  border-radius: 8px;
  font-size: var(--font-size-base);
  line-height: 1.5;
}
.rules li + li::before {
  content: '';
  position: absolute;
  top: 0;
  left: 46px;
  right: 0;
  border-top: 0.5px solid var(--color-border);
}
.ic {
  display: grid;
  flex: none;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 6px;
  color: #fff;
  background: var(--color-primary);
}
.rules li.off .ic {
  color: var(--color-text-tertiary);
  background: var(--color-fill);
}
.rules li.off .txt {
  color: var(--color-text-tertiary);
}
.try {
  flex: none;
  padding: 10px 14px;
  border-radius: 12px;
  background: var(--color-bg-container);
}
.try label {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: var(--font-size-base);
  color: var(--color-text-secondary);
}
.try input {
  flex: 1;
  min-width: 0;
  height: 28px;
  padding: 0 10px;
  border: 0;
  border-radius: 7px;
  outline: none;
  background: var(--color-fill);
  color: var(--color-text-primary);
  font: inherit;
}
.try input:focus-visible {
  box-shadow: 0 0 0 2px var(--color-primary);
}
.try ul {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 14px;
  margin: 8px 0 0;
  padding: 0;
  list-style: none;
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.try li::before {
  content: '○ ';
}
.try li.ok {
  color: var(--color-success);
}
.try li.ok::before {
  content: '● ';
}
.try li.verdict {
  margin-left: auto;
  color: var(--color-danger);
}
.try li.verdict::before,
.try li.verdict.ok::before {
  content: '';
}
.try li.verdict.ok {
  color: var(--color-success);
}
@media (max-width: 1280px) {
  .cols {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
