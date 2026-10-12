import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'

// n-select 的菜单在弹层里,换成直接把选项平铺出来的桩,断言选项数据就够了。
vi.mock('naive-ui', async importOriginal => {
  const { defineComponent, h: el } = await import('vue')
  return {
    ...(await importOriginal<typeof import('naive-ui')>()),
    NSelect: defineComponent({
      inheritAttrs: false,
      props: { options: { type: Array, default: () => [] } },
      setup(props) {
        return () =>
          el(
            'ul',
            { class: 'select-stub' },
            (props.options as { label: string; value: number }[]).map(o =>
              el('li', { 'data-value': String(o.value) }, o.label),
            ),
          )
      },
    }),
  }
})
vi.mock('#/api', () => ({ userApi: { options: vi.fn() } }))

import { userApi } from '#/api'
import UserSelect from './index.vue'

const optionsMock = vi.mocked(userApi.options)

let app: App<Element> | undefined
const orgId = ref<number | null>(null)

function mount(props: Record<string, unknown> = {}) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({ render: () => h(UserSelect, { orgId: orgId.value, ...props }) })
  app.mount(host)
}

async function settle() {
  for (let i = 0; i < 5; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

const items = () => [...document.body.querySelectorAll('li')]

beforeEach(() => {
  orgId.value = null
  optionsMock.mockReset()
  optionsMock.mockResolvedValue({
    items: [
      { id: 1, name: '张三', account: 'zhangsan' },
      { id: 2, name: '李四', account: 'lisi' },
    ],
    total: 2,
  })
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('UserSelect 数据源', () => {
  it('取数走 userApi.options,选项 label 是「姓名(账号)」、value 是用户 id', async () => {
    mount()
    await settle()

    expect(optionsMock).toHaveBeenCalledTimes(1)
    expect(optionsMock).toHaveBeenCalledWith({
      page: 1,
      pageSize: 50,
      keyword: undefined,
      orgId: undefined,
    })
    expect(items().map(li => [li.textContent, li.dataset.value])).toEqual([
      ['张三(zhangsan)', '1'],
      ['李四(lisi)', '2'],
    ])
  })

  it('pageSize 属性决定一次取多少人', async () => {
    mount({ pageSize: 200 })
    await settle()
    expect(optionsMock.mock.calls[0]![0]).toMatchObject({ pageSize: 200 })
  })

  it('orgId 变化后重新取数,并带上机构', async () => {
    mount()
    await settle()
    optionsMock.mockClear()

    orgId.value = 9
    await settle()
    expect(optionsMock).toHaveBeenCalledTimes(1)
    expect(optionsMock.mock.calls[0]![0]).toMatchObject({ orgId: 9 })
  })
})
