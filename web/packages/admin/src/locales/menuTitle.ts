// 菜单标题 → i18n key,以及「按这条规则翻出可显示文本」的单源。
// 标题本身含 '.' 视为 key;否则先按路由 path 查映射表,再按标题原文查映射表,都映射不到时回退原文。
//
// 为什么要按标题原文查:后端菜单种子(DefaultMenuSeed)存的是中文标题且只插不更新,目录与按钮节点没有 path,
// 应用(模块)标题更没有 path,只能认标题原文。内核自带的种子标题表见 kernelMenuTitles.ts,由 createSmartAdmin 注册;
// 本文件自身不带映射,表为空时函数退化为原样返回。ext/<locale>/ 下的文案模块不受影响。
//
// 映射表是消费方接缝:经 createSmartAdmin({ menuTitles }) 传入。键以 '/' 开头按路由 path 登记(path → key),
// 其余按标题原文登记(原文 → key);同一张表,不另设选项。
import { t } from '#/locales'

/** path → key:消费方给自己页面登记,优先级最高。 */
export const MENU_TITLE_KEYS: Record<string, string> = {}
/** 标题原文 → key:内核种子标题表与消费方的存量中文标题共用。 */
export const MENU_TITLE_TEXT_KEYS: Record<string, string> = {}

export function registerMenuTitles(map: Record<string, string>): void {
  for (const [from, key] of Object.entries(map)) {
    if (from.startsWith('/')) MENU_TITLE_KEYS[from] = key
    else MENU_TITLE_TEXT_KEYS[from] = key
  }
}

export function menuTitleKey(title: string, path?: string): string {
  if (title.includes('.')) return title
  const key = (path ? MENU_TITLE_KEYS[path] : undefined) ?? MENU_TITLE_TEXT_KEYS[title]
  return key ?? title
}

/**
 * 菜单标题 → 可显示文本。侧栏、面包屑、全局搜索、多标签、浏览器标题必须用同一条规则,
 * 否则同一个菜单在四个地方能显示成四个样子,侧栏最容易被漏掉不翻译。
 */
export function translateMenuTitle(title: string, path?: string): string {
  const key = menuTitleKey(title, path)
  return key.includes('.') ? t(key) : key
}
