<script setup lang="ts">
// 把按固定设计尺寸画好的迷你界面,整体等比缩放到容器可用的宽高里(居中,不裁不滚)。
// 预览因此和真实界面同比例,窗口多大就放多大。
import { computed, ref } from 'vue'
import { useResizeObserver } from '@vueuse/core'

const props = defineProps<{ width: number; height: number }>()
const box = ref<HTMLElement | null>(null)
const size = ref({ w: 0, h: 0 })

useResizeObserver(box, entries => {
  const r = entries[0].contentRect
  size.value = { w: r.width, h: r.height }
})

const scale = computed(() =>
  size.value.w && size.value.h
    ? Math.min(size.value.w / props.width, size.value.h / props.height)
    : 0,
)
</script>

<template>
  <!-- 缩小版界面只是图示,信息都在左边的表单里;对读屏隐藏,免得重复朗读一遍 -->
  <div ref="box" class="fit" aria-hidden="true">
    <div
      class="stage"
      :style="{
        width: `${width}px`,
        height: `${height}px`,
        transform: `translate(-50%, -50%) scale(${scale})`,
        visibility: scale ? 'visible' : 'hidden',
      }"
    >
      <slot />
    </div>
  </div>
</template>

<style scoped>
.fit {
  position: relative;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
.stage {
  position: absolute;
  left: 50%;
  top: 50%;
  transform-origin: center;
}
</style>
