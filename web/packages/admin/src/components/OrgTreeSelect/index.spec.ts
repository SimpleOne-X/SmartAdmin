import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createI18n } from 'vue-i18n'
import zhCN from '#/locales/zh-CN'

// n-tree-select 的菜单在弹层里,换成把选项树(JSON)和空态插槽都渲染出来的桩。
vi.mock('naive-ui', async importOriginal => {
  const { defineComponent, h: el } = await import('vue')
  return {
    ...(await importOriginal<typeof import('naive-ui')>()),
    NTreeSelect: defineComponent({
      inheritAttrs: false,
      props: { options: { type: Array, default: () => [] } },
      setup(props, { slots }) {
        return () =>
          el('div', { class: 'tree-stub', 'data-options': JSON.stringify(props.options) }, [
            el('div', { class: 'empty-slot' }, slots.empty?.()),
          ])
      },
    }),
  }
})
vi.mock('#/api', () => ({ orgApi: { options: vi.fn() } }))

import { orgApi } from '#/api'
import OrgTreeSelect from './index.vue'

const optionsMock = vi.mocked(orgApi.options)

let app: App<Element> | undefined

function mount(props: Record<string, unknown> = {}) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp({ render: () => h(OrgTreeSelect, props) })
  app.use(createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zhCN } }))
  app.mount(host)
}

async function settle() {
  for (let i = 0; i < 5; i++) {
    await Promise.resolve()
    await nextTick()
  }
}

type TreeNode = { name: string; children?: TreeNode[] }
const flatten = (nodes: TreeNode[]): string[] =>
  nodes.flatMap(n => [n.name, ...flatten(n.children ?? [])])
const names = () =>
  flatten(
    JSON.parse(
      document.body.querySelector('.tree-stub')!.getAttribute('data-options')!,
    ) as TreeNode[],
  )

beforeEach(() => {
  optionsMock.mockReset()
  optionsMock.mockResolvedValue([
    { id: 1, parentId: 0, name: '总公司' },
    { id: 2, parentId: 1, name: '技术部' },
    { id: 3, parentId: 2, name: '前端组' },
  ])
})

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('OrgTreeSelect 数据源', () => {
  it('取数走 orgApi.options,按 parentId 拼成树', async () => {
    mount()
    await settle()

    expect(optionsMock).toHaveBeenCalledTimes(1)
    expect(names()).toEqual(['总公司', '技术部', '前端组'])
    expect(document.body.querySelector('.empty-slot')!.textContent).toBe('')
  })

  it('excludeSubtreeOf 剪掉该机构自身与它的整棵子树', async () => {
    mount({ excludeSubtreeOf: 2 })
    await settle()
    expect(names()).toEqual(['总公司'])
  })

  it('取数失败:下拉空态写「加载失败」,不让人当成没有机构', async () => {
    optionsMock.mockRejectedValue(new Error('boom'))
    mount()
    await settle()

    expect(names()).toEqual([])
    expect(document.body.querySelector('.empty-slot')!.textContent).toContain('加载失败')
  })
})
