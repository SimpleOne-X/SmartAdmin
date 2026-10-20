// 文件路径配置页的纯函数:校验与提示文案的素材。规则与后端保持一致,后端才是最终裁决。

/** 文件落盘的命名规则(与后端 FilePathNamingRule 的值一致)。 */
export const NamingRule = { YearMonthOriginal: 0, BusinessPath: 1, DateGuid: 2 } as const

/** 命名规则的显示名在语言包里的键。 */
export const NAMING_RULE_LABEL_KEYS: Record<number, string> = {
  [NamingRule.YearMonthOriginal]: 'filePath.namingRule.yearMonthOriginal',
  [NamingRule.BusinessPath]: 'filePath.namingRule.businessPath',
  [NamingRule.DateGuid]: 'filePath.namingRule.dateGuid',
}

/** 业务指定路径撞名时的处理(与后端 FilePathConflictMode 的值一致);其余命名规则不会撞名。 */
export const ConflictMode = { Reject: 0, Overwrite: 1 } as const

/**
 * 业务指定路径 + 同名覆盖 + 没限制上传角色:任何登录用户都能覆盖这个文件夹里的同名文件,表单要给出警告。
 * 只读的行不收上传,谈不上覆盖。
 */
export function overwriteRisk(form: {
  namingRule: number
  onConflict: number
  readOnly: boolean
  uploadRoleIds: readonly unknown[]
}): boolean {
  return (
    !form.readOnly &&
    form.namingRule === NamingRule.BusinessPath &&
    form.onConflict === ConflictMode.Overwrite &&
    form.uploadRoleIds.length === 0
  )
}

/** 编码:字母、数字、点、下划线、连字符,1–64 位。 */
export const CODE_PATTERN = /^[A-Za-z0-9._-]{1,64}$/

/**
 * 后缀白名单规范化:去空白、转小写、去重,每项须是「点 + 1–15 位字母或数字」。
 * 格式不对返回 null;空输入是合法的空白名单,返回空串(是否必填由调用方按只读与否决定)。
 */
export function normalizeExtensions(input: string): string | null {
  const items = input
    .split(',')
    .map(s => s.trim().toLowerCase())
    .filter(Boolean)
  if (items.some(e => !/^\.[a-z0-9]{1,15}$/.test(e))) return null
  return [...new Set(items)].join(',')
}

/**
 * 共享路径 `\\host\share` 或 `//host/share` 里的主机名。不是共享路径(本地路径、只有主机没有共享名、
 * 设备路径 `\\?\`)一律为空串,按「本机」算。与后端 FilePathRules.TryGetShare 的判定一致。
 */
export function hostOf(rootPath: string): string {
  const p = rootPath.trim().replaceAll('/', '\\')
  if (!p.startsWith('\\\\')) return ''
  const [host, share] = p.slice(2).split('\\')
  return host && share && host !== '.' && host !== '?' ? host : ''
}

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

/**
 * 保存或测试连接时,密码留空会沿用已保存的密码,但主机或账号换了就不会(后端 CanReuseSavedPassword),
 * 修改时后端直接拒绝保存(44157),原来有没有设置密码都一样。
 * 同时满足才返回 true:新账号不为空(清空账号 = 改用后端运行身份,不需要密码);主机或账号变了。
 * 主机、账号都不区分大小写,账号先去首尾空白,null 与空串相同。
 */
export function passwordReentryRequired(
  original: { rootPath: string; userName?: string | null },
  rootPath: string,
  userName: string | null | undefined,
): boolean {
  const account = (userName ?? '').trim()
  if (!account) return false
  return (
    !same(hostOf(original.rootPath), hostOf(rootPath)) ||
    !same((original.userName ?? '').trim(), account)
  )
}

/**
 * 修改一台文件服务器的账号或密码时,后端会把同一主机的其他文件服务器一起改成相同的账号和密码(Windows 对一台主机只接受一套凭据)。
 * 返回表单要给出的提示:'change' 同主机其他行一起改成这个账号和密码;'clear' 清空账号,整台主机改用后端运行身份;null 不影响别的行。
 * 只在主机没变时级联,换到另一台主机或本机路径时为 null。账号先去首尾空白、按原样比较:只差大小写也算改了,后端会统一写法。
 */
export function credentialCascade(
  original: { rootPath: string; userName?: string | null },
  rootPath: string,
  userName: string | null | undefined,
  password: string,
): 'change' | 'clear' | null {
  const host = hostOf(original.rootPath)
  if (!host || !same(host, hostOf(rootPath))) return null
  const before = (original.userName ?? '').trim()
  const account = (userName ?? '').trim()
  if (!account) return before ? 'clear' : null
  return account !== before || password !== '' ? 'change' : null
}

/**
 * 完整文件夹的显示:共享路径和盘符路径用反斜杠,其余用正斜杠;子文件夹两种斜杠都认,
 * 空段去掉。只给人看,不参与任何校验。
 */
export function folderPreview(rootPath: string, folder: string): string {
  const root = rootPath.trim()
  const windows = hostOf(root) !== '' || /^[A-Za-z]:/.test(root)
  const sep = windows ? '\\' : '/'
  const base = (windows ? root.replaceAll('/', '\\') : root).replace(/[\\/]+$/, '')
  const segments = folder
    .split(/[\\/]/)
    .map(s => s.trim())
    .filter(Boolean)
  return [base, ...segments].join(sep) || root
}

const two = (n: number) => String(n).padStart(2, '0')

/** 三种命名规则各一个示例路径(相对文件夹)。示例名与语言无关,所以用英文。 */
export function exampleFileName(rule: number, now: Date): string {
  const year = now.getFullYear()
  if (rule === NamingRule.BusinessPath) return 'template/label.btw'
  if (rule === NamingRule.DateGuid) {
    return `${year}${two(now.getMonth() + 1)}${two(now.getDate())}/3f2a9c1e7b4d4e0a8c5d6f1a2b3c4d5e.pdf`
  }
  return `${year}/${two(now.getMonth() + 1)}/report_a1b2c3d4.pdf`
}
