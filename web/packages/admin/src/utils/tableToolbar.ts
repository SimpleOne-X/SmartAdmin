import type { ToolbarConfig } from 'smart-naive-table'

/**
 * 整页列表统一的工具栏:刷新 / 放大还原 / 列设置三个内置图标。
 * 放大(maximize)统一开:宽表想临时铺满整个视口看全貌时用,所有表格同一个入口、同一个位置;
 * 即便整页列表本来就铺满内容区,也保持显示。
 * 3.0 的 `toolbar` 没有全局配置,所以每个表格显式传这一份,让按钮组合是写出来的而不是靠默认。
 * 密度不放在表格上:它是「系统设置」里的全局偏好,表格再给一个入口只会让同一个设置有两处可改。
 * 「更多」菜单没有选项就不出现,需要的页面自己 `{ ...TABLE_TOOLBAR, more: [...] }`。
 * 静态数据的表格 3.0 会自己把刷新藏起来(点了也没用),这里写 true 不会强行显示。
 */
export const TABLE_TOOLBAR: ToolbarConfig = Object.freeze({
  refresh: true,
  density: false,
  columnSettings: true,
  maximize: true,
})

/**
 * 定高表格(fill-height)的虚拟滚动行高下限,每张 fill-height 的表都要传 `:min-row-height="TABLE_MIN_ROW_HEIGHT"`。
 * smart-naive-table 按密度默认给 40(紧凑)/ 48(舒适),而本项目紧凑行实际只有约 33px:
 * 虚拟列表拿这个下限去估算没渲染的行,估高大于真实行高,滚到底就少渲染最后几行、底部留白
 * (48 条的表看不到最后 3 条,100 条的表滚到底只到第 96 行)。下限必须 ≤ 任何密度下的真实行高,宁小勿大。
 */
export const TABLE_MIN_ROW_HEIGHT = 30
