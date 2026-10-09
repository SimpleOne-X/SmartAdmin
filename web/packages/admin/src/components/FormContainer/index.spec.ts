import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import FormContainer from './index.vue'
import { useAppStore, type FormStyle } from '#/stores/app'

// n-modal 的卡片 teleport 到 body,所以断言全在 document.body 上找 —— 这也正是全屏样式
// 不能写 scoped 的原因(scoped 的 data-v 属性到不了 teleport 出去的那棵子树)。
let app: App<Element> | undefined

function mount(props: Record<string, unknown>, formStyle?: FormStyle) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp(() => h(FormContainer, { title: '表单', show: true, variant: 'modal', ...props }))
  const pinia = createPinia()
  if (formStyle) useAppStore(pinia).formStyle = formStyle // 全局形态偏好
  app.use(pinia)
  app.use(
    createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: { 'zh-CN': { common: { cancel: '取消', confirm: '确定' } } },
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

describe('FormContainer 全屏', () => {
  it('默认不全屏 —— 没传 prop 的既有页面不该因为换台设备就换形态', async () => {
    mount({})
    await nextTick()
    expect(document.body.querySelector('.smart-form--fullscreen')).toBeNull()
  })

  it('fullscreen 为 true 时卡片带全屏类', async () => {
    mount({ fullscreen: true })
    await nextTick()
    expect(document.body.querySelector('.smart-form--fullscreen')).not.toBeNull()
  })

  // 'auto' 才跟着屏幕走;happy-dom 默认视口 1024×768 不算紧凑,所以仍是常规卡片。
  it("fullscreen 为 'auto' 时按紧凑屏判定,宽屏下不全屏", async () => {
    mount({ fullscreen: 'auto' })
    await nextTick()
    expect(document.body.querySelector('.smart-form--fullscreen')).toBeNull()
  })
})

async function clickConfirm() {
  await nextTick()
  document.body.querySelector<HTMLButtonElement>('.n-button--primary-type')!.click()
  await new Promise(r => setTimeout(r, 0))
}

describe('FormContainer 提交失败晃动', () => {
  const proto = Element.prototype as unknown as { animate?: unknown }
  const origAnimate = proto.animate
  const animate = vi.fn(() => ({ cancel: vi.fn() }))
  beforeEach(() => {
    animate.mockClear()
    proto.animate = animate
  })
  afterEach(() => {
    proto.animate = origAnimate
  })

  it('onConfirm reject(表单校验不过)→ 卡片晃一下,且不关闭', async () => {
    mount({ onConfirm: () => Promise.reject([[{ message: '必填' }]]) })
    await clickConfirm()
    expect(animate).toHaveBeenCalledTimes(1)
    expect(document.body.querySelector('.n-card.n-modal')).not.toBeNull()
  })

  it('onConfirm 返回 false(业务拒绝)→ 同样晃', async () => {
    mount({ onConfirm: () => false })
    await clickConfirm()
    expect(animate).toHaveBeenCalledTimes(1)
  })

  it('提交成功 → 不晃', async () => {
    mount({ onConfirm: () => true })
    await clickConfirm()
    expect(animate).not.toHaveBeenCalled()
  })

  it('抽屉形态失败不晃(整块贴边,位移会露出缝)', async () => {
    mount({ variant: 'drawer', onConfirm: () => Promise.reject(new Error('x')) })
    await clickConfirm()
    expect(animate).not.toHaveBeenCalled()
  })
})

function setWidth(w: number) {
  Object.defineProperty(window, 'innerWidth', { value: w, configurable: true })
  window.dispatchEvent(new Event('resize'))
}

describe('FormContainer 形态', () => {
  // 不传 variant 跟随全局偏好 app.formStyle(默认弹窗);抽屉随壳层档位:窄档(< 600)= 底部抽屉。
  // 壳层之外 useShellBreakpoint 退回视口宽度,所以这里改 window.innerWidth 来切档。
  const origWidth = window.innerWidth
  afterEach(() => setWidth(origWidth))

  it('不传 variant、全局偏好是默认值 → 弹窗', async () => {
    mount({ variant: undefined })
    await nextTick()
    expect(document.body.querySelector('.n-drawer')).toBeNull()
    expect(document.body.querySelector('.n-card.n-modal')).not.toBeNull()
  })

  it('全局偏好切到抽屉 → 不传 variant 的表单是右侧抽屉,取消钮是 secondary', async () => {
    mount({ variant: undefined }, 'drawer')
    await nextTick()
    expect(document.body.querySelector('.n-drawer')).not.toBeNull()
    expect(document.body.querySelector('.n-modal')).toBeNull()
    expect(document.body.querySelector('.n-drawer.smart-form-sheet')).toBeNull()
    expect(document.body.querySelector('.n-drawer--right-placement')).not.toBeNull()
    expect(document.body.querySelector('.n-button--secondary')).not.toBeNull()
  })

  it('窄档抽屉 = 底部抽屉,带 sheet 类,底栏按钮等分整行', async () => {
    setWidth(500)
    mount({ variant: undefined }, 'drawer')
    await nextTick()
    expect(
      document.body.querySelector('.n-drawer--bottom-placement.smart-form-sheet'),
    ).not.toBeNull()
    expect(document.body.querySelectorAll('.smart-form-foot > .n-button').length).toBe(2)
  })

  it('显式 variant="modal" 不受档位、也不受全局偏好影响', async () => {
    setWidth(500)
    mount({ variant: 'modal' }, 'drawer')
    await nextTick()
    expect(document.body.querySelector('.n-drawer')).toBeNull()
    expect(document.body.querySelector('.n-card.n-modal')).not.toBeNull()
  })

  it('显式 variant="drawer" 不受全局偏好(弹窗)影响', async () => {
    mount({ variant: 'drawer' }, 'modal')
    await nextTick()
    expect(document.body.querySelector('.n-drawer')).not.toBeNull()
    expect(document.body.querySelector('.n-modal')).toBeNull()
  })
})
