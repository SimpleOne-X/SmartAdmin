import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { buildThemeOverrides } from '#/theme/naive-theme'

/**
 * 几处视觉 / 动效约定,锁住别被回退:
 *   表格有列竖线和外框、弹窗从 0.96 落定、按钮无波纹、侧栏收起有过渡。
 * 样式是纯文本,直接读文件断言(happy-dom 不加载 CSS 文件,算不出计算样式);真实渲染由 e2e / 手测兜底。
 */
const here = dirname(fileURLToPath(import.meta.url))
const read = (...p: string[]) => readFileSync(resolve(here, ...p), 'utf8')

describe('表格:列竖线和外框用 Naive 原生的', () => {
  const css = read('table.css')

  it('不压掉单元格竖线(竖线来自 Naive 的 :not(--single-line) 下 th / td 的 border-right)', () => {
    expect(css).not.toMatch(/border-right:\s*0/)
  })

  it('不压掉表格自己的外框(外框是 --bordered 下 wrapper 的 border)', () => {
    expect(css).not.toMatch(/n-data-table-wrapper\s*\{[^}]*border:\s*0/)
  })

  it('fill-height 表格的末行要画底线(行少时末行与外框之间是空白,不画就像没有横线)', () => {
    expect(css).toMatch(
      /\.n-data-table\.n-data-table--flex-height \.n-data-table-td\.n-data-table-td--last-row\s*\{\s*border-bottom:\s*1px solid var\(--n-merged-border-color\);/,
    )
  })

  it('不撑满的表格不全局画末行底线(外框紧贴末行,再画就是两道)', () => {
    expect(css).not.toMatch(
      /\.n-data-table \.n-data-table-td\.n-data-table-td--last-row\s*\{[^}]*border-bottom:\s*1px/,
    )
  })

  it('线色取 --hairline-strong:比 --separator 实,125% / 150% 缩放下亚像素偏移摊淡后仍看得见', () => {
    const root = document.documentElement
    root.style.setProperty('--hairline-strong', 'rgba(1, 2, 3, 0.4)')
    root.style.setProperty('--separator', 'rgba(9, 9, 9, 0.1)')
    try {
      const o = buildThemeOverrides({ dark: false, accent: '#0A84FF' })
      expect(o.DataTable?.borderColor).toBe('rgba(1, 2, 3, 0.4)')
    } finally {
      root.style.removeProperty('--hairline-strong')
      root.style.removeProperty('--separator')
    }
  })
})

describe('表格工具条:勾选后的批量栏与正常态同高', () => {
  const css = read('table.css')

  it('批量栏左半与右半图标组的高度跟 --control-h,不用库写死的 34px', () => {
    expect(css).toMatch(
      /\.smart-table-batch-info,\s*\.smart-table-toolbar\.smart-table-toolbar--batch:not\(\.smart-table-toolbar--batch-narrow\)\s*\.smart-table-toolbar-icons\s*\{\s*height:\s*var\(--control-h, 30px\);/,
    )
  })
})

describe('弹窗:从 0.96 落定,退场比进场快', () => {
  const css = read('index.css')
  const enterFrom =
    /:is\(\.n-card, \.n-dialog\)\.n-modal\.fade-in-scale-up-transition-enter-from,\s*:is\(\.n-card, \.n-dialog\)\.n-modal\.fade-in-scale-up-transition-leave-to\s*\{\s*transform:\s*scale\(0\.96\);/

  it('起点缩放是 0.96,不是 Naive 默认的 0.5', () => {
    expect(css).toMatch(enterFrom)
  })

  it('进场时长大于退场时长', () => {
    const dur = (phase: 'enter' | 'leave') => {
      const m = css.match(
        new RegExp(
          `\\.n-modal\\.fade-in-scale-up-transition-${phase}-active\\s*\\{\\s*transition-duration:\\s*([\\d.]+)s`,
        ),
      )
      expect(m, `找不到 ${phase}-active 的时长`).not.toBeNull()
      return Number(m![1])
    }
    expect(dur('leave')).toBeLessThan(dur('enter'))
  })
})

describe('按钮:点击无扩散波纹', () => {
  it('Button.rippleDuration 为 0s', () => {
    const o = buildThemeOverrides({ dark: false, accent: '#0A84FF' })
    expect(o.Button?.rippleDuration).toBe('0s')
  })
})

/** 递归收集 src 下的 .vue / .css(排除登录页、管理端的缩略预览 Mock:它们按比例缩小原样复刻真实界面) */
const sourceFiles = (dir = resolve(here, '..')): string[] =>
  readdirSync(dir).flatMap(name => {
    const p = resolve(dir, name)
    if (statSync(p).isDirectory()) return sourceFiles(p)
    return /\.(vue|css)$/.test(name) && !name.endsWith('Mock.vue') ? [p] : []
  })

describe('全站动效:不回弹', () => {
  it('cubic-bezier 的 y 值都在 0~1 内(y>1 是冲过头再回弹,macOS 没有这种手感)', () => {
    const bad: string[] = []
    for (const f of sourceFiles()) {
      const text = readFileSync(f, 'utf8')
      for (const m of text.matchAll(
        /cubic-bezier\(\s*[\d.-]+\s*,\s*([\d.-]+)\s*,\s*[\d.-]+\s*,\s*([\d.-]+)\s*\)/g,
      )) {
        if (Number(m[1]) < 0 || Number(m[1]) > 1 || Number(m[2]) < 0 || Number(m[2]) > 1) {
          bad.push(`${f}: ${m[0]}`)
        }
      }
    }
    expect(bad).toEqual([])
  })

  it('悬停不做位移上浮(卡片 / 按钮只改投影或底色)', () => {
    const bad: string[] = []
    for (const f of sourceFiles()) {
      const text = readFileSync(f, 'utf8')
      for (const m of text.matchAll(/:hover[^{}]*\{[^}]*transform:\s*(translate|scale)[^;}]*/g)) {
        bad.push(`${f}: ${m[0].replace(/\s+/g, ' ')}`)
      }
    }
    expect(bad).toEqual([])
  })
})

describe('条件栏按钮:图标槽的宽度不由页面干预', () => {
  // smart-naive-table 3.1 及以上的「查询」按钮图标槽常驻,loading 时在同一个槽里换成转圈、宽度不变。
  // 页面再把图标槽 display:none,loading 一开始按钮就缩窄一截(78→54),同排输入框被撑宽、「»」被顶着跑
  // (回收站点「查询」时的抖动就是这么来的)。要改按钮外观走库的 theme-overrides,不要在页面里隐藏图标槽。
  it('没有任何样式把 .n-button__icon 设成 display:none', () => {
    const bad: string[] = []
    for (const f of sourceFiles()) {
      const text = readFileSync(f, 'utf8')
      for (const m of text.matchAll(/[^{}]*\.n-button__icon[^{}]*\{[^}]*display:\s*none/g)) {
        bad.push(`${f}: ${m[0].trim().replace(/\s+/g, ' ').slice(0, 120)}`)
      }
    }
    expect(bad).toEqual([])
  })
})

describe('全站圆角:落在令牌尺度上', () => {
  // 令牌 5 / 6 / 9 / 14 / 18 / 22;2~4 留给滚动条、进度条、高亮标记这类细小元素;999 是胶囊
  const allowed = new Set([0, 2, 3, 4, 5, 6, 9, 14, 18, 22, 999])

  it('border-radius 的单值像素必须是尺度内的数,其余写 var(--radius-*)', () => {
    const bad: string[] = []
    for (const f of sourceFiles()) {
      const text = readFileSync(f, 'utf8')
      for (const m of text.matchAll(/border-radius:\s*(\d+)px\s*(?:!important)?\s*;/g)) {
        if (!allowed.has(Number(m[1]))) bad.push(`${f}: ${m[0]}`)
      }
    }
    expect(bad).toEqual([])
  })
})

describe('侧栏收起:有过渡而不是卸载', () => {
  const sfc = read('..', 'layouts', 'default.vue')

  it('aside 只按 showSider 挂载,收起时不用 v-if 卸载(卸载就没有过渡)', () => {
    const tag = sfc.match(/<aside\b[^>]*>/s)?.[0] ?? ''
    expect(tag).toContain('v-if="showSider"')
    expect(tag).not.toContain('!asideHidden')
  })

  it('收起的侧栏 inert,退出 Tab 序列与无障碍树', () => {
    expect(sfc.match(/<aside\b[^>]*>/s)?.[0]).toContain(':inert="asideHidden"')
  })

  it('宽度、描边、列间距都有过渡,且不把 grid 改成单列', () => {
    expect(sfc).toMatch(/\.aside \{[^}]*transition:\s*width var\(--aside-duration\)/s)
    expect(sfc).toMatch(/\.layout \{\s*transition:\s*column-gap/)
    expect(sfc).toMatch(/\.layout\.aside-hidden \{\s*column-gap:\s*0;/)
    expect(sfc).not.toMatch(/\.layout\.aside-hidden \{[^}]*grid-template-columns/)
  })
})
