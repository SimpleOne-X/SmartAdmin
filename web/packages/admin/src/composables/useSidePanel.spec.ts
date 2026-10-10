import { afterEach, describe, expect, it } from 'vitest'
import { createApp, defineComponent, h, nextTick, type App } from 'vue'
import { useSidePanel } from './useSidePanel'
import { SHELL_PANEL_DRAWER_MAX } from './useShellBreakpoint'

// 壳层之外 useShellBreakpoint 退回视口宽度,所以这里改 window.innerWidth 来切宽度。
const origWidth = window.innerWidth
let app: App<Element> | undefined

function setWidth(w: number) {
  Object.defineProperty(window, 'innerWidth', { value: w, configurable: true })
  window.dispatchEvent(new Event('resize'))
}

/** 在组件 setup 里调 composable(它要 inject 壳层档位),把返回值带出来。 */
function run<T>(setup: () => T): T {
  let result!: T
  app = createApp(
    defineComponent({
      setup() {
        result = setup()
        return () => h('div')
      },
    }),
  )
  app.mount(document.createElement('div'))
  return result
}

afterEach(() => {
  app?.unmount()
  app = undefined
  setWidth(origWidth)
})

describe('useSidePanel', () => {
  it('内容区宽度 < 1000 算紧凑(面板该收进抽屉),≥ 1000 不算', async () => {
    setWidth(SHELL_PANEL_DRAWER_MAX - 1)
    const panel = run(() => useSidePanel())
    expect(panel.compact.value).toBe(true)

    setWidth(SHELL_PANEL_DRAWER_MAX)
    await nextTick()
    expect(panel.compact.value).toBe(false)
  })

  it('阈值可按页覆盖', () => {
    setWidth(1100)
    expect(run(() => useSidePanel()).compact.value).toBe(false)
    expect(run(() => useSidePanel(1200)).compact.value).toBe(true)
  })

  it('抽屉默认关;宽度回到不紧凑时自动关,别留一个对不上的遮罩', async () => {
    setWidth(500)
    const panel = run(() => useSidePanel())
    expect(panel.drawerOpen.value).toBe(false)

    panel.drawerOpen.value = true
    setWidth(1200)
    await nextTick()
    expect(panel.drawerOpen.value).toBe(false)
  })

  it('仍紧凑时抽屉保持打开', async () => {
    setWidth(500)
    const panel = run(() => useSidePanel())
    panel.drawerOpen.value = true
    setWidth(450)
    await nextTick()
    expect(panel.drawerOpen.value).toBe(true)
  })
})
