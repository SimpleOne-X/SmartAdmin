<script setup lang="ts">
// 正方形 Logo 裁剪:拖动平移、滑块或滚轮缩放,导出 256×256 PNG。
// 只需要这三件事,所以不引裁剪库;几何计算在 lib/logoCrop.ts(有单测)。
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { NSlider } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { clampOffset, coverScale, sourceRect } from '#/lib/logoCrop'

const props = defineProps<{ file: File }>()

/** 视口边长与取景框边长(px) */
const VIEW = 280
const FRAME = 232
const OUTPUT = 256

const { t } = useI18n()
const img = ref<HTMLImageElement | null>(null)
const url = ref('')
const natural = ref({ w: 1, h: 1 })
const zoom = ref(1)
const offset = ref({ x: 0, y: 0 })

const base = computed(() => coverScale(natural.value.w, natural.value.h, FRAME))
const scale = computed(() => base.value * zoom.value)
const state = computed(() => ({
  width: natural.value.w,
  height: natural.value.h,
  frame: FRAME,
  scale: scale.value,
  ...offset.value,
}))
const imgStyle = computed(() => ({
  width: `${natural.value.w * scale.value}px`,
  height: `${natural.value.h * scale.value}px`,
  transform: `translate(calc(-50% + ${offset.value.x}px), calc(-50% + ${offset.value.y}px))`,
}))

watch(
  () => props.file,
  file => {
    if (url.value) URL.revokeObjectURL(url.value)
    url.value = URL.createObjectURL(file)
    zoom.value = 1
    offset.value = { x: 0, y: 0 }
  },
  { immediate: true },
)
onBeforeUnmount(() => URL.revokeObjectURL(url.value))

function onLoad(e: Event) {
  const el = e.target as HTMLImageElement
  natural.value = { w: el.naturalWidth || 1, h: el.naturalHeight || 1 }
}

// 缩放后重新收边,保证取景框里始终是图
watch(zoom, () => {
  offset.value = clampOffset(state.value)
})

let drag: { x: number; y: number; ox: number; oy: number } | null = null
function onPointerDown(e: PointerEvent) {
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  drag = { x: e.clientX, y: e.clientY, ox: offset.value.x, oy: offset.value.y }
}
function onPointerMove(e: PointerEvent) {
  if (!drag) return
  offset.value = clampOffset({
    ...state.value,
    x: drag.ox + e.clientX - drag.x,
    y: drag.oy + e.clientY - drag.y,
  })
}
function onPointerUp() {
  drag = null
}
/** 键盘替代拖动:方向键平移(Shift 大步),+ / - 缩放 */
function onKey(e: KeyboardEvent) {
  const step = e.shiftKey ? 20 : 5
  const d: Record<string, [number, number]> = {
    ArrowLeft: [-step, 0],
    ArrowRight: [step, 0],
    ArrowUp: [0, -step],
    ArrowDown: [0, step],
  }
  if (d[e.key]) {
    e.preventDefault()
    offset.value = clampOffset({
      ...state.value,
      x: offset.value.x + d[e.key][0],
      y: offset.value.y + d[e.key][1],
    })
  } else if (e.key === '+' || e.key === '=') {
    e.preventDefault()
    zoom.value = Math.min(4, zoom.value + 0.1)
  } else if (e.key === '-') {
    e.preventDefault()
    zoom.value = Math.max(1, zoom.value - 0.1)
  }
}

function onWheel(e: WheelEvent) {
  zoom.value = Math.min(4, Math.max(1, zoom.value - e.deltaY * 0.002))
}

/** 按当前取景导出 PNG。 */
function crop(): Promise<Blob> {
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = OUTPUT
  const ctx = canvas.getContext('2d')
  if (!ctx || !img.value) return Promise.reject(new Error('canvas unavailable'))
  const { sx, sy, size } = sourceRect(state.value)
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(img.value, sx, sy, size, size, 0, 0, OUTPUT, OUTPUT)
  return new Promise((resolve, reject) =>
    canvas.toBlob(b => (b ? resolve(b) : reject(new Error('toBlob failed'))), 'image/png'),
  )
}

defineExpose({ crop })
</script>

<template>
  <div class="cropper">
    <div
      class="viewport"
      :style="{ width: `${VIEW}px`, height: `${VIEW}px` }"
      tabindex="0"
      role="application"
      :aria-label="t('config.logo.cropTip')"
      @keydown="onKey"
      @pointerdown="onPointerDown"
      @pointermove="onPointerMove"
      @pointerup="onPointerUp"
      @pointercancel="onPointerUp"
      @wheel.prevent="onWheel"
    >
      <img ref="img" :src="url" :style="imgStyle" alt="" draggable="false" @load="onLoad" />
      <div class="frame" :style="{ width: `${FRAME}px`, height: `${FRAME}px` }" />
    </div>
    <div class="zoom">
      <span>{{ t('config.logo.zoom') }}</span>
      <n-slider v-model:value="zoom" :min="1" :max="4" :step="0.01" :tooltip="false" />
    </div>
    <p class="tip">{{ t('config.logo.cropTip') }}</p>
  </div>
</template>

<style scoped>
.cropper {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
}
.viewport {
  position: relative;
  overflow: hidden;
  border-radius: var(--radius-lg);
  cursor: grab;
  touch-action: none;
  user-select: none;
  background: repeating-conic-gradient(#e5e7eb 0 25%, #fff 0 50%) 0 0 / 16px 16px;
}
.viewport:active {
  cursor: grabbing;
}
.viewport:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: 3px;
}
.viewport img {
  position: absolute;
  left: 50%;
  top: 50%;
  max-width: none;
  pointer-events: none;
}
.frame {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  border: 2px solid #fff;
  border-radius: 12px;
  box-shadow: 0 0 0 999px rgba(0, 0, 0, 0.45);
  pointer-events: none;
}
.zoom {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 280px;
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.zoom span {
  flex: none;
}
.tip {
  margin: 0;
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
</style>
