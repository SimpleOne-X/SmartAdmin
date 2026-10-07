/**
 * 读 JWT payload 里的字符串声明(不验签:只用来在前端识别「当前会话」,真正的鉴权在后端)。
 * 后端的声明名是未映射的短名(`sub` / `sid` / `sadm` / `unique_name`)。
 * 令牌缺失、不是 JWT、payload 不是合法 JSON 都返回 undefined,不抛错 ——
 * 访问令牌在内存里(Cookie 会话模式也是,只有刷新令牌进 HttpOnly Cookie),但 F5 后要等静默刷新完成才回来,
 * 调用方要有「读不到」的回退。
 */
export function jwtClaim(token: string | null | undefined, name: string): string | undefined {
  const payload = token?.split('.')[1]
  if (!payload) return undefined
  try {
    const base64 = payload
      .replaceAll('-', '+')
      .replaceAll('_', '/')
      .padEnd(Math.ceil(payload.length / 4) * 4, '=')
    // atob 给的是 Latin-1 字节串,要按 UTF-8 解码才不会把中文声明读成乱码
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0))
    const value = (JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>)[name]
    return typeof value === 'string' ? value : undefined
  } catch {
    return undefined
  }
}
