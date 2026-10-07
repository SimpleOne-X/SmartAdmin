<script setup lang="ts">
// 业务系统的工作台。每个应用一个首页组件——这里和 dashboard/workbench.vue 是两个独立页面,
// 由各自应用的「工作台」菜单(component 字段)指向,切应用即换首页。
// 全部真数据拼装:欢迎横幅(user store + sessions + last-login)+ 待办事项(工作台待办扩展点,
// 内核默认空)+ 快捷方式(手动置顶 + 高频自动补位)+ 我的通知(notice/mine)。
// 应用要换成自己的业务统计,在自己的 views/ 下放同 key(dashboard/biz)的页面覆盖本页;别造写死的假数字。
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NList, NListItem, NEmpty, NTag } from 'naive-ui'
import { useElementSize } from '@vueuse/core'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore } from '#/stores/user'
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
const route = useRoute()
const router = useRouter()
const leaves = useMenuFlat()

// 内容区宽度(不是视口):窄档元信息的小圆点隐藏、通知时间换到标题下一行
const viewRef = ref<HTMLElement | null>(null)
const { width: pw } = useElementSize(viewRef)
const twoCols = computed(() => pw.value === 0 || pw.value >= 760)
const wideRow = computed(() => pw.value === 0 || pw.value >= 600)

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
  <div ref="viewRef" class="view">
    <!-- 欢迎横幅:身份 + 在线状态,一行说完 -->
    <n-card class="a-card banner-card" :bordered="true" content-style="padding: 18px 20px">
      <div class="banner">
        <div class="avatar">
          <Icon icon="ph:user" :width="26" />
        </div>
        <div class="banner-body">
          <div class="hi">
            {{ t('biz.welcome', { name: user.userInfo?.name ?? user.userInfo?.account ?? '' }) }}
          </div>
          <div class="tip">{{ t('biz.subtitle') }}</div>
          <div v-if="onlineText || lastLoginText" class="meta-row">
            <span v-if="onlineText">{{ onlineText }}</span>
            <span
              v-if="onlineText && lastLoginText && wideRow"
              class="meta-dot"
              aria-hidden="true"
            />
            <span>{{ lastLoginText }}</span>
          </div>
        </div>
      </div>
    </n-card>

    <div
      class="row"
      :style="{
        gridTemplateColumns: twoCols ? 'minmax(0, 1.35fr) minmax(0, 1fr)' : 'minmax(0, 1fr)',
      }"
    >
      <!-- 待办事项:内核默认空,消费方接入真实审批/工单系统后有内容 -->
      <n-card
        class="a-card"
        :title="t('biz.todo')"
        :bordered="true"
        content-style="padding: 2px 8px 8px"
      >
        <template v-if="todo && todo.totalCount > 0" #header-extra>
          <n-tag type="error" round size="small" :bordered="false">{{ todo.totalCount }}</n-tag>
        </template>
        <n-list v-if="todo && todo.items.length" hoverable :show-divider="false">
          <n-list-item
            v-for="i in todo.items"
            :key="i.id"
            class="list-item"
            :style="{ cursor: i.url ? 'pointer' : 'default' }"
            @click="i.url && router.push(i.url)"
          >
            <div class="todo-row">
              <div class="todo-main">
                <div class="todo-title">{{ i.title }}</div>
                <div v-if="i.description" class="todo-sub">{{ i.description }}</div>
              </div>
              <span class="todo-time">{{ fmtDateTime(i.createTime, { seconds: false }) }}</span>
            </div>
          </n-list-item>
        </n-list>
        <n-empty v-else :description="t('biz.todoEmpty')" class="empty" />
      </n-card>

      <!-- 快捷方式:实心星 + 实线框=手动置顶(可移除),空心星 + 虚线框=高频访问自动推荐(点星即置顶) -->
      <n-card
        class="a-card"
        :title="t('biz.quick')"
        :bordered="true"
        content-style="padding: 4px 16px 16px"
      >
        <template #header-extra>
          <n-button quaternary circle :aria-label="t('biz.quickAdd')" @click="pickerShow = true">
            <template #icon><Icon icon="ph:plus" :width="16" /></template>
          </n-button>
        </template>
        <div v-if="groups.pinned.length || groups.suggested.length">
          <template v-if="groups.pinned.length">
            <div class="quick-group-label">{{ t('biz.quickPinned') }}</div>
            <div class="quick-grid">
              <div v-for="s in groups.pinned" :key="s.path" class="quick-item pinned">
                <n-button quaternary class="quick-body" @click="router.push(s.path)">
                  <template #icon><AppIcon :icon="s.icon" :size="17" /></template>
                  <span class="quick-name">{{ translateMenuTitle(s.title) }}</span>
                </n-button>
                <n-button
                  quaternary
                  circle
                  class="quick-star"
                  :aria-label="t('biz.quickUnpin')"
                  @click.stop="unpin(s.path)"
                >
                  <template #icon>
                    <Icon icon="ph:star-fill" :width="14" class="star-on" />
                  </template>
                </n-button>
              </div>
            </div>
          </template>
          <template v-if="groups.suggested.length">
            <div class="quick-group-label" :class="{ 'after-pinned': groups.pinned.length }">
              {{ t('biz.quickSuggested') }}
            </div>
            <div class="quick-grid">
              <div v-for="s in groups.suggested" :key="s.path" class="quick-item suggested">
                <n-button quaternary class="quick-body" @click="router.push(s.path)">
                  <template #icon><AppIcon :icon="s.icon" :size="17" /></template>
                  <span class="quick-name">{{ translateMenuTitle(s.title) }}</span>
                </n-button>
                <n-button
                  quaternary
                  circle
                  class="quick-star"
                  :aria-label="t('biz.quickPin')"
                  @click.stop="pin(s.path)"
                >
                  <template #icon><Icon icon="ph:star" :width="14" class="star-off" /></template>
                </n-button>
              </div>
            </div>
          </template>
        </div>
        <n-empty v-else :description="t('biz.noMenus')" class="empty" />
      </n-card>
    </div>

    <!-- 通知:没有"查看全部"——卡片本身就是完整内容;整行点击进「我的通知」 -->
    <n-card
      class="a-card"
      :title="t('biz.notices')"
      :bordered="true"
      content-style="padding: 2px 8px 8px"
    >
      <n-list v-if="notices.length" hoverable clickable :show-divider="false">
        <n-list-item
          v-for="n in notices"
          :key="n.id"
          class="list-item"
          @click="router.push('/personal/notice')"
        >
          <!-- 用 grid 而不是 flex:标题单行省略要靠 minmax(0, 1fr) 才收得住;窄屏时间换到标题下一行。
               已读的圆点位置留空占位,标题与未读对齐 -->
          <div class="notice-row" :class="{ stacked: !wideRow }">
            <span class="dot" :class="{ unread: !n.isRead }" />
            <span class="notice-title" :class="{ unread: !n.isRead }" :title="n.title">
              {{ n.title }}
            </span>
            <span class="notice-time">{{ fmtDateTime(n.publishTime, { seconds: false }) }}</span>
          </div>
        </n-list-item>
      </n-list>
      <n-empty v-else :description="t('biz.noticesEmpty')" class="empty" />
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
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--gap-card);
  align-content: start;
}
/* 横幅卡底色取选中态令牌(主色 6% / 暗色 14%),与管理工作台同一套 */
.banner-card {
  background-image: linear-gradient(var(--sel-bg), var(--sel-bg));
}
.banner {
  display: flex;
  align-items: center;
  gap: 16px;
}
.avatar {
  flex: none;
  width: 52px;
  height: 52px;
  border-radius: 50%;
  display: grid;
  place-items: center;
  color: var(--on-acc);
  background-image: linear-gradient(135deg, var(--signal), var(--login-accent-2));
  box-shadow: 0 8px 20px -8px var(--signal-glow);
}
.banner-body {
  min-width: 0;
  flex: 1;
}
.hi {
  font-size: 18px;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--text-1);
}
.tip {
  margin-top: 2px;
  font-size: 14px;
  color: var(--text-2);
}
.meta-row {
  margin-top: 8px;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px 8px;
  font-size: 13px;
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}
.meta-dot {
  width: 3px;
  height: 3px;
  border-radius: 50%;
  background: currentColor;
  opacity: 0.7;
  flex-shrink: 0;
}
.row {
  display: grid;
  align-items: start;
  gap: var(--gap-card);
}
/* 列表行:圆角 10px,悬停底色由 NList 自带 */
.list-item {
  border-radius: var(--radius-md);
  padding: 8px 10px;
}
.empty {
  padding: 26px 0;
}
.todo-row {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: flex-start;
  gap: 2px 16px;
}
.todo-main {
  flex: 1 1 220px;
  min-width: 0;
}
.todo-title {
  font-weight: 500;
  color: var(--text-1);
}
.todo-sub {
  margin-top: 2px;
  font-size: 13px;
  color: var(--text-3);
}
.todo-time {
  font-size: 13px;
  white-space: nowrap;
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}
.quick-group-label {
  margin: 0 0 8px;
  font-size: 13px;
  color: var(--text-3);
}
.quick-group-label.after-pinned {
  margin-top: 16px;
}
.quick-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 10px;
}
.quick-item {
  display: flex;
  align-items: center;
  min-width: 0;
  border-radius: var(--radius-md);
}
.quick-item.pinned {
  border: 1px solid var(--hairline-strong);
  background: var(--fill);
}
.quick-item.suggested {
  border: 1px dashed var(--hairline-strong);
}
.quick-body {
  flex: 1 1 auto;
  min-width: 0;
  height: 40px;
  justify-content: flex-start;
}
.quick-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.quick-star {
  flex: none;
  margin-right: 3px;
}
.star-on {
  color: var(--warn);
}
.star-off {
  color: var(--text-3);
}
.notice-row {
  display: grid;
  grid-template-columns: 6px minmax(0, 1fr) auto;
  align-items: center;
  column-gap: 10px;
  row-gap: 2px;
  overflow: hidden;
}
.notice-row.stacked {
  grid-template-columns: 6px minmax(0, 1fr);
}
.notice-row.stacked .notice-time {
  grid-column: 2;
}
.dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: transparent;
}
.dot.unread {
  background: var(--signal);
}
.notice-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-1);
}
.notice-title.unread {
  font-weight: 500;
}
.notice-time {
  font-size: 13px;
  white-space: nowrap;
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}
</style>
