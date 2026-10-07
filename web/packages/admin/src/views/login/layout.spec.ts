import { describe, expect, it } from 'vitest'
import { LAYOUT_KEY, parseLoginLayout, resolveLoginLayout } from './layout'

const store = (data: Record<string, string> = {}) => ({
  getItem: (key: string) => data[key] ?? null,
})

describe('parseLoginLayout', () => {
  it('新布局值原样认', () => {
    expect(parseLoginLayout('solo')).toBe('solo')
    expect(parseLoginLayout('split')).toBe('split')
  })

  it('旧皮肤映射到最接近的新布局:aurora / spotlight → 单栏,split → 双栏', () => {
    expect(parseLoginLayout('aurora')).toBe('solo')
    expect(parseLoginLayout('spotlight')).toBe('solo')
    expect(parseLoginLayout('split')).toBe('split')
  })

  it('认不出的值返回 null,原型链上的名字也不当成布局', () => {
    expect(parseLoginLayout('nope')).toBeNull()
    expect(parseLoginLayout('constructor')).toBeNull()
    expect(parseLoginLayout('')).toBeNull()
    expect(parseLoginLayout(null)).toBeNull()
  })
})

describe('resolveLoginLayout', () => {
  it('什么都没有时默认单栏', () => {
    expect(resolveLoginLayout('', store())).toBe('solo')
  })

  it('读新记忆 sa-login-layout', () => {
    expect(resolveLoginLayout('', store({ [LAYOUT_KEY]: 'split' }))).toBe('split')
  })

  it('旧记忆 login-skin 仍然生效', () => {
    expect(resolveLoginLayout('', store({ 'login-skin': 'split' }))).toBe('split')
    expect(resolveLoginLayout('', store({ 'login-skin': 'aurora' }))).toBe('solo')
  })

  it('新记忆优先于旧记忆', () => {
    expect(resolveLoginLayout('', store({ [LAYOUT_KEY]: 'solo', 'login-skin': 'split' }))).toBe(
      'solo',
    )
  })

  it('?skin= 与 ?layout= 一次性覆盖记忆(e2e 靠 ?skin=split 进双栏)', () => {
    expect(resolveLoginLayout('?skin=split', store({ [LAYOUT_KEY]: 'solo' }))).toBe('split')
    expect(resolveLoginLayout('?layout=solo', store({ [LAYOUT_KEY]: 'split' }))).toBe('solo')
    expect(resolveLoginLayout('?layout=split&skin=aurora', store())).toBe('split')
  })

  it('无法识别的 query 回落到记忆', () => {
    expect(resolveLoginLayout('?skin=x', store({ [LAYOUT_KEY]: 'split' }))).toBe('split')
  })

  it('storage 读取抛错(无痕窗口)按没有记忆处理', () => {
    const throwing = {
      getItem: () => {
        throw new Error('denied')
      },
    }
    expect(resolveLoginLayout('', throwing)).toBe('solo')
  })
})
