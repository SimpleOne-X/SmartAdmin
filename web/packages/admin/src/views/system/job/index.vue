<script setup lang="ts">
// 定时任务 = SmartTable(列表/搜索/分页)+ FormContainer 四分节表单(基本/触发/载荷/失败处理)。
// 状态列走专用启停端点(非全量 update);Panic 行开关旁加红 tag(悬浮出连败次数),重新启用即清连败恢复调度;
// Completed(一次性已跑完/过生效窗口)没有未来时刻,后端拒绝恢复(47010),故只给只读 tag 不给开关。
// 属性包在表单里是键值对 UI,提交时组装成 properties 对象;HTTP 的 headers 子键值对序列化成 JSON 字符串
// 放 properties.headers——读取时后端把 headers 值掩码成 ********,不改就原样回传即"不改"。
import { computed, h, onDeactivated, reactive, ref } from 'vue'
import { useMediaQuery } from '@vueuse/core'
import {
  NButton,
  NSpace,
  NTag,
  NTooltip,
  NPopconfirm,
  NForm,
  NInput,
  NInputNumber,
  NRadioGroup,
  NRadio,
  NSelect,
  NSwitch,
  NDatePicker,
  NGrid,
  NFormItemGi,
  NCollapse,
  NCollapseItem,
  NDrawer,
  NDrawerContent,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import CronEditor from '#/components/CronEditor/index.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useBatchDelete } from '#/composables/useBatchDelete'
import { useShellBreakpoint } from '#/composables/useShellBreakpoint'
import { useAuthStore } from '#/stores/auth'
import { jobApi } from '#/api'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import { fmtDateTime } from '#/utils/format'
import {
  JobConcurrencyMode,
  JobHandlerKind,
  JobMisfireStrategy,
  JobStatus,
  JobTriggerKind,
  type JobInput,
  type SysJob,
} from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const router = useRouter()
const { run, confirm } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<SysJob>>()
// 表单宽度档:视口 >= 820 标签放左,更窄放上(抽屉 / 弹窗宽度跟视口走,不跟内容区走)
const wide = useMediaQuery('(min-width: 820px)')
// 壳层窄档(内容区 < 600):表单抽屉改 92vh 底部抽屉
const { bp } = useShellBreakpoint()
const narrow = computed(() => bp.value === 'narrow')
const { checkedKeys, run: batchDelete } = useBatchDelete({
  remove: jobApi.batchRemove,
  refresh: () => tableRef.value?.refresh(),
  successMsg: t('job.deleted'),
})

// ── 列 ──
const handlerKindOptions = [
  {
    label: () => t('job.handler.compiled'),
    value: JobHandlerKind.Compiled,
    tagType: 'info' as const,
  },
  { label: () => t('job.handler.http'), value: JobHandlerKind.Http, tagType: 'success' as const },
  { label: () => t('job.handler.sql'), value: JobHandlerKind.Sql, tagType: 'warning' as const },
]
const statusOptions = [
  { label: () => t('job.status.ready'), value: JobStatus.Ready, tagType: 'success' as const },
  { label: () => t('job.status.paused'), value: JobStatus.Paused, tagType: 'default' as const },
  {
    label: () => t('job.status.completed'),
    value: JobStatus.Completed,
    tagType: 'default' as const,
  },
  { label: () => t('job.status.panic'), value: JobStatus.Panic, tagType: 'error' as const },
]

/** 触发描述:cron 原文 /「每 N 秒」/ 一次性时刻。 */
function triggerText(r: SysJob): string {
  switch (r.triggerKind) {
    case JobTriggerKind.Interval:
      return t('job.trigger.everySeconds', { n: r.intervalSeconds ?? 0 })
    case JobTriggerKind.OneShot:
      return fmtDateTime(r.oneShotTime, { empty: '—' })
    default:
      return r.cronExpression || '—'
  }
}

const statusTagType = {
  [JobStatus.Ready]: 'success',
  [JobStatus.Paused]: 'default',
  [JobStatus.Completed]: 'default',
  [JobStatus.Panic]: 'error',
} as const
const statusText = (s: JobStatus) =>
  t(
    s === JobStatus.Ready
      ? 'job.status.ready'
      : s === JobStatus.Paused
        ? 'job.status.paused'
        : s === JobStatus.Completed
          ? 'job.status.completed'
          : 'job.status.panic',
  )

function renderStatus(r: SysJob) {
  // Completed 没有未来时刻可恢复(后端 47010),不给开关
  if (r.status === JobStatus.Completed)
    return h(NTag, { size: 'small', bordered: false }, () => statusText(r.status))
  if (!authStore.hasPerm('PUT:/api/v1/sys/job/{id}/enabled'))
    return h(NTag, { size: 'small', bordered: false, type: statusTagType[r.status] }, () =>
      statusText(r.status),
    )
  const sw = h(StatusSwitch, {
    value: r.status === JobStatus.Ready,
    confirm: (next: boolean) => (next ? null : t('job.pauseConfirm', { name: r.name })),
    request: (next: boolean) => jobApi.setEnabled(r.id, next),
    // 启停会重算 nextRunTime / 清连败计数,本地写回拼不出来 → 直接重拉
    'onUpdate:value': () => tableRef.value?.refresh(),
  })
  if (r.status !== JobStatus.Panic) return sw
  return h(NSpace, { size: 6, wrapItem: false, align: 'center' }, () => [
    sw,
    h(NTooltip, null, {
      trigger: () =>
        h(NTag, { size: 'small', bordered: false, type: 'error' }, () => t('job.status.panic')),
      default: () => t('job.panicTooltip', { n: r.consecutiveErrors }),
    }),
  ])
}

const columns: SmartTableColumn<SysJob>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center', fixed: 'left' },
  // 内置任务禁删,批量删除同样不可含内置(后端整批拒绝 47014)
  { type: 'selection', width: 44, fixed: 'left', disabled: (r: SysJob) => r.isSystem },
  {
    key: 'name',
    title: () => t('job.name'),
    search: { actions: SEARCH_ACTIONS.fuzzy },
    width: 240,
    ellipsis: { tooltip: true },
    card: 'title',
    render: r =>
      h('div', null, [
        h('div', { style: 'font-weight:500;' }, r.name),
        h(
          'div',
          {
            style:
              'font-size:12px;color:var(--text-3);font-family:var(--font-mono,ui-monospace,monospace);',
          },
          r.code,
        ),
      ]),
  },
  {
    key: 'handlerKind',
    title: () => t('job.handler.kind'),
    width: 100,
    tag: true,
    search: { actions: SEARCH_ACTIONS.exact },
    options: handlerKindOptions,
  },
  {
    key: 'trigger',
    title: () => t('job.trigger.kind'),
    width: 160,
    ellipsis: { tooltip: true },
    // cron 原文用等宽字体,间隔 / 一次性时刻用等宽数字
    render: r =>
      h(
        'span',
        { class: r.triggerKind === JobTriggerKind.Cron ? 'job-mono' : 'tabular' },
        triggerText(r),
      ),
  },
  {
    key: 'status',
    title: () => t('common.status'),
    width: 130,
    search: { actions: SEARCH_ACTIONS.exact },
    options: statusOptions,
    render: renderStatus,
  },
  { key: 'nextRunTime', title: () => t('job.nextRunTime'), width: 176, format: 'datetime' },
  { key: 'lastRunTime', title: () => t('job.lastRunTime'), width: 176, format: 'datetime' },
  {
    key: 'numberOfRuns',
    title: () => t('job.runCount'),
    width: 110,
    align: 'center',
    render: r =>
      h('span', { class: 'tabular' }, [
        h('span', null, String(r.numberOfRuns)),
        h('span', { style: 'color:var(--text-3);' }, ' / '),
        h(
          'span',
          { style: r.numberOfErrors > 0 ? 'color:var(--err);' : '' },
          String(r.numberOfErrors),
        ),
      ]),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 240,
    fixed: 'right',
    hideInSetting: true,
    render: r =>
      h(NSpace, { size: 4, wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/job/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        authStore.hasPerm('POST:/api/v1/sys/job/{id}/run')
          ? h(NButton, { size: 'small', quaternary: true, onClick: () => runOnce(r) }, () =>
              t('job.runOnce'),
            )
          : null,
        // 记录:携 jobId 跳执行记录页(那边 watch query 自动带上筛选)
        h(
          NButton,
          {
            size: 'small',
            quaternary: true,
            onClick: () => router.push({ path: '/system/job-log', query: { jobId: String(r.id) } }),
          },
          () => t('job.viewLogs'),
        ),
        !authStore.hasPerm('DELETE:/api/v1/sys/job/{id}')
          ? null
          : r.isSystem
            ? h(NTooltip, null, {
                trigger: () =>
                  h(
                    NButton,
                    { size: 'small', quaternary: true, type: 'error', disabled: true },
                    () => t('common.delete'),
                  ),
                default: () => t('job.protectedTip'),
              })
            : h(
                NPopconfirm,
                {
                  onPositiveClick: () =>
                    run(() => jobApi.remove(r.id), t('job.deleted')).then(ok => {
                      if (ok) tableRef.value?.refresh()
                    }),
                },
                {
                  trigger: () =>
                    h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                      t('common.delete'),
                    ),
                  default: () => t('job.deleteConfirm', { name: r.name }),
                },
              ),
      ]),
  },
]
deriveHeaderFilters(columns)

/** 执行一次:重操作走 dialog 确认(执行在 dialog 挂起期间跑,防连点)。 */
function runOnce(r: SysJob) {
  confirm({
    content: t('job.runConfirm', { name: r.name }),
    action: () => jobApi.run(r.id),
    successMsg: t('job.runStarted'),
  }).then(ok => {
    if (ok) tableRef.value?.refresh()
  })
}

// ── 新增/编辑表单(四分节:基本/触发/载荷/失败处理) ──
interface KV {
  key: string
  value: string
}
interface JobForm {
  code: string
  name: string
  remark: string
  triggerKind: JobTriggerKind
  cronExpression: string
  intervalSeconds: number | null
  oneShotTime: string | null
  startTime: string | null
  endTime: string | null
  misfireStrategy: JobMisfireStrategy
  concurrencyMode: JobConcurrencyMode
  handlerKind: JobHandlerKind
  handlerName: string
  /** 编译类自定义属性包(键值对 UI,提交时装成 properties 对象)。 */
  props: KV[]
  httpUrl: string
  httpMethod: string
  headers: KV[]
  httpBody: string
  successStatuses: string
  sqlText: string
  timeoutSeconds: number
  retryCount: number
  retryIntervalSeconds: number
  failAlertThreshold: number
  alertByNotice: boolean
  alertEmails: string
}

const show = ref(false)
const formRef = ref<FormInst | null>(null)
const editingId = ref<number | null>(null)
const blank = (): JobForm => ({
  code: '',
  name: '',
  remark: '',
  // 给个能跑的默认值(每天零点),不留空:CronEditor 的各段控件本来就带默认选中,
  // 留空会让新增表单"看着填好了"却因必填校验存不下,而且预览区在用户动手之前一直不出现。
  triggerKind: JobTriggerKind.Cron,
  cronExpression: '0 0 0 * * ?',
  intervalSeconds: 60,
  oneShotTime: null,
  startTime: null,
  endTime: null,
  misfireStrategy: JobMisfireStrategy.Skip,
  concurrencyMode: JobConcurrencyMode.SerialSkip,
  handlerKind: JobHandlerKind.Compiled,
  handlerName: '',
  props: [],
  httpUrl: '',
  httpMethod: 'GET',
  headers: [],
  httpBody: '',
  successStatuses: '',
  sqlText: '',
  timeoutSeconds: 0,
  retryCount: 0,
  retryIntervalSeconds: 30,
  failAlertThreshold: 0,
  alertByNotice: true,
  alertEmails: '',
})
const form = reactive<JobForm>(blank())

const rules: FormRules = {
  code: {
    required: true,
    whitespace: true,
    message: () => t('job.codeRequired'),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('job.nameRequired'),
    trigger: ['input', 'blur'],
  },
  // 条件字段的 form-item 随 triggerKind/handlerKind v-if 切换,只有挂着的才参与校验
  cronExpression: {
    required: true,
    validator: () => !!form.cronExpression.trim(),
    message: () => t('job.trigger.cronRequired'),
    trigger: ['change', 'blur'],
  },
  intervalSeconds: {
    required: true,
    validator: () => form.intervalSeconds != null && form.intervalSeconds >= 5,
    message: () => t('job.trigger.intervalRequired'),
    trigger: ['change', 'blur'],
  },
  oneShotTime: {
    required: true,
    validator: () => !!form.oneShotTime,
    message: () => t('job.trigger.oneShotRequired'),
    trigger: ['change', 'blur'],
  },
  handlerName: {
    required: true,
    validator: () => !!form.handlerName,
    message: () => t('job.handler.nameRequired'),
    trigger: ['change', 'blur'],
  },
  httpUrl: {
    required: true,
    whitespace: true,
    message: () => t('job.handler.httpUrlRequired'),
    trigger: ['input', 'blur'],
  },
  sqlText: {
    required: true,
    whitespace: true,
    message: () => t('job.handler.sqlRequired'),
    trigger: ['input', 'blur'],
  },
}

// 已注册编译处理器下拉(GET /handlers);只拉一次,失败静默(下拉空了还能手看后端日志排查)
const handlerOptions = ref<{ label: string; value: string }[]>([])
// SQL 载荷总闸(后端 Jobs:Sql:Enabled)。默认按"开"起手:清单没拉到就不该凭空禁掉一种载荷,
// 真关着的话保存时后端还有 47008 兜底。
const sqlEnabled = ref(true)
let handlersLoaded = false
async function ensureHandlers() {
  if (handlersLoaded) return
  try {
    const out = await jobApi.handlers()
    handlerOptions.value = out.handlers.map(n => ({ label: n, value: n }))
    sqlEnabled.value = out.sqlEnabled
    handlersLoaded = true
  } catch {
    // 静默:处理器清单是配角,拉取失败不挡表单
  }
}

const httpMethodOptions = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD'].map(m => ({
  label: m,
  value: m,
}))

// ── 分节折叠 ──
// 基本/触发/载荷默认展开(必填都在这里),高级默认收起;用户可把已填好的节折掉减视觉噪音。
// display-directive="show":收起不卸载,校验读 form 态也不依赖 DOM 挂载。
const SEC = {
  basic: 'basic',
  trigger: 'trigger',
  handler: 'handler',
  advanced: 'advanced',
} as const
const DEFAULT_SECTIONS = [SEC.basic, SEC.trigger, SEC.handler]
const sectionOpen = ref<string[]>([...DEFAULT_SECTIONS])

/** 该行动过任一高级项?动过就默认展开,免得用户觉得"我配过的东西不见了"。 */
function hasAdvanced(r: SysJob): boolean {
  return (
    !!r.startTime ||
    !!r.endTime ||
    r.misfireStrategy !== JobMisfireStrategy.Skip ||
    r.concurrencyMode !== JobConcurrencyMode.SerialSkip ||
    r.timeoutSeconds > 0 ||
    r.retryCount > 0 ||
    r.retryIntervalSeconds !== 30 ||
    r.failAlertThreshold > 0 ||
    !r.alertByNotice ||
    !!r.alertEmails
  )
}

/** 后端 date-time 可能带小数秒,砍到秒级正好喂 n-date-picker 的 formatted-value。 */
const toLocal = (s?: string | null) => (s ? s.slice(0, 19) : null)

function openAdd() {
  editingId.value = null
  Object.assign(form, blank())
  sectionOpen.value = [...DEFAULT_SECTIONS]
  show.value = true
  void ensureHandlers()
}

function openEdit(r: SysJob) {
  editingId.value = r.id
  Object.assign(form, blank())
  sectionOpen.value = hasAdvanced(r) ? [...DEFAULT_SECTIONS, SEC.advanced] : [...DEFAULT_SECTIONS]
  let props: Record<string, string | null> = {}
  try {
    props = JSON.parse(r.propsJson || '{}') as Record<string, string | null>
  } catch {
    // 属性包坏 JSON:别让编辑崩,当空包处理
  }
  form.code = r.code
  form.name = r.name
  form.remark = r.remark ?? ''
  form.triggerKind = r.triggerKind
  form.cronExpression = r.cronExpression ?? ''
  form.intervalSeconds = r.intervalSeconds ?? 60
  form.oneShotTime = toLocal(r.oneShotTime)
  form.startTime = toLocal(r.startTime)
  form.endTime = toLocal(r.endTime)
  form.misfireStrategy = r.misfireStrategy
  form.concurrencyMode = r.concurrencyMode
  form.handlerKind = r.handlerKind
  form.timeoutSeconds = r.timeoutSeconds
  form.retryCount = r.retryCount
  form.retryIntervalSeconds = r.retryIntervalSeconds
  form.failAlertThreshold = r.failAlertThreshold
  form.alertByNotice = r.alertByNotice
  form.alertEmails = r.alertEmails ?? ''
  if (r.handlerKind === JobHandlerKind.Http) {
    form.httpUrl = props.url ?? ''
    form.httpMethod = (props.method ?? 'GET').toUpperCase()
    form.httpBody = props.body ?? ''
    form.successStatuses = props.successStatuses ?? ''
    // headers 值已被后端掩码成 ********;原样回传 = 不改,改了哪条就生效哪条
    let headers: Record<string, string> = {}
    try {
      headers = JSON.parse(props.headers || '{}') as Record<string, string>
    } catch {
      // headers 整体被掩码(非 JSON)时无从回填,置空
    }
    form.headers = Object.entries(headers).map(([key, value]) => ({ key, value: value ?? '' }))
  } else if (r.handlerKind === JobHandlerKind.Sql) {
    form.sqlText = props.sql ?? ''
  } else {
    form.handlerName = r.handlerName
    form.props = Object.entries(props).map(([key, value]) => ({ key, value: value ?? '' }))
  }
  show.value = true
  void ensureHandlers()
}

/** 表单态 → JobInput:按载荷类型装 properties;HTTP 的 headers 序列化成 JSON 字符串子键。 */
function buildInput(): JobInput {
  const properties: Record<string, string> = {}
  if (form.handlerKind === JobHandlerKind.Http) {
    properties.url = form.httpUrl.trim()
    properties.method = form.httpMethod
    const headers: Record<string, string> = {}
    for (const kv of form.headers) if (kv.key.trim()) headers[kv.key.trim()] = kv.value
    if (Object.keys(headers).length) properties.headers = JSON.stringify(headers)
    if (form.httpBody.trim()) properties.body = form.httpBody
    if (form.successStatuses.trim()) properties.successStatuses = form.successStatuses.trim()
  } else if (form.handlerKind === JobHandlerKind.Sql) {
    properties.sql = form.sqlText
  } else {
    for (const kv of form.props) if (kv.key.trim()) properties[kv.key.trim()] = kv.value
  }
  return {
    code: form.code.trim(),
    name: form.name.trim(),
    handlerKind: form.handlerKind,
    // HTTP/SQL 的处理器名由服务端固定填内置处理器,这里传空即可
    handlerName: form.handlerKind === JobHandlerKind.Compiled ? form.handlerName : '',
    properties,
    triggerKind: form.triggerKind,
    cronExpression: form.triggerKind === JobTriggerKind.Cron ? form.cronExpression.trim() : null,
    intervalSeconds: form.triggerKind === JobTriggerKind.Interval ? form.intervalSeconds : null,
    oneShotTime: form.triggerKind === JobTriggerKind.OneShot ? form.oneShotTime : null,
    startTime: form.startTime || null,
    endTime: form.endTime || null,
    misfireStrategy: form.misfireStrategy,
    concurrencyMode: form.concurrencyMode,
    timeoutSeconds: form.timeoutSeconds,
    retryCount: form.retryCount,
    retryIntervalSeconds: form.retryIntervalSeconds,
    failAlertThreshold: form.failAlertThreshold,
    alertByNotice: form.alertByNotice,
    alertEmails: form.alertEmails.trim() || null,
    remark: form.remark.trim() || null,
  }
}

const saving = ref(false)
async function submit() {
  saving.value = true
  try {
    if ((await save()) !== false) show.value = false
  } catch {
    // 静默:n-form 校验失败已有内联提示;API 错误 toast 在 save 内
  } finally {
    saving.value = false
  }
}
onDeactivated(() => {
  show.value = false
})

async function save() {
  await formRef.value?.validate()
  try {
    if (editingId.value === null) await jobApi.add(buildInput())
    else await jobApi.update(editingId.value, buildInput())
    // 文案里带「集群下最长 30 秒后生效」——各节点按 ReloadSeconds 周期重载任务表
    message.success(t('job.saved'))
    await tableRef.value?.refresh()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}
</script>

<template>
  <SmartTable
    :toolbar="TABLE_TOOLBAR"
    ref="tableRef"
    :columns="columns"
    :fetcher="jobApi.page"
    :search="{ container: 'table' }"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-job"
    :checked-row-keys="checkedKeys"
    @update:checked-row-keys="(keys: (string | number)[]) => (checkedKeys = keys)"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button v-auth="'POST:/api/v1/sys/job'" type="primary" @click="openAdd">
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
    <!-- 勾选后工具栏换成批量栏(本页全选 + 已选 N 项 + 这里的按钮 + 取消选择),按钮出现即代表有勾选 -->
    <template #batch>
      <n-button v-auth="'POST:/api/v1/sys/job/batch-delete'" type="error" @click="batchDelete">
        <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
        {{ t('common.batchDelete') }}
      </n-button>
    </template>
  </SmartTable>

  <!-- 宽档右侧 880 抽屉,窄档 92vh 底部抽屉。FormContainer 没有窄档底部形态且形态跟全局偏好走,
       这里按它的 onConfirm 协议自己落一遍:提交中锁死一切关闭途径,点遮罩不关,
       save 抛错(校验失败)或返回 false 不关,其余成功关闭;keep-alive 切走时收起。 -->
  <n-drawer
    v-model:show="show"
    :placement="narrow ? 'bottom' : 'right'"
    width="min(880px, 100vw)"
    height="92vh"
    :class="{ 'st-sheet': narrow }"
    :mask-closable="false"
    :close-on-esc="!saving"
  >
    <n-drawer-content
      :title="editingId === null ? t('job.addTitle') : t('job.editTitle')"
      :closable="!saving"
      :native-scrollbar="false"
    >
      <!-- 四分节都可折叠:默认展开基本/触发/载荷,高级默认收;填完可折掉减噪音。
         不拆页签——页签会藏必填项。两列栅格照 User 页:短字段并排,宽控件 span 2。 -->
      <n-form
        ref="formRef"
        :model="form"
        :rules="rules"
        :label-placement="wide ? 'left' : 'top'"
        :label-width="wide ? 112 : undefined"
        class="job-form"
      >
        <n-collapse
          v-model:expanded-names="sectionOpen"
          display-directive="show"
          class="job-sections"
        >
          <!-- ── 基本 ── -->
          <n-collapse-item :name="SEC.basic">
            <template #header>
              <span class="job-sec-text">{{ t('job.form.sectionBasic') }}</span>
            </template>
            <n-grid cols="1 s:2" responsive="screen" :x-gap="16" :y-gap="0">
              <n-form-item-gi :label="t('job.code')" path="code">
                <n-input
                  v-model:value="form.code"
                  :placeholder="t('job.codePlaceholder')"
                  :disabled="editingId !== null"
                />
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.name')" path="name">
                <n-input v-model:value="form.name" :placeholder="t('job.name')" />
              </n-form-item-gi>
              <n-form-item-gi :span="2" :label="t('job.remark')" :show-feedback="false">
                <n-input
                  v-model:value="form.remark"
                  type="textarea"
                  :autosize="{ minRows: 1, maxRows: 2 }"
                />
              </n-form-item-gi>
            </n-grid>
          </n-collapse-item>

          <!-- ── 触发 ── -->
          <n-collapse-item :name="SEC.trigger">
            <template #header>
              <span class="job-sec-text">{{ t('job.form.sectionTrigger') }}</span>
            </template>
            <n-grid cols="1 s:2" responsive="screen" :x-gap="16" :y-gap="0">
              <n-form-item-gi :label="t('job.trigger.kind')" :show-feedback="false">
                <n-radio-group v-model:value="form.triggerKind">
                  <n-space>
                    <n-radio :value="JobTriggerKind.Cron">{{ t('job.trigger.cron') }}</n-radio>
                    <n-radio :value="JobTriggerKind.Interval">
                      {{ t('job.trigger.interval') }}
                    </n-radio>
                    <n-radio :value="JobTriggerKind.OneShot">
                      {{ t('job.trigger.oneShot') }}
                    </n-radio>
                  </n-space>
                </n-radio-group>
              </n-form-item-gi>
              <n-form-item-gi
                v-if="form.triggerKind === JobTriggerKind.Interval"
                :label="t('job.trigger.intervalSeconds')"
                path="intervalSeconds"
              >
                <n-input-number v-model:value="form.intervalSeconds" :min="5" style="width: 200px">
                  <template #suffix>{{ t('job.trigger.seconds') }}</template>
                </n-input-number>
              </n-form-item-gi>
              <n-form-item-gi
                v-if="form.triggerKind === JobTriggerKind.OneShot"
                :label="t('job.trigger.oneShotTime')"
                path="oneShotTime"
              >
                <n-date-picker
                  v-model:formatted-value="form.oneShotTime"
                  type="datetime"
                  value-format="yyyy-MM-dd'T'HH:mm:ss"
                  clearable
                  style="width: 240px"
                />
              </n-form-item-gi>
              <n-form-item-gi
                v-if="form.triggerKind === JobTriggerKind.Cron"
                :span="2"
                :label="t('job.trigger.cronExpression')"
                path="cronExpression"
              >
                <CronEditor v-model:model-value="form.cronExpression" />
              </n-form-item-gi>
            </n-grid>
          </n-collapse-item>

          <!-- ── 载荷 ── -->
          <n-collapse-item :name="SEC.handler">
            <template #header>
              <span class="job-sec-text">{{ t('job.form.sectionHandler') }}</span>
            </template>
            <n-grid cols="1 s:2" responsive="screen" :x-gap="16" :y-gap="0">
              <n-form-item-gi :span="2" :label="t('job.handler.kind')" :show-feedback="false">
                <n-space align="center">
                  <n-radio-group v-model:value="form.handlerKind">
                    <n-space>
                      <n-radio :value="JobHandlerKind.Compiled">
                        {{ t('job.handler.compiled') }}
                      </n-radio>
                      <n-radio :value="JobHandlerKind.Http">{{ t('job.handler.http') }}</n-radio>
                      <n-radio :value="JobHandlerKind.Sql" :disabled="!sqlEnabled">
                        {{ t('job.handler.sql') }}
                      </n-radio>
                    </n-space>
                  </n-radio-group>
                  <span v-if="!sqlEnabled" class="job-hint">
                    {{ t('job.handler.sqlDisabledHint') }}
                  </span>
                </n-space>
              </n-form-item-gi>
              <template v-if="form.handlerKind === JobHandlerKind.Compiled">
                <n-form-item-gi :span="2" :label="t('job.handler.name')" path="handlerName">
                  <n-select
                    v-model:value="form.handlerName"
                    filterable
                    :options="handlerOptions"
                    :placeholder="t('job.handler.namePlaceholder')"
                  />
                </n-form-item-gi>
                <n-form-item-gi :span="2" :label="t('job.handler.props')" :show-feedback="false">
                  <div class="kv-editor">
                    <div v-for="(kv, i) in form.props" :key="i" class="kv-row">
                      <n-input
                        v-model:value="kv.key"
                        size="small"
                        :placeholder="t('job.handler.propKey')"
                      />
                      <n-input
                        v-model:value="kv.value"
                        size="small"
                        :placeholder="t('job.handler.propValue')"
                      />
                      <n-button
                        quaternary
                        circle
                        size="small"
                        :aria-label="t('common.delete')"
                        @click="form.props.splice(i, 1)"
                      >
                        <template #icon><AppIcon icon="ph:x" :size="14" /></template>
                      </n-button>
                    </div>
                    <n-button dashed size="small" @click="form.props.push({ key: '', value: '' })">
                      <template #icon><AppIcon icon="ph:plus" :size="14" /></template>
                      {{ t('job.handler.addProp') }}
                    </n-button>
                  </div>
                </n-form-item-gi>
              </template>
              <template v-if="form.handlerKind === JobHandlerKind.Http">
                <n-form-item-gi :span="2" :label="t('job.handler.httpUrl')" path="httpUrl">
                  <n-input
                    v-model:value="form.httpUrl"
                    placeholder="https://example.com/api/task"
                  />
                </n-form-item-gi>
                <n-form-item-gi :label="t('job.handler.httpMethod')" :show-feedback="false">
                  <n-select
                    v-model:value="form.httpMethod"
                    :options="httpMethodOptions"
                    style="width: 160px"
                  />
                </n-form-item-gi>
                <n-form-item-gi :label="t('job.handler.successStatuses')" :show-feedback="false">
                  <n-input
                    v-model:value="form.successStatuses"
                    :placeholder="t('job.handler.successStatusesPlaceholder')"
                    style="width: 240px"
                  />
                </n-form-item-gi>
                <n-form-item-gi
                  :span="2"
                  :label="t('job.handler.httpHeaders')"
                  :show-feedback="false"
                >
                  <div class="kv-editor">
                    <div v-for="(kv, i) in form.headers" :key="i" class="kv-row">
                      <n-input
                        v-model:value="kv.key"
                        size="small"
                        :placeholder="t('job.handler.propKey')"
                      />
                      <n-input
                        v-model:value="kv.value"
                        size="small"
                        :placeholder="t('job.handler.propValue')"
                      />
                      <n-button
                        quaternary
                        circle
                        size="small"
                        :aria-label="t('common.delete')"
                        @click="form.headers.splice(i, 1)"
                      >
                        <template #icon><AppIcon icon="ph:x" :size="14" /></template>
                      </n-button>
                    </div>
                    <n-button
                      dashed
                      size="small"
                      @click="form.headers.push({ key: '', value: '' })"
                    >
                      <template #icon><AppIcon icon="ph:plus" :size="14" /></template>
                      {{ t('job.handler.addProp') }}
                    </n-button>
                    <span v-if="editingId !== null" class="kv-hint">
                      {{ t('job.handler.headersMaskHint') }}
                    </span>
                  </div>
                </n-form-item-gi>
                <n-form-item-gi :span="2" :label="t('job.handler.httpBody')" :show-feedback="false">
                  <n-input
                    v-model:value="form.httpBody"
                    type="textarea"
                    :autosize="{ minRows: 2, maxRows: 5 }"
                  />
                </n-form-item-gi>
              </template>
              <n-form-item-gi
                v-if="form.handlerKind === JobHandlerKind.Sql"
                :span="2"
                :label="t('job.handler.sqlText')"
                path="sqlText"
              >
                <n-input
                  v-model:value="form.sqlText"
                  type="textarea"
                  :autosize="{ minRows: 3, maxRows: 8 }"
                  placeholder="UPDATE ..."
                />
              </n-form-item-gi>
            </n-grid>
          </n-collapse-item>

          <!-- ── 高级(默认收;十项都有缺省且无校验,折不折都能存) ── -->
          <!-- 比上面三节更松:单选整行不挤半列;数字+说明用 inline 横排,y-gap 给行距 -->
          <n-collapse-item :name="SEC.advanced" class="job-advanced">
            <template #header>
              <span class="job-sec-text">{{ t('job.form.sectionAdvanced') }}</span>
              <span class="job-hint">{{ t('job.form.sectionAdvancedHint') }}</span>
            </template>
            <n-grid cols="1 s:2" responsive="screen" :x-gap="24" :y-gap="4">
              <n-form-item-gi :label="t('job.form.startTime')" :show-feedback="false">
                <n-date-picker
                  v-model:formatted-value="form.startTime"
                  type="datetime"
                  value-format="yyyy-MM-dd'T'HH:mm:ss"
                  clearable
                  :placeholder="t('job.form.windowPlaceholder')"
                  style="width: 100%"
                />
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.endTime')" :show-feedback="false">
                <n-date-picker
                  v-model:formatted-value="form.endTime"
                  type="datetime"
                  value-format="yyyy-MM-dd'T'HH:mm:ss"
                  clearable
                  :placeholder="t('job.form.windowPlaceholder')"
                  style="width: 100%"
                />
              </n-form-item-gi>
              <!-- 文案较长的单选独占整行,半列会折成两行很难看 -->
              <n-form-item-gi
                :span="2"
                :label="t('job.form.misfireStrategy')"
                :show-feedback="false"
              >
                <n-radio-group v-model:value="form.misfireStrategy">
                  <n-space :size="16">
                    <n-radio :value="JobMisfireStrategy.Skip">
                      {{ t('job.form.misfireSkip') }}
                    </n-radio>
                    <n-radio :value="JobMisfireStrategy.FireOnceNow">
                      {{ t('job.form.misfireFireOnceNow') }}
                    </n-radio>
                  </n-space>
                </n-radio-group>
              </n-form-item-gi>
              <n-form-item-gi
                :span="2"
                :label="t('job.form.concurrencyMode')"
                :show-feedback="false"
              >
                <n-radio-group v-model:value="form.concurrencyMode">
                  <n-space :size="16">
                    <n-radio :value="JobConcurrencyMode.SerialSkip">
                      {{ t('job.form.concurrencySerial') }}
                    </n-radio>
                    <n-radio :value="JobConcurrencyMode.Parallel">
                      {{ t('job.form.concurrencyParallel') }}
                    </n-radio>
                  </n-space>
                </n-radio-group>
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.timeoutSeconds')" :show-feedback="false">
                <div class="job-inline">
                  <n-input-number
                    v-model:value="form.timeoutSeconds"
                    :min="0"
                    style="width: 140px"
                  />
                  <span class="job-hint">{{ t('job.form.timeoutHint') }}</span>
                </div>
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.retryCount')" :show-feedback="false">
                <n-input-number
                  v-model:value="form.retryCount"
                  :min="0"
                  :max="10"
                  style="width: 140px"
                />
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.retryIntervalSeconds')" :show-feedback="false">
                <n-input-number
                  v-model:value="form.retryIntervalSeconds"
                  :min="0"
                  style="width: 140px"
                />
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.failAlertThreshold')" :show-feedback="false">
                <div class="job-inline">
                  <n-input-number
                    v-model:value="form.failAlertThreshold"
                    :min="0"
                    style="width: 140px"
                  />
                  <span class="job-hint">{{ t('job.form.failAlertHint') }}</span>
                </div>
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.alertByNotice')" :show-feedback="false">
                <n-switch v-model:value="form.alertByNotice" />
              </n-form-item-gi>
              <n-form-item-gi :label="t('job.form.alertEmails')" :show-feedback="false">
                <n-input
                  v-model:value="form.alertEmails"
                  :placeholder="t('job.form.alertEmailsPlaceholder')"
                />
              </n-form-item-gi>
            </n-grid>
          </n-collapse-item>
        </n-collapse>
      </n-form>
      <template #footer>
        <div class="drawer-foot" :class="{ 'sheet-foot': narrow }">
          <n-button :size="narrow ? 'large' : 'medium'" :disabled="saving" @click="show = false">
            {{ t('common.cancel') }}
          </n-button>
          <n-button
            type="primary"
            :size="narrow ? 'large' : 'medium'"
            :loading="saving"
            @click="submit"
          >
            {{ t('common.save') }}
          </n-button>
        </div>
      </template>
    </n-drawer-content>
  </n-drawer>
</template>

<style scoped>
/* 抽屉底栏:右对齐;窄档(底部抽屉)两个按钮等分整行 */
.drawer-foot {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
  width: 100%;
}
.sheet-foot {
  gap: 10px;
}
.sheet-foot :deep(.n-button) {
  flex: 1;
}
.st-sheet {
  border-radius: var(--radius-xl) var(--radius-xl) 0 0;
}
/* 表格单元格里的 cron 原文:render 出来的节点没有本组件的 scope id,用无前缀 :deep 命中 */
:deep(.job-mono) {
  font-family: var(--font-mono, ui-monospace, 'SFMono-Regular', Menlo, Consolas, monospace);
  font-size: 13.5px;
}
.kv-editor {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 100%;
}
.kv-row {
  display: flex;
  gap: 8px;
  align-items: center;
}
.kv-hint {
  font-size: 13px;
  color: var(--text-3);
}
.job-hint {
  font-size: 13px;
  color: var(--text-3);
  white-space: nowrap;
  line-height: 1.4;
}
/* 数字框 + 旁注同一行,不跟 label 抢半列宽度 */
.job-inline {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: nowrap;
  min-width: 0;
}
.job-sec-text {
  font-size: 14px;
  font-weight: 600;
}
/* 分节折叠:头像分节标题;内容区留呼吸感 */
.job-sections :deep(.n-collapse-item) {
  margin: 0;
}
.job-sections :deep(.n-collapse-item__header) {
  padding: 10px 0;
}
.job-sections :deep(.n-collapse-item__content-inner) {
  padding: 4px 0 12px;
}
.job-sections :deep(.n-form-item) {
  margin-bottom: 14px;
}
.job-sections :deep(.n-form-item-feedback-wrapper:empty),
.job-sections :deep(.n-form-item-feedback-wrapper:has(:empty)) {
  min-height: 0;
}
/* 高级区再松一点:行距 + label 别贴控件 */
.job-advanced :deep(.n-form-item) {
  margin-bottom: 18px;
}
.job-advanced :deep(.n-form-item-label) {
  padding-right: 12px;
}
</style>
