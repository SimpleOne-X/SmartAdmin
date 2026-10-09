<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import {
  NButton,
  NSpace,
  NTag,
  NTooltip,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NSwitch,
  NPopconfirm,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useMediaQuery } from '@vueuse/core'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import { useI18n } from 'vue-i18n'
import AppIcon from '#/components/AppIcon.vue'
import IconPicker from '#/components/IconPicker/index.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useAuthStore } from '#/stores/auth'
import { moduleApi } from '#/api'
import { translateError } from '#/utils/error'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import type { ModuleInput, ModuleRow } from '#/types/api'

import { deriveHeaderFilters } from '#/utils/tableFilter'
import { lineIcon } from '#/utils/menuIcon'
const { t } = useI18n()
const message = useMessage()
const { run } = useConfirm()
const authStore = useAuthStore()

// 窄屏:表单标签放上方(底部抽屉里横排标签太挤);宽 / 中档沿用左标签
const narrowForm = useMediaQuery('(max-width: 640px)')
const formLayout = computed(() =>
  narrowForm.value
    ? { labelPlacement: 'top' as const, labelWidth: undefined }
    : { labelPlacement: 'left' as const, labelWidth: 90 },
)

const loading = ref(false)
const rows = ref<ModuleRow[]>([])

/** 内置 system 模块受保护(后端 42013),前端据 code 禁删,避免明知不可为的请求。 */
const isBuiltin = (r: ModuleRow) => r.code === 'system'

async function load() {
  loading.value = true
  try {
    rows.value = await moduleApi.list()
  } catch (e) {
    message.error(translateError(e))
  } finally {
    loading.value = false
  }
}
onMounted(load)

// ── 新增/编辑弹窗(FormContainer:loading/底栏/关闭时机由容器按 onConfirm 协议接管)──
const show = ref(false)
const formRef = ref<FormInst | null>(null)
const rules: FormRules = {
  // whitespace: 纯空白不算填写
  code: {
    required: true,
    whitespace: true,
    message: () => t('module.codeRequired'),
    trigger: ['input', 'blur'],
  },
  title: {
    required: true,
    whitespace: true,
    message: () => t('module.nameRequired'),
    trigger: ['input', 'blur'],
  },
}
const editingId = ref<number | null>(null)
/** 正在编辑内置 system 模块:可转授开关固定关、置灰(后端读写都按 false)。 */
const editingBuiltin = computed(() => editingId.value !== null && form.code === 'system')
/** NSwitch 只收布尔,null(存量模块)按关显示。 */
const delegatable = computed({
  get: () => form.isDelegatable === true,
  set: (v: boolean) => (form.isDelegatable = v),
})
const blank = (): ModuleInput => ({
  code: '',
  title: '',
  icon: '',
  defaultRoute: '',
  apiPrefix: '',
  sort: 0,
  enabled: true,
  remark: '',
  isDelegatable: true,
})
const form = reactive<ModuleInput>(blank())

/** 行数据 → 完整入参:openEdit 回填与 StatusSwitch 行内改状态共用(后端无独立启停端点,均走全量 update)。 */
const toInput = (r: ModuleRow): ModuleInput => ({
  code: r.code,
  title: r.title,
  icon: r.icon ?? '',
  defaultRoute: r.defaultRoute ?? '',
  apiPrefix: r.apiPrefix ?? '',
  sort: r.sort,
  enabled: r.enabled,
  remark: r.remark ?? '',
  isDelegatable: r.isDelegatable ?? false,
})

function openAdd() {
  editingId.value = null
  Object.assign(form, blank())
  show.value = true
}
function openEdit(r: ModuleRow) {
  editingId.value = r.id
  Object.assign(form, toInput(r))
  show.value = true
}

/** FormContainer onConfirm:校验失败 reject / API 失败 return false → 弹层不关;成功正常返回自动关。 */
async function save() {
  await formRef.value?.validate()
  try {
    if (editingId.value === null) await moduleApi.add({ ...form })
    else await moduleApi.update(editingId.value, { ...form })
    message.success(t('module.saved'))
    await load()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}

/** 默认路由 / 路由前缀单元格:有值用等宽次级色,空值画灰色的「—」。 */
const cellText = (v?: string | null) =>
  v ? h('span', { class: 'mono muted' }, v) : h('span', { class: 'faint' }, '—')

const columns: SmartTableColumn<ModuleRow>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    title: () => t('module.code'),
    key: 'code',
    width: 160,
    ellipsis: { tooltip: true },
    search: {},
    render: r => h('span', { class: 'mono' }, r.code),
  },
  {
    title: () => t('module.name'),
    key: 'title',
    ellipsis: { tooltip: true },
    // 窄档卡片以名称作标题(默认取第一个数据列 = 编码)
    card: 'title',
    search: {},
    render: r =>
      h(NSpace, { align: 'center', size: 6, wrapItem: false }, () => [
        r.icon ? h(AppIcon, { icon: lineIcon(r.icon), size: 18 }) : null,
        r.title,
        isBuiltin(r)
          ? h(NTag, { size: 'small', type: 'info', bordered: false }, () => t('module.builtin'))
          : null,
      ]),
  },
  {
    title: () => t('module.defaultRoute'),
    key: 'defaultRoute',
    ellipsis: { tooltip: true },
    render: r => cellText(r.defaultRoute),
  },
  {
    title: () => t('module.apiPrefix'),
    key: 'apiPrefix',
    width: 120,
    render: r => cellText(r.apiPrefix),
  },
  {
    title: () => t('module.delegatable'),
    key: 'isDelegatable',
    width: 100,
    render: r =>
      h(
        NTag,
        { size: 'small', bordered: false, type: r.isDelegatable ? 'success' : 'default' },
        () => t(r.isDelegatable ? 'common.yes' : 'common.no'),
      ),
  },
  {
    title: () => t('module.sort'),
    key: 'sort',
    width: 80,
    render: r => h('span', { class: 'num muted' }, r.sort),
  },
  {
    title: () => t('common.status'),
    key: 'enabled',
    width: 90,
    // 无启停(update)权限者不见开关,退化为只读状态标签,保留状态可见性。
    render: r => {
      if (!authStore.hasPerm('PUT:/api/v1/sys/module/{id}'))
        return h(
          NTag,
          { size: 'small', bordered: false, type: r.enabled ? 'success' : 'default' },
          () => t(r.enabled ? 'common.enabled' : 'common.disabled'),
        )
      const sw = h(StatusSwitch, {
        value: r.enabled,
        // 内置 system 应用停用会让门户失联(模块/菜单管理页都在它下面,停了就没法从 UI 恢复)——
        // 与「禁删」同级的前端保护;后端停用与删除内置模块都拒绝(42013),这里只是不给点。
        disabled: isBuiltin(r),
        // 停用会把应用从门户隐藏,先确认;启用无副作用,跳过确认(返回 null)。
        confirm: (next: boolean) => (next ? null : t('module.disableConfirm', { title: r.title })),
        request: (next: boolean) => moduleApi.update(r.id, { ...toInput(r), enabled: next }),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      })
      // 灰掉的开关悬停说明原因(包一层 span:禁用的开关自己收不到鼠标事件)
      return isBuiltin(r)
        ? h(NTooltip, null, {
            trigger: () => h('span', { style: 'display: inline-flex' }, [sw]),
            default: () => t('module.builtinNoDisable'),
          })
        : sw
    },
  },
  {
    title: () => t('common.operation'),
    key: 'op',
    width: 140,
    align: 'right',
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 2, justify: 'end', wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/module/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        isBuiltin(r) || !authStore.hasPerm('DELETE:/api/v1/sys/module/{id}')
          ? null
          : h(
              NPopconfirm,
              {
                // popconfirm 留在模板层当触发器,「执行→toast」后半段交给 useConfirm().run。
                onPositiveClick: () =>
                  run(() => moduleApi.remove(r.id), t('module.deleted')).then(ok => {
                    if (ok) load()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('module.deleteConfirm', { title: r.title }),
              },
            ),
      ]),
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <!-- 静态数据模式:module 只有全量 list 端点,没有分页端点 —— 条件搜索由表格在前端求值;
       3.0 对静态数据会自己隐藏内置刷新,所以右侧自带一个刷新按钮重新拉取。 -->
  <SmartTable
    :columns="columns"
    :data="rows"
    :loading="loading"
    row-key="id"
    :pagination="false"
    :toolbar="TABLE_TOOLBAR"
    :search="{ container: 'table' }"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-module"
  >
    <template #toolbar-right>
      <n-button v-auth="'POST:/api/v1/sys/module/add'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
      <!-- 静态数据没有内置刷新:这里补一个,形态与内置图标按钮一致(圆形、无底),紧跟在业务按钮后 -->
      <n-button quaternary circle :loading="loading" :aria-label="t('table.refresh')" @click="load">
        <template #icon><AppIcon icon="ph:arrow-clockwise" :size="16" /></template>
      </n-button>
    </template>
  </SmartTable>

  <FormContainer
    v-model:show="show"
    :title="editingId === null ? t('module.addTitle') : t('module.editTitle')"
    :width="520"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form
      ref="formRef"
      :model="form"
      :rules="rules"
      require-mark-placement="left"
      v-bind="formLayout"
    >
      <n-form-item :label="t('module.code')" path="code">
        <n-input
          v-model:value="form.code"
          class="mono"
          :placeholder="t('module.code')"
          :disabled="editingId !== null"
        />
      </n-form-item>
      <n-form-item :label="t('module.name')" path="title">
        <n-input v-model:value="form.title" :placeholder="t('module.name')" />
      </n-form-item>
      <n-form-item :label="t('module.icon')">
        <IconPicker
          :model-value="form.icon ?? ''"
          @update:model-value="(v: string) => (form.icon = v)"
        />
      </n-form-item>
      <n-form-item :label="t('module.defaultRoute')">
        <n-input
          v-model:value="form.defaultRoute as string"
          class="mono"
          placeholder="/system/user"
        />
      </n-form-item>
      <n-form-item :label="t('module.apiPrefix')">
        <n-space vertical :size="2" style="width: 100%">
          <n-input v-model:value="form.apiPrefix as string" class="mono" placeholder="sys" />
          <span class="hint">{{ t('module.apiPrefixHint') }}</span>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('module.sort')">
        <n-input-number v-model:value="form.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>
      <n-form-item :label="t('module.delegatable')">
        <n-space vertical :size="2" style="width: 100%">
          <n-tooltip :disabled="!editingBuiltin">
            <template #trigger>
              <span style="display: inline-flex">
                <n-switch v-model:value="delegatable" :disabled="editingBuiltin" />
              </span>
            </template>
            {{ t('module.builtinNotDelegatable') }}
          </n-tooltip>
          <span class="hint">{{ t('module.delegatableHint') }}</span>
        </n-space>
      </n-form-item>
      <n-form-item :label="t('module.remark')">
        <n-input v-model:value="form.remark as string" type="textarea" :autosize="{ minRows: 2 }" />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
.hint {
  font-size: 13px;
  line-height: 1.5;
  color: var(--text-3);
}

/* 辅助字样(表格单元格由库渲染,用 :deep;表单里的输入框是自己的元素,直接命中) */
.mono,
:deep(.mono) {
  font-family: var(--font-mono);
  font-size: 13.5px;
  letter-spacing: 0;
}
:deep(.muted) {
  color: var(--text-2);
}
:deep(.faint) {
  color: var(--text-3);
}
:deep(.num) {
  font-variant-numeric: tabular-nums;
}
</style>
