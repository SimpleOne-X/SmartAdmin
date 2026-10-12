# RoleSelect

角色选择器。基于 [`ApiSelect`](../ApiSelect/README.md),`fetch = roleApi.options`(登录即可调用,不要求角色页的「查询」权限码)。只列**启用**角色,`label` = 角色名,`value` = 角色 `id`(`number`)。
一次取回全部启用角色,名称搜索由 `n-select` 在浏览器里过滤(`remote` 关)。
多选常用(用户分配角色);`multiple`、`placeholder` 等经 `$attrs` 透传。

## Props

| prop        | 类型     | 默认  | 说明                     |
| ----------- | -------- | ----- | ------------------------ |
| `pageSize?` | `number` | `100` | 下拉最多列出的角色个数。 |

## 用法

```vue
<RoleSelect v-model:value="form.roleIds" multiple :placeholder="t('user.rolePlaceholder')" />
```
