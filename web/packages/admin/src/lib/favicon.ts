// 浏览器标签页图标跟随站点 Logo。改写页面里所有 rel=icon 的 href,而不是再追加一个:
// 多个候选时各浏览器挑哪个并不一致,追加的那个未必生效。原 href/type 记在 WeakMap 里,Logo 清空时还原。
// 不记在 DOM 属性上:从 DOM 读出再写回 href,会被静态分析当成"DOM 文本重新解释为 HTML"。
// apple-touch-icon 不动:iOS 主屏图标要 180×180 不透明底,和 Logo 的要求不同。

interface IconDefaults {
  href: string
  type: string | null
}

const defaults = new WeakMap<HTMLLinkElement, IconDefaults>()

export function applyFavicon(doc: Document, logo: string | null | undefined): void {
  const links = doc.querySelectorAll<HTMLLinkElement>('link[rel~="icon"]')
  for (const link of links) {
    let original = defaults.get(link)
    if (!original) {
      original = { href: link.getAttribute('href') ?? '', type: link.getAttribute('type') }
      defaults.set(link, original)
    }
    if (logo) {
      link.href = logo
      link.removeAttribute('type') // 原 type 可能是 image/svg+xml,留着会让浏览器按错的类型解析 Logo
    } else {
      link.setAttribute('href', original.href)
      if (original.type) link.setAttribute('type', original.type)
    }
  }
}
