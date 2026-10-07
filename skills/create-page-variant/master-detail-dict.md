# 变体二：主从/上下分栏（Dict 模式）

适用于父子关系紧密的数据对（字典类型+字典项、分类+子项）。

## 与 flat CRUD 的核心差异

| 方面 | flat CRUD | 主从分栏 |
|---|---|---|
| 布局 | 单表 | 上下两栏（`.fill-split` 均分，主表在上、从表在下） |
| 数据关系 | 独立 | 上方主表选中 → 下方从表按选中加载 |
| 表格 | 一个 SmartTable | 上 SmartTable + 下 n-data-table（下表是裸表，没有批量栏、条件搜索） |
| 批量删除 | 一组 useBatchDelete | 两组（主/从各一组） |
| CRUD 状态 | 一套 show/form/editingId | 两套（主/从各一套） |
| 缓存 | 无 | 改动后需 `dictStore.invalidate()` 刷缓存 |

## 关键代码模式

```typescript
// 主从选中态
const selectedType = ref<SysDictType | null>(null)
const items = ref<SysDictItem[]>([])

async function loadItems() {
  const code = selectedType.value?.code
  if (!code) { items.value = []; return }
  itemsLoading.value = true
  try {
    const list = await dictAdminApi.items(code)
    // 竞态守卫：await 期间用户可能已切换，过期响应不覆盖
    if (selectedType.value?.code === code) items.value = list
  } finally {
    if (selectedType.value?.code === code) itemsLoading.value = false
  }
}
async function selectType(r: SysDictType) {
  selectedType.value = r
  await loadItems()
}

// 两组独立的批量删除
// 上表是 SmartTable：批量按钮写 #batch，勾选后才出现，不需要 hasSelection
const { checkedKeys: typeCheckedKeys, run: typeBatchDelete } = useBatchDelete({
  remove: dictAdminApi.typeBatchRemove,
  refresh: () => { selectedType.value = null; items.value = []; typeTableRef.value?.refresh() },
})
// 下表是裸 n-data-table，没有批量栏：用 hasSelection 控制批量按钮禁用
const { checkedKeys: itemCheckedKeys, hasSelection: itemHasSelection, run: itemBatchDelete } = useBatchDelete({
  remove: dictAdminApi.itemBatchRemove,
  refresh: () => loadItems(),
})
```

## Template 骨架

```vue
<!-- 外壳 .fill-page 接整屏高度,.fill-split 把高度均分给两栏(竖直排列;要左右并排就再加 .fill-split--row);
     .fill-page--soft 让矮屏整页滚动而不是压扁。两栏是 n-card 时卡片加 .fill-card + .fill-main、
     内容层 content-class="fill-main",SmartTable 才能拿到基准高度。
     .fill-main 别写在 <SmartTable> 上(inheritAttrs:false,class 到不了根节点)。 -->
<div class="fill-page fill-page--soft fill-split">
  <!-- 上：主表 -->
  <n-card class="pane fill-card fill-main" content-class="fill-main">
    <!-- 同页两张表：上表标题用自己的 i18n 文案（不是菜单标题） -->
    <SmartTable
      :title="t('dict.typeTitle')"
      :toolbar="TABLE_TOOLBAR"
      :columns="typeColumns"
      :fetcher="api.typePage"
      fill-height
      :search="{ container: 'table' }"
      :active-row-key="selectedType?.id ?? null"
      :row-props="() => ({ style: 'cursor: pointer' })"
      :checked-row-keys="typeCheckedKeys"
      @update:checked-row-keys="(keys: (string | number)[]) => (typeCheckedKeys = keys)"
      @row-click="(row) => selectType(row)"
    >
      <template #toolbar-right><!-- 新增按钮 --></template>
      <template #batch><!-- 批量删除按钮,@click="typeBatchDelete" --></template>
    </SmartTable>
  </n-card>

  <!-- 下：从表（选中后显示）-->
  <n-card v-if="selectedType" class="pane fill-card fill-main" :title="`${selectedType.name} 的子项`">
    <n-data-table :columns="itemColumns" :data="items" :loading="itemsLoading" flex-height virtual-scroll />
  </n-card>
  <n-card v-else class="pane fill-card fill-main">
    <n-empty description="请先选择上方项" style="margin: auto" />
  </n-card>
</div>

<style scoped>
/* 上下均分、整屏高度都在 styles/layout.css,这里只给每栏一个最小高度,矮屏时整页滚动。 */
.pane {
  min-width: 0;
  min-height: 280px;
}
</style>
```

## 注意事项

- 上方主表同样写 `:search="{ container: 'table' }"`：上下排列时主表是整行宽，条件搜索能排成单行三段式；若改回左右并排，窄面板里容器宽小于 600 时 3.0 会自动收成「输入框 + 筛选」，不用 `layout: 'inline'`
- `:active-row-key` 高亮选中行，点击行触发 `@row-click`
- smart-naive-table 里点行内的按钮 / 勾选框 / 链接 / 输入框不会触发 `@row-click`；SmartTable 的行内控件无需写 `stopPropagation`（写了也无害）。裸 `n-data-table` 的行点击没有这层保护，行内控件仍要 `stopPropagation`
- 竞态守卫：`loadItems` 中 await 回来后要验证选中状态没变
- 如果主从数据影响全局缓存（如字典），每次写操作后调 `invalidate()`

**参考源码：** `web/packages/admin/src/views/system/dict/index.vue`
