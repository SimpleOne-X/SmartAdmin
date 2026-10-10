# 变体三：侧栏筛选（User 模式）

适用于表格需要额外分类筛选维度的页面（用户按机构筛选、订单按客户筛选）。

## 与 flat CRUD 的核心差异

| 方面 | flat CRUD | 侧栏筛选 |
|---|---|---|
| 布局 | 单表 | 左侧树/列表 + 右侧 SmartTable |
| 表格参数 | 固定 | `computed` 动态参数，响应侧栏选中变化 |
| 关联下拉 | 无 | `onMounted` 预加载多个下拉选项 |
| 跨页导航 | 无 | watch `route.query` 响应外部跳入参数 |
| 新增/编辑 | 单表单 | 表单字段多，可能需要 NGrid 多列布局 |

## 关键代码模式

```typescript
// 左侧机构树
const orgTree = ref<Tree<SysOrg>[]>([])
const selectedOrgId = ref<number | null>(null)
const tableParams = computed(() =>
  selectedOrgId.value == null ? {} : { orgId: selectedOrgId.value },
)

// 预加载关联下拉
onMounted(async () => {
  try {
    const { items } = await positionApi.page({ page: 1, pageSize: 200 })
    positionOptions.value = items.map((p) => ({ label: p.name, value: p.id }))
  } catch { /* 静默：配角下拉失败不打断列表 */ }

  try {
    orgTree.value = buildTree(await orgApi.list())
  } catch { /* 静默 */ }
})

// 跨页导航：角色页跳来 ?roleId=123。条件构造器下搜索值在过滤态里，不在 inst.params，
// 所以用 presetEqualFilter 预置一个「等于」条件(内部走 setFilter,远程模式会自己回第 1 页重查)
const route = useRoute()
watch(
  [tableRef, () => route.query.roleId],
  ([inst, roleId]) => {
    if (!inst) return
    presetEqualFilter(inst, 'roleId', roleId == null ? undefined : Number(roleId))
  },
  { immediate: true },
)
```

## Template 骨架

```vue
<!-- 「左分组栏 + 右列表」= styles/layout.css 的形状 3:.side-page 负责 display / gap / 拉伸 / 整屏高度,
     直接子元素的 SmartTable 自动吃满剩余宽高,页面只定侧栏宽度。 -->
<div class="sidebar-layout side-page">
  <!-- 左侧筛选树:用 n-card 时加 .fill-card 让树吃满卡片剩余高度、自己竖向滚 -->
  <n-card class="sidebar fill-card" :bordered="false" size="small">
    <n-tree
      :data="orgTree"
      :selected-keys="selectedOrgId == null ? [] : [selectedOrgId]"
      key-field="id"
      label-field="name"
      @update:selected-keys="onOrgSelect"
    />
  </n-card>

  <!-- 右侧表格:是 .side-page 的直接子元素,不要再包一层 div -->
  <SmartTable
    ref="tableRef"
    :title="tableTitle"
    :toolbar="TABLE_TOOLBAR"
    :search="{ container: 'table' }"
    :columns="columns"
    :fetcher="userApi.page"
    :params="tableParams"
    fill-height
    :checked-row-keys="checkedKeys"
    @update:checked-row-keys="(keys: (string | number)[]) => (checkedKeys = keys)"
  >
    <template #toolbar-right>
      <n-button type="primary" @click="openAdd">新增</n-button>
    </template>
    <template #batch>
      <n-button type="error" @click="batchDelete">批量删除</n-button>
    </template>
  </SmartTable>
</div>

<style scoped>
/* 左树 + 右表:display / gap / 拉伸 / 整屏高度都在 .side-page,这里只定侧栏宽度。 */
.sidebar {
  flex: 0 0 200px;
}
.sidebar :deep(.n-tree) {
  flex: 1;
  min-height: 0;
  overflow: auto;
}
</style>
```

不想用 `n-card` 当侧栏时，`styles/layout.css` 还带一套侧栏面板外观（macOS 侧边栏的样子）：外层 `.side-filter`（宽度按页覆盖 `--side-filter-width`；加 `.is-hidden` 向左收起，页面同时给这个 `aside` 写 `inert` 让它退出 Tab 顺序与读屏）、头部 `.side-filter__head`（左边 `.side-filter__title` 是分组名，右边 `.side-filter__actions` 里放 `.side-filter__btn` 图标按钮，如「展开全部 ↔ 折叠到一级」`.side-filter__toggle`、「向左收起」`.side-filter__hide`）、滚动区 `.side-filter__body`、平铺分类的行 `.side-row`（选中态 `.is-active`，可带 `.side-row__count` 角标）、树形分类给 `<n-tree>` 加 `.side-tree`（不开 `show-line`，用 `render-switcher-icon` 换成小号 chevron，层级靠缩进）。

几条约定：

- 分组名是灰色小字，说的是「这一组是什么」，和紧挨着的「全部」行不重复；放进抽屉时抽屉标题栏已有分组名，头部就不再渲染它，也不放「向左收起」。
- 树形分类进入页面只展开一级：数据到位时用 `rootExpandableIds(tree)` 播种受控的 `expanded-keys`，只播这一次，别在数据变化时重播（会覆盖用户手动展开的状态）。没有比第一层更深的层级时，不出展开切换按钮。
- 分类不多时不放搜索框（用户页机构数超过 15 才出现）。
- 向左收起后，在 `SmartTable` 的 `#toolbar` 插槽最左放一个显示当前筛选名的按钮请回面板，否则收起期间看不出筛选还在生效；状态用 `useStorage` 记在本浏览器里。

## 注意事项

- `tableParams` 是 computed，侧栏选中变化时 SmartTable 自动回第 1 页重查；侧栏联动参数走 `:params`，条件构造器的值在过滤态里，两者互不覆盖（导出要「带当前筛选」时，`flatSearchOf(inst)` 取条件构造器那部分，`orgId` 这类联动参数自己从 `tableParams` 取）
- 预加载下拉用 `try/catch` 静默失败——配角数据拉不到不应阻断主列表
- 跨页导航需 `watch` 而非 `onMounted`：页面被 keep-alive 时 onMounted 不再触发，只有 query 变化触发 watch
- 同时 watch `tableRef`：首次加载时表格实例可能还没挂载，需要等实例就绪后再预置筛选；`presetEqualFilter` 值没变返回 false 且不写，实例就绪与 query 变化各触发一次也不会重复请求
- 新增/编辑的 Input 类型可能不同（`AddUserInput` vs `UpdateUserInput`），根据业务需要决定是否拆分

**参考源码：** `web/packages/admin/src/views/system/user/index.vue`
