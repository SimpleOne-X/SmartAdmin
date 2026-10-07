<script setup lang="ts">
// 字典管理 = 主从:上=类型 SmartTable(CRUD),点击行选中 → 下=该类型的字典项(同样是 SmartTable,静态数据模式 + CRUD)。
// 关键约束:下方走管理端 dict/item/page(含停用项、带 id);下拉用的 dict/items/{code} 只回启用项且丢 id,不能复用。
// 任何类型/项的增删改后调 useDictStore().invalidate(code) 失效下拉缓存,变更即时生效。
import { computed, h, reactive, ref } from 'vue'
import {
  NButton,
  NCard,
  NDrawer,
  NDrawerContent,
  NSpace,
  NInput,
  NInputNumber,
  NPopconfirm,
  NForm,
  NFormItem,
  NEmpty,
  NSwitch,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useElementSize, useMediaQuery } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useBatchDelete } from '#/composables/useBatchDelete'
import { dictAdminApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { useDictStore } from '#/stores/dict'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import type { DictItemInput, DictTypeInput, SysDictItem, SysDictType } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()
const dictStore = useDictStore()
const typeTableRef = ref<SmartTableInst<SysDictType>>()

// 本页不开「放大」:上下两栏各自定高、表体内部滚动,放大层会把主从关系拆散。
const TYPE_TOOLBAR = { ...TABLE_TOOLBAR, maximize: false }

// ── 窄档:类型表换成卡片列表,字典项不内联,点卡片从底部抽屉看 ──
// 阈值与 smart-naive-table 的 card-on-narrow 一致(根节点宽 < 600);页面外壳宽 = 类型表根节点宽。
// 首帧量到 0 不算窄档,免得宽屏刷新时闪一下卡片。
const layoutRef = ref<HTMLElement>()
const { width: layoutWidth } = useElementSize(layoutRef)
const narrow = computed(() => layoutWidth.value > 0 && layoutWidth.value < 600)
const itemSheet = ref(false)

// 表单:窄屏标签放上方(底部抽屉里横排标签太挤),宽 / 中档沿用左标签
const narrowForm = useMediaQuery('(max-width: 640px)')
const formLayout = computed(() =>
  narrowForm.value
    ? { labelPlacement: 'top' as const, labelWidth: undefined }
    : { labelPlacement: 'left' as const, labelWidth: 80 },
)

// ── 主从选中态 + 下方字典项 ──
const selectedType = ref<SysDictType | null>(null)
const items = ref<SysDictItem[]>([])
const itemsLoading = ref(false)

async function loadItems() {
  const code = selectedType.value?.code
  if (!code) {
    items.value = []
    return
  }
  itemsLoading.value = true
  try {
    const list = await dictAdminApi.items(code)
    // 竞态守卫:await 期间用户可能已切换类型,过期响应不得覆盖当前选中项
    if (selectedType.value?.code === code) items.value = list
  } catch (e) {
    if (selectedType.value?.code === code) {
      message.error(translateError(e))
      items.value = []
    }
  } finally {
    if (selectedType.value?.code === code) itemsLoading.value = false
  }
}
async function selectType(r: SysDictType) {
  selectedType.value = r
  await loadItems()
}
/** 点类型行 / 卡片:窄档同时拉起底部抽屉(此时下栏卡片不渲染)。 */
function onTypeClick(r: SysDictType) {
  if (narrow.value) itemSheet.value = true
  return selectType(r)
}

// 批量删除:类型批删后清空选中类型 + 全量失效下拉缓存;项批删后仅重载下栏 + 失效缓存。
const { checkedKeys: typeCheckedKeys, run: typeBatchDelete } = useBatchDelete({
  remove: dictAdminApi.typeBatchRemove,
  refresh: () => {
    selectedType.value = null
    items.value = []
    dictStore.invalidate()
    typeTableRef.value?.refresh()
  },
  successMsg: t('dict.typeDeleted'),
})
const {
  checkedKeys: itemCheckedKeys,
  hasSelection: itemHasSelection,
  run: itemBatchDelete,
} = useBatchDelete({
  remove: dictAdminApi.itemBatchRemove,
  refresh: () => {
    dictStore.invalidate()
    loadItems()
  },
  successMsg: t('dict.itemDeleted'),
})

// ── 上:字典类型 SmartTable ──
// 窄栏同样用条件构造器(container: 'table'):容器宽小于 600 时 3.0 自动收成「输入框 + 筛选」,按名称过滤走列声明式 search。
const typeToInput = (r: SysDictType): DictTypeInput => ({
  code: r.code,
  name: r.name,
  sort: r.sort,
  enabled: r.enabled,
  remark: r.remark ?? '',
})
const typeColumns: SmartTableColumn<SysDictType>[] = [
  { type: 'selection' },
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    key: 'code',
    title: () => t('dict.code'),
    render: r => h('span', { class: 'mono muted' }, r.code),
  },
  // 窄档卡片以类型名称作标题(默认取第一个数据列 = 编码,不如名称好认)
  {
    key: 'name',
    title: () => t('dict.name'),
    card: 'title',
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    key: 'sort',
    title: () => t('dict.sort'),
    width: 70,
    render: r => h('span', { class: 'num muted' }, r.sort),
  },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 90,
    // 包一层 stopPropagation:开关点击不得冒泡到行 onClick(否则误切选中类型)
    render: r =>
      h('div', { onClick: (e: Event) => e.stopPropagation() }, [
        h(StatusSwitch, {
          value: r.enabled,
          request: (next: boolean) =>
            dictAdminApi.typeUpdate(r.id, { ...typeToInput(r), enabled: next }),
          'onUpdate:value': (v: boolean) => {
            r.enabled = v
            dictStore.invalidate(r.code)
          },
        }),
      ]),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 130,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    // stopPropagation:操作按钮点击不得冒泡到行 onClick(否则误切选中类型)
    render: r =>
      h(
        NSpace,
        { size: 2, justify: 'end', wrapItem: false, onClick: (e: Event) => e.stopPropagation() },
        () => [
          authStore.hasPerm('PUT:/api/v1/sys/dict/type/{id}')
            ? h(
                NButton,
                {
                  size: 'small',
                  quaternary: true,
                  type: 'primary',
                  onClick: () => openTypeEdit(r),
                },
                () => t('common.edit'),
              )
            : null,
          authStore.hasPerm('DELETE:/api/v1/sys/dict/type/{id}')
            ? h(
                NPopconfirm,
                {
                  onPositiveClick: () =>
                    run(() => dictAdminApi.typeRemove(r.id), t('dict.typeDeleted')).then(ok => {
                      if (!ok) return
                      dictStore.invalidate(r.code)
                      if (selectedType.value?.id === r.id) {
                        selectedType.value = null
                        items.value = []
                      }
                      typeTableRef.value?.refresh()
                    }),
                },
                {
                  trigger: () =>
                    h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                      t('common.delete'),
                    ),
                  default: () => t('dict.typeDeleteConfirm', { name: r.name }),
                },
              )
            : null,
        ],
      ),
  },
]
deriveHeaderFilters(typeColumns)

// ── 类型 新增/编辑弹窗 ──
const typeShow = ref(false)
const typeFormRef = ref<FormInst | null>(null)
const typeEditingId = ref<number | null>(null)
const typeRules: FormRules = {
  code: {
    required: true,
    whitespace: true,
    message: () => t('dict.codeRequired'),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('dict.nameRequired'),
    trigger: ['input', 'blur'],
  },
}
const blankType = (): DictTypeInput => ({ code: '', name: '', sort: 0, enabled: true, remark: '' })
const typeForm = reactive<DictTypeInput>(blankType())

function openTypeAdd() {
  typeEditingId.value = null
  Object.assign(typeForm, blankType())
  typeShow.value = true
}
function openTypeEdit(r: SysDictType) {
  typeEditingId.value = r.id
  Object.assign(typeForm, typeToInput(r))
  typeShow.value = true
}
async function saveType() {
  await typeFormRef.value?.validate()
  try {
    if (typeEditingId.value === null) await dictAdminApi.typeAdd({ ...typeForm })
    else await dictAdminApi.typeUpdate(typeEditingId.value, { ...typeForm })
    dictStore.invalidate(typeForm.code)
    message.success(t('dict.typeSaved'))
    await typeTableRef.value?.refresh()
    // 若编辑的是当前选中类型,同步其名称/编码到下栏标题与后续项提交
    if (selectedType.value?.id === typeEditingId.value)
      Object.assign(selectedType.value, { name: typeForm.name })
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}

// ── 下:字典项 表格 ──
const itemToInput = (r: SysDictItem): DictItemInput => ({
  dictTypeCode: r.dictTypeCode,
  label: r.label,
  value: r.value,
  sort: r.sort,
  enabled: r.enabled,
})
/** 字典项启停:悲观更新(StatusSwitch 请求成功才 emit);表格行与窄档抽屉卡片共用。 */
const itemEnabledRequest = (r: SysDictItem) => (next: boolean) =>
  dictAdminApi.itemUpdate(r.id, { ...itemToInput(r), enabled: next })
function onItemEnabled(r: SysDictItem, v: boolean) {
  r.enabled = v
  dictStore.invalidate(r.dictTypeCode)
}
/** 字典项删除成功后失效缓存并重载下栏;表格行与窄档抽屉卡片共用。 */
const removeItem = (r: SysDictItem) =>
  run(() => dictAdminApi.itemRemove(r.id), t('dict.itemDeleted')).then(ok => {
    if (!ok) return
    dictStore.invalidate(r.dictTypeCode)
    loadItems()
  })
// 字典项是静态数据(整类一次取回),条件搜索由 SmartTable 前端求值
const itemColumns: SmartTableColumn<SysDictItem>[] = [
  { type: 'selection' },
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  { title: () => t('dict.itemLabel'), key: 'label', card: 'title', search: {} },
  {
    title: () => t('dict.itemValue'),
    key: 'value',
    search: {},
    render: r => h('span', { class: 'mono muted' }, r.value),
  },
  {
    title: () => t('dict.sort'),
    key: 'sort',
    width: 70,
    render: r => h('span', { class: 'num muted' }, r.sort),
  },
  {
    title: () => t('common.status'),
    key: 'enabled',
    width: 90,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        request: itemEnabledRequest(r),
        'onUpdate:value': (v: boolean) => onItemEnabled(r, v),
      }),
  },
  {
    title: () => t('common.operation'),
    key: 'op',
    width: 130,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 2, justify: 'end', wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/dict/item/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openItemEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('DELETE:/api/v1/sys/dict/item/{id}')
          ? h(
              NPopconfirm,
              { onPositiveClick: () => removeItem(r) },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('dict.itemDeleteConfirm', { label: r.label }),
              },
            )
          : null,
      ]),
  },
]
deriveHeaderFilters(itemColumns)

// ── 字典项 新增/编辑弹窗 ──
const itemShow = ref(false)
const itemFormRef = ref<FormInst | null>(null)
const itemEditingId = ref<number | null>(null)
const itemRules: FormRules = {
  label: {
    required: true,
    whitespace: true,
    message: () => t('dict.itemLabelRequired'),
    trigger: ['input', 'blur'],
  },
  value: {
    required: true,
    whitespace: true,
    message: () => t('dict.itemValueRequired'),
    trigger: ['input', 'blur'],
  },
}
const blankItem = (): DictItemInput => ({
  dictTypeCode: '',
  label: '',
  value: '',
  sort: 0,
  enabled: true,
})
const itemForm = reactive<DictItemInput>(blankItem())

function openItemAdd() {
  if (!selectedType.value) return
  itemEditingId.value = null
  Object.assign(itemForm, blankItem(), { dictTypeCode: selectedType.value.code })
  itemShow.value = true
}
function openItemEdit(r: SysDictItem) {
  itemEditingId.value = r.id
  Object.assign(itemForm, itemToInput(r))
  itemShow.value = true
}
async function saveItem() {
  await itemFormRef.value?.validate()
  try {
    if (itemEditingId.value === null) await dictAdminApi.itemAdd({ ...itemForm })
    else await dictAdminApi.itemUpdate(itemEditingId.value, { ...itemForm })
    dictStore.invalidate(itemForm.dictTypeCode)
    message.success(t('dict.itemSaved'))
    await loadItems()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>
<template>
  <!-- 上下两栏、各吃一半 = styles/layout.css 的形状 5(.fill-split)套在整页外壳 .fill-page 里:
       字典类型在上、字典项在下,宽度都是整行,类型表的条件搜索因此能排成单行三段式。
       上栏的 SmartTable 自己就是一张卡片,直接作为 .fill-split 的子元素均分高度,外面不再套卡片(否则卡中卡);
       下栏的字典项表同样是 SmartTable(静态数据模式),也直接作为 .fill-split 的子元素均分;
       没选中类型时下栏是一张带提示的空卡片(挂 .fill-main 参与均分)。
       .fill-page--soft:矮屏让页面滚动,不把两张表压成一条缝。
       窄档(外壳宽 < 600):类型表由库换成卡片列表(card-on-narrow),下栏卡片不渲染,
       点卡片 = @row-click,从底部抽屉看字典项(抽屉里也是同一套列的 SmartTable 卡片列表,底部固定「批量删除 / 新增字典项」)。
       工具栏里不放表名:选中的是哪个类型由类型表的行高亮表达,字典项表上方也不再写类型名。
       (注释里别写带尖括号的标签名:listSearch.spec.ts 的正则会把它当成一张表而误判。) -->
  <div ref="layoutRef" class="dict-layout fill-page fill-page--soft fill-split">
    <SmartTable
      :toolbar="TYPE_TOOLBAR"
      ref="typeTableRef"
      :columns="typeColumns"
      :fetcher="dictAdminApi.typePage"
      fill-height
      :min-row-height="TABLE_MIN_ROW_HEIGHT"
      card-on-narrow
      :search="{ container: 'table' }"
      storage-key="sys-dict-type"
      :active-row-key="selectedType?.id ?? null"
      :row-props="() => ({ style: 'cursor: pointer' })"
      :checked-row-keys="typeCheckedKeys"
      @row-click="(row: SysDictType) => onTypeClick(row)"
      @update:checked-row-keys="(keys: (string | number)[]) => (typeCheckedKeys = keys)"
      @error="e => message.error(translateError(e))"
    >
      <template #pagination-prefix="{ itemCount }">
        <TableTotal :count="itemCount" />
      </template>
      <template #toolbar-right>
        <n-button v-auth="'POST:/api/v1/sys/dict/type'" type="primary" @click="openTypeAdd">
          <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
          {{ t('common.add') }}
        </n-button>
      </template>
      <!-- 勾选后工具栏换成批量栏(本页全选 + 已选 N 项 + 这里的按钮 + 取消选择),按钮出现即代表有勾选 -->
      <template #batch>
        <n-button
          v-auth="'POST:/api/v1/sys/dict/type/batch-delete'"
          secondary
          type="error"
          @click="typeBatchDelete"
        >
          <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
          {{ t('common.batchDelete') }}
        </n-button>
      </template>
    </SmartTable>

    <SmartTable
      v-if="!narrow && selectedType"
      :columns="itemColumns"
      :data="items"
      row-key="id"
      :loading="itemsLoading"
      :toolbar="TYPE_TOOLBAR"
      :search="{ container: 'table' }"
      :checked-row-keys="itemCheckedKeys"
      fill-height
      :min-row-height="TABLE_MIN_ROW_HEIGHT"
      storage-key="sys-dict-item"
      @update:checked-row-keys="(keys: (string | number)[]) => (itemCheckedKeys = keys)"
    >
      <template #pagination-prefix="{ itemCount }">
        <TableTotal :count="itemCount" />
      </template>
      <template #toolbar-right>
        <n-button v-auth="'POST:/api/v1/sys/dict/item'" type="primary" @click="openItemAdd">
          <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
          {{ t('dict.addItem') }}
        </n-button>
      </template>
      <template #batch>
        <n-button
          v-auth="'POST:/api/v1/sys/dict/item/batch-delete'"
          secondary
          type="error"
          @click="itemBatchDelete"
        >
          <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
          {{ t('common.batchDelete') }}
        </n-button>
      </template>
    </SmartTable>
    <n-card v-else-if="!narrow" class="dict-pane fill-main" :bordered="true">
      <n-empty :description="t('dict.selectTypeHint')" style="padding: 48px 0; margin: auto" />
    </n-card>
  </div>

  <!-- 窄档:字典项底部抽屉。卡片里的开关 / 勾选 / 编辑 / 删除与宽档表格行是同一套请求与权限 -->
  <n-drawer
    :show="itemSheet && narrow && !!selectedType"
    placement="bottom"
    height="85vh"
    style="border-radius: 16px 16px 0 0"
    @update:show="(v: boolean) => (itemSheet = v)"
  >
    <n-drawer-content
      :title="
        selectedType ? t('dict.itemsOf', { name: selectedType.name }) : t('dict.itemsSheetTitle')
      "
      closable
    >
      <SmartTable
        :columns="itemColumns"
        :data="items"
        row-key="id"
        :loading="itemsLoading"
        :toolbar="TYPE_TOOLBAR"
        :search="{ container: 'table' }"
        :checked-row-keys="itemCheckedKeys"
        card-on-narrow
        storage-key="sys-dict-item-sheet"
        @update:checked-row-keys="(keys: (string | number)[]) => (itemCheckedKeys = keys)"
      >
        <template #pagination-prefix="{ itemCount }">
          <TableTotal :count="itemCount" />
        </template>
      </SmartTable>
      <template #footer>
        <div class="sheet-foot">
          <n-button
            v-auth="'POST:/api/v1/sys/dict/item/batch-delete'"
            secondary
            type="error"
            size="large"
            :disabled="!itemHasSelection"
            @click="itemBatchDelete"
          >
            {{ t('common.batchDelete') }}
          </n-button>
          <n-button
            v-auth="'POST:/api/v1/sys/dict/item'"
            type="primary"
            size="large"
            @click="openItemAdd"
          >
            {{ t('dict.addItem') }}
          </n-button>
        </div>
      </template>
    </n-drawer-content>
  </n-drawer>

  <FormContainer
    v-model:show="typeShow"
    :title="typeEditingId === null ? t('dict.addTypeTitle') : t('dict.editTypeTitle')"
    :width="480"
    :on-confirm="saveType"
    :confirm-text="t('common.save')"
  >
    <n-form
      ref="typeFormRef"
      :model="typeForm"
      :rules="typeRules"
      require-mark-placement="left"
      v-bind="formLayout"
    >
      <n-form-item :label="t('dict.code')" path="code">
        <n-input
          v-model:value="typeForm.code"
          class="mono"
          :placeholder="t('dict.code')"
          :disabled="typeEditingId !== null"
        />
        <!-- 反馈槽只在编辑时有内容;新增时槽为空,校验提示照常显示 -->
        <template #feedback>
          <span v-if="typeEditingId !== null" class="field-hint">
            {{ t('dict.codeImmutable') }}
          </span>
        </template>
      </n-form-item>
      <n-form-item :label="t('dict.name')" path="name">
        <n-input v-model:value="typeForm.name" :placeholder="t('dict.name')" />
      </n-form-item>
      <n-form-item :label="t('dict.sort')">
        <n-input-number v-model:value="typeForm.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('dict.remark')">
        <n-input
          v-model:value="typeForm.remark as string"
          type="textarea"
          :autosize="{ minRows: 2 }"
        />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="typeForm.enabled" />
      </n-form-item>
    </n-form>
  </FormContainer>

  <FormContainer
    v-model:show="itemShow"
    :title="itemEditingId === null ? t('dict.addItemTitle') : t('dict.editItemTitle')"
    :width="480"
    :on-confirm="saveItem"
    :confirm-text="t('common.save')"
  >
    <n-form
      ref="itemFormRef"
      :model="itemForm"
      :rules="itemRules"
      require-mark-placement="left"
      v-bind="formLayout"
    >
      <n-form-item :label="t('dict.itemLabel')" path="label">
        <n-input v-model:value="itemForm.label" :placeholder="t('dict.itemLabel')" />
      </n-form-item>
      <n-form-item :label="t('dict.itemValue')" path="value">
        <n-input v-model:value="itemForm.value" class="mono" :placeholder="t('dict.itemValue')" />
      </n-form-item>
      <n-form-item :label="t('dict.sort')">
        <n-input-number v-model:value="itemForm.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="itemForm.enabled" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
/* 上下均分、整屏高度都在 styles/layout.css(.fill-page / .fill-split),这里只给每栏一个最小高度:
   矮屏时整页滚动,而不是把表压扁。 */
.dict-layout > :deep(.smart-table),
.dict-pane {
  min-width: 0;
  min-height: 280px;
}

/* 单元格里的辅助字样:编码 / 值用等宽,序号 / 排序用等宽数字。
   表格单元格由库与 naive 渲染,要 :deep 才够得着;抽屉卡片是自己的元素,直接命中。 */
.mono,
:deep(.mono) {
  font-family: var(--font-mono);
  font-size: 13.5px;
  letter-spacing: 0;
}
.muted,
:deep(.muted) {
  color: var(--text-2);
}
.faint,
:deep(.faint) {
  color: var(--text-3);
}
.num,
:deep(.num) {
  font-variant-numeric: tabular-nums;
}

.sheet-foot {
  display: flex;
  gap: 10px;
  width: 100%;
}
.sheet-foot .n-button {
  flex: 1;
}

/* 表单里的字段提示(编码创建后不可改) */
.field-hint {
  font-size: 13px;
  color: var(--text-3);
}
</style>
