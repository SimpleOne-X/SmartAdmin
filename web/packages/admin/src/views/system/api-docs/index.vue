<script setup lang="ts">
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

window.open(scalarUrl, '_blank')
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
