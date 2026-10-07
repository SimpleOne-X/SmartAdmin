import { nextTick, ref, watch, type Ref } from 'vue'
import { darkTheme, type GlobalTheme, type GlobalThemeOverrides } from 'naive-ui'
import { useAppStore } from '#/stores/app'
import { buildThemeOverrides } from '#/theme/naive-theme'
import { deriveAccentTokens } from '#/theme/accentTokens'

// 浏览器支持 View Transitions 且系统没开「减少动态效果」才做交叉淡入
function canCrossFade(): boolean {
  return (
    typeof document.startViewTransition === 'function' &&
    !window.matchMedia('(prefers-reduced-motion: reduce)').matches
  )
}

/**
 * 主题落地:随 app.isDark / app.accent / app.density 变化,
 *  1) 打 data-theme / data-density 到 <html>(裸 CSS 与 tokens 跟着翻);
 *  2) 把 accent 派生的强调色令牌(--accent / --acc-solid / --sel-* 等,见 theme/accentTokens.ts)写到 <html>(令消费 tokens 的裸 CSS 换色);
 *  3) 重建 Naive themeOverrides(新对象触发 n-config-provider 重渲染)。
 * 返回类型显式写出:推断出的 Naive 主题类型展开后超出 d.ts 序列化上限,包的类型声明会不完整。
 */
export function useTheme(): {
  overrides: Ref<GlobalThemeOverrides>
  naiveTheme: Ref<GlobalTheme | null>
} {
  const app = useAppStore()
  const overrides = ref<GlobalThemeOverrides>({}) as Ref<GlobalThemeOverrides>
  const naiveTheme = ref<GlobalTheme | null>(null) as Ref<GlobalTheme | null>

  // 明暗切换的过渡:像 macOS 那样把整屏旧画面与新画面做一次交叉淡入(View Transitions 的快照)。
  // 不给每个元素单独挂颜色过渡——Naive 输入框由边框层 / 状态边框层 / 阴影叠成,各层半透明边框色
  // 各自在中间色上插值,暗→亮时会闪出一圈忽明忽暗的线;快照淡入没有逐元素的中间态。
  // 浏览器不支持、或系统开了「减少动态效果」时直接瞬切。
  let lastDark: boolean | null = null

  // 强调色令牌(--accent / --signal / --acc-solid / --sel-* / --tab-* / --ambient-* / --desk-* 等):
  // 写到 <html> 的 style 上,令消费 tokens 的裸 CSS 换色。兼容别名 --color-primary* 在 tokens.css 里引用它们,不用单独写。
  function applyAccentVars() {
    const el = document.documentElement
    const { css } = deriveAccentTokens(app.accent, app.isDark)
    for (const [name, value] of Object.entries(css)) el.style.setProperty(name, value)
  }

  function applyNow() {
    const nextDark = app.isDark
    const el = document.documentElement
    el.setAttribute('data-theme', nextDark ? 'dark' : '')
    el.setAttribute('data-density', app.density)
    applyAccentVars()
    el.style.colorScheme = nextDark ? 'dark' : 'light' // 原生滚动条 / 表单控件跟着翻
    naiveTheme.value = app.isDark ? darkTheme : null
    overrides.value = buildThemeOverrides({ dark: app.isDark, accent: app.accent })
  }

  function apply() {
    // 只有明暗翻面才做交叉淡入;首次落地、换强调色、换密度都瞬切
    const flipped = lastDark !== null && lastDark !== app.isDark
    lastDark = app.isDark
    if (flipped && canCrossFade()) {
      // 回调里改 DOM,并等 Vue 把 Naive 的新主题渲染完再返回,新快照才是完整的新画面
      document.startViewTransition(async () => {
        applyNow()
        await nextTick()
      })
    } else {
      applyNow()
    }
  }

  watch([() => app.isDark, () => app.accent, () => app.density], apply, {
    immediate: true,
  })

  return { overrides, naiveTheme }
}
