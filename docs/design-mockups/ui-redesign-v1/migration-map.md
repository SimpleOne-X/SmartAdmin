# 迁移对照

仅在设计确认后才据此写实施计划。此文件现在只记录风险和对应关系。

## 原型 → 生产代码

| 原型部分 | 生产代码位置 | 说明 |
|---|---|---|
| 令牌（颜色 / 圆角 / 阴影） | `web/packages/admin/src/styles/tokens.css`、`web/DESIGN.md` | 单一事实源，需同步更新 |
| Naive 主题覆盖 | `web/packages/admin/src/theme/naive-theme.ts` | |
| 主色候选 | `web/packages/admin/src/theme/accents.ts` | 现有 6 个主色，改动需评估 |
| 布局 / 侧栏 / 顶栏 | `web/packages/admin/src/layouts/` | 注意 `layoutMode` 变体 |
| 页签 | `TabsBar.vue` | 现有固定、右键、中键、滚动等行为需保留 |
| 模块切换 | `views/module/index.vue`、`composables/useModule.ts` | |

## 风险

- 新字体需要自托管（新增静态资源，需确认）。
- 现有 `layoutMode` 各变体是否同步适配。
- 亮 / 暗主题的派生规则（`mix.ts`）需与新令牌一致。
