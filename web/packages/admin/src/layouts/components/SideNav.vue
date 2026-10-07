<script setup lang="ts">
// 侧栏薄壳:对 n-menu 的展示层封装,default.vue 复用于 4 处——完整树/二级子树/一级图标 rail/移动抽屉。
// collapsed(用户折叠开关) 与 rail(一级细栏,恒图标态、固定窄宽、无品牌字) 是两条独立的"收窄"来源,
// 二者任一为真都令 n-menu 进入 collapsed;rail 另用更窄的 collapsed-width。
// 玻璃底由外层 aside / 抽屉提供,这里保持透明,不自带底色(否则两层半透明叠出一块灰)。
import { computed } from 'vue'
import { NInput, NMenu, NScrollbar, type MenuOption } from 'naive-ui'
import { Icon } from '@iconify/vue'
import { useI18n } from 'vue-i18n'
import SmartLogo from '#/components/SmartLogo.vue'
import { useSite } from '#/composables/useSite'

const { site } = useSite()
const { t } = useI18n()

const props = defineProps<{
  options: MenuOption[]
  value?: string
  collapsed?: boolean
  rail?: boolean // 细图标栏(一级),强制图标态、无品牌文字
  showBrand?: boolean
  showSearch?: boolean // 品牌下方的菜单搜索入口(只读输入框,点开 Ctrl+K 同一个面板)
}>()
const emit = defineEmits<{ select: [key: string]; search: [] }>()

const iconOnly = computed(() => props.collapsed || props.rail)
</script>

<template>
  <div class="sidenav" :class="{ rail: props.rail, 'icon-only': iconOnly }">
    <div v-if="props.showBrand" class="brand">
      <SmartLogo :size="28" />
      <span v-show="!iconOnly" class="brand-name">
        {{ site.title }}
      </span>
    </div>
    <div v-if="props.showSearch && !iconOnly" class="side-search">
      <n-input
        size="small"
        readonly
        class="search-input"
        :placeholder="t('app.search')"
        :aria-label="t('app.search')"
        @click="emit('search')"
        @keydown.enter="emit('search')"
      >
        <template #prefix>
          <Icon icon="ph:magnifying-glass" :width="15" class="search-ico" />
        </template>
      </n-input>
    </div>
    <n-scrollbar class="side-menu">
      <n-menu
        :options="props.options"
        :value="props.value"
        :collapsed="iconOnly"
        :collapsed-width="props.rail ? 64 : 68"
        :collapsed-icon-size="20"
        :icon-size="18"
        :indent="14"
        :root-indent="14"
        @update:value="(k: string) => emit('select', k)"
      />
    </n-scrollbar>
  </div>
</template>

<style scoped>
.sidenav {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: transparent;
  overflow: hidden;
}
.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 50px;
  padding: 0 18px;
  color: var(--text-1);
  overflow: hidden;
  white-space: nowrap;
  flex-shrink: 0;
}
.icon-only .brand {
  padding: 0;
  justify-content: center;
}
.brand-name {
  min-width: 0;
  font-size: 15px;
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.2;
  color: var(--text-1); /* 品牌文字跟随主题文字色:固定深色在暗色下看不清 */
}
.side-search {
  flex-shrink: 0;
  padding: 0 12px 12px;
}
.search-input {
  cursor: pointer;
}
/* 只读输入框:光标也是手型,点哪都能打开面板 */
.search-input :deep(.n-input__input-el) {
  cursor: pointer;
}
.search-ico {
  color: var(--text-3);
}

.side-menu {
  flex: 1;
  min-height: 0;
  /* 菜单滚到底时最后一行淡出,不被圆角面板的下沿硬切 */
  -webkit-mask-image: linear-gradient(180deg, #000 calc(100% - 22px), transparent);
  mask-image: linear-gradient(180deg, #000 calc(100% - 22px), transparent);
}
/* 选中项只靠行底色 + 强调色文字 / 图标 + 加粗到 600 表达(与横向页签栏的选中页签同一字重),不画左侧竖条 */
.sidenav :deep(.n-menu-item-content--selected .n-menu-item-content-header) {
  font-weight: 600;
}
</style>
