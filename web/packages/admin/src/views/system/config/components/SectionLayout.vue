<script setup lang="ts">
// 配置中心每个分类的统一版面:页头 + 「左边设置、右边预览」,整块吃满内容区、一屏放下。
// layout 决定两栏比例:even 1:1(默认)、wide 设置多时 3:2、slim 设置少时 2:3(预览放大,不留半屏空白)。
// 没有 #preview 的分类(高级)整页给内容,不硬凑预览。
// 设置列放不下(矮窄屏)时在列内滚,页面本身不滚。
// 档位由 index.vue 量宽度后下发(layoutMode.ts):宽档左右两栏;中档单栏、预览叠到设置下面;
// 窄档预览排到设置前面并收成一条,点开才展开。
import { useI18n } from 'vue-i18n'
import { NButton } from 'naive-ui'
import { useConfigLayout } from '../layoutMode'

withDefaults(
  defineProps<{
    title: string
    desc?: string
    previewTitle?: string
    layout?: 'even' | 'wide' | 'slim'
    /** 预览内容在预览栏里的位置:center(缩小版界面)/ start(列表类,自上而下排) */
    previewAlign?: 'center' | 'start'
    /** 设置列自己不滚、也不留底部保存条的位置之外的空白(敏感操作:列表卡片自己管滚动) */
    fixedForm?: boolean
  }>(),
  { layout: 'even', previewAlign: 'center' },
)

const { t } = useI18n()
const { nar, side, previewOpen } = useConfigLayout()
</script>

<template>
  <section :class="['cfg-section', { side, nar }]">
    <header class="cfg-head">
      <h2>{{ title }}</h2>
      <p v-if="desc">{{ desc }}</p>
    </header>
    <div :class="['cfg-split', `cfg-split--${$slots.preview ? layout : 'single'}`, { side, nar }]">
      <div :class="['cfg-form', { 'cfg-form--fixed': fixedForm }]">
        <slot />
      </div>
      <aside
        v-if="$slots.preview"
        :class="['cfg-preview', { auto: previewAlign === 'start' }]"
        data-testid="config-preview"
      >
        <div class="cfg-preview-cap">
          <span class="live">{{ previewTitle }}</span>
          <span class="ex"><slot name="preview-extra" /></span>
          <n-button
            v-if="nar"
            quaternary
            size="small"
            :aria-expanded="previewOpen"
            @click="previewOpen = !previewOpen"
          >
            {{ previewOpen ? t('config.preview.collapse') : t('config.preview.expand') }}
          </n-button>
        </div>
        <div
          v-if="!nar || previewOpen"
          :class="['cfg-preview-body', `cfg-preview-body--${previewAlign}`]"
        >
          <slot name="preview" />
        </div>
      </aside>
    </div>
  </section>
</template>

<style scoped>
.cfg-section {
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.cfg-head {
  display: flex;
  flex: none;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 4px 12px;
  margin-bottom: 12px;
}
.cfg-head h2 {
  margin: 0;
  font-size: 17px;
  font-weight: 600;
  letter-spacing: -0.01em;
}
.cfg-head p {
  margin: 0;
  font-size: 14px;
  color: var(--text-2);
}
.cfg-split {
  display: grid;
  flex: 1 1 0;
  gap: 16px;
  min-height: 0;
}
/* 中档 / 窄档:单栏纵排,各块按内容高度 */
.cfg-split:not(.side) {
  display: flex;
  flex: none;
  flex-direction: column;
}
.cfg-split--even.side {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}
.cfg-split--wide.side {
  grid-template-columns: minmax(0, 1.55fr) minmax(0, 1fr);
}
.cfg-split--slim.side {
  grid-template-columns: minmax(340px, 0.8fr) minmax(0, 1.2fr);
}
.cfg-split--single.side {
  grid-template-columns: minmax(0, 1fr);
}
.cfg-form {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-width: 0;
  min-height: 0;
  /* 设置列的宽度当容器查询用:安全策略的两列分组按它收成单列 */
  container-type: inline-size;
}
/* 宽档设置列在列内滚;底部留出保存条的位置,别让它盖住最后一项 */
.side .cfg-form {
  overflow: auto;
  padding-bottom: 56px;
}
.side .cfg-form--fixed {
  overflow: hidden;
  padding-bottom: 60px;
}
.cfg-split:not(.side) .cfg-form {
  flex: none;
}
.cfg-split:not(.side) .cfg-form--fixed {
  overflow: visible;
}
/* 预览画布:浅灰圆角底,里面放缩小版界面或效果说明 */
.cfg-preview {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  border-radius: 14px;
  background: var(--fill);
}
.cfg-split:not(.side):not(.nar) .cfg-preview {
  flex: none;
  height: 400px;
}
.cfg-split:not(.side):not(.nar) .cfg-preview.auto {
  height: auto;
}
.nar .cfg-preview {
  flex: none;
  order: -1;
}
.cfg-preview-cap {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  min-height: 40px;
  padding: 8px 14px 0;
  font-size: 13px;
  color: var(--text-2);
}
.live {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-right: auto;
}
.live::before {
  content: '';
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ok);
}
.ex {
  display: inline-flex;
  align-items: center;
}
.cfg-preview-body {
  display: flex;
  flex: 1 1 0;
  flex-direction: column;
  justify-content: center;
  gap: 12px;
  min-height: 0;
  padding: 10px 14px 14px;
  overflow: auto;
}
.cfg-preview-body--start {
  justify-content: flex-start;
}
.nar .cfg-preview-body {
  flex: none;
  height: 280px;
}
.nar .cfg-preview-body--start {
  height: auto;
  max-height: 420px;
}
/* v-flash:改了哪一项,预览里受影响的部分闪一下橙框,告诉用户"改的是这里" */
.cfg-preview :deep(.cfg-flash) {
  animation: cfg-flash 1.1s var(--ease);
}
@keyframes cfg-flash {
  from {
    box-shadow: 0 0 0 3px var(--warn);
  }
  to {
    box-shadow: 0 0 0 3px transparent;
  }
}
@media (prefers-reduced-motion: reduce) {
  .cfg-preview :deep(.cfg-flash) {
    animation: none;
  }
}
</style>
