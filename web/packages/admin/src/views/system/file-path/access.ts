// 在线状态与失败原因的展示,文件路径、文件服务器两个页签共用。
import { h, ref } from 'vue'
import { NTag, NTooltip } from 'naive-ui'
import { i18n, t } from '#/locales'
import type { FileAccessStatus } from '#/api'

/** 原因码换成 `error.reason.*` 的文案;语言包里没有的码原样显示,总比丢掉原因强。 */
export function reasonText(reason: string | null | undefined): string {
  if (!reason) return ''
  const key = `error.reason.${reason}`
  return i18n.global.te(key) ? t(key) : reason
}

const faint = (text: string) => h('span', { style: { color: 'var(--text-3)' } }, text)

/**
 * 列表在线状态:一页取完后,用这一页的 Id 问一次,不轮询,翻页或刷新时再问。
 * 还没有结果的行显示「检测中」;整批请求失败显示「未知」,不弹错(列表本身是好的)。
 */
export function useAccessStatus(fetchStatus: (ids: number[]) => Promise<FileAccessStatus[]>) {
  // 没有条目 = 检测中;null = 这一批检测失败
  const results = ref<Record<string, FileAccessStatus | null>>({})
  let latest = 0

  async function load(rows: { id: number | string }[]) {
    const mine = ++latest
    results.value = {}
    const ids = rows.map(r => Number(r.id))
    if (ids.length === 0) return
    let next: Record<string, FileAccessStatus | null>
    try {
      const list = await fetchStatus(ids)
      next = Object.fromEntries(list.map(s => [String(s.id), s]))
    } catch {
      next = Object.fromEntries(ids.map(id => [String(id), null]))
    }
    // 翻页太快时只认最后一次
    if (mine === latest) results.value = next
  }

  function render(id: number | string) {
    const key = String(id)
    if (!(key in results.value)) return faint(t('filePath.status.checking'))
    const s = results.value[key]
    if (!s) return faint(t('filePath.status.unknown'))
    const tag = h(
      NTag,
      { size: 'small', bordered: false, type: s.online ? 'success' : 'error' },
      () => t(s.online ? 'filePath.status.online' : 'filePath.status.offline'),
    )
    if (s.online || !s.reason) return tag
    return h(NTooltip, null, { trigger: () => tag, default: () => reasonText(s.reason) })
  }

  return { load, render }
}
