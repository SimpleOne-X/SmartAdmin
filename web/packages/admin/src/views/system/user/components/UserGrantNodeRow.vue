<script setup lang="ts">
// 用户授权弹窗里一个节点一行:名称 / 来源与是否有效 / 三态;选了允许或拒绝再展开到期日与理由。
// 选「拒绝」时,这个节点的接口若还由别的有效节点携带,就地列出来:按节点拒绝不收回共用的接口。
import { computed } from 'vue'
import { NDatePicker, NInput, NRadioButton, NRadioGroup, NTag, NTooltip } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import { UserMenuEffect, type UserMenuEffectiveNode } from '#/types/api'
import { formatDate, type DraftEntry, type TriState } from './userGrantState'

const props = defineProps<{
  title: string
  path?: string
  level: 'catalog' | 'page' | 'button'
  node?: UserMenuEffectiveNode
  state: TriState
  entry?: DraftEntry
  /** 目标用户可编辑且这个节点所属模块可授 */
  editable: boolean
  /** 只因模块不可转授而不能改(悬浮说明) */
  notGrantable: boolean
  /** 到期日上限 yyyy-MM-dd;null = 不限 */
  maxDate: string | null
  leaked: { code: string; from: string }[]
}>()

const emit = defineEmits<{
  (e: 'update:state', v: TriState): void
  (e: 'update:expireDate', v: string | null): void
  (e: 'update:remark', v: string): void
}>()

const { t } = useI18n()
/** 角色标签最多露两个,其余折成 +N。 */
const roles = computed(() => props.node?.roles ?? [])
const shownRoles = computed(() => roles.value.slice(0, 2))
const today = formatDate(new Date())
/** 今天以前、上限以后的日期不可选。 */
const isDateDisabled = (ts: number) => {
  const d = formatDate(new Date(ts))
  return d < today || (props.maxDate != null && d > props.maxDate)
}
/** 受限时「允许」的到期日必填(不给清空);拒绝与不受限时可留空 = 长期。 */
const clearable = computed(
  () => props.maxDate == null || props.entry?.effect === UserMenuEffect.Deny,
)
</script>

<template>
  <div class="ugr" :class="[`is-${level}`, { 'is-off': !node?.effective }]">
    <div class="ugr-main">
      <div class="ugr-name">
        <span class="ugr-title">{{ title }}</span>
        <span v-if="path" class="ugr-path">{{ path }}</span>
      </div>
      <div class="ugr-src">
        <n-tag v-for="r in shownRoles" :key="r" size="small" :bordered="false">{{ r }}</n-tag>
        <span v-if="roles.length > shownRoles.length" class="faint">
          +{{ roles.length - shownRoles.length }}
        </span>
        <n-tag v-if="node?.expired" size="small" type="warning" :bordered="false">
          {{ t('userGrant.expired') }}
        </n-tag>
        <n-tag v-if="node?.deniedByAncestor" size="small" type="error" :bordered="false">
          {{ t('userGrant.deniedByAncestor') }}
        </n-tag>
        <span class="ugr-dot" :class="{ on: node?.effective }">
          {{ node?.effective ? t('userGrant.effective') : t('userGrant.ineffective') }}
        </span>
      </div>
      <n-tooltip :disabled="!notGrantable">
        <template #trigger>
          <!-- 禁用的单选组收不到鼠标事件,包一层让悬浮说明能出来 -->
          <span class="ugr-tri">
            <n-radio-group
              :value="state"
              size="small"
              :disabled="!editable"
              @update:value="(v: TriState) => emit('update:state', v)"
            >
              <n-radio-button value="follow">{{ t('userGrant.follow') }}</n-radio-button>
              <n-radio-button value="allow">{{ t('userGrant.allow') }}</n-radio-button>
              <n-radio-button value="deny">{{ t('userGrant.deny') }}</n-radio-button>
            </n-radio-group>
          </span>
        </template>
        {{ t('userGrant.notGrantable') }}
      </n-tooltip>
    </div>

    <div v-if="entry" class="ugr-edit">
      <n-date-picker
        type="date"
        size="small"
        value-format="yyyy-MM-dd"
        :formatted-value="entry.expireDate"
        :clearable="clearable"
        :is-date-disabled="isDateDisabled"
        :disabled="!editable"
        :placeholder="t('userGrant.expireLongTerm')"
        @update:formatted-value="(v: string | null) => emit('update:expireDate', v)"
      />
      <n-input
        size="small"
        :value="entry.remark ?? ''"
        :maxlength="200"
        :disabled="!editable"
        :placeholder="t('userGrant.remark')"
        @update:value="(v: string) => emit('update:remark', v)"
      />
    </div>

    <div v-if="state === 'deny' && leaked.length" class="ugr-leak">
      <AppIcon icon="ph:warning" :size="14" />
      <span>{{ t('userGrant.leaked') }}</span>
      <code v-for="l in leaked" :key="l.code">
        {{ t('userGrant.leakedFrom', { code: l.code, from: l.from }) }}
      </code>
    </div>
  </div>
</template>

<style scoped>
.ugr {
  padding: 8px 16px;
  border-top: 1px solid var(--hairline);
}
.ugr.is-button {
  padding-left: 40px;
}
.ugr.is-catalog {
  flex: 1;
  min-width: 0;
  padding: 0;
  border-top: 0;
}
.ugr-main {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto;
  gap: 6px 16px;
  align-items: center;
}
.ugr-name {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.ugr-title {
  overflow-wrap: anywhere;
}
.ugr.is-catalog .ugr-title {
  font-size: 15px;
  font-weight: 600;
}
.ugr.is-off .ugr-title {
  color: var(--text-2);
}
.ugr-path {
  font-family: var(--font-mono);
  font-size: 12px;
  color: var(--text-3);
}
.ugr-src {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  font-size: 13px;
}
.ugr-dot {
  color: var(--text-3);
}
.ugr-dot.on {
  color: var(--sel-fg);
}
.ugr-tri {
  display: inline-flex;
}
.ugr-edit {
  display: grid;
  grid-template-columns: 170px minmax(0, 1fr);
  gap: 8px;
  margin-top: 8px;
}
.ugr-leak {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 8px;
  align-items: center;
  margin-top: 6px;
  font-size: 12px;
  color: var(--warn);
}
.ugr-leak code {
  font-family: var(--font-mono);
}
@media (max-width: 640px) {
  .ugr-main,
  .ugr-edit {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
