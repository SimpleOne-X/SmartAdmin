import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { buildThemeOverrides } from '#/theme/naive-theme'

/**
 * 弹窗 / 浮层里的表格:Naive 的 DataTable 放进 .n-modal / .n-popover 时,单元格底色改取 *Modal / *Popover 变体,
 * 变体默认由全局 modalColor / popoverColor(= --bg-elevated,88% 不透明)派生。
 * 固定列 sticky 叠在别的列上,底色只要有一丝透明,下面滚过的列的字就会透出来,所以这些变体必须与非 Modal 变体同为实色。
 * happy-dom 不加载 CSS 文件,令牌值在这里手工写入根元素;令牌文件本身的实色性读 tokens.css 文本断言。
 */
const here = dirname(fileURLToPath(import.meta.url))

const BASES = [
  'tdColor',
  'tdColorHover',
  'tdColorSorting',
  'tdColorStriped',
  'thColor',
  'thColorHover',
  'thColorSorting',
] as const
const SURFACES = ['Modal', 'Popover'] as const

describe('DataTable 的 Modal / Popover 底色变体', () => {
  const tokens: Record<string, string> = {
    '--bg-card': '#ffffff',
    '--th-bg': '#f4f7fb',
    '--th-bg-hover': '#edf2f9',
    '--td-bg-hover': '#eff4fb',
    // 弹层表面故意用半透明:变体若误取这些令牌,下面的断言会红
    '--bg-elevated': 'rgba(252, 253, 255, 0.88)',
    '--glass-solid': 'rgba(255, 255, 255, 0.96)',
  }

  function build() {
    const root = document.documentElement
    for (const [k, val] of Object.entries(tokens)) root.style.setProperty(k, val)
    try {
      return buildThemeOverrides({ dark: false, accent: '#0A84FF' }).DataTable as Record<
        string,
        unknown
      >
    } finally {
      for (const k of Object.keys(tokens)) root.style.removeProperty(k)
    }
  }

  for (const surface of SURFACES) {
    it(`${surface} 变体全部有值,且与对应的非 ${surface} 变体取值一致`, () => {
      const table = build()
      for (const base of BASES) {
        const key = `${base}${surface}`
        expect(table[base], `${base} 本身缺失`).toBeTruthy()
        expect(table[key], `${key} 没有覆盖`).toBe(table[base])
      }
    })

    it(`${surface} 变体都是不透明的 #rrggbb,不取半透明的弹层令牌`, () => {
      const table = build()
      for (const base of BASES) {
        expect(String(table[`${base}${surface}`]), `${base}${surface}`).toMatch(/^#[0-9a-f]{6}$/i)
      }
    })
  }
})

describe('DataTable 底色取的令牌在亮、暗两套里都是实色', () => {
  const css = readFileSync(resolve(here, '../styles/tokens.css'), 'utf8')

  for (const name of ['--bg-card', '--th-bg', '--th-bg-hover', '--td-bg-hover']) {
    it(`${name} 亮色 / 暗色都是 #rrggbb`, () => {
      const values = [...css.matchAll(new RegExp(`${name}:\\s*([^;]+);`, 'g'))].map(m =>
        m[1]!.trim(),
      )
      // :root 一份、暗色块一份
      expect(values).toHaveLength(2)
      for (const value of values) expect(value).toMatch(/^#[0-9a-f]{6}$/i)
    })
  }
})
