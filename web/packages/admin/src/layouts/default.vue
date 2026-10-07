<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { RouterView } from 'vue-router'
import { NDrawer, NDrawerContent, NButton } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useBreakpoints, breakpointsTailwind, onKeyStroke } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { useAppStore, type LayoutMode } from '#/stores/app'
import { useUserStore } from '#/stores/user'
import { useLayoutMenu } from '#/composables/useLayoutMenu'
import { useTabsStore } from '#/stores/tabs'
import { useSite } from '#/composables/useSite'
import { useRealtime } from '#/composables/useRealtime'
import { useVisualViewportHeight } from '#/composables/useVisualViewportHeight'
import { provideShellBreakpoint } from '#/composables/useShellBreakpoint'
import ErrorBoundary from '#/components/ErrorBoundary/index.vue'
import MenuSearch from '#/components/MenuSearch.vue'
import AppWatermark from '#/components/AppWatermark.vue'
import AppHeader from './AppHeader.vue'
import SideNav from './components/SideNav.vue'
import TabsBar from './TabsBar.vue'

const { t } = useI18n()
const app = useAppStore()
const user = useUserStore()
const tabs = useTabsStore()
const { site } = useSite()
const { menuOptions, l1Options, l2Options, selectedL1, activeKey, onSelect, onSelectL1 } =
  useLayoutMenu()

// 实时通知(SignalR):鉴权外壳挂载即连、卸载即断。后端未开启实时时连接静默失败,退回轮询/惰性 401(纯增强)。
const { start: startRealtime, stop: stopRealtime } = useRealtime()
onMounted(startRealtime)
onUnmounted(stopRealtime)

// 维护 --vvh(真实可视高度):软键盘弹起时 dvh 不变,只有 visualViewport 会缩。
// 布局壳自身用 100dvh 就够,变量留给需要贴着键盘排版的页面(见 composables/useVisualViewportHeight)。
useVisualViewportHeight()

const isMobile = useBreakpoints(breakpointsTailwind).smaller('md')
const mobileOpen = ref(false)
// 菜单搜索面板(Ctrl+K 同一个):侧栏里的只读输入框、顶栏的搜索按钮都开它,面板本身在 MenuSearch 里。
const searchOpen = ref(false)

// 壳层三档(wide / mid / narrow):按内容区(.page 的 content box)宽度判,盯住的就是下面的 .page。
// 页面里用 useShellBreakpoint() 读;样式里用 .page 上的 bp-* 类 / data-bp。阈值与理由见 composables/useShellBreakpoint。
const pageRef = ref<HTMLElement>()
const { bp } = provideShellBreakpoint(pageRef)

// 窄屏忽略 layoutMode,统一走 mobile(单列 + 抽屉侧栏)。
const mode = computed<LayoutMode | 'mobile'>(() => (isMobile.value ? 'mobile' : app.layoutMode))

// 除 horizontal(无侧栏)外都有侧栏。
const withSider = new Set<LayoutMode>([
  'vertical',
  'vertical-mix',
  'vertical-hybrid-header-first',
  'top-hybrid-sidebar-first',
  'top-hybrid-header-first',
])
// 顶栏显示品牌的模式(其余由侧栏/rail 顶部显示品牌,保证每种布局品牌只出现一处)。
const headerBrandModes = new Set<string>([
  'horizontal',
  'vertical-hybrid-header-first',
  'top-hybrid-sidebar-first',
  'top-hybrid-header-first',
])
const showSider = computed(() => !isMobile.value && withSider.has(app.layoutMode))
const sideShowBrand = computed(() => mode.value === 'vertical' || mode.value === 'vertical-mix')
// 侧栏整个隐藏时品牌回到顶栏,否则这种布局下品牌无处可显
const headerShowBrand = computed(
  () => isMobile.value || headerBrandModes.has(mode.value) || asideHidden.value,
)
// 顶栏菜单:horizontal=完整树;两个 header-first=一级;侧边优先=二级(选中的一级的子菜单)。
const headerTopMenu = computed<'full' | 'l1' | 'l2' | null>(() => {
  switch (mode.value) {
    case 'horizontal':
      return 'full'
    case 'vertical-hybrid-header-first':
    case 'top-hybrid-header-first':
      return 'l1'
    case 'top-hybrid-sidebar-first':
      return 'l2'
    default:
      return null
  }
})
// 折叠按钮:有可折叠树侧栏的模式(侧边优先的一级 rail 固定宽,不折叠)。
const collapsibleSider = new Set<string>([
  'vertical',
  'vertical-mix',
  'vertical-hybrid-header-first',
  'top-hybrid-header-first',
])
const showCollapse = computed(() => !isMobile.value && collapsibleSider.has(mode.value))
// 折叠 = 把侧栏整个隐藏(不是收成图标条):内容区铺满,再点一次(或 Ctrl/⌘+B)恢复。
const asideHidden = computed(
  () => showSider.value && app.collapsed && collapsibleSider.has(mode.value),
)
// 侧栏树列自带搜索入口(品牌下的只读输入框)时,顶栏不再重复放搜索按钮;
// 一级 rail 与侧栏隐藏时侧栏里没有输入框,搜索入口回到顶栏。
const sideHasSearch = computed(
  () => showSider.value && !asideHidden.value && mode.value !== 'top-hybrid-sidebar-first',
)

// Ctrl/⌘+B 收起 / 展开侧栏(只在有可折叠侧栏的布局生效)。
// 编辑器里 Ctrl+B 是「加粗」,在输入控件 / 富文本 / 代码编辑器里不抢。
function inEditable(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) return false
  return (
    ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) ||
    target.isContentEditable ||
    !!target.closest('.cm-editor, .md-editor, .md-editor-v3')
  )
}
onKeyStroke('b', e => {
  if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return
  if (!showCollapse.value || inEditable(e.target)) return
  e.preventDefault()
  app.toggleCollapsed()
})

// 非特殊侧栏内容:vertical=完整树;两个 header-first hybrid=二级子菜单树。
const asideOptions = computed(() =>
  mode.value === 'vertical' ? menuOptions.value : l2Options.value,
)
const asideFullWidth = computed(() => {
  if (mode.value === 'vertical-mix') return 'calc(var(--rail-w) + var(--sidebar-w))'
  if (mode.value === 'top-hybrid-sidebar-first') return 'var(--rail-w)' // 一级 icon rail,固定宽
  return 'var(--sidebar-w)'
})
// 收起 = 宽度过渡到 0(侧栏不卸载,才有得过渡)。--aside-w 是满宽,内容按它定宽、被 aside 的 overflow 裁掉,
// 收窄过程中菜单文字不会跟着重排。
const asideStyle = computed(() => ({
  '--aside-w': asideFullWidth.value,
  width: asideHidden.value ? '0px' : 'var(--aside-w)',
}))

const transitionName = computed(() =>
  app.pageTransition === 'none' ? undefined : app.pageTransition,
)

const watermarkWho = computed(() => ({
  name: user.userInfo?.name,
  account: user.userInfo?.account,
  org: user.userInfo?.orgName,
  phoneTail: user.userInfo?.phoneTail,
}))

function onMobileSelect(key: string) {
  onSelect(key)
  mobileOpen.value = false
}
function onMobileSearch() {
  mobileOpen.value = false
  searchOpen.value = true
}

// 刷新标签:store 置 excludeName(逐出缓存)+ 递增 reloadKey;这里 v-if 关开一次令当前页重挂,再放行缓存。
const rvShow = ref(true)
watch(
  () => tabs.reloadKey,
  async () => {
    rvShow.value = false
    await nextTick()
    rvShow.value = true
    await nextTick()
    tabs.excludeName = ''
  },
)
</script>

<template>
  <div class="layout" :class="[`mode-${mode}`, { 'aside-hidden': asideHidden }]">
    <!-- 侧栏:玻璃面板,悬浮在壁纸上 -->
    <!-- 收起时仍挂载(宽度过渡到 0),inert 让它退出 Tab 序列与无障碍树 -->
    <aside
      v-if="showSider"
      class="aside glass"
      :class="{ 'aside--mix': mode === 'vertical-mix', 'aside--hidden': asideHidden }"
      :style="asideStyle"
      :inert="asideHidden"
    >
      <template v-if="mode === 'vertical-mix'">
        <SideNav
          class="rail"
          :options="l1Options"
          :value="selectedL1"
          rail
          show-brand
          @select="onSelectL1"
        />
        <SideNav
          class="l2col"
          :options="l2Options"
          :value="activeKey"
          show-search
          @select="onSelect"
          @search="searchOpen = true"
        />
      </template>
      <!-- 顶部混合-侧边优先:侧栏只放一级 icon rail(二级在顶栏) -->
      <SideNav
        v-else-if="mode === 'top-hybrid-sidebar-first'"
        :options="l1Options"
        :value="selectedL1"
        rail
        @select="onSelectL1"
      />
      <SideNav
        v-else
        :options="asideOptions"
        :value="activeKey"
        :show-brand="sideShowBrand"
        show-search
        @select="onSelect"
        @search="searchOpen = true"
      />
    </aside>

    <!-- 顶部玻璃面板:工具栏 + 页签条同一块,页签条在工具栏下方、以一条发丝线分隔 -->
    <div class="shell-head glass" :class="{ 'shell-head--static': !app.fixedHeader }">
      <header class="toolbar">
        <n-button
          v-if="isMobile"
          quaternary
          circle
          class="hbg"
          :aria-label="t('app.openMenu')"
          @click="mobileOpen = true"
        >
          <Icon icon="ph:sidebar-simple" :width="19" />
        </n-button>
        <div class="apph">
          <AppHeader
            :show-collapse="showCollapse"
            :show-brand="headerShowBrand"
            :top-menu="headerTopMenu"
            :show-search="!sideHasSearch"
            @search="searchOpen = true"
          />
        </div>
      </header>
      <TabsBar v-if="app.showTabs" />
    </div>

    <!-- 内容:壁纸(柔光渐变)上直接摆页面,内容层不挂玻璃 -->
    <main class="content">
      <div ref="pageRef" class="page" :class="`bp-${bp}`" :data-bp="bp">
        <!-- 错误边界包住整个内容区:页面组件抛异常时 Vue 会卸掉整棵子树,没有它就是白屏。
             component :key=路由 path 给稳定身份:out-in 过渡才能正确区分进/出场,否则显示上一页缓存
             (按 path 各自成键,不塌缩缓存)。每个页面经 namedPage 套了一层 <div.page-view> 单元素根,
             out-in 能瞬时过渡任何页。 -->
        <ErrorBoundary>
          <router-view v-slot="{ Component }">
            <transition :name="transitionName" mode="out-in">
              <keep-alive :include="tabs.cachedNames" :exclude="tabs.excludeName">
                <component :is="Component" v-if="rvShow" :key="activeKey" />
              </keep-alive>
            </transition>
          </router-view>
        </ErrorBoundary>
      </div>
      <AppWatermark v-if="site.watermark.enabled" :settings="site.watermark" :who="watermarkWho" />
    </main>

    <!-- 移动端抽屉侧栏 -->
    <n-drawer v-model:show="mobileOpen" placement="left" :width="236">
      <n-drawer-content :native-scrollbar="false" body-content-style="padding:0">
        <SideNav
          :options="menuOptions"
          :value="activeKey"
          show-brand
          show-search
          @select="onMobileSelect"
          @search="onMobileSearch"
        />
      </n-drawer-content>
    </n-drawer>

    <MenuSearch v-model:show="searchOpen" />
  </div>
</template>

<style scoped>
/* 壳层:两块玻璃面板(侧栏 / 顶部)+ 一块内容区,离屏幕边缘 8px、面板之间 8px。
   背景是一整张氛围壁纸(--wallpaper:由强调色推导的柔光 + 底色,不画网格),玻璃叠在光上才有层次;
   内容层的卡片不用玻璃(只有侧栏、顶栏、弹层是玻璃)。
   z-index 全部压在 1999 以下:smart-naive-table 的放大层是 1999(刻意低于 naive 浮层的 2000),
   壳层不能盖住它。 */
.layout {
  --sidebar-w: 232px;
  --rail-w: 64px;
  --aside-duration: 0.28s; /* 侧栏收起 / 展开,macOS 侧栏滑动的量级 */
  display: grid;
  /* dvh 而非 vh:移动端浏览器地址栏收起时 vh 仍按最小视口算,底部会被裁掉一条 */
  height: 100dvh;
  padding: 8px;
  gap: 8px;
  overflow: hidden;
  background: var(--wallpaper, var(--bg-app));
}
/* 竖向壳:侧栏满高在左,顶栏缩进到内容列上方 */
.mode-vertical,
.mode-vertical-mix,
.mode-vertical-hybrid-header-first {
  grid-template-columns: auto minmax(0, 1fr);
  grid-template-rows: auto minmax(0, 1fr);
  grid-template-areas: 'aside header' 'aside content';
}
/* 顶栏壳:顶栏通栏,侧栏在其下方左侧 */
.mode-top-hybrid-sidebar-first,
.mode-top-hybrid-header-first {
  grid-template-columns: auto minmax(0, 1fr);
  grid-template-rows: auto minmax(0, 1fr);
  grid-template-areas: 'header header' 'aside content';
}
.mode-horizontal,
.mode-mobile {
  grid-template-columns: minmax(0, 1fr);
  grid-template-rows: auto minmax(0, 1fr);
  grid-template-areas: 'header' 'content';
}

/* 侧栏隐藏:列还在,侧栏宽度过渡到 0;列间距也要归零,否则空列旁边还留一道 8px。
   两者用同一时长与曲线,内容区才是被侧栏「推」着变宽,而不是先后跳两下。 */
.layout {
  transition: column-gap var(--aside-duration) var(--ease-sheet);
}
.layout.aside-hidden {
  column-gap: 0;
}

/* 玻璃面板:半透明底 + 强模糊 + 发丝描边 + 顶边高光 + 深投影 */
.glass {
  background: var(--glass-panel);
  backdrop-filter: blur(40px) saturate(180%);
  -webkit-backdrop-filter: blur(40px) saturate(180%);
  border: 1px solid var(--glass-border);
  box-shadow: var(--glass-shadow);
}
@supports not ((backdrop-filter: blur(1px)) or (-webkit-backdrop-filter: blur(1px))) {
  .glass {
    background: var(--glass-solid, var(--glass-panel));
  }
}

.aside {
  grid-area: aside;
  position: relative;
  z-index: 2;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  border-radius: var(--radius-lg);
  transition:
    width var(--aside-duration) var(--ease-sheet),
    border-width var(--aside-duration) var(--ease-sheet),
    opacity 0.2s ease;
}
/* 收起:border-box 下宽 0 仍会留两侧 1px 描边,一并归零;透明度同时带走投影 */
.aside--hidden {
  border-width: 0;
  opacity: 0;
}
/* 内容按满宽定宽,被 .aside 的 overflow:hidden 裁掉,收窄时不重排(mix 模式由下面两条各自定宽) */
.aside:not(.aside--mix) > * {
  width: var(--aside-w);
}
.aside--mix {
  display: flex;
}
.aside--mix .rail {
  width: var(--rail-w);
  flex-shrink: 0;
  border-right: 1px solid var(--hairline);
}
.aside--mix .l2col {
  flex: none;
  width: var(--sidebar-w);
}

.shell-head {
  grid-area: header;
  position: relative;
  z-index: 5;
  min-width: 0;
  overflow: hidden;
  border-radius: 14px;
}
/* 「固定头部」关闭:顶栏不再模糊背后的内容 */
.shell-head--static {
  backdrop-filter: none;
  -webkit-backdrop-filter: none;
}
.toolbar {
  display: flex;
  align-items: stretch;
  height: 44px;
}
.hbg {
  align-self: center;
  margin-left: 8px;
}
.apph {
  flex: 1;
  min-width: 0;
  height: 100%;
}

.content {
  grid-area: content;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}
/* 页面滚动层。左右只留 2px(让卡片的描边 / 投影不被 overflow 裁掉)。
   顶部不留白:顶栏到首张卡片永远是面板之间的 8px(--gap-card),满屏页和自然滚动页一样 —— 两类页面顶部间距不同的话,
   从列表页切到工作台首张卡片会下跳一截。自然滚动页底部留 8px 给卡片投影,满屏页(下条)只留 2px。
   内边距不进壳层档位的数字:档位量的是这块的 content box,见 composables/useShellBreakpoint。 */
.page {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 0 2px 8px;
}
/* 满屏页(列表 / 左右分栏)吃满一屏,四周只留 2px,滚动发生在表体里 */
.page:not(.bp-narrow):has(> .page-view > :is(.smart-table, .fill-page, .side-page)) {
  padding: 0 2px 2px;
}
/* narrow:整页自然滚动,不再锁一屏高度。满屏列表页的 .page-view 在 layout.css 里被补成 height:100%,
   这里放回自动高度,内容多长页面就多长、由 .page 滚动。 */
.page.bp-narrow :deep(.page-view) {
  height: auto;
}
</style>
