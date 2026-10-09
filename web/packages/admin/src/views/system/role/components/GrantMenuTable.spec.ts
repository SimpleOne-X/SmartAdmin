import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * 授权菜单列表的两条界面约定,锁住别被回退。
 * 组件依赖 Naive 与 i18n,这里直接读源码断言(和 macosMotion.spec.ts 同一做法);真实渲染由手测 / e2e 兜底。
 */
const here = dirname(fileURLToPath(import.meta.url))
const src = readFileSync(resolve(here, 'GrantMenuTable.vue'), 'utf8')
const script = src.slice(0, src.indexOf('<template>'))
const template = src.slice(src.indexOf('<template>'), src.indexOf('<style'))

describe('授权菜单列表:应用切换', () => {
  it('永远是下拉(NSelect),不按应用数量在分段控件和下拉之间切换', () => {
    expect(template).toMatch(/<n-select\s+v-model:value="moduleId"/)
    expect(template).not.toMatch(/n-radio/)
    expect(script).not.toMatch(/NRadio|SEGMENT_MAX/)
  })
})

describe('授权菜单列表:按钮胶囊', () => {
  it('不挂悬浮气泡(权限码不在这里展示)', () => {
    expect(template).not.toMatch(/n-tooltip/i)
    expect(script).not.toMatch(/NTooltip/)
  })
})
