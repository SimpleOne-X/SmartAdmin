<script setup lang="ts">
// 个人中心二级壳:主布局内容区内左侧子导航 + 右侧 RouterView。
// 路径仍为 /personal/*;强制改密时只露出「修改密码」导航项。
// 宽 / 中档子导航是左侧 200px 的卡片(n-menu);内容区 < 760 收成顶部横向分段控件(n-tabs segment)。
// 按「内容区」宽度判而不是视口:侧栏展开 / 收起会改变可用宽度,和壳层三档同一个口径。
import { computed, h } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NCard, NMenu, NTab, NTabs, type MenuOption } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import { useUserStore } from '#/stores/user'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const user = useUserStore()
const { width } = useShellBreakpoint()
// 子导航收成分段控件的内容区宽度上界(不含)
const COMPACT_MAX = 760
const isCompact = computed(() => width.value < COMPACT_MAX)

const allNav = [
  { key: '/personal/profile', labelKey: 'menu.profile', icon: 'ph:user-circle' },
  { key: '/personal/password', labelKey: 'menu.password', icon: 'ph:key' },
  { key: '/personal/security', labelKey: 'menu.security', icon: 'ph:shield-check' },
  { key: '/personal/sessions', labelKey: 'menu.sessions', icon: 'ph:devices' },
  { key: '/personal/bindings', labelKey: 'menu.bindings', icon: 'ph:link-simple' },
] as const

const mustChange = computed(() => !!user.userInfo?.mustChangePassword)

const navItems = computed(() => {
  const list = mustChange.value ? allNav.filter(x => x.key === '/personal/password') : [...allNav]
  return list.map(x => ({ key: x.key, label: t(x.labelKey), icon: x.icon }))
})

const activeKey = computed(() => {
  const hit = navItems.value.find(x => route.path === x.key || route.path.startsWith(x.key + '/'))
  return hit?.key ?? navItems.value[0]?.key ?? '/personal/profile'
})

// 子导航每项带线条图标;分段控件(窄档)只放文字
const menuOptions = computed<MenuOption[]>(() =>
  navItems.value.map(x => ({
    key: x.key,
    label: x.label,
    icon: () => h(Icon, { icon: x.icon, width: 18 }),
  })),
)

function go(key: string) {
  if (key !== route.path) void router.push(key)
}
</script>

<template>
  <div class="personal-layout" :class="{ 'is-compact': isCompact }">
    <n-card v-if="!isCompact" class="personal-sider" size="small" content-style="padding: 8px">
      <n-menu
        :value="activeKey"
        :options="menuOptions"
        :indent="18"
        @update:value="(k: string) => go(k)"
      />
    </n-card>
    <div class="personal-main">
      <n-tabs
        v-if="isCompact"
        type="segment"
        class="personal-tabs"
        :value="activeKey"
        @update:value="(k: string) => go(k)"
      >
        <n-tab v-for="o in menuOptions" :key="o.key as string" :name="o.key as string">
          {{ o.label }}
        </n-tab>
      </n-tabs>
      <div class="personal-content">
        <RouterView />
      </div>
    </div>
  </div>
</template>

<style scoped>
.personal-layout {
  display: flex;
  gap: var(--gap-card);
  align-items: stretch;
  min-height: 100%;
}
.personal-sider {
  width: 200px;
  flex-shrink: 0;
  align-self: flex-start;
}
.personal-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: var(--gap-card);
}
.personal-tabs {
  flex-shrink: 0;
}
.personal-content {
  min-width: 0;
}
.personal-layout.is-compact {
  flex-direction: column;
  gap: var(--gap-card);
}
</style>
