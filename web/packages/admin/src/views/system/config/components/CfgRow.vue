<script setup lang="ts">
// 分组列表里的一行:左边标签(可带一行灰色说明),右边控件。
// keys 里任一键有未保存改动,标签前出现橙点——用户一眼看出改了哪几项。
// sub 是缩进的从属项(如「验证码类型」),disabled 让整行变灰不可操作(从属的总开关关着)。
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfigDraft } from '../draft'

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
const changed = computed(() => !!props.keys?.some(key => draft.dirtyKeys.value.includes(key)))
</script>

<template>
  <div
    :class="['cfg-row', { sub, off: disabled, tall, changed }]"
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
  font-size: var(--font-size-base);
}
.cfg-row + .cfg-row::before {
  content: '';
  position: absolute;
  top: 0;
  left: 14px;
  right: 0;
  border-top: 0.5px solid var(--color-border);
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
  color: var(--color-text-primary);
}
.dot {
  position: absolute;
  left: -11px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--color-warning);
}
small {
  font-size: var(--font-size-sm);
  line-height: 1.4;
  color: var(--color-text-tertiary);
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
</style>
