<script setup lang="ts">
// 用户「授权菜单」弹窗:壳的尺寸与底栏照角色页 GrantMenuSheet(定高、只有列表滚动、底栏常驻),
// 内容是两个页签——「单独授权」三态列表与「有效权限」只读表。打开时一次拉齐菜单树、授权记录与有效权限;
// 保存只提交变更集(与打开时的快照比)。目标用户不可编辑(自己 / 超管 / 范围外 / 对方是管理员)时整体只读并说明原因。
import { computed, reactive, ref, shallowRef, type CSSProperties } from 'vue'
import { NAlert, NButton, NModal, NSpin, NTabPane, NTabs, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useWindowSize } from '@vueuse/core'
import { menuApi, userApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import type { MenuTreeNode } from '#/types/menu'
import type { UserMenuEffective, UserMenuGrantItem } from '#/types/api'
import UserGrantMenuTable from './UserGrantMenuTable.vue'
import UserGrantEffectiveTable from './UserGrantEffectiveTable.vue'
import {
  READONLY_REASON_KEYS,
  countDraft,
  diffDraft,
  draftFromGrants,
  invalidExpiry,
  maxExpireDate,
  type Draft,
} from './userGrantState'

const emit = defineEmits<{ (e: 'saved'): void }>()
const { t } = useI18n()
const message = useMessage()
const auth = useAuthStore()

const show = ref(false)
const loading = ref(false)
const saving = ref(false)
const tab = ref<'grant' | 'effective'>('grant')
const user = ref<{ id: number; name: string } | null>(null)
const tree = shallowRef<MenuTreeNode[]>([])
const grants = shallowRef<UserMenuGrantItem[]>([])
const effective = shallowRef<UserMenuEffective | null>(null)
/** 打开时的快照;草稿另建一份,免得两边共用同一批对象、改一处两处都变。 */
let baseline: Draft = new Map()
const draft = reactive<Draft>(new Map())

/** 窄于此宽度弹窗几乎铺满视口(同角色页)。 */
const COMPACT = 640
const { width: winW } = useWindowSize()
const compact = computed(() => winW.value <= COMPACT)
const modalStyle = computed(() =>
  compact.value
    ? { width: 'calc(100vw - 16px)', height: 'calc(100dvh - 16px)' }
    : { width: 'min(1040px, calc(100vw - 48px))', height: 'min(780px, calc(100dvh - 48px))' },
)
const contentStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '1 1 0',
  minHeight: '0',
  padding: '0',
  overflow: 'hidden',
}
const footerStyle = { padding: '0' }

const maxDate = computed(() => maxExpireDate(effective.value?.delegatedMaxDays, new Date()))
const counts = computed(() => countDraft(draft))
const dirty = computed(() => {
  const d = diffDraft(baseline, draft)
  return d.upserts.length + d.removes.length > 0
})
const readonlyText = computed(() => {
  const e = effective.value
  if (!e || e.targetEditable) return ''
  const key = e.readOnlyReason != null ? READONLY_REASON_KEYS[e.readOnlyReason] : undefined
  return t('userGrant.readonly', { reason: key ? t(key) : '' })
})
/** 默认停在当前所在的应用;不在清单里(如已被去掉)就落到第一个。 */
const defaultModuleId = computed(() => {
  const modules = effective.value?.modules ?? []
  const current = auth.currentModuleId
  return current != null && modules.some(m => m.id === current) ? current : (modules[0]?.id ?? 0)
})

async function open(target: { id: number; name: string }) {
  user.value = target
  tab.value = 'grant'
  effective.value = null
  show.value = true
  loading.value = true
  try {
    const [menuTree, records, eff] = await Promise.all([
      menuApi.tree(),
      userApi.getMenuGrants(target.id),
      userApi.getEffectiveMenus(target.id),
    ])
    tree.value = menuTree
    grants.value = records
    baseline = draftFromGrants(records)
    draft.clear()
    for (const [id, entry] of draftFromGrants(records)) draft.set(id, entry)
    effective.value = eff
  } catch (e) {
    message.error(translateError(e))
    show.value = false
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!user.value) return
  const bad = invalidExpiry(baseline, draft, effective.value?.delegatedMaxDays, new Date())
  if (bad.length) {
    message.warning(t('userGrant.invalidExpiry', { count: bad.length }))
    return
  }
  const { upserts, removes } = diffDraft(baseline, draft)
  if (!upserts.length && !removes.length) {
    show.value = false
    return
  }
  saving.value = true
  try {
    await userApi.setMenuGrants(user.value.id, upserts, removes)
    message.success(t('userGrant.saved'))
    show.value = false
    emit('saved')
  } catch (e) {
    message.error(translateError(e))
  } finally {
    saving.value = false
  }
}

defineExpose({ open })
</script>

<template>
  <n-modal
    v-model:show="show"
    preset="card"
    :title="user ? t('userGrant.title', { name: user.name }) : ''"
    :closable="!saving"
    :close-on-esc="!saving"
    :mask-closable="false"
    :auto-focus="false"
    :style="modalStyle"
    :content-style="contentStyle"
    :footer-style="footerStyle"
  >
    <n-spin :show="loading" class="ugs-spin">
      <div v-if="effective" class="ugs">
        <div class="ugs-notes">
          <n-alert v-if="readonlyText" type="warning" :bordered="false">{{ readonlyText }}</n-alert>
          <n-alert v-if="!effective.hasRoles" type="info" :bordered="false">
            {{ t('userGrant.noRoles') }}
          </n-alert>
          <p class="ugs-hint">{{ t('userGrant.hint') }}</p>
        </div>
        <n-tabs v-model:value="tab" type="line" class="ugs-tabs">
          <!-- 单独授权页签切走时保留挂载:应用选择、搜索词、折叠状态不丢 -->
          <n-tab-pane name="grant" display-directive="show" :tab="t('userGrant.tabGrant')">
            <UserGrantMenuTable
              :tree="tree"
              :effective="effective"
              :draft="draft"
              :default-module-id="defaultModuleId"
              :max-date="maxDate"
            />
          </n-tab-pane>
          <n-tab-pane name="effective" :tab="t('userGrant.tabEffective')">
            <UserGrantEffectiveTable :tree="tree" :effective="effective" :grants="grants" />
          </n-tab-pane>
        </n-tabs>
      </div>
    </n-spin>

    <template #footer>
      <footer class="ugs-foot" :class="{ 'is-narrow': compact }">
        <div class="ugs-summary">
          <span>{{ t('userGrant.summary', counts) }}</span>
          <span v-if="dirty" class="ugs-dirty">{{ t('userGrant.unsaved') }}</span>
        </div>
        <n-button :disabled="saving" @click="show = false">{{ t('common.cancel') }}</n-button>
        <n-button v-if="effective?.targetEditable" type="primary" :loading="saving" @click="save">
          {{ t('common.save') }}
        </n-button>
      </footer>
    </template>
  </n-modal>
</template>

<style scoped>
.ugs-spin,
.ugs-spin :deep(.n-spin-content),
.ugs,
.ugs-tabs {
  display: flex;
  flex-direction: column;
  flex: 1 1 0;
  min-height: 0;
}
.ugs-notes {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 20px 0;
}
.ugs-hint {
  margin: 0;
  font-size: 13px;
  color: var(--text-2);
}
.ugs-tabs :deep(.n-tabs-nav) {
  padding: 0 20px;
}
.ugs-tabs :deep(.n-tab-pane),
.ugs-tabs :deep(.n-tabs-pane-wrapper) {
  display: flex;
  flex-direction: column;
  flex: 1 1 0;
  min-height: 0;
  padding: 0;
}
.ugs-foot {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 20px 14px;
  border-top: 1px solid var(--hairline);
}
.ugs-summary {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  font-size: 13px;
  color: var(--text-2);
}
.ugs-dirty {
  color: var(--warn);
}
.ugs-foot.is-narrow {
  flex-wrap: wrap;
}
.ugs-foot.is-narrow .ugs-summary {
  flex: 1 1 100%;
}
</style>
