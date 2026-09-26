<script setup lang="ts">
// 授权菜单面板:悬浮圆角抽屉,头(标题+角色)/ 工具区 / 列表 / 底栏四段。只有列表纵向滚动,
// 底栏(已授权统计 + 取消/保存)永远在视野里 —— 不复用 FormContainer,因为它在 modal 形态下会随内容撑高,
// 长列表会把保存钮顶出视口。
// onSave 协议同 FormContainer.onConfirm:返回 false 或抛错 → 不关闭;其余情况自动关。
import { computed, ref, shallowRef, watch } from 'vue'
import { NButton, NDrawer } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useWindowSize } from '@vueuse/core'
import type { MenuTreeNode } from '#/types/menu'
import type { ModuleRow } from '#/types/api'
import GrantMenuTable from './GrantMenuTable.vue'
import type { GrantSummary } from './grantMenuGroups'

const show = defineModel<boolean>('show', { default: false })

const props = defineProps<{
  roleName: string
  roleCode: string
  tree: MenuTreeNode[]
  granted: number[]
  modules: ModuleRow[]
  defaultModuleId: number
  onSave: (ids: number[]) => unknown | Promise<unknown>
}>()

const { t } = useI18n()

/** 窄于此宽度铺满整屏(无留白、无圆角)。 */
const COMPACT = 640
const { width: winW } = useWindowSize()
const compact = computed(() => winW.value <= COMPACT)
const width = computed(() => (compact.value ? winW.value : Math.min(960, winW.value - 24)))
// 定位与滚动的是外层 .n-drawer(绝对定位、自带 overflow:auto),内层 wrapper 是静态块:
// 留边/圆角必须加在 .n-drawer 上(走 attrs 的 style),wrapper 只负责撑满,否则列表把 wrapper 撑高、整个抽屉自己滚,底栏就出视口了。
const drawerStyle = computed(() =>
  compact.value
    ? { background: 'var(--color-bg-body)', overflow: 'hidden' }
    : {
        top: '12px',
        bottom: '12px',
        right: '12px',
        borderRadius: '20px',
        overflow: 'hidden',
        background: 'var(--color-bg-body)',
        boxShadow: '0 24px 60px rgba(0, 0, 0, 0.22), 0 0 0 0.5px var(--color-border)',
      },
)
const wrapperStyle = { height: '100%' }

// 勾选态在列表自己的 reactive 里,这里只收结果:打开时先是服务端现状,用户一改就跟着列表走
const checked = shallowRef<number[]>([])
const summary = ref<GrantSummary>({ on: 0, total: 0, pages: 0, buttons: 0, dirty: false })
watch(
  show,
  v => {
    if (v) checked.value = props.granted
  },
  { immediate: true },
)

const saving = ref(false)
// 提交中禁止任何途径关闭,防提交中途关闭导致状态错乱
const canClose = computed(() => !saving.value)

async function handleSave() {
  saving.value = true
  try {
    if ((await props.onSave(checked.value)) !== false) show.value = false
  } catch {
    // 静默:错误 toast 由业务 onSave 内负责
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <n-drawer
    v-model:show="show"
    :width="width"
    :close-on-esc="canClose"
    :mask-closable="false"
    :style="drawerStyle"
    :content-style="wrapperStyle"
  >
    <div class="gs" role="dialog" aria-labelledby="grant-sheet-title">
      <header class="gs-head">
        <div class="gs-head-text">
          <h2 id="grant-sheet-title">{{ t('role.grantMenus') }}</h2>
          <div class="gs-who">{{ roleName }} · {{ roleCode }}</div>
        </div>
        <button
          class="gs-close"
          type="button"
          :disabled="!canClose"
          :aria-label="t('common.close')"
          @click="show = false"
        >
          <svg
            width="14"
            height="14"
            viewBox="0 0 14 14"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M3 3l8 8M11 3l-8 8" />
          </svg>
        </button>
      </header>

      <GrantMenuTable
        :tree="tree"
        :granted="granted"
        :modules="modules"
        :default-module-id="defaultModuleId"
        @update:checked="checked = $event"
        @summary="summary = $event"
      />

      <footer class="gs-foot">
        <div class="gs-summary">
          <span>{{ t('role.grantSummary', { on: summary.on, total: summary.total }) }}</span>
          <span>
            {{ t('role.grantSummarySplit', { pages: summary.pages, buttons: summary.buttons }) }}
          </span>
          <span v-if="summary.dirty" class="gs-dirty">{{ t('role.grantUnsaved') }}</span>
        </div>
        <n-button :disabled="saving" @click="show = false">{{ t('common.cancel') }}</n-button>
        <n-button type="primary" :loading="saving" @click="handleSave">
          {{ t('common.save') }}
        </n-button>
      </footer>
    </div>
  </n-drawer>
</template>

<style scoped>
/* 设计令牌:全部取自全局角色令牌,亮/暗色随主题翻转,主色随用户所选 accent */
.gs {
  --g-sheet: var(--color-bg-body);
  --g-card: var(--color-bg-elevated);
  --g-label: var(--color-text-primary);
  --g-label-2: var(--color-text-tertiary);
  --g-label-3: var(--color-text-disabled);
  --g-sep: var(--color-border);
  --g-fill: var(--color-fill);
  --g-fill-hover: var(--color-fill-hover);
  --g-accent: var(--color-primary);
  --g-accent-tint: var(--color-primary-light);
  --g-accent-text: var(--color-primary-pressed);
  --g-on-accent: #fff;
  --g-ring: color-mix(in srgb, var(--color-primary) 45%, transparent);
  --g-cb-border: var(--color-border-strong);
  --g-warn: var(--color-warning);
  --g-mask-check: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M4 8.5l2.7 2.7L12 5.6' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");
  --g-mask-dash: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M4.5 8h7' fill='none' stroke='black' stroke-width='2' stroke-linecap='round'/%3E%3C/svg%3E");

  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  background: var(--g-sheet);
  color: var(--g-label);
}
:global(:root[data-theme='dark']) .gs {
  --g-accent-text: var(--color-primary-hover);
  --g-on-accent: #0b1220;
}

.gs-head {
  flex: none;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 18px 20px 6px;
}
.gs-head-text {
  flex: 1;
  min-width: 0;
}
.gs-head h2 {
  margin: 0;
  font-size: 20px;
  line-height: 28px;
  font-weight: 700;
  letter-spacing: -0.01em;
}
.gs-who {
  margin-top: 2px;
  font-size: 13px;
  color: var(--g-label-2);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.gs-close {
  flex: none;
  width: 30px;
  height: 30px;
  border: 0;
  border-radius: 50%;
  background: var(--g-fill);
  color: var(--g-label-2);
  display: grid;
  place-items: center;
  cursor: pointer;
  transition: background 0.15s;
}
.gs-close:hover:not(:disabled) {
  background: var(--g-fill-hover);
  color: var(--g-label);
}
.gs-close:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.gs-close:focus-visible {
  outline: 3px solid var(--g-ring);
  outline-offset: 2px;
}

/* 底栏:永远可见 */
.gs-foot {
  flex: none;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 20px 14px;
  border-top: 1px solid var(--g-sep);
  background: color-mix(in srgb, var(--g-sheet) 82%, transparent);
  backdrop-filter: blur(20px) saturate(1.8);
}
.gs-summary {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 13px;
  color: var(--g-label-2);
  font-variant-numeric: tabular-nums;
}
.gs-dirty {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--g-warn);
}
.gs-dirty::before {
  content: '';
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--g-warn);
}
</style>
