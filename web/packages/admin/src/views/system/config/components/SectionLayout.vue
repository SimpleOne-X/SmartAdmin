<script setup lang="ts">
// 配置中心每个分类的统一版面:页头 + 「左边设置、右边预览」,整块吃满内容区、一屏放下。
// layout 决定两栏比例:even 1:1(默认)、wide 设置多时 3:2、slim 设置少时 2:3(预览放大,不留半屏空白)。
// 没有 #preview 的分类(高级)整页给内容,不硬凑预览。
// 设置列放不下(矮窄屏)时在列内滚,页面本身不滚。
withDefaults(
  defineProps<{
    title: string
    desc?: string
    previewTitle?: string
    layout?: 'even' | 'wide' | 'slim'
    /** 预览内容在预览栏里的位置:center(缩小版界面)/ start(列表类,自上而下排) */
    previewAlign?: 'center' | 'start'
  }>(),
  { layout: 'even', previewAlign: 'center' },
)
</script>

<template>
  <section class="cfg-section">
    <header class="cfg-head">
      <h2>{{ title }}</h2>
      <p v-if="desc">{{ desc }}</p>
    </header>
    <div :class="['cfg-split', `cfg-split--${$slots.preview ? layout : 'single'}`]">
      <div class="cfg-form">
        <slot />
      </div>
      <aside v-if="$slots.preview" class="cfg-preview" data-testid="config-preview">
        <div class="cfg-preview-cap">
          <span class="live">{{ previewTitle }}</span>
          <slot name="preview-extra" />
        </div>
        <div :class="['cfg-preview-body', `cfg-preview-body--${previewAlign}`]">
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
  height: 100%;
  min-height: 0;
}
.cfg-head {
  display: flex;
  flex: none;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 4px 12px;
  margin-bottom: 14px;
}
.cfg-head h2 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.01em;
}
.cfg-head p {
  margin: 0;
  font-size: var(--font-size-base);
  color: var(--color-text-secondary);
}
.cfg-split {
  display: grid;
  flex: 1;
  gap: 20px;
  min-height: 0;
}
.cfg-split--even {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}
.cfg-split--wide {
  grid-template-columns: minmax(0, 1.55fr) minmax(0, 1fr);
}
.cfg-split--slim {
  grid-template-columns: minmax(360px, 0.8fr) minmax(0, 1.2fr);
}
.cfg-split--single {
  grid-template-columns: minmax(0, 1fr);
}
.cfg-form {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-width: 0;
  min-height: 0;
  overflow: auto;
}
/* 预览画布:浅灰圆角底,里面放缩小版界面或效果说明 */
.cfg-preview {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  border-radius: 14px;
  background: color-mix(in srgb, var(--color-fill) 70%, var(--color-bg-body));
}
.cfg-preview-cap {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 14px 0;
  font-size: var(--font-size-sm);
  color: var(--color-text-secondary);
}
.live {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.live::before {
  content: '';
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--color-success);
}
.cfg-preview-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: center;
  gap: 12px;
  min-height: 0;
  padding: 12px 14px 14px;
  overflow: auto;
}
.cfg-preview-body--start {
  justify-content: flex-start;
}
/* v-flash:改了哪一项,预览里受影响的部分闪一下橙框,告诉用户"改的是这里" */
.cfg-preview :deep(.cfg-flash) {
  animation: cfg-flash 1.1s ease-out;
}
@keyframes cfg-flash {
  from {
    box-shadow: 0 0 0 3px var(--color-warning);
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
@media (max-width: 1000px) {
  .cfg-split {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: auto minmax(280px, 1fr);
    overflow: auto;
  }
  .cfg-form {
    overflow: visible;
  }
}
</style>
