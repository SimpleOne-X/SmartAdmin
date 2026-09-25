<script setup lang="ts">
// Logo 编辑,仿 GitHub 换头像:点方块里的当前 Logo 弹出菜单 → 上传新图片(也可直接拖进方块)/
// 使用图片链接 / 移除。上传的图先裁成正方形,只进草稿做预览,点底部「保存」才真正上传。
import { computed, ref } from 'vue'
import { NButton, NDropdown, NInput, NModal, useMessage, type DropdownOption } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import SmartLogo from '#/components/SmartLogo.vue'
import { useAuthStore } from '#/stores/auth'
import { LOGO_KEY } from '../groups'
import { useConfigDraft } from '../draft'
import LogoCropper from './LogoCropper.vue'

const ACCEPT = ['image/png', 'image/jpeg', 'image/webp']
/** 裁剪前原图上限;裁完导出 256×256 PNG,远小于后端 1 MB 的限制 */
const MAX_SOURCE_MB = 5

const { t } = useI18n()
const message = useMessage()
const auth = useAuthStore()
const draft = useConfigDraft()
const logo = computed(() => draft.values[LOGO_KEY] ?? '')
const canUpload = computed(() => auth.hasPerm('POST:/api/v1/sys/config/logo'))

const fileInput = ref<HTMLInputElement | null>(null)
const dragging = ref(false)
const cropFile = ref<File | null>(null)
const cropper = ref<InstanceType<typeof LogoCropper> | null>(null)
const cropping = ref(false)
const urlOpen = ref(false)
const urlValue = ref('')

const options = computed<DropdownOption[]>(() => {
  const list: DropdownOption[] = []
  if (canUpload.value) list.push({ key: 'upload', label: t('config.logo.upload') })
  list.push({ key: 'url', label: t('config.logo.useUrl') })
  if (logo.value)
    list.push({ key: 'remove', label: t('config.logo.remove'), props: { class: 'logo-remove' } })
  return list
})

function onSelect(key: string) {
  if (key === 'upload') fileInput.value?.click()
  else if (key === 'url') {
    urlValue.value = logo.value.startsWith('blob:') ? '' : logo.value
    urlOpen.value = true
  } else if (key === 'remove') draft.setPendingLogo(null, '')
}

function pick(file: File | undefined) {
  if (!file) return
  if (!ACCEPT.includes(file.type)) return message.warning(t('config.logo.badType'))
  if (file.size > MAX_SOURCE_MB * 1024 * 1024)
    return message.warning(t('config.logo.tooLarge', { mb: MAX_SOURCE_MB }))
  cropFile.value = file
}

function onFileChange(e: Event) {
  const input = e.target as HTMLInputElement
  pick(input.files?.[0])
  input.value = '' // 同一张图可以再选一次
}

function onDrop(e: DragEvent) {
  dragging.value = false
  if (canUpload.value) pick(e.dataTransfer?.files[0])
}

async function applyCrop() {
  if (!cropper.value) return
  cropping.value = true
  try {
    const blob = await cropper.value.crop()
    draft.setPendingLogo(blob, URL.createObjectURL(blob))
    cropFile.value = null
  } catch {
    message.error(t('config.logo.cropFailed'))
  } finally {
    cropping.value = false
  }
}

function applyUrl() {
  const v = urlValue.value.trim()
  // 只收 http(s) 绝对地址或站内路径:javascript: 之类的伪协议不能进 <img src>/<link href>
  if (v && !/^(https?:\/\/|\/)/i.test(v)) {
    message.warning(t('config.logo.badUrl'))
    return false
  }
  draft.setPendingLogo(null, v)
}
</script>

<template>
  <div class="logo-edit">
    <n-dropdown trigger="click" :options="options" placement="bottom-end" @select="onSelect">
      <button
        type="button"
        :class="['logo-box', { dragging }]"
        :aria-label="t('config.logo.edit')"
        aria-haspopup="menu"
        data-testid="logo-box"
        @dragover.prevent="dragging = canUpload"
        @dragleave="dragging = false"
        @drop.prevent="onDrop"
      >
        <SmartLogo :size="40" :src="logo" />
        <span class="ov" aria-hidden="true">{{ t('common.edit') }}</span>
      </button>
    </n-dropdown>
    <input
      ref="fileInput"
      type="file"
      accept="image/png,image/jpeg,image/webp"
      hidden
      data-testid="logo-file"
      @change="onFileChange"
    />

    <n-modal
      :show="!!cropFile"
      preset="card"
      :title="t('config.logo.cropTitle')"
      style="width: 360px"
      :mask-closable="false"
      @update:show="v => !v && (cropFile = null)"
    >
      <LogoCropper v-if="cropFile" ref="cropper" :file="cropFile" />
      <template #footer>
        <div class="dlg-foot">
          <n-button @click="cropFile = null">{{ t('common.cancel') }}</n-button>
          <n-button type="primary" :loading="cropping" @click="applyCrop">
            {{ t('config.logo.apply') }}
          </n-button>
        </div>
      </template>
    </n-modal>

    <n-modal
      v-model:show="urlOpen"
      preset="dialog"
      :title="t('config.logo.useUrl')"
      :positive-text="t('common.confirm')"
      :negative-text="t('common.cancel')"
      @positive-click="applyUrl"
    >
      <n-input v-model:value="urlValue" placeholder="https://cdn.example.com/logo.png" />
      <p class="cfg-hint" style="margin: 8px 0 0">{{ t('config.logo.urlHint') }}</p>
    </n-modal>
  </div>
</template>

<style scoped>
.logo-box {
  position: relative;
  display: grid;
  place-items: center;
  width: 56px;
  height: 56px;
  padding: 0;
  overflow: hidden;
  border: 0;
  border-radius: 14px;
  background: var(--color-fill);
  cursor: pointer;
}
.logo-box:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: 2px;
}
.logo-box.dragging {
  box-shadow: inset 0 0 0 2px var(--color-primary);
}
/* 悬停或聚焦时底部浮出「编辑」,和系统设置里换头像一样 */
.ov {
  position: absolute;
  inset: auto 0 0;
  padding: 2px 0;
  font-size: 10px;
  color: #fff;
  background: rgba(0, 0, 0, 0.5);
  opacity: 0;
  transition: opacity 0.15s;
}
.logo-box:hover .ov,
.logo-box:focus-visible .ov {
  opacity: 1;
}
.dlg-foot {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
:global(.logo-remove) {
  color: var(--color-danger);
}
</style>
