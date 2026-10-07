import { describe, expect, it } from 'vitest'
import { shellBreakpointOf } from './useShellBreakpoint'
import { lineIcon } from '#/utils/menuIcon'

describe('shellBreakpointOf', () => {
  it('按内容区宽度分三档,边界与表格窄档 600 对齐', () => {
    expect(shellBreakpointOf(599)).toBe('narrow')
    expect(shellBreakpointOf(600)).toBe('mid')
    expect(shellBreakpointOf(1399)).toBe('mid')
    expect(shellBreakpointOf(1400)).toBe('wide')
  })
})

describe('lineIcon', () => {
  it('去掉 -duotone 后缀,其它款式与空值原样处理', () => {
    expect(lineIcon('ph:users-duotone')).toBe('ph:users')
    expect(lineIcon('ph:users')).toBe('ph:users')
    expect(lineIcon('ph:heart-fill')).toBe('ph:heart-fill')
    expect(lineIcon(undefined)).toBeUndefined()
  })
})
