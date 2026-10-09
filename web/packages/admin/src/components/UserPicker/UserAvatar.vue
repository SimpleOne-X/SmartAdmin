<script setup lang="ts">
// 选择器内部用的小头像:有头像用图,没有(或图挂了)回落姓名首字。
// 不用 NAvatar:它的文字头像在挂载时量自身宽度来缩放文字,而「已选」页挂载时常处于 display:none
// (窄档两页形态),量到 0,首字就被缩得变形。这里首字是纯 CSS 居中,不依赖测量。
import { computed, ref, watch } from 'vue'

const props = defineProps<{ name?: string | null; src?: string | null; size: number }>()

const broken = ref(false)
watch(
  () => props.src,
  () => (broken.value = false),
)

const initial = computed(() => (props.name || '?').slice(0, 1))
const style = computed(() => ({
  width: `${props.size}px`,
  height: `${props.size}px`,
  fontSize: `${Math.round(props.size * 0.44)}px`,
}))
</script>

<template>
  <span class="user-avatar" :style="style">
    <img v-if="src && !broken" :src="src" alt="" @error="broken = true" />
    <template v-else>{{ initial }}</template>
  </span>
</template>

<style scoped>
.user-avatar {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  overflow: hidden;
  border-radius: 999px;
  background: var(--sel-halo);
  color: var(--sel-fg);
  font-weight: 600;
  line-height: 1;
}
.user-avatar img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
</style>
