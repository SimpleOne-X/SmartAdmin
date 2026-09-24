// 浏览器标签页图标跟随站点 Logo。改写页面里所有 rel=icon 的 href,而不是再追加一个:
// 多个候选时各浏览器挑哪个并不一致,追加的那个未必生效。原 href 记在 data 属性上,Logo 清空时还原。
// apple-touch-icon 不动:iOS 主屏图标要 180×180 不透明底,和 Logo 的要求不同。

const DEFAULT_ATTR = 'data-default-href'

export function applyFavicon(doc: Document, logo: string | null | undefined): void {
  const links = doc.querySelectorAll<HTMLLinkElement>('link[rel~="icon"]')
  for (const link of links) {
    if (!link.hasAttribute(DEFAULT_ATTR)) {
      link.setAttribute(DEFAULT_ATTR, link.getAttribute('href') ?? '')
      link.setAttribute('data-default-type', link.getAttribute('type') ?? '')
    }
    if (logo) {
      link.href = logo
      link.removeAttribute('type') // 原 type 可能是 image/svg+xml,留着会让浏览器按错的类型解析 Logo
    } else {
      link.setAttribute('href', link.getAttribute(DEFAULT_ATTR) ?? '')
      const type = link.getAttribute('data-default-type')
      if (type) link.setAttribute('type', type)
    }
  }
}
