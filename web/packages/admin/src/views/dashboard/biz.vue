<script setup lang="ts">
// 业务系统的工作台。每个应用一个首页组件——这里和 dashboard/workbench.vue 是两个独立页面,
// 由各自应用的「工作台」菜单(component 字段)指向,切应用即换首页。
// 全部真数据拼装:欢迎横幅(user store + sessions + last-login)+ 待办事项(工作台待办扩展点,
// 内核默认空)+ 快捷方式(手动置顶 + 高频自动补位)+ 我的通知(notice/mine)。
// 应用要换成自己的业务统计,在自己的 views/ 下放同 key(dashboard/biz)的页面覆盖本页;别造写死的假数字。
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NList, NListItem, NEmpty, NTag } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore } from '#/stores/user'
import { useAppStore } from '#/stores/app'
import { btnGrad } from '#/theme/mix'
import { noticeApi, personalApi } from '#/api'
import type {
  NoticeMineItem,
  LastLoginInfo,
  WorkbenchTodoSummary,
  UserShortcutItem,
} from '#/types/api'
import { useMenuFlat } from '#/composables/useMenuFlat'
import { translateMenuTitle } from '#/locales/menuTitle'
import { onlineDurationParts } from './onlineDuration'
import { buildShortcutGroups } from './shortcutGroups'
import ShortcutPicker from './ShortcutPicker.vue'
import AppIcon from '#/components/AppIcon.vue'
import { fmtDateTime } from '#/utils/format'
import { uaSummary } from '#/utils/ua'

const { t } = useI18n()
const user = useUserStore()
const app = useAppStore()
const route = useRoute()
const router = useRouter()
const leaves = useMenuFlat()

const avatarStyle = computed(() => ({ background: btnGrad(app.accent) }))

// ── 在线时长 + 上次登录 ─────────────────────────────────────
const currentLoginTime = ref<string | null>(null)
const lastLogin = ref<LastLoginInfo | null>(null)
const now = ref(new Date())
let tickTimer: ReturnType<typeof setInterval> | undefined

const onlineText = computed(() => {
  if (!currentLoginTime.value) return ''
  const { hours, minutes } = onlineDurationParts(currentLoginTime.value, now.value)
  return hours > 0
    ? t('biz.onlineDuration', { hours, minutes })
    : t('biz.onlineDurationShort', { minutes })
})
const lastLoginText = computed(() => {
  if (!lastLogin.value) return t('biz.firstLogin')
  const device = uaSummary(lastLogin.value.userAgent)
  const ip = lastLogin.value.ip ? ` · ${lastLogin.value.ip}` : ''
  const base = t('biz.lastLogin', { time: fmtDateTime(lastLogin.value.time, { seconds: false }) })
  return `${base}${ip}${device ? ` · ${device}` : ''}`
})

// ── 待办事项:内核默认空,消费方接入真实审批/工单系统后有内容 ──────────
const todo = ref<WorkbenchTodoSummary | null>(null)

// ── 快捷方式 ─────────────────────────────────────────────
const shortcuts = ref<UserShortcutItem[]>([])
const pickerShow = ref(false)
// 排除本页自身:工作台自己也是菜单树里的一条,不该出现在"去哪儿"的快捷方式里
const groups = computed(() =>
  buildShortcutGroups(
    shortcuts.value.filter(s => s.menuPath !== route.path),
    leaves.value,
  ),
)
const pinnedPaths = computed(() => shortcuts.value.filter(s => s.pinned).map(s => s.menuPath))

async function loadShortcuts() {
  try {
    shortcuts.value = await personalApi.shortcuts()
  } catch {
    shortcuts.value = []
  }
}
async function pin(path: string) {
  await personalApi.pinShortcut(path)
  await loadShortcuts()
}
async function unpin(path: string) {
  await personalApi.unpinShortcut(path)
  await loadShortcuts()
}

// ── 通知 ────────────────────────────────────────────────
const notices = ref<NoticeMineItem[]>([])

onMounted(async () => {
  tickTimer = setInterval(() => {
    now.value = new Date()
  }, 60_000)

  try {
    const sessions = await personalApi.sessions()
    currentLoginTime.value = sessions.find(s => s.isCurrent)?.loginTime ?? null
  } catch {
    // 在线时长拿不到就不显示这一段,不影响页面其余部分
  }
  try {
    lastLogin.value = await personalApi.lastLogin()
  } catch {
    lastLogin.value = null
  }
  try {
    todo.value = await personalApi.workbenchTodo()
  } catch {
    todo.value = null
  }
  await loadShortcuts()
  try {
    notices.value = (await noticeApi.mine({ page: 1, pageSize: 5 })).items
  } catch {
    // 首页不因一张卡挂了糊用户一脸红:通知取不到就空态,其余照常可用。
  }
})
onUnmounted(() => {
  if (tickTimer) clearInterval(tickTimer)
})
</script>

<template>
  <div class="view">
    <!-- 欢迎横幅:身份 + 在线状态,一行说完 -->
    <div class="banner">
      <div class="avatar" :style="avatarStyle">
        <Icon icon="ph:user" :width="26" color="#fff" />
      </div>
      <div class="banner-body">
        <div class="hi">
          {{ t('biz.welcome', { name: user.userInfo?.name ?? user.userInfo?.account ?? '' }) }}
        </div>
        <div class="tip">{{ t('biz.subtitle') }}</div>
        <div v-if="onlineText || lastLoginText" class="meta-row">
          <span v-if="onlineText">{{ onlineText }}</span>
          <span v-if="onlineText && lastLoginText" class="meta-dot" />
          <span>{{ lastLoginText }}</span>
        </div>
      </div>
    </div>

    <div class="row">
      <!-- 待办事项:内核默认空,消费方接入真实审批/工单系统后有内容 -->
      <n-card :title="t('biz.todo')" :bordered="true">
        <template v-if="todo && todo.totalCount > 0" #header-extra>
          <n-tag type="error" round size="small">{{ todo.totalCount }}</n-tag>
        </template>
        <div v-if="todo && todo.items.length" class="todo-list">
          <div
            v-for="i in todo.items"
            :key="i.id"
            class="todo-row"
            @click="i.url && router.push(i.url)"
          >
            <div class="todo-main">
              <div class="todo-title">{{ i.title }}</div>
              <div v-if="i.description" class="todo-sub">{{ i.description }}</div>
            </div>
            <span class="todo-time">{{ fmtDateTime(i.createTime, { seconds: false }) }}</span>
          </div>
        </div>
        <n-empty v-else :description="t('biz.todoEmpty')" />
      </n-card>

      <!-- 快捷方式:实心星=手动置顶(可移除),虚线框+空心星=高频访问自动推荐(点固定即置顶) -->
      <n-card :title="t('biz.quick')" :bordered="true">
        <template #header-extra>
          <n-button size="small" quaternary circle @click="pickerShow = true">
            <template #icon><Icon icon="ph:plus" :width="16" /></template>
          </n-button>
        </template>
        <div v-if="groups.pinned.length || groups.suggested.length">
          <div v-if="groups.pinned.length" class="quick-group-label">
            {{ t('biz.quickPinned') }}
          </div>
          <div v-if="groups.pinned.length" class="quick-grid">
            <div v-for="s in groups.pinned" :key="s.path" class="quick-item">
              <button
                class="quick-star active"
                type="button"
                :aria-label="t('biz.quickUnpin')"
                @click.stop="unpin(s.path)"
              >
                <Icon icon="ph:star-fill" :width="12" />
              </button>
              <div class="quick-body" @click="router.push(s.path)">
                <AppIcon :icon="s.icon" :size="17" />
                <span class="quick-name">{{ translateMenuTitle(s.title) }}</span>
              </div>
            </div>
          </div>
          <div v-if="groups.suggested.length" class="quick-group-label">
            {{ t('biz.quickSuggested') }}
          </div>
          <div v-if="groups.suggested.length" class="quick-grid">
            <div v-for="s in groups.suggested" :key="s.path" class="quick-item suggested">
              <button
                class="quick-star"
                type="button"
                :aria-label="t('biz.quickPin')"
                @click.stop="pin(s.path)"
              >
                <Icon icon="ph:star" :width="12" />
              </button>
              <div class="quick-body" @click="router.push(s.path)">
                <AppIcon :icon="s.icon" :size="17" />
                <span class="quick-name">{{ translateMenuTitle(s.title) }}</span>
              </div>
            </div>
          </div>
        </div>
        <n-empty v-else :description="t('biz.noMenus')" />
      </n-card>
    </div>

    <!-- 通知:没有"查看全部"——卡片本身就是完整内容 -->
    <n-card :title="t('biz.notices')" :bordered="true">
      <n-list v-if="notices.length" :show-divider="false">
        <n-list-item v-for="n in notices" :key="n.id">
          <div class="notice-row" @click="router.push('/personal/notice')">
            <span class="notice-title">
              <span v-if="!n.isRead" class="dot" />
              {{ n.title }}
            </span>
            <span class="notice-time">{{ fmtDateTime(n.publishTime, { seconds: false }) }}</span>
          </div>
        </n-list-item>
      </n-list>
      <n-empty v-else :description="t('biz.noticesEmpty')" />
    </n-card>

    <ShortcutPicker
      v-model:show="pickerShow"
      :exclude-paths="[...pinnedPaths, route.path]"
      @pick="pin"
    />
  </div>
</template>

<style scoped>
.view {
  display: flex;
  flex-direction: column;
  gap: var(--gap-card);
}
.banner {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 24px;
  border-radius: var(--radius-lg);
  background: var(--color-primary-light);
}
.avatar {
  flex-shrink: 0;
  width: 52px;
  height: 52px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
}
.banner-body {
  min-width: 0;
}
.hi {
  font-size: var(--font-size-lg);
  font-weight: 600;
  color: var(--color-text-primary);
}
.tip {
  color: var(--color-text-secondary);
  margin-top: 4px;
}
.meta-row {
  margin-top: 8px;
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13px;
  color: var(--color-text-tertiary);
  font-variant-numeric: tabular-nums;
}
.meta-dot {
  width: 3px;
  height: 3px;
  border-radius: 50%;
  background: var(--color-text-disabled);
  flex-shrink: 0;
}
.row {
  display: grid;
  grid-template-columns: 1.35fr 1fr;
  gap: var(--gap-card);
  align-items: start;
}
@media (max-width: 760px) {
  .row {
    grid-template-columns: 1fr;
  }
}
.todo-list {
  display: flex;
  flex-direction: column;
}
.todo-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 0;
  border-bottom: 1px solid var(--color-border);
  cursor: pointer;
}
.todo-row:last-child {
  border-bottom: none;
}
.todo-title {
  color: var(--color-text-primary);
  font-weight: 500;
}
.todo-sub {
  margin-top: 2px;
  font-size: 12px;
  color: var(--color-text-tertiary);
}
.todo-time {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--color-text-tertiary);
}
.quick-group-label {
  font-size: 12px;
  color: var(--color-text-tertiary);
  margin: 4px 0 8px;
}
.quick-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin-bottom: 4px;
}
.quick-item {
  position: relative;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}
.quick-item.suggested {
  border-style: dashed;
}
.quick-body {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 14px 8px 10px;
  cursor: pointer;
}
.quick-name {
  font-size: 13px;
  color: var(--color-text-secondary);
}
.quick-star {
  position: absolute;
  top: -6px;
  right: -6px;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  border: 1px solid var(--color-border);
  background: var(--color-bg-elevated);
  color: var(--color-text-disabled);
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}
.quick-star.active {
  color: var(--color-warning);
}
.notice-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  cursor: pointer;
}
.notice-title {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--color-text-primary);
}
.dot {
  flex-shrink: 0;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--color-primary);
}
.notice-time {
  flex-shrink: 0;
  color: var(--color-text-secondary);
  font-size: 12px;
}
</style>
