<script setup lang="ts">
// 门户重建失败的落点:网络抖动 / 后端 5xx / 限流。会话没问题,所以不清会话,给「重试」与「重新登录」两条路。
// 路由是静态的,不依赖动态路由就绪 —— 恰恰是动态路由建不起来时才会到这里。
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NButton } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { useAuthStore } from '#/stores/auth'
import { useUserStore } from '#/stores/user'
import { resetRouter } from '#/router'
import LoginDesk from '#/views/login/components/LoginDesk.vue'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()

const redirect = computed(() => (route.query.redirect as string | undefined) || '/')

// 重试 = 再走一次守卫:routesReady 仍是 false,守卫会重新 enterInitial。
function retry() {
  router.replace(redirect.value)
}

function relogin() {
  resetRouter()
  useAuthStore().reset()
  useUserStore().clear()
  router.replace('/login')
}
</script>

<template>
  <LoginDesk :width="440" content-style="padding: 38px 30px 32px">
    <div class="boot-error" role="alert">
      <AppIcon icon="ph:warning-circle" :size="72" class="boot-icon" />
      <h1>{{ t('bootError.title') }}</h1>
      <p>{{ t('bootError.desc') }}</p>
      <div class="boot-actions">
        <n-button class="cta" type="primary" size="large" @click="retry">
          {{ t('bootError.retry') }}
        </n-button>
        <n-button quaternary size="large" @click="relogin">{{ t('bootError.relogin') }}</n-button>
      </div>
    </div>
  </LoginDesk>
</template>

<style scoped>
.boot-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
}
.boot-icon {
  color: var(--signal);
}
h1 {
  margin: 14px 0 10px;
  color: var(--text-1);
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.02em;
}
p {
  margin: 0;
  max-width: 480px;
  color: var(--text-2);
  font-size: 14.5px;
  line-height: 1.7;
}
.boot-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 10px;
  margin-top: 26px;
}
.boot-actions .cta {
  min-width: 110px;
}
</style>
