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
  >
    <div class="tbl" data-testid="sensitive-table">
      <div class="th" aria-hidden="true">
        <span>{{ t('config.sensitive.method') }}</span>
        <span>{{ t('config.sensitive.path') }}</span>
        <span>{{ t('config.security.highSens.remark') }}</span>
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
            <code :title="r.path">{{ r.path }}</code>
            <span class="remark">{{ r.remark || '—' }}</span>
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
          size="small"
          :placeholder="t('config.security.highSens.codePh')"
          :input-props="{ 'aria-label': t('config.security.highSens.code') }"
        />
        <n-input
          v-model:value="newRemark"
          size="small"
          class="rm"
          :placeholder="t('config.security.highSens.remarkPh')"
          :input-props="{ 'aria-label': t('config.security.highSens.remark') }"
        />
        <n-button type="primary" size="small" attr-type="submit" :loading="saving">
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
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  border-radius: 12px;
  background: var(--color-bg-container);
  border: 1px solid var(--color-border);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.04);
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
  font-size: var(--font-size-sm);
  color: var(--color-text-secondary);
  border-bottom: 0.5px solid var(--color-border);
}
.spin {
  flex: 1;
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
  border-bottom: 0.5px solid var(--color-border);
  font-size: var(--font-size-base);
  cursor: pointer;
}
.tr:hover {
  background: var(--color-fill-hover);
}
.tr.sel {
  background: color-mix(in srgb, var(--color-primary) 12%, transparent);
}
.tr:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: -2px;
}
code {
  overflow: hidden;
  font:
    var(--font-size-sm) ui-monospace,
    SFMono-Regular,
    Menlo,
    Consolas,
    monospace;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.remark {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--color-text-secondary);
}
.m {
  justify-self: start;
  padding: 1px 6px;
  border-radius: 5px;
  font:
    600 11px ui-monospace,
    SFMono-Regular,
    Menlo,
    Consolas,
    monospace;
  background: var(--color-fill);
  color: var(--color-text-secondary);
}
.m.POST {
  background: var(--color-success-bg);
  color: var(--color-success);
}
.m.PUT {
  background: var(--color-warning-bg);
  color: var(--color-warning);
}
.m.DELETE {
  background: var(--color-danger-bg);
  color: var(--color-danger);
}
.badge {
  justify-self: start;
  padding: 1px 6px;
  border-radius: 5px;
  font-size: var(--font-size-xs);
  background: var(--color-fill);
  color: var(--color-text-secondary);
}
.add {
  display: flex;
  flex: none;
  gap: 8px;
  padding: 8px 10px;
  border-top: 0.5px solid var(--color-border);
}
.add .rm {
  flex: 0 0 150px;
}
/* 预览:ReauthModal 的样子 */
.dlg {
  width: min(380px, 100%);
  margin: 0 auto;
  padding: 22px 22px 18px;
  border-radius: 14px;
  background: var(--color-bg-container);
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.14);
  text-align: center;
  font-size: var(--font-size-base);
}
.lock {
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  margin: 0 auto 10px;
  border-radius: 12px;
  color: var(--color-primary);
  background: color-mix(in srgb, var(--color-primary) 12%, transparent);
}
.dlg b {
  font-size: 16px;
}
.dlg p {
  margin: 6px 0 0;
  color: var(--color-text-secondary);
}
.dlg p code {
  display: block;
  margin-top: 4px;
  white-space: normal;
  word-break: break-all;
  color: var(--color-text-primary);
}
.dlg .hint {
  font-size: var(--font-size-sm);
  color: var(--color-text-tertiary);
}
.in {
  display: block;
  height: 34px;
  margin: 14px 0 12px;
  padding: 0 10px;
  border-radius: 8px;
  background: var(--color-fill);
  font-style: normal;
  line-height: 34px;
  text-align: left;
  color: var(--color-text-tertiary);
}
.acts {
  display: flex;
  gap: 8px;
}
.acts span {
  flex: 1;
  height: 32px;
  border-radius: 8px;
  line-height: 32px;
  background: var(--color-fill);
}
.acts .p {
  color: #fff;
  background: var(--color-primary);
}
</style>
