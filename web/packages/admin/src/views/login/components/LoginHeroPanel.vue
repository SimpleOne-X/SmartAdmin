<script setup lang="ts">
// 登录页双栏左栏的品牌区(也是配置中心登录预览的左半边):文字直接压在柔光色场上,
// Logo 锁定 + 大标语(强调词用强调色渐变高亮)+ 一行说明 + 单行勾选亮点。
import { computed } from 'vue'
import { Icon } from '@iconify/vue'
import { useSite } from '#/composables/useSite'
import SmartLogo from '#/components/SmartLogo.vue'

const props = withDefaults(
  defineProps<{
    headline: string
    highlight?: string
    subtitle?: string
    features?: string[]
    /** 以下两项供配置中心预览未保存的草稿;不传则取当前站点信息 */
    title?: string
    logo?: string
  }>(),
  {
    highlight: '',
    subtitle: '',
    features: () => [],
    title: undefined,
    logo: undefined,
  },
)

const { site } = useSite()

// 只做纯文本切片,不用 v-html;强调词只会成为一个普通 span。
const headlineParts = computed(() => {
  const index = props.highlight ? props.headline.indexOf(props.highlight) : -1
  if (index < 0) return [{ text: props.headline, accent: false }]
  return [
    { text: props.headline.slice(0, index), accent: false },
    { text: props.highlight, accent: true },
    { text: props.headline.slice(index + props.highlight.length), accent: false },
  ].filter(part => part.text)
})
</script>

<template>
  <div class="login-hero-panel">
    <div class="logo">
      <SmartLogo :size="46" :src="logo" />
      <span>{{ title ?? site.title }}</span>
    </div>
    <h2 class="headline">
      <span
        v-for="(part, index) in headlineParts"
        :key="index"
        :class="{ hl: part.accent }"
        v-text="part.text"
      />
    </h2>
    <p v-if="subtitle" class="sub">{{ subtitle }}</p>
    <ul v-if="features.length" class="points">
      <li v-for="feature in features" :key="feature">
        <Icon icon="ph:check" :width="15" />
        {{ feature }}
      </li>
    </ul>
  </div>
</template>

<style scoped>
.login-hero-panel {
  min-width: 0;
}
.logo {
  display: flex;
  align-items: center;
  gap: 13px;
  margin-bottom: 30px;
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.015em;
  color: var(--text-1);
}
.headline {
  margin: 0 0 14px;
  color: var(--text-1);
  font-size: clamp(30px, 2.5vw, 40px);
  font-weight: 600;
  letter-spacing: -0.04em;
  line-height: 1.18;
}
.headline .hl {
  background: linear-gradient(110deg, var(--signal), var(--login-accent-2));
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
/* 色场上的正文一律用 --text-1:--text-2 是配白底卡片的,直接压在饱和色场上对比度不够 */
.sub {
  margin: 0 0 34px;
  color: var(--text-1);
  font-size: 17px;
  line-height: 1.6;
}
.points {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 16px;
}
/* 字重 500 + 16px:同样是 --text-1,靠字重和字号把亮点和上面那句说明分开,不靠深浅 */
.points li {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--text-1);
  font-size: 16px;
  font-weight: 500;
}
.points li > :deep(svg) {
  flex-shrink: 0;
  box-sizing: content-box;
  width: 15px;
  height: 15px;
  padding: 5px;
  border-radius: 50%;
  color: var(--signal);
  background: var(--sel-bg);
  box-shadow: inset 0 0 0 1px var(--sel-ring);
}
</style>
