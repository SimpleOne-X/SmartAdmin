<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NInput,
  NQrCode,
  NSteps,
  NStep,
  useMessage,
} from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useMediaQuery } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { mfaApi } from '#/api'
import { translateError } from '#/utils/error'
import { triggerBlobDownload } from '#/utils/download'
import { useUserStore } from '#/stores/user'
import { useAuthStore } from '#/stores/auth'
import { useSite } from '#/composables/useSite'
import { resetRouter } from '#/router'
import SmartLogo from '#/components/SmartLogo.vue'
import LoginDesk from '#/views/login/components/LoginDesk.vue'
import { takeRecoveryCodes } from './bindComplete'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const message = useMessage()
const userStore = useUserStore()
const authStore = useAuthStore()
const { site, appVersion } = useSite()
const year = new Date().getFullYear()
// 窄屏(≤ 480)三个步骤标题排不下:只给当前步留标题,其余只留序号
const narrow = useMediaQuery('(max-width: 480px)')

const queryMode = route.query.mode === 'recovery' ? 'recovery' : 'bind'
const mode = ref<'bind' | 'recovery'>(queryMode)
const step = ref(1)
const loading = ref(false)
const completed = ref(false)

const queryAccount = typeof route.query.account === 'string' ? route.query.account : ''
const knownAccount = queryAccount || userStore.userInfo?.account || ''
const bind = reactive({
  account: knownAccount,
  currentPassword: '',
  bindChallengeId: '',
  otpauthUri: '',
  seed: '',
  totpCode: '',
  recoveryCodes: [] as string[],
})
const recovery = reactive({
  account: knownAccount,
  currentPassword: '',
  recoveryCode: '',
})
const canStart = computed(() => !!bind.account.trim() && !!bind.currentPassword)

async function copy(value: string) {
  try {
    await navigator.clipboard.writeText(value)
    message.success(t('mfa.copied'))
  } catch {
    message.error(t('mfa.copyFailed'))
  }
}

/** 恢复码下载到本地 txt(一次性展示后用户可离线保管)。 */
function downloadRecoveryCodes() {
  const account = bind.account.trim() || 'account'
  const lines = [
    t('mfa.recoveryDownloadHeader', { account }),
    '',
    ...bind.recoveryCodes,
    '',
    t('mfa.recoveryDownloadFooter'),
  ]
  const blob = new Blob([lines.join('\n')], { type: 'text/plain;charset=utf-8' })
  const stamp = new Date().toISOString().slice(0, 10)
  const safe = account.replace(/[^\w.\-@]+/g, '_')
  triggerBlobDownload(blob, `smart-recovery-codes-${safe}-${stamp}.txt`)
  message.success(t('mfa.recoveryDownloaded'))
}

async function startBind() {
  if (!canStart.value) {
    message.warning(t('mfa.bindRequired'))
    return
  }
  loading.value = true
  try {
    const result = await mfaApi.bindStart({
      account: bind.account.trim(),
      currentPassword: bind.currentPassword,
    })
    bind.bindChallengeId = result.bindChallengeId ?? ''
    bind.otpauthUri = result.otpauthUri ?? ''
    bind.seed = result.seed ?? ''
    if (!bind.bindChallengeId || !bind.otpauthUri || !bind.seed)
      throw new Error(t('mfa.bindStartResponseIncomplete'))
    bind.currentPassword = ''
    step.value = 2
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}

async function completeBind() {
  if (!bind.totpCode) {
    message.warning(t('mfa.codeRequired'))
    return
  }
  loading.value = true
  try {
    const result = await mfaApi.bindComplete({
      bindChallengeId: bind.bindChallengeId,
      totpCode: bind.totpCode,
    })
    const codes = takeRecoveryCodes(result)
    if (!codes) throw new Error(t('mfa.bindCompleteResponseIncomplete'))
    bind.recoveryCodes = codes
    bind.seed = ''
    bind.otpauthUri = ''
    bind.totpCode = ''
    completed.value = true
    step.value = 3
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}

async function submitRecovery() {
  if (!recovery.account || !recovery.currentPassword || !recovery.recoveryCode) {
    message.warning(t('mfa.recoveryRequired'))
    return
  }
  loading.value = true
  try {
    await mfaApi.recovery(recovery)
    recovery.currentPassword = ''
    recovery.recoveryCode = ''
    completed.value = true
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}

function backToLogin() {
  // 绑定页可能仍挂着旧登录会话;清会话后再进登录,避免守卫直接弹回壳子且不重建路由。
  resetRouter()
  authStore.reset()
  userStore.clear()
  router.replace('/login')
}

function switchMode() {
  if (mode.value === 'bind' && step.value === 3) {
    bind.recoveryCodes = []
    step.value = 1
  }
  completed.value = false
  mode.value = mode.value === 'bind' ? 'recovery' : 'bind'
}
</script>

<template>
  <LoginDesk :width="440" content-style="padding: 24px 26px 22px">
    <!-- 卡片上方的站点标志(取值顺序见 SmartLogo) -->
    <template #above>
      <div class="login-logo"><SmartLogo :size="56" /></div>
    </template>

    <!-- 卡片头:标题 + 右侧切换链接 -->
    <div class="mfa-header">
      <div class="mfa-title">
        {{ mode === 'bind' ? t('mfa.bindTitle') : t('mfa.recoveryTitle') }}
      </div>
      <n-button text type="primary" class="mfa-switch" @click="switchMode">
        {{ mode === 'bind' ? t('mfa.useRecovery') : t('mfa.backToBind') }}
      </n-button>
    </div>

    <template v-if="mode === 'bind'">
      <n-steps :current="step" size="small" class="mfa-steps">
        <n-step
          v-for="(key, i) in ['mfa.stepVerify', 'mfa.stepAuthenticator', 'mfa.stepRecovery']"
          :key="key"
          :title="!narrow || step === i + 1 ? t(key) : ''"
        />
      </n-steps>

      <n-form
        v-if="step === 1"
        label-placement="top"
        size="large"
        :show-feedback="false"
        @keyup.enter="startBind"
      >
        <n-alert type="info" :show-icon="true" class="mfa-alert">
          {{ t('mfa.bindHint') }}
        </n-alert>
        <n-form-item :label="t('mfa.account')">
          <n-input v-model:value="bind.account" autocomplete="username" />
        </n-form-item>
        <n-form-item :label="t('mfa.currentPassword')">
          <n-input
            v-model:value="bind.currentPassword"
            type="password"
            show-password-on="click"
            autocomplete="current-password"
          />
        </n-form-item>
        <n-button
          class="cta mfa-submit"
          type="primary"
          size="large"
          block
          :loading="loading"
          @click="startBind"
        >
          {{ t('mfa.startBind') }}
        </n-button>
      </n-form>

      <n-form
        v-else-if="step === 2"
        label-placement="top"
        size="large"
        :show-feedback="false"
        @keyup.enter="completeBind"
      >
        <n-alert type="info" :show-icon="true" class="mfa-alert">
          {{ t('mfa.scanSetupHint') }}
        </n-alert>
        <div class="mfa-qr-wrap">
          <!-- 全局 box-sizing: border-box 会让 NQrCode 的内边距挤掉画布(画布溢出白底),还原成 content-box -->
          <n-qr-code
            :value="bind.otpauthUri"
            :size="200"
            error-correction-level="M"
            style="box-sizing: content-box"
          />
        </div>
        <n-form-item :label="t('mfa.manualKey')">
          <n-input :value="bind.seed" readonly class="mfa-seed">
            <template #suffix>
              <n-button text type="primary" @click="copy(bind.seed)">
                {{ t('mfa.copy') }}
              </n-button>
            </template>
          </n-input>
        </n-form-item>
        <p class="mfa-manual-hint">{{ t('mfa.manualSetupHint') }}</p>
        <n-form-item :label="t('mfa.authenticatorCode')">
          <n-input v-model:value="bind.totpCode" :maxlength="6" autocomplete="one-time-code" />
        </n-form-item>
        <n-button
          class="cta mfa-submit"
          type="primary"
          size="large"
          block
          :loading="loading"
          @click="completeBind"
        >
          {{ t('mfa.completeBind') }}
        </n-button>
      </n-form>

      <div v-else class="recovery-codes">
        <n-alert type="warning" :show-icon="true" class="mfa-alert">
          {{ t('mfa.recoveryOnce') }}
        </n-alert>
        <div class="recovery-grid">
          <div v-for="code in bind.recoveryCodes" :key="code" class="recovery-code">
            {{ code }}
          </div>
        </div>
        <div class="recovery-actions">
          <n-button class="cta" type="primary" size="large" block @click="downloadRecoveryCodes">
            <template #icon><Icon icon="ph:download-simple" :width="18" /></template>
            {{ t('mfa.downloadCodes') }}
          </n-button>
          <n-button secondary size="large" block @click="copy(bind.recoveryCodes.join('\n'))">
            <template #icon><Icon icon="ph:copy" :width="18" /></template>
            {{ t('mfa.copyAll') }}
          </n-button>
          <n-button secondary size="large" block @click="backToLogin">
            {{ t('mfa.backToLogin') }}
          </n-button>
        </div>
      </div>
    </template>

    <n-form
      v-else-if="!completed"
      label-placement="top"
      size="large"
      :show-feedback="false"
      @keyup.enter="submitRecovery"
    >
      <n-alert type="warning" :show-icon="true" class="mfa-alert">
        {{ t('mfa.recoveryHint') }}
      </n-alert>
      <n-form-item :label="t('mfa.account')">
        <n-input v-model:value="recovery.account" autocomplete="username" />
      </n-form-item>
      <n-form-item :label="t('mfa.currentPassword')">
        <n-input
          v-model:value="recovery.currentPassword"
          type="password"
          show-password-on="click"
          autocomplete="current-password"
        />
      </n-form-item>
      <n-form-item :label="t('mfa.recoveryCode')">
        <n-input v-model:value="recovery.recoveryCode" autocomplete="one-time-code" />
      </n-form-item>
      <n-button
        class="cta mfa-submit"
        type="primary"
        size="large"
        block
        :loading="loading"
        @click="submitRecovery"
      >
        {{ t('mfa.submitRecovery') }}
      </n-button>
    </n-form>
    <template v-else>
      <n-alert type="success" :show-icon="true" class="mfa-alert">
        {{ t('mfa.recoveryDone') }}
      </n-alert>
      <n-button class="cta" type="primary" size="large" block @click="backToLogin">
        {{ t('mfa.backToLogin') }}
      </n-button>
    </template>

    <!-- 页脚:版权 + 构建期版本号(同登录页) -->
    <template #after>
      <p class="login-foot">
        <span>© {{ year }} {{ site.copyright || site.title }}</span>
        <span v-if="appVersion" class="login-foot-ver">v{{ appVersion }}</span>
      </p>
    </template>
  </LoginDesk>
</template>

<style scoped>
.login-logo {
  display: flex;
  justify-content: center;
  margin: 0 auto 22px;
}
.login-logo :deep(.site-logo) {
  border-radius: var(--radius-lg);
  box-shadow: 0 14px 40px -10px var(--signal-glow);
}
.mfa-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 18px;
}
.mfa-title {
  min-width: 0;
  font-size: 19px;
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.35;
  color: var(--text-1);
}
.mfa-switch {
  flex: none;
  margin-top: 2px;
}
.mfa-steps {
  margin-bottom: 18px;
}
.mfa-alert {
  margin-bottom: 16px;
}
.mfa-submit {
  margin-top: 4px;
}
/* 表单项之间的间距(表单关了反馈行,没有校验规则要显示) */
:deep(.n-form-item) {
  margin-bottom: 14px;
}
.mfa-qr-wrap {
  display: flex;
  justify-content: center;
  margin: 0 0 16px;
}
.mfa-seed :deep(input) {
  font-family: var(--font-mono);
}
.mfa-manual-hint {
  margin: -8px 0 14px;
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-3);
}
.recovery-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
  margin-bottom: 16px;
}
.recovery-code {
  padding: 8px 10px;
  border-radius: 4px;
  background: var(--fill);
  text-align: center;
  letter-spacing: 0.04em;
  font-family: var(--font-mono);
  font-size: 13.5px;
}
.recovery-actions {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.login-foot {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  margin: 22px 0 0;
  font-size: 13px;
  color: var(--text-3);
}
.login-foot-ver {
  opacity: 0.75;
}
@media (max-width: 480px) {
  .recovery-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
