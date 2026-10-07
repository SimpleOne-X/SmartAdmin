<script setup lang="ts">
import { useRouter } from 'vue-router'
import { NButton, NCard } from 'naive-ui'
import { useI18n } from 'vue-i18n'

const router = useRouter()
const { t } = useI18n()
</script>

<template>
  <!-- 404 挂在布局壳内:撑满内容区,居中块放进卡片里(文字直接压在画布柔光上对比度不够) -->
  <n-card class="nf-card" content-style="padding: 40px 28px 36px">
    <div class="nf" role="status">
      <div class="code">404</div>
      <div class="msg">{{ t('notFound.desc') }}</div>
      <n-button type="primary" size="large" class="back" @click="router.replace('/')">
        {{ t('notFound.back') }}
      </n-button>
    </div>
  </n-card>
</template>

<style scoped>
/* 撑满 layout 的 .page 容器,而不是整个视口——404 挂在布局壳内 */
.nf-card {
  min-height: 60vh;
  height: 100%;
}
.nf-card :deep(.n-card__content) {
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
}
.nf {
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  gap: 16px;
}
.code {
  font-size: 96px;
  font-weight: 800;
  line-height: 1;
  letter-spacing: -0.04em;
  color: var(--signal);
}
.msg {
  color: var(--text-2);
  font-size: 16px;
}
.back {
  min-width: 120px;
}
</style>
