<script setup lang="ts">
// 水印设置:左边「内容」「样式」两组设置,右边预览(缩小版后台,水印层直接叠在上面,改一项即时变)。
// 水印只印在登录后的页面,所以预览只有后台一种,不像「品牌与登录页」要切登录页。
import { computed } from 'vue'
import { NCheckbox, NCheckboxGroup, NInput, NSelect, NSlider, NSwitch } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import {
  DEFAULT_WATERMARK,
  formatWatermarkTime,
  normalizeWatermarkFields,
  watermarkFromValues,
  watermarkParts,
  WATERMARK_FIELDS,
  WATERMARK_KEY,
  WATERMARK_LIMITS,
  WATERMARK_TIME_FORMATS,
  type WatermarkDensity,
  type WatermarkLayout,
  type WatermarkTimeFormat,
} from '#/lib/watermark'
import { useConfigDraft, useDraftFields } from '../draft'
import { useConfigLayout } from '../layoutMode'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'
import CfgSegment from './CfgSegment.vue'
import ScaleToFit from './ScaleToFit.vue'
import AdminMock from './AdminMock.vue'

const { t } = useI18n()
const { values } = useConfigDraft()
const { str, num, bool } = useDraftFields()
const { ctl } = useConfigLayout()

// 内容项勾选顺序不算数,存的时候按固定顺序排,线上和预览才印得一样;键不存在(老库没种子)按默认内容项。
const wmOn = bool(WATERMARK_KEY.enabled)
const wmFields = computed<string[]>({
  get: () =>
    values[WATERMARK_KEY.fields] === undefined
      ? [...DEFAULT_WATERMARK.fields]
      : normalizeWatermarkFields(values[WATERMARK_KEY.fields]),
  set: v => {
    values[WATERMARK_KEY.fields] = normalizeWatermarkFields(v).join(',')
  },
})
const wmText = str(WATERMARK_KEY.text)
const wmEnum = <T extends string>(key: string, fallback: T) =>
  computed<T>({
    get: () => (values[key] || fallback) as T,
    set: v => {
      values[key] = v
    },
  })
const wmTimeFormat = wmEnum<WatermarkTimeFormat>(
  WATERMARK_KEY.timeFormat,
  DEFAULT_WATERMARK.timeFormat,
)
const wmLayout = wmEnum<WatermarkLayout>(WATERMARK_KEY.layout, DEFAULT_WATERMARK.layout)
const wmDensity = wmEnum<WatermarkDensity>(WATERMARK_KEY.density, DEFAULT_WATERMARK.density)
const wmSize = num(WATERMARK_KEY.fontSize, DEFAULT_WATERMARK.fontSize)
const wmOpacity = num(WATERMARK_KEY.opacity, DEFAULT_WATERMARK.opacity)
const wmRotate = num(WATERMARK_KEY.rotate, DEFAULT_WATERMARK.rotate)
const wmCross = computed({
  get: () => values[WATERMARK_KEY.cross] !== 'false',
  set: v => {
    values[WATERMARK_KEY.cross] = v ? 'true' : 'false'
  },
})
const wmHasField = (f: string) => wmFields.value.includes(f)
const wmFieldOptions = computed(() =>
  WATERMARK_FIELDS.map(f => ({ value: f, label: t(`config.watermark.field.${f}`) })),
)
// 时间格式的选项文字就是「现在这一刻按该格式印出来」的样子,不用背格式串
const wmTimeOptions = computed(() =>
  WATERMARK_TIME_FORMATS.map(f => ({ value: f, label: formatWatermarkTime(f, new Date()) })),
)
const wmLayoutOptions = computed(() => [
  { value: 'single' as const, label: t('config.watermark.layoutSingle') },
  { value: 'multi' as const, label: t('config.watermark.layoutMulti') },
])
const wmDensityOptions = computed(() => [
  { value: 'sparse' as const, label: t('config.watermark.sparse') },
  { value: 'normal' as const, label: t('config.watermark.normal') },
  { value: 'dense' as const, label: t('config.watermark.dense') },
])
// 启用了但没有任何可印的东西(一项没勾,或只勾了自定义文字却没填):警告,照常允许保存
const wmEmpty = computed(
  () =>
    wmOn.value &&
    watermarkParts(
      watermarkFromValues(values),
      { name: 'x', account: 'x', org: 'x', phoneTail: 'x' },
      new Date(),
    ).length === 0,
)
</script>

<template>
  <SectionLayout
    :title="t('config.tab.watermark')"
    :desc="t('config.watermark.desc')"
    :preview-title="t('config.brand.preview')"
  >
    <CfgGroup :title="t('config.watermark.groupContent')">
      <CfgRow
        :label="t('config.watermark.enable')"
        :hint="t('config.watermark.enableHint')"
        :keys="[WATERMARK_KEY.enabled]"
      >
        <n-switch v-model:value="wmOn" :aria-label="t('config.watermark.enable')" />
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.content')"
        :keys="[WATERMARK_KEY.fields]"
        :disabled="!wmOn"
        tall
        class="wm-content"
      >
        <template #hint>
          <span :class="{ warn: wmEmpty }">
            {{ wmEmpty ? t('config.watermark.contentEmpty') : t('config.watermark.contentHint') }}
          </span>
        </template>
        <n-checkbox-group v-model:value="wmFields" :disabled="!wmOn">
          <div class="wm-fields">
            <n-checkbox
              v-for="o in wmFieldOptions"
              :key="o.value"
              :value="o.value"
              :label="o.label"
            />
          </div>
        </n-checkbox-group>
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.text')"
        :hint="t('config.watermark.textHint')"
        :keys="[WATERMARK_KEY.text]"
        :disabled="!wmOn || !wmHasField('text')"
        sub
      >
        <n-input
          v-model:value="wmText"
          :size="ctl"
          :maxlength="WATERMARK_LIMITS.text"
          show-count
          :disabled="!wmOn || !wmHasField('text')"
          :placeholder="t('config.watermark.textPlaceholder', { max: WATERMARK_LIMITS.text })"
          :input-props="{ 'aria-label': t('config.watermark.text') }"
        />
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.timeFormat')"
        :keys="[WATERMARK_KEY.timeFormat]"
        :disabled="!wmOn || !wmHasField('time')"
        sub
      >
        <n-select
          v-model:value="wmTimeFormat"
          :size="ctl"
          :options="wmTimeOptions"
          :disabled="!wmOn || !wmHasField('time')"
          :aria-label="t('config.watermark.timeFormat')"
        />
      </CfgRow>
    </CfgGroup>

    <CfgGroup :title="t('config.watermark.groupStyle')">
      <CfgRow
        :label="t('config.watermark.layout')"
        :keys="[WATERMARK_KEY.layout]"
        :disabled="!wmOn"
      >
        <CfgSegment
          v-model="wmLayout"
          :options="wmLayoutOptions"
          :label="t('config.watermark.layout')"
        />
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.fontSize')"
        :keys="[WATERMARK_KEY.fontSize]"
        :disabled="!wmOn"
      >
        <n-slider
          v-model:value="wmSize"
          class="slider"
          :min="WATERMARK_LIMITS.fontSize.min"
          :max="WATERMARK_LIMITS.fontSize.max"
          :tooltip="false"
          :disabled="!wmOn"
          :aria-label="t('config.watermark.fontSize')"
        />
        <span class="unit">{{ wmSize }} px</span>
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.opacity')"
        :hint="t('config.watermark.opacityHint')"
        :keys="[WATERMARK_KEY.opacity]"
        :disabled="!wmOn"
      >
        <n-slider
          v-model:value="wmOpacity"
          class="slider"
          :min="WATERMARK_LIMITS.opacity.min"
          :max="WATERMARK_LIMITS.opacity.max"
          :tooltip="false"
          :disabled="!wmOn"
          :aria-label="t('config.watermark.opacity')"
        />
        <span class="unit">{{ wmOpacity }}%</span>
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.rotate')"
        :keys="[WATERMARK_KEY.rotate]"
        :disabled="!wmOn"
      >
        <n-slider
          v-model:value="wmRotate"
          class="slider"
          :min="WATERMARK_LIMITS.rotate.min"
          :max="WATERMARK_LIMITS.rotate.max"
          :step="5"
          :tooltip="false"
          :disabled="!wmOn"
          :aria-label="t('config.watermark.rotate')"
        />
        <span class="unit">{{ wmRotate }}°</span>
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.density')"
        :keys="[WATERMARK_KEY.density]"
        :disabled="!wmOn"
      >
        <CfgSegment
          v-model="wmDensity"
          :options="wmDensityOptions"
          :label="t('config.watermark.density')"
        />
      </CfgRow>
      <CfgRow
        :label="t('config.watermark.cross')"
        :hint="t('config.watermark.crossHint')"
        :keys="[WATERMARK_KEY.cross]"
        :disabled="!wmOn"
      >
        <n-switch
          v-model:value="wmCross"
          :disabled="!wmOn"
          :aria-label="t('config.watermark.cross')"
        />
      </CfgRow>
    </CfgGroup>

    <template #preview>
      <ScaleToFit :width="880" :height="540">
        <AdminMock />
      </ScaleToFit>
    </template>
  </SectionLayout>
</template>

<style scoped>
.warn {
  color: var(--warn);
}
.wm-content :deep(.ctl) {
  flex: 1.8 1 0;
}
.wm-content :deep(.n-checkbox-group) {
  width: 100%;
}
.wm-fields {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 8px 16px;
}
.slider {
  width: 160px;
}
.unit {
  flex: none;
  min-width: 44px;
  font-size: 13px;
  color: var(--text-2);
  text-align: right;
  font-variant-numeric: tabular-nums;
}
</style>
