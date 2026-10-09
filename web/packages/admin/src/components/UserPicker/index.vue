<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  NButton,
  NDropdown,
  NEmpty,
  NInput,
  NSpin,
  NTab,
  NTabs,
  NTree,
  NTreeSelect,
  useMessage,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { useDebounceFn, useWindowSize } from '@vueuse/core'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import UserAvatar from './UserAvatar.vue'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import { orgApi, userApi } from '#/api'
import { buildTree } from '#/utils/tree'
import { translateError } from '#/utils/error'
import type { SysOrg, UserItem } from '#/types/api'

const props = defineProps<{ excludeIds?: number[] }>()
const emit = defineEmits<{ (e: 'confirm', ids: number[]): void }>()

const { t } = useI18n()
const message = useMessage()

const show = ref(false)

// 屏幕形态:
//   宽 / 中档 = 居中弹窗里的三栏(机构 | 可选用户 | 已选);
//   窄档(壳层 < 600)= 底部抽屉,三栏收成「可选用户 / 已选」两页,机构换成页内的树选择;
//   内容区 < 900 但还不到窄档时仍是弹窗,三栏同样收成两页(920 宽的三栏在这里放不下)。
// 三栏选择器在抽屉里太局促,所以宽 / 中档固定用弹窗,不跟随「表单形态」全局偏好。
const { bp } = useShellBreakpoint()
const { width: winW } = useWindowSize()
const compact = computed(() => bp.value === 'narrow' || winW.value < 900)
const variant = computed(() => (bp.value === 'narrow' ? 'drawer' : 'modal'))
const tab = ref<'users' | 'selected'>('users')

// ── 机构(左栏) ──
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
  reload()
}
// 两页形态下机构换成树选择:没有「再点一次取消」,选「全部」(id 0)即不限机构。
function onOrgPick(key: number | null) {
  selectedOrgKey.value = key ?? null
  reload()
}

// ── 可选用户(中栏) ──
// 一页 20 人;接口只有「姓名 / 账号」两个独立的模糊条件、没有联合检索,所以搜索框带一个范围切换。
const PAGE_SIZE = 20
type SearchField = 'name' | 'account'

const field = ref<SearchField>('name')
const keyword = ref('')
const page = ref(1)
const total = ref(0)
const rows = ref<UserItem[]>([])
const loading = ref(false)
// 防乱序:输入停顿、翻页、换机构都会发请求,后发的先回来时,先发的结果不能覆盖它。
let seq = 0

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))
const placeholder = computed(() =>
  field.value === 'name' ? t('userPicker.searchName') : t('userPicker.searchAccount'),
)
const scopeOptions = computed(() => [
  { key: 'name', label: t('user.name') },
  { key: 'account', label: t('user.account') },
])

async function load() {
  const mine = ++seq
  loading.value = true
  try {
    const kw = keyword.value.trim() || undefined
    const res = await userApi.page({
      page: page.value,
      pageSize: PAGE_SIZE,
      name: field.value === 'name' ? kw : undefined,
      account: field.value === 'account' ? kw : undefined,
      orgId: orgId.value,
    })
    if (mine !== seq) return
    rows.value = res.items
    total.value = res.total
  } catch (e) {
    if (mine !== seq) return
    rows.value = []
    total.value = 0
    message.error(translateError(e))
  } finally {
    if (mine === seq) loading.value = false
  }
}

/** 条件变了:回第 1 页重查。 */
function reload() {
  page.value = 1
  void load()
}
const reloadDebounced = useDebounceFn(reload, 300)

function onSearchInput(v: string) {
  keyword.value = v
  void reloadDebounced()
}
function onScope(key: SearchField) {
  field.value = key
  if (keyword.value.trim()) reload()
}
function goPage(n: number) {
  if (n < 1 || n > pageCount.value || n === page.value) return
  page.value = n
  void load()
}

// ── 已选用户(右栏) ──
const selected = reactive<UserItem[]>([])
const selectedSet = computed(() => new Set(selected.map(u => u.id)))
const excludeSet = computed(() => new Set(props.excludeIds ?? []))
const selectedSearch = ref('')
// 已选不多时搜索框只是噪音,超过一屏才出现。
const SELECTED_SEARCH_MIN = 8
const filteredSelected = computed(() => {
  const q = selectedSearch.value.trim().toLowerCase()
  if (!q) return selected
  return selected.filter(
    u => u.name.toLowerCase().includes(q) || u.account.toLowerCase().includes(q),
  )
})

function addUsers(users: UserItem[]) {
  for (const u of users) {
    if (!selectedSet.value.has(u.id) && !excludeSet.value.has(u.id)) selected.push(u)
  }
}
function removeUser(id: number) {
  const idx = selected.findIndex(u => u.id === id)
  if (idx >= 0) selected.splice(idx, 1)
}
function toggleUser(u: UserItem) {
  if (excludeSet.value.has(u.id)) return
  if (selectedSet.value.has(u.id)) removeUser(u.id)
  else addUsers([u])
}
function clearSelected() {
  selected.splice(0, selected.length)
}

// 「全选本页」:排除名单里的人不算;本页能选的都选上了,再点就是取消本页。
const selectable = computed(() => rows.value.filter(u => !excludeSet.value.has(u.id)))
const pageAllSelected = computed(
  () => selectable.value.length > 0 && selectable.value.every(u => selectedSet.value.has(u.id)),
)
function togglePage() {
  if (pageAllSelected.value) {
    for (const u of selectable.value) removeUser(u.id)
  } else {
    addUsers(selectable.value)
  }
}

/** 次行:账号(与姓名相同就不重复)· 机构。 */
function subline(u: UserItem) {
  return [u.account !== u.name ? u.account : '', u.orgName].filter(Boolean).join(' · ')
}

// ── 公共 API ──
async function open(ids?: number[]) {
  selected.splice(0, selected.length)
  selectedSearch.value = ''
  selectedOrgKey.value = null
  keyword.value = ''
  field.value = 'name'
  page.value = 1
  rows.value = []
  total.value = 0
  tab.value = 'users'
  show.value = true

  void load()
  if (!ids?.length) return
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

async function handleConfirm() {
  emit(
    'confirm',
    selected.map(u => u.id),
  )
}

defineExpose({ open })
</script>

<template>
  <FormContainer
    v-model:show="show"
    :variant="variant"
    :title="t('userPicker.title')"
    :width="920"
    :on-confirm="handleConfirm"
    :confirm-text="t('common.confirm')"
  >
    <n-tabs v-if="compact" v-model:value="tab" type="segment" animated class="picker-tabs">
      <n-tab name="users">{{ t('userPicker.available') }}</n-tab>
      <n-tab name="selected">{{ t('userPicker.selected') }} {{ selected.length }}</n-tab>
    </n-tabs>
    <div class="user-picker" :class="{ 'is-compact': compact }">
      <!-- 左栏:机构(侧栏式列表;两页形态下换成可选用户页顶部的树选择) -->
      <div v-if="!compact" class="picker-org">
        <div class="side-title">{{ t('userPicker.org') }}</div>
        <n-tree
          :data="orgTree"
          key-field="id"
          label-field="name"
          children-field="children"
          selectable
          block-line
          default-expand-all
          :selected-keys="[selectedOrgKey ?? 0]"
          @update:selected-keys="onOrgSelect"
        />
      </div>

      <!-- 中栏:搜索 + 用户列表 -->
      <div v-show="!compact || tab === 'users'" class="picker-main">
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
        <div class="picker-toolbar">
          <n-input
            :value="keyword"
            round
            clearable
            :size="compact ? 'large' : 'medium'"
            :placeholder="placeholder"
            class="picker-search"
            @update:value="onSearchInput"
          >
            <template #prefix>
              <n-dropdown trigger="click" :options="scopeOptions" :value="field" @select="onScope">
                <span class="scope-trigger" role="button" tabindex="0">
                  <AppIcon icon="ph:magnifying-glass" :size="15" />
                  <AppIcon icon="ph:caret-down" :size="10" />
                </span>
              </n-dropdown>
            </template>
          </n-input>
          <n-button
            text
            type="primary"
            class="page-toggle"
            :disabled="selectable.length === 0"
            @click="togglePage"
          >
            {{ pageAllSelected ? t('userPicker.unselectPage') : t('userPicker.selectPage') }}
          </n-button>
        </div>

        <n-spin :show="loading" size="small" class="picker-spin">
          <div class="user-list" role="group">
            <div
              v-for="u in rows"
              :key="u.id"
              class="user-row"
              :class="{
                'is-selected': selectedSet.has(u.id),
                'is-disabled': excludeSet.has(u.id),
              }"
              role="checkbox"
              :aria-checked="selectedSet.has(u.id) ? 'true' : 'false'"
              :aria-disabled="excludeSet.has(u.id) ? 'true' : undefined"
              :tabindex="excludeSet.has(u.id) ? -1 : 0"
              @click="toggleUser(u)"
              @keydown.enter.prevent="toggleUser(u)"
              @keydown.space.prevent="toggleUser(u)"
            >
              <UserAvatar :name="u.name" :src="u.avatar" :size="32" />
              <span class="user-main">
                <span class="user-name">{{ u.name }}</span>
                <span v-if="subline(u)" class="user-sub">{{ subline(u) }}</span>
              </span>
              <span v-if="excludeSet.has(u.id)" class="user-tag">
                {{ t('userPicker.existing') }}
              </span>
              <span v-else class="user-check"><AppIcon icon="ph:check-bold" :size="12" /></span>
            </div>
            <n-empty
              v-if="!loading && rows.length === 0"
              :description="t('common.noData')"
              class="user-empty"
            />
          </div>
        </n-spin>

        <div class="picker-foot">
          <span class="picker-total">{{ t('userPicker.total', { n: total }) }}</span>
          <div v-if="pageCount > 1" class="pager">
            <button
              type="button"
              class="pager-btn pager-prev"
              :disabled="page <= 1"
              :aria-label="t('userPicker.prevPage')"
              @click="goPage(page - 1)"
            >
              <AppIcon icon="ph:caret-left" :size="14" />
            </button>
            <span class="pager-num">{{ page }} / {{ pageCount }}</span>
            <button
              type="button"
              class="pager-btn pager-next"
              :disabled="page >= pageCount"
              :aria-label="t('userPicker.nextPage')"
              @click="goPage(page + 1)"
            >
              <AppIcon icon="ph:caret-right" :size="14" />
            </button>
          </div>
        </div>
      </div>

      <!-- 右栏:已选 -->
      <div v-show="!compact || tab === 'selected'" class="picker-selected">
        <div class="side-head">
          <span class="side-title">
            {{ t('userPicker.selected') }}
            <span class="side-count">{{ selected.length }}</span>
          </span>
          <n-button
            text
            type="error"
            class="selected-clear"
            :disabled="selected.length === 0"
            @click="clearSelected"
          >
            {{ t('userPicker.clear') }}
          </n-button>
        </div>
        <n-input
          v-if="selected.length > SELECTED_SEARCH_MIN"
          v-model:value="selectedSearch"
          round
          clearable
          :size="compact ? 'large' : 'small'"
          :placeholder="t('common.search')"
          class="selected-search"
        />
        <div class="selected-list">
          <div v-for="u in filteredSelected" :key="u.id" class="selected-item">
            <UserAvatar :name="u.name" :src="u.avatar" :size="28" />
            <span class="user-main">
              <span class="user-name">{{ u.name }}</span>
              <span v-if="subline(u)" class="user-sub">{{ subline(u) }}</span>
            </span>
            <button
              type="button"
              class="item-remove"
              :aria-label="t('userPicker.remove')"
              @click="removeUser(u.id)"
            >
              <AppIcon icon="ph:x" :size="12" />
            </button>
          </div>
          <n-empty
            v-if="selected.length === 0"
            :description="t('userPicker.emptySelected')"
            class="user-empty"
          />
        </div>
      </div>
    </div>
  </FormContainer>
</template>

<style scoped>
/* 三栏:机构(浅底侧栏)| 可选用户 | 已选。整体定高,各栏自己滚,翻页和搜索时弹窗不跳。 */
.user-picker {
  display: flex;
  gap: 16px;
  height: min(460px, calc(100vh - 260px));
}

/* ── 左栏:macOS 侧栏式机构列表 ── */
.picker-org {
  width: 200px;
  flex-shrink: 0;
  overflow: auto;
  padding: 8px;
  border-radius: var(--radius-lg);
  background: var(--fill);
}
.side-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-3);
}
.picker-org .side-title {
  padding: 2px 8px 6px;
}
.picker-org :deep(.n-tree-node) {
  min-height: var(--sidebar-row-h);
  border-radius: var(--radius-md);
}
.picker-org :deep(.n-tree-node:hover) {
  background: var(--hover);
}
/* 灰底面板上 --sel-bg(6% 强调色)几乎看不出来,选中条用更实的 --fill-strong,文字再用强调色加粗。 */
.picker-org :deep(.n-tree-node.n-tree-node--selected) {
  background: var(--fill-strong);
}
.picker-org :deep(.n-tree-node.n-tree-node--selected .n-tree-node-content__text) {
  color: var(--sel-fg);
  font-weight: 600;
}

/* ── 中栏 ── */
.picker-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.picker-org-select {
  margin-bottom: 8px;
}
.picker-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
}
.picker-search {
  flex: 1;
  min-width: 0;
}
.scope-trigger {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  color: var(--text-3);
  cursor: pointer;
}
.page-toggle {
  flex-shrink: 0;
  font-size: 13.5px;
}
.picker-spin {
  flex: 1;
  min-height: 0;
}
.picker-spin :deep(.n-spin-content) {
  height: 100%;
}
.user-list {
  height: 100%;
  overflow: auto;
  margin: 0 -4px;
  padding: 0 4px;
}

.user-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 48px;
  padding: 0 10px;
  border-radius: var(--radius-md);
  cursor: pointer;
  transition: background var(--transition-fast);
  outline: none;
}
.user-row:hover {
  background: var(--hover);
}
.user-row.is-selected {
  background: var(--sel-bg);
}
.user-row:focus-visible {
  box-shadow: inset 0 0 0 2px var(--sel-ring);
}
.user-row.is-disabled {
  cursor: not-allowed;
  background: none;
  opacity: 0.55;
}

.user-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.user-name {
  font-size: 14px;
  color: var(--text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.user-sub {
  font-size: 12.5px;
  color: var(--text-3);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.user-tag {
  flex-shrink: 0;
  font-size: 12.5px;
  color: var(--text-3);
}

/* 圆形勾选标记:空心环 → 选中后实心强调色。整行可点,它只负责告诉人「选了没」。 */
.user-check {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border-radius: 999px;
  border: 1.5px solid var(--border);
  color: transparent;
  transition:
    background var(--transition-fast),
    border-color var(--transition-fast);
}
.user-row.is-selected .user-check {
  background: var(--acc-solid);
  border-color: var(--acc-solid);
  color: var(--on-acc);
}

.user-empty {
  padding: 48px 0;
}

.picker-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 34px;
  padding-top: 8px;
  border-top: 1px solid var(--hairline);
  font-size: 13px;
  color: var(--text-3);
}
.pager {
  display: flex;
  align-items: center;
  gap: 4px;
}
.pager-num {
  min-width: 48px;
  text-align: center;
  font-variant-numeric: tabular-nums;
  color: var(--text-2);
}
.pager-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: none;
  border-radius: var(--radius-sm);
  background: none;
  color: var(--text-2);
  cursor: pointer;
  transition: background var(--transition-fast);
}
.pager-btn:hover:not(:disabled) {
  background: var(--hover);
}
.pager-btn:disabled {
  color: var(--text-4);
  cursor: not-allowed;
}

/* ── 右栏 ── */
.picker-selected {
  width: 232px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding-left: 16px;
  border-left: 1px solid var(--hairline);
}
.side-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 30px;
  margin-bottom: 8px;
}
.side-head .side-title {
  font-size: 13.5px;
  color: var(--text-1);
}
.side-count {
  margin-left: 4px;
  font-weight: 500;
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}
.selected-search {
  margin-bottom: 8px;
}
.selected-list {
  flex: 1;
  min-height: 0;
  overflow: auto;
}
.selected-item {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 0 6px 0 8px;
  border-radius: var(--radius-md);
}
.selected-item:hover {
  background: var(--hover);
}
.item-remove {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: none;
  border-radius: 999px;
  background: none;
  color: var(--text-3);
  cursor: pointer;
  opacity: 0;
  transition:
    opacity var(--transition-fast),
    background var(--transition-fast);
}
.selected-item:hover .item-remove,
.item-remove:focus-visible {
  opacity: 1;
}
.item-remove:hover {
  background: var(--fill-strong);
  color: var(--err);
}

/* 两页形态:分段页签在上,当前页占满整行;已选页不再要左侧分隔线,条目放大到可点;触屏没有悬停,移除钮常显 */
.picker-tabs {
  margin-bottom: 12px;
}
.user-picker.is-compact {
  height: min(60vh, 520px);
}
.is-compact .picker-selected {
  width: auto;
  flex: 1;
  padding-left: 0;
  border-left: none;
}
.is-compact .selected-item {
  min-height: 52px;
}
.is-compact .item-remove {
  opacity: 1;
  width: 32px;
  height: 32px;
}
.is-compact .user-row {
  min-height: 56px;
}
</style>
