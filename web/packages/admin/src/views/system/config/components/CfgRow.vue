<script setup lang="ts">
// 分组列表里的一行:左边标签(可带一行灰色说明),右边控件。
// keys 里任一键有未保存改动,标签前出现橙点——用户一眼看出改了哪几项。
// sub 是缩进的从属项(如「验证码类型」),disabled 让整行变灰不可操作(从属的总开关关着)。
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfigDraft } from '../draft'
import { useConfigLayout } from '../layoutMode'

const props = defineProps<{
  label?: string
  hint?: string
  keys?: readonly string[]
  sub?: boolean
  disabled?: boolean
  /** 控件比一行高(多行输入、标签组):顶端对齐 */
  tall?: boolean
}>()

const { t } = useI18n()
const draft = useConfigDraft()
const { nar } = useConfigLayout()
const changed = computed(() => !!props.keys?.some(key => draft.dirtyKeys.value.includes(key)))
</script>

<template>
  <div
    :class="['cfg-row', { sub, off: disabled, tall, changed, nar }]"
    :aria-disabled="disabled || undefined"
  >
    <div class="lb">
      <span class="name">
        <i v-if="changed" class="dot" aria-hidden="true" />
        <slot name="label">{{ label }}</slot>
        <span v-if="changed" class="sr-only">{{ t('config.unsavedTab') }}</span>
      </span>
      <small v-if="hint || $slots.hint">
        <slot name="hint">{{ hint }}</slot>
      </small>
    </div>
    <div class="ctl">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.cfg-row {
  position: relative;
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 48px;
  padding: 6px 14px;
  font-size: 14px;
}
.cfg-row + .cfg-row::before {
  content: '';
  position: absolute;
  top: 0;
  left: 14px;
  right: 0;
  border-top: 1px solid var(--separator);
}
.cfg-row.sub {
  padding-left: 34px;
}
.cfg-row.sub::before {
  left: 34px;
}
.cfg-row.tall {
  align-items: flex-start;
  padding-top: 8px;
  padding-bottom: 8px;
}
.cfg-row.off .lb,
.cfg-row.off .ctl {
  opacity: 0.45;
  pointer-events: none;
}
.lb {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 130px;
}
.name {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
  color: var(--text-1);
}
.dot {
  position: absolute;
  left: -11px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--warn);
}
small {
  font-size: 13px;
  line-height: 1.4;
  color: var(--text-3);
}
.ctl {
  display: flex;
  flex: 0 1 auto;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  min-width: 0;
}
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}
/* 窄档:从属项的缩进收一点 */
.cfg-row.nar.sub {
  padding-left: 24px;
}
.cfg-row.nar.sub::before {
  left: 24px;
}
</style>

<style>
/* 带文本输入框的行:标签与输入按 1:1.8 分(输入框是主角,要宽),输入框铺满自己那一份。
   放在非 scoped 块里::has() 要看子孙里有没有输入框,scoped 的属性选择器改写会打乱它。 */
.cfg-row:has(> .ctl > :is(.n-input, .n-select)) > .lb {
  flex: 1 1 0;
}
.cfg-row:has(> .ctl > :is(.n-input, .n-select)) > .ctl {
  flex: 1.8 1 0;
}
.cfg-row > .ctl > :is(.n-input, .n-select) {
  flex: 1;
  min-width: 0;
}
/* 窄档:设置行上下排,标签一行、控件占满一行(带开关的行保持左右一行,开关本来就小) */
.cfg-row.nar:not(:has(> .ctl > .n-switch)) {
  flex-wrap: wrap;
  row-gap: 8px;
}
.cfg-row.nar:not(:has(> .ctl > .n-switch)) > .lb {
  flex: 1 1 100%;
}
.cfg-row.nar:not(:has(> .ctl > .n-switch)) > .ctl,
.cfg-row.nar:has(> .ctl > :is(.n-input, .n-select)) > .ctl {
  flex: 1 1 100%;
}
.cfg-row.nar:not(:has(> .ctl > .n-switch)) > .ctl {
  justify-content: flex-start;
}
.cfg-row.nar > .ctl > .n-input-number {
  width: 120px;
}
.cfg-row.nar > .ctl > .n-slider {
  flex: 1;
  width: auto;
}
</style>
