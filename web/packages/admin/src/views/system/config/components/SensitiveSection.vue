<script setup lang="ts">
// 敏感操作:执行这些接口前要求用户再次验证身份(高敏感权限)。内核内置的只读列出,不能删;
// 消费者可以追加自己的业务接口。增删直接调 highSensApi 即时生效,不走底部保存条
// (写路径本身要求近期再认证,由后端强制)。
// 预览是用户执行选中那一行时弹出的验证框,文案与 ReauthModal 一致。
import { computed, onMounted, ref } from 'vue'
import { NButton, NInput, NPopconfirm, NSpin, useMessage } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { highSensApi } from '#/api'
import { translateError } from '#/utils/error'
import { vFlash } from '../flash'
import { useConfigLayout } from '../layoutMode'
import SectionLayout from './SectionLayout.vue'

interface Row {
  code: string
  method: string
  path: string
  remark?: string | null
  /** 自定义项才有 id,可删除 */
  id?: number
}

const { t } = useI18n()
const message = useMessage()
const { ctl, nar, side } = useConfigLayout()
const loading = ref(false)
const saving = ref(false)
const rows = ref<Row[]>([])
const selected = ref(0)
const newCode = ref('')
const newRemark = ref('')
const list = ref<HTMLElement | null>(null)

/** 权限码是规范化路由 {METHOD}:/{route},拆成方法与路径分开显示 */
function toRow(code: string, extra: Partial<Row> = {}): Row {
  const i = code.indexOf(':')
  return i > 0
    ? { code, method: code.slice(0, i).toUpperCase(), path: code.slice(i + 1), ...extra }
    : { code, method: '', path: code, ...extra }
}

async function load() {
  loading.value = true
  try {
    const data = await highSensApi.list()
    rows.value = [
      ...(data.defaults ?? []).map(code => toRow(code)),
      ...(data.customs ?? []).flatMap(item =>
        item.id == null
          ? []
          : [toRow(item.permissionCode ?? '', { id: Number(item.id), remark: item.remark })],
      ),
    ]
    selected.value = Math.min(selected.value, Math.max(rows.value.length - 1, 0))
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}

async function add() {
  const code = newCode.value.trim()
  if (!code) {
    message.warning(t('config.security.highSens.codeRequired'))
    return
  }
  saving.value = true
  try {
    await highSensApi.add({ permissionCode: code, remark: newRemark.value.trim() || undefined })
    message.success(t('config.sensitive.added'))
    newCode.value = ''
    newRemark.value = ''
    await load()
    selected.value = rows.value.findIndex(r => r.code === code)
    list.value?.scrollTo({ top: list.value.scrollHeight })
  } catch (e) {
    message.error(translateError(e))
  } finally {
    saving.value = false
  }
}

async function remove(id: number) {
  saving.value = true
  try {
    await highSensApi.remove(id)
    message.success(t('config.sensitive.removed'))
    await load()
  } catch (e) {
    message.error(translateError(e))
  } finally {
    saving.value = false
  }
}

const current = computed(() => rows.value[selected.value])

onMounted(() => void load())
</script>

<template>
  <SectionLayout
    :title="t('config.tab.sensitive')"
    :desc="t('config.sensitive.desc')"
    :preview-title="t('config.sensitive.preview')"
    fixed-form
  >
    <div :class="['tbl', { nar, side }]" data-testid="sensitive-table">
      <div class="th" aria-hidden="true">
        <span>{{ t('config.sensitive.method') }}</span>
        <span>{{ t('config.sensitive.path') }}</span>
        <span class="rm">{{ t('config.security.highSens.remark') }}</span>
        <span />
      </div>
      <n-spin :show="loading" class="spin">
        <ul ref="list" class="tb" role="listbox" :aria-label="t('config.tab.sensitive')">
          <li
            v-for="(r, i) in rows"
            :key="r.code"
            role="option"
            :aria-selected="i === selected"
            :class="['tr', { sel: i === selected }]"
            tabindex="0"
            @click="selected = i"
            @keydown.enter.space.prevent="selected = i"
          >
            <span :class="['m', r.method]">{{ r.method || '—' }}</span>
            <span class="pt">
              <code :title="r.path">{{ r.path }}</code>
              <small class="narsub">{{ r.remark || '' }}</small>
            </span>
            <span class="remark rm">{{ r.remark || '—' }}</span>
            <span v-if="r.id == null" class="badge">{{ t('config.sensitive.builtin') }}</span>
            <n-popconfirm v-else @positive-click="remove(r.id)">
              <template #trigger>
                <n-button size="tiny" quaternary type="error" :disabled="saving" @click.stop>
                  {{ t('common.delete') }}
                </n-button>
              </template>
              {{ t('config.sensitive.removeConfirm', { code: r.code }) }}
            </n-popconfirm>
          </li>
        </ul>
      </n-spin>
      <form class="add" @submit.prevent="add">
        <n-input
          v-model:value="newCode"
          :size="ctl"
          :placeholder="t('config.security.highSens.codePh')"
          :input-props="{ 'aria-label': t('config.security.highSens.code') }"
        />
        <n-input
          v-model:value="newRemark"
          :size="ctl"
          class="rmi"
          :placeholder="t('config.security.highSens.remarkPh')"
          :input-props="{ 'aria-label': t('config.security.highSens.remark') }"
        />
        <n-button type="primary" :size="ctl" attr-type="submit" :loading="saving">
          {{ t('common.add') }}
        </n-button>
      </form>
    </div>

    <template #preview-extra>
      <span>{{ t('config.sensitive.previewTip') }}</span>
    </template>
    <template #preview>
      <div v-if="current" v-flash="current.code" class="dlg" aria-hidden="true">
        <div class="lock"><AppIcon icon="ph:lock-key" :size="24" /></div>
        <b>{{ t('reauth.title') }}</b>
        <p>
          {{ t('config.sensitive.previewDoing') }}
          <code>{{ current.code }}</code>
        </p>
        <p class="hint">{{ t('reauth.hint') }}</p>
        <i class="in">{{ t('reauth.passwordPlaceholder') }}</i>
        <div class="acts">
          <span>{{ t('common.cancel') }}</span>
          <span class="p">{{ t('reauth.confirm') }}</span>
        </div>
      </div>
    </template>
  </SectionLayout>
</template>

<style scoped>
.tbl {
  display: flex;
  flex: 1 1 0;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  border-radius: var(--radius-lg);
  background: var(--fill);
}
/* 中档 / 窄档设置列不定高,列表卡片给一个固定高度,行多了在卡片里滚 */
.tbl:not(.side) {
  flex: none;
  height: 440px;
}
.th,
.tr {
  display: grid;
  grid-template-columns: 70px minmax(0, 1fr) 120px 64px;
  gap: 10px;
  align-items: center;
  min-height: 38px;
  padding: 0 14px;
}
.th {
  flex: none;
  font-size: 13px;
  color: var(--text-2);
  border-bottom: 1px solid var(--separator);
}
.spin {
  flex: 1 1 0;
  min-height: 0;
}
.spin :deep(.n-spin-content) {
  height: 100%;
}
.tb {
  height: 100%;
  margin: 0;
  padding: 0;
  overflow: auto;
  list-style: none;
}
.tr {
  position: relative;
  font-size: 14px;
  cursor: pointer;
}
.tr + .tr::before {
  content: '';
  position: absolute;
  top: 0;
  left: 14px;
  right: 0;
  border-top: 1px solid var(--separator);
}
.tr:hover {
  background: var(--hover);
}
.tr.sel {
  background: var(--sel-bg);
  box-shadow: inset 0 0 0 1px var(--sel-ring);
}
.tr:focus-visible {
  outline: 2px solid var(--signal);
  outline-offset: -2px;
}
.m {
  font-family: var(--font-mono);
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-2);
}
.m.DELETE {
  color: var(--err);
}
.m.POST {
  color: var(--ok);
}
.m.PUT {
  color: var(--warn);
}
.pt {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
code {
  overflow: hidden;
  font-family: var(--font-mono);
  font-size: 13px;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.narsub {
  display: none;
  font-size: 13px;
  color: var(--text-3);
}
.remark {
  overflow: hidden;
  font-size: 13px;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--text-3);
}
.badge {
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--glass-strong);
  font-size: 12px;
  color: var(--text-2);
  text-align: center;
}
/* 窄档:去掉备注列,备注挪到路径下面一行 */
.tbl.nar .th,
.tbl.nar .tr {
  grid-template-columns: 56px minmax(0, 1fr) auto;
  gap: 8px;
  padding: 0 10px;
}
.tbl.nar .rm {
  display: none;
}
.tbl.nar .tr {
  min-height: 44px;
}
.tbl.nar .narsub {
  display: block;
}
.add {
  display: flex;
  flex: none;
  gap: 8px;
  padding: 10px 14px;
  border-top: 1px solid var(--separator);
}
.add :deep(.n-input) {
  flex: 1;
  min-width: 0;
}
.add :deep(.n-input.rmi) {
  flex: 0 1 150px;
}
.tbl.nar .add {
  flex-wrap: wrap;
}
.tbl.nar .add :deep(.n-input),
.tbl.nar .add :deep(.n-input.rmi) {
  flex: 1 1 100%;
}
/* 预览:ReauthModal 的样子 */
.dlg {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: min(100%, 360px);
  margin: 0 auto;
  padding: 22px 24px 18px;
  border-radius: var(--radius-lg);
  background: var(--color-bg-elevated);
  box-shadow: var(--shadow-2);
  text-align: left;
  font-size: 14px;
}
.lock {
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  border-radius: var(--radius-lg);
  color: var(--sel-fg);
  background: var(--sel-bg);
}
.dlg b {
  font-size: 16px;
  font-weight: 600;
}
.dlg p {
  margin: 0;
  line-height: 1.5;
}
.dlg p code {
  display: block;
  margin-top: 2px;
  font-size: 13px;
  white-space: normal;
  word-break: break-all;
  color: var(--text-1);
}
.dlg .hint {
  font-size: 13px;
  color: var(--text-3);
}
.in {
  display: block;
  height: 34px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--fill);
  font-style: normal;
  font-size: 13px;
  line-height: 32px;
  color: var(--text-3);
}
.acts {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 6px;
}
.acts span {
  padding: 4px 14px;
  border-radius: 9px;
  background: var(--fill);
  font-size: 13px;
}
.acts .p {
  color: var(--on-acc);
  background: var(--acc-solid);
}
</style>
