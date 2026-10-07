<script setup lang="ts">
// 水印层:登录后的壳层(layouts/default.vue)与系统配置的草稿预览共用。
// 自己带一个每秒的时钟,放在这个小组件里是为了让「当前时间」的刷新只重画水印,不连带重渲染整个布局壳;
// NWatermark 按入参的值(不是对象身份)判断要不要重画,所以不含秒的格式一分钟才画一次。
import { computed } from 'vue'
import { NWatermark } from 'naive-ui'
import { useNow } from '@vueuse/core'
import { useAppStore } from '#/stores/app'
import { buildWatermarkProps, type WatermarkSettings, type WatermarkWho } from '#/lib/watermark'

const props = withDefaults(
  defineProps<{
    settings: WatermarkSettings
    who: WatermarkWho
    /** 铺满视口(壳层);预览里放在缩放舞台内,舞台的 transform 会把 fixed 收进舞台自己的范围 */
    fullscreen?: boolean
    zIndex?: number
  }>(),
  { fullscreen: true, zIndex: 10 },
)

const app = useAppStore()
const now = useNow({ interval: 1000 })
const wm = computed(() => buildWatermarkProps(props.settings, props.who, app.isDark, now.value))
</script>

<template>
  <n-watermark
    v-if="wm"
    :content="wm.content"
    :cross="wm.cross"
    :fullscreen="fullscreen"
    :font-size="wm.fontSize"
    :line-height="wm.lineHeight"
    :rotate="wm.rotate"
    :width="wm.width"
    :height="wm.height"
    :x-gap="wm.xGap"
    :y-gap="wm.yGap"
    :x-offset="wm.xOffset"
    :y-offset="wm.yOffset"
    :font-color="wm.fontColor"
    :z-index="zIndex"
  />
</template>
