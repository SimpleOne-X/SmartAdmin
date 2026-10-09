// 用户授权弹窗的草稿状态(纯函数,组件只管渲染)。草稿 = 菜单 Id → 单独授权记录;没有记录就是「跟随角色」。
// 保存只提交变更集:与打开时的快照比,算出新增 / 修改(upserts)与移除(removes),没动的记录不提交。
// 到期按日期:前端只选日期,提交当天 23:59:59;普通管理员授允许的上限是今天 + 最长天数(后端同一口径)。
import { splitPermission, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect, type UserMenuGrantItem, type UserMenuGrantUpsert } from '#/types/api'

export type TriState = 'follow' | 'allow' | 'deny'

export interface DraftEntry {
  effect: UserMenuEffect
  /** 到期日 yyyy-MM-dd,当天最后一秒失效;null = 长期 */
  expireDate: string | null
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
      { effect: g.effect, expireDate: toExpireDate(g.expireTime), remark: g.remark ?? null },
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
  draft.set(menuId, { effect, expireDate, remark: cur?.remark ?? null })
}

const normRemark = (remark: string | null) => remark?.trim() || null

const sameEntry = (a: DraftEntry, b: DraftEntry) =>
  a.effect === b.effect &&
  a.expireDate === b.expireDate &&
  normRemark(a.remark) === normRemark(b.remark)

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
      expireTime: toExpireTime(entry.expireDate),
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

/** 普通管理员授允许的到期日上限 = 今天 + 最长天数(也是默认值);不限时为 null。 */
export function maxExpireDate(maxDays: number | null | undefined, today: Date): string | null {
  if (maxDays == null) return null
  return formatDate(new Date(today.getFullYear(), today.getMonth(), today.getDate() + maxDays))
}

/**
 * 要提交的记录里到期日不合规的菜单 Id:谁都不能选今天以前的日期;受限(maxDays 非空)时,
 * 「允许」还必须有到期日且不晚于上限。只看变更集,没动的老记录不校验(后端同样只校验变更集)。
 */
export function invalidExpiry(
  baseline: Draft,
  draft: Draft,
  maxDays: number | null | undefined,
  today: Date,
): number[] {
  const todayStr = formatDate(today)
  const max = maxExpireDate(maxDays, today)
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
