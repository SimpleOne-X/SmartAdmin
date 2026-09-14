// AI 厂商预设的展示元数据:纯静态表,不发请求、不依赖后端。
// 后端 AiProviderPresets.cs 落定了 11 个预置 code(见 GET /api/v1/sys/ai/provider/presets 的 AiProviderPreset.code);
// 这里只补一份「按 code 取卡片缩写/图标/强调色」的视觉层,厂商预设下拉与厂商卡片(ProviderCard.vue/ProviderForm.vue)据此渲染,
// 厂商的展示名/协议/Base URL 等业务字段仍以后端返回的 AiProviderPreset 为准,不在这里重复。
// 新增预设 code 时后端与这里要同步维护;未登记的 code 统一兜底成 custom 的视觉,不会渲染不出来。

export interface AiProviderPresetMeta {
  /** 卡片角标用的极短缩写(拉丁字母,2 个字符左右,深色徽标上的文字) */
  abbr: string
  /** Iconify 图标名(ph:*,已随内核种子图标一并登记进 ph-subset.json) */
  icon: string
  /** 强调色(卡片描边 / 图标底色),16 进制 */
  color: string
}

/** 厂商预设 code → 展示元数据。key 必须与后端 AiProviderPresets.cs 的 code 一致。 */
export const AI_PROVIDER_PRESET_META: Readonly<Record<string, AiProviderPresetMeta>> = {
  openai: { abbr: 'AI', icon: 'ph:asterisk-duotone', color: '#10A37F' },
  'azure-openai': { abbr: 'Az', icon: 'ph:cloud-duotone', color: '#0078D4' },
  anthropic: { abbr: 'An', icon: 'ph:mountains-duotone', color: '#CC785C' },
  deepseek: { abbr: 'DS', icon: 'ph:compass-duotone', color: '#4D6BFE' },
  qwen: { abbr: 'Qw', icon: 'ph:wind-duotone', color: '#615CED' },
  zhipu: { abbr: 'Zh', icon: 'ph:diamond-duotone', color: '#3A5CE6' },
  moonshot: { abbr: 'Ms', icon: 'ph:moon-stars-duotone', color: '#16171A' },
  doubao: { abbr: 'Db', icon: 'ph:planet-duotone', color: '#F2A93B' },
  gemini: { abbr: 'Ge', icon: 'ph:globe-duotone', color: '#4285F4' },
  ollama: { abbr: 'Ol', icon: 'ph:terminal-window-duotone', color: '#4B5563' },
  custom: { abbr: 'Cu', icon: 'ph:puzzle-piece-duotone', color: '#8A8F98' },
}

/** 未登记 code 的兜底视觉(等同 custom)。 */
export const FALLBACK_PRESET_META: AiProviderPresetMeta = AI_PROVIDER_PRESET_META.custom!

/** 按 code 取展示元数据;查无此 code(新预设未同步维护)时兜底成 custom,不抛错。 */
export function resolvePresetMeta(code: string | null | undefined): AiProviderPresetMeta {
  if (!code) return FALLBACK_PRESET_META
  return AI_PROVIDER_PRESET_META[code] ?? FALLBACK_PRESET_META
}
