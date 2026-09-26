<script setup lang="ts">
// 角色授权列表:目录卡片 → 页面行 → 按钮胶囊。分组与勾选联动在 grantMenuGroups.ts(纯函数,有单测),这里只管渲染。
// 直接挂在目录下的按钮(无页面权限项,如只给移动端 / 第三方调的接口)渲染成该目录卡片里的一行,
// 页面名位置显示「接口权限(无页面)」;勾选提交时只提交按钮 id,不需要为它们建假页面。
// 版式与 CSS 变量(--g-*)由外层 GrantMenuSheet 提供,本组件只在它里面用。
import { computed, reactive, ref, watch } from 'vue'
import { NSelect } from 'naive-ui'
import { useI18n } from 'vue-i18n'
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
}>()

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
  const options = props.modules.map(m => ({ label: m.title, value: m.id }))
  // 「未分配」下没有任何目录时不占一个空的分段(当前正停在它上面则保留,免得选中项凭空消失)
  if (moduleId.value === UNASSIGNED || allGroups.some(g => moduleOf(g) === UNASSIGNED))
    options.push({ label: t('menu.moduleUnassigned'), value: UNASSIGNED })
  return options
})
const segmented = computed(() => moduleOptions.value.length <= SEGMENT_MAX)
const activeIndex = computed(() =>
  Math.max(
    0,
    moduleOptions.value.findIndex(o => o.value === moduleId.value),
  ),
)

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
const on = (e: Event) => (e.target as HTMLInputElement).checked

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
  <div class="gt">
    <div class="gt-toolbar" :class="{ 'is-scrolled': scrolled }">
      <div
        v-if="segmented"
        class="gt-seg"
        role="group"
        :aria-label="t('menu.module')"
        :style="{ '--n': moduleOptions.length, '--i': activeIndex }"
      >
        <span class="gt-seg-thumb" aria-hidden="true" />
        <button
          v-for="o in moduleOptions"
          :key="o.value"
          type="button"
          :aria-pressed="o.value === moduleId"
          :title="o.label"
          @click="moduleId = o.value"
        >
          {{ o.label }}
        </button>
      </div>
      <n-select
        v-else
        v-model:value="moduleId"
        :options="moduleOptions"
        :placeholder="t('menu.module')"
        style="width: 180px; flex: none"
      />
      <div class="gt-search" :class="{ 'has-text': search }">
        <svg
          class="gt-mag"
          width="15"
          height="15"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          stroke-width="1.6"
          stroke-linecap="round"
          aria-hidden="true"
        >
          <circle cx="7" cy="7" r="4.6" />
          <path d="M10.6 10.6L14 14" />
        </svg>
        <input
          v-model="search"
          type="search"
          :placeholder="t('role.grantSearch')"
          :aria-label="t('role.grantSearch')"
          autocomplete="off"
        />
        <button
          class="gt-clear"
          type="button"
          :aria-label="t('role.grantSearchClear')"
          @click="search = ''"
        >
          <svg
            width="9"
            height="9"
            viewBox="0 0 14 14"
            fill="none"
            stroke="currentColor"
            stroke-width="2.4"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M3 3l8 8M11 3l-8 8" />
          </svg>
        </button>
      </div>
      <button
        v-if="foldable.length && !query"
        class="gt-text-btn"
        type="button"
        @click="toggleCollapseAll"
      >
        {{ allCollapsed ? t('common.expandAll') : t('common.collapseAll') }}
      </button>
    </div>

    <div class="gt-scroll" @scroll="onScroll">
      <section
        v-for="v in views"
        :key="v.group.id"
        class="gt-group"
        :class="{ 'is-standalone': v.group.standalone, 'is-collapsed': !isOpen(v) }"
      >
        <div v-if="!v.group.standalone" class="gt-group-head">
          <input
            class="gt-cb"
            type="checkbox"
            :aria-label="t('role.grantSelectAll', { name: v.group.title })"
            v-bind="menusState(v.menus)"
            @change="onGroup(v, on($event))"
          />
          <button
            class="gt-group-toggle"
            type="button"
            :aria-expanded="isOpen(v)"
            @click="toggleCollapse(v.group.id)"
          >
            <span class="gt-group-title"><MatchText :text="v.group.title" :query="query" /></span>
            <span class="gt-count" :class="{ full: isFull(groupCountOf(v)) }">
              {{ countText(groupCountOf(v)) }}
            </span>
            <svg
              class="gt-chev"
              width="14"
              height="14"
              viewBox="0 0 14 14"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M3 5l4 4 4-4" />
            </svg>
          </button>
        </div>

        <div class="gt-collapse">
          <div class="gt-collapse-in">
            <div v-for="m in v.menus" :key="m.anchor ? `anchor-${m.id}` : m.id" class="gt-row">
              <label class="gt-row-name">
                <input
                  class="gt-cb"
                  type="checkbox"
                  :checked="m.checked"
                  @change="onMenu(v, m, on($event))"
                />
                <span class="gt-name-box">
                  <span v-if="m.anchor">
                    <span class="gt-tag"><MatchText :text="m.title" :query="query" /></span>
                  </span>
                  <span v-else class="gt-title"><MatchText :text="m.title" :query="query" /></span>
                  <span v-if="m.anchor || m.path" class="gt-sub">
                    {{ m.anchor ? t('role.apiOnlyHint') : m.path }}
                  </span>
                </span>
              </label>
              <div class="gt-chips">
                <label
                  v-for="b in m.buttons"
                  :key="b.id"
                  class="gt-chip"
                  :title="b.permission?.split(';').join('\n')"
                >
                  <input
                    type="checkbox"
                    :checked="b.checked"
                    @change="onButton(v, m, b.id, on($event))"
                  />
                  <svg
                    viewBox="0 0 16 16"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2.2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    aria-hidden="true"
                  >
                    <path d="M3.5 8.5l3 3 6-6.5" />
                  </svg>
                  <MatchText :text="b.title" :query="query" />
                </label>
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
        <svg
          width="36"
          height="36"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          stroke-width="1.2"
          stroke-linecap="round"
          aria-hidden="true"
        >
          <circle cx="7" cy="7" r="4.6" />
          <path d="M10.6 10.6L14 14" />
        </svg>
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
  gap: 8px;
  align-items: center;
  padding: 10px 20px 12px;
  transition: box-shadow 0.2s;
}
.gt-toolbar.is-scrolled {
  box-shadow: 0 1px 0 var(--g-sep);
}
.gt-seg {
  position: relative;
  flex: none;
  display: grid;
  grid-template-columns: repeat(var(--n), 1fr);
  width: calc(var(--n) * 96px);
  background: var(--g-fill);
  border-radius: 10px;
  padding: 2px;
}
.gt-seg-thumb {
  position: absolute;
  top: 2px;
  bottom: 2px;
  left: 2px;
  width: calc((100% - 4px) / var(--n));
  background: var(--g-card);
  border-radius: 8px;
  box-shadow:
    0 1px 3px rgba(0, 0, 0, 0.14),
    0 0 0 0.5px rgba(0, 0, 0, 0.04);
  transform: translateX(calc(var(--i) * 100%));
  transition: transform 0.26s cubic-bezier(0.2, 0.8, 0.2, 1);
}
.gt-seg button {
  position: relative;
  z-index: 1;
  height: 30px;
  padding: 0 8px;
  border: 0;
  background: none;
  border-radius: 8px;
  font: inherit;
  font-size: 13px;
  font-weight: 500;
  color: var(--g-label-2);
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  transition: color 0.15s;
}
.gt-seg button[aria-pressed='true'] {
  color: var(--g-label);
  font-weight: 600;
}
.gt-search {
  position: relative;
  flex: 1;
  min-width: 0;
}
.gt-mag {
  position: absolute;
  left: 10px;
  top: 50%;
  margin-top: -7.5px;
  color: var(--g-label-3);
  pointer-events: none;
}
.gt-search input {
  width: 100%;
  height: 34px;
  border: 0;
  border-radius: 10px;
  background: var(--g-fill);
  color: var(--g-label);
  padding: 0 34px 0 32px;
  font: inherit;
  font-size: 14px;
  outline: none;
  transition:
    box-shadow 0.15s,
    background 0.15s;
}
.gt-search input::placeholder {
  color: var(--g-label-3);
}
.gt-search input:focus {
  box-shadow: 0 0 0 3px var(--g-ring);
  background: var(--g-card);
}
/* 浏览器自带的搜索清除叉与自绘的重复,去掉 */
.gt-search input::-webkit-search-cancel-button {
  display: none;
}
.gt-clear {
  position: absolute;
  right: 6px;
  top: 50%;
  margin-top: -10px;
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 50%;
  background: var(--g-label-3);
  color: var(--g-card);
  display: none;
  place-items: center;
  cursor: pointer;
}
.gt-search.has-text .gt-clear {
  display: grid;
}
.gt-text-btn {
  flex: none;
  height: 34px;
  padding: 0 10px;
  border: 0;
  background: none;
  border-radius: 8px;
  font: inherit;
  font-size: 14px;
  color: var(--g-accent-text);
  white-space: nowrap;
  cursor: pointer;
}
.gt-text-btn:hover {
  background: var(--g-accent-tint);
}

/* 滚动区:整个抽屉里唯一纵向滚动的地方,头尾固定 */
.gt-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 4px 20px 20px;
  scrollbar-width: thin;
  scrollbar-color: var(--g-label-3) transparent;
}
.gt-group {
  background: var(--g-card);
  border-radius: 14px;
  margin-bottom: 14px;
}
.gt-group-head {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 0 16px;
  min-height: 48px;
}
.gt-group-toggle {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  height: 48px;
  padding: 0;
  border: 0;
  background: none;
  text-align: left;
  font: inherit;
  font-size: 15px;
  font-weight: 600;
  color: var(--g-label);
  cursor: pointer;
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
  color: var(--g-label-2);
  font-variant-numeric: tabular-nums;
}
.gt-count.full {
  color: var(--g-accent-text);
}
.gt-chev {
  flex: none;
  color: var(--g-label-3);
  transition: transform 0.25s cubic-bezier(0.2, 0.8, 0.2, 1);
}
.gt-group.is-collapsed .gt-chev {
  transform: rotate(-90deg);
}
.gt-collapse {
  display: grid;
  grid-template-rows: 1fr;
  transition: grid-template-rows 0.28s cubic-bezier(0.2, 0.8, 0.2, 1);
}
.gt-group.is-collapsed .gt-collapse {
  grid-template-rows: 0fr;
}
.gt-collapse-in {
  min-height: 0;
  overflow: hidden;
}
/* 折叠后里面的复选框不该还能被 Tab 到 */
.gt-group.is-collapsed .gt-collapse-in {
  visibility: hidden;
  transition: visibility 0s 0.28s;
}

/* 行:页面 + 按钮胶囊 + 已选数;行间是缩进发丝线 */
.gt-row {
  position: relative;
  display: grid;
  grid-template-columns: 240px 1fr 52px;
  gap: 8px 16px;
  padding: 10px 16px;
  align-items: start;
}
.gt-row::before {
  content: '';
  position: absolute;
  left: 16px;
  right: 0;
  top: 0;
  height: 1px;
  background: var(--g-sep);
}
.gt-group.is-standalone .gt-row::before {
  display: none;
}
.gt-row-name {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  min-width: 0;
  min-height: 30px;
  font-size: 15px;
  color: var(--g-label);
  cursor: pointer;
}
.gt-row-name .gt-cb {
  margin-top: 4px;
}
.gt-name-box {
  display: flex;
  flex-direction: column;
  min-width: 0;
  padding-top: 3px;
}
.gt-title {
  overflow-wrap: anywhere;
}
.gt-sub {
  font-family: var(--font-family-mono, ui-monospace, Consolas, monospace);
  font-size: 12px;
  line-height: 1.3;
  color: var(--g-label-2);
  overflow-wrap: anywhere;
}
.gt-tag {
  display: inline-block;
  padding: 2px 8px;
  border-radius: 6px;
  background: var(--g-fill);
  color: var(--g-label-2);
  font-size: 12px;
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
  color: var(--g-label-3);
}
.gt-rc {
  min-height: 30px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  font-size: 13px;
  color: var(--g-label-2);
  font-variant-numeric: tabular-nums;
}
.gt-rc.full {
  color: var(--g-accent-text);
  font-weight: 600;
}

/* 圆形勾选框(三态);对勾/横线用 mask 画,颜色跟 --g-on-accent 走,主题色怎么换都清楚 */
.gt-cb {
  appearance: none;
  -webkit-appearance: none;
  position: relative;
  flex: none;
  width: 22px;
  height: 22px;
  margin: 0;
  border-radius: 50%;
  border: 1.5px solid var(--g-cb-border);
  background: transparent;
  cursor: pointer;
  transition:
    background-color 0.15s,
    border-color 0.15s,
    transform 0.12s;
}
.gt-cb::after {
  content: '';
  position: absolute;
  inset: -1.5px;
  background: var(--g-on-accent);
  opacity: 0;
  -webkit-mask: var(--g-mask-check) center / 16px no-repeat;
  mask: var(--g-mask-check) center / 16px no-repeat;
}
.gt-cb:active {
  transform: scale(0.9);
}
.gt-cb:checked,
.gt-cb:indeterminate {
  background-color: var(--g-accent);
  border-color: var(--g-accent);
}
.gt-cb:checked::after,
.gt-cb:indeterminate::after {
  opacity: 1;
}
.gt-cb:indeterminate::after {
  -webkit-mask-image: var(--g-mask-dash);
  mask-image: var(--g-mask-dash);
}
.gt-cb:focus-visible {
  outline: 3px solid var(--g-ring);
  outline-offset: 2px;
}

/* 按钮权限胶囊:选中态带对勾,不只靠颜色区分 */
.gt-chip {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 30px;
  padding: 0 12px;
  border-radius: 15px;
  background: var(--g-fill);
  color: var(--g-label);
  font-size: 13px;
  cursor: pointer;
  user-select: none;
  transition:
    background 0.15s,
    color 0.15s,
    transform 0.12s;
}
.gt-chip:hover {
  background: var(--g-fill-hover);
}
.gt-chip:active {
  transform: scale(0.96);
}
.gt-chip input {
  position: absolute;
  inset: 0;
  opacity: 0;
  margin: 0;
  cursor: pointer;
}
.gt-chip svg {
  display: none;
  width: 13px;
  height: 13px;
}
.gt-chip:has(input:checked) {
  background: var(--g-accent-tint);
  color: var(--g-accent-text);
  font-weight: 600;
}
.gt-chip:has(input:checked) svg {
  display: block;
}
.gt-chip:has(input:focus-visible) {
  outline: 3px solid var(--g-ring);
  outline-offset: 2px;
}

.gt-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 64px 24px;
  text-align: center;
  font-size: 13px;
  color: var(--g-label-2);
}
.gt-empty svg {
  margin-bottom: 8px;
  color: var(--g-label-3);
}
.gt-empty b {
  font-size: 16px;
  color: var(--g-label);
}

/* 窄屏:行改成「名称 + 已选数」一行、胶囊另起一行 */
@media (max-width: 640px) {
  .gt-toolbar {
    flex-wrap: wrap;
  }
  .gt-seg {
    width: 100%;
  }
  .gt-row {
    grid-template-columns: 1fr auto;
  }
  .gt-chips {
    grid-column: 1 / -1;
    grid-row: 2;
    padding-left: 34px;
  }
  .gt-rc {
    grid-column: 2;
    grid-row: 1;
  }
}
@media (pointer: coarse) {
  .gt-chip {
    height: 36px;
  }
  .gt-seg button {
    height: 36px;
  }
}
@media (prefers-reduced-motion: reduce) {
  .gt *,
  .gt *::before,
  .gt *::after {
    transition-duration: 0.01ms !important;
  }
}
</style>
