<script setup lang="ts">
// 缓存管理 = 定向失效动作卡片。只清不看:缓存值含明文验证码/一次性令牌、键内嵌手机号/IP(后端故无键浏览/取值端点)。
// 每张卡二次确认后执行,toast 显被清条数。正常授权变更后端已自动失效,此页面向"直接改库"等旁路场景做手动刷新。
import { computed, ref } from 'vue'
import { NCard, NButton, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import { useConfirm } from '#/composables/useConfirm'
import { cacheApi } from '#/api'
import { translateError } from '#/utils/error'

const { t } = useI18n()
const message = useMessage()
const { ask } = useConfirm()
const { bp } = useShellBreakpoint()
const narrow = computed(() => bp.value === 'narrow')

interface CacheAction {
  key: 'auth' | 'dict' | 'config' | 'portal'
  icon: string
  perm: string
  run: () => Promise<number>
}

const actions: CacheAction[] = [
  {
    key: 'auth',
    icon: 'ph:shield-check',
    perm: 'POST:/api/v1/sys/cache/flush-auth',
    run: cacheApi.flushAuth,
  },
  {
    key: 'dict',
    icon: 'ph:book-open-text',
    perm: 'POST:/api/v1/sys/cache/flush-dict',
    run: cacheApi.flushDict,
  },
  {
    key: 'config',
    icon: 'ph:sliders-horizontal',
    perm: 'POST:/api/v1/sys/cache/flush-config',
    run: cacheApi.flushConfig,
  },
  {
    key: 'portal',
    icon: 'ph:squares-four',
    perm: 'POST:/api/v1/sys/cache/rebuild-portal',
    run: cacheApi.rebuildPortal,
  },
]

const busy = ref<string | null>(null)

async function trigger(a: CacheAction) {
  if (!(await ask({ type: 'warning', content: t(`cache.${a.key}Confirm`) }))) return
  busy.value = a.key
  try {
    const n = await a.run()
    // 门户是"重建代际"语义(返回新代际值,非清除条数),单独文案;其余显被清条数
    message.success(a.key === 'portal' ? t('cache.portalDone') : t('cache.flushed', { n }))
  } catch (e) {
    message.error(translateError(e))
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <div class="cache">
    <!-- 两列;内容区窄档(< 600)一列 -->
    <div class="cards" :class="{ 'cards--narrow': narrow }">
      <n-card
        v-for="a in actions"
        :key="a.key"
        v-auth="a.perm"
        class="a-card action-card"
        content-style="padding: 16px 18px"
      >
        <div class="head">
          <span class="sym"><AppIcon :icon="a.icon" :size="16" /></span>
          <b class="title">{{ t(`cache.${a.key}Title`) }}</b>
        </div>
        <p class="desc">{{ t(`cache.${a.key}Desc`) }}</p>
        <div class="act">
          <n-button
            type="primary"
            secondary
            :size="narrow ? 'large' : 'medium'"
            :loading="busy === a.key"
            @click="trigger(a)"
          >
            <template #icon><AppIcon icon="ph:broom" :size="16" /></template>
            {{ t('cache.execute') }}
          </n-button>
        </div>
      </n-card>
    </div>
    <p class="note">{{ t('cache.note') }}</p>
  </div>
</template>

<style scoped>
.cache {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
/* 卡片等高,「执行」按钮用 margin-top:auto 沉到卡片底部 */
.cards {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}
.cards--narrow {
  grid-template-columns: minmax(0, 1fr);
}
.action-card {
  height: 100%;
}
.action-card :deep(.n-card__content) {
  display: flex;
  flex-direction: column;
  height: 100%;
}
.head {
  display: flex;
  align-items: center;
  gap: 10px;
}
/* 图标底块:中性玻璃 */
.sym {
  display: grid;
  place-items: center;
  flex-shrink: 0;
  width: 24px;
  height: 24px;
  border-radius: var(--radius-sm);
  color: var(--text-1);
  background: var(--glass-strong);
  box-shadow:
    inset 0 0 0 1px var(--hairline),
    inset 0 1px 0 var(--edge-light);
}
.title {
  font-size: 15px;
}
.desc {
  margin: 10px 0 0;
  min-height: 44px;
  font-size: 14px;
  line-height: 1.6;
  color: var(--text-2);
}
.act {
  margin-top: auto;
  padding-top: 14px;
}
.note {
  margin: 0;
  padding: 0 4px;
  font-size: 13px;
  line-height: 1.7;
  color: var(--text-3);
}
</style>
