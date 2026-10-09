import { afterEach, describe, expect, it } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import UserAvatar from './UserAvatar.vue'

let app: App<Element> | undefined

function mount(props: { name?: string | null; src?: string | null }) {
  const host = document.createElement('div')
  document.body.appendChild(host)
  app = createApp(() => h(UserAvatar, { size: 32, ...props }))
  app.mount(host)
  return host
}

afterEach(() => {
  app?.unmount()
  app = undefined
  document.body.innerHTML = ''
})

describe('UserAvatar', () => {
  it('没有头像时显示姓名首字', () => {
    const host = mount({ name: '张三' })
    expect(host.textContent).toBe('张')
    expect(host.querySelector('img')).toBeNull()
  })

  it('姓名为空时用 ? 兜底', () => {
    expect(mount({ name: '' }).textContent).toBe('?')
  })

  it('有头像时渲染图片', () => {
    const host = mount({ name: '张三', src: '/a.png' })
    expect(host.querySelector('img')?.getAttribute('src')).toBe('/a.png')
  })

  it('图片加载失败回落首字,换了地址再试图片', async () => {
    const src = ref('/broken.png')
    const host = document.createElement('div')
    document.body.appendChild(host)
    app = createApp(() => h(UserAvatar, { size: 32, name: '李四', src: src.value }))
    app.mount(host)

    host.querySelector('img')!.dispatchEvent(new Event('error'))
    await nextTick()
    expect(host.querySelector('img')).toBeNull()
    expect(host.textContent).toBe('李')

    src.value = '/ok.png'
    await nextTick()
    expect(host.querySelector('img')?.getAttribute('src')).toBe('/ok.png')
  })
})
