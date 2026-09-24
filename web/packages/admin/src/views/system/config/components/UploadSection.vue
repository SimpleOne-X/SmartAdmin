<script setup lang="ts">
// 文件上传:大小上限 + 后缀白名单。后端 FileService 读这两键强制执行,保存即生效。
// 后缀以逗号分隔落库,输入时补前导点、转小写(与后端 ParseExts 对齐)。
// 预览是用户看到的上传框,再拿几个示例文件按新规则判一遍,能不能传、为什么不能一目了然。
import { computed } from 'vue'
import { NDynamicTags, NInputNumber, NSlider } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { UPLOAD_EXTS_KEY, UPLOAD_MAX_KEY } from '../groups'
import { useConfigDraft, useDraftFields } from '../draft'
import { vFlash } from '../flash'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'

const { t } = useI18n()
const { values } = useConfigDraft()
const { num } = useDraftFields()
const maxSizeMb = num(UPLOAD_MAX_KEY, 20)

function normalizeExt(v: string): string {
  const s = v.trim().toLowerCase()
  return s.startsWith('.') ? s : '.' + s
}
const exts = computed({
  get: () =>
    (values[UPLOAD_EXTS_KEY] ?? '')
      .split(',')
      .map(e => e.trim())
      .filter(Boolean),
  set: (list: string[]) => {
    values[UPLOAD_EXTS_KEY] = [...new Set(list.map(normalizeExt).filter(e => e !== '.'))].join(',')
  },
})

const PRESETS = {
  images: ['.jpg', '.jpeg', '.png', '.gif', '.webp'],
  docs: ['.pdf', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx'],
  archives: ['.zip', '.rar', '.7z'],
} as const
function addPreset(name: keyof typeof PRESETS) {
  exts.value = [...exts.value, ...PRESETS[name]]
}

const SAMPLES = [
  { name: 'report.pdf', mb: 3.2 },
  { name: 'photo.png', mb: 1.4 },
  { name: 'demo.mp4', mb: 48 },
  { name: 'setup.exe', mb: 12 },
]
const samples = computed(() =>
  SAMPLES.map(f => {
    const ext = f.name.slice(f.name.lastIndexOf('.'))
    // 白名单留空时格式以服务端默认为准,这里不替它下结论
    const extOk = exts.value.length ? exts.value.includes(ext) : undefined
    const sizeOk = f.mb <= maxSizeMb.value
    const status =
      extOk === false
        ? { ok: false, text: t('config.upload.fileBadExt', { ext }) }
        : !sizeOk
          ? { ok: false, text: t('config.upload.fileTooBig', { mb: maxSizeMb.value }) }
          : extOk
            ? { ok: true, text: t('config.upload.fileOk') }
            : { ok: undefined, text: t('config.upload.fileDefault') }
    return { ...f, ext: ext.slice(1).toUpperCase(), status }
  }),
)
const limitText = computed(() =>
  t('config.upload.previewLimit', {
    exts: exts.value.length ? exts.value.join(' ') : t('config.upload.previewDefault'),
    mb: maxSizeMb.value,
  }),
)
</script>

<template>
  <SectionLayout
    :title="t('config.tab.upload')"
    :desc="t('config.upload.desc')"
    :preview-title="t('config.upload.preview')"
    layout="slim"
  >
    <CfgGroup :title="t('config.upload.groupLimit')">
      <CfgRow :label="t('config.upload.maxSizeMb')" :keys="[UPLOAD_MAX_KEY]">
        <n-slider
          v-model:value="maxSizeMb"
          class="slider"
          :min="1"
          :max="200"
          :tooltip="false"
          :aria-label="t('config.upload.maxSizeMb')"
        />
        <n-input-number
          v-model:value="maxSizeMb"
          class="num"
          size="small"
          :min="1"
          :show-button="false"
          :input-props="{ 'aria-label': t('config.upload.maxSizeMb') }"
        />
        <span class="unit">MB</span>
      </CfgRow>
      <CfgRow
        :label="t('config.upload.allowedExtensions')"
        :hint="t('config.upload.extTip')"
        :keys="[UPLOAD_EXTS_KEY]"
        tall
        class="exts"
      >
        <n-dynamic-tags v-model:value="exts" size="small" />
      </CfgRow>
      <CfgRow :label="t('config.upload.quickAdd')">
        <span class="presets">
          <button
            v-for="(_, name) in PRESETS"
            :key="name"
            type="button"
            class="chip"
            @click="addPreset(name)"
          >
            {{ t(`config.upload.preset.${name}`) }}
          </button>
          <button type="button" class="chip" @click="exts = []">
            {{ t('config.upload.preset.clear') }}
          </button>
        </span>
      </CfgRow>
    </CfgGroup>

    <template #preview>
      <div class="drop" data-testid="upload-preview">
        <AppIcon icon="ph:cloud-arrow-up" :size="30" class="ic" />
        <b>{{ t('config.upload.previewDrop') }}</b>
        <small v-flash="limitText">{{ limitText }}</small>
      </div>
      <ul v-flash="limitText" class="files">
        <li v-for="f in samples" :key="f.name">
          <span class="fi">{{ f.ext }}</span>
          <span class="nm">
            {{ f.name }}
            <small>{{ f.mb }} MB</small>
          </span>
          <span :class="['st', { ok: f.status.ok === true, no: f.status.ok === false }]">
            {{ f.status.text }}
          </span>
        </li>
      </ul>
    </template>
  </SectionLayout>
</template>

<style scoped>
.slider {
  width: 160px;
}
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
.exts :deep(.ctl) {
  flex: 1.4;
}
.exts :deep(.n-dynamic-tags) {
  justify-content: flex-end;
}
.presets {
  display: inline-flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 5px;
}
.chip {
  padding: 2px 9px;
  border: 0;
  border-radius: 6px;
  background: var(--color-fill);
  font: inherit;
  font-size: var(--font-size-sm);
  line-height: 20px;
  color: var(--color-text-secondary);
  cursor: pointer;
}
.chip:hover {
  background: var(--color-fill-hover);
}
.chip:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: 1px;
}
.drop {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 24px 16px;
  border: 1.5px dashed var(--color-border-strong);
  border-radius: 14px;
  background: var(--color-bg-container);
  text-align: center;
}
.drop .ic {
  color: var(--color-primary);
}
.drop b {
  font-weight: 500;
}
.drop small {
  padding: 1px 6px;
  border-radius: 6px;
  color: var(--color-text-secondary);
}
.files {
  margin: 0;
  padding: 0;
  list-style: none;
  border-radius: 12px;
  background: var(--color-bg-container);
}
.files li {
  position: relative;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 14px;
  font-size: var(--font-size-base);
}
.files li + li::before {
  content: '';
  position: absolute;
  top: 0;
  left: 50px;
  right: 0;
  border-top: 0.5px solid var(--color-border);
}
.fi {
  display: grid;
  flex: none;
  place-items: center;
  width: 26px;
  height: 30px;
  border-radius: 5px;
  background: var(--color-fill);
  font-size: 9px;
  font-weight: 700;
  color: var(--color-text-secondary);
}
.nm {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}
.nm small {
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.st {
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.st.ok {
  color: var(--color-success);
}
.st.no {
  color: var(--color-danger);
}
</style>
