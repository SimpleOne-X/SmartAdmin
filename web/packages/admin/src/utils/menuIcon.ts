// 菜单 / 页签图标统一成 Phosphor 线条款。
// 菜单种子(DefaultMenuSeed.cs)与库里配的菜单存的是带 `-duotone` 后缀的名字,而设计定全站线条款
// (14–18px 的小图标上,淡色填充层会和线条糊在一起)。渲染处去掉后缀,不必改库里的数据:
// 管理员在菜单管理里配的带该后缀的名字同样生效,换了款式也不用改数据。
// 只剥 `-duotone`;`-fill` 实心款是设计里保留的点缀(品牌标、状态点),原样放行。

/** 去掉名字末尾的 `-duotone` 后缀(如 users 款);其它名字原样返回。 */
export function lineIcon(name: string | undefined | null): string | undefined {
  if (!name) return undefined
  return name.replace(/-duotone$/, '')
}
