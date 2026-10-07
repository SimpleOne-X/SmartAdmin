<script setup lang="ts">
// 角色授权列表:目录卡片 → 页面行 → 按钮胶囊。分组与勾选联动在 grantMenuGroups.ts(纯函数,有单测),这里只管渲染。
// 直接挂在目录下的按钮(无页面权限项,如只给移动端 / 第三方调的接口)渲染成该目录卡片里的一行,
// 页面名位置显示「接口权限(无页面)」;勾选提交时只提交按钮 id,不需要为它们建假页面。
// 控件都是 Naive 官方组件(NRadioGroup / NInput / NCheckbox / NTag / NTooltip);外层 GrantMenuSheet 管弹窗壳与底栏。
import { computed, reactive, ref, watch } from 'vue'
import {
  NButton,
  NCheckbox,
  NInput,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NTag,
  NTooltip,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { translateMenuTitle } from '#/locales/menuTitle'
import type { MenuTreeNode } from '#/types/menu'
import type { ModuleRow } from '#/types/api'
import MatchText from './MatchText.vue'
import {
  buildGroups,
  collectChecked as collect,
  countGrants,
  groupCount,
  menuButtonCount,
  menusState,
  setButtonChecked,
  setGroupChecked,
  setMenuChecked,
  type CatalogGroup,
  type GrantSummary,
  type MenuRow,
} from './grantMenuGroups'

const UNASSIGNED = 0
/** 应用不多于这个数用分段控件,再多放不下,退回下拉。 */
const SEGMENT_MAX = 4

const props = defineProps<{
  tree: MenuTreeNode[]
  granted: number[]
  modules: ModuleRow[]
  defaultModuleId: number
  /** 窄屏(触屏):控件放大一档,行改成「名称 + 已选数」一行、胶囊另起一行 */
  compact?: boolean
}>()
const ctlSize = computed(() => (props.compact ? 'large' : 'medium'))

const emit = defineEmits<{
  (e: 'update:checked', ids: number[]): void
  (e: 'summary', summary: GrantSummary): void
}>()

const { t } = useI18n()
const search = ref('')
const query = computed(() => search.value.trim().toLowerCase())
const moduleId = ref(props.defaultModuleId)
watch(
  () => props.defaultModuleId,
  v => {
    moduleId.value = v
  },
)

const allGroups = reactive<CatalogGroup[]>([])
/** 用户改动前的勾选快照,用来判断「有未保存的修改」。 */
let baseline = ''
const idsKey = (ids: number[]) => ids.toSorted((a, b) => a - b).join(',')

function emitSummary(ids: number[]) {
  emit('summary', { ...countGrants(allGroups), dirty: idsKey(ids) !== baseline })
}

watch(
  () => [props.tree, props.granted] as const,
  ([tree, granted]) => {
    const groups = buildGroups(tree, new Set(granted), t('role.apiOnlyRow'))
    allGroups.splice(0, allGroups.length, ...groups)
    const ids = collect(allGroups)
    baseline = idsKey(ids)
    emitSummary(ids)
  },
  { immediate: true },
)

// ── 应用(模块)切换 ──
const moduleOfGroup = computed(() => new Map(props.tree.map(n => [n.id, n.moduleId ?? UNASSIGNED])))
const moduleOf = (g: CatalogGroup) => moduleOfGroup.value.get(g.id) ?? UNASSIGNED

const moduleOptions = computed(() => {
  const options = props.modules.map(m => ({ label: translateMenuTitle(m.title), value: m.id }))
  // 「未分配」下没有任何目录时不占一个空的分段(当前正停在它上面则保留,免得选中项凭空消失)
  if (moduleId.value === UNASSIGNED || allGroups.some(g => moduleOf(g) === UNASSIGNED))
    options.push({ label: t('menu.moduleUnassigned'), value: UNASSIGNED })
  return options
})
const segmented = computed(() => moduleOptions.value.length <= SEGMENT_MAX)

// ── 搜索过滤:目录名命中 → 整个目录;否则只留页面名 / 按钮名命中的行 ──
interface GroupView {
  /** 真实分组(勾选态与联动都落在它身上) */
  group: CatalogGroup
  /** 当前可见的行;没在搜索时就是全部行 */
  menus: MenuRow[]
}

const matchesGroup = (g: CatalogGroup, q: string) =>
  g.title.toLowerCase().includes(q) || g.menus.some(m => matchesRow(m, q))
const matchesRow = (m: MenuRow, q: string) =>
  m.title.toLowerCase().includes(q) || m.buttons.some(b => b.title.toLowerCase().includes(q))

const views = computed<GroupView[]>(() => {
  const q = query.value
  const out: GroupView[] = []
  for (const g of allGroups) {
    if (moduleOf(g) !== moduleId.value) continue
    if (!q || g.title.toLowerCase().includes(q)) {
      out.push({ group: g, menus: g.menus })
      continue
    }
    const menus = g.menus.filter(m => matchesRow(m, q))
    if (menus.length) out.push({ group: g, menus })
  }
  return out
})

/** 当前应用下没结果、但别的应用里有:提示去切换。 */
const elsewhereMatches = computed(() => {
  const q = query.value
  if (!q || views.value.length) return 0
  return allGroups.filter(g => moduleOf(g) !== moduleId.value && matchesGroup(g, q)).length
})

// ── 勾选 ──
function changed() {
  const ids = collect(allGroups)
  emit('update:checked', ids)
  emitSummary(ids)
}
function onGroup(v: GroupView, val: boolean) {
  setGroupChecked(v.group, val, v.menus)
  changed()
}
function onMenu(v: GroupView, menu: MenuRow, val: boolean) {
  setMenuChecked(v.group, menu, val)
  changed()
}
function onButton(v: GroupView, menu: MenuRow, buttonId: number, val: boolean) {
  const btn = menu.buttons.find(b => b.id === buttonId)
  if (!btn) return
  setButtonChecked(v.group, menu, btn, val)
  changed()
}

/** 胶囊(NTag checkable)不是原生控件:补上键盘切换,和勾选框一样能用 Enter / 空格操作 */
function onChipKey(e: KeyboardEvent, v: GroupView, menu: MenuRow, buttonId: number) {
  const btn = menu.buttons.find(b => b.id === buttonId)
  if (!btn) return
  e.preventDefault()
  onButton(v, menu, buttonId, !btn.checked)
}

// ── 计数展示 ──
const countText = (c: { on: number; total: number }) => (c.total ? `${c.on}/${c.total}` : '')
const isFull = (c: { on: number; total: number }) => c.total > 0 && c.on === c.total
const groupCountOf = (v: GroupView) => groupCount({ menus: v.menus })

// ── 折叠/展开(搜索时一律展开,免得命中项藏在折叠里) ──
const collapsed = reactive(new Set<number>())
const foldable = computed(() => views.value.filter(v => !v.group.standalone))
const allCollapsed = computed(
  () => foldable.value.length > 0 && foldable.value.every(v => collapsed.has(v.group.id)),
)
const isOpen = (v: GroupView) => !!query.value || !collapsed.has(v.group.id)

function toggleCollapse(id: number) {
  if (collapsed.has(id)) collapsed.delete(id)
  else collapsed.add(id)
}
function toggleCollapseAll() {
  if (allCollapsed.value) collapsed.clear()
  else for (const v of foldable.value) collapsed.add(v.group.id)
}

// 滚动后在工具栏下缘出一条发丝线,提示上面还有内容被滚走了
const scrolled = ref(false)
const onScroll = (e: Event) => {
  scrolled.value = (e.target as HTMLElement).scrollTop > 2
}
</script>

<template>
  <div class="gt" :class="{ 'is-narrow': compact }">
    <div class="gt-toolbar" :class="{ 'is-scrolled': scrolled }">
      <n-radio-group
        v-if="segmented"
        v-model:value="moduleId"
        name="grant-module"
        :size="ctlSize"
        :aria-label="t('menu.module')"
        :class="{ 'gt-seg-full': compact }"
      >
        <n-radio-button v-for="o in moduleOptions" :key="o.value" :value="o.value">
          {{ o.label }}
        </n-radio-button>
      </n-radio-group>
      <n-select
        v-else
        v-model:value="moduleId"
        :options="moduleOptions"
        :size="ctlSize"
        :placeholder="t('menu.module')"
        style="width: 180px; flex: none"
      />
      <n-input
        v-model:value="search"
        clearable
        :size="ctlSize"
        :placeholder="t('role.grantSearch')"
        :input-props="{ 'aria-label': t('role.grantSearch'), autocomplete: 'off' }"
        style="flex: 1; min-width: 0"
      >
        <template #prefix><AppIcon class="faint" icon="ph:magnifying-glass" :size="15" /></template>
      </n-input>
      <n-button
        v-if="foldable.length && !query"
        text
        type="primary"
        :size="ctlSize"
        style="flex: none"
        @click="toggleCollapseAll"
      >
        {{ allCollapsed ? t('common.expandAll') : t('common.collapseAll') }}
      </n-button>
    </div>

    <div class="gt-scroll" @scroll="onScroll">
      <section
        v-for="v in views"
        :key="v.group.id"
        class="gt-group"
        :class="{ 'is-standalone': v.group.standalone, 'is-collapsed': !isOpen(v) }"
      >
        <div
          v-if="!v.group.standalone"
          class="gt-group-head"
          role="button"
          tabindex="0"
          :aria-expanded="isOpen(v)"
          @click="toggleCollapse(v.group.id)"
          @keydown.enter="toggleCollapse(v.group.id)"
        >
          <span @click.stop>
            <n-checkbox
              v-bind="menusState(v.menus)"
              :aria-label="t('role.grantSelectAll', { name: v.group.title })"
              @update:checked="(val: boolean) => onGroup(v, val)"
            />
          </span>
          <span class="gt-group-title"><MatchText :text="v.group.title" :query="query" /></span>
          <span class="gt-count" :class="{ full: isFull(groupCountOf(v)) }">
            {{ countText(groupCountOf(v)) }}
          </span>
          <AppIcon class="gt-chev faint" icon="ph:caret-down" :size="14" />
        </div>

        <div class="gt-collapse">
          <div class="gt-collapse-in">
            <div v-for="m in v.menus" :key="m.anchor ? `anchor-${m.id}` : m.id" class="gt-row">
              <div class="gt-row-name">
                <n-checkbox
                  :checked="m.checked"
                  :aria-label="m.title"
                  @update:checked="(val: boolean) => onMenu(v, m, val)"
                >
                  <span class="gt-name-box">
                    <span v-if="m.anchor">
                      <span class="gt-tag"><MatchText :text="m.title" :query="query" /></span>
                    </span>
                    <span v-else class="gt-title">
                      <MatchText :text="m.title" :query="query" />
                    </span>
                  </span>
                </n-checkbox>
              </div>
              <div class="gt-chips">
                <n-tooltip
                  v-for="b in m.buttons"
                  :key="b.id"
                  :disabled="!b.permission"
                  placement="top"
                >
                  <template #trigger>
                    <n-tag
                      checkable
                      round
                      class="gt-chip"
                      role="checkbox"
                      tabindex="0"
                      :aria-checked="b.checked"
                      :bordered="false"
                      :checked="b.checked"
                      :size="ctlSize"
                      @update:checked="(val: boolean) => onButton(v, m, b.id, val)"
                      @keydown.enter="onChipKey($event, v, m, b.id)"
                      @keydown.space="onChipKey($event, v, m, b.id)"
                    >
                      <AppIcon v-if="b.checked" class="gt-chip-check" icon="ph:check" :size="13" />
                      <MatchText :text="b.title" :query="query" />
                    </n-tag>
                  </template>
                  <div class="gt-perm">{{ b.permission?.split(';').join('\n') }}</div>
                </n-tooltip>
                <span v-if="!m.buttons.length" class="gt-none">{{ t('role.noButtons') }}</span>
              </div>
              <span class="gt-rc" :class="{ full: isFull(menuButtonCount(m)) }">
                {{ countText(menuButtonCount(m)) }}
              </span>
            </div>
          </div>
        </div>
      </section>

      <div v-if="!views.length" class="gt-empty">
        <AppIcon class="faint" icon="ph:magnifying-glass" :size="34" />
        <b>{{ query ? t('role.grantNoMatch') : t('role.grantNoMenus') }}</b>
        <span v-if="elsewhereMatches">
          {{ t('role.grantNoMatchElsewhere', { count: elsewhereMatches }) }}
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.gt {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

/* 工具区:应用切换 + 搜索 + 折叠 */
.gt-toolbar {
  flex: none;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  padding: 10px 20px 12px;
  transition: box-shadow var(--transition-base);
}
.gt-toolbar.is-scrolled {
  box-shadow: 0 1px 0 var(--hairline);
}
/* 窄屏:分段控件独占一行、各段等分 */
.gt-seg-full {
  display: flex;
  width: 100%;
}
.gt-seg-full :deep(.n-radio-button) {
  flex: 1;
  text-align: center;
}

/* 滚动区:整个弹窗里唯一纵向滚动的地方,头尾固定 */
.gt-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 4px 20px 20px;
  scrollbar-width: thin;
}

/* 目录卡片:发丝线描边,不铺底色 */
.gt-group {
  border: 1px solid var(--hairline);
  border-radius: 14px;
  margin-bottom: 12px;
  overflow: hidden;
}
.gt-group-head {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 0 16px;
  min-height: 46px;
  cursor: pointer;
  font-size: 15px;
  font-weight: 600;
}
.gt-group-head:hover {
  background: var(--hover);
}
.gt-group-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.gt-count {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}
.gt-count.full,
.gt-rc.full {
  color: var(--sel-fg);
}
.gt-chev {
  flex: none;
  transition: transform 0.25s var(--ease);
}
.gt-group.is-collapsed .gt-chev {
  transform: rotate(-90deg);
}
.gt-collapse {
  display: grid;
  grid-template-rows: 1fr;
  transition: grid-template-rows 0.28s var(--ease);
}
.gt-group.is-collapsed .gt-collapse {
  grid-template-rows: 0fr;
}
.gt-collapse-in {
  min-height: 0;
  overflow: hidden;
}
/* 折叠后里面的控件不该还能被 Tab 到 */
.gt-group.is-collapsed .gt-collapse-in {
  visibility: hidden;
  transition: visibility 0s 0.28s;
}

/* 行:页面 + 按钮胶囊 + 已选数;行间是发丝线 */
.gt-row {
  display: grid;
  grid-template-columns: 240px minmax(0, 1fr) 52px;
  gap: 8px 16px;
  padding: 10px 16px;
  align-items: start;
  border-top: 1px solid var(--hairline);
}
.gt-group.is-standalone .gt-row {
  border-top: 0;
}
.gt-row-name {
  min-width: 0;
  min-height: 30px;
  font-size: 15px;
}
.gt-row-name :deep(.n-checkbox) {
  align-items: flex-start;
}
.gt-row-name :deep(.n-checkbox__label) {
  padding-left: 10px;
  min-width: 0;
  white-space: normal;
}
.gt-name-box {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.gt-title {
  overflow-wrap: anywhere;
  line-height: 22px;
}
.gt-tag {
  display: inline-block;
  padding: 1px 8px;
  border-radius: 6px;
  background: var(--fill);
  color: var(--text-2);
  font-size: 13px;
  font-weight: 500;
}
.gt-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  min-height: 30px;
}
.gt-none {
  font-size: 13px;
  color: var(--text-3);
}
.gt-rc {
  min-height: 30px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  font-size: 13px;
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}
.gt-rc.full {
  font-weight: 600;
}

/* 圆形勾选框:NCheckbox 默认是圆角方框,这里改成圆(三态仍沿用官方的对勾 / 横线) */
.gt :deep(.n-checkbox .n-checkbox-box),
.gt :deep(.n-checkbox .n-checkbox-box__border) {
  border-radius: 50%;
}

/* 按钮权限胶囊:选中态带对勾,不只靠颜色区分 */
.gt-chip.n-tag {
  cursor: pointer;
  user-select: none;
}
.gt-chip.n-tag--checked {
  background: var(--sel-bg) !important;
  color: var(--sel-fg) !important;
  font-weight: 600;
  box-shadow: inset 0 0 0 1px var(--sel-ring);
}
.gt-chip:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 2px;
}
.gt-chip-check {
  margin-right: 3px;
}
/* 悬停提示:权限码,多条换行 */
.gt-perm {
  font-family: var(--font-mono);
  white-space: pre-line;
}

.gt-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 64px 24px;
  text-align: center;
  font-size: 13px;
  color: var(--text-2);
}
.gt-empty b {
  font-size: 16px;
  color: var(--text-1);
}

/* 窄屏:行改成「名称 + 已选数」一行、胶囊另起一行 */
.gt.is-narrow .gt-toolbar {
  padding: 8px 16px 10px;
}
.gt.is-narrow .gt-scroll {
  padding: 4px 12px 16px;
}
.gt.is-narrow .gt-row {
  grid-template-columns: minmax(0, 1fr) auto;
  padding: 10px 12px;
}
.gt.is-narrow .gt-chips {
  grid-column: 1 / -1;
  grid-row: 2;
  padding-left: 32px;
}
.gt.is-narrow .gt-rc {
  grid-column: 2;
  grid-row: 1;
}

@media (prefers-reduced-motion: reduce) {
  .gt *,
  .gt *::before,
  .gt *::after {
    transition-duration: 0.01ms !important;
  }
}
</style>
