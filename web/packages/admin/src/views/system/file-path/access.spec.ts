import { describe, expect, it } from 'vitest'
import { NTag, NTooltip } from 'naive-ui'
import type { VNode } from 'vue'
import { t } from '#/locales'
import { translateError } from '#/utils/error'
import { useAccessStatus } from './access'

type Slots = { trigger: () => VNode; default: () => unknown }

describe('useAccessStatus', () => {
  it('shows_the_translated_error_on_unknown_when_the_whole_request_fails', async () => {
    const error = new DOMException('signal timed out', 'TimeoutError')
    const { load, render } = useAccessStatus(() => Promise.reject(error))

    await load([{ id: 1 }, { id: '2' }])

    for (const id of [1, 2]) {
      const vnode = render(id) as VNode
      // 「未知」还是那个灰字,悬停才看到为什么不知道
      expect(vnode.type).toBe(NTooltip)
      const slots = vnode.children as Slots
      expect(slots.trigger().children).toBe(t('filePath.status.unknown'))
      expect(slots.default()).toBe(translateError(error))
      expect(slots.default()).toBe(t('error.network.timeout'))
    }
  })

  it('a_later_successful_batch_drops_the_old_failure', async () => {
    let fail = true
    const { load, render } = useAccessStatus(ids =>
      fail
        ? Promise.reject(new TypeError('Failed to fetch'))
        : Promise.resolve(ids.map(id => ({ id, online: true, reason: null }))),
    )

    await load([{ id: 1 }])
    expect((render(1) as VNode).type).toBe(NTooltip)

    fail = false
    await load([{ id: 1 }])
    expect((render(1) as VNode).type).toBe(NTag)
  })

  it('offline_rows_still_show_their_own_reason', async () => {
    const { load, render } = useAccessStatus(async () => [
      { id: 1, online: false, reason: 'accessDenied' },
    ])

    await load([{ id: 1 }])

    const vnode = render(1) as VNode
    expect(vnode.type).toBe(NTooltip)
    expect((vnode.children as Slots).default()).toBe(t('error.reason.accessDenied'))
  })
})
