import { afterEach, describe, expect, it } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createI18n } from 'vue-i18n'
import AttemptTable from './AttemptTable.vue'
import type { SysJobLog } from '#/types/api'

// 执行记录详情抽屉里的「各次尝试」子表:SmartTable 静态数据模式,嵌入式 —— 没有条件构造器、没有工具栏。
let app: App<Element> | undefined

const row = (id: number, retryIndex: number, over: Partial<SysJobLog> = {}) =>
  ({
    id,
    retryIndex,
    runStatus: 2,
    startTime: '2026-10-06T08:00:00',
    endTime: '2026-10-06T08:00:01',
    elapsedMs: 1000,
    ...over,
  }) as SysJobLog

function mount(rows: SysJobLog[]) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp(() => h(AttemptTable, { rows }))
  app.use(
    createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: {
        'zh-CN': {
          common: { rowNo: '序号' },
          job: {
            log: {
              retryIndex: '次数',
              firstTry: '首次',
              retryN: '第 {n} 次重试',
              runStatus: '状态',
              startTime: '开始',
              elapsed: '耗时',
              success: '成功',
            },
          },
        },
      },
    }),
  )
  app.mount(host)
  return host
}

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('AttemptTable', () => {
  it('每次尝试一行:首次 + 各次重试,已结束的行显示耗时', async () => {
    const host = mount([row(1, 0), row(2, 1)])
    await nextTick()
    const text = host.textContent ?? ''
    expect(text).toContain('首次')
    expect(text).toContain('第 1 次重试')
    expect(text).toContain('1000 ms')
    expect(host.querySelectorAll('.n-data-table-tbody .n-data-table-tr').length).toBe(2)
  })

  it('运行中的行(endTime 为空)耗时显示「—」', async () => {
    const host = mount([row(1, 0, { endTime: null, elapsedMs: 0 })])
    await nextTick()
    expect(host.querySelector('.n-data-table-tbody')?.textContent).toContain('—')
  })

  it('嵌入式:没有条件构造器、没有工具栏', async () => {
    const host = mount([row(1, 0)])
    await nextTick()
    expect(host.querySelector('.smart-table-cond')).toBeNull()
    expect(host.querySelector('.smart-table-toolbar')).toBeNull()
  })
})
