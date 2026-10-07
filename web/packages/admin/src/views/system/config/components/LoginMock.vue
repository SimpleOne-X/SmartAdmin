<script setup lang="ts">
// 缩小版登录页(880×540 设计尺寸,外面 ScaleToFit 等比缩放),画的是双栏形态:左栏品牌区压在柔光色场上,
// 右栏是毛玻璃登录卡(与真实登录页同一套视觉语言,色场静止不漂移)。form-only 只画登录卡所在的右栏(440×540),
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
    // 缺省视为开:库里没有这个键时登录页照常显示亮点
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
    <div class="desk" aria-hidden="true">
      <i />
      <i />
      <i />
      <i />
    </div>
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
    <div class="form-wrap">
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
  </div>
</template>

<style scoped>
.mock {
  position: relative;
  display: grid;
  grid-template-columns: 1.15fr 1fr;
  width: 880px;
  height: 540px;
  overflow: hidden;
  border-radius: 10px;
  background: var(--desk-base);
  box-shadow: var(--shadow-2);
}
.mock.form-only {
  grid-template-columns: 1fr;
  width: 440px;
}
/* 色场:和真实登录页同一组 --desk-1..4,预览里不动 */
.desk {
  position: absolute;
  inset: -20%;
  pointer-events: none;
}
.desk i {
  position: absolute;
  display: block;
  border-radius: 50%;
  filter: blur(70px);
  opacity: var(--desk-op);
}
.desk i:nth-child(1) {
  left: 2%;
  top: 4%;
  width: 46%;
  height: 62%;
  background: var(--desk-1);
}
.desk i:nth-child(2) {
  left: 26%;
  top: 34%;
  width: 44%;
  height: 56%;
  background: var(--desk-2);
}
.desk i:nth-child(3) {
  left: 54%;
  top: -6%;
  width: 40%;
  height: 58%;
  background: var(--desk-3);
}
.desk i:nth-child(4) {
  left: 62%;
  top: 46%;
  width: 46%;
  height: 62%;
  background: var(--desk-4);
}
.hero {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 32px 36px;
  overflow: hidden;
}
.hero :deep(.login-hero-panel) {
  max-width: 330px;
}
.hero :deep(.headline) {
  font-size: 28px;
  line-height: 1.3;
}
/* 真实登录页的左栏字号更大(logo 22 / 说明 17 / 亮点 16),预览按 880×540 的画布排版,这里把字号钉成预览尺寸 */
.hero :deep(.logo) {
  margin-bottom: 20px;
  gap: 11px;
  font-size: 18px;
}
.hero :deep(.sub) {
  margin: 0 0 18px;
  font-size: 15px;
}
.hero :deep(.points) {
  gap: 14px;
}
.hero :deep(.points li) {
  gap: 11px;
  font-size: 14.5px;
}
.form-wrap {
  position: relative;
  display: grid;
  place-items: center;
  padding: 16px;
  min-height: 0;
}
/* 登录卡:毛玻璃,与真实登录页同一套材质 */
.form {
  display: flex;
  flex-direction: column;
  gap: 10px;
  width: 100%;
  height: 100%;
  padding: 24px 28px 12px;
  overflow: hidden;
  border: 1px solid var(--glass-border);
  border-radius: 22px;
  background: var(--glass-panel);
  box-shadow: var(--glass-shadow);
  font-size: 13px;
}
.mock:not(.form-only) .form {
  max-width: 384px;
}
.welcome {
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.035em;
  color: var(--text-1);
}
.sub {
  margin: -6px 0 2px;
  border-radius: 6px;
  color: var(--text-2);
}
.modes {
  display: flex;
  gap: 18px;
  border-radius: 6px;
  color: var(--text-2);
}
.modes .on {
  padding-bottom: 3px;
  border-bottom: 2px solid var(--signal);
  color: var(--text-1);
  font-weight: 600;
}
.in {
  display: block;
  height: 40px;
  border-radius: 11px;
  background: var(--fill);
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
  border-radius: 11px;
  font-weight: 700;
  letter-spacing: 1px;
  color: var(--text-2);
  background: repeating-linear-gradient(45deg, var(--fill) 0 4px, transparent 4px 8px);
}
.go {
  display: grid;
  place-items: center;
  height: 42px;
  border-radius: 11px;
  color: #fff;
  background-image: linear-gradient(120deg, var(--signal), var(--login-accent-2));
  box-shadow: 0 10px 26px -10px var(--signal-glow);
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
  color: var(--text-3);
}
.divider::before,
.divider::after {
  content: '';
  flex: 1;
  border-top: 1px solid var(--hairline);
}
.btns {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 12px;
  margin-top: 12px;
}
/* 与真实登录页一致:只露品牌标,不加底圈 */
.btns span {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
}
.btns span :deep(.oauth-brand-badge) {
  width: 28px;
  height: 28px;
}
.copy {
  margin-top: auto;
  border-radius: 6px;
  text-align: center;
  font-size: 12px;
  color: var(--text-3);
}
</style>
