<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NButton, NCard, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '#/stores/auth'
import { useModule } from '#/composables/useModule'
import { translateMenuTitle } from '#/locales/menuTitle'
import { translateError } from '#/utils/error'
import type { AppModule } from '#/types/menu'

const route = useRoute()
const router = useRouter()
const message = useMessage()
const { t } = useI18n()
const auth = useAuthStore()
const { findOwnerModule, switchModule } = useModule()

// 动态路由只注册当前应用的菜单:别的应用的页面地址(书签、第三方后台配的主页)在这里同样是 404。
// 单应用部署没有"别的应用",也就不提应用、不反查。
const multiApp = computed(() => auth.modules.length > 1)
const currentApp = computed(() => {
  const m = auth.modules.find(x => x.id === auth.currentModuleId)
  return m ? translateMenuTitle(m.title) : ''
})

const owner = ref<AppModule | null>(null)
const ownerName = computed(() => (owner.value ? translateMenuTitle(owner.value.title) : ''))
const switching = ref(false)

// 同一个组件实例会随地址变化复用,较早发出的反查晚到时不能盖掉较新地址的结果
let lookup = 0
watch(
  () => route.path,
  async path => {
    owner.value = null
    if (!multiApp.value) return
    const mine = ++lookup
    const found = await findOwnerModule(path)
    if (mine === lookup) owner.value = found
  },
  { immediate: true },
)

async function openInOwner() {
  if (!owner.value || switching.value) return
  switching.value = true
  try {
    await switchModule(owner.value.id, route.fullPath)
  } catch (e) {
    message.error(translateError(e))
  } finally {
    switching.value = false
  }
}
</script>

<template>
  <!-- 404 挂在布局壳内:撑满内容区,居中块放进卡片里(文字直接压在画布柔光上对比度不够) -->
  <n-card class="nf-card" content-style="padding: 40px 28px 36px">
    <div class="nf" role="status">
      <div class="code">404</div>
      <div class="msg">
        {{ owner ? t('notFound.otherApp', { app: ownerName }) : t('notFound.desc') }}
      </div>
      <div v-if="multiApp && currentApp" class="hint">
        {{ t('notFound.currentApp', { app: currentApp }) }}
      </div>
      <div class="actions">
        <n-button
          v-if="owner"
          type="primary"
          size="large"
          :loading="switching"
          @click="openInOwner"
        >
          {{ t('notFound.switchAndOpen', { app: ownerName }) }}
        </n-button>
        <n-button
          :type="owner ? 'default' : 'primary'"
          size="large"
          class="back"
          @click="router.replace('/')"
        >
          {{ t('notFound.back') }}
        </n-button>
        <n-button v-if="multiApp && !owner" text @click="router.push('/module')">
          {{ t('app.switchModule') }}
        </n-button>
      </div>
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
.hint {
  color: var(--text-3);
  font-size: 13px;
}
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 12px;
}
.back {
  min-width: 120px;
}
</style>
