<script setup lang="ts">
// 带左侧面板的页面在窄内容区里的面板抽屉:从左侧滑出,标题栏写分组名,插槽放面板内容。
// 与 useSidePanel 配对使用:`compact` 时不画 aside,点工具栏按钮把 `drawerOpen` 置 true 打开这里。
// 面板里的 `.side-filter__btn` 等沿用 layout.css 的侧栏约定,抽屉里是触屏,图标按钮放大到可点的尺寸。
import { NDrawer, NDrawerContent } from 'naive-ui'

defineProps<{
  /** 抽屉标题,通常是分组名(「机构」「分类」) */
  title: string
}>()

const show = defineModel<boolean>('show', { default: false })
</script>

<template>
  <n-drawer v-model:show="show" placement="left" width="min(300px, 88vw)">
    <n-drawer-content :title="title" closable>
      <div class="side-panel-body">
        <slot />
      </div>
    </n-drawer-content>
  </n-drawer>
</template>

<!-- 不加 scoped:面板内容是调用方模板里的节点,带的是调用方的 data-v,组件自己的 scoped 规则够不着里面的 .side-filter__btn。 -->
<style>
/* 与 aside 里同样的纵向堆叠与间距 */
.side-panel-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

/* 抽屉在触屏上用:头部图标按钮放大到可点的尺寸 */
.side-panel-body .side-filter__btn {
  width: 32px;
  height: 32px;
}
</style>
