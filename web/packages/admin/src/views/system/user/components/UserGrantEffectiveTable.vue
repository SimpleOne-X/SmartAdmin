<script setup lang="ts">
// 「有效权限」页签:只读列出有效的节点和有单独授权记录的节点,说清来源;单独授权带上授权人、时间、到期、理由。
// 嵌在弹窗里的小表,不套整页列表标准(登记在 listSearch.spec.ts 的 EMBEDDED)。
import { computed, h } from 'vue'
import { NTag } from 'naive-ui'
import { useI18n } from 'vue-i18n'
import { SmartTable, type SmartTableColumn } from 'smart-naive-table'
import { translateMenuTitle } from '#/locales/menuTitle'
import type { MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuEffective, type UserMenuGrantItem } from '#/types/api'

const props = defineProps<{
  tree: MenuTreeNode[]
  effective: UserMenuEffective
  grants: UserMenuGrantItem[]
}>()
const { t } = useI18n()

interface Row {
  menuId: number
  path: string
  effective: boolean
  source: string
  grant?: UserMenuEffect | null
  grantorName?: string | null
  grantTime?: string
  expireTime?: string | null
  remark?: string | null
}

const rows = computed<Row[]>(() => {
  // 按树的先序排:同一目录下的节点挨在一起,路径带上目录 / 页面前缀
  const paths = new Map<number, { path: string; order: number }>()
  let order = 0
  const walk = (nodes: MenuTreeNode[], prefix: string) => {
    for (const n of nodes) {
      const path = prefix + translateMenuTitle(n.title, n.path || undefined)
      paths.set(n.id, { path, order: order++ })
      walk(n.children, `${path} / `)
    }
  }
  walk(props.tree, '')
  const grantOf = new Map(props.grants.map(g => [g.menuId, g]))
  return props.effective.nodes
    .filter(n => n.effective || n.grant != null)
    .map(n => {
      const g = grantOf.get(n.menuId)
      const source = [
        ...n.roles,
        n.grant === UserMenuEffect.Allow
          ? t('userGrant.allow')
          : n.grant === UserMenuEffect.Deny
            ? t('userGrant.deny')
            : null,
        n.expired ? t('userGrant.expired') : null,
        n.deniedByAncestor ? t('userGrant.deniedByAncestor') : null,
      ]
        .filter(Boolean)
        .join(' · ')
      return {
        menuId: n.menuId,
        path: paths.get(n.menuId)?.path ?? String(n.menuId),
        effective: n.effective,
        source: source || '—',
        grant: n.grant,
        grantorName: g?.grantorName,
        grantTime: g?.grantTime,
        expireTime: g?.expireTime,
        remark: g?.remark,
      }
    })
    .toSorted((a, b) => (paths.get(a.menuId)?.order ?? 0) - (paths.get(b.menuId)?.order ?? 0))
})

const dash = () => h('span', { class: 'faint' }, '—')
const columns: SmartTableColumn<Row>[] = [
  { key: 'path', title: () => t('userGrant.colMenu'), ellipsis: { tooltip: true } },
  {
    key: 'effective',
    title: () => t('userGrant.colState'),
    width: 90,
    render: r =>
      h(NTag, { size: 'small', bordered: false, type: r.effective ? 'success' : 'default' }, () =>
        t(r.effective ? 'userGrant.effective' : 'userGrant.ineffective'),
      ),
  },
  { key: 'source', title: () => t('userGrant.colSource'), ellipsis: { tooltip: true } },
  {
    key: 'grantorName',
    title: () => t('userGrant.colGrantor'),
    width: 120,
    ellipsis: { tooltip: true },
    render: r => r.grantorName || dash(),
  },
  { key: 'grantTime', title: () => t('userGrant.colGrantTime'), width: 170, format: 'datetime' },
  {
    key: 'expireTime',
    title: () => t('userGrant.colExpire'),
    width: 170,
    render: r =>
      r.grant == null
        ? dash()
        : r.expireTime
          ? r.expireTime.replace('T', ' ')
          : t('userGrant.expireLongTerm'),
  },
  {
    key: 'remark',
    title: () => t('userGrant.colRemark'),
    ellipsis: { tooltip: true },
    render: r => r.remark || dash(),
  },
]
</script>

<template>
  <SmartTable
    :columns="columns"
    :data="rows"
    row-key="menuId"
    :toolbar="false"
    :pagination="false"
    fill-height
  />
</template>
