import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  loginLocation,
  rememberLoginRedirect,
  safeRedirect,
  takeLoginRedirect,
} from './loginRedirect'

// happy-dom 在 Node 22+ 下 window.sessionStorage 可能为 undefined,用内存 Storage 桩。
function makeMemoryStorage(): Storage {
  const data = new Map<string, string>()
  return {
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
    clear: () => data.clear(),
    key: (i: number) => Array.from(data.keys())[i] ?? null,
    get length() {
      return data.size
    },
  } as Storage
}

beforeEach(() => {
  vi.stubGlobal('sessionStorage', makeMemoryStorage())
})

describe('safeRedirect', () => {
  it('safeRedirect 接受站内路径含查询串', () => {
    const board = '/fullscreen/eam/board/maintenance-board?department=X'
    expect(safeRedirect(board)).toBe(board)
    expect(safeRedirect('/')).toBe('/')
    expect(safeRedirect('/system/user#top')).toBe('/system/user#top')
    expect(safeRedirect('/a%2Fb?x=%2F')).toBe('/a%2Fb?x=%2F') // 编码的斜杠不在开头就无害
  })

  it('safeRedirect 拒绝外站与协议相对地址', () => {
    for (const evil of [
      '//evil.com',
      '/\\evil.com',
      'https://evil.com',
      'javascript:alert(1)',
      '',
      undefined,
      null,
      ['/a'],
      '/\t/evil.com', // 浏览器解析 URL 时会吃掉制表符 / 换行,剩下 //evil.com
      '/\n/evil.com',
      '/%2F%2Fevil.com', // 百分号编码的 `//`
      '/%2fevil.com',
      '/%5Cevil.com', // 百分号编码的 `\`
      '/%5cevil.com',
    ]) {
      expect(safeRedirect(evil)).toBeNull()
    }
  })

  it('safeRedirect 拒绝登录页本身', () => {
    expect(safeRedirect('/login?redirect=/x')).toBeNull()
    expect(safeRedirect('/login')).toBeNull()
  })
})

describe('login redirect 暂存', () => {
  it('take 读出即删', () => {
    rememberLoginRedirect('/a')
    expect(takeLoginRedirect()).toBe('/a')
    expect(takeLoginRedirect()).toBeNull()
  })

  it('不安全的地址不写入,也不覆盖已暂存的', () => {
    rememberLoginRedirect('/a')
    rememberLoginRedirect('//evil.com')
    rememberLoginRedirect(undefined)
    expect(takeLoginRedirect()).toBe('/a')
  })

  it('存储被篡改成不安全的值时 take 也不放行', () => {
    sessionStorage.setItem('smart-admin:login-redirect', '//evil.com')
    expect(takeLoginRedirect()).toBeNull()
  })
})

describe('loginLocation', () => {
  it('loginLocation 对首页不带 redirect', () => {
    expect(loginLocation('/')).toEqual({ path: '/login', replace: true })
  })

  it('其它地址带上 redirect', () => {
    expect(loginLocation('/fullscreen/eam/board/maintenance-board?department=X')).toEqual({
      path: '/login',
      query: { redirect: '/fullscreen/eam/board/maintenance-board?department=X' },
      replace: true,
    })
  })
})
