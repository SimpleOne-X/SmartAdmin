<script setup lang="ts">
// 机构管理 = 树表:SmartTable 远程模式(fetcher)+ children 树模式(无分页)。
// org list 平铺 → buildTree 拼 children;上级机构用 OrgTreeSelect(剪自身子树防成环);
// 无独立启停端点,StatusSwitch 走全量 update;删除有子机构后端拒,前端照调由 translateError 弹码。
import { computed, h, reactive, ref, shallowRef } from 'vue'
import {
  NButton,
  NSpace,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NSwitch,
  NDropdown,
  NModal,
  useMessage,
  type DropdownDividerOption,
  type DropdownOption,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import {
  SmartTable,
  type SmartTableColumn,
  type SmartTableFetcher,
  type SmartTableInst,
} from 'smart-naive-table'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import DictSelect from '#/components/DictSelect/index.vue'
import DictTag from '#/components/DictTag/index.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import OrgTreeSelect from '#/components/OrgTreeSelect/index.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { orgApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import { buildTree, expandableIds, type Tree } from '#/utils/tree'
import { filterTreeByParams, type TreeSearchFields } from '#/utils/treeFilter'
import type { OrgInput, SysOrg } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { run, confirm } = useConfirm()
const authStore = useAuthStore()

const loading = ref(false)
// 整份替换 + 只读(行内改动一律重拉,不往节点上写值),不需要深响应
const tree = shallowRef<Tree<SysOrg>[]>([])

/** 从服务端拉全量机构并拼成树。只由 treeFetcher 调用:增删改后一律走 `reload()`(让表格重新取数),不要直接调它。 */
async function load() {
  loading.value = true
  try {
    tree.value = buildTree(await orgApi.list())
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}

// ── 取数 + 搜索 + 展开控制 ─────────────────────────────────────────
// 树表用远程模式:3.0 静态模式的条件过滤是平铺的 rows.filter,不递归 children,在树上只会命中顶层行。
// 所以 fetcher 自己递归过滤,条件构造器只负责收集条件(全局序列化器摊平成 { name, code })。
// 取数器每次都重新拉一遍服务端:工具栏内置的刷新图标只会重新调 fetcher,
// 如果这里只过滤内存里的旧树,点刷新就是假刷新(别人新建的机构永远出不来)。机构是全量小表,多拉一次无所谓。
// 首次进入也由它取数(immediate),所以页面不需要 onMounted(load),也就没有「树还没加载就先过滤出空表」的窗口。
const tableRef = ref<SmartTableInst<Tree<SysOrg>>>()
const reload = () => tableRef.value?.refresh()

const SEARCH_FIELDS: TreeSearchFields<Tree<SysOrg>> = {
  name: n => n.name,
  code: n => n.code,
}
const treeFetcher: SmartTableFetcher<Tree<SysOrg>> = async params => {
  await load()
  const items = filterTreeByParams(tree.value, params, SEARCH_FIELDS)
  return { items, total: items.length }
}

// 展开受控:一旦传了 expanded-row-keys,naive 就以它为准,default-expand-all 会被初始值直接覆盖成"全折叠",
// 所以"默认全展开"得自己播种。每次取数完成(首次进入、搜索、清除条件、增删改后重拉)都按结果重算,
// 否则命中结果藏在折叠的祖先里看不见 —— filterTree 保留的祖先链就是靠它全部展开的。
const expandedKeys = ref<number[]>([])
// 取数器返回的(已过滤的)树:「展开全部」要按它算,而不是按整棵树。
const visibleTree = shallowRef<Tree<SysOrg>[]>([])
function onLoaded(rows: Tree<SysOrg>[]) {
  visibleTree.value = rows
  expandedKeys.value = expandableIds(rows)
}

const allExpanded = computed(() => expandedKeys.value.length > 0)
const toggleExpandAll = () =>
  (expandedKeys.value = allExpanded.value ? [] : expandableIds(visibleTree.value))

// ── 弹窗表单(parentId 可空:OrgTreeSelect clearable → null,save 时归一为 0=根)──
interface OrgForm {
  parentId: number | null
  name: string
  code: string
  category: string | null
  sort: number
  enabled: boolean
}
const show = ref(false)
const formRef = ref<FormInst | null>(null)
const editingId = ref<number | null>(null)
const rules: FormRules = {
  name: {
    required: true,
    whitespace: true,
    message: () => t('org.nameRequired'),
    trigger: ['input', 'blur'],
  },
}
const blank = (): OrgForm => ({
  parentId: 0,
  name: '',
  code: '',
  category: null,
  sort: 0,
  enabled: true,
})
const form = reactive<OrgForm>(blank())

// 行数据 → 完整入参:openEdit 回填与 StatusSwitch 行内改状态共用(后端无独立启停端点,均走全量 update)。
const toInput = (r: SysOrg): OrgInput => ({
  parentId: r.parentId,
  name: r.name,
  code: r.code,
  category: r.category ?? null,
  sort: r.sort,
  enabled: r.enabled,
})

function openAdd(parentId = 0) {
  editingId.value = null
  Object.assign(form, blank(), { parentId })
  show.value = true
}
function openEdit(r: SysOrg) {
  editingId.value = r.id
  Object.assign(form, toInput(r))
  show.value = true
}

/** FormContainer onConfirm:校验失败 reject / API 失败 return false → 弹层不关;成功正常返回自动关。 */
async function save() {
  await formRef.value?.validate()
  try {
    const payload: OrgInput = { ...form, parentId: form.parentId ?? 0 }
    if (editingId.value === null) await orgApi.add(payload)
    else await orgApi.update(editingId.value, payload)
    message.success(t('org.saved'))
    await reload()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}

// ── 复制弹窗 ──────────────────────────────────────────────────────
const copyShow = ref(false)
const copyId = ref(0)
const copySourceName = ref('')
const copyName = ref('')

function openCopy(r: SysOrg) {
  copyId.value = r.id
  copySourceName.value = r.name
  copyName.value = r.name + t('common.copySuffix')
  copyShow.value = true
}
async function confirmCopy() {
  const ok = await run(() => orgApi.copy(copyId.value, { name: copyName.value }), t('org.copied'))
  if (ok) {
    copyShow.value = false
    reload()
  }
}

/** 删除:下拉菜单项不适合内联 popconfirm,走 useConfirm().confirm 的 dialog(它的设计用途)。 */
const onDelete = (r: SysOrg) =>
  confirm({
    content: t('org.deleteConfirm', { name: r.name }),
    type: 'error',
    action: () => orgApi.remove(r.id),
    successMsg: t('org.deleted'),
  }).then(ok => {
    if (ok) reload()
  })

// 下拉菜单项的线条图标
const menuIcon = (name: string) => () => h(AppIcon, { icon: name, size: 16 })

const columns: SmartTableColumn<Tree<SysOrg>>[] = [
  // 序号与树列同固定左侧:固定列必须在最左连续,否则横向滚动时树列会压在序号列上。
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center', fixed: 'left' },
  // 树列左对齐,展开缩进才好读;fixed 是为了横向滚动时不丢失「这是哪一行」。
  {
    title: () => t('org.name'),
    key: 'name',
    // 树的缩进和展开箭头落在这一列:不指定时 Naive 取第一个没有 type 的列,正好是 64px 宽的序号列,
    // 层级一深序号就被缩进挤出列外
    tree: true,
    align: 'left',
    width: 260,
    fixed: 'left',
    ellipsis: { tooltip: true },
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    title: () => t('org.code'),
    key: 'code',
    ellipsis: { tooltip: true },
    render: r => h('span', { class: 'mono muted' }, r.code),
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    title: () => t('org.category'),
    key: 'category',
    width: 100,
    render: r => h(DictTag, { typeCode: 'org_category', value: r.category }),
  },
  {
    title: () => t('org.sort'),
    key: 'sort',
    width: 80,
    render: r => h('span', { class: 'num muted' }, r.sort),
  },
  {
    title: () => t('common.status'),
    key: 'enabled',
    width: 90,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !authStore.hasPerm('PUT:/api/v1/sys/org/{id}'),
        request: (next: boolean) => orgApi.update(r.id, { ...toInput(r), enabled: next }),
        // 重拉而非往行对象上写:搜索态下的祖先行是 filterTree 的浅拷贝,写它不会回到源树(开关会弹回去)。
        // StatusSwitch 是悲观更新(请求成功才 emit),所以这里重拉一次就是最终态,和本页其余变更的做法一致。
        'onUpdate:value': () => reload(),
      }),
  },
  // 操作收敛成「编辑 + 更多▾」:4 个操作塞 260px 会换行、行高参差,且没 fixed 时横向滚动够不着。
  {
    title: () => t('common.operation'),
    key: 'op',
    width: 150,
    fixed: 'right',
    render: r => {
      // 每个操作按各自权限码显隐;更多▾下拉只保留已授权项,全无则不出下拉。
      // 菜单项带线条图标;删除是破坏性操作,前面隔一条分割线并用错误色。
      const canAdd = authStore.hasPerm('POST:/api/v1/sys/org/add')
      const canCopy = authStore.hasPerm('POST:/api/v1/sys/org/{id}/copy')
      const canDelete = authStore.hasPerm('DELETE:/api/v1/sys/org/{id}')
      const rawOptions: (DropdownOption | DropdownDividerOption | null)[] = [
        canAdd ? { key: 'addChild', label: t('org.addChild'), icon: menuIcon('ph:plus') } : null,
        canCopy ? { key: 'copy', label: t('org.copy'), icon: menuIcon('ph:copy') } : null,
        (canAdd || canCopy) && canDelete ? { type: 'divider', key: 'divider' } : null,
        canDelete
          ? {
              key: 'delete',
              label: t('common.delete'),
              icon: menuIcon('ph:trash'),
              props: { style: 'color: var(--err)' },
            }
          : null,
      ]
      const dropdownOptions = rawOptions.filter(
        (o): o is DropdownOption | DropdownDividerOption => o !== null,
      )
      return h(NSpace, { size: 2, wrapItem: false, justify: 'center' }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/org/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        dropdownOptions.length
          ? h(
              NDropdown,
              {
                trigger: 'click',
                options: dropdownOptions,
                onSelect: (key: string) => {
                  if (key === 'addChild') openAdd(r.id)
                  else if (key === 'copy') openCopy(r)
                  else onDelete(r)
                },
              },
              () =>
                h(
                  NButton,
                  { size: 'small', quaternary: true, type: 'primary', iconPlacement: 'right' },
                  {
                    default: () => t('common.more'),
                    icon: () => h(AppIcon, { icon: 'ph:caret-down', size: 12 }),
                  },
                ),
            )
          : null,
      ])
    },
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <div class="view fill-page">
    <!-- expanded-row-keys / update:expanded-row-keys 不是 SmartTable 的 prop —— 它 inheritAttrs:false + v-bind="attrs",
         未声明的 attr 原样透传给内层 n-data-table(:loading 走的也是这条路)。
         card-on-narrow:卡片宽 < 600 换成卡片列表(树的缩进与展开状态库照旧带上,expanded-row-keys 照常生效)。 -->
    <SmartTable
      ref="tableRef"
      :columns="columns"
      :fetcher="treeFetcher"
      :loading="loading"
      row-key="id"
      :pagination="false"
      :search="{ container: 'table' }"
      :toolbar="TABLE_TOOLBAR"
      fill-height
      :min-row-height="TABLE_MIN_ROW_HEIGHT"
      card-on-narrow
      storage-key="sys-org"
      :expanded-row-keys="expandedKeys"
      @update:expanded-row-keys="(keys: number[]) => (expandedKeys = keys)"
      @loaded="onLoaded"
    >
      <template #toolbar-right>
        <n-button secondary @click="toggleExpandAll">
          <template #icon>
            <AppIcon
              :icon="allExpanded ? 'ph:arrows-in-line-vertical' : 'ph:arrows-out-line-vertical'"
              :size="16"
            />
          </template>
          {{ allExpanded ? t('common.collapseAll') : t('common.expandAll') }}
        </n-button>
        <n-button v-auth="'POST:/api/v1/sys/org/add'" type="primary" @click="openAdd(0)">
          <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
          {{ t('common.add') }}
        </n-button>
      </template>
    </SmartTable>

    <FormContainer
      v-model:show="show"
      :title="editingId === null ? t('org.addTitle') : t('org.editTitle')"
      :width="480"
      :on-confirm="save"
      :confirm-text="t('common.save')"
    >
      <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="80">
        <n-form-item :label="t('org.parent')">
          <OrgTreeSelect
            v-model:value="form.parentId"
            clearable
            :exclude-subtree-of="editingId"
            :placeholder="t('org.parentPlaceholder')"
          />
        </n-form-item>
        <n-form-item :label="t('org.name')" path="name">
          <n-input v-model:value="form.name" :placeholder="t('org.name')" />
        </n-form-item>
        <n-form-item :label="t('org.code')">
          <n-input
            v-model:value="form.code"
            :placeholder="t('org.codePlaceholder')"
            :disabled="editingId !== null"
          />
          <template v-if="editingId !== null" #feedback>
            <span class="form-hint">{{ t('org.codeImmutableHint') }}</span>
          </template>
        </n-form-item>
        <n-form-item :label="t('org.category')">
          <DictSelect
            v-model:value="form.category"
            type-code="org_category"
            clearable
            :placeholder="t('org.categoryPlaceholder')"
          />
        </n-form-item>
        <n-form-item :label="t('org.sort')">
          <n-input-number v-model:value="form.sort" :min="0" style="width: 160px" />
        </n-form-item>
        <n-form-item :label="t('common.status')">
          <n-switch v-model:value="form.enabled" />
        </n-form-item>
      </n-form>
    </FormContainer>

    <!-- 复制机构:居中小弹窗,遮罩不可关,右上角 × 可关 -->
    <n-modal
      v-model:show="copyShow"
      preset="card"
      :title="t('org.copyTitle')"
      style="width: min(420px, calc(100vw - 24px))"
      :bordered="false"
      :mask-closable="false"
      :segmented="{ footer: 'soft' }"
    >
      <p class="copy-tip">{{ t('org.copyConfirm', { name: copySourceName }) }}</p>
      <n-form label-placement="top" require-mark-placement="left">
        <n-form-item :label="t('org.copyNameLabel')">
          <n-input v-model:value="copyName" :placeholder="t('org.copyNameLabel')" />
        </n-form-item>
      </n-form>
      <template #footer>
        <n-space justify="end">
          <n-button secondary @click="copyShow = false">{{ t('common.cancel') }}</n-button>
          <n-button type="primary" @click="confirmCopy">{{ t('common.confirm') }}</n-button>
        </n-space>
      </template>
    </n-modal>
  </div>
</template>

<style scoped>
/* display / flex-direction / 整屏高度链都在 styles/layout.css 的 .fill-page,这里只留卡片间距。 */
.view {
  gap: var(--gap-card);
}

/* 编码「创建后不可修改」提示,挂在表单项的 feedback 位 */
.form-hint {
  font-size: 13px;
  color: var(--text-3);
}

/* 复制弹窗说明文字 */
.copy-tip {
  margin: 0 0 14px;
  font-size: 14px;
  line-height: 1.6;
  color: var(--text-2);
}
</style>
