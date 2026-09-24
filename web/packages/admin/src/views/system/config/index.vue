<script setup lang="ts">
// 配置中心:仿 macOS「系统设置」。顶部图标页签,每个分类一屏放下、「左边设置、右边预览」
// (没有可预览效果的分类不放预览)。结构化表单共用一份页面级草稿(draft.ts),改动由底部浮出的
// 保存条统一保存;改过的行和页签带橙点。当前分类写进 ?tab=,刷新和分享链接能回到同一处。
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { NButton, NSpin, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import { CONFIG_TABS, TAB_GROUPS, TAB_ICONS, type ConfigTab } from './groups'
import { checkDraft, createConfigDraft, provideConfigDraft } from './draft'
import BrandSection from './components/BrandSection.vue'
import SecuritySection from './components/SecuritySection.vue'
import SigninSection from './components/SigninSection.vue'
import SensitiveSection from './components/SensitiveSection.vue'
import UploadSection from './components/UploadSection.vue'
import JobSection from './components/JobSection.vue'
import AdvancedSection from './components/AdvancedSection.vue'

const { t } = useI18n()
const message = useMessage()
const { ask } = useConfirm()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const draft = createConfigDraft()
provideConfigDraft(draft)
const canSave = computed(() => auth.hasPerm('PUT:/api/v1/sys/config/batch'))
const dirtyCount = computed(() => draft.dirtyKeys.value.length)
const brandSection = ref<InstanceType<typeof BrandSection> | null>(null)

/** 方向键在页签间移动并切换,焦点跟过去 */
function onNavKey(e: KeyboardEvent) {
  const i = CONFIG_TABS.indexOf(tab.value)
  const next =
    e.key === 'ArrowRight'
      ? (i + 1) % CONFIG_TABS.length
      : e.key === 'ArrowLeft'
        ? (i - 1 + CONFIG_TABS.length) % CONFIG_TABS.length
        : e.key === 'Home'
          ? 0
          : e.key === 'End'
            ? CONFIG_TABS.length - 1
            : -1
  if (next < 0) return
  e.preventDefault()
  tab.value = CONFIG_TABS[next]
  void nextTick(() =>
    (e.currentTarget as HTMLElement | null)
      ?.querySelector<HTMLElement>(`[data-tab="${CONFIG_TABS[next]}"]`)
      ?.focus(),
  )
}

const tab = computed<ConfigTab>({
  get: () => {
    const q = route.query.tab
    return CONFIG_TABS.includes(q as ConfigTab) ? (q as ConfigTab) : 'brand'
  },
  set: v => {
    void router.replace({ query: { ...route.query, tab: v } })
  },
})

onMounted(async () => {
  try {
    await draft.load()
  } catch (e) {
    message.error(translateError(e))
  }
})

async function save() {
  const check = checkDraft(draft.values)
  if (check.error) {
    tab.value = check.error.tab
    if (check.error.locale) brandSection.value?.setLocale(check.error.locale)
    message.error(t(check.error.message))
    return
  }
  for (const w of check.warnings)
    message.warning(
      `${t(`config.login.locale${w.locale === 'zh-CN' ? 'Zh' : 'En'}`)}: ${t(w.message)}`,
    )
  try {
    await draft.save()
    message.success(t('config.saved'))
  } catch (e) {
    message.error(translateError(e))
  }
}

// 有未保存改动时离开要确认;关标签页/刷新交给浏览器自己的提示
onBeforeRouteLeave(async to => {
  if (!dirtyCount.value || to.path === route.path) return true
  return ask({ title: t('config.leaveTitle'), content: t('config.leaveConfirm') })
})
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (dirtyCount.value) e.preventDefault()
}
window.addEventListener('beforeunload', onBeforeUnload)
onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  draft.discard() // 释放预览用的 blob: 地址
})
</script>

<template>
  <!-- .fill-page:整页吃满一屏(内核 styles/layout.css 的高度链),每个分类在 .cfg-body 里一屏放下 -->
  <div class="config-center fill-page">
    <!-- 页签导航:role=tablist + 漫游 tabindex,方向键 / Home / End 切换(WAI-ARIA Tabs 模式) -->
    <nav
      class="cfg-nav"
      role="tablist"
      :aria-label="t('config.title')"
      data-testid="config-nav"
      @keydown="onNavKey"
    >
      <template v-for="(group, gi) in TAB_GROUPS" :key="gi">
        <span v-if="gi" class="gap" aria-hidden="true" />
        <button
          v-for="key in group"
          :key="key"
          type="button"
          role="tab"
          :aria-selected="tab === key"
          :tabindex="tab === key ? 0 : -1"
          :class="['nav-item', { on: tab === key }]"
          :data-tab="key"
          @click="tab = key"
        >
          <AppIcon :icon="TAB_ICONS[key]" :size="22" />
          <span>{{ t(`config.tab.${key}`) }}</span>
          <template v-if="draft.dirtyTabs.value.has(key)">
            <i class="dot" aria-hidden="true" :title="t('config.unsavedTab')" />
            <span class="sr-only">{{ t('config.unsavedTab') }}</span>
          </template>
        </button>
      </template>
    </nav>

    <div v-if="draft.loading.value" class="cfg-loading"><n-spin /></div>
    <div v-else class="cfg-body">
      <BrandSection v-show="tab === 'brand'" ref="brandSection" />
      <SecuritySection v-show="tab === 'security'" />
      <SigninSection v-show="tab === 'signin'" />
      <SensitiveSection v-if="tab === 'sensitive'" />
      <UploadSection v-show="tab === 'upload'" />
      <JobSection v-show="tab === 'job'" />
      <AdvancedSection v-if="tab === 'advanced'" />
    </div>

    <!-- 保存条浮在底部正中(毛玻璃胶囊),有改动才出现 -->
    <transition name="savebar">
      <div v-if="dirtyCount" class="savebar" data-testid="config-savebar">
        <span class="msg" role="status" aria-live="polite">
          <i class="dot" aria-hidden="true" />
          {{ t('config.unsaved', { n: dirtyCount }) }}
        </span>
        <n-button size="small" :disabled="draft.saving.value" @click="draft.discard()">
          {{ t('config.discard') }}
        </n-button>
        <n-button
          v-if="canSave"
          size="small"
          type="primary"
          :loading="draft.saving.value"
          @click="save"
        >
          {{ t('common.save') }}
        </n-button>
      </div>
    </transition>
  </div>
</template>

<style scoped>
.config-center {
  position: relative;
  overflow: hidden;
  border-radius: var(--radius-lg);
  background: var(--color-bg-body);
}
.cfg-nav {
  display: flex;
  flex: none;
  flex-wrap: wrap;
  justify-content: center;
  gap: 2px;
  padding: 8px 16px;
  border-bottom: 0.5px solid var(--color-border);
  background: var(--color-bg-container);
}
.gap {
  width: 14px;
}
.nav-item {
  position: relative;
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  gap: 3px;
  min-width: 76px;
  min-height: 52px;
  padding: 6px 10px 5px;
  border: 0;
  border-radius: 8px;
  background: none;
  font: inherit;
  font-size: var(--font-size-sm);
  color: var(--color-text-secondary);
  cursor: pointer;
  user-select: none;
}
.nav-item:hover {
  background: var(--color-fill-hover);
}
.nav-item.on {
  background: var(--color-fill);
  color: var(--color-primary);
}
.nav-item.on span {
  color: var(--color-text-primary);
  font-weight: 500;
}
.nav-item:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: -2px;
}
.nav-item .dot {
  position: absolute;
  top: 6px;
  right: 16px;
  box-shadow: 0 0 0 2px var(--color-bg-container);
}
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}
.dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--color-warning);
}
.cfg-loading {
  display: grid;
  flex: 1;
  place-items: center;
}
/* 内容区吃掉导航之外的全部高度;每个分类自己保证一屏放下(SectionLayout) */
.cfg-body {
  flex: 1;
  min-height: 0;
  padding: 16px 24px 20px;
}
.cfg-body > :deep(section) {
  height: 100%;
}
.savebar {
  position: absolute;
  left: 50%;
  bottom: 18px;
  z-index: 10;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 7px 7px 7px 16px;
  border-radius: 14px;
  background: color-mix(in srgb, var(--color-bg-container) 88%, transparent);
  backdrop-filter: blur(20px);
  border: 1px solid var(--color-border);
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.14);
  transform: translateX(-50%);
}
.savebar .msg {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin-right: 4px;
  font-size: var(--font-size-base);
  color: var(--color-text-secondary);
  white-space: nowrap;
}
.savebar-enter-active,
.savebar-leave-active {
  transition:
    transform 0.25s cubic-bezier(0.2, 0.9, 0.3, 1.2),
    opacity 0.2s ease;
}
.savebar-enter-from,
.savebar-leave-to {
  transform: translate(-50%, 140%);
  opacity: 0;
}
/* 系统开了「减少动态效果」就只淡入淡出,不做位移 */
@media (prefers-reduced-motion: reduce) {
  .savebar-enter-active,
  .savebar-leave-active {
    transition: opacity 0.15s linear;
  }
  .savebar-enter-from,
  .savebar-leave-to {
    transform: translateX(-50%);
  }
}
/* 矮屏:收紧留白 */
@media (max-height: 899px) {
  .cfg-nav {
    padding: 6px 16px;
  }
  .nav-item {
    min-height: 48px;
    padding: 4px 10px 3px;
  }
  .cfg-body {
    padding: 12px 20px 14px;
  }
}
</style>
