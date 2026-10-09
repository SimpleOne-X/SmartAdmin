<script setup lang="ts">
// 授权菜单弹窗:居中卡片(窄屏近乎铺满),标题 / 工具区 / 列表 / 底栏四段。只有列表纵向滚动,
// 底栏(已授权统计 + 取消/保存)永远在视野里 —— 不复用 FormContainer,因为它在 modal 形态下会随内容撑高,
// 长列表会把保存钮顶出视口。弹窗定高、内容区 flex 吃满,滚动只发生在列表里。
// onSave 协议同 FormContainer.onConfirm:返回 false 或抛错 → 不关闭;其余情况自动关。
import { computed, ref, shallowRef, watch, type CSSProperties } from 'vue'
import { NButton, NModal } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useWindowSize } from '@vueuse/core'
import type { MenuTreeNode } from '#/types/menu'
import type { ModuleRow } from '#/types/api'
import GrantMenuTable from './GrantMenuTable.vue'
import type { GrantSummary } from './grantMenuGroups'

const show = defineModel<boolean>('show', { default: false })

const props = defineProps<{
  tree: MenuTreeNode[]
  granted: number[]
  modules: ModuleRow[]
  defaultModuleId: number
  /** 列表上方的一行说明(如范围限制);不给不显示 */
  hint?: string
  onSave: (ids: number[]) => unknown | Promise<unknown>
}>()

const { t } = useI18n()

/** 窄于此宽度弹窗几乎铺满视口、控件放大一档。 */
const COMPACT = 640
const { width: winW } = useWindowSize()
const compact = computed(() => winW.value <= COMPACT)
// 定高:列表再长弹窗也不超过视口;卡片是 flex 列,内容区吃剩余高度,滚动只在列表里
const modalStyle = computed(() =>
  compact.value
    ? { width: 'calc(100vw - 16px)', height: 'calc(100dvh - 16px)' }
    : { width: 'min(960px, calc(100vw - 48px))', height: 'min(760px, calc(100dvh - 48px))' },
)
// 窄屏是触屏:控件放大一档
const ctlSize = computed(() => (compact.value ? 'large' : 'medium'))
const contentStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '1 1 0',
  minHeight: '0',
  padding: '0',
  overflow: 'hidden',
}
const footerStyle = { padding: '0' }

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
  <n-modal
    v-model:show="show"
    preset="card"
    :title="t('role.grantMenus')"
    :closable="canClose"
    :close-on-esc="canClose"
    :mask-closable="false"
    :auto-focus="false"
    :style="modalStyle"
    :content-style="contentStyle"
    :footer-style="footerStyle"
  >
    <div class="gs" :class="{ 'is-narrow': compact }">
      <p v-if="hint" class="gs-hint">{{ hint }}</p>
      <GrantMenuTable
        :tree="tree"
        :granted="granted"
        :modules="modules"
        :default-module-id="defaultModuleId"
        :compact="compact"
        @update:checked="checked = $event"
        @summary="summary = $event"
      />
    </div>

    <template #footer>
      <footer class="gs-foot" :class="{ 'is-narrow': compact }">
        <div class="gs-summary">
          <span>{{ t('role.grantSummary', { on: summary.on, total: summary.total }) }}</span>
          <span>
            {{ t('role.grantSummarySplit', { pages: summary.pages, buttons: summary.buttons }) }}
          </span>
          <span v-if="summary.dirty" class="gs-dirty">{{ t('role.grantUnsaved') }}</span>
        </div>
        <n-button :size="ctlSize" :disabled="saving" @click="show = false">
          {{ t('common.cancel') }}
        </n-button>
        <n-button type="primary" :size="ctlSize" :loading="saving" @click="handleSave">
          {{ t('common.save') }}
        </n-button>
      </footer>
    </template>
  </n-modal>
</template>

<style scoped>
.gs {
  display: flex;
  flex: 1 1 0;
  flex-direction: column;
  min-height: 0;
  color: var(--text-1);
}

.gs-hint {
  margin: 10px 20px 0;
  font-size: 13px;
  color: var(--text-2);
}

/* 底栏:永远可见,与列表之间一条发丝线 */
.gs-foot {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 20px 14px;
  border-top: 1px solid var(--hairline);
}
.gs-summary {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 13px;
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}
.gs-dirty {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--warn);
}
.gs-dirty::before {
  content: '';
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--warn);
}

/* 窄屏:统计行独占一行,按钮等分 */
.gs-foot.is-narrow {
  flex-wrap: wrap;
  padding: 10px 16px 14px;
}
.gs-foot.is-narrow .gs-summary {
  flex: 1 1 100%;
}
.gs-foot.is-narrow .n-button {
  flex: 1;
}
</style>
