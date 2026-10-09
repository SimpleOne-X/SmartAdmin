import type { GlobalThemeOverrides } from 'naive-ui'
import { mix, rgba } from './mix'
import { deriveAccentTokens } from './accentTokens'

// 读取当前主题下的 token 值(getComputedStyle 同步反映最新的 data-theme)。
const v = (name: string): string =>
  getComputedStyle(document.documentElement).getPropertyValue(name).trim()

/** 语义色 hover/pressed 统一派生(hover 往白提 12%,pressed 往黑压 12%)。主色不在此列——它走 `mix.ts` 的 `derivePrimary`。 */
function semantic(base: string) {
  const hover = mix(base, '#FFFFFF', 0.12)
  return { base, hover, pressed: mix(base, '#000000', 0.12), suppl: hover }
}

/**
 * tokens.css → Naive UI GlobalThemeOverrides(映射关系见 DESIGN.md 映射表)。
 * 强调色相关的值由 accent 按 `accentTokens.ts` 算;其余颜色 / 度量从当前 CSS 变量读取。
 *
 * 度量(中文界面 14px 偏小,整体比 Naive 默认大一档):正文 15px、默认控件高 30px(小 24、迷你 20)、Large 42px 只留给登录页与触屏窄档。
 * 按钮默认款(没写 type 的 NButton,如表格库里的「搜索」「更多」)渲染成与 secondary 同色同无描边,整站只剩一种次按钮。
 */
export function buildThemeOverrides(opts: { dark: boolean; accent: string }): GlobalThemeOverrides {
  const { dark } = opts
  const a = deriveAccentTokens(opts.accent, dark)
  const { signal, acc, onAcc, hover, pressed, activeFg, linkFg, ring } = a

  const ok = semantic(v('--ok'))
  const warn = semantic(v('--warn'))
  const err = semantic(v('--err'))
  const info = semantic(v('--info'))

  const text1 = v('--text-1')
  const text2 = v('--text-2')
  const text3 = v('--text-3')
  const text4 = v('--text-4')
  const border = v('--border')
  const separator = v('--separator')
  const hairline = v('--hairline')
  const hairlineStrong = v('--hairline-strong')
  const tooltipBg = v('--tooltip-bg')
  const glassStrong = v('--glass-strong')
  const fill = v('--fill')
  const fillStrong = v('--fill-strong')
  const hoverBg = v('--hover')
  const pressedBg = v('--pressed')
  const input = v('--input')
  const inputDisabled = v('--input-disabled')
  const cardSolid = v('--bg-card')
  const elevated = v('--bg-elevated')
  const segTrack = v('--seg-track')
  const segThumb = v('--seg-thumb')
  const shadow1 = v('--shadow-1')
  const shadow2 = v('--shadow-2')
  const shadow3 = v('--shadow-3')
  const thBg = v('--th-bg')
  const thBgHover = v('--th-bg-hover')
  const tdBgHover = v('--td-bg-hover')

  const radius = v('--radius-md') // 9
  const selection = {
    borderRadius: radius,
    color: input,
    colorActive: input,
    colorDisabled: inputDisabled,
    border: `1px solid ${border}`,
    borderHover: `1px solid ${v('--select-border-hover')}`,
    borderActive: `1px solid ${signal}`,
    borderFocus: `1px solid ${signal}`,
    boxShadowActive: ring,
    boxShadowFocus: ring,
  }
  const checkbox = {
    borderRadius: v('--radius-xs'),
    boxShadowFocus: ring,
    checkMarkColor: '#FFFFFF',
    colorChecked: acc,
    borderChecked: `1px solid ${acc}`,
    colorIndeterminate: acc,
    borderIndeterminate: `1px solid ${acc}`,
    border: `1px solid ${border}`,
    color: input,
  }

  return {
    common: {
      primaryColor: acc,
      primaryColorHover: hover,
      primaryColorPressed: pressed,
      primaryColorSuppl: hover,

      successColor: ok.base,
      successColorHover: ok.hover,
      successColorPressed: ok.pressed,
      successColorSuppl: ok.suppl,
      warningColor: warn.base,
      warningColorHover: warn.hover,
      warningColorPressed: warn.pressed,
      warningColorSuppl: warn.suppl,
      errorColor: err.base,
      errorColorHover: err.hover,
      errorColorPressed: err.pressed,
      errorColorSuppl: err.suppl,
      infoColor: info.base,
      infoColorHover: info.hover,
      infoColorPressed: info.pressed,
      infoColorSuppl: info.suppl,

      bodyColor: v('--bg-app'),
      cardColor: v('--bg-card-glass'),
      tableColor: 'transparent',
      modalColor: elevated,
      popoverColor: elevated,

      textColorBase: text1,
      textColor1: text1,
      textColor2: text2,
      textColor3: text3,
      placeholderColor: text3,
      textColorDisabled: text4,

      borderColor: border,
      dividerColor: separator,
      // 两者同色会让禁用框和可填框长一个样,操作员分不清哪里能输入,必须走不同令牌。
      // 这两个 common 变量流向 Input 与 InternalSelection,
      // 即 NInput / NInputNumber / NSelect / NDatePicker / NCascader 等全部输入类组件。
      inputColor: input,
      inputColorDisabled: inputDisabled,
      actionColor: fill,
      tableHeaderColor: 'transparent',
      hoverColor: hoverBg,
      pressedColor: pressedBg,
      closeColorHover: hoverBg,
      closeColorPressed: pressedBg,
      clearColor: text3,

      borderRadius: radius,
      borderRadiusSmall: v('--radius-sm'),
      fontFamily: v('--font'),
      fontFamilyMono: v('--font-mono'),
      fontWeightStrong: '600',
      fontSizeMini: '13px',
      fontSizeTiny: '14px',
      fontSizeSmall: '14px',
      fontSizeMedium: '15px',
      fontSizeLarge: '16px',
      fontSizeHuge: '17px',
      fontSize: '15px',
      heightMini: '20px',
      heightTiny: '22px',
      heightSmall: '24px',
      heightMedium: '30px',
      heightLarge: '42px',
      heightHuge: '46px',

      boxShadow1: shadow1,
      boxShadow2: shadow2,
      boxShadow3: shadow3,
      cubicBezierEaseInOut: 'cubic-bezier(.2, .8, .2, 1)',
    },
    Button: {
      fontWeight: '500',
      borderRadiusMedium: radius,
      borderRadiusSmall: '7px',
      borderRadiusLarge: '12px',
      fontSizeLarge: '15px',
      // 点击不出扩散波纹:那是 Ant 系的反馈,macOS 的按钮只有按下态的底色变化(colorPressed)。
      // 波纹是定时器切 class + 无 fill-mode 的 CSS 动画,时长归零即彻底不可见,不必逐类型清 rippleColor。
      rippleDuration: '0s',
      paddingMedium: '0 12px',
      paddingSmall: '0 10px',
      textColorPrimary: onAcc,
      textColorHoverPrimary: onAcc,
      textColorPressedPrimary: onAcc,
      textColorFocusPrimary: onAcc,
      textColorDisabledPrimary: onAcc,
      colorSecondary: fill,
      colorSecondaryHover: fillStrong,
      colorSecondaryPressed: pressedBg,
      // 没写 type 的默认款(库里的「搜索」「更多」)渲染成与 secondary 同款:中性灰底、无描边
      color: fill,
      colorHover: fillStrong,
      colorPressed: pressedBg,
      colorFocus: fill,
      border: '1px solid transparent',
      borderHover: '1px solid transparent',
      borderPressed: '1px solid transparent',
      borderFocus: `1px solid ${signal}`,
      // 文字 / 行内操作按钮(详情、编辑):浅色 / 深色统一用 linkFg
      textColorTextPrimary: linkFg,
      textColorTextHoverPrimary: linkFg,
      textColorTextPressedPrimary: linkFg,
      textColorTextFocusPrimary: linkFg,
      textColorGhostPrimary: linkFg,
      textColorGhostHoverPrimary: linkFg,
      textColorGhostPressedPrimary: linkFg,
      textColorGhostFocusPrimary: linkFg,
      colorQuaternaryHover: hoverBg,
      colorQuaternaryPressed: pressedBg,
      colorTertiaryHover: hoverBg,
      colorTertiaryPressed: pressedBg,
    },
    Input: {
      borderRadius: radius,
      color: input,
      colorDisabled: inputDisabled,
      colorFocus: input,
      border: `1px solid ${border}`,
      borderHover: `1px solid ${v('--input-border-hover')}`,
      borderFocus: `1px solid ${signal}`,
      boxShadowFocus: ring,
      heightLarge: '42px',
      fontSizeLarge: '15px',
      caretColor: signal,
    },
    Select: {
      peers: {
        InternalSelection: selection,
        InternalSelectMenu: {
          borderRadius: '12px',
          optionHeightMedium: '30px',
          color: elevated,
          optionCheckColor: signal,
        },
      },
    },
    TreeSelect: { peers: { InternalSelection: selection } },
    DatePicker: {
      peers: {
        Input: {
          borderRadius: radius,
          color: input,
          borderFocus: `1px solid ${signal}`,
          boxShadowFocus: ring,
        },
      },
    },
    InputNumber: {
      peers: { Input: { color: input, borderFocus: `1px solid ${signal}`, boxShadowFocus: ring } },
    },
    Card: {
      color: cardSolid,
      colorEmbedded: fill,
      borderColor: 'transparent',
      borderRadius: v('--radius-lg'),
      titleFontSizeMedium: '15px',
      titleFontSizeSmall: '15px',
      titleFontWeight: '600',
      paddingMedium: '12px 14px 14px',
      paddingSmall: '10px 12px',
      colorModal: elevated,
      actionColor: fill,
    },
    DataTable: {
      thColor: thBg,
      thColorHover: thBgHover,
      thColorSorting: thBgHover,
      // 内容层不透明:行底取实色卡片色(固定列 sticky 叠在别的列上不会透底),不加斑马纹(tdColorStriped 同色)
      tdColor: cardSolid,
      tdColorHover: tdBgHover,
      tdColorStriped: cardSolid,
      tdColorSorting: cardSolid,
      thTextColor: text2,
      thFontWeight: '600',
      thIconColor: text3,
      // 表格线(列竖线 / 外框 / 行线)的标准就是 --separator(亮色 0.16),不换更实的线色:见 DESIGN.md §5「表格线色」
      borderColor: separator,
      borderRadius: '10px',
      thPaddingMedium: '6px 12px',
      tdPaddingMedium: '4px 12px',
      fontSizeMedium: '15px',
      thPaddingSmall: '6px 10px',
      tdPaddingSmall: '4px 10px',
      fontSizeSmall: '14px',
      peers: { Checkbox: checkbox },
    },
    Menu: {
      borderRadius: radius,
      itemHeight: '34px',
      fontSize: '15px',
      itemColorHover: hoverBg,
      itemColorActive: rgba(signal, dark ? 0.16 : 0.09),
      itemColorActiveHover: rgba(signal, dark ? 0.2 : 0.12),
      itemColorActiveCollapsed: rgba(signal, dark ? 0.16 : 0.09),
      // 侧栏标签用主文字色(macOS 侧栏就是 label 主色,不是次级灰);选中 / 悬停靠底色区分
      itemTextColor: text1,
      itemTextColorHover: text1,
      // 侧栏图标与文字同色(每个状态都一致):平时主文字色,选中才跟随强调色;
      // 强调色只出现在选中项(行底色 + 加粗的图标文字),整条侧栏更安静
      itemIconColor: text1,
      itemIconColorHover: text1,
      itemIconColorCollapsed: text1,
      itemTextColorActive: activeFg,
      itemIconColorActive: activeFg,
      itemTextColorActiveHover: activeFg,
      itemIconColorActiveHover: activeFg,
      itemTextColorChildActive: text1,
      itemIconColorChildActive: text1,
      itemTextColorChildActiveHover: text1,
      itemIconColorChildActiveHover: text1,
      arrowColor: text3,
      arrowColorHover: text2,
      arrowColorActive: activeFg,
      arrowColorActiveHover: activeFg,
      arrowColorChildActive: text2,
      arrowColorChildActiveHover: text1,
      groupTextColor: text3,
    },
    Tabs: {
      colorSegment: segTrack,
      tabColorSegment: segThumb,
      tabBorderRadius: '8px',
      tabTextColorSegment: text2,
      tabTextColorActiveSegment: text1,
      tabTextColorHoverSegment: text1,
      tabFontWeightActive: '500',
      tabColor: 'transparent',
      tabBorderColor: 'transparent',
      tabTextColorCard: text3,
      tabTextColorActiveCard: activeFg,
      tabTextColorHoverCard: text1,
      tabTextColorActiveLine: text1,
      tabTextColorHoverLine: text1,
      barColor: acc,
    },
    Radio: {
      buttonBorderRadius: '8px',
      buttonColor: segTrack,
      buttonColorActive: segThumb,
      buttonBorderColor: 'transparent',
      buttonBorderColorActive: 'transparent',
      buttonBorderColorHover: 'transparent',
      buttonTextColor: text2,
      buttonTextColorActive: text1,
      buttonTextColorHover: text1,
      buttonBoxShadow: 'none',
      buttonBoxShadowHover: 'none',
      buttonBoxShadowFocus: ring,
      dotColorActive: acc,
      boxShadowActive: `inset 0 0 0 1px ${acc}`,
      boxShadowFocus: ring,
    },
    Checkbox: checkbox,
    Tree: {
      nodeBorderRadius: '8px',
      nodeColorHover: hoverBg,
      nodeColorActive: glassStrong,
      nodeHeight: '30px',
      peers: { Checkbox: checkbox },
    },
    Switch: {
      railColor: v('--switch-off'),
      railColorActive: acc,
      boxShadowFocus: ring,
      railHeightMedium: '24px',
      railWidthMedium: '42px',
      buttonHeightMedium: '20px',
      buttonWidthMedium: '20px',
      buttonWidthPressedMedium: '26px',
    },
    Tag: {
      heightTiny: '20px',
      heightSmall: '22px',
      heightMedium: '24px',
      fontSizeTiny: '13px',
      fontSizeSmall: '14px',
      fontSizeMedium: '15px',
      borderRadius: v('--radius-sm'),
      color: fill,
      textColor: text2,
      fontWeightStrong: '500',
      colorPrimary: glassStrong,
      textColorPrimary: text1,
      borderPrimary: `1px solid ${hairlineStrong}`,
    },
    List: { color: 'transparent', colorHover: hoverBg, borderColor: separator },
    Dialog: {
      borderRadius: v('--radius-xl'),
      color: elevated,
      padding: '24px 26px 22px',
      titleFontSize: '17px',
      titleFontWeight: '600',
    },
    Modal: { color: elevated, boxShadow: shadow3 },
    Drawer: {
      color: elevated,
      headerPadding: '18px 24px',
      bodyPadding: '20px 24px',
      footerPadding: '14px 24px',
      titleFontSize: '17px',
      titleFontWeight: '600',
      headerBorderBottom: `1px solid ${separator}`,
      footerBorderTop: `1px solid ${separator}`,
    },
    Popover: { color: elevated, borderRadius: v('--radius-lg'), boxShadow: shadow2 },
    Dropdown: {
      color: elevated,
      borderRadius: '12px',
      padding: '5px',
      optionHeightMedium: '30px',
      optionColorHover: hoverBg,
      dividerColor: separator,
    },
    // 提示气泡和下拉 / 弹层同一套浮层表面,随亮暗主题走(亮色近白底深字,暗色深蓝底亮字),不做亮暗都黑的特例。
    // 底色、字色、阴影成套给:Tooltip 底下是 Popover,Naive 合并时全局 `Popover` 覆盖(上面的 elevated 与大阴影)
    // 压过 Tooltip 自带的值,所以同一套要在 peers.Popover 里再写一遍(它在合并链最后,只对 Tooltip 生效)。
    // 阴影用描边 + 卡片级的轻阴影:气泡很小,弹层那种大范围投影会显得过重。
    Tooltip: {
      color: tooltipBg,
      textColor: text1,
      borderRadius: '8px',
      padding: '5px 9px',
      boxShadow: `0 0 0 1px ${hairlineStrong}, ${shadow1}`,
      peers: {
        Popover: {
          color: tooltipBg,
          textColor: text1,
          borderRadius: '8px',
          padding: '5px 9px',
          boxShadow: `0 0 0 1px ${hairlineStrong}, ${shadow1}`,
        },
      },
    },
    Message: { borderRadius: '12px', color: elevated, padding: '10px 16px', boxShadow: shadow2 },
    Notification: { borderRadius: '16px', color: elevated, boxShadow: shadow2 },
    Alert: {
      borderRadius: '10px',
      colorInfo: fill,
      borderInfo: `1px solid ${hairline}`,
      iconColorInfo: text2,
      titleTextColorInfo: text1,
      contentTextColorInfo: text2,
    },
    Pagination: {
      itemBorderRadius: '8px',
      itemColorActive: glassStrong,
      itemTextColorActive: text1,
      itemBorderActive: `1px solid ${hairlineStrong}`,
      itemColorActiveHover: glassStrong,
    },
    Statistic: {
      valueFontSize: '26px',
      valueFontWeight: '500',
      labelFontSize: '13px',
      labelTextColor: text2,
    },
    Descriptions: {
      thColor: fill,
      borderColor: separator,
      thTextColor: text3,
      tdColor: 'transparent',
    },
    Progress: { railColor: fill, fillColor: acc },
    Breadcrumb: { itemTextColor: text3, itemTextColorActive: text1, separatorColor: text4 },
    Slider: {
      fillColor: acc,
      fillColorHover: acc,
      railColor: fillStrong,
      railColorHover: fillStrong,
      dotBorderActive: `2px solid ${acc}`,
    },
    Steps: {
      indicatorColorProcess: acc,
      indicatorTextColorProcess: onAcc,
      indicatorBorderColorProcess: acc,
      indicatorColorFinish: 'transparent',
      indicatorBorderColorFinish: border,
      indicatorTextColorFinish: text2,
      splitorColorFinish: text3,
      splitorColorProcess: separator,
      splitorColorWait: separator,
    },
    Empty: { textColor: text3, iconColor: text4 },
    Divider: { color: separator },
    Timeline: { circleBorder: `2px solid ${border}`, titleFontWeight: '500', lineColor: separator },
    Badge: { color: v('--badge-bg') },
    Scrollbar: { color: fillStrong, colorHover: border },
    Code: { textColor: text1 },
  }
}
