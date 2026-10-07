<script setup lang="ts">
import { reactive, ref, computed, nextTick, onMounted, onBeforeUnmount, h } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NInput,
  NCheckbox,
  NModal,
  NDropdown,
  useMessage,
  type DropdownOption,
} from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { authApi, externalAuthApi, ApiError } from '#/api'
import type { LoginOutput } from '#/types/api'
import { useUserStore } from '#/stores/user'
import { useAuthStore } from '#/stores/auth'
import { useSite } from '#/composables/useSite'
import { resetRouter } from '#/router'
import { translateError } from '#/utils/error'
import {
  splitLoginProviders,
  PREVIEW_ALL_SSO_BRANDS,
  previewAllBrandProviders,
  claimWeComAutoLogin,
} from '#/utils/oauthBrand'
import SmartLogo from '#/components/SmartLogo.vue'
import { runtime } from '#/lib/runtime'
import BrandIcon from '#/components/oauth/BrandIcon.vue'

// 双栏布局下品牌区在左栏,卡内不重复 logo/标题,改用「欢迎回来」问候语(showGreeting);
// 版权页脚由外壳画在卡外(showFooter=false)。单栏时全部默认:卡内顶部是 logo + 站点标题,再是「登录」标题。
withDefaults(
  defineProps<{
    showLogo?: boolean
    showTitle?: boolean
    showGreeting?: boolean
    showFooter?: boolean
  }>(),
  {
    showLogo: true,
    showTitle: true,
    showGreeting: false,
    showFooter: true,
  },
)
// 登录成功、即将跳转时通知外壳播退场动画(true);跳转没发生时撤销(false),免得页面停在淡出态
const emit = defineEmits<{ leave: [on: boolean] }>()

const router = useRouter()
const route = useRoute()
const message = useMessage()
const { t } = useI18n()
const user = useUserStore()
const auth = useAuthStore()
const { site, appVersion, loadSite } = useSite()
const year = new Date().getFullYear()

/** 未绑定 SSO 后带回:登录成功后须确认再 claim(防静默抢绑) */
const pendingLink = computed(() =>
  typeof route.query.pendingLink === 'string' ? route.query.pendingLink : '',
)
const pendingProvider = computed(() =>
  typeof route.query.provider === 'string' ? route.query.provider : '',
)
const pendingDisplayName = computed(() =>
  typeof route.query.displayName === 'string' ? route.query.displayName : '',
)
const pendingConfirmShow = ref(false)
const pendingClaimBusy = ref(false)
const pendingClaimToken = ref('')

// 开发态(createSmartAdmin({ dev }))预填超管账号密码,免得每次手敲;生产留空。
// SSO pending-link 场景故意不预填密码:解绑后 GitHub 会落到本页,预填+一点就 claim 看起来像「SSO 直接进了」。
const model = reactive(
  runtime.dev && !pendingLink.value
    ? { account: 'superAdmin', password: 'Aa123456', remember: true }
    : {
        account: runtime.dev ? 'superAdmin' : '',
        password: '',
        remember: true,
      },
)
const loading = ref(false)

// 登录形态:账号密码 / 短信免密 / 短信二次验证(40009) / TOTP 二次验证(40018)
// 强制 MFA 未绑定(40020)用 Modal 引导,不切 mode,登录页默认也不常驻绑定链接
const mode = ref<'account' | 'sms' | 'mfa' | 'totp'>('account')

// 短信免密登录
const smsModel = reactive({ phone: '', code: '' })
// 二次验证挑战(40009 信令 args 下发)
const mfa = reactive({ challengeId: '', phoneMask: '', code: '' })
// TOTP 挑战(40018)
const totp = reactive({ challengeId: '', code: '' })
// 强制 MFA 但未绑定(40020):弹窗 + 带到 /mfa/bind 的账号
const bindRequiredShow = ref(false)
const bindAccount = ref('')

// 发码/重发共用倒计时(同一时刻只有一个发码入口可见)
const countdown = ref(0)
let timer: number | undefined
function startCountdown(s: number) {
  countdown.value = s
  window.clearInterval(timer)
  timer = window.setInterval(() => {
    countdown.value -= 1
    if (countdown.value <= 0) window.clearInterval(timer)
  }, 1000)
}
onBeforeUnmount(() => window.clearInterval(timer))

// 验证码:是否启用由匿名站点信息(sys.security.captcha.enabled)运行时驱动;启用才拉取并展示。
// 账号模式护登录,短信模式护发码(发码端点防脚本滥用)。
const captchaEnabled = ref(false)
const captchaId = ref('')
const captchaSvg = ref('')
const captchaCode = ref('')
const captchaType = ref('char')

// math 类型需算出结果再输入,提示语不同;其余类型统一“请输入验证码”。
const captchaHint = computed(() =>
  captchaType.value === 'math' ? t('login.captchaMathPlaceholder') : t('login.captchaPlaceholder'),
)

async function loadCaptcha() {
  try {
    const c = await authApi.captcha()
    captchaId.value = c.captchaId
    captchaSvg.value = c.svg
    captchaType.value = c.type || 'char'
  } catch {
    // 拉取失败不阻塞登录页渲染;点击图形可重试
  }
}

/** 验证码一次性消费:任何用掉票据的请求失败后必刷新,避免复用作废票据 */
async function refreshCaptchaAfterUse() {
  if (captchaEnabled.value) {
    captchaCode.value = ''
    await loadCaptcha()
  }
}

onMounted(async () => {
  // 站点信息全站共用(useSite 去重);验证码开关据此运行时驱动,启用才拉图。
  await loadSite()
  if (site.captchaEnabled) {
    captchaEnabled.value = true
    await loadCaptcha()
  }
  void loadSsoProviders()

  // SSO 回调带回的 TOTP 挑战:直接进入 totp 完成态
  const q = router.currentRoute.value.query
  const ch = typeof q.totpChallenge === 'string' ? q.totpChallenge : ''
  if (ch) {
    totp.challengeId = ch
    totp.code = ''
    mode.value = 'totp'
  }
})

// 第三方登录:默认后端 providers 驱动;PREVIEW_ALL_SSO_BRANDS 时铺全品牌图(图标验收)。
const ssoProviders = ref<{ code: string; displayName: string; icon?: string | null }[]>([])
const pendingProviderLabel = computed(() => {
  const code = pendingProvider.value
  if (!code) return t('oauth.thirdParty')
  const hit = ssoProviders.value.find(p => p.code === code)
  return hit?.displayName || code
})
const ssoDisplayList = computed(() =>
  PREVIEW_ALL_SSO_BRANDS ? previewAllBrandProviders() : ssoProviders.value,
)
// 预览全图标时不截断;正常模式 N=4 溢出
const ssoSplit = computed(() =>
  PREVIEW_ALL_SSO_BRANDS
    ? { visible: ssoDisplayList.value, overflow: [] as typeof ssoDisplayList.value }
    : splitLoginProviders(ssoDisplayList.value),
)
const ssoOverflowOptions = computed<DropdownOption[]>(() =>
  ssoSplit.value.overflow.map(p => ({
    key: p.code,
    label: () =>
      h(
        'span',
        {
          class: 'lf-sso-menu-item',
          style: { display: 'inline-flex', alignItems: 'center', gap: '8px' },
        },
        [h(BrandIcon, { code: p.code, icon: p.icon, size: 20 }), h('span', null, p.displayName)],
      ),
  })),
)
async function loadSsoProviders() {
  try {
    ssoProviders.value = await externalAuthApi.providers()
  } catch {
    // 未配置外部登录或拉取失败:整段 SSO 区不显(预览全图标模式仍会显示)
    return
  }
  // 企业微信客户端里直接走企业微信登录;SSO 带回来的认领 / 二次验证要留在本页完成
  const q = route.query
  if (
    !q.pendingLink &&
    !q.totpChallenge &&
    claimWeComAutoLogin({
      userAgent: navigator.userAgent,
      providers: ssoProviders.value,
      storage: sessionStorage,
    })
  ) {
    window.location.href = ssoHref('wecom')
  }
}
/** 顶层跳 IdP(与 Gitee 一样用 <a href>)。预览假项仍给 authorize 链,未配后端会失败。 */
function ssoHref(code: string) {
  return externalAuthApi.authorizeUrl(code)
}
function onSsoOverflow(key: string | number) {
  window.location.href = ssoHref(String(key))
}

async function enterHome() {
  await router.replace('/')
}

// 密码错 / 验证码错:表单左右抖一下(macOS 登录框的做法),比只弹 toast 更直接。
// 先摘掉 class、等一帧再加,连续失败也能重播。
const shaking = ref(false)
async function shake() {
  shaking.value = false
  await nextTick()
  requestAnimationFrame(() => {
    shaking.value = true
  })
}

// 登录成功的收尾:先让页面淡出,再跳转。系统开了「减少动态效果」就不等。
const REDUCED_MOTION = '(prefers-reduced-motion: reduce)'
async function leaveThenEnter() {
  emit('leave', true)
  if (!window.matchMedia?.(REDUCED_MOTION).matches) {
    await new Promise<void>(resolve => setTimeout(resolve, 320))
  }
  await enterHome()
  emit('leave', false)
}

async function finishLogin(res: LoginOutput) {
  // 每次登录都清动态路由/授权态,否则上次会话的 routesReady=true 会跳过 enterInitial,
  // 菜单壳在、业务路由未重建 → 进系统后处处 404(绑定 MFA 后回登录再登最易踩中)。
  resetRouter()
  auth.reset()
  user.setSession(res)

  // 现场绑定:不静默 claim——须用户确认,并依赖服务端 binder cookie 同浏览器校验
  const link = pendingLink.value
  if (link) {
    pendingClaimToken.value = link
    pendingConfirmShow.value = true
    return
  }
  message.success(t('login.success'))
  await leaveThenEnter()
}

const pendingConfirmIdentity = computed(() =>
  pendingDisplayName.value
    ? t('oauth.pendingLinkIdentity', { display: pendingDisplayName.value })
    : '',
)

async function confirmPendingBind() {
  if (pendingClaimBusy.value) return
  pendingClaimBusy.value = true
  try {
    await externalAuthApi.claimPendingLink(pendingClaimToken.value)
    message.success(t('oauth.pendingLinkSuccess', { name: pendingProviderLabel.value }))
  } catch (e) {
    message.warning(translateError(e))
  } finally {
    pendingClaimBusy.value = false
    pendingConfirmShow.value = false
    pendingClaimToken.value = ''
  }
  await enterHome()
}

async function skipPendingBind() {
  pendingConfirmShow.value = false
  pendingClaimToken.value = ''
  message.success(t('login.success'))
  await enterHome()
}

async function onSubmit() {
  if (mode.value === 'mfa') return onMfaSubmit()
  if (mode.value === 'totp') return onTotpSubmit()
  if (mode.value === 'sms') return onSmsSubmit()

  if (!model.account || !model.password) {
    message.warning(t('login.required'))
    return
  }
  if (captchaEnabled.value && !captchaCode.value) {
    message.warning(t('login.captchaRequired'))
    return
  }
  loading.value = true
  try {
    const res = await authApi.login({
      account: model.account,
      password: model.password,
      ...(captchaEnabled.value
        ? { captchaId: captchaId.value, captchaCode: captchaCode.value }
        : {}),
    })
    await finishLogin(res)
  } catch (e) {
    // 40018 = 密码已过、需 TOTP 二次验证
    if (e instanceof ApiError && e.code === 40018 && e.args) {
      totp.challengeId = String(e.args.challengeId ?? '')
      totp.code = ''
      mode.value = 'totp'
    } else if (e instanceof ApiError && e.code === 40020) {
      // 40020 = 强制 MFA 未绑定 → Modal 引导自助设置(登录页默认不常驻链接)
      bindAccount.value = model.account.trim()
      bindRequiredShow.value = true
    } else if (e instanceof ApiError && e.code === 40009 && e.args) {
      // 40009 = 密码已过、需短信二次验证
      mfa.challengeId = String(e.args.challengeId ?? '')
      mfa.phoneMask = String(e.args.phoneMask ?? '')
      mfa.code = ''
      mode.value = 'mfa'
      startCountdown(Number(e.args.resendSeconds ?? 60))
    } else {
      message.error(translateError(e))
      void shake()
    }
    await refreshCaptchaAfterUse()
  } finally {
    loading.value = false
  }
}

function closeBindRequired() {
  bindRequiredShow.value = false
}

function goBindAuthenticator() {
  const account = bindAccount.value || model.account.trim()
  bindRequiredShow.value = false
  void router.push({ path: '/mfa/bind', query: account ? { account } : {} })
}

function goRecovery() {
  const account = model.account.trim() || bindAccount.value
  void router.push({
    path: '/mfa/bind',
    query: { ...(account ? { account } : {}), mode: 'recovery' },
  })
}

async function onMfaSubmit() {
  if (!mfa.code) {
    message.warning(t('login.smsCodePlaceholder'))
    return
  }
  loading.value = true
  try {
    await finishLogin(
      await authApi.smsChallengeLogin({ challengeId: mfa.challengeId, code: mfa.code }),
    )
  } catch (e) {
    message.error(translateError(e))
    void shake()
  } finally {
    loading.value = false
  }
}

async function onTotpSubmit() {
  if (!totp.code) {
    message.warning(t('login.totpPlaceholder'))
    return
  }
  loading.value = true
  try {
    await finishLogin(
      await authApi.totpChallengeLogin({ challengeId: totp.challengeId, code: totp.code }),
    )
  } catch (e) {
    message.error(translateError(e))
    void shake()
  } finally {
    loading.value = false
  }
}

async function onMfaResend() {
  if (countdown.value > 0) return
  try {
    const r = await authApi.smsChallengeResend({ challengeId: mfa.challengeId })
    message.success(t('login.smsSent'))
    startCountdown(r.resendSeconds)
  } catch (e) {
    message.error(translateError(e))
  }
}

function backToAccount() {
  mode.value = 'account'
  mfa.challengeId = ''
  mfa.code = ''
  totp.challengeId = ''
  totp.code = ''
}

async function onSendSmsCode() {
  if (countdown.value > 0) return
  if (!smsModel.phone) {
    message.warning(t('login.phonePlaceholder'))
    return
  }
  if (captchaEnabled.value && !captchaCode.value) {
    message.warning(t('login.captchaRequired'))
    return
  }
  try {
    const r = await authApi.smsLoginSend({
      phone: smsModel.phone,
      ...(captchaEnabled.value
        ? { captchaId: captchaId.value, captchaCode: captchaCode.value }
        : {}),
    })
    message.success(t('login.smsSent'))
    startCountdown(r.resendSeconds)
  } catch (e) {
    message.error(translateError(e))
  }
  await refreshCaptchaAfterUse()
}

async function onSmsSubmit() {
  if (!smsModel.phone || !smsModel.code) {
    message.warning(t('login.smsRequired'))
    return
  }
  loading.value = true
  try {
    await finishLogin(await authApi.smsLogin({ phone: smsModel.phone, code: smsModel.code }))
  } catch (e) {
    message.error(translateError(e))
    void shake()
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login-form" :class="{ shake: shaking }" @animationend.self="shaking = false">
    <div v-if="showLogo" class="lf-brand">
      <!-- 站点 logo 的取值顺序(配置 → brand 选项 → 内置矢量标)统一在 SmartLogo 里 -->
      <SmartLogo :size="34" />
      <span class="lf-word">{{ site.title }}</span>
    </div>

    <n-alert
      v-if="pendingLink"
      type="info"
      :bordered="false"
      class="lf-pending-alert"
      :title="t('oauth.pendingLinkTitle', { name: pendingProviderLabel })"
    >
      {{ t('oauth.pendingLinkHint', { name: pendingProviderLabel }) }}
    </n-alert>

    <!-- 登录方式切换:旧内容先淡出上移,新内容的逐项浮现由外壳(LoginDesk)的入场动画负责 -->
    <Transition name="lf-swap" mode="out-in">
      <div :key="mode" class="lf-body">
        <!-- 短信二次验证:密码已过,凭挑战 + 短信码完成登录 -->
        <template v-if="mode === 'mfa'">
          <h2 v-if="showTitle || showGreeting" class="lf-title">
            {{ t('login.mfaTitle', { phone: mfa.phoneMask }) }}
          </h2>
          <p class="lf-hint-line">{{ t('login.mfaSub') }}</p>
          <n-form :show-feedback="false" @keyup.enter="onSubmit">
            <n-form-item :label="t('login.smsCode')" path="code">
              <n-input
                v-model:value="mfa.code"
                :placeholder="t('login.smsCodePlaceholder')"
                size="large"
                :maxlength="6"
              >
                <template #prefix><Icon icon="ph:chat-circle-text" /></template>
              </n-input>
            </n-form-item>
            <div class="row lf-between">
              <n-button text type="primary" @click="backToAccount">
                {{ t('login.backToPassword') }}
              </n-button>
              <n-button
                text
                :type="countdown > 0 ? 'default' : 'primary'"
                :disabled="countdown > 0"
                @click="onMfaResend"
              >
                {{ countdown > 0 ? t('login.resendAfter', { s: countdown }) : t('login.sendCode') }}
              </n-button>
            </div>
            <n-button
              class="hero-btn cta"
              type="primary"
              size="large"
              block
              attr-type="button"
              :loading="loading"
              @click="onSubmit"
            >
              {{ loading ? t('common.loading') : t('login.submit') }}
            </n-button>
          </n-form>
        </template>

        <!-- TOTP 二次验证:密码已过,凭 Authenticator 动态口令完成登录 -->
        <template v-else-if="mode === 'totp'">
          <h2 v-if="showTitle || showGreeting" class="lf-title">{{ t('login.totpTitle') }}</h2>
          <p class="lf-hint-line">{{ t('login.totpSub') }}</p>
          <n-form :show-feedback="false" @keyup.enter="onSubmit">
            <n-form-item :label="t('login.totpCode')" path="code">
              <n-input
                v-model:value="totp.code"
                :placeholder="t('login.totpPlaceholder')"
                size="large"
                :maxlength="6"
              >
                <template #prefix><Icon icon="ph:shield-check" /></template>
              </n-input>
            </n-form-item>
            <div class="row lf-between">
              <n-button text type="primary" @click="backToAccount">
                {{ t('login.backToPassword') }}
              </n-button>
              <n-button text type="primary" @click="goRecovery">
                {{ t('login.useRecovery') }}
              </n-button>
            </div>
            <n-button
              class="hero-btn cta"
              type="primary"
              size="large"
              block
              attr-type="button"
              :loading="loading"
              @click="onSubmit"
            >
              {{ loading ? t('common.loading') : t('login.submit') }}
            </n-button>
          </n-form>
        </template>

        <template v-else>
          <!-- 单栏:卡内标题「登录」;双栏:品牌在左栏,这里是问候语 -->
          <h2 v-if="showTitle" class="lf-title">{{ t('login.title') }}</h2>
          <div v-else-if="showGreeting" class="lf-greet">
            <h1>{{ t('login.welcome') }}</h1>
            <p>{{ t('login.welcomeSub') }}</p>
          </div>
          <n-form
            :model="mode === 'account' ? model : smsModel"
            :show-feedback="false"
            @keyup.enter="onSubmit"
          >
            <template v-if="mode === 'account'">
              <n-form-item :label="t('login.account')" path="account">
                <n-input
                  v-model:value="model.account"
                  :placeholder="t('login.accountPlaceholder')"
                  size="large"
                >
                  <template #prefix><Icon icon="ph:user" /></template>
                </n-input>
              </n-form-item>
              <n-form-item :label="t('login.password')" path="password">
                <n-input
                  v-model:value="model.password"
                  type="password"
                  show-password-on="click"
                  :placeholder="t('login.passwordPlaceholder')"
                  size="large"
                >
                  <template #prefix><Icon icon="ph:lock" /></template>
                </n-input>
              </n-form-item>
            </template>
            <template v-else>
              <n-form-item :label="t('login.phone')" path="phone">
                <n-input
                  v-model:value="smsModel.phone"
                  :placeholder="t('login.phonePlaceholder')"
                  size="large"
                  :maxlength="20"
                >
                  <template #prefix><Icon icon="ph:device-mobile" /></template>
                </n-input>
              </n-form-item>
            </template>
            <n-form-item v-if="captchaEnabled" :label="t('login.captcha')" path="captcha">
              <div class="lf-captcha">
                <n-input
                  v-model:value="captchaCode"
                  :placeholder="captchaHint"
                  size="large"
                  @keyup.enter="onSubmit"
                >
                  <template #prefix><Icon icon="ph:shield-check" /></template>
                </n-input>
                <!-- SVG 来自本站后端;点击重取一张(一次性票据);取图失败时图形位为空,点击重试 -->
                <button
                  type="button"
                  class="lf-captcha-img"
                  :class="{ empty: !captchaSvg }"
                  :title="t('login.captchaPlaceholder')"
                  :aria-label="t('login.captchaPlaceholder')"
                  @click="loadCaptcha"
                  v-html="captchaSvg"
                />
              </div>
            </n-form-item>
            <n-form-item v-if="mode === 'sms'" :label="t('login.smsCode')" path="code">
              <div class="lf-captcha">
                <n-input
                  v-model:value="smsModel.code"
                  :placeholder="t('login.smsCodePlaceholder')"
                  size="large"
                  :maxlength="6"
                >
                  <template #prefix><Icon icon="ph:chat-circle-text" /></template>
                </n-input>
                <n-button
                  class="lf-send-btn"
                  secondary
                  size="large"
                  attr-type="button"
                  :disabled="countdown > 0"
                  @click="onSendSmsCode"
                >
                  {{
                    countdown > 0 ? t('login.resendAfter', { s: countdown }) : t('login.sendCode')
                  }}
                </n-button>
              </div>
            </n-form-item>
            <div class="row lf-between">
              <n-checkbox v-if="mode === 'account'" v-model:checked="model.remember">
                {{ t('login.remember') }}
              </n-checkbox>
              <span v-else />
              <n-button
                v-if="site.smsLoginEnabled"
                text
                type="primary"
                @click="mode = mode === 'account' ? 'sms' : 'account'"
              >
                {{ mode === 'account' ? t('login.smsLogin') : t('login.accountLogin') }}
              </n-button>
            </div>
            <n-button
              class="hero-btn cta"
              type="primary"
              size="large"
              block
              attr-type="button"
              :loading="loading"
              @click="onSubmit"
            >
              {{ loading ? t('common.loading') : t('login.submit') }}
            </n-button>
          </n-form>

          <!-- 第三方登录:圆形品牌标;<a href>。PREVIEW_ALL_SSO_BRANDS 时展示全部品牌图。 -->
          <template v-if="ssoDisplayList.length">
            <div class="lf-divider">
              <span>{{ t('login.otherMethods') }}</span>
            </div>
            <div class="lf-sso">
              <a
                v-for="p in ssoSplit.visible"
                :key="p.code"
                class="lf-sso-btn"
                :href="ssoHref(p.code)"
                :title="p.displayName"
                :aria-label="p.displayName"
              >
                <BrandIcon :code="p.code" :icon="p.icon" :size="28" />
              </a>
              <n-dropdown
                v-if="ssoSplit.overflow.length"
                trigger="click"
                :options="ssoOverflowOptions"
                @select="onSsoOverflow"
              >
                <a
                  class="lf-sso-btn lf-sso-more"
                  href="javascript:void(0)"
                  role="button"
                  :title="t('login.moreMethods')"
                  :aria-label="t('login.moreMethods')"
                  @click.prevent
                >
                  <Icon icon="ph:dots-three" :width="18" />
                </a>
              </n-dropdown>
            </div>
          </template>
        </template>
      </div>
    </Transition>

    <!-- 页脚:版权(可选链接)+ 构建期版本号。双栏时外壳在卡外自绘页脚,传 :show-footer="false" 关掉。 -->
    <footer v-if="showFooter" class="lf-foot">
      <span>
        © {{ year }}
        <a v-if="site.copyrightUrl" :href="site.copyrightUrl" target="_blank" rel="noopener">
          {{ site.copyright || site.title }}
        </a>
        <template v-else>{{ site.copyright || site.title }}</template>
      </span>
      <span v-if="appVersion" class="lf-ver">v{{ appVersion }}</span>
    </footer>

    <!-- 强制 MFA 未绑定(40020):遮罩 Modal,账密表单仍在底下;默认登录页不常驻绑定链接 -->
    <n-modal
      v-model:show="bindRequiredShow"
      preset="dialog"
      type="warning"
      :title="t('login.totpBindTitle')"
      :content="t('login.totpBindSub')"
      :positive-text="t('login.setupAuthenticator')"
      :negative-text="t('common.cancel')"
      @positive-click="goBindAuthenticator"
      @negative-click="closeBindRequired"
    />
    <!-- pending-link:登录后显式确认绑定,防静默抢绑 -->
    <n-modal
      v-model:show="pendingConfirmShow"
      preset="dialog"
      type="info"
      :title="t('oauth.pendingLinkConfirmTitle')"
      :content="
        t('oauth.pendingLinkConfirmContent', {
          name: pendingProviderLabel,
          identity: pendingConfirmIdentity,
          account: user.userInfo?.account ?? '',
        })
      "
      :positive-text="t('oauth.pendingLinkConfirmOk')"
      :negative-text="t('oauth.pendingLinkConfirmSkip')"
      :loading="pendingClaimBusy"
      :closable="false"
      :mask-closable="false"
      @positive-click="confirmPendingBind"
      @negative-click="skipPendingBind"
    />
  </div>
</template>

<style scoped>
.login-form {
  width: 100%;
}
/* 登录失败:左右抖一下并逐渐收住(macOS 登录框的做法),不回弹到别处 */
.login-form.shake {
  animation: lf-shake 0.46s cubic-bezier(0.36, 0.07, 0.19, 0.97);
}
@keyframes lf-shake {
  0%,
  100% {
    transform: none;
  }
  15% {
    transform: translate3d(-9px, 0, 0);
  }
  30% {
    transform: translate3d(8px, 0, 0);
  }
  45% {
    transform: translate3d(-6px, 0, 0);
  }
  60% {
    transform: translate3d(4px, 0, 0);
  }
  80% {
    transform: translate3d(-2px, 0, 0);
  }
}
/* 登录方式切换:旧内容淡出并轻微上移变虚 */
.lf-swap-leave-active {
  transition:
    opacity 0.16s ease-in,
    transform 0.16s ease-in,
    filter 0.16s ease-in;
}
.lf-swap-leave-to {
  opacity: 0;
  transform: translate3d(0, -6px, 0);
  filter: blur(4px);
}
@media (prefers-reduced-motion: reduce) {
  .login-form.shake {
    animation: none;
  }
}
.lf-pending-alert {
  margin-bottom: 16px;
  border-radius: var(--radius-md);
  text-align: left;
}
/* 单栏卡内顶部:站点 logo + 标题。logo 取值顺序见 SmartLogo */
.lf-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 18px;
}
.lf-word {
  font-size: 22px;
  font-weight: 700;
  letter-spacing: -0.01em;
  color: var(--text-1);
}
.lf-title {
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.3;
  margin: 0 0 16px;
  color: var(--text-1);
}
/* 双栏卡内问候语 */
.lf-greet {
  text-align: center;
  margin: 2px 0 20px;
}
.lf-greet h1 {
  font-size: 26px;
  font-weight: 600;
  letter-spacing: -0.035em;
  margin: 0 0 4px;
  color: var(--text-1);
}
.lf-greet p {
  margin: 0;
  font-size: 13.5px;
  color: var(--text-3);
}
/* 二次验证副标题:标题下的弱化说明行 */
.lf-hint-line {
  margin: -8px 0 18px;
  font-size: 13px;
  color: var(--text-3);
}
/* 表单项之间的间距(表单关了反馈行,没有校验规则要显示) */
.login-form :deep(.n-form-item) {
  margin-bottom: 14px;
}
.row {
  margin: 2px 0 14px;
  min-height: 32px;
}
.lf-between {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.lf-between > :deep(.n-button) {
  height: 32px;
  padding: 0 2px;
}
/* 验证码:输入框 + 可点击刷新的 SVG 图形(等高对齐) */
.lf-captcha {
  display: flex;
  gap: 10px;
  width: 100%;
  align-items: stretch;
}
.lf-captcha > :deep(.n-input) {
  flex: 1;
  min-width: 0;
}
.lf-captcha-img {
  flex: none;
  height: 42px;
  min-width: 100px;
  padding: 0;
  border: 1px solid var(--hairline);
  border-radius: var(--radius-md);
  background: #fff;
  cursor: pointer;
  overflow: hidden;
  display: grid;
  place-items: center;
  line-height: 0;
}
/* 取图失败时图形位为空:换成输入框同款底色,否则空白框像 bug;点击重试 */
.lf-captcha-img.empty {
  background: var(--fill);
}
.lf-captcha-img:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 2px;
}
.lf-captcha-img :deep(svg) {
  display: block;
  height: 40px;
  width: auto;
}
/* 发码按钮:与验证码图形同尺寸位 */
.lf-send-btn {
  flex: none;
  min-width: 104px;
}
/* 第三方登录:分隔线 + 圆形品牌标 */
.lf-divider {
  display: flex;
  align-items: center;
  gap: 14px;
  margin: 22px 0 14px;
}
.lf-divider::before,
.lf-divider::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--hairline);
}
.lf-divider span {
  font-size: 13px;
  color: var(--text-3);
}
.lf-sso {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 12px;
}
/* 第三方登录:只露品牌标本身,不加灰底 / 描边 / 圆形裁切(会吃掉品牌标的轮廓);
   40px 是点击热区,hover 才浮出一层极淡的圆角底,macOS 工具栏按钮的做法 */
.lf-sso-btn {
  width: 40px;
  height: 40px;
  padding: 0;
  display: inline-grid;
  place-items: center;
  flex: none;
  border: none;
  border-radius: var(--radius-lg);
  background: transparent;
  color: var(--text-1);
  text-decoration: none;
  cursor: pointer;
  transition: background var(--transition-base);
}
.lf-sso-btn :deep(.oauth-brand-badge) {
  width: 28px;
  height: 28px;
}
.lf-sso-btn:hover {
  background: var(--hover);
  color: var(--text-1);
  text-decoration: none;
}
.lf-sso-btn:active {
  background: var(--pressed);
}
.lf-sso-btn:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 2px;
}
/* 页脚:版权 + 版本号,弱化色 */
.lf-foot {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  margin-top: 20px;
  font-size: 13px;
  color: var(--text-3);
}
.lf-foot a {
  color: inherit;
  text-decoration: none;
}
.lf-foot a:hover {
  color: var(--signal);
}
.lf-ver {
  opacity: 0.75;
}
@media (max-width: 480px) {
  .lf-captcha-img {
    min-width: 92px;
  }
}
</style>
