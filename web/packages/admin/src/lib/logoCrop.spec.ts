import { describe, expect, it } from 'vitest'
import { clampOffset, coverScale, sourceRect } from './logoCrop'

describe('logoCrop', () => {
  it('coverScale 让短边刚好等于取景框', () => {
    expect(coverScale(400, 200, 100)).toBe(0.5)
    expect(coverScale(200, 400, 100)).toBe(0.5)
  })

  it('居中未平移时取原图正中的正方形', () => {
    // 400×200 的横图,铺满 100 的框 → scale 0.5,取中间 200×200
    expect(sourceRect({ width: 400, height: 200, frame: 100, scale: 0.5, x: 0, y: 0 })).toEqual({
      sx: 100,
      sy: 0,
      size: 200,
    })
  })

  it('图往右拖,取景落在原图更靠左的位置', () => {
    const r = sourceRect({ width: 400, height: 200, frame: 100, scale: 0.5, x: 50, y: 0 })
    expect(r.sx).toBe(0)
  })

  it('偏移被收在图片盖满取景框的范围内', () => {
    const s = { width: 400, height: 200, frame: 100, scale: 0.5 }
    // 横向可移 (400*0.5-100)/2 = 50,纵向刚好铺满不可移
    expect(clampOffset({ ...s, x: 80, y: 30 })).toEqual({ x: 50, y: 0 })
    expect(clampOffset({ ...s, x: -80, y: -30 })).toEqual({ x: -50, y: 0 })
  })
})
