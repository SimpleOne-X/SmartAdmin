# SmartAdmin UI 重设计 v1 · 交接说明

> 状态：**草稿，未确认**。
> 设计确认之前，不修改 `web/` 下任何代码。

## 目的

重新设计 SmartAdmin 前端（`web/packages/admin`）的整体界面。
上一版（Apple 风格 / 玻璃质感）已推翻，已从仓库删除（git 历史可查）。

## 硬性约束

- 只使用 Naive UI 官方组件，不凭空造新组件。确实需要时，先写进 `components.md` 的「待确认」一节，等确认。
- 面向 ERP / MES 类企业应用：大量数据表格，紧凑密度，但字号要够大。
- 亮 / 暗两套主题都要高级、可读。
- 页面自适应。基准显示环境：2560×1440，缩放 125%（CSS 视口约 2048×1050）。
- 不引入新依赖、不新增外部资源（字体、图标）而不经确认。

## 目录

| 文件 | 内容 | 状态 |
|---|---|---|
| `README.md` | 本文件 | 草稿 |
| `SmartAdmin-原型设计-V1.html` | 可点击的原型本体（全部页面，亮 / 暗） | 待做 |
| `design-tokens.html` | 令牌页：颜色、字体、间距、圆角、阴影，标注对应的 Naive `GlobalThemeOverrides` 键 | 待做 |
| `components.md` | 界面元素 → Naive UI 官方组件对照，含「待确认」清单 | 骨架 |
| `screens.md` | 页面清单与覆盖情况 | 骨架 |
| `interactions.md` | 交互规格（页签、侧栏、快捷键、模块切换等） | 骨架 |
| `migration-map.md` | 原型 → `web/packages/admin/src/` 迁移对照与风险 | 骨架 |
| `screenshots/` | 每页亮 / 暗截图 | 空 |
| `vendor/` | 原型离线依赖（Vue、Naive、图标、字体）；可选 | 未建 |

## 相关文档位置

- 设计说明（决策与取舍）：`docs/superpowers/specs/2026-09-26-ui-redesign-design.md`
- 迁移实施计划：`docs/superpowers/plans/`，设计确认后再写。
- 生产令牌规范：`web/DESIGN.md`、`web/packages/admin/src/styles/tokens.css`，确认前不动。

## 如何预览

在本目录起一个静态服务，再用浏览器打开 `SmartAdmin-原型设计-V1.html`：

```bash
python -m http.server 5288 --bind 127.0.0.1
```

## 待用户决策

- （在此列出需要用户拍板的问题）
