<script setup lang="ts">
// 入口类全屏页(登录 / 二次验证 / 第三方回调 / 门户加载失败)共用的外壳:macOS 锁屏语言。
// 整页一张慢慢飘的柔光色场(.desk,颜色全由强调色推导,无网格)+ 浮在上面的毛玻璃卡,右上角是工具栏。
// 单栏 = 居中一张卡;双栏(split)= 左栏品牌区(brand 插槽)压在色场上,右栏是这张卡。
// 只管视觉外壳:卡里放什么、表单怎么提交都在各页自己手里。
//
// 动效(全部不回弹):入场按「卡 → 标题 → 字段 → 按钮」依次浮现(先模糊后清晰);
// 色场与卡跟着指针做反向视差;leaving 为真时整页淡出放大一点(像 macOS 解锁),给登录成功收尾。
import { onBeforeUnmount, ref } from 'vue'
import { NButton, NCard, NTooltip } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useAppStore } from '#/stores/app'

withDefaults(
  defineProps<{
    /** 双栏:左栏渲染 brand 插槽,卡片宽度收到 384 */
    split?: boolean
    /** 卡片宽度(px),窄屏被 max-width:100% 收住 */
    width?: number
    /** NCard 内容区内边距 */
    contentStyle?: string
    /** 退场:登录成功后由页面置真,整页淡出,等动画播完再跳转 */
    leaving?: boolean
  }>(),
  { split: false, width: 400, contentStyle: 'padding: 26px 26px 22px', leaving: false },
)

const app = useAppStore()
const { t } = useI18n()

// 指针视差:只认鼠标(触屏没有悬停位置),坐标归一到 -1..1 写成 CSS 变量,位移与过渡都在 CSS 里。
// 另外把指针相对卡片左上角的位置(--mx / --my,px)和「指针在窗口内」(--lit)写出来,
// 给卡片面上那束跟着指针走的柔光用:指针停在卡片上时视差位移趋近于零,光斑是这时唯一的反馈。
const root = ref<HTMLElement | null>(null)
let raf = 0
function setPointer(x: number, y: number, clientX?: number, clientY?: number) {
  cancelAnimationFrame(raf)
  raf = requestAnimationFrame(() => {
    const el = root.value
    if (!el) return
    el.style.setProperty('--px', x.toFixed(3))
    el.style.setProperty('--py', y.toFixed(3))
    if (clientX === undefined || clientY === undefined) {
      // 指针离开:只熄灭柔光,保留最后位置,淡出时光斑不跳
      el.style.setProperty('--lit', '0')
      return
    }
    const card = el.querySelector('.desk-card')?.getBoundingClientRect()
    if (card) {
      el.style.setProperty('--mx', `${(clientX - card.left).toFixed(1)}px`)
      el.style.setProperty('--my', `${(clientY - card.top).toFixed(1)}px`)
    }
    el.style.setProperty('--lit', '1')
  })
}
function onPointerMove(e: PointerEvent) {
  if (e.pointerType !== 'mouse') return
  setPointer(
    (e.clientX / window.innerWidth) * 2 - 1,
    (e.clientY / window.innerHeight) * 2 - 1,
    e.clientX,
    e.clientY,
  )
}
onBeforeUnmount(() => cancelAnimationFrame(raf))
</script>

<template>
  <div
    ref="root"
    class="login desk-bg"
    :class="{ split, leaving }"
    @pointermove="onPointerMove"
    @pointerleave="setPointer(0, 0)"
  >
    <div class="desk" aria-hidden="true">
      <i />
      <i />
      <i />
      <i />
    </div>

    <div v-if="split" class="login-brand">
      <div class="login-brand-inner"><slot name="brand" /></div>
    </div>

    <div class="login-stage">
      <!-- 右上角工具栏:页面自己的按钮(布局切换、语言)在前,主题切换永远贴最右角 -->
      <div class="login-corner">
        <slot name="corner" />
        <n-tooltip>
          <template #trigger>
            <n-button
              quaternary
              circle
              :aria-label="app.isDark ? t('app.light') : t('app.dark')"
              @click="app.toggleDark()"
            >
              <template #icon>
                <Icon :icon="app.isDark ? 'ph:moon' : 'ph:sun'" :width="18" />
              </template>
            </n-button>
          </template>
          {{ app.isDark ? t('app.light') : t('app.dark') }}
        </n-tooltip>
      </div>

      <!-- margin-top 给右上角工具栏留位:窄屏内容比视口高时,顶部不会被工具栏盖住 -->
      <div
        class="login-card"
        :class="{ 'on-desk': split }"
        :style="split ? undefined : { width: `${width}px` }"
      >
        <slot name="above" />
        <n-card :bordered="true" class="desk-card" :content-style="contentStyle">
          <slot />
        </n-card>
        <slot name="after" />
      </div>
    </div>
  </div>
</template>

<style scoped>
/* 登录页(单栏 / 双栏都算)是 macOS 桌面那种纯色场:不要网格线,不要透视地面 */
.login {
  /* 减速到底、不过冲:入场与视差共用这条曲线 */
  --rise: cubic-bezier(0.22, 1, 0.36, 1);
  min-height: 100dvh;
  background: var(--desk-base);
  position: relative;
  overflow: hidden;
}
.login.split {
  display: grid;
  grid-template-columns: minmax(340px, 46%) 1fr;
}
.login-stage {
  min-height: 100dvh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  position: relative;
  overflow: hidden;
}

/* 色场:四团 blur(70px) 的柔光球慢速漂移,颜色由强调色推导(--desk-1..4);
   整体再按指针反向平移一点(用独立的 translate 属性,不和漂移动画的 transform 打架) */
.desk {
  position: absolute;
  inset: -20%;
  pointer-events: none;
  z-index: 0;
  translate: calc(var(--px, 0) * -44px) calc(var(--py, 0) * -30px);
  transition: translate 1.2s var(--rise);
}
.desk i {
  position: absolute;
  display: block;
  border-radius: 50%;
  filter: blur(70px);
  opacity: var(--desk-op);
}
.desk i:nth-child(1) {
  left: 2%;
  top: 4%;
  width: 46%;
  height: 62%;
  background: var(--desk-1);
  animation: drift1 26s ease-in-out infinite alternate;
}
.desk i:nth-child(2) {
  left: 26%;
  top: 34%;
  width: 44%;
  height: 56%;
  background: var(--desk-2);
  animation: drift2 32s ease-in-out infinite alternate;
}
.desk i:nth-child(3) {
  left: 54%;
  top: -6%;
  width: 40%;
  height: 58%;
  background: var(--desk-3);
  animation: drift3 29s ease-in-out infinite alternate;
}
.desk i:nth-child(4) {
  left: 62%;
  top: 46%;
  width: 46%;
  height: 62%;
  background: var(--desk-4);
  animation: drift1 35s ease-in-out infinite alternate-reverse;
}
@keyframes drift1 {
  from {
    transform: none;
  }
  to {
    transform: translate3d(6%, -5%, 0) scale(1.12);
  }
}
@keyframes drift2 {
  from {
    transform: none;
  }
  to {
    transform: translate3d(-7%, 4%, 0) scale(1.08);
  }
}
@keyframes drift3 {
  from {
    transform: none;
  }
  to {
    transform: translate3d(4%, 7%, 0) scale(1.15);
  }
}

.login-corner {
  position: absolute;
  top: 16px;
  right: 16px;
  z-index: 3;
  display: flex;
  gap: 8px;
  animation: fadeIn 0.9s var(--rise) 0.7s both;
}

/* 双栏左栏:文字直接压在色场上(像 macOS 桌面上的字),不单独做一块彩色面板 */
.login-brand {
  position: relative;
  z-index: 1;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 48px clamp(32px, 5vw, 76px);
}
.login-brand-inner {
  max-width: 440px;
}

.login-card {
  width: 400px;
  max-width: 100%;
  position: relative;
  z-index: 2;
  margin-top: 44px;
}
.login-card.on-desk {
  width: 384px;
}

/* 表单是一块浮在色场上的毛玻璃卡(macOS 的 vibrancy 材质),单栏双栏同一套 */
.desk-card {
  border: 1px solid var(--glass-border) !important;
  border-radius: 22px !important;
  background: var(--glass-panel) !important;
  box-shadow: var(--glass-shadow) !important;
  backdrop-filter: blur(40px) saturate(180%);
  -webkit-backdrop-filter: blur(40px) saturate(180%);
  /* 反向视差:卡朝指针方向挪,色场反方向挪,拉出前后景深。
     卡的跟随比色场快(0.45s 对 1.2s),指针一动卡就有回应;位移在窗口边缘最大,
     指针停在卡片上时趋近于零,不会让要点的按钮在手下漂走 */
  translate: calc(var(--px, 0) * 16px) calc(var(--py, 0) * 11px);
  transition:
    translate 0.45s var(--rise),
    background-color 0.3s,
    border-color 0.3s,
    box-shadow 0.3s;
  isolation: isolate;
}
/* 跟着指针走的柔光:画在毛玻璃面上、内容的下面(isolation + z-index:-1),强调色推导,亮暗主题通用。
   指针不在窗口内时淡出;--mx / --my 是指针相对卡片左上角的位置(见脚本) */
.desk-card::after {
  content: '';
  position: absolute;
  inset: 0;
  z-index: -1;
  border-radius: inherit;
  pointer-events: none;
  background: radial-gradient(
    340px circle at var(--mx, 50%) var(--my, 0%),
    color-mix(in srgb, var(--signal) var(--spot, 28%), transparent),
    transparent 72%
  );
  opacity: var(--lit, 0);
  transition: opacity 0.5s var(--rise);
}
/* 亮色的玻璃底本来就亮,同样的强调色占比要给高一点才看得出;暗色里 20% 已经够醒目 */
:root[data-theme='dark'] .desk-card {
  --spot: 20%;
}
.login-card :deep(.n-input) {
  border-radius: var(--radius-md);
}
/* 主按钮:强调色 → 第二色渐变,带一层强调色辉光;hover 只加深辉光并微提亮、不位移,按下轻轻收一下 */
.login-card :deep(.n-button.cta) {
  background-image: linear-gradient(120deg, var(--signal), var(--login-accent-2));
  border-color: transparent;
  box-shadow: 0 10px 26px -10px var(--signal-glow);
  transition:
    box-shadow 0.22s var(--ease),
    transform 0.2s var(--ease),
    filter 0.2s var(--ease);
}
.login-card :deep(.n-button.cta:hover) {
  box-shadow: 0 16px 34px -10px var(--signal-glow);
  filter: brightness(1.04);
}
.login-card :deep(.n-button.cta:active:not(:disabled)) {
  transform: scale(0.985);
  filter: brightness(0.97);
  box-shadow: 0 6px 18px -10px var(--signal-glow);
}

/* ── 入场编排 ─────────────────────────────────────────────
   动画必须挂在 .desk-card 自己身上,不能挂它的父级 .login-card:父级 opacity < 1 会让子元素的
   backdrop-filter 失去背景,动画期间卡是不模糊的,结束那一帧模糊才「啪」地打上去,看着就是生硬。
   内容项(标题 / 字段 / 按钮 / 左栏文字)各自带延迟依次浮现,先模糊后清晰,不回弹。 */
.desk-card {
  animation: cardIn 0.95s var(--rise) 0.08s both;
}
.login-card > :deep(:not(.desk-card)) {
  animation: fadeIn 0.9s var(--rise) 0.55s both;
}

/* 左栏:Logo → 标语 → 说明 → 亮点逐行 */
.login-brand-inner :deep(.login-hero-panel > :not(.points)),
.login-brand-inner :deep(.points li) {
  animation: riseIn 0.9s var(--rise) both;
}
.login-brand-inner :deep(.login-hero-panel > :nth-child(1)) {
  animation-delay: 0.1s;
}
.login-brand-inner :deep(.login-hero-panel > :nth-child(2)) {
  animation-delay: 0.18s;
}
.login-brand-inner :deep(.login-hero-panel > :nth-child(3)) {
  animation-delay: 0.26s;
}
.login-brand-inner :deep(.points li:nth-child(1)) {
  animation-delay: 0.36s;
}
.login-brand-inner :deep(.points li:nth-child(2)) {
  animation-delay: 0.43s;
}
.login-brand-inner :deep(.points li:nth-child(n + 3)) {
  animation-delay: 0.5s;
}

/* 卡内:LoginForm 的 .login-form > .lf-brand / .lf-body(标题、表单、第三方登录)和表单里的各行 */
.login-card :deep(.login-form > .lf-brand),
.login-card :deep(.login-form > .lf-pending-alert),
.login-card :deep(.lf-body > :not(form)),
.login-card :deep(.lf-body form > *) {
  animation: riseIn 0.8s var(--rise) both;
}
.login-card :deep(.login-form > .lf-brand),
.login-card :deep(.login-form > .lf-pending-alert) {
  animation-delay: 0.18s;
}
.login-card :deep(.lf-body > :not(form)) {
  animation-delay: 0.22s;
}
.login-card :deep(.lf-body form > :nth-child(1)) {
  animation-delay: 0.28s;
}
.login-card :deep(.lf-body form > :nth-child(2)) {
  animation-delay: 0.34s;
}
.login-card :deep(.lf-body form > :nth-child(3)) {
  animation-delay: 0.4s;
}
.login-card :deep(.lf-body form > :nth-child(4)) {
  animation-delay: 0.46s;
}
.login-card :deep(.lf-body form > :nth-child(n + 5)) {
  animation-delay: 0.52s;
}
.login-card :deep(.lf-body > .lf-divider) {
  animation-delay: 0.56s;
}
.login-card :deep(.lf-body > .lf-sso) {
  animation-delay: 0.62s;
}

@keyframes cardIn {
  from {
    opacity: 0;
    transform: translate3d(0, 28px, 0) scale(0.965);
  }
  to {
    opacity: 1;
    transform: none;
  }
}
@keyframes riseIn {
  from {
    opacity: 0;
    transform: translate3d(0, 12px, 0);
    filter: blur(6px);
  }
  to {
    opacity: 1;
    transform: none;
    filter: none;
  }
}
@keyframes fadeIn {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

/* ── 退场:登录成功后整页淡出并微微放大(macOS 解锁的感觉),色场留着不动 ── */
.login.leaving .desk-card {
  animation: cardOut 0.34s cubic-bezier(0.4, 0, 1, 1) both;
}
.login.leaving .login-brand-inner,
.login.leaving .login-corner,
.login.leaving .login-card > :deep(:not(.desk-card)) {
  animation: fadeOut 0.28s ease-in both;
}
@keyframes cardOut {
  to {
    opacity: 0;
    transform: translate3d(0, -8px, 0) scale(1.02);
  }
}
@keyframes fadeOut {
  to {
    opacity: 0;
  }
}

/* 减少动效:全局规则已把时长压到近零,这里再清掉延迟(否则 both 的起始态会把内容藏到延迟结束)并取消视差与漂移 */
@media (prefers-reduced-motion: reduce) {
  .desk i {
    animation: none !important;
  }
  .desk,
  .desk-card {
    translate: none;
  }
  .desk-card::after {
    display: none;
  }
  .login :deep(*),
  .login .desk-card,
  .login .login-corner {
    animation-delay: 0s !important;
  }
}
/* 窄屏收紧:卡片侧边留 16px,卡内边距随之压小(内联的 content-style 要靠 !important 盖住) */
@media (max-width: 480px) {
  .login-stage {
    padding: 16px;
  }
  .desk-card :deep(.n-card__content) {
    padding: 22px 20px 18px !important;
  }
}
</style>

<!-- View Transitions 的伪元素在文档根上,scoped 样式够不到,所以单独一块全局样式。
     只在布局切换那一刻(.switching)给卡片命名:常驻的 view-transition-name 会让它多出独立图层。 -->
<style>
.login.switching .desk-card {
  view-transition-name: login-card;
}
::view-transition-group(login-card) {
  animation-duration: 0.62s;
  animation-timing-function: cubic-bezier(0.22, 1, 0.36, 1);
}
</style>
