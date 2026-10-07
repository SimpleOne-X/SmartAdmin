<script setup lang="ts">
import { computed, h, nextTick, onMounted, onScopeDispose, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  NAvatar,
  NButton,
  NButtonGroup,
  NDropdown,
  NTooltip,
  NMenu,
  NBreadcrumb,
  NBreadcrumbItem,
  useMessage,
  type DropdownOption,
} from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useBreakpoints, breakpointsTailwind } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { useAppStore, type Locale } from '#/stores/app'
import { useUserStore } from '#/stores/user'
import { useAuthStore } from '#/stores/auth'
import { useLayoutMenu } from '#/composables/useLayoutMenu'
import { useMenuFlat } from '#/composables/useMenuFlat'
import { useSite } from '#/composables/useSite'
import { useFullscreenToggle } from '#/composables/useFullscreenToggle'
import { useHeaderTools } from '#/composables/useHeaderTools'
import { authApi } from '#/api'
import { clearClientCache } from '#/composables/clearClientCache'
import { beginVoluntaryLogout } from '#/composables/useRealtime'
import { resetRouter } from '#/router'
import { translateError } from '#/utils/error'
import SmartLogo from '#/components/SmartLogo.vue'
import AppIcon from '#/components/AppIcon.vue'
import SettingsDrawer from './SettingsDrawer.vue'
import NoticeBell from './NoticeBell.vue'

withDefaults(
  defineProps<{
    showCollapse?: boolean
    showBrand?: boolean
    topMenu?: 'full' | 'l1' | 'l2' | null
    /** 顶栏是否出搜索按钮:侧栏已经带搜索入口时由 default.vue 关掉,免得同屏两个入口 */
    showSearch?: boolean
  }>(),
  { showCollapse: true, showBrand: false, topMenu: null, showSearch: true },
)
const emit = defineEmits<{ search: [] }>()

const app = useAppStore()
const { site } = useSite()
const user = useUserStore()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const message = useMessage()
const { t } = useI18n()
// 手机视口(< 768,与 layouts/default.vue 同一断点):顶栏只留侧栏开关 / 通知 / 头像,
// 其余入口(外观设置、切换应用、语言)收进头像菜单,见 userOptions。
const isMobile = useBreakpoints(breakpointsTailwind).smaller('md')
// 自研而非 VueUse useFullscreen:后者在 iPadOS 上只探标准 API,把明明能全屏的设备判成不支持。
// isSupported=false(如 iPhone)时整个按钮不渲染,不留点了没反应的按钮。
const {
  isSupported: fullscreenSupported,
  isFullscreen,
  toggle: toggleFullscreen,
} = useFullscreenToggle()
const { menuOptions, l1Options, l2Options, activeKey, selectedL1, onSelect, onSelectL1 } =
  useLayoutMenu()
const flat = useMenuFlat()
// 扩展模块登记的顶栏入口(registerHeaderTool),渲染在内置铃铛左侧。
const headerTools = useHeaderTools()

const settingsOpen = ref(false)

const title = computed(() => {
  const m = route.meta.title as string | undefined
  if (!m) return ''
  return m.includes('.') ? t(m) : m
})
const crumbs = computed(() => flat.value.find(l => l.path === route.path)?.breadcrumb ?? [])

// 前进 / 后退:读 vue-router 写进 history.state 的 back / forward 指针,没有可去的地方就置灰。
// 页签切换也是 router.push,所以后退会沿着浏览历史回到上一个页签的页面,和浏览器自己的后退一致。
const canBack = ref(false)
const canForward = ref(false)
function syncHistory() {
  const s = window.history.state as { back?: string | null; forward?: string | null } | null
  canBack.value = !!s?.back
  canForward.value = !!s?.forward
}
onMounted(syncHistory)
// afterEach 在 history.state 更新之后触发;nextTick 再兜一层,保证读到的是落定后的值。
const stopHistoryHook = router.afterEach(() => void nextTick(syncHistory))
onScopeDispose(stopHistoryHook)

// 语言名按各自书写系统展示,不随当前 locale 翻译。
const localeOptions: DropdownOption[] = [
  { label: '简体中文', key: 'zh-CN' },
  { label: 'English', key: 'en-US' },
]
function onLocale(key: string) {
  app.setLocale(key as Locale)
}

// 下拉图标(用户菜单:个人中心 / 外观设置 / 切换应用 / 清除缓存 / 退出)。
// color 用于危险项(退出)整行红字+红图标。
const renderUserIcon = (name: string, color?: string) => () =>
  h(Icon, { icon: name, width: 16, style: color ? { color } : undefined })

const displayName = computed(() => user.userInfo?.name ?? user.userInfo?.account ?? '')
const initial = computed(() => displayName.value.trim().slice(0, 1).toUpperCase())

// 下拉:账号信息头 / 个人中心 / 外观设置 / 切换应用 / 清除缓存 / 退出;五块能力进 /personal 二级壳(见 PersonalLayout)。
// 下拉层 Teleport 到 body,不吃本组件的 scoped 样式,账号头用行内样式。
const userOptions = computed<DropdownOption[]>(() => {
  const list: DropdownOption[] = [
    {
      key: 'header',
      type: 'render',
      render: () =>
        h(
          'div',
          {
            style:
              'display:flex;flex-direction:column;gap:2px;padding:8px 12px 6px;min-width:200px;',
          },
          [
            h('b', { style: 'font-size:var(--font-size-md);font-weight:600;' }, displayName.value),
            user.userInfo?.account && user.userInfo.account !== displayName.value
              ? h(
                  'span',
                  { style: 'font-size:var(--font-size-sm);color:var(--text-3);' },
                  user.userInfo.account,
                )
              : null,
          ],
        ),
    },
    { type: 'divider', key: 'd0' },
    { label: t('app.personalCenter'), key: 'personal', icon: renderUserIcon('ph:user-circle') },
    { label: t('app.appearance'), key: 'settings', icon: renderUserIcon('ph:sliders-horizontal') },
  ]
  // 切换应用:多应用门户才有;顶栏那颗圆形按钮在手机上被收起,这里是它的落脚点
  if (auth.modules.length > 1)
    list.push({
      label: t('app.switchModule'),
      key: 'module',
      icon: renderUserIcon('ph:squares-four'),
    })
  // 语言:顶栏的语言按钮在手机上被收起,收进二级菜单
  if (isMobile.value)
    list.push({
      label: t('app.language'),
      key: 'locale',
      icon: renderUserIcon('ph:translate'),
      children: localeOptions.map(o => ({ ...o, key: `locale:${o.key}` })),
    })
  list.push(
    { label: t('app.clearCache'), key: 'clearCache', icon: renderUserIcon('ph:broom') },
    { type: 'divider', key: 'd1' },
    // 退出用危险色突出,避免与普通项同权。
    {
      label: t('app.logout'),
      key: 'logout',
      icon: renderUserIcon('ph:sign-out', 'var(--err)'),
      props: { style: 'color: var(--err)' },
    },
  )
  return list
})
async function onUser(key: string) {
  if (key === 'personal') router.push('/personal/profile')
  else if (key === 'settings') settingsOpen.value = true
  else if (key === 'module') router.push('/module')
  else if (key.startsWith('locale:')) onLocale(key.slice('locale:'.length))
  else if (key === 'clearCache') await onClearCache()
  else if (key === 'logout') await logout()
}
async function onClearCache() {
  try {
    await clearClientCache()
    message.success(t('app.cacheCleared'))
  } catch (e) {
    message.error(translateError(e))
  }
}
async function logout() {
  // 先断实时并标记自愿退出:logout API 会 Revoke 会话并推 force-logout,避免误弹「您已被强制下线」。
  await beginVoluntaryLogout()
  try {
    await authApi.logout()
  } catch (e) {
    message.error(translateError(e)) // 尽力而为,不阻断登出
  }
  resetRouter()
  auth.reset() // 内部会 clearTabs()
  user.clear()
  router.replace('/login')
}
</script>

<template>
  <div class="bar">
    <div class="left">
      <n-button
        v-if="showCollapse"
        quaternary
        circle
        :aria-label="t('app.collapse')"
        @click="app.toggleCollapsed()"
      >
        <Icon icon="ph:sidebar-simple" :width="19" />
      </n-button>
      <n-button-group size="small" class="mobile-hide">
        <n-button
          quaternary
          circle
          :disabled="!canBack"
          :aria-label="t('app.back')"
          @click="router.back()"
        >
          <Icon icon="ph:caret-left" :width="16" />
        </n-button>
        <n-button
          quaternary
          circle
          :disabled="!canForward"
          :aria-label="t('app.forward')"
          @click="router.forward()"
        >
          <Icon icon="ph:caret-right" :width="16" />
        </n-button>
      </n-button-group>
      <div v-if="showBrand" class="brand">
        <SmartLogo :size="26" />
        <span class="brand-name mobile-hide">{{ site.title }}</span>
      </div>
      <!-- 手机上面包屑收起,换成当前页标题,免得顶栏只剩一个 Logo、看不出自己在哪一页 -->
      <n-breadcrumb v-if="!isMobile && app.showBreadcrumb && crumbs.length" class="crumb">
        <n-breadcrumb-item v-for="(c, i) in crumbs" :key="i">{{ c }}</n-breadcrumb-item>
      </n-breadcrumb>
      <span v-else class="title">{{ title }}</span>
    </div>

    <div v-if="topMenu" class="center">
      <n-menu
        v-if="topMenu === 'full'"
        mode="horizontal"
        responsive
        :options="menuOptions"
        :value="activeKey"
        @update:value="onSelect"
      />
      <n-menu
        v-else-if="topMenu === 'l1'"
        mode="horizontal"
        responsive
        :options="l1Options"
        :value="selectedL1"
        @update:value="onSelectL1"
      />
      <n-menu
        v-else
        mode="horizontal"
        responsive
        :options="l2Options"
        :value="activeKey"
        @update:value="onSelect"
      />
    </div>

    <!-- mobile-hide:窄屏(<768px)藏起来的次要项。顶栏总宽 > 屏宽时右端会被 layout 的 overflow:hidden 裁掉,
         而全站唯一的登出入口就在最右边的用户下拉里 —— 手机上不能因为一排图标而退不出登录。
         手机只留侧栏开关(default.vue 的 .hbg)/ 通知 / 头像;外观设置、切换应用、语言进头像菜单,
         搜索有侧栏里的入口与 Ctrl+K,全屏在手机上无意义,主题模式在外观设置里。 -->
    <div class="right">
      <n-tooltip v-if="showSearch">
        <template #trigger>
          <n-button
            quaternary
            circle
            class="mobile-hide"
            :aria-label="t('app.search')"
            @click="emit('search')"
          >
            <Icon icon="ph:magnifying-glass" :width="19" />
          </n-button>
        </template>
        {{ t('app.search') }} (Ctrl+K)
      </n-tooltip>

      <n-tooltip>
        <template #trigger>
          <n-button
            quaternary
            circle
            class="mobile-hide"
            :aria-label="app.isDark ? t('app.dark') : t('app.light')"
            @click="app.toggleDark()"
          >
            <Icon :icon="app.isDark ? 'ph:moon' : 'ph:sun'" :width="19" />
          </n-button>
        </template>
        {{ app.isDark ? t('app.dark') : t('app.light') }}
      </n-tooltip>

      <n-tooltip v-if="fullscreenSupported">
        <template #trigger>
          <n-button
            quaternary
            circle
            class="mobile-hide"
            :aria-label="isFullscreen ? t('app.exitFullscreen') : t('app.fullscreen')"
            @click="toggleFullscreen()"
          >
            <Icon :icon="isFullscreen ? 'ph:corners-in' : 'ph:corners-out'" :width="19" />
          </n-button>
        </template>
        {{ isFullscreen ? t('app.exitFullscreen') : t('app.fullscreen') }}
      </n-tooltip>

      <n-dropdown :options="localeOptions" @select="onLocale">
        <n-button quaternary circle class="mobile-hide" :aria-label="t('app.language')">
          <Icon icon="ph:translate" :width="19" />
        </n-button>
      </n-dropdown>

      <!-- 扩展模块的入口:图标模式画成同款圆形按钮,组件模式整块交给它自己(自带角标/弹层时用) -->
      <template v-for="tool in headerTools" :key="tool.key">
        <component :is="tool.component" v-if="tool.component" />
        <n-tooltip v-else>
          <template #trigger>
            <n-button
              quaternary
              circle
              :class="{ 'mobile-hide': !tool.keepOnMobile }"
              :aria-label="tool.label"
              @click="tool.onClick()"
            >
              <AppIcon :icon="tool.icon" :size="19" />
            </n-button>
          </template>
          {{ tool.label }}
        </n-tooltip>
      </template>

      <NoticeBell />

      <!-- 导航到应用选择页,而非下拉直切:应用一多下拉就难用,且"设为默认"只在选择页上。 -->
      <n-tooltip v-if="auth.modules.length > 1">
        <template #trigger>
          <n-button
            quaternary
            circle
            class="mobile-hide"
            :aria-label="t('app.switchModule')"
            @click="router.push('/module')"
          >
            <Icon icon="ph:squares-four" :width="19" />
          </n-button>
        </template>
        {{ t('app.switchModule') }}
      </n-tooltip>

      <n-dropdown :options="userOptions" placement="bottom-end" @select="onUser">
        <div class="top-user" tabindex="0" role="button" :aria-label="t('app.profile')">
          <!-- 有头像用头像(profile 页可传),没有回落姓名首字,再没有回落原图标;头像来源见 stores/user.ts 的 avatar 注释 -->
          <n-avatar v-if="user.userInfo?.avatar" round :size="28" :src="user.userInfo.avatar" />
          <n-avatar v-else-if="initial" round :size="28">{{ initial }}</n-avatar>
          <Icon v-else icon="ph:user-circle" :width="24" />
          <span class="top-user-name mobile-hide">{{ displayName }}</span>
          <Icon icon="ph:caret-down" :width="13" class="caret mobile-hide" />
        </div>
      </n-dropdown>
    </div>

    <SettingsDrawer v-model:show="settingsOpen" />
  </div>
</template>

<style scoped>
.bar {
  height: 100%;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 14px 0 10px;
}
/* 可收缩(min-width:0 + overflow):右侧那组是刚需(含唯一的登出入口),要挤先挤左边的标题/面包屑。 */
.left {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
}
.brand {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text-1);
  font-weight: 600;
  flex-shrink: 0;
}
.brand-name {
  font-size: 15px;
  letter-spacing: -0.02em;
}
.crumb {
  margin-left: 6px;
  font-size: 14px;
  min-width: 0;
}
.title {
  margin-left: 6px;
  font-size: 14px;
  font-weight: 500;
  color: var(--text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.center {
  flex: 1;
  min-width: 0;
  overflow: hidden;
}
.right {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  flex-shrink: 0;
}

/* 头像胶囊:头像 + 姓名 + 下拉箭头,悬停 / 聚焦出淡底与发丝描边 */
.top-user {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 8px 0 4px;
  margin-left: 4px;
  border: 1px solid transparent;
  border-radius: 999px;
  cursor: pointer;
  transition:
    background-color var(--transition-fast),
    border-color var(--transition-fast);
}
.top-user:hover,
.top-user:focus-visible {
  background: var(--hover);
  border-color: var(--hairline);
  outline: none;
}
.top-user-name {
  font-size: 14px;
  font-weight: 500;
  white-space: nowrap;
}
.caret {
  color: var(--text-3);
}
@media (max-width: 1100px) {
  .top-user-name {
    display: none;
  }
}

/* 窄屏(断点与 layouts/default.vue 的 breakpointsTailwind.smaller('md') 一致):
   只留侧栏开关 / 通知 / 头像,其余隐藏,保证最右的登出入口不被裁掉。 */
@media (max-width: 767px) {
  .bar {
    gap: 8px;
    padding: 0 8px;
  }
  .top-user {
    padding: 0 4px;
    margin-left: 0;
  }
  .mobile-hide {
    display: none !important;
  }
}
</style>
