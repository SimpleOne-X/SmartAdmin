# 变体一：树表（Org 模式）

适用于有父子层级的数据（机构、分类、菜单）。

## 与 flat CRUD 的核心差异

| 方面 | flat CRUD | 树表 |
|---|---|---|
| 数据获取 | `fetcher` prop（SmartTable 内部管分页，后端过滤） | 手动 `load()` + `buildTree()` 得到整棵树，`:fetcher` 传一个**本地取数器**从内存里的树取数 |
| 分页 | 有 | `:pagination="false"` |
| 搜索 | 列级 `search: { actions }` + 条件构造器，后端过滤 | 同样用条件构造器收集条件，取数器里用 `filterTree()` 过滤内存里的树 |
| 展开 | 不适用 | 受控 `expanded-row-keys` + 全展/全收按钮 |
| 新增 | 一种 `openAdd()` | `openAdd(parentId)` — 可新增子节点 |
| 刷新 | `tableRef.value?.refresh()` | 调 `load()` 重新拉取 |

## 关键代码模式

```typescript
import type { SmartTableFetcher } from 'smart-naive-table'
import { buildTree, expandableIds, filterTree, type Tree } from '#/utils/tree' // 业务模块:from 'smart-admin-web'

const tree = ref<Tree<SysOrg>[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    tree.value = buildTree(await orgApi.list())  // list 接口返回平铺数组
  } finally {
    loading.value = false
  }
}
onMounted(load)

// 搜索：本地取数器。条件构造器的条件经全局 flatFilterSerializer 摊平成扁平键(name / code),
// 再用 filterTree 过滤整棵树：命中的节点保留整棵子树，未命中但有后代命中的作为祖先链保留。
// 不能用 3.0 的静态 :data 过滤：它是平铺的 rows.filter(...)，不递归 children，在树上只会命中顶层行。
const treeFetcher: SmartTableFetcher<Tree<SysOrg>> = async (params) => {
  const name = String(params.name ?? '').trim().toLowerCase()
  const code = String(params.code ?? '').trim().toLowerCase()
  const items = filterTree(
    tree.value,
    (n) => (!name || n.name.toLowerCase().includes(name)) && (!code || n.code.toLowerCase().includes(code)),
  )
  return { items, total: items.length }
}

// 展开控制：取数器返回的（过滤后的）行经 @loaded 拿到，数据变化时重算展开节点
const visibleTree = ref<Tree<SysOrg>[]>([])
const expandedKeys = ref<number[]>([])
watch(visibleTree, (t) => (expandedKeys.value = expandableIds(t)), { immediate: true })
```

树加载、增删改之后要让表格重新取数，`load()` 末尾补一句 `tableRef.value?.refresh()`；搜索列写 `search: { actions: SEARCH_ACTIONS.fuzzy }`。

## Template 骨架

```vue
<!-- 顶层不是裸 SmartTable(外面还有统计条 / 说明卡)时,外壳加 .fill-page 接上整屏高度链 -->
<div class="view fill-page">
  <SmartTable
    ref="tableRef"
    :title="tableTitle"
    :toolbar="TABLE_TOOLBAR"
    :search="{ container: 'table' }"
    :columns="columns"
    :fetcher="treeFetcher"
    :loading="loading"
    row-key="id"
    :pagination="false"
    fill-height
    :expanded-row-keys="expandedKeys"
    @loaded="(rows: Tree<SysOrg>[]) => (visibleTree = rows)"
    @update:expanded-row-keys="(keys: number[]) => (expandedKeys = keys)"
  >
    <!-- 业务按钮写 #toolbar-right（工具栏右半），不写 #toolbar（左半，会挤到搜索框左边） -->
    <template #toolbar-right>
      <n-button quaternary @click="toggleExpandAll">
        {{ allExpanded ? '全部收起' : '全部展开' }}
      </n-button>
      <n-button type="primary" @click="openAdd(0)">新增</n-button>
    </template>
  </SmartTable>
</div>

<style scoped>
/* display / flex-direction / 整屏高度链都在 styles/layout.css 的 .fill-page,这里只留卡片间距。 */
.view {
  gap: var(--gap-card);
}
</style>
```

## 注意事项

- 模板顶层直接就是 `<SmartTable>` 时连 `.fill-page` 都不用加，自动满屏；只有外面还包了别的块才需要外壳
- 树表没有后端过滤，条件是前端对已加载的整棵树求值，不是服务端分页
- `list` 接口返回全量平铺数据（非分页），后端用 `GET list` 而非 `GET page`
- 后端删除需检查 `HasChildren`（有子节点不允许删除）
- 表单中的"上级节点"用 `OrgTreeSelect` 组件，需传 `:exclude-subtree-of="editingId"` 防止选自己/子孙为父（成环）
- NDropdown 适合收纳多个操作（编辑/新增子节点/删除），避免操作列过宽

**参考源码：** `web/packages/admin/src/views/system/org/index.vue`
