import { describe, expect, it } from 'vitest'
import { MenuType, type MenuTreeNode } from '#/types/menu'
import { UserMenuEffect } from '#/types/api'
import {
  countDraft,
  diffDraft,
  draftFromGrants,
  expiryLimit,
  invalidExpiry,
  isExpiryDateDisabled,
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

// 后端的到期时间不一定是 23:59:59(别的入口写的、或历史数据);日期没动就必须原样带回
const grantAt = (expireTime: string): Draft =>
  draftFromGrants([
    { menuId: 1, effect: UserMenuEffect.Allow, expireTime, remark: 'a', grantTime: 't' },
  ])

describe('非整点到期时间', () => {
  it('只改备注时沿用基线的原始到期时间串', () => {
    const draft = grantAt('2026-10-20T08:30:00')
    draft.get(1)!.remark = 'b'
    expect(diffDraft(grantAt('2026-10-20T08:30:00'), draft).upserts).toEqual([
      { menuId: 1, effect: UserMenuEffect.Allow, expireTime: '2026-10-20T08:30:00', remark: 'b' },
    ])
  })

  it('已过期的记录只改备注,不会被写成当天最后一秒而复活', () => {
    const draft = grantAt('2026-10-09T08:00:00') // TODAY 当天早上 8 点,此时 10 点已过期
    draft.get(1)!.remark = 'b'
    expect(diffDraft(grantAt('2026-10-09T08:00:00'), draft).upserts[0].expireTime).toBe(
      '2026-10-09T08:00:00',
    )
  })

  it('经 setTriState 切换效果时同样沿用原始到期时间串', () => {
    const draft = grantAt('2026-10-20T08:30:00')
    setTriState(draft, 1, 'deny', null)
    expect(diffDraft(grantAt('2026-10-20T08:30:00'), draft).upserts).toEqual([
      { menuId: 1, effect: UserMenuEffect.Deny, expireTime: '2026-10-20T08:30:00', remark: 'a' },
    ])
  })

  it('日期被改动才重新生成当天最后一秒', () => {
    const draft = grantAt('2026-10-20T08:30:00')
    draft.get(1)!.expireDate = '2026-10-25'
    expect(diffDraft(grantAt('2026-10-20T08:30:00'), draft).upserts[0].expireTime).toBe(
      '2026-10-25T23:59:59',
    )
  })

  it('日期改了又改回原值,等于没动', () => {
    const draft = grantAt('2026-10-20T08:30:00')
    draft.get(1)!.expireDate = '2026-10-25'
    draft.get(1)!.expireDate = '2026-10-20'
    expect(diffDraft(grantAt('2026-10-20T08:30:00'), draft)).toEqual({ upserts: [], removes: [] })
  })

  it('清掉到期日就是长期,不再带原始时间串', () => {
    const draft = grantAt('2026-10-20T08:30:00')
    draft.get(1)!.expireDate = null
    expect(diffDraft(grantAt('2026-10-20T08:30:00'), draft).upserts[0].expireTime).toBeNull()
  })
})

describe('到期日', () => {
  it('后备算法:浏览器今天 + 最长天数;不限时为 null', () => {
    expect(maxExpireDate(90, TODAY)).toBe('2027-01-07')
    expect(maxExpireDate(null, TODAY)).toBeNull()
  })

  it('上限取服务端给的最晚到期日,不看浏览器的今天', () => {
    // 服务器还在昨天时,最晚到期日比浏览器算出来的 2027-01-07 早一天
    expect(expiryLimit({ delegatedMaxDays: 90, delegatedMaxDate: '2027-01-06' }, TODAY)).toBe(
      '2027-01-06',
    )
    // 换一个远得多的今天,结果不变:根本没用浏览器的日期
    expect(
      expiryLimit(
        { delegatedMaxDays: 90, delegatedMaxDate: '2027-01-06' },
        new Date(2031, 0, 1, 10, 0, 0),
      ),
    ).toBe('2027-01-06')
  })

  it('服务端没给最晚到期日(自定义策略)才退回浏览器今天 + 最长天数;都没有就是不限', () => {
    expect(expiryLimit({ delegatedMaxDays: 90 }, TODAY)).toBe('2027-01-07')
    expect(expiryLimit({ delegatedMaxDays: 90, delegatedMaxDate: null }, TODAY)).toBe('2027-01-07')
    expect(expiryLimit({ delegatedMaxDays: null, delegatedMaxDate: null }, TODAY)).toBeNull()
    expect(expiryLimit(undefined, TODAY)).toBeNull()
  })

  it('日期选择器:今天以前、上限以后不可选,上限当天可选;不限时只管过去', () => {
    expect(isExpiryDateDisabled('2026-10-08', '2026-10-09', '2027-01-07')).toBe(true)
    expect(isExpiryDateDisabled('2026-10-09', '2026-10-09', '2027-01-07')).toBe(false)
    expect(isExpiryDateDisabled('2027-01-07', '2026-10-09', '2027-01-07')).toBe(false)
    expect(isExpiryDateDisabled('2027-01-08', '2026-10-09', '2027-01-07')).toBe(true)
    expect(isExpiryDateDisabled('2099-01-01', '2026-10-09', null)).toBe(false)
  })

  it('受限时允许必须有到期日且不晚于上限;拒绝不要求;任何人都不能选过去的日期', () => {
    const draft: Draft = new Map()
    setTriState(draft, 1, 'allow', null) // 缺到期日
    setTriState(draft, 2, 'allow', '2027-01-08') // 超上限一天
    setTriState(draft, 3, 'allow', '2027-01-07') // 正好上限
    setTriState(draft, 4, 'deny', null) // 拒绝不要求
    draft.set(5, { effect: UserMenuEffect.Deny, expireDate: '2026-10-08', remark: null }) // 过去
    expect(invalidExpiry(new Map(), draft, '2027-01-07', TODAY)).toEqual([1, 2, 5])
    expect(invalidExpiry(new Map(), draft, null, TODAY)).toEqual([5])
  })

  it('校验的上限就是传入的那一天,不再自己用今天加天数', () => {
    const draft: Draft = new Map()
    setTriState(draft, 1, 'allow', '2027-01-08') // 浏览器算出的上限是 2027-01-07,服务端给的更晚
    setTriState(draft, 2, 'allow', '2027-01-09')
    expect(invalidExpiry(new Map(), draft, '2027-01-08', TODAY)).toEqual([2])
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
        '2027-01-07',
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
