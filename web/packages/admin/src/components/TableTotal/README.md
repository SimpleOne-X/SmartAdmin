# TableTotal — 分页栏的「共 N 条」

SmartTable 的分页栏默认只有页码和每页条数,总条数要宿主从 `#pagination-prefix` 插槽里给。
每张分页表格都写同一段,`listSearch.spec.ts` 守卫这一条:

```vue
<SmartTable ...>
  <template #pagination-prefix="{ itemCount }">
    <TableTotal :count="itemCount" />
  </template>
</SmartTable>
```

| Prop    | 类型      | 说明                                              |
| ------- | --------- | ------------------------------------------------- |
| `count` | `number?` | 总条数,取插槽的 `itemCount`(远程表格是后端 total) |

文案走 `table.total`(`共 {n} 条` / `Total {n}`),切语言即时生效。`:pagination="false"` 的树表没有分页栏,不用写。
