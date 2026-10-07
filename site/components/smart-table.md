# SmartTable

Nearly every list page in the SmartAdmin frontend is the same table: `smart-naive-table` (a standalone npm package, `^3.0.0`). Its model has just two pieces — one `columns` array that drives the conditional search, the dict cells, and the column-settings panel all at once, and one `fetcher` function that wires in any backend. This page covers exactly one thing: how to use it correctly inside SmartAdmin. The full, prop-by-prop reference lives in the package README and isn't repeated here.

## The bare minimum for a list page

Take the built-in position management page (`web/packages/admin/src/views/system/position/index.vue`) as the skeleton, strip it down to nothing but the table, and written in an app it looks like this:

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

The `name` column has `search`, so it becomes a field in the condition builder, limited to the "contains" operator the backend can run; `createTime` has `format: 'datetime'`, so it's formatted in local time; `storage-key` remembers which localStorage key this table's column settings live under, and the naming convention for it is below. Whatever you don't declare, you can ignore — the table won't invent behavior for you. Four conventions hide in that short snippet; the rest of this page unpacks them one by one.

## The fetcher is the only thing you have to adapt

SmartTable makes exactly one assumption about the backend: `fetcher(params) => Promise<{ items, total }>`. SmartAdmin's backend doesn't return that shape — it returns a `PagedList<T>` (`{ current, size, total, items }`), and its paging params are `Current`/`Size`, not `page`/`pageSize`. That mismatch shouldn't be scattered across every page, so the kernel pins it down in one place, the package's `api/index.ts`:

```ts
// frontend {page,pageSize} → backend record props {Current,Size} (PascalCase)
const pageParams = (p: { page: number; pageSize: number }) => ({ Current: p.page, Size: p.pageSize })

// backend PagedList<T> → SmartTable's {items,total} contract
function toPage<T>(res): { items: T[]; total: number } {
  const p = unwrap<PagedList<T>>(res)
  return { items: p.items, total: p.total }
}

export const positionApi = {
  page: (params: { page: number; pageSize: number; name?: string }) =>
    client
      .GET('/api/v1/sys/position/page', {
        // search key `name` → backend's PascalCase `Name`; the type wants Pascal, the binding itself is case-insensitive
        params: { query: { ...pageParams(params), Name: params.name } },
      })
      .then((r) => toPage<SysPosition>(r)),
}
```

So in the page, `:fetcher="positionApi.page"` just hands the API-layer method straight in — the mapping is already done there. When you add a list page for your own endpoints, write one in the app's `src/api/<domain>.ts` in the shape of `userApi.page`/`positionApi.page` and change the endpoint and search field names: the `client` is the app's own `./client`, and `pageParams`/`toPage`/`unwrap` come from `smart-admin-web`. Don't hand-assemble `Current`/`Size` inside the component.

The `name` column's condition arrives in the `fetcher` as the flat key `name` (the "Conditional search" section below explains how); which backend field that lands on is decided by the `Name: params.name` step in the API layer.

## Three things already wired up for you

**Labels are injected globally — pages don't pass them by hand.** SmartTable's button copy — search / reset / refresh / column settings — needs to follow i18n, and `createSmartAdmin()` hooks it up once during assembly so every page inherits it afterward:

```ts
app.provide(
  SMART_TABLE_DEFAULTS,
  createSmartTableDefaults({
    density: options.table?.density ?? 'compact',
    defaultPageSize: 100,
    pageSizes: options.table?.pageSizes, // unset: 3.0 picks the options from fill-height
    filterSerializer: flatFilterSerializer,
    labels: computed(() => {
      void i18n.global.locale.value // touch locale so it's tracked as a dependency — switching language recomputes instantly
      const t = i18n.global.t
      return { search: t('common.search'), reset: t('common.reset'), /* …column settings / density, etc. */ }
    }),
  }),
)
```

The page layer therefore never writes `:labels`; you only pass it to override an individual page's copy. The default density and page-size options can be changed by the app through `createSmartAdmin({ table: { density, pageSizes } })`. Left unset, the page-size options are `100 / 500 / 1000` without `fill-height` and `100 / 1000 / 10000` with it; the ceiling is the backend's `SmartAdmin:Api:MaxPageSize` (default 10000), so an app that lowers it must pass `pageSizes` to match. Column titles are the exception — they must be written as a function, `title: () => t('...')`. Writing `title: t('...')` directly evaluates only at the moment the column is built and won't update on a language switch.

**Errors stay in the view layer.** The package shows no UI of its own; when the `fetcher` throws, it emits to `@error`, and the page decides how to surface it. Across the app this is written uniformly as `@error="(e) => message.error(translateError(e))"`: `translateError` maps the backend's numeric `ErrorCode` into copy in the current language.

**Action buttons show or hide by permission.** The authorization model is "permission code IS the route," so for each action the page checks `authStore.hasPerm('{METHOD}:/{route}')` once and simply doesn't render the button when the permission is missing:

```ts
// inside the actions-column render, gate each button individually
authStore.hasPerm('PUT:/api/v1/sys/position/{id}')
  ? h(NButton, { onClick: () => openEdit(r) }, () => t('common.edit'))
  : null
```

The add button in the right half of the toolbar (`#toolbar-right`) and the bulk-delete button inside the batch bar (`#batch`) work the same way via the `v-auth` directive: `v-auth="'POST:/api/v1/sys/position/add'"`. The permission-code string must match the backend route template character for character (including placeholder segments like `{id}`), or it will never match.

`storage-key` decides which localStorage key holds the column settings (prefixed `protable:`); name it uniformly as `{module}-{page}`, e.g. `sys-position`, `sys-user`.

## Conditional search: always the condition builder

Every list page searches through the condition builder: `:search="{ container: 'table' }"` puts one row of "field + operator + value" inside the table card, and below 600px of container width it collapses into "input + filter". The standalone search card and `layout: 'inline'` are no longer used.

Open only the operators the backend can actually run. Offering one it can't makes the UI lie. `SEARCH_ACTIONS` has the three ready-made sets:

```ts
{ key: 'name', search: { actions: SEARCH_ACTIONS.fuzzy } },        // text, backend Contains
{ key: 'status', options, search: { actions: SEARCH_ACTIONS.exact } }, // enum and id, backend ==
{ key: 'createTime', search: { type: 'daterange', actions: SEARCH_ACTIONS.dayRange } }, // date, backend >= and <=
```

The condition builder produces `filters`, not flat params. The kernel injects `flatFilterSerializer` globally to flatten them back into each endpoint's existing flat keys, so `positionApi.page` needs no change. Under the condition builder `search.key` is ignored and the filter key is the column key. A page whose column key differs from the backend param name, or that has a date range, builds its own `createFlatFilterSerializer({ rename, ranges })` and passes it to `:filter-serializer`. Combinations the backend can't express (two "contains" on one field, say) don't throw: the first recognizable condition wins and the user is told once.

A paged endpoint with no filtering on the backend (online sessions, my notices, the recycle bin) uses `createClientFilterFetcher(api, fields)` with `passthroughFilterSerializer`. With no condition it pages on the server; with one it fetches everything (capped at 10000 rows) and filters in the browser. Preset a filter from a route query with `presetEqualFilter`, and export with the current filters through `flatSearchOf`.

## One standard for every table

Every table looks the same: one row across the top of the card, with the title and conditional search on the left, and the business buttons and built-in icons on the right.

```vue
<SmartTable
  :title="tableTitle"
  :toolbar="TABLE_TOOLBAR"
  :search="{ container: 'table' }"
  fill-height
  ...
>
  <template #toolbar-right> business buttons </template>
  <template #batch> bulk actions </template>
</SmartTable>
```

- The title comes from `useTableTitle()`, the current page's menu title, which follows the language. A page with several tables gives each its own i18n text.
- `TABLE_TOOLBAR` turns on refresh, column settings and maximize, and leaves the density button out (density is a global preference in the settings drawer). 3.0 leaves maximize off by default and `toolbar` has no global config, so each table passes it. Import and export go into the "More" menu: `{ ...TABLE_TOOLBAR, more: [...] }`, with selections arriving on `@more-select`.
- Business buttons go in `#toolbar-right`, not `#toolbar`. The latter is the left half of the toolbar, and buttons there get squeezed in front of the search box. Bulk actions go in `#batch`, which replaces the toolbar once rows are checked.
- `fill-height` makes the table fill its parent and turns on virtual scrolling, so the page never computes a height.

Pages don't set the page size: the global initial value is 100, and 3.0 picks the options from `fill-height`. The one exception is a table embedded in a dialog (`UserPicker`). The kernel's `listSearch.spec.ts` guards the whole standard. App pages don't get that test, so follow the same shape by hand.

## Tree tables: a local fetcher, and its four traps

Hierarchical trees like orgs and menus have no pagination: you pull the whole thing back at once and lay it out yourself, as the org page (`org/index.vue`) and the menu page (`menu/index.vue`) do. The table takes a `:fetcher`, and a local fetcher reads from the whole tree in memory. Don't use 3.0's static `:data` filtering here. It is a flat `rows.filter(...)` that doesn't recurse into `children`, so on a tree it only ever matches top-level rows.

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

Give the tree column `minWidth: 220` + `fixed: 'left'` so you never lose track of "which row is this" while scrolling sideways; give every text column `ellipsis: { tooltip: true }`, or long paths wrap and leave rows at ragged heights. Keep at most two actions — Edit, plus a "More ▾" `n-dropdown`. Four actions laid flat across 260–300px are bound to wrap, and you can't reach them once you scroll horizontally; both org and menu tripped on exactly this. For a delete inside a dropdown item, use `useConfirm().confirm` (a dialog) — `n-popconfirm` is an inline trigger and won't fit inside a dropdown.

What will actually cost you an afternoon of debugging is the four silent failures below:

**Don't add a permanently-empty column.** Once the menu tree strips out button nodes, only directories and pages remain, and permission codes hang only off buttons — so a "permission code" column is 100% "—". By the same token, filtering runs over the stripped tree, so matching on `n.permission` never hits; to search by permission code you have to look at a node's button children (see `buttonInfoById` in `menu/index.vue`). The menu page just deletes the permission-code column outright.

**Search runs in the fetcher, through `filterTree`.** The fetcher receives the flat keys (`name`, `code`) that the condition builder flattened, and filters the in-memory tree with `filterTree` from `utils/tree.ts` (a matching node keeps its whole subtree; a non-matching node with a matching descendant is kept as part of the ancestor chain). A tree has no backend filtering, so conditions are evaluated against the whole loaded tree, not paged on the server. Take the filtered rows from `@loaded` to drive "expand all", and call `tableRef.value?.refresh()` after the tree loads or changes, so the table fetches again.

**Controlled expansion means dropping `default-expand-all`.** Once you pass `:expanded-row-keys`, naive treats it as the source of truth, and an initial `[]` overrides `default-expand-all` straight into "all collapsed." You have to seed "expand all by default" yourself with `expandableIds(tree)`. One more thing: when the tree changes the controlled keys don't follow automatically — recompute them after a search or an app switch, or matching results stay hidden inside collapsed ancestors.

::: warning Reload after an inline status change — don't write onto the row object
When `filterTree` prunes, an ancestor kept "only because a descendant matched" is a shallow copy. In a search state, writing onto the row object (`r.enabled = v`) writes to that copy and never reaches the source tree — the toggle springs back after you click it. So after an inline change, call `load()` to refetch rather than writing back locally. `StatusSwitch` is a pessimistic update (it only emits once the request succeeds), so a single refetch gives you the final state.
:::

## Sorting, master-detail, narrow columns

These are all ready to copy from the sample pages, one line each:

- **Column sorting** (the `user` page): put `sorter: true` on the column, and clicking the header merges `{ sortField, sortOrder }` into the `fetcher`; the API layer maps them to the backend's `SortField`/`SortOrder` (see `userApi.page`). The backend sorts safely against an entity-column allowlist, ignoring invalid fields and falling back to the default (`PagedListExtensions.OrderBySafe`); the field name is the entity property name, case-insensitive.
- **Master-detail selection** (the `dict` page): `:active-row-key` + `@row-click` for row highlighting; clicking an in-row button, checkbox or link doesn't fire `@row-click`, so in-row controls don't need `stopPropagation`.
- **Narrow-column search**: for a table in a narrow pane (say a side-by-side split), write the same `:search="{ container: 'table' }"`. Below 600px of container width, 3.0 collapses the condition builder into "input + filter". The `dict` page stacks its two tables vertically, so its master table is full width and never hits that tier.

Column-width dragging, summary rows, cell merging and the like all pass through to the inner `n-data-table` via attrs or column properties — SmartTable exposes no extra API for them; any prop the package doesn't intercept is forwarded down as-is.

## Version

`smart-admin-web` declares it as a peer dependency (`^3.0.0`); the app installs that one copy in its own `package.json`, which is what lets tables on the app's pages read the defaults the kernel injects. After you change the backend's sort or paging contract, remember to run `npm run gen:api` to regenerate the schema (the backend must be running).

The package's full prop, event, and escape-hatch slot reference is authoritative in the [README](https://github.com/SimpleOne-X/smart-naive-table/blob/main/README.en.md); this page covers only how it's wired into SmartAdmin.
