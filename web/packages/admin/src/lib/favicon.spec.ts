import { beforeEach, describe, expect, it } from 'vitest'
import { applyFavicon } from './favicon'

const icons = () => [...document.querySelectorAll<HTMLLinkElement>('link[rel="icon"]')]

describe('applyFavicon', () => {
  beforeEach(() => {
    document.head.innerHTML = `
      <link rel="icon" href="/favicon.ico" sizes="any" />
      <link rel="icon" type="image/svg+xml" href="/smart-logo.svg" />
      <link rel="apple-touch-icon" href="/apple-touch-icon.png" />`
  })

  it('有 Logo 时改写所有 rel=icon,去掉可能不符的 type', () => {
    applyFavicon(document, '/api/v1/sys/file/1/view?sig=x')
    for (const link of icons()) {
      expect(link.getAttribute('href')).toBe('/api/v1/sys/file/1/view?sig=x')
      expect(link.hasAttribute('type')).toBe(false)
    }
  })

  it('Logo 清空时还原模板自带的图标和 type', () => {
    applyFavicon(document, '/logo.png')
    applyFavicon(document, '')
    expect(icons().map(l => l.getAttribute('href'))).toEqual(['/favicon.ico', '/smart-logo.svg'])
    expect(icons()[1].getAttribute('type')).toBe('image/svg+xml')
  })

  it('不动 apple-touch-icon', () => {
    applyFavicon(document, '/logo.png')
    expect(document.querySelector('link[rel="apple-touch-icon"]')?.getAttribute('href')).toBe(
      '/apple-touch-icon.png',
    )
  })

  it('连续换 Logo 仍记得最初的默认图标', () => {
    applyFavicon(document, '/a.png')
    applyFavicon(document, '/b.png')
    applyFavicon(document, null)
    expect(icons()[0].getAttribute('href')).toBe('/favicon.ico')
  })
})
