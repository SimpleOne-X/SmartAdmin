<script setup lang="ts">
/**
 * @deprecated SmartTable 自带放大按钮:`:toolbar="{ maximize: true }"`
 * (Teleport 到 body、盖住侧栏与顶栏、不调用浏览器全屏 API,与本实现是同一思路)。
 * 本组件与 useTableZoom 没有内核页面在用,仅为已依赖它们的下游应用保留,下一个 .NET 大版本删除。
 */
// 表格「放大 / 还原」按钮。与 SmartTable 工具栏自带的刷新 / 密度 / 列设置同形态(circle quaternary + tooltip),
// 放大态换收起图标并染主色 —— 不要写成带文字的 secondary 按钮,那在工具栏里一眼就是外来户。
//
// 状态由调用方的 useTableZoom() 持有(一块表一份实例),这里只负责外观与触发。
// 按钮必须和表格一起进 Teleport 容器:留在 n-card 的 #header-extra 里,放大后会被遮罩盖住,
// 还原按钮点不到,只能按 Esc。用法见 skills/create-page-variant/split-zoom.md。
import { NButton, NTooltip } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'

withDefaults(defineProps<{ zoomed: boolean; size?: 'tiny' | 'small' | 'medium' | 'large' }>(), {
  size: 'small',
})
defineEmits<{ toggle: [] }>()

const { t } = useI18n()
</script>

<template>
  <n-tooltip>
    <template #trigger>
      <n-button
        :size="size"
        circle
        quaternary
        :type="zoomed ? 'primary' : 'default'"
        :aria-label="zoomed ? t('table.zoomExit') : t('table.zoom')"
        @click="$emit('toggle')"
      >
        <template #icon>
          <AppIcon :icon="zoomed ? 'ph:corners-in' : 'ph:corners-out'" />
        </template>
      </n-button>
    </template>
    {{ zoomed ? t('table.zoomExit') : t('table.zoom') }}
  </n-tooltip>
</template>
