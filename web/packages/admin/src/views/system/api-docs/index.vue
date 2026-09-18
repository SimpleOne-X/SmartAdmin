<script setup lang="ts">
import { onActivated, onMounted } from 'vue'
import { NButton, NCard, NSpace } from 'naive-ui'
import { useClipboard } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { runtime } from '#/lib/runtime'
import { useUserStore } from '#/stores/user'

const { t } = useI18n()
const userStore = useUserStore()
const { copy, copied } = useClipboard()

const scalarUrl = `${runtime.apiBase}/scalar`

function copyToken() {
  copy(userStore.accessToken)
}

// 同一次挂载内 onMounted 和 onActivated 会不会一起触发,取决于这个页面是不是"曾经被
// defineAsyncComponent resolve 过一次"——namedPage() 按路由名把异步组件定义缓存在模块级,
// 第一次进入时还在 resolve、走异步路径,只有 onMounted 挂上钩子;此后每次全新挂载(关闭标签页
// 重开、F5、切模块再切回来)都命中已缓存的 resolve 结果,走 Vue 的同步 fast path——setup() 在
// <keep-alive> patch 的同一个同步过程里跑完,onActivated 的"首次挂载也触发一次"逻辑这时才生效,
// 于是 onMounted 和 onActivated 在同一个 tick 里各调用了一次 openDocs,开出两个标签页。
// 用一个同步置位、下一个微任务清除的门闩吸收这种"同 tick 双触发":两次同步调用只开一次;
// 而真正的重新激活(用户切走再切回)发生在别的 tick,门闩早已清空,照样正常重开——这正是
// I7 修复本身要保的行为,不能被这里的去重顺带拿掉。
let openGuardActive = false

function openDocs() {
  if (openGuardActive) return
  openGuardActive = true
  window.open(scalarUrl, '_blank')
  Promise.resolve().then(() => {
    openGuardActive = false
  })
}

onMounted(openDocs)
onActivated(openDocs)
</script>

<template>
  <NCard :title="t('apiDocs.title')">
    <NSpace vertical size="large">
      <p>{{ t('apiDocs.openedHint') }}</p>
      <NButton tag="a" :href="scalarUrl" target="_blank">{{ t('apiDocs.fallbackLink') }}</NButton>
      <NSpace align="center">
        <NButton @click="copyToken">{{ t('apiDocs.copyToken') }}</NButton>
        <span v-if="copied">{{ t('apiDocs.tokenCopied') }}</span>
      </NSpace>
      <p>{{ t('apiDocs.copyTokenHint') }}</p>
    </NSpace>
  </NCard>
</template>
