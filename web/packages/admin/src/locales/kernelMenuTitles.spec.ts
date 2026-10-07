/// <reference types="node" />
// 要读后端种子源文件做对账,tsconfig 的 types 只给 vite/client,故显式引 node 类型。
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, describe, expect, it } from 'vitest'
import { i18n } from '#/locales'
import zhCN from './zh-CN'
import enUS from './en-US'
import { KERNEL_MENU_TITLES } from './kernelMenuTitles'
import { MENU_TITLE_TEXT_KEYS, registerMenuTitles, translateMenuTitle } from './menuTitle'

// 种子里的菜单 / 应用标题是中文,侧栏等处全靠「标题原文 → key」表翻译;表漏一条,
// 那一项在英文界面下就静默显示中文,不会报错。这里把「表、两份语言包、后端种子」三方对齐钉成硬约束。

const lookup = (messages: unknown, key: string): unknown =>
  key
    .split('.')
    .reduce<unknown>((acc, k) => (acc as Record<string, unknown> | undefined)?.[k], messages)

const CJK = /[㐀-鿿]/

describe('内核种子标题译名表', () => {
  it('每个 key 在 zh-CN 与 en-US 里都有非空文案', () => {
    const missing = Object.values(KERNEL_MENU_TITLES).filter(
      key => !lookup(zhCN, key) || !lookup(enUS, key),
    )
    expect(missing).toEqual([])
  })

  it('zh-CN 文案就是种子里的中文原文(中文界面显示不变)', () => {
    const drift = Object.entries(KERNEL_MENU_TITLES).filter(([zh, key]) => lookup(zhCN, key) !== zh)
    expect(drift).toEqual([])
  })

  it('en-US 文案不含中文', () => {
    const left = Object.values(KERNEL_MENU_TITLES).filter(key =>
      CJK.test(String(lookup(enUS, key))),
    )
    expect(left).toEqual([])
  })

  it('menuTitle 命名空间里没有表里用不到的游离文案', () => {
    const used = new Set(Object.values(KERNEL_MENU_TITLES))
    const all: string[] = []
    const walk = (node: Record<string, unknown>, prefix: string) => {
      for (const [k, v] of Object.entries(node)) {
        const p = `${prefix}.${k}`
        if (typeof v === 'string') all.push(p)
        else walk(v as Record<string, unknown>, p)
      }
    }
    walk((zhCN as Record<string, unknown>).menuTitle as Record<string, unknown>, 'menuTitle')
    expect(all.filter(k => !used.has(k))).toEqual([])
  })

  // 后端种子新增 / 改名标题却忘了同步这里,就是本用例红。仓库外(只拿到 web/ 的拷贝)读不到源文件时整组跳过。
  const seedDir = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '../../../../../backend/src/SmartAdmin.Services/Seed',
  )
  const seedFiles = ['DefaultMenuSeed.cs', 'DefaultModuleSeed.cs'].map(f => path.join(seedDir, f))
  it.skipIf(!seedFiles.every(existsSync))('后端种子里的每个 Title 都有译名', () => {
    const titles = new Set(
      seedFiles.flatMap(f =>
        [...readFileSync(f, 'utf8').matchAll(/Title = "([^"]+)"/g)].map(m => m[1]!),
      ),
    )
    expect(titles.size).toBeGreaterThan(100) // 防正则失效后空集合假绿
    expect([...titles].filter(title => !(title in KERNEL_MENU_TITLES)).toSorted()).toEqual([])
    expect(
      Object.keys(KERNEL_MENU_TITLES)
        .filter(title => !titles.has(title))
        .toSorted(),
    ).toEqual([])
  })
})

describe('英文界面下的侧栏标题', () => {
  const original = i18n.global.locale.value
  afterEach(() => {
    i18n.global.locale.value = original
  })

  it('注册内核表后,种子中文标题(目录 / 页面 / 按钮 / 应用名)翻成英文', () => {
    registerMenuTitles(KERNEL_MENU_TITLES)
    expect(Object.keys(MENU_TITLE_TEXT_KEYS).length).toBe(Object.keys(KERNEL_MENU_TITLES).length)
    i18n.global.locale.value = 'en-US'
    expect(translateMenuTitle('组织管理')).toBe('Organization')
    expect(translateMenuTitle('用户管理', '/system/user')).toBe('Users')
    expect(translateMenuTitle('用户-重置密码')).toBe('User - Reset Password')
    expect(translateMenuTitle('业务中心')).toBe('Business Center')
  })

  it('中文界面保持原文;库里被改过名的标题不翻', () => {
    registerMenuTitles(KERNEL_MENU_TITLES)
    expect(translateMenuTitle('用户管理')).toBe('用户管理')
    i18n.global.locale.value = 'en-US'
    expect(translateMenuTitle('员工档案')).toBe('员工档案')
  })

  it('消费方的 menuTitles 同键覆盖内核表', () => {
    registerMenuTitles(KERNEL_MENU_TITLES)
    registerMenuTitles({ 用户管理: 'menuTitle.page.role' })
    i18n.global.locale.value = 'en-US'
    expect(translateMenuTitle('用户管理')).toBe('Roles')
  })
})
