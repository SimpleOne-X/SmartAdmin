<script setup lang="ts">
// 品牌与登录页:左边「站点」「登录页」两组设置,右边一块预览,用分段控件切「后台 / 登录页」。
// 预览跟着焦点走:编辑站点组时看后台,编辑登录页组时看登录页,也可手动切。
// 登录页文案中英文分开配,组名右侧切语言,预览同步切到该语言。
import { computed, ref } from 'vue'
import { NInput, NSwitch } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { splitLoginHeroFeatures } from '#/lib/loginHero'
import { useAppStore } from '#/stores/app'
import {
  BRAND_KEYS,
  heroKeys,
  LOGIN_LOCALES,
  LOGO_KEY,
  SHOW_FEATURES_KEY,
  SUBTITLE_KEY,
  type LoginLocale,
} from '../groups'
import { useConfigDraft, useDraftFields } from '../draft'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'
import CfgSegment from './CfgSegment.vue'
import ScaleToFit from './ScaleToFit.vue'
import LogoEditor from './LogoEditor.vue'
import AdminMock from './AdminMock.vue'
import LoginMock from './LoginMock.vue'

const { t } = useI18n()
const app = useAppStore()
const { values } = useConfigDraft()
const { str } = useDraftFields()

const title = str('sys.site.title')
const copyright = str('sys.site.copyright')
const copyrightUrl = str('sys.site.copyrightUrl')
const subtitle = str(SUBTITLE_KEY)

const view = ref<'admin' | 'login'>('admin')
const viewOptions = computed(() => [
  { value: 'admin' as const, label: t('config.brand.previewAdmin') },
  { value: 'login' as const, label: t('config.brand.previewLogin') },
])

const locale = ref<LoginLocale>(app.locale === 'en-US' ? 'en-US' : 'zh-CN')
const localeOptions = computed(() =>
  LOGIN_LOCALES.map(l => ({
    value: l,
    label: t(`config.login.locale${l === 'zh-CN' ? 'Zh' : 'En'}`),
  })),
)
function setLocale(l: LoginLocale) {
  locale.value = l
  view.value = 'login'
}
const keys = computed(() => heroKeys(locale.value))
const field = (name: 'headline' | 'highlight' | 'features') =>
  computed({
    get: () => values[keys.value[name]] ?? '',
    set: (v: string) => {
      values[keys.value[name]] = v
    },
  })
const headline = field('headline')
const highlight = field('highlight')
const features = field('features')
// 缺省视为开:库里没有这个键时登录页照常显示卖点
const showFeatures = computed({
  get: () => values[SHOW_FEATURES_KEY] !== 'false',
  set: v => {
    values[SHOW_FEATURES_KEY] = v ? 'true' : 'false'
  },
})
const highlightMissing = computed(() => {
  const h = headline.value.trim()
  const k = highlight.value.trim()
  return !!h && !!k && !h.includes(k)
})
const tooManyFeatures = computed(
  () => splitLoginHeroFeatures(features.value, Number.MAX_SAFE_INTEGER).length > 5,
)
const heroRowKeys = (name: 'headline' | 'highlight' | 'features') =>
  LOGIN_LOCALES.map(l => heroKeys(l)[name])

defineExpose({ setLocale })
</script>

<template>
  <SectionLayout
    :title="t('config.tab.brand')"
    :desc="t('config.brand.desc')"
    :preview-title="t('config.brand.preview')"
  >
    <CfgGroup :title="t('config.brand.groupSite')" @focusin="view = 'admin'">
      <CfgRow
        :label="t('config.logo.title')"
        :hint="t('config.logo.help')"
        :keys="[LOGO_KEY]"
        class="logo-row"
      >
        <LogoEditor />
      </CfgRow>
      <CfgRow :label="t('config.base.siteTitle')" :keys="[BRAND_KEYS[0]]">
        <n-input
          v-model:value="title"
          class="fld"
          placeholder="SmartAdmin"
          :input-props="{ 'aria-label': t('config.base.siteTitle') }"
        />
      </CfgRow>
      <CfgRow :label="t('config.base.copyright')" :keys="['sys.site.copyright']">
        <n-input
          v-model:value="copyright"
          class="fld"
          :placeholder="t('config.brand.copyrightPh')"
          :input-props="{ 'aria-label': t('config.base.copyright') }"
        />
      </CfgRow>
      <CfgRow
        :label="t('config.base.copyrightUrl')"
        :hint="t('config.brand.copyrightUrlHint')"
        :keys="['sys.site.copyrightUrl']"
      >
        <n-input
          v-model:value="copyrightUrl"
          class="fld"
          placeholder="https://"
          :input-props="{ 'aria-label': t('config.base.copyrightUrl') }"
        />
      </CfgRow>
    </CfgGroup>

    <CfgGroup :title="t('config.brand.groupLogin')" @focusin="view = 'login'">
      <template #extra>
        <CfgSegment
          :model-value="locale"
          :options="localeOptions"
          :label="t('config.login.language')"
          data-testid="login-locale"
          @update:model-value="setLocale"
        />
      </template>
      <CfgRow :label="t('config.base.siteSubtitle')" :keys="[SUBTITLE_KEY]">
        <n-input
          v-model:value="subtitle"
          class="fld"
          :placeholder="t('login.subtitle', {}, { locale })"
          :input-props="{ 'aria-label': t('config.base.siteSubtitle') }"
        />
      </CfgRow>
      <CfgRow :label="t('config.login.headline')" :keys="heroRowKeys('headline')">
        <n-input
          v-model:value="headline"
          class="fld"
          :placeholder="t('config.login.headlinePlaceholder')"
          :input-props="{ 'aria-label': t('config.login.headline') }"
        />
      </CfgRow>
      <CfgRow :label="t('config.login.highlight')" :keys="heroRowKeys('highlight')">
        <template #hint>
          <span :class="{ warn: highlightMissing }">
            {{
              highlightMissing
                ? t('config.login.highlightMissing')
                : t('config.login.highlightHint')
            }}
          </span>
        </template>
        <n-input
          v-model:value="highlight"
          class="fld"
          :status="highlightMissing ? 'warning' : undefined"
          :placeholder="t('config.login.highlightPlaceholder')"
          :input-props="{ 'aria-label': t('config.login.highlight') }"
        />
      </CfgRow>
      <CfgRow :label="t('config.login.showFeatures')" :keys="[SHOW_FEATURES_KEY]">
        <n-switch v-model:value="showFeatures" :aria-label="t('config.login.showFeatures')" />
      </CfgRow>
      <CfgRow
        :label="t('config.login.features')"
        :keys="heroRowKeys('features')"
        :disabled="!showFeatures"
        tall
        class="feat"
      >
        <template #hint>
          <span :class="{ err: tooManyFeatures }">
            {{
              tooManyFeatures ? t('config.login.tooManyFeatures') : t('config.login.featuresHint')
            }}
          </span>
        </template>
        <n-input
          v-model:value="features"
          type="textarea"
          :status="tooManyFeatures ? 'error' : undefined"
          :autosize="{ minRows: 2, maxRows: 4 }"
          :placeholder="t('config.login.featuresPlaceholder')"
          :input-props="{ 'aria-label': t('config.login.features') }"
        />
      </CfgRow>
    </CfgGroup>

    <template #preview-extra>
      <CfgSegment v-model="view" :options="viewOptions" :label="t('config.brand.preview')" />
    </template>
    <template #preview>
      <ScaleToFit v-show="view === 'admin'" :width="880" :height="540">
        <AdminMock />
      </ScaleToFit>
      <ScaleToFit v-show="view === 'login'" :width="880" :height="540">
        <LoginMock :locale="locale" />
      </ScaleToFit>
    </template>
  </SectionLayout>
</template>

<style scoped>
.fld {
  width: 240px;
}
.logo-row {
  min-height: 64px;
}
.feat :deep(.ctl) {
  flex: 1.6;
}
.warn {
  color: var(--color-warning);
}
.err {
  color: var(--color-danger);
}
</style>
