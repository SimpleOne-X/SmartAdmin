<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { NButton, NCard, NEmpty, NSpin, NTag, NTooltip, useMessage } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useAppStore } from '#/stores/app'
import { useAuthStore } from '#/stores/auth'
import { useUserStore } from '#/stores/user'
import { useModule } from '#/composables/useModule'
import { beginVoluntaryLogout } from '#/composables/useRealtime'
import { resetRouter } from '#/router'
import { translateError } from '#/utils/error'
import { authApi } from '#/api'
import AppIcon from '#/components/AppIcon.vue'
import SmartLogo from '#/components/SmartLogo.vue'
import { translateMenuTitle } from '#/locales/menuTitle'

const router = useRouter()
const message = useMessage()
const { t } = useI18n()
const app = useAppStore()
const auth = useAuthStore()
const user = useUserStore()
const busy = ref(false)

/** 应用图标取线条款:库里配的 duotone 款去掉后缀(其余图标集名字原样) */
const lineIcon = (icon?: string | null) =>
  (icon || 'ph:app-window').replace(/^(ph:.+)-duotone$/, '$1')

async function pick(id: number) {
  busy.value = true
  try {
    // switchModule = 建路由 + 清标签 + 落新应用首页(旧应用的标签在新应用里都是死链)
    await useModule().switchModule(id)
  } catch (e) {
    message.error(translateError(e))
  } finally {
    busy.value = false
  }
}

async function handleLogout() {
  await beginVoluntaryLogout()
  try {
    await authApi.logout()
  } catch {}
  resetRouter()
  auth.reset()
  user.clear()
  router.replace('/login')
}

async function setDefault(id: number) {
  try {
    await useModule().setDefault(id)
    message.success(t('common.success'))
  } catch (e) {
    message.error(translateError(e))
  }
}
</script>

<template>
  <div class="module-wrap">
    <!-- 页头:站点标志 + 标题 + 返回 + 主题切换(整屏页没有顶栏) -->
    <div class="module-head">
      <SmartLogo :size="34" />
      <h1>{{ t('module.choose') }}</h1>
      <!-- 从应用内点九宫格进来的(路由已就绪)才给返回;登录直落选择页时无处可返。 -->
      <n-button v-if="auth.routesReady" text class="module-back" @click="router.back()">
        <Icon icon="ph:arrow-left" :width="15" />
        <span style="margin-left: 4px">{{ t('module.back') }}</span>
      </n-button>
      <n-tooltip>
        <template #trigger>
          <n-button
            quaternary
            circle
            :class="{ 'module-theme': !auth.routesReady }"
            :aria-label="app.isDark ? t('app.light') : t('app.dark')"
            @click="app.toggleDark()"
          >
            <template #icon>
              <Icon :icon="app.isDark ? 'ph:moon' : 'ph:sun'" :width="18" />
            </template>
          </n-button>
        </template>
        {{ app.isDark ? t('app.light') : t('app.dark') }}
      </n-tooltip>
    </div>

    <!-- 空态:没有任何可访问的应用 -->
    <n-card v-if="!auth.modules.length" class="module-empty" content-style="padding: 48px 24px">
      <n-empty :description="t('module.empty')">
        <template #extra>
          <n-button @click="handleLogout">{{ t('app.logout') }}</n-button>
        </template>
      </n-empty>
    </n-card>

    <!-- 卡片网格:切换中整个网格盖加载圈 -->
    <n-spin v-else :show="busy" class="module-spin">
      <div class="grid">
        <!-- 卡片是可点 div:补 role/tabindex/键盘激活,让键盘用户也能选应用(a11y)。
             键盘事件只认卡片自身(.self),卡内「设为默认」按钮上的回车 / 空格不会连带切换应用 -->
        <n-card
          v-for="m in auth.modules"
          :key="m.id"
          class="mod-card"
          :class="{ current: m.id === auth.currentModuleId }"
          content-style="padding: 20px"
          role="button"
          tabindex="0"
          :aria-current="m.id === auth.currentModuleId ? 'true' : undefined"
          @click="pick(m.id)"
          @keydown.enter.self.prevent="pick(m.id)"
          @keydown.space.self.prevent="pick(m.id)"
        >
          <div class="mod-top">
            <AppIcon :icon="lineIcon(m.icon)" :size="28" class="ico" />
            <n-tag
              v-if="m.id === auth.defaultModuleId"
              size="small"
              type="primary"
              :bordered="false"
            >
              {{ t('module.isDefault') }}
            </n-tag>
            <n-button v-else text size="tiny" class="def" @click.stop="setDefault(m.id)">
              {{ t('module.setDefault') }}
            </n-button>
          </div>
          <div class="name">{{ translateMenuTitle(m.title) }}</div>
          <div class="code">{{ m.code }}</div>
        </n-card>
      </div>
    </n-spin>
  </div>
</template>

<style scoped>
.module-wrap {
  min-height: 100dvh;
  padding: clamp(32px, 9vh, 80px) 24px;
  display: flex;
  flex-direction: column;
  align-items: center;
}
.module-head {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
  max-width: 900px;
  margin-bottom: 24px;
}
.module-head h1 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--text-1);
}
/* 返回把右边的主题按钮一起推到最右;没有返回时由主题按钮自己推 */
.module-back {
  min-height: 32px;
  margin-left: auto;
}
.module-theme {
  margin-left: auto;
}
.module-empty,
.module-spin {
  width: 100%;
  max-width: 900px;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 12px;
  width: 100%;
}
.mod-card {
  cursor: pointer;
  transition: box-shadow 0.25s var(--ease);
}
/* macOS 的卡片悬停不位移,只让投影加深一档 */
.mod-card:hover {
  box-shadow: var(--shadow-2);
}
/* 当前所在应用:与当前页签、侧栏选中同一套选中态——强调色淡底 + 1px 描边 + 外光晕,不用粗实线框 */
.mod-card.current {
  background-image: linear-gradient(var(--sel-bg), var(--sel-bg));
  box-shadow:
    0 0 0 1px var(--sel-ring),
    0 0 0 4px var(--sel-halo),
    var(--shadow-1);
}
.mod-card.current .name {
  color: var(--sel-fg);
}
.mod-card:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 2px;
}
.mod-top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  min-height: 32px;
  margin-bottom: 8px;
}
.ico {
  color: var(--signal);
}
/* 「设为默认」:触摸目标 32px 高,贴齐卡片右上角 */
.def {
  height: 32px;
  margin: -4px -6px 0 0;
  padding: 0 6px;
}
.name {
  font-size: 16px;
  font-weight: 600;
  color: var(--text-1);
}
.code {
  margin-top: 4px;
  font-family: var(--font-mono);
  font-size: 13px;
  color: var(--text-3);
}
@media (prefers-reduced-motion: reduce) {
  .mod-card:hover {
    transform: none;
  }
}
</style>
