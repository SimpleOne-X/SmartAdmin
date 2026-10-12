import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'

// n-select 的菜单在弹层里,换成直接把选项平铺出来的桩;remote 打在属性上,用来确认搜索交给本地过滤。
vi.mock('naive-ui', async importOriginal => {
  const { defineComponent, h: el } = await import('vue')
  return {
    ...(await importOriginal<typeof import('naive-ui')>()),
    NSelect: defineComponent({
      inheritAttrs: false,
      props: { options: { type: Array, default: () => [] }, remote: Boolean },
      setup(props) {
        return () =>
          el(
            'ul',
            { class: 'select-stub', 'data-remote': String(props.remote) },
            (props.options as { label: string; value: number }[]).map(o =>
              el('li', { 'data-value': String(o.value) }, o.label),
            ),
          )
      },
    }),
  }
})
vi.mock('#/api', () => ({ roleApi: { options: vi.fn() } }))

import { roleApi } from '#/api'
import RoleSelect from './index.vue'

const optionsMock = vi.mocked(roleApi.options)

let app: App<Element> | undefined

function mount(props: Record<string, unknown> = {}) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({ render: () => h(RoleSelect, props) })
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
  optionsMock.mockReset()
  optionsMock.mockResolvedValue([
    { id: 1, name: '管理员' },
    { id: 2, name: '审计员' },
    { id: 3, name: '普通用户' },
  ])
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('RoleSelect 数据源', () => {
  it('取数走 roleApi.options,选项 label 是角色名、value 是角色 id,搜索交给本地过滤', async () => {
    mount()
    await settle()

    expect(optionsMock).toHaveBeenCalledTimes(1)
    expect(items().map(li => [li.textContent, li.dataset.value])).toEqual([
      ['管理员', '1'],
      ['审计员', '2'],
      ['普通用户', '3'],
    ])
    expect(document.body.querySelector('.select-stub')!.getAttribute('data-remote')).toBe('false')
  })

  it('pageSize 限制下拉最多列出多少个角色', async () => {
    mount({ pageSize: 2 })
    await settle()
    expect(items().map(li => li.textContent)).toEqual(['管理员', '审计员'])
  })
})
