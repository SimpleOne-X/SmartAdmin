import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { createApp } from 'vue'
import { setActivePinia, createPinia } from 'pinia'
import { createPersistedState } from 'pinia-plugin-persistedstate'
import { isCookieSession, parseStoredUser, useUserStore } from './user'
import type { LoginOutput } from '#/types/api'

type PersistedUserStore = ReturnType<typeof useUserStore> & { $persist: () => void }

function sample(partial: Partial<LoginOutput> = {}): LoginOutput {
  return {
    accessToken: 'at',
    expiresAt: '2099-01-01T00:00:00Z',
    refreshToken: 'rt',
    refreshExpiresAt: '2099-01-02T00:00:00Z',
    userId: 1,
    account: 'admin',
    name: 'Admin',
    mustChangePassword: false,
    ...partial,
  }
}

// happy-dom 在 Node 22+ 下 window.localStorage 可能为 undefined,与 app.spec 同款内存 Storage 桩。
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

function mountUserStore(storage: Storage): PersistedUserStore {
  const pinia = createPinia()
  // 显式注入 storage:默认 window.localStorage 在本测试环境不可靠
  pinia.use(createPersistedState({ storage }))
  createApp({}).use(pinia)
  setActivePinia(pinia)
  return useUserStore() as PersistedUserStore
}

beforeEach(() => {
  setActivePinia(createPinia())
})

describe('isCookieSession', () => {
  it('sessionMode=cookie / csrfRequired=true → Cookie 会话', () => {
    expect(isCookieSession(sample({ sessionMode: 'cookie', refreshToken: '' }))).toBe(true)
    expect(isCookieSession(sample({ csrfRequired: true, refreshToken: '' }))).toBe(true)
  })

  it('sessionMode=body / 显式 false → 非 Cookie 会话', () => {
    expect(isCookieSession(sample({ sessionMode: 'body' }))).toBe(false)
    expect(isCookieSession(sample({ csrfRequired: false }))).toBe(false)
  })

  it('无声明时用空 refresh + 有 access 兜底', () => {
    expect(isCookieSession(sample({ refreshToken: '', accessToken: 'at' }))).toBe(true)
    expect(isCookieSession(sample({ refreshToken: 'rt', accessToken: 'at' }))).toBe(false)
  })
})

describe('useUserStore Cookie 会话 persist', () => {
  it('body 模式仍持久化 access/refresh', () => {
    const storage = makeMemoryStorage()
    const user = mountUserStore(storage)
    user.setSession(sample({ sessionMode: 'body', csrfRequired: false }))
    user.$persist()
    expect(user.cookieSession).toBe(false)
    expect(user.accessToken).toBe('at')
    expect(user.refreshToken).toBe('rt')

    const raw = storage.getItem('user')
    expect(raw).toBeTruthy()
    const parsed = JSON.parse(raw!) as {
      accessToken: string
      refreshToken: string
      cookieSession: boolean
    }
    expect(parsed.accessToken).toBe('at')
    expect(parsed.refreshToken).toBe('rt')
    expect(parsed.cookieSession).toBe(false)
  })

  it('cookie 模式不把令牌写入 localStorage', () => {
    const storage = makeMemoryStorage()
    const user = mountUserStore(storage)
    user.setSession(sample({ sessionMode: 'cookie', csrfRequired: true, refreshToken: '' }))
    user.$persist()
    expect(user.cookieSession).toBe(true)
    expect(user.accessToken).toBe('at')
    expect(user.refreshToken).toBe('')

    const raw = storage.getItem('user')
    expect(raw).toBeTruthy()
    const parsed = JSON.parse(raw!) as {
      accessToken: string
      refreshToken: string
      cookieSession: boolean
      userInfo: unknown
    }
    expect(parsed.cookieSession).toBe(true)
    expect(parsed.accessToken).toBe('')
    expect(parsed.refreshToken).toBe('')
    expect(parsed.userInfo).toBeNull()
  })

  it('clear 清掉 cookieSession 与令牌', () => {
    const storage = makeMemoryStorage()
    const user = mountUserStore(storage)
    user.setSession(sample({ sessionMode: 'cookie', refreshToken: '' }))
    user.clear()
    expect(user.accessToken).toBe('')
    expect(user.refreshToken).toBe('')
    expect(user.cookieSession).toBe(false)
    expect(user.userInfo).toBeNull()
  })
})

/** 模拟别的标签页按 persist 序列化格式写进 localStorage 的会话。 */
function writeFromOtherTab(storage: Storage, over: Record<string, unknown> = {}) {
  storage.setItem(
    'user',
    JSON.stringify({
      accessToken: 'at-B',
      refreshToken: 'B',
      cookieSession: false,
      userInfo: { userId: 2, account: 'other', name: 'Other', mustChangePassword: false },
      ...over,
    }),
  )
}

describe('useUserStore.adoptFromStorage', () => {
  afterEach(() => {
    vi.useRealTimers() // 还原 setSystemTime 改过的 Date
  })

  function signedIn() {
    const storage = makeMemoryStorage()
    vi.stubGlobal('localStorage', storage)
    const user = mountUserStore(storage)
    user.setSession(sample({ refreshToken: 'A', sessionMode: 'body' }))
    return { storage, user }
  }

  it('接手签发戳更新的令牌', () => {
    const { storage, user } = signedIn()
    writeFromOtherTab(storage, { issuedAt: user.issuedAt + 1 })

    expect(user.adoptFromStorage()).toBe(true)
    expect(user.refreshToken).toBe('B')
    expect(user.accessToken).toBe('at-B')
    expect(user.userInfo?.account).toBe('other')
  })

  it('戳相同、更旧或缺失的不接手(落后标签页写回的旧令牌)', () => {
    const { storage, user } = signedIn()

    writeFromOtherTab(storage, { issuedAt: user.issuedAt })
    expect(user.adoptFromStorage()).toBe(false)
    writeFromOtherTab(storage, { issuedAt: user.issuedAt - 1 })
    expect(user.adoptFromStorage()).toBe(false)
    writeFromOtherTab(storage) // 缺戳按 0 处理
    expect(user.adoptFromStorage()).toBe(false)
    expect(user.refreshToken).toBe('A')
  })

  it('候选参数优先于存储:用 storage 事件的 newValue 判断,不重读当前存储', () => {
    const { storage, user } = signedIn()
    writeFromOtherTab(storage, { issuedAt: 1 }) // 当前存储已被落后标签页写回旧令牌

    const fresh = parseStoredUser(
      JSON.stringify({
        accessToken: 'at-B',
        refreshToken: 'B',
        cookieSession: false,
        userInfo: null,
        issuedAt: user.issuedAt + 1,
      }),
    )
    expect(user.adoptFromStorage(fresh)).toBe(true)
    expect(user.refreshToken).toBe('B')
    expect(user.issuedAt).toBe(fresh!.issuedAt)
  })

  it('存储没有刷新令牌、没有数据、或本页是 Cookie 会话时不动', () => {
    const { storage, user } = signedIn()

    writeFromOtherTab(storage, { refreshToken: '', issuedAt: user.issuedAt + 1 }) // Cookie 会话落盘形状
    expect(user.adoptFromStorage()).toBe(false)

    storage.removeItem('user')
    expect(user.adoptFromStorage()).toBe(false)

    user.setSession(sample({ sessionMode: 'cookie', refreshToken: '' })) // 本页是 Cookie 会话
    writeFromOtherTab(storage, { refreshToken: 'C', issuedAt: user.issuedAt + 1 })
    expect(user.adoptFromStorage()).toBe(false)
    expect(user.refreshToken).toBe('')
  })

  it('persist 落盘带签发戳;setSession 的戳单调递增,系统时钟回拨也不倒退', () => {
    const { storage, user } = signedIn()
    const first = user.issuedAt
    expect(first).toBeGreaterThan(0)
    user.$persist()
    expect(JSON.parse(storage.getItem('user')!).issuedAt).toBe(first)

    vi.setSystemTime(first - 60_000) // 时钟回拨
    user.setSession(sample({ refreshToken: 'A2', sessionMode: 'body' }))
    expect(user.issuedAt).toBeGreaterThan(first)

    user.clear()
    expect(user.issuedAt).toBe(0)
  })
})
