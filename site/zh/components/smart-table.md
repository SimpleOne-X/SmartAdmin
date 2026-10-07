# SmartTable

SmartAdmin 前端几乎每一张列表页都是同一张表：`smart-naive-table`（独立 npm 包，`^3.0.0`）。它的模型只有两件东西：一个 `columns` 数组同时驱动条件搜索、字典单元格和列设置面板，一个 `fetcher` 函数把任意后端接回来。这页只讲一件事：在 SmartAdmin 里怎么把它用对。逐个 prop 的完整清单在包的 README，这里不重复。

## 一张列表页最少要写什么

拿内置的岗位管理（`web/packages/admin/src/views/system/position/index.vue`）当骨架，删到只剩表格，写在应用里是这样：

```vue
<script setup lang="ts">
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import {
  SEARCH_ACTIONS, TABLE_TOOLBAR, positionApi, translateError, useTableTitle,
  type SysPosition,
} from 'smart-admin-web'

const tableTitle = useTableTitle()
const columns: SmartTableColumn<SysPosition>[] = [
  { key: 'name', title: () => t('position.name'), search: { actions: SEARCH_ACTIONS.fuzzy } },
  { key: 'code', title: () => t('position.code') },
  { key: 'createTime', title: () => t('common.createTime'), format: 'datetime' },
]
</script>

<template>
  <SmartTable
    :title="tableTitle"
    :toolbar="TABLE_TOOLBAR"
    :search="{ container: 'table' }"
    :columns="columns"
    :fetcher="positionApi.page"
    fill-height
    storage-key="sys-position"
    @error="(e) => message.error(translateError(e))"
  />
</template>
```

`name` 列写了 `search`，它就成为条件构造器里的一个字段，比较符限定为后端做得到的「包含」。`createTime` 写 `format: 'datetime'`，就按本地时间格式化。`storage-key` 记住这张表列设置存在 localStorage 的哪个键，取名规则见下文。没写的东西不用管，表格不会替你臆造。这段短代码已经把下面要讲的几条约定都用上了，逐个拆开看。

## fetcher 是唯一要你适配的地方

SmartTable 对后端只有一个假设：`fetcher(params) => Promise<{ items, total }>`。SmartAdmin 的后端返回的不是这个形状。它返回 `PagedList<T>`（`{ current, size, total, items }`），翻页参数也叫 `Current`/`Size` 而不是 `page`/`pageSize`。这层差异不该散落在每个页面里，内核把它压在包里的 `api/index.ts` 一处：

```ts
// 前端 {page,pageSize} → 后端 record 属性 {Current,Size}(PascalCase)
const pageParams = (p: { page: number; pageSize: number }) => ({ Current: p.page, Size: p.pageSize })

// 后端 PagedList<T> → SmartTable 契约的 {items,total}
function toPage<T>(res): { items: T[]; total: number } {
  const p = unwrap<PagedList<T>>(res)
  return { items: p.items, total: p.total }
}

export const positionApi = {
  page: (params: { page: number; pageSize: number; name?: string }) =>
    client
      .GET('/api/v1/sys/position/page', {
        // 搜索键 name → 后端 PascalCase 的 Name;类型要求 Pascal,绑定本身大小写不敏感
        params: { query: { ...pageParams(params), Name: params.name } },
      })
      .then((r) => toPage<SysPosition>(r)),
}
```

所以页面里 `:fetcher="positionApi.page"` 直接把 api 层的方法传进去就行，映射已经在那里做完了。给自己的接口加列表页时，在应用的 `src/api/<域>.ts` 里照 `userApi.page`/`positionApi.page` 的形态写一份，改端点和搜索字段名：`client` 用应用的 `./client`，`pageParams`/`toPage`/`unwrap` 从 `smart-admin-web` 导入。`Current`/`Size` 不要在组件里手动拼。

`name` 列的条件会以扁平键 `name` 进 `fetcher`（下文「条件搜索」讲这一步怎么来）。它最终落到后端哪个字段，由 api 层那一步 `Name: params.name` 决定。

## 已经替你接好的三件事

**labels 全局注入，页面不用手传。** SmartTable 的“搜索/重置/刷新/列设置”这些按钮文案要跟 i18n 走，`createSmartAdmin()` 装配时一次性接上，之后每个页面都继承：

```ts
app.provide(
  SMART_TABLE_DEFAULTS,
  createSmartTableDefaults({
    density: options.table?.density ?? 'compact',
    defaultPageSize: 100,
    pageSizes: options.table?.pageSizes, // 不传由 3.0 按 fill-height 选档
    filterSerializer: flatFilterSerializer,
    labels: computed(() => {
      void i18n.global.locale.value // 触发 locale 依赖收集,切语言即时重算
      const t = i18n.global.t
      return { search: t('common.search'), reset: t('common.reset'), /* …列设置/密度等 */ }
    }),
  }),
)
```

页面层因此不用写 `:labels`，只有要覆盖某一页的个别文案时才单独传。默认密度和每页条数选项，应用可以经 `createSmartAdmin({ table: { density, pageSizes } })` 改。每页条数的档位不写时，没开 `fill-height` 是 `100 / 500 / 1000`，开了是 `100 / 1000 / 10000`；上限是后端的 `SmartAdmin:Api:MaxPageSize`（默认 10000），应用把它调小，就要同步传 `pageSizes`。但列标题是个例外，它必须写成函数形式 `title: () => t('...')`。直接写 `title: t('...')`，只在建列那一刻求值，切语言不会更新。

**错误留在视图层。** 包内不弹任何 UI，`fetcher` 抛错会 emit 到 `@error`，由页面决定怎么提示。全站统一写 `@error="(e) => message.error(translateError(e))"`：`translateError` 把后端的数字 `ErrorCode` 映射成当前语言的文案。

**操作按钮按权限显隐。** 授权模型是“权限码即路由”，页面里对每个动作查一次 `authStore.hasPerm('{METHOD}:/{route}')`，无权就不渲染那个按钮：

```ts
// 操作列 render 里,逐个按钮门控
authStore.hasPerm('PUT:/api/v1/sys/position/{id}')
  ? h(NButton, { onClick: () => openEdit(r) }, () => t('common.edit'))
  : null
```

工具栏右半（`#toolbar-right`）的新增按钮和批量栏（`#batch`）里的批量删除按钮同理，用 `v-auth` 指令：`v-auth="'POST:/api/v1/sys/position/add'"`。权限码字符串必须和后端路由模板逐字一致（含 `{id}` 这样的占位段），否则永远命中不到。

`storage-key` 决定列设置存到 localStorage 的哪个键（前缀 `protable:`），命名统一用 `{模块}-{页面}`，如 `sys-position`、`sys-user`。

## 条件搜索：一律用条件构造器

列表页的搜索统一用条件构造器：`:search="{ container: 'table' }"`，一行「字段 + 比较符 + 值」并入表格卡片，容器窄于 600 时自动收成「输入框 + 筛选」。独立搜索卡片和 `layout: 'inline'` 不再用。

比较符只开放后端做得到的，开放做不到的，界面就在说谎。`SEARCH_ACTIONS` 是现成的三组：

```ts
{ key: 'name', search: { actions: SEARCH_ACTIONS.fuzzy } },        // 文本，后端 Contains
{ key: 'status', options, search: { actions: SEARCH_ACTIONS.exact } }, // 枚举与 id，后端 ==
{ key: 'createTime', search: { type: 'daterange', actions: SEARCH_ACTIONS.dayRange } }, // 日期，后端 >= 与 <=
```

条件构造器产出的是 `filters`，不是扁平参数。内核全局注入了 `flatFilterSerializer`，把它摊平回各接口现有的扁平键，所以 `positionApi.page` 一行不用改。条件构造器下 `search.key` 被忽略，过滤键就是列 key。列 key 与后端参数名不同、或者有日期区间的页面，自己 `createFlatFilterSerializer({ rename, ranges })` 后传给 `:filter-serializer`。后端表达不了的组合（同一字段两个「包含」之类）不会抛错，按第一个能识别的条件生效，并提示一次。

后端没有过滤能力的分页接口（在线会话、我的通知、回收站）用 `createClientFilterFetcher(api, fields)` 配 `passthroughFilterSerializer`：没条件走服务端分页，有条件取全量（上限 10000 条）在浏览器里过滤。路由 query 预置筛选用 `presetEqualFilter`，导出要带当前筛选用 `flatSearchOf`。

## 表格统一标准

所有表格是同一个样子：卡片顶部单行，左边是标题和条件搜索，右边是业务按钮和内置图标。

```vue
<SmartTable
  :title="tableTitle"
  :toolbar="TABLE_TOOLBAR"
  :search="{ container: 'table' }"
  fill-height
  ...
>
  <template #toolbar-right> 业务按钮 </template>
  <template #batch> 批量操作 </template>
</SmartTable>
```

- 标题用 `useTableTitle()` 取当前页的菜单标题，随语言切换；同页有多张表时各用自己的 i18n 文案。
- `TABLE_TOOLBAR` 打开刷新、列设置、放大，不放密度按钮（密度只在「系统设置」里全局调）。3.0 默认不开放大，`toolbar` 又没有全局配置，所以逐表传。导入导出收进「更多」菜单：`{ ...TABLE_TOOLBAR, more: [...] }`，选中走 `@more-select`。
- 业务按钮写 `#toolbar-right`，不写 `#toolbar`。后者是工具栏左半，按钮会挤到搜索框左边。批量操作写 `#batch`，勾选后工具栏换成批量栏。
- `fill-height` 让表格铺满父容器并启用虚拟滚动，页面不用自己算高度。

每页条数页面不写：初始值全局是 100，档位由 3.0 按 `fill-height` 选。唯一的例外是嵌在弹窗里的表格（`UserPicker`）。整套标准由内核的 `listSearch.spec.ts` 守着，应用的页面没有这条测试，照同一个写法即可。

## 树表：本地取数器，坑不少

机构、菜单这类带层级的树表没有分页，一次把整棵拉回来自己摆，对应 `org/index.vue` 和 `menu/index.vue`。表格传 `:fetcher`，由一个本地取数器从内存里的整棵树取数。不能用 3.0 的静态 `:data` 过滤：它是平铺的 `rows.filter(...)`，不递归 `children`，在树上只会命中顶层行。

```vue
<SmartTable
  ref="tableRef"
  :columns="columns"
  :fetcher="treeFetcher"
  :search="{ container: 'table' }"
  row-key="id"
  :pagination="false"
  :expanded-row-keys="expandedKeys"
  @loaded="(rows) => (visibleTree = rows)"
  @update:expanded-row-keys="(keys) => (expandedKeys = keys)"
/>
```

树列设 `minWidth: 220 + fixed: 'left'`，横向滚动时不会丢掉“这是哪一行”。文本列一律 `ellipsis: { tooltip: true }`，否则长路径换行会把行高撑得参差。操作最多留两个：编辑，加一个 `n-dropdown` 的“更多▾”。四个操作平铺在 260~300px 里必然换行，横向滚动时又够不着，org 和 menu 都栽过这一跤。下拉项里的删除用 `useConfirm().confirm`（dialog），`n-popconfirm` 是内联触发器，塞不进 dropdown。

真正让人栽跟头的是下面四个静默失败：

**别加恒空列。** 菜单树剥掉按钮节点后只剩目录和页面，而权限码只挂在按钮上。于是“权限码”那一列 100% 是“—”。同理，过滤跑在剥离后的树上，写 `n.permission` 永远命中不了。要按权限码搜，得去查节点的按钮子节点，参考 `menu/index.vue` 的 `buttonInfoById`。菜单页干脆把权限码列整个删了。

**搜索走取数器加 `filterTree`。** 取数器收到条件构造器摊平后的扁平键（`name`、`code`），用 `utils/tree.ts` 的 `filterTree` 过滤内存里的树。它的规则是：命中的节点连整棵子树保留，没命中但有后代命中的，作为祖先链保留。树表没有后端过滤，条件是对已加载的整棵树求值，不是服务端分页。过滤后的树经 `@loaded` 拿到，用来驱动「展开全部」；树加载或增删改之后要调 `tableRef.value?.refresh()`，表格才会重新取数。

**受控展开必须删掉 `default-expand-all`。** `:expanded-row-keys` 一旦传了，naive 就以它为准，初始的 `[]` 会把 `default-expand-all` 直接盖成“全折叠”。“默认全展开”得自己用 `expandableIds(tree)` 播种。还有一点：树变了，受控 keys 不会自动跟着变。搜索或切应用之后要重算，否则命中结果藏在折叠的祖先里，看不见。

::: warning 行内改状态后要重拉，不能往行对象上写值
`filterTree` 剪枝时，“仅因后代命中而保留”的祖先是浅拷贝。搜索态下往行对象上写值（`r.enabled = v`）写的是这份副本，回不到源树。开关点完于是会自己弹回去。所以行内变更后调 `load()` 重拉，而不是本地写回。`StatusSwitch` 是悲观更新，请求成功才 emit，重拉一次就是最终态。
:::

## 排序、主从、窄栏

这些都在样例页里现成可抄，各占一句：

- **列排序**（`user` 页）：列写 `sorter: true`，点表头把 `{ sortField, sortOrder }` 并进 `fetcher`；api 层要把它们映射成后端的 `SortField`/`SortOrder`（见 `userApi.page`）。后端按实体列白名单安全排序，非法字段忽略回退默认（`PagedListExtensions.OrderBySafe`），字段名就是实体属性名，大小写不敏感。
- **主从选中**（`dict` 页，字典类型在上、字典项在下）：`:active-row-key` + `@row-click` 做行高亮；点行内的按钮、勾选框和链接不会触发 `@row-click`，行内控件无需写 `stopPropagation`。
- **窄栏搜索**：表放在窄面板（如左右并排的分栏）里，同样写 `:search="{ container: 'table' }"`，容器窄于 600 时 3.0 自动把条件构造器收成「输入框 + 筛选」。`dict` 页是上下排列，主表整行宽，用不上这一档。

列宽拖拽、合计行、合并单元格这类，全部经 attrs 或列属性透传给内层 `n-data-table`。包里没拦的属性都原样往下传，所以不需要 SmartTable 额外开 API。

## 版本

`smart-admin-web` 把它声明为 peer 依赖（`^3.0.0`），应用在自己的 `package.json` 里装这一份，内核注入的全局默认值才能被应用页面里的表格读到。改了后端的排序或分页契约后，记得 `npm run gen:api` 重新生成 schema，注意后端要在跑。

包的完整 prop、事件与逃生口 slot 以 [README](https://github.com/SimpleOne-X/smart-naive-table/blob/main/README.md) 为准。本页只覆盖它在 SmartAdmin 里的接法。
