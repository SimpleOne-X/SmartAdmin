// 用户授权弹窗的草稿状态(纯函数,组件只管渲染)。草稿 = 菜单 Id → 单独授权记录;没有记录就是「跟随角色」。
// 保存只提交变更集:与打开时的快照比,算出新增 / 修改(upserts)与移除(removes),没动的记录不提交。
// 到期按日期:前端只选日期,新选的日期提交当天 23:59:59;日期没动的老记录原样带回它的到期时间串
//(后端存的不一定是 23:59:59,只改备注不能把它悄悄改写,更不能让已过期的记录重新生效)。
// 普通管理员授允许的上限是服务端给出的最晚到期日(服务器本地日期 + 最长天数),校验与展示共用这一天。
import { splitPermission, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuGrantItem, type UserMenuGrantUpsert } from '#/types/api'

export type TriState = 'follow' | 'allow' | 'deny'

export interface DraftEntry {
  effect: UserMenuEffect
  /** 到期日 yyyy-MM-dd;null = 长期 */
  expireDate: string | null
  /** 基线记录的原始到期时间串,只在日期没被改动时原样带回;新建的记录没有 */
  expireTime?: string | null
  remark: string | null
}
export type Draft = Map<number, DraftEntry>

/** 弹窗只读的原因码 → 文案 key(直接复用后端错误码的 msgKey 文案)。 */
export const READONLY_REASON_KEYS: Record<number, string> = {
  42029: 'error.user.cannotOperateSelf',
  42007: 'error.user.superAdminProtected',
  41005: 'error.user.outOfDataScope',
  41007: 'error.perm.targetIsDelegatedAdmin',
}

const pad = (n: number) => String(n).padStart(2, '0')

/** 本地日期 yyyy-MM-dd。 */
export const formatDate = (d: Date) =>
  `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`

/** 后端到期时间(本地时间串)→ 日期部分。 */
export const toExpireDate = (expireTime?: string | null) =>
  expireTime ? expireTime.slice(0, 10) : null

/** 日期 → 后端到期时间:当天最后一秒失效。 */
export const toExpireTime = (date: string | null) => (date ? `${date}T23:59:59` : null)

export function draftFromGrants(grants: UserMenuGrantItem[]): Draft {
  return new Map(
    grants.map(g => [
      g.menuId,
      {
        effect: g.effect,
        expireDate: toExpireDate(g.expireTime),
        expireTime: g.expireTime ?? null,
        remark: g.remark ?? null,
      },
    ]),
  )
}

export function triStateOf(draft: Draft, menuId: number): TriState {
  const entry = draft.get(menuId)
  if (!entry) return 'follow'
  return entry.effect === UserMenuEffect.Allow ? 'allow' : 'deny'
}

/**
 * 切换三态。从「跟随」进入允许时带上默认到期日(普通管理员授允许必须限时,默认给到上限);
 * 进入拒绝不带(拒绝只会收紧,不要求限时);已有记录切换效果时保留已填的到期日与备注。
 */
export function setTriState(
  draft: Draft,
  menuId: number,
  next: TriState,
  defaultExpireDate: string | null,
): void {
  if (next === 'follow') {
    draft.delete(menuId)
    return
  }
  const effect = next === 'allow' ? UserMenuEffect.Allow : UserMenuEffect.Deny
  const cur = draft.get(menuId)
  const expireDate = cur?.expireDate ?? (effect === UserMenuEffect.Allow ? defaultExpireDate : null)
  draft.set(menuId, {
    effect,
    expireDate,
    expireTime: cur?.expireTime,
    remark: cur?.remark ?? null,
  })
}

const normRemark = (remark: string | null) => remark?.trim() || null

const sameEntry = (a: DraftEntry, b: DraftEntry) =>
  a.effect === b.effect &&
  a.expireDate === b.expireDate &&
  normRemark(a.remark) === normRemark(b.remark)

/** 提交用的到期时间:日期没动就沿用原始时间串,日期被改动(或新选)才取当天最后一秒。 */
const expireTimeOf = (entry: DraftEntry) =>
  entry.expireTime && toExpireDate(entry.expireTime) === entry.expireDate
    ? entry.expireTime
    : toExpireTime(entry.expireDate)

/** 与打开时的快照比出变更集。按菜单 Id 升序,提交内容稳定。 */
export function diffDraft(
  baseline: Draft,
  draft: Draft,
): { upserts: UserMenuGrantUpsert[]; removes: number[] } {
  const upserts: UserMenuGrantUpsert[] = []
  for (const [menuId, entry] of draft) {
    const before = baseline.get(menuId)
    if (before && sameEntry(before, entry)) continue
    upserts.push({
      menuId,
      effect: entry.effect,
      expireTime: expireTimeOf(entry),
      remark: normRemark(entry.remark),
    })
  }
  const removes = [...baseline.keys()].filter(id => !draft.has(id))
  return {
    upserts: upserts.toSorted((a, b) => a.menuId - b.menuId),
    removes: removes.toSorted((a, b) => a - b),
  }
}

export function countDraft(draft: Draft): { allow: number; deny: number } {
  let allow = 0
  let deny = 0
  for (const entry of draft.values()) {
    if (entry.effect === UserMenuEffect.Allow) allow++
    else deny++
  }
  return { allow, deny }
}

/** 浏览器的今天 + 最长天数;不限时为 null。只在服务端没给出最晚到期日时作后备。 */
export function maxExpireDate(maxDays: number | null | undefined, today: Date): string | null {
  if (maxDays == null) return null
  return formatDate(new Date(today.getFullYear(), today.getMonth(), today.getDate() + maxDays))
}

/**
 * 普通管理员授允许的到期日上限(也是默认值);不限时为 null。
 * 服务端给了 delegatedMaxDate 就直接用它:那是服务器本地日期 + 最长天数,与后端校验是同一份计算。
 * 浏览器与服务器不在同一时区时(服务器 UTC、用户东八区),浏览器自己加天数每天凌晨会领先一天,选出被后端拒收的上限。
 * 日期串只做字符串比较,不转成 Date(new Date('yyyy-MM-dd') 按 UTC 解析,在东八区以西的时区会差一天)。
 * 没给(自定义的策略实现没提供最晚到期日)才退回浏览器的今天 + delegatedMaxDays。
 */
export function expiryLimit(
  effective:
    { delegatedMaxDate?: string | null; delegatedMaxDays?: number | null } | null | undefined,
  today: Date,
): string | null {
  if (effective?.delegatedMaxDate) return effective.delegatedMaxDate
  return maxExpireDate(effective?.delegatedMaxDays, today)
}

/** 日期选择器里不可选的日子:今天以前,以及(受限时)最晚到期日以后。三个参数都是 yyyy-MM-dd,按字符串比较。 */
export const isExpiryDateDisabled = (date: string, today: string, max: string | null): boolean =>
  date < today || (max != null && date > max)

/**
 * 要提交的记录里到期日不合规的菜单 Id:谁都不能选今天以前的日期;受限(max 非空,即 expiryLimit 的结果)时,
 * 「允许」还必须有到期日且不晚于上限。只看变更集,没动的老记录不校验(后端同样只校验变更集)。
 */
export function invalidExpiry(
  baseline: Draft,
  draft: Draft,
  max: string | null,
  today: Date,
): number[] {
  const todayStr = formatDate(today)
  return diffDraft(baseline, draft)
    .upserts.filter(u => {
      const date = toExpireDate(u.expireTime)
      if (date && date < todayStr) return true
      if (u.effect !== UserMenuEffect.Allow || max == null) return false
      return !date || date > max
    })
    .map(u => u.menuId)
}

/**
 * 拒掉 menuId(连同子孙)后,仍由子树外的有效节点携带的权限码 → 携带它们的节点 Id。
 * 规则同后端 UserMenuGrantRules.LeakedCodes;这里给还没保存的「拒绝」就地提示用,effective 取打开时的有效节点。
 */
export function leakedCodes(
  tree: MenuTreeNode[],
  effective: Set<number>,
  menuId: number,
): { code: string; carriers: number[] }[] {
  const byId = new Map<number, MenuTreeNode>()
  const index = (nodes: MenuTreeNode[]) => {
    for (const n of nodes) {
      byId.set(n.id, n)
      index(n.children)
    }
  }
  index(tree)
  const root = byId.get(menuId)
  if (!root) return []

  const subtree = new Set<number>()
  const collect = (n: MenuTreeNode) => {
    subtree.add(n.id)
    n.children.forEach(collect)
  }
  collect(root)
  const denied = new Set([...subtree].flatMap(id => splitPermission(byId.get(id)!.permission)))
  if (!denied.size) return []

  const carriers = new Map<string, number[]>()
  for (const id of effective) {
    if (subtree.has(id)) continue
    const n = byId.get(id)
    if (!n) continue
    for (const code of splitPermission(n.permission))
      if (denied.has(code)) carriers.set(code, [...(carriers.get(code) ?? []), id])
  }
  return [...carriers.keys()]
    .toSorted()
    .map(code => ({ code, carriers: carriers.get(code)!.toSorted((a, b) => a - b) }))
}
