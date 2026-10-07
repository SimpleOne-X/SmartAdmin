// 登录页布局的取值与兼容。
// 布局只存在浏览器本地(不是后台配置项),所以兼容只涉及两处浏览器侧的皮肤 id 格式:
//   · localStorage['login-skin'](皮肤 id 的浏览器记忆)
//   · URL 的 ?skin=(一次性覆盖,便于对比预览;e2e 也靠它进双栏)
// 皮肤 id → 布局:split 是双栏;aurora / spotlight 是「居中一张卡」→ 单栏。

export type LoginLayout = 'solo' | 'split'

/** 布局的记忆键(值 solo / split)。 */
export const LAYOUT_KEY = 'sa-login-layout'
/** 皮肤 id 的记忆键(兼容格式),只读不写。 */
const LEGACY_SKIN_KEY = 'login-skin'

/** 皮肤 id → 布局('solo' / 'split' 与布局同名,所以布局值也走这张表)。 */
const SKIN_TO_LAYOUT: Record<string, LoginLayout> = {
  aurora: 'solo',
  spotlight: 'solo',
  split: 'split',
  solo: 'solo',
}

/** 把布局值或皮肤 id 解析成布局;认不出返回 null。 */
export function parseLoginLayout(value: string | null | undefined): LoginLayout | null {
  return (value && Object.hasOwn(SKIN_TO_LAYOUT, value) ? SKIN_TO_LAYOUT[value] : null) ?? null
}

/**
 * 决定登录页首次渲染用哪种布局。优先级:
 * ?layout= / ?skin=(一次性,不写记忆)→ localStorage[sa-login-layout] → localStorage[login-skin] → 单栏。
 * storage 读取可能抛(无痕窗口 / 站点数据被禁),一律按「没有记忆」处理。
 */
export function resolveLoginLayout(search: string, storage: Pick<Storage, 'getItem'>): LoginLayout {
  const query = new URLSearchParams(search)
  const fromQuery = parseLoginLayout(query.get('layout')) ?? parseLoginLayout(query.get('skin'))
  if (fromQuery) return fromQuery
  const read = (key: string) => {
    try {
      return storage.getItem(key)
    } catch {
      return null
    }
  }
  return parseLoginLayout(read(LAYOUT_KEY)) ?? parseLoginLayout(read(LEGACY_SKIN_KEY)) ?? 'solo'
}
