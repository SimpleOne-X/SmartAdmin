<script setup lang="ts">
// 角色管理 = SmartTable CRUD(照职位范式)+ 专属面板两个:授权菜单弹窗(GrantMenuSheet,勾选菜单树)、数据范围抽屉(选范围类型 + 自定义机构)。
// 删角色会**物理**删掉用户↔角色关联(不可恢复)——所以删前先查一次持有人数当面告知,单删与批量删都盖到,
// 只给行内按钮加警告的话,勾选 + 批量删就绕过去了。
import { computed, h, onMounted, reactive, ref, shallowRef } from 'vue'
import {
  NButton,
  NSpace,
  NInput,
  NInputNumber,
  NSwitch,
  NForm,
  NFormItem,
  NSelect,
  NDropdown,
  useMessage,
  type FormInst,
  type FormRules,
} from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn, type SmartTableInst } from 'smart-naive-table'
import AppIcon from '#/components/AppIcon.vue'
import FormContainer from '#/components/FormContainer/index.vue'
import StatusSwitch from '#/components/StatusSwitch/index.vue'
import OrgTreeSelect from '#/components/OrgTreeSelect/index.vue'
import UserPicker from '#/components/UserPicker/index.vue'
import GrantMenuSheet from './components/GrantMenuSheet.vue'
import {
  isBuiltinRole,
  treeForRole,
  modulesForRole,
  grantedInTree,
} from './components/grantMenuGroups'
import { useConfirm } from '#/composables/useConfirm'
import { useBatchDelete } from '#/composables/useBatchDelete'
import { roleApi, menuApi, moduleApi, userApi } from '#/api'
import { useAuthStore } from '#/stores/auth'
import { translateError } from '#/utils/error'
import { SEARCH_ACTIONS, deriveHeaderFilters } from '#/utils/tableFilter'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from '#/utils/tableToolbar'
import TableTotal from '#/components/TableTotal/index.vue'
import { DataScopeType, type ModuleRow, type RoleInput, type SysRole } from '#/types/api'
import type { MenuTreeNode } from '#/types/menu'

const { t } = useI18n()
const message = useMessage()
const { confirm } = useConfirm()
const auth = useAuthStore()
const tableRef = ref<SmartTableInst<SysRole>>()

/** 「未分配」哨兵:雪花 id 无 0,用它表示 moduleId==null 的顶级目录分组。 */
const UNASSIGNED = 0
/** 授权弹窗的「所属应用」下拉数据源。 */
const modules = shallowRef<ModuleRow[]>([])
onMounted(async () => {
  try {
    modules.value = await moduleApi.list()
  } catch {
    // 模块列表仅供授权弹窗的「所属应用」下拉;拉取失败不阻塞角色管理主流程。
  }
})

/**
 * 持有该角色的用户数。复用用户分页端点(Size=1 只要 total),不新开接口。
 * 查不到(如只有角色 CRUD、没有用户查询权限的管理员会 403)返回 null → 退回通用文案,不阻断删除:
 * 用户数只用来让删除者知情,不改变谁能删什么。
 */
async function countUsers(roleId: number): Promise<number | null> {
  try {
    const { total } = await userApi.page({ page: 1, pageSize: 1, roleId })
    return total
  } catch {
    return null
  }
}

const { checkedKeys, run: batchDelete } = useBatchDelete({
  remove: roleApi.batchRemove,
  refresh: () => tableRef.value?.refresh(),
  successMsg: t('role.deleted'),
  content: async ids => {
    const counts = await Promise.all(ids.map(countUsers))
    if (counts.some(n => n === null)) return t('common.batchDeleteConfirm', { count: ids.length })
    const users = counts.reduce<number>((sum, n) => sum + n!, 0)
    return users === 0
      ? t('common.batchDeleteConfirm', { count: ids.length })
      : t('role.batchDeleteWithUsers', { count: ids.length, users })
  },
})

async function askDelete(r: SysRole) {
  const n = await countUsers(r.id)
  const content =
    n === null
      ? t('role.deleteConfirm', { name: r.name })
      : n === 0
        ? t('role.deleteConfirmNoUsers', { name: r.name })
        : t('role.deleteConfirmWithUsers', { name: r.name, count: n })
  const ok = await confirm({
    content,
    type: 'error',
    action: () => roleApi.remove(r.id),
    successMsg: t('role.deleted'),
  })
  if (ok) tableRef.value?.refresh()
}

const toInput = (r: SysRole): RoleInput => ({
  name: r.name,
  code: r.code,
  sort: r.sort,
  enabled: r.enabled,
  remark: r.remark ?? '',
  isDelegatable: r.isDelegatable ?? false,
})

const columns: SmartTableColumn<SysRole>[] = [
  { type: 'selection' },

  { type: 'index', title: () => t('common.rowNo'), width: 64, align: 'center' },
  {
    key: 'code',
    width: 180,
    ellipsis: { tooltip: true },
    title: () => t('role.code'),
    render: r => h('span', { class: 'mono muted' }, r.code),
  },
  {
    key: 'name',
    ellipsis: { tooltip: true },
    title: () => t('role.name'),
    card: 'title', // 窄档卡片以角色名称为标题,编码作为字段
    search: { actions: SEARCH_ACTIONS.fuzzy },
  },
  {
    key: 'sort',
    title: () => t('role.sort'),
    width: 80,
    render: r => h('span', { class: 'num muted' }, r.sort),
  },
  {
    key: 'enabled',
    title: () => t('common.status'),
    width: 90,
    render: r =>
      h(StatusSwitch, {
        value: r.enabled,
        disabled: !auth.isSuperAdmin || !auth.hasPerm('PUT:/api/v1/sys/role/{id}'),
        request: (next: boolean) => roleApi.update(r.id, { ...toInput(r), enabled: next }),
        'onUpdate:value': (v: boolean) => {
          r.enabled = v
        },
      }),
  },
  {
    key: 'remark',
    title: () => t('role.remark'),
    ellipsis: { tooltip: true },
    render: r => r.remark || h('span', { class: 'faint' }, '—'),
  },
  {
    key: 'op',
    title: () => t('common.operation'),
    width: 240,
    fixed: 'right',
    hideInSetting: true,
    render: r => {
      // 角色定义(编辑/删除)与角色授权面(授权菜单/数据范围)为超管专属;
      // 仅"授权用户"(角色指派面)对普通管理员开放(经 RoleGrantPolicy 收口校验)。
      const dropdownOptions = [
        auth.isSuperAdmin && auth.hasPerm('PUT:/api/v1/sys/role/menu')
          ? { label: t('role.grantMenus'), key: 'menus' }
          : null,
        auth.isSuperAdmin && auth.hasPerm('PUT:/api/v1/sys/role/datascope')
          ? { label: t('role.dataScope'), key: 'scope' }
          : null,
        auth.hasPerm('PUT:/api/v1/sys/role/users')
          ? { label: t('role.grantUsers'), key: 'users' }
          : null,
      ].filter((o): o is { label: string; key: string } => o !== null)
      return h(NSpace, { size: 4, wrapItem: false }, () => [
        auth.isSuperAdmin && auth.hasPerm('PUT:/api/v1/sys/role/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'primary', onClick: () => openEdit(r) },
              () => t('common.edit'),
            )
          : null,
        auth.isSuperAdmin && auth.hasPerm('DELETE:/api/v1/sys/role/{id}')
          ? h(
              NButton,
              { size: 'small', quaternary: true, type: 'error', onClick: () => askDelete(r) },
              () => t('common.delete'),
            )
          : null,
        dropdownOptions.length
          ? h(
              NDropdown,
              {
                options: dropdownOptions,
                onSelect: (key: string) => {
                  if (key === 'menus') openMenus(r)
                  else if (key === 'scope') openScope(r)
                  else openUsers(r)
                },
              },
              () => h(NButton, { size: 'small', quaternary: true }, () => t('common.more')),
            )
          : null,
      ])
    },
  },
]
deriveHeaderFilters(columns)

// ── 新增/编辑弹窗 ──
const show = ref(false)
const formRef = ref<FormInst | null>(null)
const editingId = ref<number | null>(null)
const rules: FormRules = {
  code: {
    required: true,
    whitespace: true,
    message: () => t('role.codeRequired'),
    trigger: ['input', 'blur'],
  },
  name: {
    required: true,
    whitespace: true,
    message: () => t('role.nameRequired'),
    trigger: ['input', 'blur'],
  },
}
const blank = (): RoleInput => ({
  name: '',
  code: '',
  sort: 0,
  enabled: true,
  remark: '',
  isDelegatable: false,
})
const form = reactive<RoleInput>(blank())

function openAdd() {
  editingId.value = null
  Object.assign(form, blank())
  show.value = true
}
function openEdit(r: SysRole) {
  editingId.value = r.id
  Object.assign(form, toInput(r))
  show.value = true
}
async function save() {
  await formRef.value?.validate()
  try {
    if (editingId.value === null) await roleApi.add({ ...form })
    else await roleApi.update(editingId.value, { ...form })
    message.success(t('role.saved'))
    await tableRef.value?.refresh()
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}

// ── 授权菜单弹窗(目录卡片列表 → 全量替换角色授权)──
const showMenus = ref(false)
// 整份替换 + 只读(勾选态在 GrantMenuTable 自己的 reactive 里,改动经 GrantMenuSheet 带回 saveMenus),不需要深响应
const menuTree = shallowRef<MenuTreeNode[]>([])
const menuGranted = shallowRef<number[]>([])
const menuRole = ref<SysRole | null>(null)
const defaultModuleId = ref(UNASSIGNED)
// 授权弹窗实际展示的应用:非内置角色不含系统应用(除非它下面还有消费者自建的目录)
const menuModules = shallowRef<ModuleRow[]>([])

async function openMenus(r: SysRole) {
  try {
    const [tree, granted] = await Promise.all([menuApi.tree(), roleApi.getMenus(r.id)])
    // 系统菜单只能授给内置角色:非内置角色的弹窗里不出现系统自带的目录(后端同样拒绝)
    const builtin = isBuiltinRole(r)
    menuRole.value = r
    menuTree.value = treeForRole(tree, builtin)
    menuModules.value = modulesForRole(modules.value, builtin, menuTree.value)
    // 非内置角色身上可能还留着看不见的系统菜单授权:只带看得见的进弹窗,保存时它们就不会被回传而得到 41009
    menuGranted.value = builtin ? granted : grantedInTree(granted, menuTree.value)
    const preferred = auth.currentModuleId ?? menuModules.value[0]?.id ?? UNASSIGNED
    defaultModuleId.value = menuModules.value.some(m => m.id === preferred)
      ? preferred
      : (menuModules.value[0]?.id ?? UNASSIGNED)
    showMenus.value = true
  } catch (e) {
    message.error(translateError(e))
  }
}
async function saveMenus(ids: number[]) {
  if (menuRole.value === null) return
  try {
    await roleApi.setMenus(menuRole.value.id, ids)
    message.success(t('role.grantSaved'))
  } catch (e) {
    message.error(translateError(e))
    return false
  }
}

// ── 授权用户(UserPicker) ──
const userPickerRef = ref<InstanceType<typeof UserPicker> | null>(null)
const userRoleId = ref<number | null>(null)

async function openUsers(r: SysRole) {
  userRoleId.value = r.id
  try {
    const ids = await roleApi.getUsers(r.id)
    userPickerRef.value?.open(ids)
  } catch (e) {
    message.error(translateError(e))
  }
}
async function onUserConfirm(ids: number[]) {
  if (userRoleId.value === null) return
  try {
    await roleApi.setUsers(userRoleId.value, ids)
    message.success(t('role.grantUsersSaved'))
  } catch (e) {
    message.error(translateError(e))
  }
}

// ── 数据范围抽屉 ──
const showScope = ref(false)
const scopeRoleId = ref<number | null>(null)
const scopeRole = ref<SysRole | null>(null)
const scopeForm = reactive<{ scopeType: DataScopeType; customOrgIds: number[] }>({
  scopeType: DataScopeType.Org,
  customOrgIds: [],
})
const isCustom = computed(() => scopeForm.scopeType === DataScopeType.Custom)
const scopeOptions = computed(() => [
  { label: t('role.scope.all'), value: DataScopeType.All },
  { label: t('role.scope.org'), value: DataScopeType.Org },
  { label: t('role.scope.orgAndChildren'), value: DataScopeType.OrgAndChildren },
  { label: t('role.scope.self'), value: DataScopeType.Self },
  { label: t('role.scope.custom'), value: DataScopeType.Custom },
])
async function openScope(r: SysRole) {
  scopeRoleId.value = r.id
  scopeRole.value = r
  try {
    const cfg = await roleApi.getDataScope(r.id)
    scopeForm.scopeType = cfg?.scopeType ?? DataScopeType.Org
    scopeForm.customOrgIds = cfg?.customOrgIds
      ? cfg.customOrgIds.split(',').filter(Boolean).map(Number)
      : []
    showScope.value = true
  } catch (e) {
    message.error(translateError(e))
  }
}
async function saveScope() {
  if (scopeRoleId.value === null) return
  try {
    await roleApi.setDataScope(
      scopeRoleId.value,
      scopeForm.scopeType,
      isCustom.value ? scopeForm.customOrgIds : undefined,
    )
    message.success(t('role.scopeSaved'))
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
    :fetcher="roleApi.page"
    :search="{ container: 'table' }"
    fill-height
    :min-row-height="TABLE_MIN_ROW_HEIGHT"
    card-on-narrow
    storage-key="sys-role"
    :checked-row-keys="checkedKeys"
    @update:checked-row-keys="(keys: (string | number)[]) => (checkedKeys = keys)"
    @error="e => message.error(translateError(e))"
  >
    <template #pagination-prefix="{ itemCount }">
      <TableTotal :count="itemCount" />
    </template>
    <template #toolbar-right>
      <n-button
        v-if="auth.isSuperAdmin"
        v-auth="'POST:/api/v1/sys/role/add'"
        type="primary"
        @click="openAdd"
      >
        <template #icon><AppIcon icon="ph:plus" :size="16" /></template>
        {{ t('common.add') }}
      </n-button>
    </template>
    <!-- 勾选后工具栏换成批量栏(本页全选 + 已选 N 项 + 这里的按钮 + 取消选择),按钮出现即代表有勾选 -->
    <template #batch>
      <n-button
        v-if="auth.isSuperAdmin"
        v-auth="'POST:/api/v1/sys/role/batch-delete'"
        secondary
        type="error"
        @click="batchDelete"
      >
        <template #icon><AppIcon icon="ph:trash" :size="16" /></template>
        {{ t('common.batchDelete') }}
      </n-button>
    </template>
  </SmartTable>

  <!-- 新增/编辑 -->
  <FormContainer
    v-model:show="show"
    :title="editingId === null ? t('role.addTitle') : t('role.editTitle')"
    :width="480"
    :on-confirm="save"
    :confirm-text="t('common.save')"
  >
    <n-form ref="formRef" :model="form" :rules="rules" label-placement="left" :label-width="80">
      <n-form-item :label="t('role.code')" path="code">
        <n-input
          v-model:value="form.code"
          :placeholder="t('role.code')"
          :disabled="editingId !== null"
        />
      </n-form-item>
      <n-form-item :label="t('role.name')" path="name">
        <n-input v-model:value="form.name" :placeholder="t('role.name')" />
      </n-form-item>
      <n-form-item :label="t('role.sort')">
        <n-input-number v-model:value="form.sort" :min="0" style="width: 160px" />
      </n-form-item>
      <n-form-item :label="t('role.remark')">
        <n-input
          v-model:value="form.remark as string"
          type="textarea"
          :rows="2"
          :placeholder="t('role.remark')"
        />
      </n-form-item>
      <n-form-item :label="t('common.status')">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>
      <n-form-item v-if="auth.isSuperAdmin" :label="t('role.isDelegatable')">
        <n-switch v-model:value="form.isDelegatable as boolean" />
        <template #feedback>
          <span class="form-hint">{{ t('role.isDelegatableHint') }}</span>
        </template>
      </n-form-item>
    </n-form>
  </FormContainer>

  <!-- 授权菜单 -->
  <GrantMenuSheet
    v-model:show="showMenus"
    :tree="menuTree"
    :granted="menuGranted"
    :modules="menuModules"
    :default-module-id="defaultModuleId"
    :hint="menuRole && !isBuiltinRole(menuRole) ? t('role.systemMenusBuiltinOnly') : undefined"
    :on-save="saveMenus"
  />

  <!-- 授权用户 -->
  <UserPicker ref="userPickerRef" @confirm="onUserConfirm" />

  <!-- 数据范围 -->
  <FormContainer
    v-model:show="showScope"
    :title="t('role.dataScope')"
    :width="480"
    :on-confirm="saveScope"
    :confirm-text="t('common.save')"
  >
    <div v-if="scopeRole" class="scope-who">{{ scopeRole.name }} · {{ scopeRole.code }}</div>
    <n-form :model="scopeForm" label-placement="left" :label-width="90">
      <n-form-item :label="t('role.scopeType')">
        <n-select v-model:value="scopeForm.scopeType" :options="scopeOptions" />
      </n-form-item>
      <n-form-item v-if="isCustom" :label="t('role.customOrgs')">
        <OrgTreeSelect
          v-model:value="scopeForm.customOrgIds"
          multiple
          :placeholder="t('role.customOrgsHint')"
        />
      </n-form-item>
    </n-form>
  </FormContainer>
</template>

<style scoped>
/* 开关下方的说明文字 */
.form-hint {
  font-size: 13px;
  color: var(--text-3);
}

/* 数据范围表单顶部:当前角色「名称 · 编码」 */
.scope-who {
  margin: -4px 0 14px;
  font-size: 13px;
  color: var(--text-3);
}
</style>
