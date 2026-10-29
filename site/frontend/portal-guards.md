# Multi-App Portal & Router Guards

Which app a user lands in after login is decided by a ladder, tried in order: the remembered app, the only app, the default app — and only when none of those holds does the chooser appear. A user may be authorized for several apps (modules) at once, so that landing point can't be hard-coded; it's computed for the current user.

## Which app to enter after login: enterInitial

SmartAdmin's shell is a multi-app portal: each user is authorized for some set of apps, and a nine-square chooser in the top-right switches between them at any time. What decides, after login or a hard refresh, whether to "go straight into an app" or "show the chooser" is `enterInitial()` in `composables/useModule.ts`:

```ts
async function enterInitial(): Promise<EnterResult> {
  const [{ modules, defaultModuleId }, perm, profile] = await Promise.all([
    personalApi.modules(),
    personalApi.permissions().then((codes) => ({ ok: true, codes })).catch(() => ({ ok: false, codes: [] as string[] })),
    personalApi.profile().then((p) => ({ sadm: p.isSuperAdmin })).catch(() => ({ sadm: false })),
  ])
  auth.modules = modules
  auth.defaultModuleId = defaultModuleId ?? null
  auth.permissionCodes = perm.codes
  auth.permissionsLoaded = perm.ok
  auth.isSuperAdmin = profile.sadm
  if (modules.length === 0) return { chooser: true }
  const remembered = auth.currentModuleId
  if (remembered && modules.some((m) => m.id === remembered)) return enter(remembered)
  if (modules.length === 1) return enter(modules[0]!.id)
  if (defaultModuleId && modules.some((m) => m.id === defaultModuleId)) return enter(defaultModuleId)
  return { chooser: true }
}
```

The module list, permission codes, and super-admin flag are fetched in parallel. The latter two fail closed: the moment `personalApi.permissions()` fails, `permissionsLoaded` stays `false`, and the `v-auth` directive treats that as "hide it" rather than granting access it can't be sure of; if `profile` can't be fetched, the user is treated as ordinary, so nobody gets mistaken for a super admin. This step doesn't block entry to the portal — you can still get in without permissions, every button except a super admin's is just treated as "no access" for now. A super admin goes through the separate fail-open branch in `hasPerm` keyed on `isSuperAdmin`, so their buttons still show even when the permission-code fetch failed — the server-side `sadm` claim backstops it either way.

Once that data is in, `enterInitial` runs an "which app to enter" ladder, top to bottom, first hit wins:

- **No apps assigned at all** → show the chooser, with an "no apps assigned" empty-state hint on the chooser page.
- **A remembered app that's still in your app list** → go straight into it. That "remembered app" is `auth.currentModuleId`, the *only* persisted field in the auth store (`buildRoutesForModule` writes it every time you enter an app). A hard refresh or a deep link landing back in the app you were last in is entirely down to this rung.
- **Exactly one app** → go straight in, no chooser needed.
- **A default app is configured** (`defaultModuleId`, settable on the chooser page via `setDefault`) and it's in the list → go straight in.
- **None of the above** → show the chooser.

Switching apps takes a different path. `switchModule(moduleId)` re-runs `enter()` (rebuilding that app's dynamic routes — internally it's the same `buildRoutesForModule(moduleId)` covered on the [Routing & Dynamic Menus](/frontend/routing) page), clears the tabs store (a new app means the tab bar should start from scratch), and replaces the current route with the new app's `homePath`. `homePath` is an auth-store getter: it prefers the module's own `defaultRoute`, falls back to the first leaf of the menu tree, and failing that lands back on `/module` — an app with no menus configured has no home page to speak of, and sending the user back to the chooser beats crashing them into a path that doesn't belong to this app with a 404.

## The guard: every navigation runs through beforeEach

`router/index.ts`'s `beforeEach` is the seam that stitches the static shell, the dynamic routes, and the portal state together (excerpted; see the source for the full definition):

```ts
router.beforeEach(async to => {
  const user = useUserStore()
  const auth = useAuthStore()

  // Cookie session: access lives only in memory; a silent HttpOnly-refresh exchange restores it after F5.
  if (!user.accessToken && (user.cookieSession || user.refreshToken)) {
    const outcome = await refreshSession()
    if (outcome === 'dead') user.clear()
    else if (outcome === 'transient' && to.name !== 'login' && !to.meta.public)
      return to.name === 'boot-error'
        ? true
        : { path: '/boot-error', query: { redirect: to.fullPath }, replace: true }
  }

  if (to.name === 'login') {
    rememberLoginRedirect(to.query.redirect)
    const needReauth = !!(to.query.pendingLink || to.query.totpChallenge)
    if (needReauth) {
      if (user.accessToken || user.refreshToken || user.cookieSession) {
        resetRouter()
        auth.reset()
        user.clear()
      }
      return true
    }
    if (!user.isLoggedIn) return true
    const { path, query, hash } = router.resolve(takeLoginRedirect() ?? '/')
    return { path, query, hash, replace: true }
  }

  // Public OAuth-callback and MFA bind/recover pages must not be bounced to login.
  if (to.meta.public) return true

  if (!user.isLoggedIn) return loginLocation(to.fullPath)

  if (user.userInfo?.mustChangePassword) {
    return to.path === '/personal/password' ? true : { path: '/personal/password', replace: true }
  }

  // The rebuild-failure page is itself the landing spot for a failed rebuild — let it through,
  // or the rebuild guard below retries on it and loops forever.
  if (to.name === 'boot-error') return true

  if (!auth.routesReady) {
    try {
      const { useModule } = await import('#/composables/useModule')
      const res = await useModule().enterInitial()
      if (res.chooser)
        return to.name === 'module' || isFullscreenPath(to.path)
          ? true
          : { path: '/module', replace: true }
      if (to.name === 'module') return true
      if (to.path === '/') return { path: auth.homePath, replace: true }
      return to.fullPath
    } catch (e) {
      // Only clear the session when it's genuinely dead; network jitter/5xx/rate-limits stay put
      // for a retryable error page (see bootFailure.ts).
      if (isSessionDead(e)) {
        resetRouter()
        auth.reset()
        user.clear()
        return loginLocation(to.fullPath)
      }
      return { path: '/boot-error', query: { redirect: to.fullPath }, replace: true }
    }
  }

  if (to.path === '/') return { path: auth.homePath, replace: true }
  return true
})
```

It handles these things, in order:

**Silent token refresh for a Cookie session.** When there's no `accessToken` in memory but either a `cookieSession` flag or a `refreshToken` is still around, the guard first `await`s `refreshSession()` for a silent renewal. In Cookie-session mode `accessToken` lives only in memory, so it's inevitably empty after an F5, and has to be restored via the HttpOnly refresh cookie; in the default mode the token is already hydrated from `localStorage`, so this step doesn't fire. There are three outcomes: `ok` falls through; `dead` (the server says outright the session is gone) clears whatever login state is left locally; `transient` (network jitter, a backend restart, a rate limit — the session may well still be valid) keeps the session, and sends a page that needs one to the retryable `/boot-error`, while the login page and `meta.public` pages, which never needed a session, pass through as usual.

**Login redirect, including SSO re-verification.** `/login` is the one auth-free page; an already-logged-in user visiting it is bounced to the remembered redirect target, or `/` if there is none. The exception is a URL carrying `pendingLink` (an unlinked SSO account) or `totpChallenge` (a second factor after SSO) — both must stay on the login page to finish the password or second-factor step, even with a leftover session still attached (skip the cleanup and a user could see the odd sight of unlinking a third-party account, then logging back in and sailing straight past the login page).

**Back to where you were after login.** An unauthenticated visit to a page (say, the deep link of a wall-mounted board) sends the user to `/login?redirect=<original address>` (no `redirect` when the address is the home page); a session that dies mid-use returns to the login page with the current address the same way. The login page stashes `redirect` in `sessionStorage` on entry, because sign-in can detour (an SSO round trip, the MFA bind page) and the query never survives to the moment login succeeds; once it does, `takeLoginRedirect()` reads it and deletes it, and the user lands back on the original address. `redirect` comes from the URL and is untrusted: only a same-site absolute path is accepted, and `//evil.com`, `/\evil.com` (and their percent-encoded forms such as `/%2F%2Fevil.com`), `https://…` and the login page itself are all dropped (`safeRedirect` in `router/loginRedirect.ts`), so it can't be abused as an open redirect.

**`meta.public` passes straight through.** Public routes like the OAuth callback and MFA bind/recovery aren't caught by the "not logged in → send to `/login`" check below.

**Forced password change.** Once `mustChangePassword` is true (it comes back on the first login after an admin creates or resets an account), every navigation except `/personal/password` itself is intercepted and redirected there. This check is placed deliberately *before* the dynamic-route rebuild below: the password page is a static route and renders without the menu tree, so letting it through first avoids the "rebuild → pick app → bounced back to password again" loop. Once the password change succeeds, the existing flow forces a logout and re-login, and the backend clears the flag.

**`boot-error` passes itself through.** It's where a failed portal rebuild lands, and it can't be caught by the rebuild guard below and retried, or it loops forever. The page's Retry navigates to `redirect` again and goes back through the guard; when `redirect` is a `/fullscreen/` page, the error page retries on its own every 30 seconds — a wall-mounted board is unattended, and if it boots before the server or catches a redeploy restart it has to wait for the backend by itself. When the retry fails again the guard lands on the same error-page address, the navigation is deduplicated, the page isn't remounted, and the timer keeps running.

**The refresh / deep-link rebuild, triage by error kind.** Dynamic routes live only in the router's in-memory route table — they aren't persisted. On a hard refresh or a directly-opened deep link, `auth.routesReady` is inevitably `false` and none of the `menu-{id}` routes are registered yet. The guard detects this and calls `enterInitial()` right here — which both rebuilds the routes and fills in `auth.modules`, so the portal's decision and the guard's rebuild share one and the same call. With the result in hand the guard then decides where to go: chooser result → `/module` (already headed to `/module` → let it through directly, because the data the chooser page needs is now ready and it must not be bounced back to `/`, or once a default app is set there'd be no way in to change it; a full-screen page (`/fullscreen/*`, a wall-mounted board) also passes straight through — it is a static route outside the layout shell and does not depend on the app choice, and the chooser would only strand an unattended screen halfway); target is `/` → resolve to `auth.homePath` directly; anything else → re-return `to.fullPath` so the same URL resolves again now that its route exists. A thrown `enterInitial()` doesn't clear the session unconditionally: `isSessionDead(e)` (`router/bootFailure.ts`) triages first — only an HTTP 401, or a backend code that explicitly says the token is invalid or the account is disabled, counts as the session being genuinely gone, and gets the session cleared and the user sent to `/login` (with `redirect`); network jitter, a 5xx, or a rate limit stay put and land on a retryable `/boot-error` instead of conflating a network hiccup with a lost session.

Two spots here that don't return `to.fullPath` are worth noticing. When the target is `/`, returning `to.fullPath` would be a redirect to itself — and `/` has no static `redirect`, so Vue Router would flag it as an infinite redirect. The other trap is subtler: this rebuild logic can't short-circuit on `to.meta.public`, because a not-yet-registered dynamic route first hits the catch-all (404), which carries the `public` flag — honor `public` and let it through, and the user sees a bogus 404 instead of the correct, rebuilt page.

**`/` always lands on `auth.homePath`.** On normal navigation with the routes already in place, visiting `/` is likewise handed to the guard to compute the home page. This check can't be written as a static `redirect` on the `layout` route, for the same reason as before — `redirect` is evaluated at resolve time, before this guard runs, when the menu tree (and therefore `homePath`) isn't ready yet, so any landing spot computed there is guaranteed wrong.

After navigation is confirmed, `afterEach` records visited pages as tabs, skipping three categories: anything marked `meta.public`, the four fixed names `login`/`module`/`not-found`/`personal`, and any route not hung under `layout` (those don't belong to any app's workspace and shouldn't leave a trace in the tab bar):

```ts
router.afterEach(to => {
  if (to.meta.public) return
  if (['login', 'module', 'not-found', 'personal'].includes(to.name as string)) return
  if (!to.matched.some(r => r.name === 'layout')) return
  useTabsStore().addTab(to)
})
```

How the dynamic routes actually grow out of the menu tree — how `buildRoutesForModule` turns each menu node's `component` string into a real lazy-loaded component, and how `namedPage` gives it a stable identity so `keep-alive` recognizes it — is the subject of [Routing & Dynamic Menus](/frontend/routing); this page is only about the portal's "which app to enter" decision, and how the guard pulls it in on every navigation.
