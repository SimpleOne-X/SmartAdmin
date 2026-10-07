/** 在线时长拆成小时/分钟,供 i18n 模板插值;不做负数(时钟回拨/服务器时间误差时钳到 0)。 */
export function onlineDurationParts(
  loginTime: string,
  now: Date = new Date(),
): { hours: number; minutes: number } {
  const start = new Date(loginTime)
  const ms = Math.max(0, now.getTime() - start.getTime())
  const totalMinutes = Math.floor(ms / 60000)
  return { hours: Math.floor(totalMinutes / 60), minutes: totalMinutes % 60 }
}
