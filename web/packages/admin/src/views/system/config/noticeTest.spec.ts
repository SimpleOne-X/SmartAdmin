import { describe, expect, it } from 'vitest'
import { noticeTestText } from './noticeTest'

const messages: Record<string, string> = {
  'config.externalAuth.noticeTest.ok': '已发送',
  'config.externalAuth.noticeTest.failed': '发送失败:{detail}',
}
const tr = {
  te: (key: string) => key in messages,
  t: (key: string, params: { detail: string }) => messages[key].replace('{detail}', params.detail),
}

describe('noticeTestText', () => {
  it('成功:只给成功文案', () => {
    expect(noticeTestText({ status: 'ok' }, tr)).toBe('已发送')
  })

  it('说明里已经带错误码:不重复', () => {
    const r = { status: 'failed', providerCode: '60020', detail: 'errcode=60020 ip denied' }
    expect(noticeTestText(r, tr)).toBe('发送失败:errcode=60020 ip denied')
  })

  it('说明里没有错误码:追加在后面', () => {
    const r = { status: 'failed', providerCode: '60020', detail: 'ip denied' }
    expect(noticeTestText(r, tr)).toBe('发送失败:ip denied (60020)')
  })

  it('只有错误码没有说明:仍然显示错误码', () => {
    expect(noticeTestText({ status: 'failed', providerCode: '60020' }, tr)).toBe('发送失败:(60020)')
  })

  it('没有错误码:说明原样显示', () => {
    expect(noticeTestText({ status: 'failed', detail: 'TaskCanceledException' }, tr)).toBe(
      '发送失败:TaskCanceledException',
    )
  })

  it('前端还没翻译的状态:原样显示状态码,不露出 i18n 键', () => {
    expect(noticeTestText({ status: 'brandNew' }, tr)).toBe('brandNew')
  })
})
