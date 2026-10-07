<script setup lang="ts">
// 登录页:macOS 锁屏语言的外壳(色场 + 毛玻璃卡,见 components/LoginDesk.vue)+ 表单(LoginForm.vue)。
// 布局两种:单栏(默认,居中一张卡)/ 双栏(内容区 ≥ 900 才可选:左栏品牌区,右栏卡)。
// 选择只存浏览器本地(localStorage 'sa-login-layout' = solo | split),不是后台配置;< 900 强制单栏且不显示切换按钮。
// 兼容皮肤 id(aurora / split / spotlight)的浏览器记忆与 ?skin= 一次性覆盖,
// 映射到最接近的布局:split → 双栏,aurora / spotlight 是居中一张卡 → 单栏。
import { computed, nextTick, ref } from 'vue'
import { NButton, NTooltip } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useMediaQuery } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { useAppStore } from '#/stores/app'
import { useLoginHero } from '#/composables/useLoginHero'
import { useSite } from '#/composables/useSite'
import LoginDesk from './components/LoginDesk.vue'
import LoginForm from './LoginForm.vue'
import LoginHeroPanel from './components/LoginHeroPanel.vue'
import { LAYOUT_KEY, resolveLoginLayout, type LoginLayout } from './layout'

const app = useAppStore()
const { t } = useI18n()
const { site, appVersion } = useSite()
const hero = useLoginHero()
const year = new Date().getFullYear()

const wide = useMediaQuery('(min-width: 900px)')
const layout = ref<LoginLayout>(resolveLoginLayout(window.location.search, localStorage))
const split = computed(() => layout.value === 'split' && wide.value)

// 登录成功后外壳播退场动画(LoginForm 通知)
const leaving = ref(false)

// 切换布局时给卡片挂 view-transition-name(见 LoginDesk 的全局样式),让它从居中平滑滑到右栏;
// 浏览器不支持 View Transitions 或系统开了「减少动态效果」就直接切。
const switching = ref(false)
function setLayout(next: LoginLayout) {
  if (layout.value === next) return
  const commit = () => {
    layout.value = next
    try {
      localStorage.setItem(LAYOUT_KEY, next)
    } catch {
      // 无痕窗口 / 站点数据被禁:这次照常切换,只是不记忆
    }
  }
  const doc = document as Document & {
    startViewTransition?: (update: () => Promise<void>) => { finished: Promise<void> }
  }
  if (!doc.startViewTransition || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
    commit()
    return
  }
  switching.value = true
  const transition = doc.startViewTransition(async () => {
    commit()
    await nextTick()
  })
  void transition.finished.finally(() => {
    switching.value = false
  })
}
</script>

<template>
  <LoginDesk
    :split="split"
    :leaving="leaving"
    :class="{ switching }"
    :width="400"
    content-style="padding: 26px 26px 22px"
  >
    <!-- 双栏左栏:品牌区(文案可在系统配置里改,空值回退内置文案) -->
    <template v-if="split" #brand>
      <LoginHeroPanel
        :headline="hero.headline"
        :highlight="hero.highlight"
        :subtitle="site.subtitle || t('login.subtitle')"
        :features="hero.features"
      />
    </template>

    <!-- 右上角工具栏:布局切换(仅内容区 ≥ 900 才出现)+ 语言;主题切换由外壳贴最右角 -->
    <template #corner>
      <!-- 与语言、主题同款的幽灵圆钮,不抢眼;图标显示「点了会切到的布局」,点击时图标旋转淡出再换成另一个 -->
      <n-tooltip v-if="wide">
        <template #trigger>
          <n-button
            quaternary
            circle
            data-testid="login-layout-toggle"
            :aria-label="t('login.layoutLabel')"
            @click="setLayout(split ? 'solo' : 'split')"
          >
            <template #icon>
              <Transition name="ico" mode="out-in">
                <Icon
                  :key="split ? 'solo' : 'split'"
                  :icon="split ? 'ph:rectangle' : 'ph:columns'"
                  :width="18"
                />
              </Transition>
            </template>
          </n-button>
        </template>
        {{ split ? t('login.layoutToSolo') : t('login.layoutToSplit') }}
      </n-tooltip>
      <n-tooltip>
        <template #trigger>
          <n-button
            quaternary
            circle
            :aria-label="t('app.language')"
            @click="app.setLocale(app.locale === 'zh-CN' ? 'en-US' : 'zh-CN')"
          >
            <span class="lang-text">{{ app.locale === 'zh-CN' ? '中' : 'EN' }}</span>
          </n-button>
        </template>
        {{ t('app.language') }}
      </n-tooltip>
    </template>

    <!-- 单栏:卡内顶部 logo + 站点标题 + 「登录」标题 + 页脚;双栏:卡内是问候语,页脚在卡外 -->
    <LoginForm
      :show-logo="!split"
      :show-title="!split"
      :show-greeting="split"
      :show-footer="!split"
      @leave="leaving = $event"
    />

    <template v-if="split" #after>
      <p class="foot">
        <span>
          © {{ year }}
          <a v-if="site.copyrightUrl" :href="site.copyrightUrl" target="_blank" rel="noopener">
            {{ site.copyright || site.title }}
          </a>
          <template v-else>{{ site.copyright || site.title }}</template>
        </span>
        <span v-if="appVersion" class="foot-ver">v{{ appVersion }}</span>
      </p>
    </template>
  </LoginDesk>
</template>

<style scoped>
/* 布局切换图标:旧图标缩小、微转、淡出,新图标从另一侧转回来;时长很短,不拖手 */
.ico-enter-active,
.ico-leave-active {
  transition:
    opacity 0.16s var(--ease),
    transform 0.2s var(--ease);
}
.ico-leave-to {
  opacity: 0;
  transform: rotate(-70deg) scale(0.6);
}
.ico-enter-from {
  opacity: 0;
  transform: rotate(70deg) scale(0.6);
}
.lang-text {
  font-size: 13px;
  font-weight: 600;
  line-height: 1;
}
.foot {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  margin: 22px 0 0;
  font-size: 13px;
  color: var(--text-3);
}
.foot a {
  color: inherit;
  text-decoration: none;
}
.foot a:hover {
  color: var(--signal);
}
.foot-ver {
  opacity: 0.75;
}
</style>
