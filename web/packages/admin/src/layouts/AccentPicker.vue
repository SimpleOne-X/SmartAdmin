<script setup lang="ts">
// 强调色选择器(仿 macOS 系统设置 → 外观 → 强调色):
// 一排圆点,选中的带双层环 + 对勾,下方写出当前色名与色值;最后一颗彩虹圆点是「自定义」,点开取色板。
// 圆点显示的是实心控件真正用的那个色(solid() 压暗后的结果),不是原始色相,和按钮 / 开关对得上。
// 底部一条真控件拼的效果条,换色时就能看到主按钮、开关、链接、进度会变成什么样。
import { computed } from 'vue'
import { NButton, NColorPicker, NProgress, NSwitch, NTooltip } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useAppStore } from '#/stores/app'
import { ACCENT_META, ACCENTS } from '#/theme/accents'
import { solid } from '#/theme/mix'

const app = useAppStore()
const { t } = useI18n()

const presetIndex = computed(() =>
  ACCENTS.findIndex(c => c.toLowerCase() === app.accent.toLowerCase()),
)
const isCustom = computed(() => presetIndex.value < 0)
const customDot = computed(() => (isCustom.value ? solid(app.accent) : ''))
const currentName = computed(() =>
  isCustom.value
    ? t('settings.accentName.custom')
    : t(`settings.accentName.${ACCENT_META[presetIndex.value]!.key}`),
)

// n-color-picker 可能回 #rrggbb 小写或带透明度的串,统一收成 #RRGGBB 大写再存
function setCustom(value: string) {
  const m = /^#([0-9a-f]{6})/i.exec(value)
  if (m) app.setAccent(`#${m[1]!.toUpperCase()}`)
}

// roving tabindex:方向键在预设之间走,Tab 只停在选中的那颗上
function onKey(e: KeyboardEvent) {
  const step =
    e.key === 'ArrowRight' || e.key === 'ArrowDown'
      ? 1
      : e.key === 'ArrowLeft' || e.key === 'ArrowUp'
        ? -1
        : 0
  if (!step || isCustom.value) return
  e.preventDefault()
  const next = ACCENTS[(presetIndex.value + step + ACCENTS.length) % ACCENTS.length]!
  app.setAccent(next)
  ;(e.currentTarget as HTMLElement).querySelector<HTMLElement>(`[data-color="${next}"]`)?.focus()
}
</script>

<template>
  <div class="accent-card">
    <div class="row" role="radiogroup" :aria-label="t('settings.themeColor')" @keydown="onKey">
      <n-tooltip v-for="(m, i) in ACCENT_META" :key="m.color" :delay="300">
        <template #trigger>
          <button
            type="button"
            role="radio"
            class="dot"
            :class="{ on: presetIndex === i }"
            :style="{ '--c': m.dot }"
            :data-color="m.color"
            :aria-checked="presetIndex === i"
            :aria-label="t(`settings.accentName.${m.key}`)"
            :tabindex="presetIndex === i || (isCustom && i === 0) ? 0 : -1"
            @click="app.setAccent(m.color)"
          >
            <Icon v-if="presetIndex === i" icon="ph:check-bold" :width="13" />
          </button>
        </template>
        {{ t(`settings.accentName.${m.key}`) }}
      </n-tooltip>

      <!-- 自定义:彩虹圆点盖着一个透明的取色触发器,点圆点就是点触发器,取色板由 Naive 自己弹 -->
      <n-tooltip :delay="300">
        <template #trigger>
          <span
            class="dot custom"
            :class="{ on: isCustom }"
            :style="isCustom ? { '--c': customDot } : undefined"
          >
            <Icon v-if="isCustom" icon="ph:check-bold" :width="13" />
            <n-color-picker
              class="picker"
              :value="app.accent"
              :modes="['hex']"
              :show-alpha="false"
              :swatches="[...ACCENTS]"
              :aria-label="t('settings.accentName.custom')"
              @update:value="setCustom"
            />
          </span>
        </template>
        {{ t('settings.accentName.custom') }}
      </n-tooltip>
    </div>

    <div class="name" aria-live="polite">
      <b>{{ currentName }}</b>
      <span class="hex">{{ app.accent.toUpperCase() }}</span>
    </div>

    <div class="demo" aria-hidden="true">
      <n-button type="primary" size="small" tabindex="-1">
        {{ t('settings.accentDemoButton') }}
      </n-button>
      <n-switch :value="true" size="small" />
      <span class="link">{{ t('settings.accentDemoLink') }}</span>
      <n-progress type="line" :percentage="64" :show-indicator="false" :height="6" class="bar" />
    </div>
  </div>
</template>

<style scoped>
.accent-card {
  padding: 14px 14px 12px;
  border-radius: 14px;
  background: var(--glass);
  box-shadow:
    inset 0 0 0 1px var(--hairline),
    inset 0 1px 0 var(--edge-light);
}
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

/* 圆点:顶左一抹高光 + 内描边,像一颗实物;悬停略放大;选中是「底色环 + 强调色环」两层,中间一个对勾 */
.dot {
  position: relative;
  display: grid;
  flex: none;
  place-items: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: none;
  border-radius: 50%;
  color: #fff;
  cursor: pointer;
  background:
    radial-gradient(circle at 30% 22%, rgb(255 255 255 / 0.38), transparent 58%), var(--c);
  box-shadow:
    inset 0 0 0 1px rgb(0 0 0 / 0.14),
    0 1px 2px rgb(0 0 0 / 0.18);
  transition:
    transform var(--transition-fast),
    box-shadow var(--transition-fast);
}
/* 色块悬停不放大(系统设置的强调色圆点同理),只在按下时略缩给出触感 */
.dot:active {
  transform: scale(0.96);
}
.dot.on {
  box-shadow:
    inset 0 0 0 1px rgb(0 0 0 / 0.14),
    0 0 0 2px var(--bg-elevated),
    0 0 0 4px var(--c);
}
.dot:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: 5px;
}
.dot :deep(svg) {
  filter: drop-shadow(0 1px 1px rgb(0 0 0 / 0.35));
}
@media (prefers-reduced-motion: reduce) {
  .dot {
    transition: none;
  }
  .dot:hover,
  .dot:active {
    transform: none;
  }
}

/* 彩虹圆点:没选中时是一圈色相,选中后底色换成当前自定义色(环和对勾同预设) */
.dot.custom {
  overflow: hidden;
  --c: #8e8e93;
  background:
    radial-gradient(circle at 30% 22%, rgb(255 255 255 / 0.38), transparent 58%),
    conic-gradient(#ff5e57, #ffb340, #f6e04a, #4cd964, #32c8d8, #4a8cff, #b25cf0, #ff5e57);
}
.dot.custom.on {
  background:
    radial-gradient(circle at 30% 22%, rgb(255 255 255 / 0.38), transparent 58%), var(--c);
}
/* 取色触发器盖在彩虹圆点上、完全透明:点圆点就是点它,取色板由 Naive 自己弹。
   它在 NTooltip 的 #trigger 插槽里渲染,拿不到本组件的 scoped 属性,所以必须走 :deep */
.dot.custom :deep(.n-color-picker) {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: pointer;
}

.name {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin: 12px 2px 0;
  font-size: var(--font-size-base);
  color: var(--text-2);
}
.name b {
  font-weight: 500;
  color: var(--text-1);
}
.hex {
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  color: var(--text-3);
}

/* 效果条:纯展示,不吃点击;背景是不透明实底,和抽屉里其它分组的玻璃底拉开层次 */
.demo {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-top: 12px;
  padding: 10px 12px;
  border-radius: var(--radius-md);
  background: var(--fill);
  pointer-events: none;
}
.link {
  font-size: var(--font-size-base);
  color: var(--sel-fg);
}
.bar {
  flex: 1;
  min-width: 40px;
}
</style>
