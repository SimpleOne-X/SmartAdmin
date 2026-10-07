// ECharts 按需注册 + 主题构建(BaseChart 的运行时基座)。
// 新增图种/组件时在下方 use([...]) 加一行即可,不引 echarts 全量 → 控包体。
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart, BarChart, PieChart, GaugeChart, RadarChart, ScatterChart } from 'echarts/charts'
import {
  TitleComponent,
  TooltipComponent,
  LegendComponent,
  GridComponent,
  DataZoomComponent,
  ToolboxComponent,
} from 'echarts/components'

use([
  CanvasRenderer,
  LineChart,
  BarChart,
  PieChart,
  GaugeChart,
  RadarChart,
  ScatterChart,
  TitleComponent,
  TooltipComponent,
  LegendComponent,
  GridComponent,
  DataZoomComponent,
  ToolboxComponent,
])

// 从 CSS tokens 现读一套 echarts 主题对象。单一色源 = tokens:
// 明暗切换 / accent 换色都由 useTheme 写 <html> 的 CSS 变量,这里现读即随之变。
// 调用点用 computed 显式依赖 app.isDark / app.accent 触发重算(见 index.vue)。
export function buildEChartsTheme() {
  const css = getComputedStyle(document.documentElement)
  const v = (name: string) => css.getPropertyValue(name).trim()
  const text = v('--color-text-secondary')
  const textStrong = v('--color-text-primary')
  const line = v('--color-border')
  const axis = {
    axisLine: { lineStyle: { color: line } },
    axisTick: { lineStyle: { color: line } },
    axisLabel: { color: text },
    splitLine: { lineStyle: { color: line } },
  }
  return {
    // 数据色 = 强调色的邻近色相(--chart-1..6,由 useTheme 随强调色与明暗写到 <html>);
    // 不用状态语义色(--color-success / warning / danger):绿 / 棕 / 红在蓝色主题里既不搭,又暗示「正常 / 警告 / 出错」。
    color: [1, 2, 3, 4, 5, 6].map(i => v(`--chart-${i}`)),
    backgroundColor: 'transparent',
    textStyle: { color: text },
    title: { textStyle: { color: textStrong } },
    legend: { textStyle: { color: text } },
    categoryAxis: axis,
    valueAxis: axis,
  }
}
