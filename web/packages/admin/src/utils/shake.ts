// 失败晃动:元素沿水平方向左右抖几下并逐渐收住(macOS 登录框 / 提示框拒绝输入时的做法)。
// 登录失败、弹窗表单校验不过共用这一份,保证两处手感一致。
//
// 用 Web Animations API 而不是「加 class 触发 CSS 动画」,原因有两个:
// - 全局有一条 prefers-reduced-motion 规则把所有 CSS 动画压到 0.01ms(styles/index.css),
//   系统关了动画效果的机器(Windows 关「显示动画」、远程桌面、部分企业策略)上,
//   class 方案的晃动就整个消失,操作员只剩一条一闪而过的 toast;
// - 连续失败要能重播:class 方案得先摘掉、等一帧再加回去,WAAPI 取消上一段再播即可。
// 全局那条规则只管 CSS 动画与过渡,不会碰到这里显式传了时长的 WAAPI 动画。

const DURATION = 460
const EASING = 'cubic-bezier(0.36, 0.07, 0.19, 0.97)'
// 位移(px)关键帧:幅度逐步收小,首尾回到原位,不回弹到别处
const STEPS: [offset: number, x: number][] = [
  [0, 0],
  [0.15, -9],
  [0.3, 8],
  [0.45, -6],
  [0.6, 4],
  [0.8, -2],
  [1, 0],
]
// 系统要求减少动态效果时,晃动不取消(它是唯一就地的失败反馈),但幅度与时长都收小:
// 约 ±3.6px、0.32s 的轻微抖动,既看得见,又不是大幅位移。
const REDUCED_AMPLITUDE = 0.4
const REDUCED_DURATION = 0.7

const running = new WeakMap<Element, Animation>()

export function shake(el: Element | null | undefined): void {
  if (!el || typeof el.animate !== 'function') return
  const reduced = !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  const amp = reduced ? REDUCED_AMPLITUDE : 1
  running.get(el)?.cancel()
  running.set(
    el,
    el.animate(
      // easing 写在每个关键帧上:作用于「本帧到下一帧」这一段,与 CSS 里 animation-timing-function 的语义一致
      STEPS.map(([offset, x]) => ({
        offset,
        transform: x === 0 ? 'none' : `translate3d(${+(x * amp).toFixed(2)}px, 0, 0)`,
        easing: EASING,
      })),
      { duration: reduced ? DURATION * REDUCED_DURATION : DURATION },
    ),
  )
}
