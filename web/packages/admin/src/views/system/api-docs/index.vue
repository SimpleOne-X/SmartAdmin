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

function openDocs() {
  window.open(scalarUrl, '_blank')
}

// 内核页面被 <keep-alive> 按路由名缓存:setup 只在首次创建时跑一次,再次进入这个已打开的标签页
// 只是把缓存实例激活,顶层的 window.open 不会重跑——页面照样显示"已在新标签页打开",却什么都没打开。
// 故首次挂载与每次缓存激活都各开一次。
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
