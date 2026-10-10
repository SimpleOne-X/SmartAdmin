import { afterEach, describe, expect, it } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import SidePanelDrawer from './index.vue'

// n-drawer 的内容 teleport 到 body,断言全在 document.body 上找。
let app: App<Element> | undefined

function mount(show: boolean) {
  const shown = ref(show)
  app = createApp(() =>
    h(
      SidePanelDrawer,
      { title: '机构', show: shown.value, 'onUpdate:show': (v: boolean) => (shown.value = v) },
      { default: () => h('span', { class: 'panel-body' }, '面板内容') },
    ),
  )
  app.mount(document.createElement('div'))
  return shown
}

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('SidePanelDrawer', () => {
  it('左侧抽屉,标题取 title,插槽内容放进正文', async () => {
    mount(true)
    await nextTick()
    expect(document.body.querySelector('.n-drawer--left-placement')).not.toBeNull()
    expect(document.body.querySelector('.n-drawer-header__main')?.textContent).toBe('机构')
    expect(document.body.querySelector('.side-panel-body .panel-body')?.textContent).toBe(
      '面板内容',
    )
  })

  it('show 为 false 时不渲染抽屉', async () => {
    mount(false)
    await nextTick()
    expect(document.body.querySelector('.n-drawer')).toBeNull()
  })

  it('点标题栏的关闭钮 → update:show(false)', async () => {
    const shown = mount(true)
    await nextTick()
    document.body.querySelector<HTMLElement>('.n-base-close')!.click()
    await nextTick()
    expect(shown.value).toBe(false)
  })
})
