<script setup lang="ts">
import { computed, h, onMounted, reactive, ref, unref } from 'vue'
import { NButton, NEmpty, NInput, NTab, NTabs, NTree, NTreeSelect, useMessage } from 'naive-ui'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import { useI18n } from 'vue-i18n'
import { useWindowSize } from '@vueuse/core'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import { orgApi, userApi } from '#/api'
import { buildTree } from '#/utils/tree'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS } from '#/utils/tableFilter'
import type { SysOrg, UserItem } from '#/types/api'

const props = defineProps<{ excludeIds?: number[] }>()
const emit = defineEmits<{ (e: 'confirm', ids: number[]): void }>()

const { t } = useI18n()
const message = useMessage()

const show = ref(false)
const tableRef = ref<SmartTableInst<UserItem>>()

// 屏幕形态:
//   宽 / 中档 = 居中弹窗里的三栏(机构树 | 可选用户 | 已选);
//   窄档(壳层 < 600)= 底部抽屉,三栏收成「可选用户 / 已选」两页,机构树换成页内的树选择;
//   内容区 < 900 但还不到窄档时仍是弹窗,三栏同样收成两页(1100 宽的三栏在这里放不下)。
// 三栏选择器在抽屉里太局促,所以宽 / 中档固定用弹窗,不跟随「表单形态」全局偏好。
const { bp } = useShellBreakpoint()
const { width: winW } = useWindowSize()
const compact = computed(() => bp.value === 'narrow' || winW.value < 900)
const variant = computed(() => (bp.value === 'narrow' ? 'drawer' : 'modal'))
const tab = ref<'users' | 'selected'>('users')
const btnSize = computed(() => (compact.value ? 'large' : 'small'))

// ── 机构树(左面板) ──
const orgFlat = ref<SysOrg[]>([])
const orgTree = computed(() => {
  const all = { id: 0, parentId: -1, name: t('common.all'), children: buildTree(orgFlat.value) }
  return [all]
})
const selectedOrgKey = ref<number | null>(null)
const orgId = computed(() =>
  selectedOrgKey.value && selectedOrgKey.value !== 0 ? selectedOrgKey.value : undefined,
)

onMounted(async () => {
  try {
    orgFlat.value = await orgApi.list()
  } catch {
    /* 静默 */
  }
})

function onOrgSelect(keys: Array<string | number>) {
  selectedOrgKey.value = (keys[0] as number) ?? null
  tableRef.value?.refresh()
}
// 两页形态下机构树换成树选择:没有「再点一次取消」,选「全部」(id 0)即不限机构。
function onOrgPick(key: number | null) {
  selectedOrgKey.value = key ?? null
  tableRef.value?.refresh()
}

// ── 可选用户表(中间面板) ──
const checkedKeys = ref<(string | number)[]>([])
const excludeSet = computed(() => new Set(props.excludeIds ?? []))

const fetcher = (params: { page: number; pageSize: number; account?: string }) =>
  userApi.page({
    page: params.page,
    pageSize: params.pageSize,
    account: params.account,
    orgId: orgId.value,
  })

const columns: SmartTableColumn<UserItem>[] = [
  { type: 'selection' },
  { key: 'name', title: () => t('user.name'), width: 100 },
  {
    key: 'account',
    title: () => t('user.account'),
    width: 120,
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 70,
    render: r => {
      const already = selectedSet.value.has(r.id) || excludeSet.value.has(r.id)
      return h(
        NButton,
        {
          size: 'tiny',
          type: 'primary',
          quaternary: true,
          disabled: already,
          onClick: () => addUsers([r]),
        },
        () => (already ? t('userPicker.added') : t('userPicker.add')),
      )
    },
  },
]

// ── 已选用户(右面板) ──
const selected = reactive<UserItem[]>([])
const selectedSet = computed(() => new Set(selected.map(u => u.id)))
const selectedSearch = ref('')
const filteredSelected = computed(() => {
  const q = selectedSearch.value.trim().toLowerCase()
  if (!q) return selected
  return selected.filter(
    u => u.name.toLowerCase().includes(q) || u.account.toLowerCase().includes(q),
  )
})

function addUsers(users: UserItem[]) {
  for (const u of users) {
    if (!selectedSet.value.has(u.id) && !excludeSet.value.has(u.id)) {
      selected.push(u)
    }
  }
}

function addChecked() {
  // SmartTable 只 expose rows,别改用未公开的 tableData —— 恒为 undefined。
  // unref 兼容类型声明的 Ref 与运行时 expose 解包两种形态。
  const rows = (unref(tableRef.value?.rows) ?? []).filter(r => checkedKeys.value.includes(r.id))
  addUsers(rows)
  checkedKeys.value = []
}

function removeUser(id: number) {
  const idx = selected.findIndex(u => u.id === id)
  if (idx >= 0) selected.splice(idx, 1)
}

function clearSelected() {
  selected.splice(0, selected.length)
}

// ── 公共 API ──
async function open(ids?: number[]) {
  selected.splice(0, selected.length)
  checkedKeys.value = []
  selectedSearch.value = ''
  selectedOrgKey.value = null
  tab.value = 'users'
  show.value = true

  if (ids?.length) {
    try {
      // ponytail: 拉一页,靠 id 过滤。取 200 是为了在应用把 Api:MaxPageSize 调小时也不会被后端回 48001。
      // 用户超过 200 个时,排在后面的 id 解析不出名字,走下面那条退回显示 id 的分支;
      // 真要支持更大规模的用户名单,再改成分页搜索。
      const { items } = await userApi.page({ page: 1, pageSize: 200 })
      const all = items.filter(u => ids.includes(u.id))
      // 没拉到的 id(翻页之外),退回只显示 id
      const found = new Set(all.map(u => u.id))
      for (const u of all) selected.push(u)
      for (const id of ids) {
        if (!found.has(id)) selected.push({ id, account: String(id), name: String(id) } as UserItem)
      }
    } catch {
      for (const id of ids) selected.push({ id, account: String(id), name: String(id) } as UserItem)
    }
  }
}

async function handleConfirm() {
  const ids = selected.map(u => u.id)
  emit('confirm', ids)
}

defineExpose({ open })
</script>

<template>
  <FormContainer
    v-model:show="show"
    :variant="variant"
    :title="t('userPicker.title')"
    :width="1100"
    :on-confirm="handleConfirm"
    :confirm-text="t('common.confirm')"
  >
    <n-tabs v-if="compact" v-model:value="tab" type="segment" animated class="picker-tabs">
      <n-tab name="users">{{ t('userPicker.available') }}</n-tab>
      <n-tab name="selected">{{ t('userPicker.selected') }}({{ selected.length }})</n-tab>
    </n-tabs>
    <div class="user-picker" :class="{ 'is-compact': compact }">
      <!-- 左面板:机构树(两页形态下换成可选用户页顶部的树选择) -->
      <div v-if="!compact" class="picker-tree">
        <div class="panel-header">{{ t('org.title') }}</div>
        <n-tree
          :data="orgTree"
          key-field="id"
          label-field="name"
          children-field="children"
          selectable
          block-line
          default-expand-all
          :selected-keys="selectedOrgKey != null ? [selectedOrgKey] : []"
          @update:selected-keys="onOrgSelect"
        />
      </div>

      <!-- 中间面板:可选用户 -->
      <div v-show="!compact || tab === 'users'" class="picker-table">
        <n-tree-select
          v-if="compact"
          :value="selectedOrgKey ?? 0"
          :options="orgTree"
          key-field="id"
          label-field="name"
          children-field="children"
          default-expand-all
          size="large"
          class="picker-org-select"
          @update:value="onOrgPick"
        />
        <div class="panel-header">
          <span>{{ t('userPicker.available') }}</span>
          <n-button
            :size="btnSize"
            type="primary"
            :disabled="checkedKeys.length === 0"
            @click="addChecked"
          >
            {{ t('userPicker.addChecked') }}({{ checkedKeys.length }})
          </n-button>
        </div>
        <SmartTable
          :default-page-size="100"
          ref="tableRef"
          :columns="columns"
          :fetcher="fetcher"
          :search="{ container: 'table' }"
          :pagination="{ pageSize: 10 }"
          size="small"
          storage-key="user-picker"
          :tool-button="false"
          :checked-row-keys="checkedKeys"
          @update:checked-row-keys="(keys: (string | number)[]) => (checkedKeys = keys)"
          @error="e => message.error(translateError(e))"
        />
      </div>

      <!-- 右面板:已选用户 -->
      <div v-show="!compact || tab === 'selected'" class="picker-selected">
        <div class="panel-header">
          <span>{{ t('userPicker.selected') }}({{ selected.length }})</span>
          <n-button
            :size="btnSize"
            quaternary
            type="error"
            :disabled="selected.length === 0"
            @click="clearSelected"
          >
            {{ t('userPicker.clear') }}
          </n-button>
        </div>
        <n-input
          v-model:value="selectedSearch"
          :size="compact ? 'large' : 'small'"
          clearable
          :placeholder="t('common.search')"
          style="margin-bottom: 8px"
        />
        <div class="selected-list">
          <div v-for="u in filteredSelected" :key="u.id" class="selected-item">
            <span class="selected-name">
              {{ u.name }}
              <span class="selected-account">({{ u.account }})</span>
            </span>
            <n-button :size="btnSize" quaternary type="error" @click="removeUser(u.id)">
              <template #icon><AppIcon icon="ph:x" :size="14" /></template>
            </n-button>
          </div>
          <n-empty
            v-if="filteredSelected.length === 0"
            :description="t('common.noData')"
            style="padding: 24px 0"
          />
        </div>
      </div>
    </div>
  </FormContainer>
</template>

<style scoped>
/* 选择器的尺寸、分隔线、字号。 */
.user-picker {
  display: flex;
  gap: 12px;
  min-height: min(440px, calc(100vh - 250px));
}
.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 34px;
  font-size: 13.5px;
  font-weight: 600;
  padding: 0 0 8px;
  border-bottom: 1px solid var(--hairline);
  margin-bottom: 8px;
}
.picker-tree {
  width: 200px;
  flex-shrink: 0;
  overflow: auto;
  border-right: 1px solid var(--hairline);
  padding-right: 12px;
}
.picker-table {
  flex: 1;
  min-width: 0;
  overflow: hidden;
}
.picker-org-select {
  margin-bottom: 8px;
}
.picker-selected {
  width: 220px;
  flex-shrink: 0;
  border-left: 1px solid var(--hairline);
  padding-left: 12px;
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.selected-list {
  flex: 1;
  min-height: 0;
  overflow: auto;
  max-height: 380px;
}
.selected-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  padding: 2px 0;
  font-size: 13.5px;
  border-bottom: 1px solid var(--hairline);
}
.selected-item:last-child {
  border-bottom: none;
}
.selected-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.selected-account {
  color: var(--text-3);
  margin-left: 2px;
}

/* 两页形态:分段页签在上,当前页占满整行;已选页不再要左侧分隔线,条目行高放大到可点 */
.picker-tabs {
  margin-bottom: 12px;
}
.user-picker.is-compact {
  min-height: 0;
}
.is-compact .picker-selected {
  width: auto;
  flex: 1;
  border-left: none;
  padding-left: 0;
}
.is-compact .selected-list {
  max-height: none;
}
.is-compact .selected-item {
  min-height: 44px;
}
</style>
