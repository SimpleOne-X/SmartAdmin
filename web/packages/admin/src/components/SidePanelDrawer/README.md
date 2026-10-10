# SidePanelDrawer — 左侧面板的窄档抽屉

「左分组栏 + 右列表」的页面(`.side-page`,分组栏用 `.side-filter`)在内容区窄时,分组栏不该再占一列:200px 的栏加上表格,手机上表格只剩 160px 出头。本组件是面板收起后的去处——从左侧滑出的抽屉,标题栏写分组名,插槽放面板内容。

判据与抽屉配成一对:`useSidePanel()` 管「现在该不该收」(壳层内容区宽度 < 1000,阈值取 `SHELL_PANEL_DRAWER_MAX`,量到宽度前按宽屏渲染,首帧不闪),本组件管抽屉本身。

## 用法

```vue
<script setup lang="ts">
import { createReusableTemplate } from '@vueuse/core'
import { SidePanelDrawer, useSidePanel } from 'smart-admin-web'

const { compact, drawerOpen } = useSidePanel()
// 面板内容只写一份:宽屏放进 aside,窄屏放进抽屉
const [DefinePanel, ReusePanel] = createReusableTemplate()
</script>

<template>
  <DefinePanel>…分组头 / 搜索 / 树…</DefinePanel>

  <div class="side-page">
    <aside v-if="!compact" class="side-filter"><ReusePanel /></aside>
    <SmartTable fill-height card-on-narrow …>
      <!-- 限定数据范围的控件放 #toolbar 左半,不是业务按钮 -->
      <template v-if="compact" #toolbar>
        <n-button secondary @click="drawerOpen = true">分组</n-button>
      </template>
    </SmartTable>
  </div>

  <SidePanelDrawer v-model:show="drawerOpen" title="分组"><ReusePanel /></SidePanelDrawer>
</template>
```

选中分组后想自动收起抽屉,在选中回调里 `drawerOpen.value = false`。宽度回到不紧凑时 `useSidePanel` 会自己把 `drawerOpen` 置回 `false`。

范例页:`src/views/system/user/index.vue`(机构树,另有宽屏下「向左收起」的逻辑,是该页自己的)。

## Props

| Prop           | 类型      | 默认    | 说明                                |
| -------------- | --------- | ------- | ----------------------------------- |
| `v-model:show` | `boolean` | `false` | 抽屉显隐                            |
| `title`        | `string`  | 必传    | 标题,通常是分组名(「机构」「分类」) |

## Slots

| Slot      | 说明     |
| --------- | -------- |
| `default` | 面板内容 |

## 边界与注意

- 抽屉宽 `min(300px, 88vw)`,固定从左侧滑出,不提供 `placement`。
- 面板里的 `.side-filter__btn` 在抽屉里放大到 32×32,触屏好点;其余沿用 `layout.css` 的侧栏约定(`.side-filter__head` / `.side-tree` …)。
- 样式在本组件的**非 scoped** `<style>`:面板内容是调用方模板里的节点,带调用方的 `data-v`,组件自己的 scoped 规则够不着。
- 不要用 CSS 在窄档直接 `display: none` 掉 `.side-filter`:没有入口的话,用户就彻底失去分组筛选。入口(工具栏按钮)由页面放。
