import { describe, expect, it } from 'vitest'
import {
  CODE_PATTERN,
  credentialCascade,
  exampleFileName,
  folderPreview,
  hostOf,
  NamingRule,
  ConflictMode,
  normalizeExtensions,
  overwriteRisk,
  passwordReentryRequired,
} from './rules'

describe('CODE_PATTERN', () => {
  it('接受字母、数字、点、下划线、连字符', () => {
    expect(CODE_PATTERN.test('Order.Attachment')).toBe(true)
    expect(CODE_PATTERN.test('a_b-1')).toBe(true)
  })

  it('拒绝空格和空串', () => {
    expect(CODE_PATTERN.test('a b')).toBe(false)
    expect(CODE_PATTERN.test('')).toBe(false)
  })

  it('长度上限 64:64 个通过,65 个不通过', () => {
    expect(CODE_PATTERN.test('a'.repeat(64))).toBe(true)
    expect(CODE_PATTERN.test('a'.repeat(65))).toBe(false)
  })
})

describe('normalizeExtensions', () => {
  it('去空白、转小写、逗号分隔', () => {
    expect(normalizeExtensions(' .PDF,.png')).toBe('.pdf,.png')
    expect(normalizeExtensions('.pdf , .PNG ,')).toBe('.pdf,.png')
  })

  it('重复项只留一个', () => {
    expect(normalizeExtensions('.pdf,.PDF')).toBe('.pdf')
  })

  it('不以点开头的返回 null', () => {
    expect(normalizeExtensions('pdf')).toBeNull()
    expect(normalizeExtensions('.pdf,png')).toBeNull()
  })

  it('只有一个点、含非字母数字、超过 15 位的返回 null', () => {
    expect(normalizeExtensions('.')).toBeNull()
    expect(normalizeExtensions('.p-df')).toBeNull()
    expect(normalizeExtensions(`.${'a'.repeat(15)}`)).toBe(`.${'a'.repeat(15)}`)
    expect(normalizeExtensions(`.${'a'.repeat(16)}`)).toBeNull()
  })

  it('空输入是合法的空白名单,返回空串', () => {
    expect(normalizeExtensions('  ')).toBe('')
  })
})

describe('folderPreview', () => {
  it('共享路径用反斜杠,正斜杠写法的子文件夹也被换成反斜杠', () => {
    expect(folderPreview('\\\\nas01\\files', 'Order/Attachment')).toBe(
      '\\\\nas01\\files\\Order\\Attachment',
    )
  })

  it('正斜杠写法的共享路径同样按共享路径显示', () => {
    expect(folderPreview('//nas01/files', 'a')).toBe('\\\\nas01\\files\\a')
  })

  it('其余用正斜杠', () => {
    expect(folderPreview('/mnt/nas01', 'a')).toBe('/mnt/nas01/a')
  })

  it('盘符路径是 Windows 路径,用反斜杠', () => {
    expect(folderPreview('D:\\data', 'a/b')).toBe('D:\\data\\a\\b')
  })

  it('子文件夹为空时只显示根路径,根路径末尾的分隔符不重复', () => {
    expect(folderPreview('/mnt/nas01/', '')).toBe('/mnt/nas01')
    expect(folderPreview('/mnt/nas01/', ' /a/ ')).toBe('/mnt/nas01/a')
  })
})

describe('exampleFileName', () => {
  const now = new Date(2026, 9, 20)

  it('年/月/原名_短ID', () => {
    expect(exampleFileName(0, now)).toMatch(/^2026\/10\/[\w-]+_[0-9a-f]{8}\.\w+$/)
  })

  it('业务指定路径:子路径/文件名', () => {
    expect(exampleFileName(1, now)).toMatch(/^[\w-]+\/[\w-]+\.\w+$/)
  })

  it('日期/GUID 以 yyyyMMdd 开头', () => {
    expect(exampleFileName(2, now).startsWith('20261020/')).toBe(true)
    expect(exampleFileName(2, now)).toMatch(/^20261020\/[0-9a-f]{32}\.\w+$/)
  })

  it('月、日补零', () => {
    expect(exampleFileName(2, new Date(2026, 0, 5)).startsWith('20260105/')).toBe(true)
    expect(exampleFileName(0, new Date(2026, 0, 5)).startsWith('2026/01/')).toBe(true)
  })
})

describe('hostOf', () => {
  it('取共享路径里的主机名,两种斜杠写法都认', () => {
    expect(hostOf('\\\\nas01\\files')).toBe('nas01')
    expect(hostOf('//nas01/files')).toBe('nas01')
    expect(hostOf('\\\\nas01\\files\\sub')).toBe('nas01')
  })

  it('不是共享路径的一律为空串', () => {
    expect(hostOf('/mnt/a')).toBe('')
    expect(hostOf('D:\\data')).toBe('')
    expect(hostOf('')).toBe('')
    expect(hostOf('\\\\nas01')).toBe('')
    expect(hostOf('\\\\nas01\\')).toBe('')
    expect(hostOf('\\\\?\\C:\\x')).toBe('')
  })

  it('首尾空白忽略', () => {
    expect(hostOf('  \\\\nas01\\files ')).toBe('nas01')
  })
})

describe('passwordReentryRequired', () => {
  const saved = { rootPath: '\\\\nas01\\files', userName: 'svc-files', hasPassword: true }

  it('主机和账号都没变:不用重填', () => {
    expect(passwordReentryRequired(saved, '\\\\nas01\\files', 'svc-files')).toBe(false)
    expect(passwordReentryRequired(saved, '\\\\nas01\\other', 'svc-files')).toBe(false)
  })

  it('同一主机但大小写不同:不用重填', () => {
    expect(passwordReentryRequired(saved, '\\\\NAS01\\files', 'svc-files')).toBe(false)
  })

  it('换了主机:要重填', () => {
    expect(passwordReentryRequired(saved, '\\\\nas02\\files', 'svc-files')).toBe(true)
  })

  it('账号只差大小写或首尾空白:不用重填', () => {
    expect(passwordReentryRequired(saved, '\\\\nas01\\files', 'SVC-Files')).toBe(false)
    expect(passwordReentryRequired(saved, '\\\\nas01\\files', ' svc-files ')).toBe(false)
  })

  it('换了账号:要重填', () => {
    expect(passwordReentryRequired(saved, '\\\\nas01\\files', 'svc-other')).toBe(true)
  })

  it('账号清空是改成匿名访问,不需要密码', () => {
    expect(passwordReentryRequired(saved, '\\\\nas01\\files', '')).toBe(false)
    expect(passwordReentryRequired(saved, '\\\\nas01\\files', '  ')).toBe(false)
    expect(passwordReentryRequired(saved, '\\\\nas02\\files', null)).toBe(false)
  })

  it('原来没有设置密码也一样:换了主机或账号就要重填,空信封不能沿用到新主机或新账号', () => {
    const noPassword = { ...saved, hasPassword: false }
    expect(passwordReentryRequired(noPassword, '\\\\nas02\\files', 'svc-files')).toBe(true)
    expect(passwordReentryRequired(noPassword, '\\\\nas01\\files', 'svc-other')).toBe(true)
    expect(passwordReentryRequired(noPassword, '\\\\nas01\\files', 'svc-files')).toBe(false)
  })

  it('本机路径之间互改,主机都是空串:不用重填', () => {
    const local = { rootPath: '/mnt/a', userName: 'u', hasPassword: true }
    expect(passwordReentryRequired(local, '/mnt/b', 'u')).toBe(false)
  })

  it('原来没有账号、现在填了账号:账号变了,要重填', () => {
    const anon = { rootPath: '\\\\nas01\\files', userName: null, hasPassword: true }
    expect(passwordReentryRequired(anon, '\\\\nas01\\files', 'svc-files')).toBe(true)
  })
})

describe('credentialCascade', () => {
  const saved = { rootPath: '\\\\nas01\\files', userName: 'svc-files' }

  it('同一主机上改了账号或填了新密码:同主机其他行一起改', () => {
    expect(credentialCascade(saved, '\\\\nas01\\files', 'svc-new', '')).toBe('change')
    expect(credentialCascade(saved, '\\\\nas01\\files', 'svc-files', 'n3w')).toBe('change')
    expect(credentialCascade(saved, '\\\\NAS01\\scan', 'svc-files', 'n3w')).toBe('change')
  })

  it('账号只差大小写也一起改:同一台主机上各行的写法保持一致', () => {
    expect(credentialCascade(saved, '\\\\nas01\\files', 'SVC-Files', '')).toBe('change')
  })

  it('账号去掉首尾空白后没变、密码留空:不影响别的行', () => {
    expect(credentialCascade(saved, '\\\\nas01\\files', ' svc-files ', '')).toBeNull()
  })

  it('清空账号:整台主机改用后端运行身份', () => {
    expect(credentialCascade(saved, '\\\\nas01\\files', '  ', '')).toBe('clear')
    expect(credentialCascade(saved, '\\\\nas01\\files', null, 'ignored')).toBe('clear')
  })

  it('原来就没有账号、现在也没有:不影响别的行', () => {
    expect(credentialCascade({ ...saved, userName: null }, '\\\\nas01\\files', '', '')).toBeNull()
  })

  it('原来没有账号、现在填了:同主机其他行一起改', () => {
    expect(
      credentialCascade({ ...saved, userName: null }, '\\\\nas01\\files', 'svc-files', 'p'),
    ).toBe('change')
  })

  it('换到另一台主机或本机路径:不级联(另一台主机按账号一致校验)', () => {
    expect(credentialCascade(saved, '\\\\nas02\\files', 'svc-new', 'n3w')).toBeNull()
    expect(credentialCascade(saved, 'D:\\files', null, '')).toBeNull()
    expect(credentialCascade({ rootPath: '/mnt/a', userName: 'u' }, '/mnt/b', 'v', 'p')).toBeNull()
  })
})

describe('overwriteRisk', () => {
  const risky = {
    namingRule: NamingRule.BusinessPath,
    onConflict: ConflictMode.Overwrite,
    readOnly: false,
    uploadRoleIds: [] as string[],
  }

  it('业务指定路径、同名覆盖、没限制上传角色:有风险', () => {
    expect(overwriteRisk(risky)).toBe(true)
  })

  it('限制了上传角色、同名拒绝或别的命名规则:没有风险', () => {
    expect(overwriteRisk({ ...risky, uploadRoleIds: ['1'] })).toBe(false)
    expect(overwriteRisk({ ...risky, onConflict: ConflictMode.Reject })).toBe(false)
    expect(overwriteRisk({ ...risky, namingRule: NamingRule.DateGuid })).toBe(false)
  })

  it('只读的行不收上传,谈不上覆盖', () => {
    expect(overwriteRisk({ ...risky, readOnly: true })).toBe(false)
  })
})
