<script setup lang="ts">
// 文件上传:大小上限 + 后缀白名单。后端 FileService 读这两键强制执行,保存即生效。
// 后缀以逗号分隔落库,输入时补前导点、转小写(与后端 ParseExts 对齐)。
// 预览是用户看到的上传框,再拿几个示例文件按新规则判一遍,能不能传、为什么不能一目了然。
import { computed } from 'vue'
import { NButton, NDynamicTags, NInputNumber, NSlider } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { UPLOAD_EXTS_KEY, UPLOAD_MAX_KEY } from '../groups'
import { useConfigDraft, useDraftFields } from '../draft'
import { useConfigLayout } from '../layoutMode'
import { vFlash } from '../flash'
import SectionLayout from './SectionLayout.vue'
import CfgGroup from './CfgGroup.vue'
import CfgRow from './CfgRow.vue'

const { t } = useI18n()
const { values } = useConfigDraft()
const { num } = useDraftFields()
const { ctl, sm } = useConfigLayout()
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
          class="num sm"
          :size="ctl"
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
          <n-button v-for="(_, name) in PRESETS" :key="name" :size="sm" @click="addPreset(name)">
            {{ t(`config.upload.preset.${name}`) }}
          </n-button>
          <n-button :size="sm" @click="exts = []">
            {{ t('config.upload.preset.clear') }}
          </n-button>
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
  width: 96px;
}
.num.sm {
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
.exts :deep(.ctl) {
  flex: 1.4 1 0;
}
.exts :deep(.n-dynamic-tags) {
  justify-content: flex-end;
}
.presets {
  display: inline-flex;
  flex: none;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 5px;
}
.drop {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 24px 16px;
  border: 1.5px dashed var(--hairline-strong);
  border-radius: var(--radius-lg);
  background: var(--cfg-card);
  text-align: center;
}
.drop .ic {
  color: var(--signal);
}
.drop b {
  font-size: 14px;
  font-weight: 600;
}
.drop small {
  border-radius: 6px;
  font-size: 13px;
  color: var(--text-3);
}
.files {
  margin: 0;
  padding: 4px 0;
  list-style: none;
  border-radius: var(--radius-lg);
  background: var(--cfg-card);
}
.files li {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 14px;
  font-size: 14px;
}
.files li + li {
  border-top: 1px solid var(--separator);
}
.fi {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 24px;
  border-radius: 6px;
  background: var(--glass-strong);
  font-size: 11px;
  font-weight: 600;
  color: var(--text-2);
}
.nm {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}
.nm small {
  font-size: 13px;
  color: var(--text-3);
}
.st {
  flex: none;
  font-size: 13px;
  color: var(--text-3);
}
.st.ok {
  color: var(--ok);
}
.st.no {
  color: var(--err);
}
</style>
