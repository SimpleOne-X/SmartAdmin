import { defineStore } from 'pinia'
import type { LoginOutput } from '#/types/api'

interface UserInfo {
  userId: number
  account: string
  name: string
  /** 头像 ViewUrl;登录出参不含,由 useModule.enterInitial 经 /personal/profile 回填,profile 页保存时同步。 */
  avatar?: string | null
  /** 机构名称;同 avatar 由 useModule.enterInitial 经 /personal/profile 回填,水印用。 */
  orgName?: string | null
  /** 手机号后四位(只存尾号,整号不进浏览器存储);回填来源同 orgName,水印用。 */
  phoneTail?: string | null
  /** 是否需强制改密(管理员建号/重置后首登为 true);路由守卫据此强制跳改密页。 */
  mustChangePassword: boolean
  /** 登录时快照的超管标记;profile 故障时 v-auth 用此值 fail-open。 */
  isSuperAdmin?: boolean
}

/**
 * 判定是否为 Cookie 会话:
 * - sessionMode === 'cookie' / csrfRequired === true(后端 AuthCookieService 显式信号)
 * - 两个显式信号都缺席时兜底:body 下发了 access 但 refresh 为空
 */
export function isCookieSession(
  data: Pick<LoginOutput, 'sessionMode' | 'csrfRequired' | 'accessToken' | 'refreshToken'>,
): boolean {
  if (data.sessionMode === 'cookie' || data.csrfRequired === true) return true
  if (data.sessionMode === 'body' || data.csrfRequired === false) return false
  return !!data.accessToken && !data.refreshToken
}

/** persist 落盘用的 localStorage 键(pinia-plugin-persistedstate 默认取 store id);sessionSync 靠它认出会话变化。 */
export const USER_STORAGE_KEY = 'user'

/** persist 落盘的会话形状。 */
export interface StoredUser {
  accessToken: string
  refreshToken: string
  cookieSession: boolean
  userInfo: UserInfo | null
  /** 这份令牌的签发戳(本地毫秒,单调递增);缺失按 0 处理。 */
  issuedAt: number
}

/** 解析 persist 落盘的 JSON;空值或坏数据返回 null。 */
export function parseStoredUser(raw: string | null | undefined): StoredUser | null {
  if (!raw) return null
  try {
    const v = JSON.parse(raw) as Partial<StoredUser> | null
    if (!v || typeof v !== 'object') return null
    return {
      accessToken: v.accessToken ?? '',
      refreshToken: v.refreshToken ?? '',
      cookieSession: !!v.cookieSession,
      userInfo: v.userInfo ?? null,
      issuedAt: typeof v.issuedAt === 'number' ? v.issuedAt : 0,
    }
  } catch {
    return null
  }
}

/** 读 localStorage 里 persist 落盘的会话;存储不可用(隐私模式等)或没有数据返回 null。 */
function readStoredUser(): StoredUser | null {
  try {
    return parseStoredUser(localStorage.getItem(USER_STORAGE_KEY))
  } catch {
    return null
  }
}

/**
 * 令牌 + 用户信息。
 * - 默认模式:持久化 access/refresh 到 localStorage。
 * - Cookie 会话:access 仅内存;refresh 在 HttpOnly Cookie;storage 只记 cookieSession 标记,
 *   绝不以 localStorage/sessionStorage 存放 access/refresh。
 */
export const useUserStore = defineStore('user', {
  state: () => ({
    accessToken: '',
    refreshToken: '',
    /**
     * Cookie 会话标记(可持久化,不含任何令牌)。
     * F5 后 access 已丢,靠此标记 + HttpOnly refresh Cookie 触发静默刷新。
     */
    cookieSession: false,
    userInfo: null as UserInfo | null,
    /**
     * 当前令牌的签发戳:同一浏览器的多个标签页靠它分新旧(令牌本身是不透明串、比不出先后),
     * 落后标签页写回的旧令牌不会被别的标签页当成新令牌接手。
     */
    issuedAt: 0,
  }),
  getters: {
    isLoggedIn: (s): boolean => !!s.accessToken,
  },
  actions: {
    /** 用登录/刷新出参落地会话。 */
    setSession(data: LoginOutput) {
      const cookie = isCookieSession(data)
      this.cookieSession = cookie
      // 取 max:系统时钟回拨时戳也不能倒退,否则别的标签页会把这份新令牌当成旧的。
      this.issuedAt = Math.max(Date.now(), this.issuedAt + 1)
      this.accessToken = data.accessToken
      // Cookie 模式 body 的 refresh 为空串,不进内存也不进 storage
      this.refreshToken = cookie ? '' : (data.refreshToken ?? '')
      this.userInfo = {
        userId: Number(data.userId),
        account: data.account,
        name: data.name,
        mustChangePassword: data.mustChangePassword ?? false,
        isSuperAdmin: data.isSuperAdmin ?? false,
      }
    },
    clear() {
      this.accessToken = ''
      this.refreshToken = ''
      this.cookieSession = false
      this.userInfo = null
      this.issuedAt = 0
    },
    /**
     * 同一浏览器的其它标签页续期后会把新令牌写进 localStorage;本页内存里的刷新令牌已被服务端轮换掉,
     * 再拿它去换只会撞上重放检测。body 模式下候选(默认读当前存储,storage 事件传 `newValue`)的签发戳
     * 严格新于本页时整体接手,返回是否接手;戳相同或更旧的不接手(落后标签页写回的旧令牌)。
     * Cookie 会话的令牌在浏览器 Cookie 里、各标签页天然共用,无需接手。
     */
    adoptFromStorage(candidate: StoredUser | null = readStoredUser()): boolean {
      if (this.cookieSession) return false
      if (!candidate || candidate.cookieSession || !candidate.refreshToken) return false
      if (candidate.issuedAt <= this.issuedAt) return false
      this.accessToken = candidate.accessToken
      this.refreshToken = candidate.refreshToken
      this.userInfo = candidate.userInfo
      this.issuedAt = candidate.issuedAt
      return true
    },
  },
  persist: {
    key: USER_STORAGE_KEY,
    // 序列化时按会话模式分流:Cookie 会话只落 cookieSession,令牌字段强制空串。
    serializer: {
      serialize(state) {
        if (state.cookieSession) {
          return JSON.stringify({
            accessToken: '',
            refreshToken: '',
            cookieSession: true,
            userInfo: null,
          })
        }
        return JSON.stringify({
          accessToken: state.accessToken ?? '',
          refreshToken: state.refreshToken ?? '',
          cookieSession: false,
          userInfo: state.userInfo ?? null,
          issuedAt: state.issuedAt ?? 0,
        })
      },
      deserialize(raw) {
        return JSON.parse(raw) as StoredUser
      },
    },
    afterHydrate: ({ store }) => {
      // 防御:Cookie 会话下 storage 里不该有令牌,残留的一律抹掉
      if (store.cookieSession) {
        store.accessToken = ''
        store.refreshToken = ''
      }
    },
  },
})
