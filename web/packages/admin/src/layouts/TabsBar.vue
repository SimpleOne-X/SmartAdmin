<script setup lang="ts">
import { computed, h, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NDropdown, type DropdownOption } from 'naive-ui'
import { Icon } from '@iconify/vue'
import AppIcon from '#/components/AppIcon.vue'
import { useI18n } from 'vue-i18n'
import { useTabsStore, type TabItem } from '#/stores/tabs'
import { translateMenuTitle } from '#/locales/menuTitle'
import { lineIcon } from '#/utils/menuIcon'

const tabs = useTabsStore()
const route = useRoute()
const router = useRouter()
const { t } = useI18n()

const activePath = computed(() => route.path)
const scrollerRef = ref<HTMLElement>()

function tabTitle(item: TabItem) {
  return translateMenuTitle(item.title, item.path)
}
function onClick(item: TabItem) {
  if (item.path !== route.path) router.push(item.fullPath)
}

function renderIcon(name: string) {
  return () => h(Icon, { icon: name, width: 16 })
}

const ctxShow = ref(false)
const ctxX = ref(0)
const ctxY = ref(0)
const ctxTab = ref<TabItem>()
const ctxOptions = computed<DropdownOption[]>(() => [
  { label: t('tabs.refresh'), key: 'refresh', icon: renderIcon('ph:arrow-clockwise') },
  // 应用首页(affix)恒固定,不提供固定/取消项;其余标签可用户手动固定。
  {
    label: ctxTab.value?.pinned ? t('tabs.unpin') : t('tabs.pin'),
    key: 'pin',
    icon: renderIcon('ph:push-pin'),
    disabled: ctxTab.value?.affix,
  },
  {
    label: t('tabs.close'),
    key: 'close',
    icon: renderIcon('ph:x'),
    disabled: ctxTab.value?.affix || ctxTab.value?.pinned,
  },
  { label: t('tabs.closeOthers'), key: 'others', icon: renderIcon('ph:arrows-in-line-horizontal') },
  { label: t('tabs.closeLeft'), key: 'left', icon: renderIcon('ph:arrow-line-left') },
  { label: t('tabs.closeRight'), key: 'right', icon: renderIcon('ph:arrow-line-right') },
  { label: t('tabs.closeAll'), key: 'all', icon: renderIcon('ph:list-dashes') },
])

function onContext(e: MouseEvent, item: TabItem) {
  e.preventDefault()
  ctxTab.value = item
  // 先关再于 nextTick 重开:菜单已展开时右键切到别的标签,只改坐标 NDropdown 不会重定位。
  ctxShow.value = false
  nextTick(() => {
    ctxX.value = e.clientX
    ctxY.value = e.clientY
    ctxShow.value = true
  })
}
function onCtxSelect(key: string) {
  ctxShow.value = false
  const tab = ctxTab.value
  if (!tab) return
  if (key === 'refresh') tabs.refreshTab(tab.name)
  else if (key === 'pin') tabs.togglePin(tab.path)
  else if (key === 'close') tabs.removeTab(tab.path)
  else if (key === 'others') tabs.closeOthers(tab.path)
  else if (key === 'left') tabs.closeLeft(tab.path)
  else if (key === 'right') tabs.closeRight(tab.path)
  else if (key === 'all') tabs.closeAll()
}

// 激活标签滚入视野
watch(activePath, () => {
  nextTick(() =>
    scrollerRef.value
      ?.querySelector('.chip.active')
      ?.scrollIntoView({ block: 'nearest', inline: 'nearest' }),
  )
})

// 页签溢出时两端各 18px 柔和渐隐(没有硬边):量滚动容器,哪一端还有内容就渐隐哪一端。
// 滚动容器就是 scrollerRef 本身(原生滚动、滚动条隐藏:macOS 的滚动条是 overlay,静止时不可见)。
const fadeStart = ref(false)
const fadeEnd = ref(false)
function measureFade() {
  const c = scrollerRef.value
  if (!c) return
  fadeStart.value = c.scrollLeft > 1
  fadeEnd.value = c.scrollLeft + c.clientWidth < c.scrollWidth - 1
}
// 没有滚动条可拖,鼠标滚轮的竖向滚动转成横向(触控板的横向手势 deltaX 浏览器自己处理)。
// 没有溢出时不接管,免得吞掉本该给页面的滚轮。
function onWheel(e: WheelEvent) {
  const c = scrollerRef.value
  if (!c || c.scrollWidth <= c.clientWidth || Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return
  e.preventDefault()
  c.scrollBy({ left: e.deltaY, behavior: 'instant' })
}

// 关页签:宽度收拢 + 淡出,右边的页签随之滑过来(Safari 关标签的做法)。
// 收拢要从「当前宽度」起步,而页签宽度是 flex 算出来的,CSS 里没有现成值 → 离场前量一下,交给 CSS 变量。
function onBeforeLeave(el: Element) {
  ;(el as HTMLElement).style.setProperty('--chip-w', `${el.getBoundingClientRect().width}px`)
}

let ro: ResizeObserver | undefined
onMounted(() => {
  measureFade()
  if (typeof ResizeObserver === 'undefined') return
  ro = new ResizeObserver(measureFade)
  if (scrollerRef.value) ro.observe(scrollerRef.value)
  // 页签条本身(scroller 的唯一子元素):页签增删、收缩时它的宽度变,渐隐要跟着重量
  if (scrollerRef.value?.firstElementChild) ro.observe(scrollerRef.value.firstElementChild)
})
onBeforeUnmount(() => ro?.disconnect())
</script>

<template>
  <div class="tabsbar">
    <div
      ref="scrollerRef"
      class="scroller"
      :class="{ 'fade-start': fadeStart, 'fade-end': fadeEnd }"
      @scroll.passive="measureFade"
      @wheel="onWheel"
    >
      <transition-group tag="div" name="chip" class="strip" @before-leave="onBeforeLeave">
        <!-- chip 与关闭 X 均为可点非按钮元素:补 role/tabindex/键盘激活,键盘用户也能切换/关闭标签(右键菜单是鼠标专属) -->
        <div
          v-for="item in tabs.tabs"
          :key="item.path"
          class="chip"
          :class="{ active: item.path === activePath, 'chip--fixed': item.affix || item.pinned }"
          :title="tabTitle(item)"
          role="button"
          tabindex="0"
          @click="onClick(item)"
          @keydown.enter="onClick(item)"
          @keydown.space.prevent="onClick(item)"
          @contextmenu="onContext($event, item)"
          @mousedown.middle.prevent
          @auxclick.middle.prevent="tabs.removeTab(item.path)"
        >
          <!-- 标签图标沿用菜单元数据,可能不在离线子集内 → 走 AppIcon 的懒加载兜底;统一线条款 -->
          <AppIcon v-if="item.icon" :icon="lineIcon(item.icon)" :size="15" class="chip-icon" />
          <span class="chip-label">{{ tabTitle(item) }}</span>
          <!-- 固定标签(用户 pin)显示图钉、不显示关闭 X;应用首页 affix 两者都不显示 -->
          <Icon v-if="item.pinned" icon="ph:push-pin-fill" :width="12" class="chip-pin" />
          <Icon
            v-else-if="!item.affix"
            icon="ph:x"
            :width="13"
            class="chip-close"
            role="button"
            tabindex="0"
            :aria-label="t('tabs.close')"
            @mousedown.prevent
            @click.stop="tabs.removeTab(item.path)"
            @keydown.enter.stop="tabs.removeTab(item.path)"
            @keydown.space.stop.prevent="tabs.removeTab(item.path)"
          />
        </div>
      </transition-group>
    </div>
    <n-dropdown
      trigger="manual"
      placement="bottom-start"
      :show="ctxShow"
      :x="ctxX"
      :y="ctxY"
      :options="ctxOptions"
      @select="onCtxSelect"
      @clickoutside="ctxShow = false"
    />
  </div>
</template>

<style scoped>
/* 页签条在顶栏玻璃面板里(default.vue 的 .shell-head),自己不画底、不画上下边线以外的东西;
   右端不放刷新 / 页签操作按钮:那些操作都在右键菜单里。 */
.tabsbar {
  height: 40px;
  display: flex;
  align-items: center;
  padding: 0 10px 0 12px;
  background: transparent;
  box-shadow: inset 0 1px 0 var(--hairline);
}
/* 原生滚动 + 隐藏滚动条:页签先收缩,收到最小宽度才溢出,溢出时靠两端渐隐暗示、滚轮 / 触控板横划。
   scroll-behavior: smooth 让激活页签滚入视野是一段缓动而不是跳变(系统开了减少动态效果时由全局规则压成瞬时)。 */
.scroller {
  flex: 1;
  min-width: 0;
  overflow-x: auto;
  overflow-y: hidden;
  overscroll-behavior-x: contain;
  scroll-behavior: smooth;
  scrollbar-width: none;
}
.scroller::-webkit-scrollbar {
  display: none;
}
.fade-start:not(.fade-end) {
  -webkit-mask-image: linear-gradient(to right, transparent 0, #000 18px);
  mask-image: linear-gradient(to right, transparent 0, #000 18px);
}
.fade-end:not(.fade-start) {
  -webkit-mask-image: linear-gradient(to left, transparent 0, #000 18px);
  mask-image: linear-gradient(to left, transparent 0, #000 18px);
}
.fade-start.fade-end {
  -webkit-mask-image: linear-gradient(
    to right,
    transparent 0,
    #000 18px,
    #000 calc(100% - 18px),
    transparent 100%
  );
  mask-image: linear-gradient(
    to right,
    transparent 0,
    #000 18px,
    #000 calc(100% - 18px),
    transparent 100%
  );
}
.strip {
  display: flex;
  align-items: center;
  width: 100%; /* 撑满容器才会触发页签收缩;页签少时靠左,各自保持自然宽度 */
  padding: 5px 0;
}
/* 未选中:带一点淡底(--fill),否则看起来像一排纯文字;选中:强调色淡底 + 强调色文字加粗,
   无下划线、无描边、无发光。 */
.chip {
  position: relative;
  display: inline-flex;
  margin-right: 4px; /* 不用 gap:关闭时间距要和页签一起收拢 */
  align-items: center;
  gap: 7px;
  flex: 0 1 auto; /* 可收缩:标题放不下就截断,收到 min-width 才轮到滚动 */
  min-width: 104px;
  max-width: 180px;
  height: 30px;
  padding: 0 11px;
  border: none;
  border-radius: var(--radius-md);
  background: var(--fill);
  color: var(--text-3);
  font-size: 14px;
  white-space: nowrap;
  cursor: pointer;
  user-select: none;
  transition:
    color var(--transition-fast),
    background-color var(--transition-fast);
}
.chip:hover {
  color: var(--text-1);
  background: var(--hover);
}
.chip.active {
  background: var(--tab-on-bg);
  color: var(--tab-on-fg);
  font-weight: 600;
}
.chip :deep(svg) {
  flex-shrink: 0;
}
.chip-label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}
/* 首页(affix)与用户固定的页签保持自然宽度,不参与收缩 */
.chip--fixed {
  flex: none;
  min-width: 0;
  max-width: none;
}
/* 鼠标点击不画聚焦环:× 是带 border-radius:50% 的 svg,浏览器默认聚焦环会被圆角化成一个黑圈(关闭瞬间闪现);
   键盘聚焦(:focus-visible)仍然画。点击 × 时另由 mousedown.prevent 阻止它抢焦点。 */
.chip:focus:not(:focus-visible),
.chip-close:focus:not(:focus-visible) {
  outline: none;
}
.chip:focus-visible,
.chip-close:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 1px;
}
.chip-pin {
  opacity: 0.7;
}
.chip-icon {
  transition: opacity var(--transition-fast);
}
.chip-close {
  border-radius: 50%;
  transition: opacity var(--transition-fast);
}

/* 桌面(有悬停):× 盖在左侧图标的位置上,悬停 / 键盘聚焦到页签时才出现(图标淡出让位),不占布局宽度。
   这是 Safari 标签栏的做法:标题多得 20px,收缩后四个汉字的页签名仍放得下。
   没有图标的页签没有可盖的位置,× 退回流内、仍只在悬停时出现。 */
@media (hover: hover) {
  .chip-close {
    position: absolute;
    left: 12px;
    top: 50%;
    margin-top: -6.5px;
    opacity: 0;
    pointer-events: none;
  }
  .chip:hover .chip-close,
  .chip:focus-within .chip-close {
    opacity: 0.75;
    pointer-events: auto;
  }
  .chip:hover .chip-close:hover,
  .chip-close:focus-visible {
    opacity: 1;
  }
  .chip:hover .chip-icon,
  .chip:focus-within .chip-icon {
    opacity: 0;
  }
  .chip:not(:has(.chip-icon)) .chip-close {
    position: static;
    margin-top: 0;
  }
}

/* 触屏没有悬停:× 常显在右侧(仍占宽度),保证关得掉 */
@media (hover: none) {
  .chip {
    padding-right: 8px;
  }
  .chip-close {
    opacity: 0.7;
  }
}

/* 页签增删:宽度 / 内边距 / 间距一起收拢或展开,右边的页签随布局平滑滑动(flex 实时重排,不需要 FLIP)。
   曲线取 macOS 切换外观同款 ease-in-out;系统开了减少动态效果时由全局规则压成瞬时。 */
.chip-enter-active,
.chip-leave-active {
  overflow: hidden;
  transition-property:
    width, max-width, min-width, padding-left, padding-right, margin-right, opacity;
  transition-duration: 0.22s;
  transition-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
}
.chip-enter-from {
  max-width: 0;
  min-width: 0;
  padding-left: 0;
  padding-right: 0;
  margin-right: 0;
  opacity: 0;
}
.chip-leave-active {
  flex: none;
  pointer-events: none;
}
.chip-leave-from {
  width: var(--chip-w);
}
.chip-leave-to {
  width: 0;
  min-width: 0;
  padding-left: 0;
  padding-right: 0;
  margin-right: 0;
  opacity: 0;
}
</style>
