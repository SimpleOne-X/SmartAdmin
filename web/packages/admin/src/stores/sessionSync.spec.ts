import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createPersistedState } from 'pinia-plugin-persistedstate'
import { installSessionSync } from './sessionSync'
import { useUserStore } from './user'

const authReset = vi.hoisted(() => vi.fn())
const routerMock = vi.hoisted(() => ({
  router: {
    currentRoute: {
      value: { path: '/board', fullPath: '/board', query: {} as Record<string, unknown> },
    },
    replace: vi.fn(),
  },
  resetRouter: vi.fn(),
}))

vi.mock('#/stores/auth', () => ({ useAuthStore: () => ({ reset: authReset }) }))
vi.mock('#/router', () => routerMock)

// happy-dom 在 Node 22+ 下 window.localStorage 可能为 undefined,与 user.spec 同款内存 Storage 桩。
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

const session = (over: Record<string, unknown> = {}) =>
  JSON.stringify({
    accessToken: 'at-B',
    refreshToken: 'B',
    cookieSession: false,
    userInfo: { userId: 2, account: 'other', name: 'Other', mustChangePassword: false },
    ...over,
  })
const SIGNED_OUT = session({ accessToken: '', refreshToken: '', userInfo: null })

let storage: Storage
let user: ReturnType<typeof useUserStore>

/** 模拟别的标签页写了 user 键:先改共享存储,再给本页派发 storage 事件。 */
function otherTabWrites(newValue: string | null, key = 'user') {
  if (newValue === null) storage.removeItem(key)
  else storage.setItem(key, newValue)
  window.dispatchEvent(new StorageEvent('storage', { key, newValue }))
}

function signIn() {
  user.setSession({
    accessToken: 'at-A',
    expiresAt: '2099-01-01T00:00:00Z',
    refreshToken: 'A',
    refreshExpiresAt: '2099-01-02T00:00:00Z',
    userId: 1,
    account: 'admin',
    name: 'Admin',
    mustChangePassword: false,
    sessionMode: 'body',
  })
}

beforeEach(() => {
  storage = makeMemoryStorage()
  vi.stubGlobal('localStorage', storage)
  const pinia = createPinia()
  pinia.use(createPersistedState({ storage }))
  createApp({}).use(pinia)
  setActivePinia(pinia)
  user = useUserStore()
  authReset.mockClear()
  routerMock.resetRouter.mockClear()
  routerMock.router.replace.mockClear()
  routerMock.router.currentRoute.value = { path: '/board', fullPath: '/board', query: {} }
  // 重复安装只挂一次监听:下面每个用例里 replace / reset 只响应一次即是证明。
  installSessionSync()
  installSessionSync()
})

describe('installSessionSync', () => {
  it('其它标签页写入新令牌后本页内存跟上', () => {
    signIn()
    otherTabWrites(session({ issuedAt: user.issuedAt + 1 }))

    expect(user.refreshToken).toBe('B')
    expect(user.accessToken).toBe('at-B')
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })

  it('落后标签页写回的旧令牌不接手,本页把自己的新令牌写回存储', () => {
    signIn()
    otherTabWrites(session({ issuedAt: user.issuedAt - 1 }))

    expect(user.refreshToken).toBe('A')
    const saved = JSON.parse(storage.getItem('user')!)
    expect(saved.refreshToken).toBe('A')
    expect(saved.issuedAt).toBe(user.issuedAt)
  })

  it('本页没有会话时,旧候选不触发写回', () => {
    otherTabWrites(session({ issuedAt: -1 }))

    expect(JSON.parse(storage.getItem('user')!).refreshToken).toBe('B') // 存储保持别人写的
  })

  it('其它标签页退出登录后本页回到登录页', async () => {
    signIn()
    otherTabWrites(SIGNED_OUT)

    await vi.waitFor(() => expect(routerMock.router.replace).toHaveBeenCalledTimes(1))
    expect(routerMock.router.replace).toHaveBeenCalledWith(
      expect.objectContaining({ path: '/login', query: { redirect: '/board' } }),
    )
    expect(user.accessToken).toBe('')
    expect(user.refreshToken).toBe('')
    expect(authReset).toHaveBeenCalledTimes(1)
    expect(routerMock.resetRouter).toHaveBeenCalledTimes(1)
  })

  it('Cookie 会话的落盘形状(令牌为空、cookieSession=true)不算退出', async () => {
    signIn()
    otherTabWrites(
      session({ accessToken: '', refreshToken: '', userInfo: null, cookieSession: true }),
    )

    await Promise.resolve()
    expect(user.accessToken).toBe('at-A')
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })

  it('本页没有会话时,别的标签页退出不处理(公开页保持原样)', async () => {
    otherTabWrites(SIGNED_OUT)

    await Promise.resolve()
    expect(authReset).not.toHaveBeenCalled()
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })

  describe('中转页上被退出时的回跳地址', () => {
    async function signedOutOn(path: string, query: Record<string, unknown>) {
      signIn()
      routerMock.router.currentRoute.value = { path, fullPath: path, query }
      otherTabWrites(SIGNED_OUT)
      await vi.waitFor(() => expect(routerMock.router.replace).toHaveBeenCalledTimes(1))
    }

    it('取该页自带的 redirect', async () => {
      await signedOutOn('/boot-error', { redirect: '/board?department=X' })
      expect(routerMock.router.replace).toHaveBeenCalledWith({
        path: '/login',
        query: { redirect: '/board?department=X' },
        replace: true,
      })
    })

    it('没有 redirect 或不安全就不带', async () => {
      await signedOutOn('/oauth/callback', {})
      expect(routerMock.router.replace).toHaveBeenCalledWith({ path: '/login', replace: true })
      routerMock.router.replace.mockClear()

      await signedOutOn('/boot-error', { redirect: '//evil.com' })
      expect(routerMock.router.replace).toHaveBeenCalledWith({ path: '/login', replace: true })
    })

    it('/mfa/bind 同理', async () => {
      await signedOutOn('/mfa/bind', {})
      expect(routerMock.router.replace).toHaveBeenCalledWith({ path: '/login', replace: true })
    })
  })

  it('已在登录页时退出事件只清会话,不重复跳转', async () => {
    signIn()
    routerMock.router.currentRoute.value = { path: '/login', fullPath: '/login', query: {} }
    otherTabWrites(SIGNED_OUT)

    await vi.waitFor(() => expect(user.accessToken).toBe(''))
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })

  it('与 user 键无关的 storage 事件不处理', async () => {
    signIn()
    otherTabWrites(SIGNED_OUT, 'app')
    otherTabWrites(null, 'user') // 键被删除:没有可采信的新值

    await Promise.resolve()
    expect(user.accessToken).toBe('at-A')
    expect(routerMock.router.replace).not.toHaveBeenCalled()
  })
})
