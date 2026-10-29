import { describe, expect, it } from 'vitest'
import { ApiError } from '#/api'
import { isSessionDead, isSessionDeadCode } from './bootFailure'

describe('isSessionDead', () => {
  it('401 与令牌失效业务码 → 会话已死', () => {
    expect(isSessionDead(new ApiError(401))).toBe(true)
    expect(isSessionDead(new ApiError(40005))).toBe(true) // AccountDisabled
    expect(isSessionDead(new ApiError(40006))).toBe(true)
    expect(isSessionDead(new ApiError(40007))).toBe(true)
  })

  // 这几类不是会话问题,不该清会话踢回登录页 —— 重登还是会撞上同一个问题。
  it('网络错 / 5xx / 限流 / 其它业务码 → 不动会话', () => {
    expect(isSessionDead(new TypeError('Failed to fetch'))).toBe(false)
    expect(isSessionDead(new ApiError(500))).toBe(false)
    expect(isSessionDead(new ApiError(502))).toBe(false)
    expect(isSessionDead(new ApiError(40008))).toBe(false) // TooManyRequests
    expect(isSessionDead(undefined)).toBe(false)
  })
})

describe('isSessionDeadCode', () => {
  it('401 与账号停用 / 令牌失效业务码 → 会话已死', () => {
    expect(isSessionDeadCode(401)).toBe(true)
    expect(isSessionDeadCode(40005)).toBe(true)
    expect(isSessionDeadCode(40006)).toBe(true)
    expect(isSessionDeadCode(40007)).toBe(true)
  })

  it('5xx / 限流 / 其它业务码 / 无码 → 不动会话', () => {
    expect(isSessionDeadCode(500)).toBe(false)
    expect(isSessionDeadCode(502)).toBe(false)
    expect(isSessionDeadCode(40008)).toBe(false)
    expect(isSessionDeadCode(undefined)).toBe(false)
  })
})
