// 多应用门户的进门/切换流程:登录后该直接进哪个应用、还是弹应用选择器,以及切换时清理旧应用的路由与标签。
// enterInitial() 也是 F5/深链的恢复入口(动态路由只在内存里),路由守卫会调它。
// 坑:权限码拉取失败时 permissionsLoaded 保持 false,v-auth 走 fail-closed 藏按钮 —— 宁可少显示,不谎报有权限。
import { useAuthStore } from '#/stores/auth'
import { useTabsStore } from '#/stores/tabs'
import { useUserStore } from '#/stores/user'
import { personalApi } from '#/api'
import { buildRoutesForModule } from './useAuthMenu'
import { menuHasPath, normalizeRoutePath } from './authMenuRoute'
import { viewComponentPaths } from '#/router/viewRegistry'
import { router } from '#/router'
import type { AppModule, MenuNode } from '#/types/menu'

type EnterResult = { chooser: true } | { chooser: false; moduleId: number }

// 进门单飞:F5/深链时守卫可能被并发导航各调一次 enterInitial,而 buildRoutesForModule 起手就 resetRouter()
// —— 后一次会把前一次刚注册的动态路由整片摘掉,表现是偶发 404/白屏。模块级 promise 让并发合流成一次。
let entering: Promise<EnterResult> | null = null

/** 进指定应用:重建它的动态路由。 */
async function enter(moduleId: number): Promise<EnterResult> {
  await buildRoutesForModule(moduleId)
  return { chooser: false, moduleId }
}

/** 门户:登录后决定直接进 / 进默认 / 弹选择器,以及切换应用。逻辑与 Naive 无关。 */
export function useModule() {
  const auth = useAuthStore()

  function enterInitial(): Promise<EnterResult> {
    entering ??= doEnterInitial().finally(() => {
      entering = null
    })
    return entering
  }

  async function doEnterInitial(): Promise<EnterResult> {
    // 并行拉模块 + 当前用户权限码。权限码喂 v-auth:成功(哪怕空集=超管)才标 loaded;
    // 失败不阻断进门户,但 permissionsLoaded 保持 false → v-auth fail-closed(藏按钮),不谎报"有权限"。
    // profile 拿超管标记喂 v-auth(只对超管 fail-open,普通用户空集则隐藏)+ 顺手回填顶栏头像;失败按普通用户处理(安全侧,不误放行)。
    const [{ modules, defaultModuleId }, perm, profile] = await Promise.all([
      personalApi.modules(),
      personalApi
        .permissions()
        .then(codes => ({ ok: true, codes }))
        .catch(() => ({ ok: false, codes: [] as string[] })),
      personalApi
        .profile()
        .then(p => ({
          sadm: p.isSuperAdmin,
          avatar: p.avatar ?? null,
          orgName: p.orgName ?? null,
          phoneTail: p.phone ? p.phone.slice(-4) : null,
        }))
        .catch(() => ({
          sadm: useUserStore().userInfo?.isSuperAdmin ?? false,
          avatar: null,
          orgName: null,
          phoneTail: null,
        })),
    ])
    auth.modules = modules
    auth.defaultModuleId = defaultModuleId ?? null
    auth.permissionCodes = perm.codes
    auth.permissionsLoaded = perm.ok
    auth.isSuperAdmin = profile.sadm
    // 顶栏头像:登录出参不含 avatar,这里回填(取不到按无头像处理,顶栏回落图标)
    const user = useUserStore()
    if (user.userInfo) {
      user.userInfo.avatar = profile.avatar
      user.userInfo.orgName = profile.orgName // 水印要印机构和手机尾号,同样只有 profile 里有
      user.userInfo.phoneTail = profile.phoneTail
    }
    if (modules.length === 0) return { chooser: true } // 空态:选择器里提示未分配应用
    // F5/深链优先重建"上次所在应用"(持久化的 currentModuleId),让其动态路由复活,跨应用深链不落 404。
    const remembered = auth.currentModuleId
    if (remembered && modules.some(m => m.id === remembered)) return enter(remembered)
    if (modules.length === 1) return enter(modules[0]!.id)
    if (defaultModuleId && modules.some(m => m.id === defaultModuleId))
      return enter(defaultModuleId)
    return { chooser: true }
  }

  /** `target` 缺省落新应用自己的首页;给了就落到那个地址(跨应用深链:先切过去再打开它)。 */
  async function switchModule(moduleId: number, target?: string): Promise<void> {
    await enter(moduleId)
    useTabsStore().clearTabs() // 切应用 → 标签归零(新应用路由已重建)
    router.replace(target ?? auth.homePath)
  }

  /**
   * 用户有权进入的其它应用里,哪个注册了 `path` 这个页面;没有则 null。
   * 动态路由只按当前应用的菜单注册,别的应用的页面地址会落到通配 404 —— 404 页靠它把
   * 「地址不存在」和「页面属于别的应用」分开。只在命中 404 时才调,所以不预拉各应用的菜单。
   * 某个应用的菜单拉取失败按「没有」处理:这是个提示,不该因它再报一层错。
   */
  async function findOwnerModule(path: string): Promise<AppModule | null> {
    const target = normalizeRoutePath(path)
    const others = auth.modules.filter(m => m.id !== auth.currentModuleId)
    // defaultRoute 是现成的索引,命中就不必拉菜单
    const byDefault = others.find(
      m => m.defaultRoute && normalizeRoutePath(m.defaultRoute) === target,
    )
    if (byDefault) return byDefault

    const viewKeys = new Set(viewComponentPaths())
    const trees = await Promise.all(
      others.map(m => personalApi.menu(m.id).catch(() => [] as MenuNode[])),
    )
    const hit = trees.findIndex(tree => menuHasPath(tree, target, viewKeys))
    return hit < 0 ? null : others[hit]!
  }

  async function setDefault(moduleId: number): Promise<void> {
    await personalApi.setDefaultModule(moduleId)
    auth.defaultModuleId = moduleId // 本地同步,选择页角标立刻转移,不必重拉 /personal/modules
  }

  return { enter, enterInitial, switchModule, findOwnerModule, setDefault }
}
