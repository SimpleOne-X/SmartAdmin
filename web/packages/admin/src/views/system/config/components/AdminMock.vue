<script setup lang="ts">
// 缩小版后台(880×540 设计尺寸,外面 ScaleToFit 等比缩放):浏览器标签页、侧栏品牌位、页脚版权,
// 都读草稿,改一项对应位置即时变并闪一下。
import { computed } from 'vue'
import SmartLogo from '#/components/SmartLogo.vue'
import { LOGO_KEY } from '../groups'
import { useConfigDraft } from '../draft'
import { vFlash } from '../flash'

const { values } = useConfigDraft()
const title = computed(() => values['sys.site.title']?.trim() || 'SmartAdmin')
// 版权留空时和登录页页脚一样回退到站点名
const copyright = computed(() => values['sys.site.copyright']?.trim() || title.value)
const hasUrl = computed(() => !!values['sys.site.copyrightUrl']?.trim())
const logo = computed(() => values[LOGO_KEY] ?? '')
</script>

<template>
  <div class="mock">
    <div class="chrome">
      <span class="lights">
        <i />
        <i />
        <i />
      </span>
      <span v-flash="`${title}|${logo}`" class="btab">
        <SmartLogo :size="16" :src="logo" />
        <span>{{ title }}</span>
      </span>
    </div>
    <div class="app">
      <div class="side">
        <div v-flash="`${title}|${logo}`" class="brand" data-testid="preview-brand">
          <SmartLogo :size="30" :src="logo" />
          <span>{{ title }}</span>
        </div>
        <i v-for="n in 5" :key="n" :style="{ width: n % 2 ? '80%' : '62%' }" />
      </div>
      <div class="main">
        <div class="top" />
        <div class="body">
          <i class="w" />
          <i />
          <i />
          <i />
        </div>
        <div v-flash="`${copyright}|${hasUrl}`" class="foot">
          <a v-if="hasUrl">© {{ copyright }}</a>
          <span v-else>© {{ copyright }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.mock {
  display: flex;
  flex-direction: column;
  width: 880px;
  height: 540px;
  overflow: hidden;
  border-radius: 10px;
  background: var(--color-bg-container);
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.12);
  font-size: 14px;
}
.chrome {
  display: flex;
  flex: none;
  align-items: flex-end;
  gap: 10px;
  height: 38px;
  padding: 0 12px;
  background: var(--color-fill);
}
.lights {
  display: flex;
  align-self: center;
  gap: 6px;
}
.lights i {
  width: 11px;
  height: 11px;
  border-radius: 50%;
  background: #ff5f57;
}
.lights i:nth-child(2) {
  background: #febc2e;
}
.lights i:nth-child(3) {
  background: #28c840;
}
.btab {
  display: flex;
  align-items: center;
  gap: 7px;
  max-width: 260px;
  height: 30px;
  padding: 0 14px;
  border-radius: 8px 8px 0 0;
  background: var(--color-bg-container);
  font-size: 12.5px;
}
.btab span {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.app {
  display: grid;
  flex: 1;
  grid-template-columns: 196px 1fr;
  min-height: 0;
}
.side {
  padding: 16px 12px;
  border-right: 1px solid var(--color-border);
}
.brand {
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 4px 6px;
  margin-bottom: 14px;
  overflow: hidden;
  border-radius: 8px;
  font-size: 15px;
  font-weight: 600;
  white-space: nowrap;
}
.side i {
  display: block;
  height: 10px;
  margin: 16px 8px;
  border-radius: 5px;
  background: var(--color-fill);
}
.main {
  display: flex;
  flex-direction: column;
  background: var(--color-bg-body);
}
.top {
  height: 48px;
  border-bottom: 1px solid var(--color-border);
  background: var(--color-bg-container);
}
.body {
  display: grid;
  flex: 1;
  grid-template-columns: repeat(3, 1fr);
  grid-auto-rows: 90px;
  gap: 14px;
  padding: 20px;
}
.body i {
  border-radius: 10px;
  background: var(--color-bg-container);
}
.body i.w {
  grid-column: span 3;
  grid-row: span 2;
}
.foot {
  display: grid;
  place-items: center;
  height: 42px;
  border-radius: 6px;
  font-size: 12.5px;
  color: var(--color-text-tertiary);
}
.foot a {
  color: var(--color-primary);
}
</style>
