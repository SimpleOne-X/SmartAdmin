type NoticeTestView = { status: string; providerCode?: string | null; detail?: string | null }

type Translator = {
  te: (key: string) => boolean
  t: (key: string, params: { detail: string }) => string
}

/**
 * 「发送测试消息」的结果转成提示文案。对方的错误码要让管理员看得到,说明里没带就追加在后面。
 * 后端新增的状态码前端还没翻译时,原样显示状态码,不要露出 i18n 键。
 */
export function noticeTestText(r: NoticeTestView, { te, t }: Translator): string {
  const key = `config.externalAuth.noticeTest.${r.status}`
  if (!te(key)) return r.status
  const detail = r.detail ?? ''
  const code = r.providerCode
  return t(key, {
    detail: code && !detail.includes(code) ? `${detail} (${code})`.trim() : detail,
  })
}
