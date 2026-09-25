<script setup lang="ts">
import { computed } from 'vue'
import { Icon } from '@iconify/vue'
import { useAppStore } from '#/stores/app'
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

const app = useAppStore()
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
      <SmartLogo :size="34" :src="logo" />
      <span>{{ title ?? site.title }}</span>
    </div>
    <div class="bar" :style="{ background: app.accent }" />
    <h1 class="headline">
      <span
        v-for="(part, index) in headlineParts"
        :key="index"
        :style="part.accent ? { color: app.accent } : undefined"
      >
        {{ part.text }}
      </span>
    </h1>
    <p v-if="subtitle" class="sub">{{ subtitle }}</p>
    <ul v-if="features.length" class="points">
      <li v-for="feature in features" :key="feature">
        <Icon icon="ph:check-circle-duotone" :width="18" :style="{ color: app.accent }" />
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
  gap: 12px;
  font-size: 22px;
  font-weight: 700;
  color: #2a3c82;
}
.bar {
  width: 40px;
  height: 3px;
  border-radius: 2px;
  margin: 28px 0 20px;
}
.headline {
  font-size: 42px;
  line-height: 1.2;
  font-weight: 800;
  color: var(--color-text-primary);
  margin: 0;
}
.sub {
  color: var(--color-text-secondary);
  margin: 16px 0 28px;
}
.points {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.points li {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--color-text-secondary);
}
</style>
