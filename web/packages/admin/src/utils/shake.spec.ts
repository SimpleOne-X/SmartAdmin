import { afterEach, describe, expect, it, vi } from 'vitest'
import { shake } from './shake'

type Frame = { transform: string; offset?: number }

function fakeEl() {
  const cancels: ReturnType<typeof vi.fn>[] = []
  const animate = vi.fn((_frames: Frame[], _opts: KeyframeAnimationOptions) => {
    const cancel = vi.fn()
    cancels.push(cancel)
    return { cancel } as unknown as Animation
  })
  return { el: { animate } as unknown as Element, animate, cancels }
}

/** 取一组关键帧里 translate3d 的 x 位移(px) */
const xs = (frames: Frame[]) =>
  frames.map(f => {
    const m = f.transform.match(/translate3d\((-?[\d.]+)px/)
    return m ? Number(m[1]) : 0
  })

function stubReducedMotion(reduce: boolean) {
  vi.stubGlobal('matchMedia', (q: string) => ({
    matches: reduce && q.includes('prefers-reduced-motion'),
  }))
}

afterEach(() => vi.unstubAllGlobals())

describe('shake:失败晃动', () => {
  it('左右抖几下并逐渐收住,首尾回到原位', () => {
    stubReducedMotion(false)
    const { el, animate } = fakeEl()
    shake(el)
    expect(animate).toHaveBeenCalledTimes(1)
    const [frames, opts] = animate.mock.calls[0]
    const x = xs(frames)
    expect(x[0]).toBe(0)
    expect(x[x.length - 1]).toBe(0)
    // 正负交替、幅度递减
    const mid = x.slice(1, -1)
    expect(Math.max(...mid.map(Math.abs))).toBe(9)
    expect(mid.map(Math.sign)).toEqual(mid.map((_, i) => (i % 2 === 0 ? -1 : 1)))
    expect(opts.duration).toBeGreaterThan(300)
  })

  // 全局 prefers-reduced-motion 规则会把 CSS 动画压到 0.01ms;系统关了动画的机器上
  // 靠 class 触发的 CSS 动画等于没有,所以这里走 WAAPI,并且减少动效时仍然给出(更小的)反馈。
  it('系统开了「减少动态效果」时仍然晃,只是幅度更小、更短', () => {
    stubReducedMotion(false)
    const normal = fakeEl()
    shake(normal.el)
    stubReducedMotion(true)
    const reduced = fakeEl()
    shake(reduced.el)
    expect(reduced.animate).toHaveBeenCalledTimes(1)
    const maxOf = (c: ReturnType<typeof fakeEl>) =>
      Math.max(...xs(c.animate.mock.calls[0][0]).map(Math.abs))
    expect(maxOf(reduced)).toBeGreaterThan(0)
    expect(maxOf(reduced)).toBeLessThan(maxOf(normal))
    expect(reduced.animate.mock.calls[0][1].duration).toBeLessThan(
      normal.animate.mock.calls[0][1].duration as number,
    )
  })

  it('连续失败:先取消上一段再重播', () => {
    stubReducedMotion(false)
    const { el, animate, cancels } = fakeEl()
    shake(el)
    shake(el)
    expect(animate).toHaveBeenCalledTimes(2)
    expect(cancels[0]).toHaveBeenCalledTimes(1)
    expect(cancels[1]).not.toHaveBeenCalled()
  })

  it('没有元素或不支持 Web Animations 时静默跳过', () => {
    expect(() => shake(null)).not.toThrow()
    expect(() => shake(undefined)).not.toThrow()
    expect(() => shake({} as Element)).not.toThrow()
  })
})
