# OrgTreeSelect

机构树下拉。`onMounted` 拉 `orgApi.options()`(平铺的 `id` / `parentId` / `name`,登录即可调用,不要求机构页的「查询」权限码,范围同机构列表)→ `utils/tree.buildTree` 按 `parentId` 拼树,封 `n-tree-select`。
取数失败时下拉空态显示「加载失败」,不会被当成没有机构。
`inheritAttrs:false` + `v-bind="$attrs"`:`v-model:value`、`placeholder`、`size`、`disabled` 等一切透传给 `n-tree-select`,不重复声明。`clearable` 默认开(上级/归属机构通常可空),`$attrs` 可覆盖。

节点字段固定 `key-field=id`、`label-field=name`、`children-field=children`,value 即机构 `id`(`number`)。

## Props

| prop                | 类型             | 说明                                                                                                                       |
| ------------------- | ---------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `excludeSubtreeOf?` | `number \| null` | 剪掉该机构**及其所有后代**(防选到自身子树成环)。`null`/不传 = 不剪。用户页归属机构传 `null`;机构页编辑上级传 `editingId`。 |

## 用法

```vue
<!-- 用户归属机构(可空,不剪) -->
<OrgTreeSelect v-model:value="form.orgId" :placeholder="t('user.orgPlaceholder')" />

<!-- 机构页选上级(剪自身子树) -->
<OrgTreeSelect v-model:value="form.parentId" :exclude-subtree-of="editingId" clearable />
```
