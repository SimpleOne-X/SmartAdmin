// 会话失效的分诊:门户重建(enterInitial)失败和静默续期(refresh)失败共用这一份判定。
//
// 只有会话真的没了才清会话回登录页:HTTP 401,或后端明说令牌失效 / 账号停用的业务码。
// 网络抖一下、后端 502、被限流都不是会话问题 —— 清会话赶回登录页,重登时还会撞上同一个网络问题,
// 白丢当前页面又解决不了任何事,所以这类失败走可重试的错误页(或保留会话、下次请求再续)。
import { ApiError } from '#/api'

/** 账号已停用(ErrorCode.AccountDisabled)。 */
export const ACCOUNT_DISABLED_CODE = 40005
/** 令牌/刷新令牌失效(ErrorCode.TokenInvalid / RefreshTokenInvalid)。 */
export const TOKEN_INVALID_CODE = 40006
export const REFRESH_TOKEN_INVALID_CODE = 40007

const SESSION_DEAD_CODES = new Set([
  401,
  ACCOUNT_DISABLED_CODE,
  TOKEN_INVALID_CODE,
  REFRESH_TOKEN_INVALID_CODE,
])

/** 该 HTTP 状态码 / 业务码是否意味着会话已失效。 */
export function isSessionDeadCode(code: number | undefined): boolean {
  return code !== undefined && SESSION_DEAD_CODES.has(code)
}

/**
 * 该错误是否意味着会话已失效(可以清会话回登录页)。
 * 非 ApiError(网络中断、超时)与 5xx / 429 一律返回 false —— 留在原地重试。
 */
export function isSessionDead(err: unknown): boolean {
  return err instanceof ApiError && isSessionDeadCode(err.code)
}
