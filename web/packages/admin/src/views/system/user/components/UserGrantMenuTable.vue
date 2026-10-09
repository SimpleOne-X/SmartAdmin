<script setup lang="ts">
// 用户授权弹窗的节点列表:应用切换 + 搜索 + 目录卡片(目录 → 页面 → 按钮,每个节点一行三态)。
// 分组复用角色页 grantMenuGroups.buildGroups 的结构(不用它的勾选态),角色页的 GrantMenuTable 不动。
// 草稿在父组件(reactive Map),这里只经 userGrantState 的纯函数改它。嵌套目录与角色页一样展平成带前缀的行。
import { computed, reactive, ref, watch } from 'vue'
import { NButton, NInput, NSelect } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { translateMenuTitle } from '#/locales/menuTitle'
import type { MenuTreeNode } from '#/types/menu'
import type { UserMenuEffective } from '#/types/api'
import { buildGroups, type CatalogGroup, type MenuRow } from '../../role/components/grantMenuGroups'
import UserGrantNodeRow from './UserGrantNodeRow.vue'
import { leakedCodes, setTriState, triStateOf, type Draft, type TriState } from './userGrantState'

const UNASSIGNED = 0

const props = defineProps<{
  tree: MenuTreeNode[]
  effective: UserMenuEffective
  draft: Draft
  defaultModuleId: number
  maxDate: string | null
}>()

const { t } = useI18n()
const search = ref('')
const query = computed(() => search.value.trim().toLowerCase())
const moduleId = ref(props.defaultModuleId)
watch(
  () => props.defaultModuleId,
  v => (moduleId.value = v),
)

const groups = computed(() => buildGroups(props.tree, new Set(), t('role.apiOnlyRow')))
const nodeMap = computed(() => new Map(props.effective.nodes.map(n => [n.menuId, n])))
const effectiveIds = computed(
  () => new Set(props.effective.nodes.filter(n => n.effective).map(n => n.menuId)),
)
const titles = computed(() => {
  const map = new Map<number, string>()
  const walk = (nodes: MenuTreeNode[]) => {
    for (const n of nodes) {
      map.set(n.id, translateMenuTitle(n.title, n.path || undefined))
      walk(n.children)
    }
  }
  walk(props.tree)
  return map
})

// ── 应用切换:同角色页,按顶级节点的 moduleId 分 ──
const moduleOfTop = computed(() => new Map(props.tree.map(n => [n.id, n.moduleId ?? UNASSIGNED])))
const moduleOf = (g: CatalogGroup) => moduleOfTop.value.get(g.id) ?? UNASSIGNED
const moduleOptions = computed(() => {
  const options = props.effective.modules.map(m => ({
    label: translateMenuTitle(m.title),
    value: m.id,
  }))
  if (groups.value.some(g => moduleOf(g) === UNASSIGNED))
    options.push({ label: t('menu.moduleUnassigned'), value: UNASSIGNED })
  return options
})

// ── 搜索:目录名命中 → 整组;否则只留页面名 / 按钮名命中的行 ──
const matchesRow = (m: MenuRow, q: string) =>
  m.title.toLowerCase().includes(q) || m.buttons.some(b => b.title.toLowerCase().includes(q))
const views = computed(() => {
  const q = query.value
  const out: { group: CatalogGroup; menus: MenuRow[] }[] = []
  for (const g of groups.value) {
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

// ── 折叠(搜索时一律展开,免得命中项藏在折叠里) ──
const collapsed = reactive(new Set<number>())
const isOpen = (id: number) => !!query.value || !collapsed.has(id)
function toggle(id: number) {
  if (collapsed.has(id)) collapsed.delete(id)
  else collapsed.add(id)
}

// ── 行属性与改动 ──
function rowProps(id: number, title: string, path?: string) {
  const node = nodeMap.value.get(id)
  const grantable = node?.grantable ?? false
  const state = triStateOf(props.draft, id)
  return {
    title,
    path,
    node,
    state,
    entry: props.draft.get(id),
    editable: props.effective.targetEditable && grantable,
    notGrantable: props.effective.targetEditable && !grantable,
    maxDate: props.maxDate,
    leaked:
      state === 'deny'
        ? leakedCodes(props.tree, effectiveIds.value, id).map(l => ({
            code: l.code,
            from: l.carriers.map(c => titles.value.get(c) ?? String(c)).join('、'),
          }))
        : [],
  }
}
const onState = (id: number, next: TriState) => setTriState(props.draft, id, next, props.maxDate)
function onExpire(id: number, v: string | null) {
  const entry = props.draft.get(id)
  if (entry) entry.expireDate = v
}
function onRemark(id: number, v: string) {
  const entry = props.draft.get(id)
  if (entry) entry.remark = v
}
</script>

<template>
  <div class="ut">
    <div class="ut-toolbar">
      <n-select
        v-model:value="moduleId"
        class="ut-module"
        :options="moduleOptions"
        :aria-label="t('menu.module')"
      />
      <n-input
        v-model:value="search"
        clearable
        :placeholder="t('role.grantSearch')"
        :input-props="{ 'aria-label': t('role.grantSearch'), autocomplete: 'off' }"
        style="flex: 1; min-width: 0"
      >
        <template #prefix><AppIcon class="faint" icon="ph:magnifying-glass" :size="15" /></template>
      </n-input>
    </div>

    <div class="ut-scroll">
      <section v-for="v in views" :key="v.group.id" class="ut-group">
        <header v-if="!v.group.standalone" class="ut-head">
          <n-button
            text
            class="ut-fold"
            :aria-expanded="isOpen(v.group.id)"
            :aria-label="v.group.title"
            @click="toggle(v.group.id)"
          >
            <AppIcon :icon="isOpen(v.group.id) ? 'ph:caret-down' : 'ph:caret-right'" :size="14" />
          </n-button>
          <span v-if="v.group.synthetic" class="ut-head-title">{{ v.group.title }}</span>
          <UserGrantNodeRow
            v-else
            level="catalog"
            v-bind="rowProps(v.group.id, v.group.title)"
            @update:state="s => onState(v.group.id, s)"
            @update:expire-date="d => onExpire(v.group.id, d)"
            @update:remark="r => onRemark(v.group.id, r)"
          />
        </header>
        <div v-show="isOpen(v.group.id)">
          <template v-for="m in v.menus" :key="m.anchor ? `anchor-${m.id}` : m.id">
            <div v-if="m.anchor" class="ut-anchor">
              <span class="ut-tag">{{ m.title }}</span>
            </div>
            <UserGrantNodeRow
              v-else
              level="page"
              v-bind="rowProps(m.id, m.title, m.path)"
              @update:state="s => onState(m.id, s)"
              @update:expire-date="d => onExpire(m.id, d)"
              @update:remark="r => onRemark(m.id, r)"
            />
            <UserGrantNodeRow
              v-for="b in m.buttons"
              :key="b.id"
              level="button"
              v-bind="rowProps(b.id, b.title)"
              @update:state="s => onState(b.id, s)"
              @update:expire-date="d => onExpire(b.id, d)"
              @update:remark="r => onRemark(b.id, r)"
            />
          </template>
        </div>
      </section>

      <div v-if="!views.length" class="ut-empty">
        {{ query ? t('role.grantNoMatch') : t('role.grantNoMenus') }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.ut {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.ut-toolbar {
  flex: none;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  padding: 10px 20px 12px;
}
.ut-module {
  flex: none;
  width: 180px;
}
.ut-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 4px 20px 20px;
  scrollbar-width: thin;
}
.ut-group {
  border: 1px solid var(--hairline);
  border-radius: 14px;
  margin-bottom: 12px;
  overflow: hidden;
}
.ut-head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 46px;
  padding: 6px 16px;
}
.ut-head-title {
  font-size: 15px;
  font-weight: 600;
}
.ut-fold {
  flex: none;
}
.ut-anchor {
  padding: 8px 16px;
  border-top: 1px solid var(--hairline);
}
.ut-tag {
  display: inline-block;
  padding: 1px 8px;
  border-radius: 6px;
  background: var(--fill);
  color: var(--text-2);
  font-size: 13px;
  font-weight: 500;
}
.ut-empty {
  padding: 64px 24px;
  text-align: center;
  color: var(--text-2);
}
@media (max-width: 640px) {
  .ut-module {
    width: 100%;
  }
}
</style>
