// Logo 裁剪的几何计算:图片以视口中心 + 偏移量摆放、按 scale 缩放,正方形取景框居中。
// 与 DOM 无关,裁剪组件只负责把指针/滚轮事件换成 offset/scale,再用这里的结果画 canvas。

export interface CropState {
  /** 图片原始宽高 */
  width: number
  height: number
  /** 取景框边长(px,视口坐标) */
  frame: number
  /** 当前缩放(图片像素 → 视口像素) */
  scale: number
  /** 图片中心相对取景框中心的偏移(视口像素) */
  x: number
  y: number
}

/** 刚好铺满取景框的最小缩放:短边等于框边,不留空白。 */
export function coverScale(width: number, height: number, frame: number): number {
  return frame / Math.min(width, height)
}

// 可移范围为 0 时直接归零,避免 Math.max(-0, …) 产出 -0
const clamp = (v: number, max: number) => (max === 0 ? 0 : Math.min(max, Math.max(-max, v)))

/** 把偏移收在"图片始终盖满取景框"的范围内。 */
export function clampOffset(s: CropState): { x: number; y: number } {
  const maxX = Math.max(0, (s.width * s.scale - s.frame) / 2)
  const maxY = Math.max(0, (s.height * s.scale - s.frame) / 2)
  return { x: clamp(s.x, maxX), y: clamp(s.y, maxY) }
}

/** 取景框在原图上的区域(原图像素),供 drawImage 取源矩形。 */
export function sourceRect(s: CropState): { sx: number; sy: number; size: number } {
  const size = s.frame / s.scale
  return {
    sx: s.width / 2 - s.x / s.scale - size / 2,
    sy: s.height / 2 - s.y / s.scale - size / 2,
    size,
  }
}
