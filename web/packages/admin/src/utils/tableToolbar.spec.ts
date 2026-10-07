import { describe, expect, it } from 'vitest'
import { TABLE_MIN_ROW_HEIGHT, TABLE_TOOLBAR } from './tableToolbar'

describe('TABLE_TOOLBAR', () => {
  it('不带密度按钮:密度只在「系统设置」里全局调,表格上不放第二个入口', () => {
    expect(TABLE_TOOLBAR.density).toBeFalsy()
  })

  it('开刷新 / 放大还原 / 列设置:所有表格统一显示这三个内置图标', () => {
    expect(TABLE_TOOLBAR).toMatchObject({ refresh: true, columnSettings: true, maximize: true })
  })

  it('虚拟滚动行高下限不大于紧凑行的真实行高(约 33px),否则滚到底会少渲染最后几行', () => {
    expect(TABLE_MIN_ROW_HEIGHT).toBeLessThanOrEqual(32)
  })
})
