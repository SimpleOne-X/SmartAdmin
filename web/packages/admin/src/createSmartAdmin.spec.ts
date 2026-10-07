import { iconLoaded } from '@iconify/vue'
import { describe, expect, it } from 'vitest'
import { SMART_TABLE_DEFAULTS, defaultFilterSerializer } from 'smart-naive-table'
import { createSmartAdmin } from '#/createSmartAdmin'
import { flatFilterSerializer } from '#/utils/tableFilter'

const set = (name: string) => ({
  prefix: 'ph',
  icons: { [name]: { body: '<path d="M0 0h8v8H0z"/>' } },
  width: 256,
  height: 256,
})

// app.provide 的值落在 _context.provides 上,按注入键取出来看实际生效的默认值
const tableDefaults = (app: ReturnType<typeof createSmartAdmin>['app']) =>
  (app as unknown as { _context: { provides: Record<symbol, any> } })._context.provides[
    SMART_TABLE_DEFAULTS as symbol
  ]

describe('createSmartAdmin', () => {
  it('收集插件与应用两层的 iconSets,启动时同步注册', () => {
    expect(iconLoaded('ph:truck-duotone')).toBe(false)
    expect(iconLoaded('ph:anchor')).toBe(false)
    createSmartAdmin({ plugins: [{ iconSets: [set('truck-duotone')] }], iconSets: [set('anchor')] })
    expect(iconLoaded('ph:truck-duotone')).toBe(true)
    expect(iconLoaded('ph:anchor')).toBe(true)
  })

  it('表格过滤序列化器默认用内核的扁平版本', () => {
    const { app } = createSmartAdmin()
    expect(tableDefaults(app).filterSerializer).toBe(flatFilterSerializer)
  })

  it('表格列宽拖拽全站默认开启,应用可以用 table.resizable 关掉', () => {
    expect(tableDefaults(createSmartAdmin().app).resizable).toBe(true)
    expect(tableDefaults(createSmartAdmin({ table: { resizable: false } }).app).resizable).toBe(
      false,
    )
  })

  it('应用可以用 table.filterSerializer 换回 3.0 默认协议', () => {
    const { app } = createSmartAdmin({ table: { filterSerializer: defaultFilterSerializer } })
    expect(tableDefaults(app).filterSerializer).toBe(defaultFilterSerializer)
  })
})
