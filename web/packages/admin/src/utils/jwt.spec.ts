import { describe, expect, it } from 'vitest'
import { jwtClaim } from './jwt'

/** 造一段 base64url 编码的 JSON(按 UTF-8 字节编码,与后端签发的 JWT 一致)。 */
function b64url(value: object): string {
  let binary = ''
  new TextEncoder().encode(JSON.stringify(value)).forEach(b => (binary += String.fromCharCode(b)))
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '')
}
const token = (payload: object) => `${b64url({ alg: 'none' })}.${b64url(payload)}.signature`

describe('jwtClaim', () => {
  it('读出字符串声明', () => {
    expect(jwtClaim(token({ sid: 'abc123', sub: '1' }), 'sid')).toBe('abc123')
  })

  it('payload 含中文也能按 UTF-8 读出', () => {
    expect(jwtClaim(token({ unique_name: '管理员' }), 'unique_name')).toBe('管理员')
  })

  it('声明不存在、或不是字符串:返回 undefined', () => {
    expect(jwtClaim(token({ sub: '1' }), 'sid')).toBeUndefined()
    expect(jwtClaim(token({ sid: 42 }), 'sid')).toBeUndefined()
  })

  it('没有令牌或不是 JWT:返回 undefined,不抛错', () => {
    expect(jwtClaim(undefined, 'sid')).toBeUndefined()
    expect(jwtClaim(null, 'sid')).toBeUndefined()
    expect(jwtClaim('', 'sid')).toBeUndefined()
    expect(jwtClaim('not-a-jwt', 'sid')).toBeUndefined()
  })

  it('payload 不是合法 JSON:返回 undefined,不抛错', () => {
    expect(jwtClaim('a.%%%.c', 'sid')).toBeUndefined()
    expect(jwtClaim(`a.${btoa('not json')}.c`, 'sid')).toBeUndefined()
  })
})
