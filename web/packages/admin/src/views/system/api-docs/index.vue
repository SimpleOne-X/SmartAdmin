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

// 门闩要保的不变量:不管这是本次会话里第几次挂载,一次全新挂载只应该开一个标签页。页面经
// defineAsyncComponent 包装挂进 <keep-alive> 时,onMounted 和 onActivated 有可能在同一个 tick
// 里各调用一次 openDocs(不是只有"某一次特定的挂载"才会撞;不能假设第一次挂载就天然安全)。
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
