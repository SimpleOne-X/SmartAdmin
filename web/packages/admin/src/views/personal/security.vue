<script setup lang="ts">
// 个人安全:TOTP 自助绑定/恢复入口。不进业务菜单,顶栏用户下拉进入。
// 管理员配置路径类文案(系统配置/安全策略)只给能进配置的人看,普通用户不暴露运维指引。
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { NCard, NButton, NAlert } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useUserStore } from '#/stores/user'
import { useAuthStore } from '#/stores/auth'

const { t } = useI18n()
const router = useRouter()
const user = useUserStore()
const auth = useAuthStore()

// hasPerm 对超管恒 true;普通用户须具备系统配置读权限才看运维提示。
const showAdminHint = computed(() => auth.hasPerm('GET:/api/v1/sys/config/page'))

function goBind(mode?: 'recovery') {
  const account = user.userInfo?.account
  router.push({
    path: '/mfa/bind',
    query: {
      ...(account ? { account } : {}),
      ...(mode === 'recovery' ? { mode: 'recovery' } : {}),
    },
  })
}
</script>

<template>
  <n-card
    class="a-card"
    :bordered="true"
    :title="t('personalSecurity.title')"
    content-style="padding: 20px 24px"
    style="max-width: 560px; width: 100%"
  >
    <div class="sec">
      <n-alert type="info" :bordered="false">
        {{ t('personalSecurity.hint') }}
      </n-alert>
      <n-alert v-if="showAdminHint" type="warning" :bordered="false">
        {{ t('personalSecurity.adminHint') }}
      </n-alert>
      <p class="sec-desc">{{ t('personalSecurity.bindDesc') }}</p>
      <div class="sec-actions">
        <n-button type="primary" @click="goBind()">
          <template #icon><Icon icon="ph:shield-check" :width="16" /></template>
          {{ t('personalSecurity.setupAuthenticator') }}
        </n-button>
        <n-button quaternary @click="goBind('recovery')">
          <template #icon><Icon icon="ph:key" :width="16" /></template>
          {{ t('personalSecurity.useRecovery') }}
        </n-button>
      </div>
      <div class="sec-note">{{ t('personalSecurity.note') }}</div>
    </div>
  </n-card>
</template>

<style scoped>
.sec {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.sec-desc {
  margin: 4px 0 0;
  font-size: 14px;
  line-height: 1.6;
  color: var(--text-2);
}
.sec-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.sec-note {
  font-size: 13px;
  line-height: 1.6;
  color: var(--text-3);
}
</style>
