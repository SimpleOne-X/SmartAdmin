import { describe, expect, it } from 'vitest'
import {
  CODE_PATTERN,
  exampleFileName,
  folderPreview,
  hostOf,
  normalizeExtensions,
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

  it('原来就没有设置密码:不用重填', () => {
    expect(passwordReentryRequired({ ...saved, hasPassword: false }, '\\\\nas02\\files', 'x')).toBe(
      false,
    )
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
