import { describe, expect, it } from 'vitest'
import { MenuType, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect } from '#/types/api'
import {
  countDraft,
  diffDraft,
  draftFromGrants,
  invalidExpiry,
  leakedCodes,
  maxExpireDate,
  setTriState,
  triStateOf,
  type Draft,
} from './userGrantState'

const TODAY = new Date(2026, 9, 9, 10, 0, 0) // 2026-10-09

const node = (
  id: number,
  type: MenuType,
  permission = '',
  children: MenuTreeNode[] = [],
): MenuTreeNode => ({
  id,
  parentId: 0,
  type,
  title: `n${id}`,
  permission,
  sort: 0,
  enabled: true,
  visible: true,
  children,
})

describe('草稿三态', () => {
  it('从授权记录建草稿;没有记录就是跟随角色', () => {
    const draft = draftFromGrants([
      {
        menuId: 1,
        effect: UserMenuEffect.Allow,
        expireTime: '2026-10-20T23:59:59',
        remark: 'x',
        grantTime: '2026-10-09T10:00:00',
      },
    ])
    expect(triStateOf(draft, 1)).toBe('allow')
    expect(draft.get(1)?.expireDate).toBe('2026-10-20')
    expect(triStateOf(draft, 2)).toBe('follow')
  })

  it('进入允许带默认到期日;切到拒绝保留已填的;回到跟随即删除', () => {
    const draft: Draft = new Map()
    setTriState(draft, 5, 'allow', '2027-01-07')
    expect(draft.get(5)).toEqual({
      effect: UserMenuEffect.Allow,
      expireDate: '2027-01-07',
      remark: null,
    })
    setTriState(draft, 5, 'deny', '2027-01-07')
    expect(draft.get(5)?.effect).toBe(UserMenuEffect.Deny)
    expect(draft.get(5)?.expireDate).toBe('2027-01-07')
    setTriState(draft, 5, 'follow', null)
    expect(draft.has(5)).toBe(false)
  })

  it('新进拒绝不带到期日(拒绝不要求限时)', () => {
    const draft: Draft = new Map()
    setTriState(draft, 6, 'deny', '2027-01-07')
    expect(draft.get(6)?.expireDate).toBeNull()
  })

  it('计数允许与拒绝', () => {
    const draft: Draft = new Map()
    setTriState(draft, 1, 'allow', null)
    setTriState(draft, 2, 'deny', null)
    setTriState(draft, 3, 'deny', null)
    expect(countDraft(draft)).toEqual({ allow: 1, deny: 2 })
  })
})

const baseline = (): Draft =>
  draftFromGrants([
    { menuId: 1, effect: UserMenuEffect.Allow, expireTime: null, remark: 'a', grantTime: 't' },
    { menuId: 2, effect: UserMenuEffect.Deny, expireTime: null, remark: null, grantTime: 't' },
  ])

describe('变更集', () => {
  it('没改动就是空变更集', () => {
    expect(diffDraft(baseline(), baseline())).toEqual({ upserts: [], removes: [] })
  })

  it('新增、修改进 upserts,删掉的进 removes;到期日转成当天最后一秒', () => {
    const draft = baseline()
    draft.get(1)!.remark = 'b'
    draft.delete(2)
    setTriState(draft, 3, 'allow', '2026-10-20')
    expect(diffDraft(baseline(), draft)).toEqual({
      upserts: [
        { menuId: 1, effect: UserMenuEffect.Allow, expireTime: null, remark: 'b' },
        {
          menuId: 3,
          effect: UserMenuEffect.Allow,
          expireTime: '2026-10-20T23:59:59',
          remark: null,
        },
      ],
      removes: [2],
    })
  })

  it('备注首尾空白不算改动', () => {
    const draft = baseline()
    draft.get(1)!.remark = ' a '
    expect(diffDraft(baseline(), draft).upserts).toEqual([])
  })
})

describe('到期日', () => {
  it('上限 = 今天 + 最长天数;不限时为 null', () => {
    expect(maxExpireDate(90, TODAY)).toBe('2027-01-07')
    expect(maxExpireDate(null, TODAY)).toBeNull()
  })

  it('受限时允许必须有到期日且不晚于上限;拒绝不要求;任何人都不能选过去的日期', () => {
    const draft: Draft = new Map()
    setTriState(draft, 1, 'allow', null) // 缺到期日
    setTriState(draft, 2, 'allow', '2027-01-08') // 超上限一天
    setTriState(draft, 3, 'allow', '2027-01-07') // 正好上限
    setTriState(draft, 4, 'deny', null) // 拒绝不要求
    draft.set(5, { effect: UserMenuEffect.Deny, expireDate: '2026-10-08', remark: null }) // 过去
    expect(invalidExpiry(new Map(), draft, 90, TODAY)).toEqual([1, 2, 5])
    expect(invalidExpiry(new Map(), draft, null, TODAY)).toEqual([5])
  })

  it('没改动的老记录不校验', () => {
    const old = draftFromGrants([
      { menuId: 9, effect: UserMenuEffect.Allow, expireTime: null, remark: null, grantTime: 't' },
    ])
    expect(
      invalidExpiry(
        old,
        draftFromGrants([
          {
            menuId: 9,
            effect: UserMenuEffect.Allow,
            expireTime: null,
            remark: null,
            grantTime: 't',
          },
        ]),
        90,
        TODAY,
      ),
    ).toEqual([])
  })
})

describe('漏网接口', () => {
  // 10 页面 → 11 按钮(a;shared);20 页面 → 21 按钮(shared)
  const tree = [
    node(10, MenuType.Menu, '', [node(11, MenuType.Button, 'GET:/a;GET:/shared')]),
    node(20, MenuType.Menu, '', [node(21, MenuType.Button, 'GET:/shared')]),
  ]

  it('被拒子树里的码还由子树外的有效节点携带时列出来', () => {
    expect(leakedCodes(tree, new Set([11, 21]), 10)).toEqual([
      { code: 'GET:/shared', carriers: [21] },
    ])
  })

  it('携带节点不有效,或只在子树里,就没有漏网', () => {
    expect(leakedCodes(tree, new Set([11]), 10)).toEqual([])
    expect(leakedCodes(tree, new Set([21]), 999)).toEqual([])
  })
})
