export interface LoginHeroCopy {
  headline: string
  highlight: string
  features: string[]
}

export interface LoginHeroFallbacks {
  headline: string
  highlight: string
  features: string[]
}

/** 卖点按行输入;空行忽略,最多保留 5 条。 */
export function splitLoginHeroFeatures(value: string | null | undefined, max = 5): string[] {
  if (!value?.trim()) return []
  return value
    .replace(/\r\n/g, '\n')
    .split('\n')
    .map(line => line.trim())
    .filter(Boolean)
    .slice(0, max)
}

/** 归一化站点接口下发的 Hero 文案,非法形状按缺失处理。 */
export function normalizeLoginHero(value: unknown): Record<string, LoginHeroCopy> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {}

  const result: Record<string, LoginHeroCopy> = {}
  for (const [locale, raw] of Object.entries(value)) {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) continue
    const copy = raw as Record<string, unknown>
    result[locale] = {
      headline: typeof copy.headline === 'string' ? copy.headline : '',
      highlight: typeof copy.highlight === 'string' ? copy.highlight : '',
      features: Array.isArray(copy.features)
        ? copy.features.filter((item): item is string => typeof item === 'string' && !!item.trim())
        : [],
    }
  }
  return result
}

/** 空配置逐字段回退内置文案;卖点开关关闭时返回空清单。 */
export function resolveLoginHero(
  copy: Partial<LoginHeroCopy> | undefined,
  fallback: LoginHeroFallbacks,
  showFeatures = true,
): LoginHeroCopy {
  return {
    headline: copy?.headline?.trim() || fallback.headline,
    highlight: copy?.highlight?.trim() || fallback.highlight,
    features: showFeatures ? (copy?.features?.length ? copy.features : fallback.features) : [],
  }
}
