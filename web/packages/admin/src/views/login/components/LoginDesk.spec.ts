import { afterEach, describe, expect, it } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { addCollection, type IconifyJSON } from '@iconify/vue'
import phSubset from '#/assets/icons/ph-subset.json'
import LoginDesk from './LoginDesk.vue'

// 图标走离线子集,否则 Iconify 会去请求公网。
addCollection(phSubset as IconifyJSON)

// 指针视差 + 卡片面上的柔光:位移与光斑都由 CSS 变量驱动,这里只钉脚本写出的变量。
let app: App<Element> | undefined

function mount() {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp(() => h(LoginDesk, null, { default: () => h('div', { class: 'slot' }) }))
  app.use(createPinia())
  app.use(
    createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: { 'zh-CN': { app: { light: '亮色', dark: '暗色' } } },
    }),
  )
  app.mount(host)
  return host.querySelector<HTMLElement>('.login')!
}

/** 等 rAF 回调执行(happy-dom 的 rAF 是定时器) */
const frame = () => new Promise<void>(r => setTimeout(r, 40))

function move(el: HTMLElement, x: number, y: number, pointerType = 'mouse') {
  const e = new MouseEvent('pointermove', { clientX: x, clientY: y, bubbles: true })
  Object.defineProperty(e, 'pointerType', { value: pointerType })
  el.dispatchEvent(e)
}

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('LoginDesk 指针交互', () => {
  it('鼠标移动:写出归一化的 --px/--py、相对卡片的 --mx/--my,并点亮柔光', async () => {
    const root = mount()
    await nextTick()
    move(root, window.innerWidth, 0) // 右上角:px = 1,py = -1
    await frame()
    expect(root.style.getPropertyValue('--px')).toBe('1.000')
    expect(root.style.getPropertyValue('--py')).toBe('-1.000')
    expect(root.style.getPropertyValue('--mx')).toMatch(/px$/)
    expect(root.style.getPropertyValue('--my')).toMatch(/px$/)
    expect(root.style.getPropertyValue('--lit')).toBe('1')
  })

  it('指针离开:视差归零、柔光熄灭,光斑位置保留(淡出时不跳)', async () => {
    const root = mount()
    await nextTick()
    move(root, 100, 100)
    await frame()
    const mx = root.style.getPropertyValue('--mx')
    root.dispatchEvent(new MouseEvent('pointerleave'))
    await frame()
    expect(root.style.getPropertyValue('--px')).toBe('0.000')
    expect(root.style.getPropertyValue('--py')).toBe('0.000')
    expect(root.style.getPropertyValue('--lit')).toBe('0')
    expect(root.style.getPropertyValue('--mx')).toBe(mx)
  })

  it('触屏 / 笔不触发(没有悬停位置)', async () => {
    const root = mount()
    await nextTick()
    move(root, 100, 100, 'touch')
    await frame()
    expect(root.style.getPropertyValue('--px')).toBe('')
    expect(root.style.getPropertyValue('--lit')).toBe('')
  })
})
