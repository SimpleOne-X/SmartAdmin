<script setup lang="ts">
// 缩小版登录页(880×540 设计尺寸,外面 ScaleToFit 等比缩放)。form-only 只画右侧登录框(440×540),
// 给「登录方式」页用。Hero 文案、验证码、短信登录入口、第三方按钮、页脚版权都读草稿——
// 别的分类改了,这里一样跟着变。
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import BrandIcon from '#/components/oauth/BrandIcon.vue'
import { resolveLoginHero, splitLoginHeroFeatures } from '#/lib/loginHero'
import LoginHeroPanel from '#/views/login/components/LoginHeroPanel.vue'
import {
  CAPTCHA_TYPE_KEY,
  heroKeys,
  LOGO_KEY,
  SHOW_FEATURES_KEY,
  SMS_LOGIN_KEY,
  SUBTITLE_KEY,
  type LoginLocale,
} from '../groups'
import { enabledKey, useConfigDraft } from '../draft'
import { vFlash } from '../flash'

const props = defineProps<{ locale: LoginLocale; formOnly?: boolean }>()

const CAPTCHA_SAMPLE: Record<string, string> = { char: 'A7K2', path: 'K9x4', math: '3 + 5 = ?' }

const { t } = useI18n()
const draft = useConfigDraft()
const { values } = draft
const tr = (key: string) => t(key, {}, { locale: props.locale })
const keys = computed(() => heroKeys(props.locale))

const hero = computed(() =>
  resolveLoginHero(
    {
      headline: values[keys.value.headline] ?? '',
      highlight: values[keys.value.highlight] ?? '',
      features: splitLoginHeroFeatures(values[keys.value.features]),
    },
    {
      headline: `${tr('login.headlinePre')}${tr('login.headlineAccent')}${tr('login.headlinePost')}`,
      highlight: tr('login.headlineAccent'),
      features: [tr('login.featRbac'), tr('login.featScope'), tr('login.featPortal')],
    },
    // 缺省视为开:库里没有这个键时登录页照常显示卖点
    values[SHOW_FEATURES_KEY] !== 'false',
  ),
)
const subtitle = computed(() => values[SUBTITLE_KEY]?.trim() || tr('login.subtitle'))
const siteTitle = computed(() => values['sys.site.title']?.trim() || 'SmartAdmin')
const copyright = computed(() => values['sys.site.copyright']?.trim() || siteTitle.value)
const captchaOn = computed(() => values['sys.security.captcha.enabled'] === 'true')
const captchaSample = computed(() => CAPTCHA_SAMPLE[values[CAPTCHA_TYPE_KEY] || 'char'] ?? 'A7K2')
const smsOn = computed(() => values[SMS_LOGIN_KEY] === 'true')
const oauth = computed(() =>
  draft.providerRows.value.filter(p => p.registered && values[enabledKey(p.code)] === 'true'),
)
const oauthSig = computed(() => oauth.value.map(p => p.code).join(','))
</script>

<template>
  <div :class="['mock', { 'form-only': formOnly }]">
    <div v-if="!formOnly" v-flash="JSON.stringify(hero)" class="hero">
      <LoginHeroPanel
        :headline="hero.headline"
        :highlight="hero.highlight"
        :subtitle="subtitle"
        :features="hero.features"
        :title="siteTitle"
        :logo="values[LOGO_KEY] ?? ''"
      />
    </div>
    <div class="form">
      <b class="welcome">{{ tr('login.welcome') }}</b>
      <span v-flash="subtitle" class="sub">{{ subtitle }}</span>
      <div v-flash="smsOn" class="modes">
        <span class="on">{{ tr('login.accountLogin') }}</span>
        <span v-if="smsOn" data-testid="preview-sms">{{ tr('login.smsLogin') }}</span>
      </div>
      <i class="in" />
      <i class="in" />
      <div v-if="captchaOn" class="captcha">
        <i class="in" />
        <span>{{ captchaSample }}</span>
      </div>
      <span class="go">{{ tr('login.submit') }}</span>
      <div v-flash="oauthSig" class="oauth" data-testid="preview-oauth">
        <template v-if="oauth.length">
          <div class="divider">{{ t('config.externalAuth.previewOther') }}</div>
          <div class="btns">
            <span v-for="p in oauth" :key="p.code" :title="p.displayName">
              <BrandIcon :code="p.code" :icon="p.icon" :size="20" />
            </span>
          </div>
        </template>
      </div>
      <div v-flash="copyright" class="copy">© {{ copyright }}</div>
    </div>
  </div>
</template>

<style scoped>
.mock {
  display: grid;
  grid-template-columns: 1.15fr 1fr;
  width: 880px;
  height: 540px;
  overflow: hidden;
  border-radius: 10px;
  background: var(--color-bg-container);
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.12);
}
.mock.form-only {
  grid-template-columns: 1fr;
  width: 440px;
}
.hero {
  display: flex;
  align-items: center;
  padding: 32px 36px;
  overflow: hidden;
  background:
    radial-gradient(
      60% 70% at 20% 15%,
      color-mix(in srgb, var(--color-primary) 18%, transparent),
      transparent 72%
    ),
    var(--color-bg-body);
}
.hero :deep(.headline) {
  font-size: 30px;
  line-height: 1.3;
}
.hero :deep(.bar) {
  margin: 22px 0 16px;
}
.hero :deep(.sub) {
  margin: 12px 0 18px;
}
.form {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 44px 40px 18px;
  font-size: 13px;
}
.welcome {
  font-size: 22px;
}
.sub {
  margin: -8px 0 4px;
  border-radius: 6px;
  color: var(--color-text-secondary);
}
.modes {
  display: flex;
  gap: 18px;
  border-radius: 6px;
  color: var(--color-text-secondary);
}
.modes .on {
  padding-bottom: 3px;
  border-bottom: 2px solid var(--color-primary);
  color: var(--color-text-primary);
  font-weight: 600;
}
.in {
  display: block;
  height: 40px;
  border-radius: 8px;
  background: var(--color-fill);
}
.captcha {
  display: flex;
  gap: 8px;
}
.captcha .in {
  flex: 1;
}
.captcha span {
  display: grid;
  place-items: center;
  width: 104px;
  border-radius: 8px;
  font-weight: 700;
  letter-spacing: 1px;
  color: var(--color-text-secondary);
  background: repeating-linear-gradient(
    45deg,
    var(--color-fill) 0 4px,
    var(--color-bg-body) 4px 8px
  );
}
.go {
  display: grid;
  place-items: center;
  height: 42px;
  border-radius: 8px;
  color: #fff;
  background: var(--color-primary);
  font-weight: 600;
}
.oauth {
  min-height: 20px;
  border-radius: 8px;
}
.divider {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 12px;
  color: var(--color-text-tertiary);
}
.divider::before,
.divider::after {
  content: '';
  flex: 1;
  border-top: 1px solid var(--color-border);
}
.btns {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 12px;
  margin-top: 12px;
}
.btns span {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  border: 1px solid var(--color-border);
  border-radius: 50%;
}
.copy {
  margin-top: auto;
  border-radius: 6px;
  text-align: center;
  font-size: 12px;
  color: var(--color-text-tertiary);
}
</style>
