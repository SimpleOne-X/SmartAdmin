<script setup lang="ts">
// 用户管理(写侧)= SmartTable(列表/搜索/分页)+ UserFormModal(新增/编辑)+ ResetPasswordModal(重置/初始口令展示)+ 专用启停端点。
// 超管行(isSuperAdmin)删除/停用置灰防自锁;启停走专用 setEnabled(非全量 update)。
// 导入导出:ImportWizard 四步向导 + ExportColumnsModal 选列导出(带当前筛选)。
import { computed, h, onMounted, ref, watch } from 'vue'
import { createReusableTemplate, useElementSize } from '@vueuse/core'
import { useRoute } from 'vue-router'
import {
  NButton,
  NTree,
  NSpace,
  NTag,
  NAvatar,
  type DataTableSortState,
  NPopconfirm,
  NDropdown,
  NInput,
  NSpin,
  NTooltip,
  NDrawer,
  NDrawerContent,
  useMessage,
  type DropdownOption,
  type TreeOption,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import DictTag from '#/components/DictTag/index.vue'
import ImportWizard, { type ImportWizardApi } from '#/components/ImportWizard/index.vue'
import ExportColumnsModal from '#/components/ExportColumnsModal/index.vue'
import UserFormModal from './components/UserFormModal.vue'
import ResetPasswordModal from './components/ResetPasswordModal.vue'
import UserGrantMenuSheet from './components/UserGrantMenuSheet.vue'
import { useConfirm } from '#/composables/useConfirm'
import { useBatchDelete } from '#/composables/useBatchDelete'
import { mfaApi, userApi, positionApi, roleApi, orgApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { useUserStore } from '#/stores/user'
import { translateError } from '#/utils/error'
import {
  SEARCH_ACTIONS,
  flatSearchOf,
  presetEqualFilter,
  deriveHeaderFilters,
} from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import { triggerBlobDownload } from '#/utils/download'
import { buildTree, expandableIds } from '#/utils/tree'
import type { ExportColumnDef, SysOrg, UserItem } from '#/types/api'

const { t } = useI18n()
const message = useMessage()
const { run, confirm } = useConfirm()
const authStore = useAuthStore()
const tableRef = ref<SmartTableInst<UserItem>>()
const { checkedKeys, run: batchDelete } = useBatchDelete({
  remove: userApi.batchRemove,
  refresh: () => tableRef.value?.refresh(),
  successMsg: t('user.deleted'),
})

// 新增/编辑弹窗 + 重置密码弹窗(表单/校验/保存均在各自组件内,父页只传下拉选项 + 收 saved/passwordGenerated)。
const userFormRef = ref<InstanceType<typeof UserFormModal> | null>(null)
const resetModalRef = ref<InstanceType<typeof ResetPasswordModal> | null>(null)
// 授权菜单弹窗(用户单独授权);超管那一行和自己那一行不出入口(后端同样拒绝)
const grantSheetRef = ref<InstanceType<typeof UserGrantMenuSheet> | null>(null)
const userStore = useUserStore()
const myId = computed(() => userStore.userInfo?.userId)

const clearMfaLoading = ref(false)

async function clearUserMfa(user: UserItem) {
  clearMfaLoading.value = true
  try {
    const ok = await confirm({
      content: t('user.clearMfaConfirm', { name: user.name }),
      action: () => mfaApi.clear({ userId: user.id }),
      successMsg: t('user.mfaCleared'),
    })
    if (ok) tableRef.value?.refresh()
  } finally {
    clearMfaLoading.value = false
  }
}

// 职位/角色/主管下拉:各拉一页足量。ponytail: 200 覆盖绝大多数系统;真超再上分页搜索。
// roleOptions 一份两用:既喂编辑弹窗,也当列表页「角色」搜索项的选项源(SmartTable 支持 Ref 选项源)。
const positionOptions = ref<{ label: string; value: number }[]>([])
const roleOptions = ref<{ label: string; value: number }[]>([])
const directorOptions = ref<{ label: string; value: number }[]>([])
// 左侧机构树筛选:选中节点即按其机构过滤用户;params 深监听联动 SmartTable(回第 1 页重查)。
// 面板交互对齐内核 layout.css 的 .side-filter 约定(机构管理页搜索/展开收起用的同一套):
// 搜索走 NTree 自带 pattern/filter;展开受控,进页面全折叠,选中节点自动展开其祖先链。
const orgFlat = ref<SysOrg[]>([])
// 拉取期间给树位占位(见下方 n-spin),避免树从空白直接跳成展开好的一整棵——那一下比慢半拍更扎眼。
const orgLoading = ref(true)
const orgTree = computed(() => buildTree(orgFlat.value))
const selectedOrgId = ref<number | null>(null)
const tableParams = computed<{ orgId?: number }>(() =>
  selectedOrgId.value == null ? {} : { orgId: selectedOrgId.value },
)
function onOrgSelect(keys: (string | number)[]) {
  // 再点选中项 → 取消选中 → 恢复全部
  pickOrg(keys.length ? Number(keys[0]) : null)
}

// ── 内容区较窄时,机构树收进左侧抽屉 ──
// 判据是页面内容区宽度(不是视口):侧栏展开 / 收起都会改变它。面板占掉的宽度本来就会让右侧表格变窄,
// 收进抽屉后表格得到整行宽度;表格自己的窄档(卡片列表)由库按表格卡片宽度另行判定,页面不管。
const COMPACT_WIDTH = 1000
const layoutRef = ref<HTMLElement>()
const { width: layoutWidth } = useElementSize(layoutRef)
// 量到宽度前(0)按宽屏渲染,避免首帧闪一下抽屉模式
const compact = computed(() => layoutWidth.value > 0 && layoutWidth.value < COMPACT_WIDTH)
const orgDrawer = ref(false)
function pickOrg(id: number | null) {
  selectedOrgId.value = id
  orgDrawer.value = false
}
// 机构面板的内容(展开收起 / 搜索 / 「全部」/ 树)只写一份:宽屏放在 aside 里,窄屏放进抽屉。
const [DefineOrgPanel, ReuseOrgPanel] = createReusableTemplate()
const selectedOrgName = computed(
  () => orgFlat.value.find(o => o.id === selectedOrgId.value)?.name ?? t('user.orgFilter'),
)

const orgPattern = ref('')
function filterOrg(input: string, node: TreeOption) {
  const kw = input.trim().toLowerCase()
  if (!kw) return true
  const org = node as unknown as SysOrg
  return org.name.toLowerCase().includes(kw) || (org.code ?? '').toLowerCase().includes(kw)
}

const orgExpandedKeys = ref<number[]>([])
const expandableOrgKeys = computed(() => expandableIds(orgTree.value))
const allOrgExpanded = computed(
  () =>
    expandableOrgKeys.value.length > 0 &&
    orgExpandedKeys.value.length >= expandableOrgKeys.value.length,
)
function toggleExpandAllOrg() {
  orgExpandedKeys.value = allOrgExpanded.value ? [] : [...expandableOrgKeys.value]
}
// 受控展开:一旦传了 expanded-keys,naive 就以它为准,不会自己默认展开——进页面全展开得自己播种。
watch(expandableOrgKeys, keys => (orgExpandedKeys.value = keys), { immediate: true })

// 选中的机构可能藏在收起的父级里(典型:切换搜索结果后选中项被折叠的祖先挡住),把祖先链补进展开集。
watch(selectedOrgId, id => {
  if (id == null) return
  const byId = new Map(orgFlat.value.map(o => [o.id, o]))
  const next = new Set(orgExpandedKeys.value)
  let cursor = byId.get(id)?.parentId ?? 0
  while (cursor && byId.has(cursor)) {
    next.add(cursor)
    cursor = byId.get(cursor)!.parentId
  }
  orgExpandedKeys.value = [...next]
})
onMounted(async () => {
  try {
    const { items } = await positionApi.page({ page: 1, pageSize: 200 })
    positionOptions.value = items.map(p => ({ label: p.name, value: p.id }))
  } catch {
    // 静默:职位是配角,不打断列表
  }
  try {
    const { items } = await roleApi.page({ page: 1, pageSize: 200 })
    roleOptions.value = items.map(r => ({ label: r.name, value: r.id }))
  } catch {
    // 静默:角色下拉拉取失败不打断列表
  }
  try {
    // 主管候选就是用户自身;拉一页足量作下拉源(允许把某用户选为自己主管,后台系统无害,不加环检)
    const { items } = await userApi.page({ page: 1, pageSize: 200 })
    directorOptions.value = items.map(u => ({ label: `${u.name}(${u.account})`, value: u.id }))
  } catch {
    // 静默:主管下拉是配角,拉取失败不打断列表
  }
  try {
    orgFlat.value = await orgApi.list()
  } catch {
    // 静默:机构树是筛选辅助,拉取失败不打断列表
  } finally {
    orgLoading.value = false
  }
})

// 角色反查:角色页「查看用户」跳这里并带 ?roleId=。
// 必须 watch 而非 onMounted:标签页按 path 做主键且页面被 keep-alive,本页标签已开着时再跳一次只换 query,
// 组件实例被复用、onMounted 不会再触发。
// 同时把 tableRef 一起纳入监听源:immediate 在 setup 期求值,那时表格实例还没挂上(ref 为空),
// 只听 query 的话冷启动这一路会静默失效——实例就绪时这条 watch 会再触发一次,把筛选补上。
const route = useRoute()
watch(
  [tableRef, () => route.query.roleId],
  ([inst, roleId]) => {
    if (!inst) return
    // route 是全局的:页面被 keep-alive 着,切到别的页面时 query.roleId 变 undefined,本页的 watcher 照样会响。
    // 不守卫就等于「离开本页 = 清掉角色条件并在后台发一次没人看的请求」,切回来条件还没了(任务日志页同理)。
    if (route.path !== '/system/user') return
    // 条件构造器下搜索值在过滤态里而不是 params;presetEqualFilter 内部比较当前条件,
    // 值没变就不写 —— 实例就绪与 query 变化可能各触发一次,第二次不该再发请求。
    // 远程模式下 setFilter 自己会回第 1 页重查,不用再 search()。
    presetEqualFilter(inst, 'roleId', roleId == null ? undefined : Number(roleId))
  },
  { immediate: true },
)

// 头像首字母兜底。NAvatar 的规矩:default 插槽一有内容就渲染它、彻底无视 src(见 naive Avatar.render)。
// 所以有头像时 default 必须留空让 <img> 出来,首字母只放 #fallback(图挂了/链过期才兜底);无头像才用 default 当文字头像。
const initial = (name?: string | null) => (name || '?').slice(0, 1)

// ── 导入 / 导出 ──
const importShow = ref(false)
const exportShow = ref(false)
const exporting = ref(false)

// 导入 / 导出收进表格内置的「更多」菜单(统一标准:业务按钮只留高频动作)。
// 没有任何权限时 more 为空数组,按钮不出现,不会露出一个空菜单;权限码与原按钮的 v-auth 一字不差。
const toolbarMore = computed(() =>
  [
    authStore.hasPerm('POST:/api/v1/sys/user/import/preview')
      ? { label: t('import.button'), key: 'import' }
      : null,
    authStore.hasPerm('GET:/api/v1/sys/user/export')
      ? { label: t('export.button'), key: 'export' }
      : null,
  ].filter((o): o is { label: string; key: string } => o !== null),
)
// 引用稳定的 toolbar 配置:不要把 `{ ...TABLE_TOOLBAR, more }` 直接写进模板,那样每次重渲染都会新建一个对象
const toolbar = computed(() => ({ ...TABLE_TOOLBAR, more: toolbarMore.value }))
function onMoreSelect(key: string | number) {
  if (key === 'import') importShow.value = true
  else if (key === 'export') exportShow.value = true
}

/** 与后端 UserExportProfile.Columns 对齐(前端无列清单端点,照档案硬编码)。 */
const userExportColumns: ExportColumnDef[] = [
  { key: 'Account', title: t('userExport.account') },
  { key: 'Name', title: t('userExport.name') },
  { key: 'Nickname', title: t('userExport.nickname') },
  { key: 'Phone', title: t('userExport.phone') },
  { key: 'Email', title: t('userExport.email') },
  { key: 'Gender', title: t('userExport.gender') },
  { key: 'OrgName', title: t('userExport.orgName') },
  { key: 'PositionName', title: t('userExport.positionName') },
  { key: 'DirectorName', title: t('userExport.directorName') },
  { key: 'Enabled', title: t('userExport.enabled') },
  { key: 'IsSuperAdmin', title: t('userExport.superAdmin'), defaultSelected: false },
  { key: 'CreateTime', title: t('userExport.createTime') },
]

const userImportApi: ImportWizardApi = {
  downloadTemplate: () => userApi.importTemplate(),
  preview: (file, mapping) => userApi.importPreview(file, mapping),
  validate: rows => userApi.importValidate(rows),
  commit: (rows, strategy) => userApi.importCommit(rows, strategy),
  errorReport: rows => userApi.importErrorReport(rows),
}

// 当前排序:库把排序当作额外请求参数直接拼进列表请求,不会放进 inst.params,导出要「与列表排序一致」就得自己接住。
const sorter = ref<{ field: string; order: 'asc' | 'desc' } | null>(null)
function onSorter(state: DataTableSortState | DataTableSortState[] | null) {
  const s = Array.isArray(state) ? state[0] : state
  sorter.value =
    s && s.order
      ? { field: String(s.columnKey), order: s.order === 'ascend' ? 'asc' : 'desc' }
      : null
}

/** 导出:带当前 SmartTable 筛选(含左侧机构树 orgId)+ 当前排序 + 选中列。 */
async function onExport(keys: string[]) {
  // 搜索值在过滤态里,flatSearchOf 序列化回扁平键;orgId 来自左侧机构树的联动参数(:params),不在过滤态里,
  // 排序也不在 inst.params 里,所以这两项都取页面自己持有的状态。
  const p = flatSearchOf(tableRef.value)
  exporting.value = true
  try {
    const blob = await userApi.export({
      account: p.account || undefined,
      name: p.name || undefined,
      orgId: tableParams.value.orgId,
      roleId: p.roleId != null ? Number(p.roleId) : undefined,
      sortField: sorter.value?.field,
      sortOrder: sorter.value?.order,
      columns: keys.join(','),
    })
    triggerBlobDownload(blob, t('userExport.fileName'))
    exportShow.value = false
    message.success(t('export.done'))
  } catch (e) {
    message.error(translateError(e))
  } finally {
    exporting.value = false
  }
}

/** 空值占位:「—」用 faint 弱化 */
const dash = () => h('span', { class: 'faint' }, '—')
/** 下拉菜单项的线条图标 */
const menuIcon = (name: string) => () => h(AppIcon, { icon: name, size: 16 })

const columns: SmartTableColumn<UserItem>[] = [
  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  // 超管行禁勾:批量删除同样不可含超管(后端也会整体拒绝)
  { type: 'selection', disabled: (r: UserItem) => r.isSuperAdmin },
  {
    key: 'avatar',
    title: () => t('user.avatar'),
    width: 64,
    align: 'center',
    hideInSetting: false,
    card: false, // 窄档卡片的标题是姓名,头像不进卡片
    render: r =>
      h(
        NAvatar,
        { round: true, size: 'small', src: r.avatar || undefined },
        r.avatar ? { fallback: () => initial(r.name) } : { default: () => initial(r.name) },
      ),
  },
  {
    key: 'account',
    ellipsis: { tooltip: true },
    title: () => t('user.account'),
    search: { actions: SEARCH_ACTIONS.fuzzy },
    sorter: true,
  },
  {
    key: 'name',
    ellipsis: { tooltip: true },
    title: () => t('user.name'),
    card: 'title',
    search: { actions: SEARCH_ACTIONS.fuzzy },
    sorter: true,
  },
  {
    key: 'phone',
    width: 130,
    ellipsis: { tooltip: true },
    title: () => t('user.phone'),
    sorter: true,
    render: r => (r.phone ? h('span', { class: 'num' }, r.phone) : dash()),
  },
  {
    key: 'gender',
    title: () => t('user.gender'),
    width: 80,
    hideInTable: true, // 次要列:默认隐藏收窄整表,列设置抽屉可打开
    render: r => (r.gender ? h(DictTag, { typeCode: 'gender', value: r.gender }) : dash()),
  },
  // 只作搜索项,不进表格也不进列设置。options 直接吃编辑表单已拉好的 roleOptions(SmartTable 支持 Ref 选项源),不额外发请求;
  // 列上有 options,搜索控件自动是 select。
  {
    key: 'roleId',
    title: () => t('user.roles'),
    hideInTable: true,
    options: roleOptions,
    search: { actions: SEARCH_ACTIONS.exact, props: { clearable: true } },
  },
  {
    key: 'orgName',
    ellipsis: { tooltip: true },
    title: () => t('user.org'),
    render: r => r.orgName || dash(),
  },
  {
    key: 'positionName',
    title: () => t('user.position'),
    hideInTable: true,
    render: r => r.positionName || dash(),
  },
  {
    key: 'enabled',
    title: () => t('user.status'),
    width: 90,
    sorter: true,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        // 超管不可停用(防自锁 —— 停了就没法从 UI 恢复;后端也保护);无启停权限亦置灰。
        disabled: r.isSuperAdmin || !authStore.hasPerm('PUT:/api/v1/sys/user/{id}/enabled'),
        confirm: (next: boolean) => (next ? null : t('user.disableConfirm', { name: r.name })),
        request: (next: boolean) => userApi.setEnabled(r.id, next),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      }),
  },
  {
    key: 'isSuperAdmin',
    title: () => t('user.superAdmin'),
    width: 90,
    render: r =>
      r.isSuperAdmin
        ? h(NTag, { type: 'warning', size: 'small', bordered: false }, () => t('user.superAdmin'))
        : dash(),
  },
  {
    key: 'createTime',
    width: 170,
    title: () => t('user.createTime'),
    format: 'datetime',
    sorter: true,
  },
  // 操作:编辑/删除外露;重置密码、解绑验证器进「更多」(放操作列末尾)。
  // width 须够「编辑+删除+更多」单行;wrap:false 禁止 NSpace 默认换行(否则「更多」掉到删除下一行)。
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 200,
    fixed: 'right', // 钉右:横向滚动时操作列始终可见
    hideInSetting: true,
    render: r => {
      const rawMoreOptions: (DropdownOption | null)[] = [
        authStore.hasPerm('PUT:/api/v1/sys/user/menu') && !r.isSuperAdmin && r.id !== myId.value
          ? { key: 'grantMenus', label: t('userGrant.action'), icon: menuIcon('ph:list-checks') }
          : null,
        authStore.hasPerm('PUT:/api/v1/sys/user/{id}/password')
          ? { key: 'resetPassword', label: t('user.resetPassword'), icon: menuIcon('ph:key') }
          : null,
        r.totpEnabled && authStore.hasPerm('POST:/api/v1/sys/mfa/clear')
          ? {
              key: 'clearMfa',
              label: t('user.clearMfa'),
              icon: menuIcon('ph:link-break'),
              disabled: clearMfaLoading.value,
            }
          : null,
      ]
      const moreOptions = rawMoreOptions.filter((o): o is DropdownOption => o !== null)
      return h(NSpace, { size: 4, wrap: false, wrapItem: false }, () => [
        authStore.hasPerm('PUT:/api/v1/sys/user/{id}')
          ? h(
              NButton,
              {
                size: 'small',
                quaternary: true,
                type: 'primary',
                onClick: () => userFormRef.value?.openEdit(r),
              },
              () => t('common.edit'),
            )
          : null,
        r.isSuperAdmin || !authStore.hasPerm('DELETE:/api/v1/sys/user/{id}')
          ? null
          : h(
              NPopconfirm,
              {
                onPositiveClick: () =>
                  run(() => userApi.remove(r.id), t('user.deleted')).then(ok => {
                    if (ok) tableRef.value?.refresh()
                  }),
              },
              {
                trigger: () =>
                  h(NButton, { size: 'small', quaternary: true, type: 'error' }, () =>
                    t('common.delete'),
                  ),
                default: () => t('user.deleteConfirm', { name: r.name }),
              },
            ),
        moreOptions.length
          ? h(
              NDropdown,
              {
                trigger: 'click',
                options: moreOptions,
                onSelect: (key: string) => {
                  if (key === 'resetPassword') resetModalRef.value?.openReset(r)
                  else if (key === 'clearMfa') void clearUserMfa(r)
                  else if (key === 'grantMenus')
                    grantSheetRef.value?.open({ id: r.id, name: r.name })
                },
              },
              () =>
                h(
                  NButton,
                  {
                    size: 'small',
                    quaternary: true,
                    type: 'primary',
                    iconPlacement: 'right',
                    loading: clearMfaLoading.value,
                  },
                  {
                    default: () => t('common.more'),
                    icon: () => h(AppIcon, { icon: 'ph:caret-down', size: 12 }),
                  },
                ),
            )
          : null,
      ])
    },
  },
]
deriveHeaderFilters(columns)
</script>

<template>
  <!-- 机构面板内容(展开收起 / 搜索 / 「全部」/ 树),只写一份:宽屏放进 aside,内容区 < 1000 时放进抽屉。 -->
  <DefineOrgPanel>
    <div class="side-filter__head">
      <div class="side-filter__actions">
        <n-tooltip v-if="expandableOrgKeys.length">
          <template #trigger>
            <n-button
              quaternary
              circle
              size="small"
              :aria-label="allOrgExpanded ? t('common.collapseAll') : t('common.expandAll')"
              @click="toggleExpandAllOrg"
            >
              <template #icon>
                <AppIcon
                  :icon="
                    allOrgExpanded ? 'ph:arrows-in-line-vertical' : 'ph:arrows-out-line-vertical'
                  "
                  :size="18"
                />
              </template>
            </n-button>
          </template>
          {{ allOrgExpanded ? t('common.collapseAll') : t('common.expandAll') }}
        </n-tooltip>
      </div>
    </div>

    <n-input
      v-model:value="orgPattern"
      size="small"
      clearable
      :placeholder="t('org.searchPlaceholder')"
    >
      <template #prefix><AppIcon icon="ph:magnifying-glass" :size="14" /></template>
    </n-input>

    <button
      type="button"
      class="side-row"
      :class="{ 'is-active': selectedOrgId === null }"
      @click="pickOrg(null)"
    >
      <AppIcon class="side-row__icon" icon="ph:squares-four" :size="15" />
      <span class="side-row__label">{{ t('user.allOrgs') }}</span>
    </button>

    <div class="side-filter__divider" />

    <n-spin :show="orgLoading" class="fill-pass">
      <n-tree
        class="side-tree"
        block-line
        selectable
        show-line
        key-field="id"
        label-field="name"
        children-field="children"
        :data="orgTree"
        :pattern="orgPattern"
        :filter="filterOrg"
        :show-irrelevant-nodes="false"
        :selected-keys="selectedOrgId == null ? [] : [selectedOrgId]"
        :expanded-keys="orgExpandedKeys"
        @update:selected-keys="onOrgSelect"
        @update:expanded-keys="keys => (orgExpandedKeys = keys as unknown as number[])"
      />
    </n-spin>
  </DefineOrgPanel>

  <!-- 「左分组栏 + 右列表」= styles/layout.css 里的形状 3:.side-page 负责 display/gap/拉伸/整屏高度。
       窄内容区不画 aside,表格独占整行,SmartTable 照样是 .side-page 的直接子元素,高度链不变。 -->
  <div ref="layoutRef" class="user-layout side-page">
    <!-- 左侧机构树筛选:面板外观/交互对齐内核 .side-filter 约定(机构管理页搜索、展开收起同款) -->
    <aside v-if="!compact" class="side-filter">
      <ReuseOrgPanel />
    </aside>

    <SmartTable
      ref="tableRef"
      :columns="columns"
      :fetcher="userApi.page"
      :params="tableParams"
      :search="{ container: 'table' }"
      :toolbar="toolbar"
      fill-height
      :min-row-height="TABLE_MIN_ROW_HEIGHT"
      card-on-narrow
      storage-key="sys-user"
      :checked-row-keys="checkedKeys"
      @update:sorter="onSorter"
      @update:checked-row-keys="(keys: (string | number)[]) => (checkedKeys = keys)"
      @more-select="onMoreSelect"
      @error="e => message.error(translateError(e))"
    >
      <template #pagination-prefix="{ itemCount }">
        <TableTotal :count="itemCount" />
      </template>
      <!-- 内容区 < 1000:机构树收进抽屉,工具栏最左出「机构」按钮(限定数据范围的控件,不是业务按钮,所以放在 #toolbar 左半) -->
      <template v-if="compact" #toolbar>
        <n-button secondary @click="orgDrawer = true">
          <template #icon><AppIcon icon="ph:tree-structure" :size="16" /></template>
          {{ selectedOrgName }}
        </n-button>
      </template>
      <!-- #toolbar-right = 工具栏右半(内置图标之前) -->
      <template #toolbar-right>
        <n-button v-auth="'POST:/api/v1/sys/user'" type="primary" @click="userFormRef?.openAdd()">
          <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
          {{ t('common.add') }}
        </n-button>
      </template>
      <!-- 勾选后工具栏换成批量栏(本页全选 + 已选 N 项 + 这里的按钮 + 取消选择),按钮出现即代表有勾选 -->
      <template #batch>
        <n-button
          v-auth="'POST:/api/v1/sys/user/batch-delete'"
          secondary
          type="error"
          @click="batchDelete"
        >
          <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
          {{ t('common.batchDelete') }}
        </n-button>
      </template>
    </SmartTable>
  </div>

  <!-- 内容区 < 1000:机构树的左侧抽屉,选中后自动收起 -->
  <n-drawer v-model:show="orgDrawer" placement="left" width="min(300px, 88vw)">
    <n-drawer-content :title="t('user.orgFilter')" closable>
      <div class="org-drawer-body">
        <ReuseOrgPanel />
      </div>
    </n-drawer-content>
  </n-drawer>

  <UserFormModal
    ref="userFormRef"
    :position-options="positionOptions"
    :role-options="roleOptions"
    :director-options="directorOptions"
    @saved="() => tableRef?.refresh()"
    @password-generated="p => resetModalRef?.showResult(p, true)"
  />

  <ResetPasswordModal ref="resetModalRef" />

  <UserGrantMenuSheet ref="grantSheetRef" @saved="() => tableRef?.refresh()" />

  <ImportWizard
    v-model:show="importShow"
    :api="userImportApi"
    :template-file-name="t('userExport.importTemplateName')"
    :error-report-file-name="t('userExport.errorReportName')"
    @done="() => tableRef?.refresh()"
  />

  <ExportColumnsModal
    v-model:show="exportShow"
    :columns="userExportColumns"
    :loading="exporting"
    @confirm="onExport"
  />
</template>

<style scoped>
/* 树形比平铺分类宽一点:缩进和展开箭头要留出空间,默认 --side-filter-width(224px)偏窄。 */
.user-layout {
  --side-filter-width: 248px;
}

/* 抽屉里的机构面板:与 aside 里同样的纵向堆叠与间距 */
.org-drawer-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
</style>
