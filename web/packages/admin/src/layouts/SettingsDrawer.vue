<script setup lang="ts">
// 外观抽屉:外观模式 / 强调色 / 密度 / 布局(下拉) / 界面,一页滚完,不分页签——
// 页签里套分段控件(外观模式本身就是分段控件)是两层同形的东西叠在一起。
// 模式 / 页内切换用 NTabs type="segment"(轨道 + 滑块,和 macOS 的外观切换同一种控件),
// 不用 NRadioGroup + NRadioButton(那是描边按钮组,只用在筛选类取值上)。
import {
  NDrawer,
  NDrawerContent,
  NTabs,
  NTab,
  NList,
  NListItem,
  NSwitch,
  NSelect,
  NPopconfirm,
  NButton,
} from 'naive-ui'
import { computed } from 'vue'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useMessage } from 'naive-ui'
import { useClipboard } from '@vueuse/core'
import { useAppStore, LAYOUT_MODES, type LayoutMode } from '#/stores/app'
import { runtime } from '#/lib/runtime'
import AccentPicker from './AccentPicker.vue'

const show = defineModel<boolean>('show', { default: false })
const app = useAppStore()
const { t } = useI18n()
const message = useMessage()
const { copy, isSupported } = useClipboard()
// 「复制配置」是给内核开发者改 stores/app.ts 的 DEFAULTS 用的开发期动作,不该出现在终端管理员的设置里——仅开发态显示。
const showCopyConfig = isSupported && runtime.dev

// 复制当前外观配置(内核开发者粘回 stores/app.ts 的 DEFAULTS 即为新默认)。
async function copyConfig() {
  await copy(JSON.stringify(app.exportSettings(), null, 2))
  message.success(t('settings.configCopied'))
}

// 布局:下拉选六种 layoutMode,六种变体一个都不能少。
// 标签键由模式名派生:vertical-mix → settings.layoutVerticalMix。
const layoutOptions = computed(() =>
  LAYOUT_MODES.map(m => ({
    label: t(
      'settings.layout' +
        m
          .split('-')
          .map(w => w[0]!.toUpperCase() + w.slice(1))
          .join(''),
    ),
    value: m,
  })),
)

// computed:t() 一次求值不随语言切换更新,故包 computed 让下拉标签跟随 locale。
const transitionOptions = computed(() => [
  { label: t('settings.transitionNone'), value: 'none' },
  { label: t('settings.transitionFade'), value: 'fade' },
  { label: t('settings.transitionFadeSlide'), value: 'fade-slide' },
])
</script>

<template>
  <n-drawer v-model:show="show" width="min(360px, 100vw)" placement="right">
    <n-drawer-content :title="t('app.appearance')" closable :native-scrollbar="false">
      <div class="section-title first">{{ t('settings.themeMode') }}</div>
      <n-tabs v-model:value="app.themeScheme" type="segment">
        <n-tab name="light">{{ t('app.light') }}</n-tab>
        <n-tab name="dark">{{ t('app.dark') }}</n-tab>
        <n-tab name="auto">{{ t('settings.auto') }}</n-tab>
      </n-tabs>

      <div class="section-title">{{ t('settings.themeColor') }}</div>
      <AccentPicker />

      <div class="section-title">{{ t('app.density') }}</div>
      <n-tabs v-model:value="app.density" type="segment">
        <n-tab name="comfortable">{{ t('app.comfortable') }}</n-tab>
        <n-tab name="compact">{{ t('app.compact') }}</n-tab>
      </n-tabs>

      <div class="section-title">{{ t('settings.layout') }}</div>
      <n-select
        :value="app.layoutMode"
        :options="layoutOptions"
        @update:value="(v: LayoutMode) => app.setLayoutMode(v)"
      />

      <div class="section-title">{{ t('settings.formStyle') }}</div>
      <!-- FormContainer 全局形态:所有 CRUD 表单一键在弹窗 / 抽屉间切换(个别页面按实例写死的除外) -->
      <n-tabs v-model:value="app.formStyle" type="segment">
        <n-tab name="modal">{{ t('settings.formStyleModal') }}</n-tab>
        <n-tab name="drawer">{{ t('settings.formStyleDrawer') }}</n-tab>
      </n-tabs>

      <div class="section-title">{{ t('settings.interface') }}</div>
      <n-list class="set-group" hoverable :bordered="false" :show-divider="false">
        <n-list-item>
          <template #prefix>
            <div class="sym"><Icon icon="ph:caret-double-right" :width="15" /></div>
          </template>
          <div class="txt">
            <b>{{ t('settings.showBreadcrumb') }}</b>
            <span>{{ t('settings.descBreadcrumb') }}</span>
          </div>
          <template #suffix><n-switch v-model:value="app.showBreadcrumb" size="small" /></template>
        </n-list-item>
        <n-list-item>
          <template #prefix>
            <div class="sym"><Icon icon="ph:browsers" :width="15" /></div>
          </template>
          <div class="txt">
            <b>{{ t('settings.showTabs') }}</b>
            <span>{{ t('settings.descTabs') }}</span>
          </div>
          <template #suffix><n-switch v-model:value="app.showTabs" size="small" /></template>
        </n-list-item>
        <n-list-item>
          <template #prefix>
            <div class="sym"><Icon icon="ph:push-pin" :width="15" /></div>
          </template>
          <div class="txt">
            <b>{{ t('settings.fixedHeader') }}</b>
            <span>{{ t('settings.descFixedHeader') }}</span>
          </div>
          <template #suffix><n-switch v-model:value="app.fixedHeader" size="small" /></template>
        </n-list-item>
        <n-list-item>
          <template #prefix>
            <div class="sym"><Icon icon="ph:sidebar-simple" :width="15" /></div>
          </template>
          <div class="txt">
            <b>{{ t('app.collapse') }}</b>
            <span>{{ t('settings.descCollapse') }}</span>
          </div>
          <template #suffix><n-switch v-model:value="app.collapsed" size="small" /></template>
        </n-list-item>
        <n-list-item>
          <template #prefix>
            <div class="sym"><Icon icon="ph:sparkle" :width="15" /></div>
          </template>
          <div class="txt">
            <b>{{ t('settings.pageTransition') }}</b>
            <span>{{ t('settings.descPageTransition') }}</span>
          </div>
          <template #suffix>
            <n-select
              v-model:value="app.pageTransition"
              :options="transitionOptions"
              size="small"
              class="sel"
            />
          </template>
        </n-list-item>
      </n-list>

      <template #footer>
        <div class="footer-actions">
          <n-button v-if="showCopyConfig" block secondary @click="copyConfig">
            <template #icon><Icon icon="ph:clipboard-text" :width="16" /></template>
            {{ t('settings.copyConfig') }}
          </n-button>
          <n-popconfirm @positive-click="app.resetSettings()">
            <template #trigger>
              <n-button block secondary>
                <template #icon><Icon icon="ph:arrow-counter-clockwise" :width="16" /></template>
                {{ t('common.reset') }}
              </n-button>
            </template>
            {{ t('settings.resetConfirm') }}
          </n-popconfirm>
        </div>
      </template>
    </n-drawer-content>
  </n-drawer>
</template>

<style scoped>
.footer-actions {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 100%;
}

/* 分节标题走正文字体、正常字距:等宽 + 大写 + 宽字距是只对拉丁字母成立的手法,落在汉字上会把「界面」撑成「界 面」 */
.section-title {
  margin: 22px 0 8px 4px;
  font-size: var(--font-size-base);
  font-weight: 500;
  letter-spacing: -0.01em;
  color: var(--text-3);
}
.section-title.first {
  margin-top: 0;
}

/* 设置组:不用 NCard embedded(那块平灰底和整站玻璃语言无关,四行加粗标签配四个同权开关像一张通用设置表)。
   玻璃底 + 行首单色图标 + 标题下一行说明把权重压开。分隔线关掉 NList 自带的通栏 show-divider,
   自己从文字起始处起、右边留白地画,悬停时淡出——macOS 系统设置的做法。 */
.set-group.n-list {
  border-radius: 14px;
  background: var(--glass);
  box-shadow:
    inset 0 0 0 1px var(--hairline),
    inset 0 1px 0 var(--edge-light);
}
.set-group :deep(.n-list-item) {
  position: relative;
  padding: 10px 12px !important;
  border-radius: var(--radius-lg);
}
.set-group :deep(.n-list-item + .n-list-item::before) {
  content: '';
  position: absolute;
  left: 52px;
  right: 12px;
  top: 0;
  height: 1px;
  background: var(--separator);
  transition: opacity var(--transition-fast);
}
.set-group :deep(.n-list-item:hover::before),
.set-group :deep(.n-list-item:hover + .n-list-item::before) {
  opacity: 0;
}
.sym {
  display: grid;
  place-items: center;
  flex-shrink: 0;
  width: 24px;
  height: 24px;
  border-radius: var(--radius-sm);
  background: var(--glass-strong);
  color: var(--text-1);
  box-shadow:
    inset 0 0 0 1px var(--hairline),
    inset 0 1px 0 var(--edge-light);
}
.txt b {
  display: block;
  font-size: var(--font-size-md);
  font-weight: 500;
  line-height: 1.3;
}
.txt span {
  display: block;
  margin-top: 2px;
  font-size: var(--font-size-sm);
  line-height: 1.4;
  color: var(--text-3);
}
.sel {
  width: 112px;
}
</style>
