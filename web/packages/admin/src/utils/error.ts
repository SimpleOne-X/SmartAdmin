import { i18n, t } from '#/locales'
import { ApiError } from '#/api'

/**
 * 数值 ErrorCode → msgKey(镜像后端 [MsgKey];CellError 只带码不带文案)。
 * 只列导入预览会在单元格/列错误里出现的码 + 常见下载失败码;其余走兜底。
 */
const CODE_MSG_KEY: Record<number, string> = {
  41002: 'error.perm.demoReadOnly',
  44001: 'error.file.empty',
  44002: 'error.file.tooLarge',
  44003: 'error.file.extNotAllowed',
  46001: 'error.excel.providerMissing',
  46002: 'error.import.fileEmpty',
  46003: 'error.import.rowLimitExceeded',
  46004: 'error.import.columnMissing',
  46005: 'error.import.cellRequired',
  46006: 'error.import.cellDictInvalid',
  46007: 'error.import.cellRefNotFound',
  46008: 'error.import.cellFormatInvalid',
  46009: 'error.import.duplicateInFile',
  46010: 'error.import.duplicateInDb',
  46011: 'error.import.orgOutOfScope',
  46012: 'error.export.tooManyRows',
  46013: 'error.export.columnInvalid',
}

/**
 * 请求被中止:超时闸(client.ts 的 timeoutMiddleware)抛 TimeoutError,主动取消抛 AbortError。
 * 二者都是 DOMException,原始 message 是浏览器的英文原话,不能直接给用户看。
 * 续期暂时失败(client.ts 的 RefreshUnavailableError)同理,message 是英文,换成本地化文案。
 */
function abortMsgKey(err: unknown): string | undefined {
  if (!(err instanceof Error)) return undefined
  if (err.name === 'TimeoutError') return 'error.network.timeout'
  if (err.name === 'AbortError') return 'error.network.aborted'
  if (err.name === 'RefreshUnavailableError') return 'error.network.refreshUnavailable'
  return undefined
}

/**
 * args.reason 是后端的原因码(accessDenied 这类,文件服务器访问失败的原因),文案在 `error.reason.<码>`。
 * 先换成翻译再填占位符;语言包里没有的码原样保留,总比丢掉原因强。
 */
function translatedArgs(args?: Record<string, unknown> | null): Record<string, unknown> {
  // 后端无实参时信封里是 "args": null,默认参数只管 undefined,所以这里用 ?? 兜
  const a = args ?? {}
  const key = typeof a.reason === 'string' ? `error.reason.${a.reason}` : ''
  return key && i18n.global.te(key) ? { ...a, reason: t(key) } : a
}

/**
 * 把任意错误翻成用户可读文案:
 *   - 数字 ErrorCode(CellError.code) → 按码查 i18n
 *   - 超时 / 主动取消 → 专门文案(否则用户看到浏览器的英文原话)
 *   - ApiError.msgKey 命中 i18n → 本地化文案;否则退回 message;再退回通用兜底。
 * 纯函数,不依赖 Naive —— 视图拿到字符串后自行 useMessage 弹出。
 */
export function translateError(err: unknown): string {
  const abortKey = abortMsgKey(err)
  if (abortKey) return t(abortKey)
  if (typeof err === 'number') {
    const key = CODE_MSG_KEY[err]
    if (key && i18n.global.te(key)) return t(key)
    return t('error._fallback')
  }
  if (err instanceof ApiError) {
    // args 必须带上:后端 AdminException 的 args 就是文案占位符的实参(如 {max}),
    // 丢了它界面上会显示占位符原文,最该看到的那个数反而没了。无占位符的文案传 args 无害。
    if (err.msgKey && i18n.global.te(err.msgKey)) return t(err.msgKey, translatedArgs(err.args))
    // 无 msgKey 时用数字码兜底(下载失败信封偶发只有 code)
    if (!err.msgKey && CODE_MSG_KEY[err.code] && i18n.global.te(CODE_MSG_KEY[err.code])) {
      return t(CODE_MSG_KEY[err.code], translatedArgs(err.args))
    }
    if (err.message) return err.message
  }
  if (err instanceof Error && err.message) return err.message
  return t('error._fallback')
}

/**
 * 同 translateError,但后端在 AdminException 上附了明细(`new AdminException(code, null, "…")`)时优先显示明细。
 * 对接外部系统、批量导入这类场景,对端原话 / 重复项清单比一句泛化的 i18n 文案更有用。
 * 判据:AdminExceptionFilter 无明细时把 Message 置成 msgKey,所以 message !== msgKey 即为明细。
 */
export function translateErrorDetail(err: unknown): string {
  if (err instanceof ApiError && err.message && err.msgKey && err.message !== err.msgKey) {
    return err.message
  }
  return translateError(err)
}
