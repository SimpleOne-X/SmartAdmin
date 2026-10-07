import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { ACCENTS } from '#/theme/accents'
import { deriveAccentTokens } from '#/theme/accentTokens'

/**
 * 令牌契约:页面按令牌名写 var(--xxx),这些名字必须在 :root 里都有定义;--color-* 等旧命名保留为别名。
 * tokens.css 是纯文本,这里直接读文件解析,不依赖浏览器的样式计算(happy-dom 不加载 CSS 文件)。
 */
const css = readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), 'tokens.css'), 'utf8')

/** 取出某个顶层规则块的正文(括号配对,兼容值里的 rgba()/var()/渐变)。 */
function block(selector: string): string {
  const start = css.indexOf(`\n${selector} {`)
  expect(start, `找不到 ${selector}`).toBeGreaterThanOrEqual(0)
  let depth = 0
  for (let i = css.indexOf('{', start); i < css.length; i++) {
    if (css[i] === '{') depth++
    else if (css[i] === '}' && --depth === 0) return css.slice(css.indexOf('{', start) + 1, i)
  }
  throw new Error(`${selector} 的括号不配对`)
}

const decls = (body: string) =>
  new Map([...body.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)].map(m => [m[1]!, m[2]!.trim()]))
const root = decls(block(':root'))
const dark = decls(block(":root[data-theme='dark']"))

/** 页面与主题运行时依赖的基础令牌名:任何一个在 :root 里缺失,页面就会取不到色。 */
const PROTOTYPE_TOKENS = [
  '--font',
  '--font-mono',
  '--bg-app',
  '--bg-sidebar',
  '--bg-header',
  '--bg-elevated-a',
  '--text-1',
  '--text-2',
  '--text-3',
  '--border',
  '--separator',
  '--fill',
  '--hover',
  '--hairline',
  '--hairline-strong',
  '--glass',
  '--glass-strong',
  '--edge-top',
  '--edge-mid',
  '--edge-bottom',
  '--edge-light',
  '--ambient-1',
  '--ambient-2',
  '--ambient-3',
  '--ambient-4',
  '--noise-opacity',
  '--accent',
  '--signal',
  '--signal-glow',
  '--mark-bg',
  '--mark-fg',
  '--tab-strip',
  '--tab-active',
  '--tab-active-bg',
  '--tab-on-bg',
  '--tab-on-fg',
  '--tab-on-ring',
  '--mask',
  '--num-a',
  '--num-b',
  '--glass-solid',
  '--tab-active-fg',
  '--tab-hover',
  '--wallpaper',
  '--glass-panel',
  '--glass-border',
  '--glass-shadow',
  '--glass-shadow-soft',
  '--tab-active-ring',
  '--ok',
  '--err',
  '--warn',
  '--acc-solid',
  '--on-acc',
  '--sel-bg',
  '--sel-ring',
  '--sel-halo',
  '--sel-fg',
  '--shadow-1',
  '--shadow-2',
  '--login-accent-2',
  '--desk-base',
  '--desk-op',
  '--desk-1',
  '--desk-2',
  '--desk-3',
  '--desk-4',
  // 不由运行时写入、只在 tokens.css 里静态定义的名字
  '--input',
  '--bg-card',
  '--bg-elevated',
  '--text-4',
  '--info',
  '--success',
  '--warning',
  '--error',
]

/** 旧命名的令牌(--color-* 等)——兼容别名,必须仍然存在。 */
const LEGACY_TOKENS = [
  '--color-primary',
  '--color-primary-hover',
  '--color-primary-pressed',
  '--color-primary-light',
  '--color-success',
  '--color-success-bg',
  '--color-warning',
  '--color-warning-bg',
  '--color-danger',
  '--color-danger-bg',
  '--color-info',
  '--color-info-bg',
  '--color-bg-body',
  '--color-bg-app',
  '--color-bg-container',
  '--color-bg-container-solid',
  '--color-bg-elevated',
  '--color-bg-input',
  '--color-text-primary',
  '--color-text-secondary',
  '--color-text-tertiary',
  '--color-text-disabled',
  '--color-border',
  '--color-border-strong',
  '--color-fill',
  '--color-fill-hover',
  '--color-fill-disabled',
  '--color-mask',
  '--color-header-bg',
  '--font-family-base',
  '--font-family-mono',
  '--shadow-3',
  '--radius-sm',
  '--radius-md',
  '--radius-lg',
  '--radius-xl',
  '--font-size-xs',
  '--font-size-sm',
  '--font-size-base',
  '--font-size-md',
  '--font-size-lg',
  '--font-size-xl',
  '--font-weight-medium',
  '--font-weight-semibold',
  '--transition-fast',
  '--transition-base',
]

const norm = (s: string) => s.replace(/\s+/g, '').toLowerCase()

describe('tokens.css 令牌契约', () => {
  it('设计令牌名在 :root 里都有定义', () => {
    expect(PROTOTYPE_TOKENS.filter(n => !root.has(n))).toEqual([])
  })

  it('旧令牌名都保留着', () => {
    expect(LEGACY_TOKENS.filter(n => !root.has(n))).toEqual([])
  })

  it('旧的颜色令牌是别名(引用新令牌),不是另存一份值', () => {
    const colorTokens = LEGACY_TOKENS.filter(
      n => n.startsWith('--color-') && !n.startsWith('--color-gray'),
    )
    const notAlias = colorTokens.filter(n => !/^var\(--[\w-]+\)$/.test(root.get(n) ?? ''))
    expect(notAlias).toEqual([])
  })

  it('暗色块只覆盖 :root 里已有的令牌(不会冒出只在暗色存在的名字)', () => {
    expect([...dark.keys()].filter(n => !root.has(n))).toEqual([])
  })

  it('随明暗翻转的核心令牌在暗色块里都有覆盖', () => {
    const flip = [
      '--bg-app',
      '--bg-card',
      '--bg-elevated',
      '--text-1',
      '--text-2',
      '--text-3',
      '--text-4',
      '--border',
      '--separator',
      '--hairline',
      '--fill',
      '--hover',
      '--glass',
      '--glass-panel',
      '--glass-border',
      '--glass-shadow',
      '--mask',
      '--ok',
      '--warn',
      '--err',
      '--info',
      '--shadow-1',
      '--shadow-2',
      '--shadow-3',
      '--wallpaper',
      '--desk-base',
      '--bg-sidebar',
      '--bg-header',
    ]
    expect(flip.filter(n => !dark.has(n))).toEqual([])
  })

  it('整站不画网格:--grid-line / --grid-dot 不能出现', () => {
    expect(css).not.toMatch(/--grid-(line|dot)\s*:/)
  })

  it('useTheme 运行时写入的强调色令牌,tokens.css 里都有默认蓝的初始值(首帧不空)', () => {
    for (const dk of [false, true]) {
      const { css: vars } = deriveAccentTokens(ACCENTS[0], dk)
      expect(Object.keys(vars).filter(n => !root.has(n))).toEqual([])
    }
  })

  it('初始值与默认蓝实际算出来的一致(改了推导规则必须同步改 tokens.css)', () => {
    for (const dk of [false, true]) {
      const table = dk ? new Map([...root, ...dark]) : root
      const { css: vars } = deriveAccentTokens(ACCENTS[0], dk)
      const stale = Object.entries(vars).filter(
        ([n, val]) => norm(table.get(n) ?? '') !== norm(val),
      )
      expect(stale.map(([n]) => n)).toEqual([])
    }
  })

  it('字体走系统字体栈,不引入 Inter / JetBrains Mono / Noto Sans SC 等未打包字体', () => {
    expect(root.get('--font')).not.toMatch(/Inter|Noto/)
    expect(root.get('--font-mono')).not.toMatch(/JetBrains/)
  })
})
