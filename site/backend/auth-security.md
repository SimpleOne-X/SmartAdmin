# Authentication and Security

The kernel turns login, tokens, sessions, brute-force protection, and log redaction into default behavior — a zero-config host already comes with all of these, no extra wiring needed. Most policies come in two layers: a deployment-time Options default, plus a `SysConfig` that can override it at runtime (change configuration, not code — and no redeploy). This page covers the mechanism and config keys of each policy; for the pre-launch, line-by-line check of which ones you must change and which can stay at their defaults, see the [security baseline in the deployment guide](/guide/deployment/) — that page is the checklist, this one is the mechanism.

## JWT tokens

The access token is a short-lived JWT that's never persisted; the refresh token is long-lived, stored server-side only as a hash, and supports rotation and revocation (`Core/Security/ITokenProvider.cs`). Configured under `SmartAdmin:Jwt` (`AdminJwtOptions`):

| Config key | Default | Description |
| --- | --- | --- |
| `SmartAdmin:Jwt:SecretKey` | `null` | Signing key, at least 32 bytes |
| `SmartAdmin:Jwt:Issuer` | `SmartAdmin` | Issuer (`iss` claim) |
| `SmartAdmin:Jwt:ExpireMinutes` | `120` | Access token lifetime (minutes) |
| `SmartAdmin:Jwt:RefreshExpireMinutes` | `10080` | Refresh token lifetime (minutes, 7 days) |

**The signing key resolves through three paths** (`JwtKeyResolver.cs`):

- **`SecretKey` is configured** (required for production): used directly; if shorter than 32 bytes, startup throws immediately and refuses to start — a weak key can be brute-forced to forge a super-admin token.
- **Not configured + production environment**: throws immediately and refuses to start (fail-fast). If production silently fell back to an auto-generated development key, multiple replicas would each sign with their own key, causing random 401s, and a leaked key would let anyone forge arbitrary tokens.
- **Not configured + development environment**: generates a 64-byte cryptographically random key and persists it to `{ContentRoot}/data/dev-jwt.key`, so the signing/validation key survives restarts and previously issued tokens stay valid — while printing a prominent warning. This file sits alongside the default SQLite database in the data directory, and is naturally covered by `.gitignore`.

::: warning Production requires a configured signing key
This is a security baseline, not a suggestion. Production will simply refuse to start without an explicitly configured `SmartAdmin:Jwt:SecretKey`.
:::

**Validation parameters** (`SmartAdminSetup.cs`): `MapInboundClaims = false` (preserves the original `sub`/`sid`/`sadm`/`org` claim names); `ValidateAudience = false` (single monolithic backend, audience unused); `ValidateLifetime = true`; `ClockSkew` tightened to 30 seconds (default is 5 minutes; tightened to match short-lived tokens); `NameClaimType = unique_name` (`User.Identity.Name` = login account).

**Access / refresh token lifetimes are runtime-configurable.** At issuance, the effective minutes come from `ISecurityPolicyProvider.GetSessionTtlAsync()`, which reads `SysConfig` first and falls back to the JWT defaults:

| Runtime config key | Fallback default |
| --- | --- |
| `sys.security.session.accessMinutes` | `Jwt:ExpireMinutes` |
| `sys.security.session.refreshMinutes` | `Jwt:RefreshExpireMinutes` |

## Captcha

Login captchas are handled by `CaptchaService` plus three built-in generators with zero drawing-library dependencies. Configured under `SmartAdmin:Security:Captcha` (`AdminCaptchaOptions`):

| Config key | Default | Description |
| --- | --- | --- |
| `...:Captcha:Enabled` | `false` | Whether login captcha is enabled |
| `...:Captcha:Type` | `char` | `char` (character SVG) / `path` (stroke-outline glyph) / `math` (arithmetic) |

**Off by default**: a zero-config API has to accept logins straight away; account-level login lockout already blocks the primary brute-force vector, so captcha is an opt-in browser-side hardening measure — turn it on as needed for deployments with a frontend and for production.

Overridable at runtime via `SysConfig` (changes take effect immediately): `sys.security.captcha.enabled` (whether validation is enforced), `sys.security.captcha.type` (which type is issued). Falls back to the Options default when unset.

**One-time tickets** (`CaptchaService.cs`): stored in cache in plaintext (2-minute TTL), issued with a GUID v7 as the ticket Id; validated with an **atomic get-and-remove** —

```csharp
// Concurrent requests carrying the same captchaId — only one gets a non-null value back,
// preventing a single captcha from being amplified into N guesses
var stored = await cache.GetAndRemoveAsync<string>(CacheKeys.Captcha(captchaId!));
AdminException.ThrowIf(stored is null, ErrorCode.CaptchaExpired);   // 40002
AdminException.ThrowIf(!string.Equals(stored, code, StringComparison.OrdinalIgnoreCase),
    ErrorCode.CaptchaWrong);   // 40003
```

The ticket is invalidated regardless of whether the check succeeds or fails — the same captcha can never be replayed or guessed multiple times. Image/slider/behavioral captchas can be swapped in ahead of time by self-registering `ICaptchaProvider`.

## SMS verification (second factor & passwordless sign-in)

Two independent features, both **off by default**, toggleable at runtime from the config center's security tab (changes take effect immediately, no restart):

| Runtime config key | Options fallback | Default | Feature |
| --- | --- | --- | --- |
| `sys.security.mfa.enabled` | `...:SmsOtp:MfaEnabled` | `false` | SMS second factor: after the password passes, confirm an SMS code |
| `sys.security.smsLogin.enabled` | `...:SmsOtp:LoginEnabled` | `false` | Passwordless sign-in: phone number + SMS code |

**Second-factor flow**: after every password-side check passes (lockout → captcha → credentials → policy), `AuthService.CheckSmsSecondFactorAsync` creates a cache-backed challenge **bound to the verified user's id** (not just the phone), sends a code, and signals the frontend with `SmsCodeRequired` (40009) whose `args` carry `challengeId` / `phoneMask` / countdown parameters; `POST /api/v1/auth/login/sms` (plus `/resend`) completes the login. The `/login` request/response contract never changes shape — 40009 is a signal, not a failure, and it doesn't count toward login lockout.

::: warning The second factor only applies to users with a bound phone
A user **without a phone number signs in with password alone even when the switch is on**. This is deliberate, not a bug: flipping a global switch must never be able to lock anyone out of the system — the seeded super admin has no phone, and neither may existing users. To enforce MFA for an account, bind a phone number to it (profile page or user management).

Consumers who want the strict interpretation ("no phone = no login") override exactly one step:

```csharp
public sealed class StrictAuthService(
    IRepository<SysUser> users, IPasswordHasher hasher, ITokenProvider tokens,
    ISessionService sessions, ILogService logService, ILoginLockService loginLock,
    ICaptchaService captcha, ISecurityPolicyProvider policy, ISmsOtpService smsOtp)
    : AuthService(users, hasher, tokens, sessions, logService, loginLock, captcha, policy, smsOtp)
{
    protected override async Task CheckSmsSecondFactorAsync(SysUser user)
    {
        // Kernel default lets phone-less users through; strict mode rejects them instead
        if (await smsOtp.IsMfaEnabledAsync() && string.IsNullOrWhiteSpace(user.Phone))
            throw new AdminException(ErrorCode.AccountDisabled);
        await base.CheckSmsSecondFactorAsync(user);
    }
}
```
:::

**Passwordless sign-in**: `POST /api/v1/auth/sms/send` issues the code (honors the image-captcha toggle, so enabling captcha also guards this endpoint), `POST /api/v1/auth/sms/login` exchanges phone + code for tokens. **Anti-enumeration**: an unknown, duplicate, or disabled phone number gets the exact same success-shaped response — cooldown included — it just never sends anything; verification then fails with the generic `SmsCodeExpired`. A side effect worth knowing: **users sharing a duplicate phone number are silently excluded from passwordless sign-in** (resolution requires exactly one enabled match); the kernel doesn't unique-index `Phone` (existing data may hold duplicates), so keep phone numbers unique at entry time if you rely on this feature.

**Abuse controls** are server-side (`SmartAdmin:Security:SmsOtp`, `AdminSmsOtpOptions` — deployment-time, only the two switches are runtime keys):

| Option | Default | Description |
| --- | --- | --- |
| `CodeLength` | `6` | Code digits (cryptographically random) |
| `TtlSeconds` | `300` | Code (and MFA challenge) lifetime |
| `ResendSeconds` | `60` | Per-phone resend cooldown — shared across MFA and passwordless sends |
| `MaxAttempts` | `5` | Wrong tries before the code is invalidated |
| `DailySendLimitPerPhone` | `10` | Per-phone daily send cap (SMS-bombing / cost control) |

Codes are consumed with the same atomic get-and-remove as captcha tickets (single-use, replay-safe) and live **only in cache** — no schema change. Cooldown/daily counters have the same per-instance ceiling as login lockout under the default in-memory cache; the Redis package makes them global.

**The sending channel is an abstraction**: the kernel ships only `ISmsSender` with a `LoggingSmsSender` default that writes the code to the backend log (`[SMS:mfa] …` / `[SMS:login] …`) — good for development, useless in production. Register a real provider **before** `AddSmartAdmin()` (the `purpose` argument, `mfa` / `login`, maps to your vendor's template ids):

```csharp
builder.Services.AddSingleton<ISmsSender, AliyunSmsSender>();   // yours
builder.Services.AddSmartAdmin(builder.Configuration);          // TryAdd yields
```

Error codes: `SmsCodeRequired` 40009 (signal), `SmsCodeWrong` 40010 (carries `attemptsLeft`), `SmsCodeExpired` 40011 (missing/expired/consumed/exhausted — deliberately indistinguishable), `SmsLoginDisabled` 40012; send throttling reuses `TooManyRequests` 40008.

## Optional TOTP (authenticator)

Besides SMS second factor, the kernel ships **optional, off-by-default TOTP**: users bind a phone authenticator and enter a 6-digit code after the password. It is **independent of** `SmsOtp` — enable either, both, or neither. This is general admin hardening, **not** an MLPS Level-3 product pack.

The runtime master switch works like captcha: flip it in the config center security tab, no restart required in the common case:

| Key | Default | Meaning |
| --- | --- | --- |
| `sys.security.totp.enabled` | `false` | Feature gate; when off, bind / login challenge / recovery all refuse |
| `sys.security.totp.requireForSuperAdmin` | `false` | Whether super-admins must complete a second factor |
| `SmartAdmin:Security:Totp:Enabled` | `false` | Deploy floor: when `true`, capability stays on (UI cannot turn it off) |
| `SmartAdmin:Security:Totp:Issuer` | `SmartAdmin` | Issuer shown in the authenticator |
| `SmartAdmin:Security:Totp:ChallengeTtlSeconds` | `300` | Login challenge lifetime (seconds) |
| `SmartAdmin:Security:Totp:ReauthWindowMinutes` | `5` | High-risk write re-auth window (minutes) |
| `SmartAdmin:Security:Totp:RecoveryCodeCount` | `10` | Recovery codes issued per bind |

Per account, `ForceTotp` (user admin "force TOTP") requires that user to finish TOTP at login. It is orthogonal to the global keys and does nothing useful while the master switch is off.

**Self-service bind** is the product path. `POST /api/v1/auth/mfa/bind/start` takes account + current password and returns an `otpauth` URI plus a one-time seed; `complete` verifies the 6-digit code, stores the seed, and returns recovery codes **once** (server stores hashes only). The frontend entry is the built-in Account Security page (`/personal/security`) after login — the login page does **not** show a permanent bind link. Admins clear MFA with `POST /api/v1/sys/mfa/clear` (usually behind re-auth). Invite bind, InitGrant, and emergency grants are **not** product paths; the kernel has no endpoints for them.

Login envelope codes (same class as SMS 40009 — "one more step" or "must bind first"):

| Code | Meaning | Frontend |
| --- | --- | --- |
| `40018` | Bound; needs TOTP | TOTP step; `POST /api/v1/auth/login/totp` |
| `40019` | Wrong TOTP | Retry |
| `40020` | Forced but unbound | Modal → `/mfa/bind`, not a permanent login-page link |
| `40022` | Bad / reused recovery code | Another code or admin clear |

Recovery: `POST /api/v1/auth/mfa/recovery` (account + password + code). Success clears MFA and revokes sessions; re-bind is required. Each recovery code works once.

High-risk writes (user edits, clear MFA, some role/session ops) may use `[RequireReauth]`: re-check password or TOTP inside a short window (`POST /api/v1/auth/reauth`). The frontend package's request layer pops a shared re-auth modal on `40024` and replays the original request once it passes.

The kernel already carries `[RequireReauth]` on a batch of high-risk endpoints — user and role writes, config writes, force-logout, scheduled-job writes, menu writes, permanent recycle-bin delete, clearing MFA — and that list is itself manageable. `GET /api/v1/sys/mfa/high-sensitivity` lists the kernel's default set (`HighSensitivityPermissions.Default`, read-only, not removable through the admin page) plus whatever a consumer has appended; `POST` on the same path appends a custom permission code, `DELETE /api/v1/sys/mfa/high-sensitivity/{id}` removes a custom entry (the default set can never be deleted). All three endpoints carry `[RolePermission]`, and the writes also carry `[RequireReauth]`. Appending a route to this list only records it in the "high-sensitivity" catalog — making a consumer's own write endpoint actually require re-authentication still means putting `[RequireReauth]` on that endpoint explicitly.

TOTP seeds are envelope-encrypted via `ISecretProtector` — not whole-database field encryption. Full key table: `docs/agents/security-optional-config.md` in the repo.

## Optional cookie session and CSRF

Default session shape is unchanged: login JSON returns access + refresh; clients may keep refresh in localStorage and refresh via the body. When **`SmartAdmin:Security:Session:CookieMode`** is `true` (default `false`, deploy-time, restart required):

- refresh is **HttpOnly** cookie only (`smart_rt`); body refresh is cleared
- a readable CSRF cookie (`smart_csrf`) is issued; mutating requests must send header `X-Smart-CSRF` (constant-time match)
- login payload includes `sessionMode: "cookie"` and `csrfRequired: true`
- the frontend package's request layer already uses `credentials: 'include'` and attaches CSRF; access stays in memory with silent cookie refresh

Business APIs still use `Authorization: Bearer` plus per-request `sid` liveness checks — cookies do **not** replace the access token. Cross-subdomain setups need `Session:CookieDomain` (often with `SameSite=None` + HTTPS). Same-origin Vite proxy local work usually needs no Domain.

Optional idle / absolute caps (default `0` = off): `Session:IdleMinutesNormal`, `IdleMinutesMfa`, `AbsoluteHours`. Independent of CookieMode.

## Email channel

The kernel ships the same kind of abstraction for email, `IEmailSender`, mirroring `ISmsSender` — and **no built-in feature actually uses it yet**. It's a channel wired up ahead of time, so email verification codes or notification emails can plug straight into it later without a fresh replaceability design pass.

Configuration lives under `SmartAdmin:Email` (`AdminEmailOptions`); a single field decides which implementation is active:

| `Host` | Implementation selected | Behavior |
| --- | --- | --- |
| Empty (default) | `LoggingEmailSender` | Writes recipient/subject to the backend log instead of sending — visible in development, useless in production |
| Non-empty | `SmtpEmailSender` | Talks SMTP directly via the BCL's `System.Net.Mail` (STARTTLS, default port 587) |

`SmtpEmailSender`'s ceiling is whatever the BCL's own `SmtpClient` can do: plain SMTP works, but it can't do OAuth2 SMTP or a cloud vendor's own API. For that, implement your own `IEmailSender` — MailKit is a common choice — and register it before `AddSmartAdmin()` to take over. This replacement path is already covered by the regression tests, so a future kernel upgrade won't quietly break it:

```csharp
builder.Services.AddSingleton<IEmailSender, MailKitEmailSender>();  // yours, TryAdd yields
builder.Services.AddSmartAdmin(builder.Configuration);
```

## Login lockout (brute-force protection)

`LoginLockService` is invoked at the **very front of login**, before credential validation — during a lockout, even the correct password is rejected. Configured under `SmartAdmin:Security:LoginLock` (`AdminLoginLockOptions`):

| Config key | Runtime key | Default | Description |
| --- | --- | --- | --- |
| `...:LoginLock:MaxFailCount` | `sys.security.loginLock.maxFailCount` | `5` | Consecutive password failures before lockout; `<=0` disables it |
| `...:LoginLock:LockMinutes` | `sys.security.loginLock.lockMinutes` | `10` | Lockout duration, also the sliding expiry window for the failure count |

The failure count is stored in cache, incremented atomically with its TTL refreshed each time: continued failures push the window out, while stopping lets the count expire after `LockMinutes` and unlock automatically. **Only "wrong password" counts toward lockout** — wrong captcha, already-locked, disabled account, etc. don't increment it, avoiding both an indefinitely extended lockout window and accidental collateral lockout (`AuthService.OnLoginFailedAsync`).

::: tip Account normalization must match the database
The lockout counter's key is normalized first (trim whitespace + lowercase); its equivalence class must be at least as coarse as the database's matching equivalence class. Otherwise case/trailing-space variants (which MySQL's `utf8mb4_0900_ai_ci` / PAD SPACE collation would treat as the same row) split into separate counters, letting an attacker bypass lockout and guess indefinitely.

For example, `Admin`, `ADMIN`, and `admin ` (trailing space) all resolve to the same user row under that collation. The normalization function collapses all three into the same cache key first, so three failed attempts stack onto one counter instead of splitting into three — which would otherwise hand an attacker double the guesses for free.
:::

**Blocking account enumeration** (`AuthService.ValidateUserAsync`): "account doesn't exist" and "wrong password" both throw `ErrorCode.PasswordWrong` (indistinguishable in the response), and when the account doesn't exist, an equivalent-cost dummy hash still runs — making response timing indistinguishable too, closing both side channels at once.

## Per-user grants

Roles carry the routine permissions; per-user grants are for exceptions: you mark a single menu node for a single user as "Allow" or "Deny", optionally with an expiry time.
The effective menu is computed as (menus granted by enabled roles ∪ active Allows) − active Denies and their descendants, keeping only enabled nodes.

"Active" means the grant has no expiry time, or its expiry time hasn't passed yet.
Deny wins, and it reaches descendants:
deny a page and the buttons under it go with it, so you never end up with the page gone but its button endpoints still callable.
The rule is implemented exactly once, in `UserMenuGrantRules`, and permission aggregation, the portal menu and the grant UI all call it.
Super admins still get everything, take no part in the computation, and cannot be given grants.

A Deny lands on the node, not on the permission code.
Several buttons sharing one endpoint is common, so denying one of them leaves the endpoint callable as long as another effective node still carries that code.
The grant UI lists such leaked endpoints together with the nodes that carry them, so an admin doesn't assume an endpoint is closed when it isn't.

Per-user grants cover features, never data.
Data scope still comes only from roles, so a user with no roles and only per-user grants can open pages and call endpoints but sees nothing beyond the rows they created themselves.

Who can grant splits into two tiers:

- A super admin can grant any menu, and an Allow may be open-ended.
- An ordinary admin is a non-super-admin whose effective permission codes include `PUT:/api/v1/sys/user/menu`. Tick the "User - Grant Menus" button on their role and they have it. Four constraints bind them:
  - The target user must be inside their data scope.
  - The target must not be an ordinary admin too.
    Permissions between admins are adjusted only by a super admin, so two admins cannot grant each other anything.
  - Every entry in the change set must sit in a menu whose module is delegatable.
    Adds, edits and removals all count, and so do Allows and Denies; the admin does not need to hold those menus themselves.
  - An Allow must carry an expiry time, and its date can be no later than today plus the number of days `DelegatedGrantMaxDays` gives (below).
    A Deny only tightens permissions, so it needs no expiry.

Nobody can grant to themselves (`CannotOperateSelf`, 42029) or to a super admin (`SuperAdminProtected`, 42007), super admins included.

"Delegatable" is a switch on the module (`SysModule.IsDelegatable`), set by a super admin on the module-management page.
It is a different switch from the role's own "delegatable" flag (`SysRole.IsDelegatable`), which decides whether an ordinary admin may hand that role to others.
Only `true` counts; `null` and `false` both mean not delegatable.
Existing modules are `null` after an upgrade, so until a super admin turns a module's switch on, an ordinary admin can grant nothing inside it.
The built-in "System" module is permanently non-delegatable: its switch is greyed out and any submitted value is ignored.
In a fresh database the sample module "Business Center" is delegatable by default.
A menu's module is found by walking up `ParentId` to the root and taking the root's `ModuleId`;
a menu that sits under no module counts as non-delegatable.

Inside a delegatable module the granter is no longer restricted entry by entry:
an ordinary admin can turn a Deny set by a super admin into an Allow within the time limit, or delete a permanent Allow set by a super admin.
Turning a module's "delegatable" switch off is not a revocation.
Existing grants keep working; ordinary admins just can no longer change them.
To lock or pull back a grant, a super admin turns the switch off first and then handles that record personally.

| Config key | Default | Description |
| --- | --- | --- |
| `SmartAdmin:Security:DelegatedGrantMaxDays` | `90` | When an ordinary admin grants an Allow, the latest expiry date is this many days after today, judged by date |

"Today" is the server's local date.
The date-picker limit and the pre-save check in the grant dialog use the day the server computed (`delegatedMaxDate` in the `menus/effective` response), never the browser's date.
The range is 0 to 3650; a value outside it makes startup fail, and a negative value is never treated as "unlimited".
`0` means unlimited: no expiry time is required and there is no upper bound.
Super admins are not subject to this setting.

Role grants have a rule of their own: system menus can be granted only to built-in roles.
A system menu is a directory the kernel seed plants under the built-in "System" app (Id ≤ 999, such as Organization or System Operations) together with everything below it, including pages you hang under those directories.
A built-in role is one whose Id is fixed in the kernel seed, 1–999, such as "System Administrator".
Roles created in the UI, and roles your own seed plants (Id ≥ 1000), are not built in.
When a super admin grants a non-built-in role on the role-grant page, the kernel's own directories aren't shown, and the endpoint rejects them with 41009 (`SystemMenuNotAssignable`).
The guard only stops calls that carry a login context; seeds, startup tasks and other code with no login context are unrestricted.

A top-level directory you create yourself under the "System" app (Id ≥ 1000) is not a system menu, and a new role can be granted it as usual.
It still belongs to the "System" app, though, which is permanently non-delegatable, so an ordinary admin cannot grant it to a user.
To delegate it, re-parent the directory under your own app first.

If someone later moves a page under a kernel directory, that page's grant already held by a new role is not reclaimed automatically.
A super admin opens that role's grant dialog and saves once: the page no longer shows in the dialog, and a save submits only the grants that are visible, so it is taken back.
This restriction covers role grants only; a super admin can still grant a system menu to an individual user.

::: warning Upgrading deletes System-menu grants held by new roles
On the first startup after upgrading from a database whose seed version is below 7, once the database is ready the kernel physically deletes the `sys_role_menu` rows of "non-built-in role × system menu".
Directories you created yourself under the "System" app (Id ≥ 1000) are not touched.
The deletion is irreversible, so back up that table before upgrading.
Every row writes a Warning log line (role name, code, menu title, menu Id), and the affected users' permission-code cache is invalidated and the portal-menu cache is recomputed as a whole.
An empty database, an ordinary restart, or seeding turned off (which skips the version gate) never runs it.

- Projects that rely on a new role to reach System pages lose that access for those users after the upgrade;
  switch them to the built-in "System Administrator" role.
- Roles your own seed plants (Id ≥ 1000) count as non-built-in too.
  On the upgrade startup the cleanup deletes the System menus already granted to them;
  but a seed has no login context and isn't subject to the guard, so every later restart plants the missing rows again.
  A project that grants System menus this way has to use a built-in role instead, or change its own seed.
- The cleanup runs once and is never retried.
  The version row is written as 7 before the cleanup runs, so if the cleanup fails the startup aborts with an exception.
  A failed lookup or a failed deletion leaves an Error log line saying no grant was deleted (a failed deletion also says the transaction was rolled back).
  When a deployment that skips table creation lacks `sys_user_menu`, the missing-table guard aborts the startup, but the cleanup is registered ahead of it and has already finished.
  To recover, a super admin re-saves the grants of the roles involved on the role-grant page:
  the dialog doesn't show system menus and a save submits only the grants that are visible, so saving takes them back.
- If only the cache invalidation fails after the grants are deleted, it writes an Error log line and startup carries on.
  Clear "permission cache" and "portal menu cache" separately on the cache-management page.
- During a rolling upgrade, replicas still running the old version can keep granting System menus to new roles.
  Those grants are not cleaned up, and once the upgrade finishes a super admin takes them back by re-saving in the same way.
:::

When a grant is rejected, match the error code to find the cause:

| Code | Name | Appears when |
| --- | --- | --- |
| `41005` | `UserOutOfDataScope` | The target user is outside the granter's data scope; reading that user's grants and effective permissions is rejected the same way |
| `41006` | `MenuNotGrantable` | A menu in the change set belongs to a module that isn't delegatable |
| `41007` | `TargetIsDelegatedAdmin` | An ordinary admin grants to another ordinary admin; also when the target was later put into an admin role and the original granter tries to edit that user's records |
| `41008` | `DelegatedGrantExpiryInvalid` | An ordinary admin's Allow has no expiry time, or its expiry date is later than today plus the maximum days |
| `41009` | `SystemMenuNotAssignable` | A system menu is granted to a non-built-in role |
| `42015` | `MenuNotFound` | A menu being added or edited doesn't exist |
| `42031` | `UserMenuGrantInvalid` | The change set itself is invalid: the same menu appears more than once (adds, edits and removals counted together, so a menu in both an edit and a removal counts), the effect is neither Allow nor Deny, the expiry time is not later than now (Allow and Deny alike), or the remark exceeds 200 characters |

After a successful save the kernel publishes `UserMenuGrantsChangedEvent` through `IEventBus`, carrying the target user, the operator, and the details of what was added, changed and removed.
The kernel never subscribes to it itself; a consumer that wants external auditing or alerting just subscribes.
See [Event bus](./event-bus.md) for how.

A grant change takes effect on the API immediately:
permission codes aren't in the JWT but are read from cache on every request, and saving a grant invalidates that user's cache.
Expiry needs no background job, because the cache TTL is capped at the nearest expiry moment and the next request after it recomputes.
The limitation is that a page the target user already has open won't refresh its sidebar and buttons live; they need to reload or sign in again.
Role changes behave the same way, since the kernel has no push channel for them.

The kernel wires per-user grants only into the default `RbacPermissionProvider` and `MenuService`.
If a project replaces `IPermissionProvider` or `IMenuService` wholesale, its implementation knows nothing about `sys_user_menu`, and per-user grants have no effect.
To support them, read that table yourself and call `UserMenuGrantRules` to compute the effective menu.

## Sessions and force-logout

Sessions are managed by `SessionService`: the copy in the database is the source of truth, and the cached copy exists only to spare the hot path one query. Refresh tokens are stored as a SHA-256 hash and nothing else, and every timestamp is UTC. At login, a GUID v7 is generated as the `sessionId`, written into the token's `sid` claim, and used as the stable anchor for listing online users and for force-logout.

**Force-logout takes effect immediately.** The authorization pipeline checks whether the session behind `sid` is still active on every request (see [Request Pipeline](./request-pipeline.md), step ②). When an admin kicks a user from "Online Users":

```csharp
public virtual async Task RevokeAsync(string sessionId)
{
    // mark the session row's RevokedAt
    // mark the refresh token Status = Revoked
    await cache.RemoveAsync(CacheKeys.Session(sessionId));   // cache removed → next check queries DB, finds it revoked → 401
}
```

The kicked user's access token, even if not yet expired, gets a 401 on the next request. Disabling/deleting a user goes through `RevokeAllForUserAsync`, taking down all of their sessions.

**Concurrency policy** (`SmartAdmin:Security:Session`, `AdminSessionOptions`):

| Config key | Default | Description |
| --- | --- | --- |
| `...:Session:Mode` | `Multi` | `Multi` (multiple devices coexist) / `Single` (new login kicks the old one) |
| `...:Session:MaxConcurrent` | `0` | Max concurrent sessions; when `>0`, exceeding it revokes the oldest login first; `0` means unlimited |
| `...:Session:CookieMode` | `false` | When `true`, refresh is HttpOnly cookie + CSRF only (see section above) |
| `...:Session:CookieDomain` | `null` | Cookie Domain; empty = current host |
| `...:Session:IdleMinutesNormal` / `IdleMinutesMfa` | `0` | Idle timeout (minutes); `0` = off |
| `...:Session:AbsoluteHours` | `0` | Absolute max hours; `0` = follow refresh only |

Quota trimming uses "**insert first, then converge**": the new session is inserted into the DB before trimming runs, so two concurrent logins both see each other's row and both compute the same "keep only the newest N" answer — convergence happens naturally, without relying on an in-process lock, which is what lets it work correctly across multiple replicas (a single-process lock wouldn't kick an old session on a different replica).

**Refresh-token reuse detection** (`SessionService.RefreshAsync`): a rotated token reappearing counts as a replay — the entire session is revoked (attacker and legitimate user are both logged out; safety takes priority). Rotation uses a conditional update (only sets `Used` if still `Active`), which doubles as concurrency protection.

## Password policy

`SecurityPolicyProvider.GetPasswordPolicyAsync()` reads each value from `SysConfig` first, falling back to defaults:

| Runtime config key | Default |
| --- | --- |
| `sys.security.password.minLength` | `8` |
| `sys.security.password.requireUpper` | `true` |
| `sys.security.password.requireLower` | `true` |
| `sys.security.password.requireDigit` | `true` |
| `sys.security.password.requireSpecial` | `false` |

If unmet, throws `ErrorCode.PasswordTooWeak`, with `args` carrying the specific requirements for the frontend to display. Passwords are hashed with PBKDF2 (`Pbkdf2PasswordHasher`).

**Password aging (off by default).** Beyond complexity, there's a runtime-configurable expiry policy: `sys.security.password.expireDays` (seeded to `0` = never expires; only enabled when >0). At login step 4, `AuthService.CheckPasswordExpiryAsync` takes `SysUser.LastPasswordChangeTime` plus the valid-days count and compares it against the current time — **expiry doesn't block login**; it only sets that user's `MustChangePassword` to true in the DB and returns it in the login response, so the frontend forces a redirect to the change-password page (the same signal as an admin password reset). A successful self-service password change refreshes `LastPasswordChangeTime`, clears the flag, and restarts the expiry window from zero.

**Forced change at first login (off by default).** The "Change at first sign-in" switch in Config Center → Security (`sys.security.password.forceChangeOnFirstLogin`, seeded to `false`) decides whether accounts created or imported by an admin get `MustChangePassword` set. It is read once at creation time, so flipping it does not touch existing accounts. Admin password resets and password expiry ignore it and still force a change.

`LastPasswordChangeTime` can be null, for example on accounts written straight into the database rather than created or re-passworded through the kernel. Before actually judging expiry, a null anchor is backfilled with the current time first, so the window starts from that login — otherwise, on the day the policy is switched on, a batch of accounts with no anchor would all be judged expired at once and collectively stuck on the change-password page. Consumer code that implements `ISecurityPolicyProvider` itself isn't affected: `GetPasswordExpireDaysAsync` ships with a default interface implementation returning 0, so an implementation without that member still compiles and behaves as if the policy is off.

**Can a password be changed back to one used before?** By default, yes. To forbid it, set `sys.security.password.historyCount` to N in the config center's security tab, and the system remembers the last N passwords (seeded to `0`, meaning none are kept). On a change, the new password is checked against each of them, and a match is rejected with `ErrorCode.PasswordReused` (42025). The *current* password is checked separately — the history table is empty the moment the policy is switched on, so history alone would not stop the loophole of "change it to the one already in use". `IPasswordHistoryService` stores hashes only, reusing the same `IPasswordHasher` as `SysUser.Password`, and verification runs `Verify(plaintext, hash)` entry by entry. After each write, that user's history is trimmed to the newest N rows and the rest hard-deleted, so the table doesn't grow without bound.

There are three write sites: self-service change (`PersonalService`), admin user creation, and admin password reset (the latter two in `UserService`). The last two only record, they don't check — an initial password chosen by an admin is not subject to the no-reuse rule. All three share one shape: `IPasswordHistoryService` is declared as an **optional constructor parameter defaulting to `null`**:

```csharp
public class PersonalService(
    /* ...existing dependencies... */
    IPasswordHistoryService? passwordHistory = null) : IPersonalService
{
    // Policy off, or the consumer never injected one: ?. short-circuits to a no-op — no throw, no lookup
    await (passwordHistory?.EnsureNotReusedAsync(userId, input.NewPassword) ?? Task.CompletedTask);
}
```

That shape exists to keep replaceability cheap. Because the parameter has a default, a consumer subclass of `PersonalService` / `UserService` does not have to touch its primary constructor, old call sites still compile, and the behavior is the same as "history policy off". Make it a required parameter instead and every optional security policy the kernel adds later would force a constructor-signature change on every downstream subclass — a bill nobody wants to pay.

**Default initial password** (`SmartAdmin:Security:DefaultInitialPassword`): defaults to `null` → when creating a user or resetting a password, a cryptographically random strong password is generated per account, closing off the known weakness of "a fixed default password shipped in a public NuGet package." A password reset returns the random password to the admin to relay on the spot.

::: tip Super-admin password on first startup
If `SmartAdmin:Seed:AdminPassword` is configured, that value is used; if not (the default), a random password is generated and **printed prominently to the startup log once** — only during the startup run that actually creates the account; subsequent startups don't print it again (printing an already-invalidated random password would only mislead).
:::

## API key access (machine clients)

Devices, handheld scanners and third-party systems calling the backend have no "login session"; they hold a pre-shared key. The kernel ships a second authentication scheme for them, named `ApiKey`, living alongside JWT without touching it: the default scheme is still Bearer, and only endpoints explicitly marked `[ApiKey]` reach it.

```csharp
[ApiController]
[Route("api/v1/device")]
public class DeviceController : ControllerBase
{
    [HttpPost("heartbeat")]
    [ApiKey]                      // X-Api-Key only, no user JWT; missing / wrong key → 401 + 40027
    public Result<bool> Heartbeat(HeartbeatInput input) => ...;

    [HttpGet("orders")]
    [ApiKey]
    [RolePermission]              // only passes if the key is bound to a user: that user's roles and data scope apply
    public Result<PagedList<Order>> Orders([FromQuery] OrderQuery q) => ...;

    [HttpGet("status")]
    [ApiKey]
    [SkipEnvelope]                // the success response is the dto itself, no envelope
    public DeviceStatus Status() => ...;
}
```

The key is read from a request header, `X-Api-Key` by default, configured under `SmartAdmin:Security:ApiKey`:

```json
{
  "SmartAdmin": {
    "Security": {
      "ApiKey": {
        "HeaderName": "X-Api-Key",
        "Keys": [
          { "Name": "pda", "Key": "<a random string of 32+ bytes, injected via environment>" },
          { "Name": "erp", "Key": "<...>", "UserId": 68860291608576 }
        ]
      }
    }
  }
}
```

`Name` goes into the operation log and the rate-limit partition, and becomes the principal's `unique_name`; `Key` is the secret, compared in constant time. `UserId` is optional: when bound, a key calling an endpoint that also carries `[RolePermission]` is judged by that user's permission codes, data scope and operation log — no second authorization model. Unbound, it can only call endpoints carrying nothing but `[ApiKey]`; `[RolePermission]` endpoints answer 403, and data scope falls back to the empty (fail-closed) scope. A machine principal has no session, so `[RolePermission]` and `[ActiveSession]` skip the session check for it. To accept both JWT and key on one endpoint, write `[Authorize(AuthenticationSchemes = "Bearer,ApiKey")]`.

The key source is replaceable: the default `ConfigApiKeyValidator` reads the section above; to read from a database table or a key-management service, register your own `IApiKeyValidator` before `AddSmartAdmin()` and it takes over (TryAdd). Return `ApiKeyPrincipal(Name, UserId)` on success, `null` for an unknown key.

`[SkipEnvelope]` is independent: put it on endpoints where a third party demands a fixed response shape and the success response goes out as is, with the OpenAPI contract skipping the envelope wrapper too. It affects the success path only — business exceptions still use the standard envelope (200 + code), and 401 / 403 / 429 are unchanged. In the contract, `[ApiKey]` endpoints carry `x-auth: apikey`.

## Request rate limiting

Rate-limited by **client IP** using a fixed window. Requests carrying the API key header get their own bucket, partitioned by a hash of the key and counted against `KeyPermitPerWindow`, independent of the user-side IP bucket: a fleet of devices behind one gateway doesn't share one IP allowance, and one key running hot doesn't hurt the people on the same IP. mounted via a built-in `IStartupFilter` that calls `UseRateLimiter` — no manual middleware wiring needed. Configured under `SmartAdmin:Security:RateLimit` (`AdminRateLimitOptions`):

| Config key | Runtime key | Default | Description |
| --- | --- | --- | --- |
| `...:RateLimit:Enabled` | `sys.security.rateLimit.enabled` | `true` | Deployment-time hard master switch; when `false`, no rate limiting regardless of DB config |
| `...:RateLimit:WindowSeconds` | `sys.security.rateLimit.windowSeconds` | `60` | Window length (seconds) |
| `...:RateLimit:PermitPerWindow` | `sys.security.rateLimit.permitPerWindow` | `300` | Global: requests per window per IP (blocks flooding) |
| `...:RateLimit:AuthPermitPerWindow` | `sys.security.rateLimit.authPermitPerWindow` | `20` | A stricter tier for auth endpoints (`/api/v1/auth/*`), blocking online brute-forcing |
| `...:RateLimit:KeyPermitPerWindow` | (none, Options only) | `600` | Machine clients: requests per window per API key, counted apart from the IP bucket; `<=0` means unlimited |

`Enabled` is a deployment-time hard master switch; when `true`, the actual on/off state and thresholds are runtime-tunable via `SysConfig`.

::: warning Behind a reverse proxy, the IP seen is the proxy's
When deploying behind a proper gateway, wire up the `ForwardedHeaders` middleware to parse `X-Forwarded-For` first — otherwise all clients behind the same proxy share a single rate-limit partition.
:::

## Demo mode (read-only showcase)

To stand the system up as a public demo site that anyone can come in and click around in but nobody can change data in, turn on `SmartAdmin:DemoMode` (default `false`):

```jsonc
// appsettings.json — or the environment variable SmartAdmin__DemoMode=true
{ "SmartAdmin": { "DemoMode": true } }
```

Only then does the kernel register the global authorization filter `DemoModeFilter`, which allows requests by HTTP method: GET/HEAD/OPTIONS read as usual; `/api/v1/auth/*` (login, logout, refresh) is allowed through too, or you couldn't even log in; every other POST/PUT/PATCH/DELETE returns HTTP 403 with envelope code `41002` (`DemoModeReadOnly`), and the frontend pops a read-only notice keyed by that code.

What it blocks is the write action itself, independent of role permissions — the decision falls in the authorization stage and looks only at the request method, so even a super admin can't change anything once inside. To let a specific write endpoint through (say the demo site's own feedback form), don't reach for this master switch; handle it separately on the business side.

## Log redaction

Operation logs record all write operations by default (read operations and anonymous endpoints excluded). Input parameters are redacted before being written to the DB, preventing plaintext passwords from ending up in logs (`SensitiveDataMasker.cs`, invoked by `OperationLogFilter`):

```csharp
var paramJson = SensitiveDataMasker.Mask(context.ActionArguments, logging.OpLog.ParamMaxChars);
```

**Redaction is by field name, not by value**: any property whose name contains one of ten keywords — `password`, `pwd`, `secret`, `token`, `credential`, `header`, `authorization`, `apikey`, `api_key`, `cookie` (case-insensitive, substring match — `newPassword`, `access_token` both match) — has its value replaced with `***`, recursively across nested objects and arrays. The keyword list lives in `Core/Security/SensitiveKeys.cs` and is shared with the [SQL log](/backend/sql-log)'s parameter masking, so the two outlets never drift apart. Serialization failures (including non-serializable inputs like `IFormFile`) don't block the request — a placeholder string `<unserializable>` is logged instead. Masked JSON longer than `ParamMaxChars` (default 8192, see [Ops Endpoints](/backend/ops)) is truncated too: only the opening survives, annotated with the original length, and the result is still valid JSON.

Login logs (`AuthService`) record the raw input account (even when the account doesn't exist) plus the specific failure code, to support investigating brute-force attempts or account probing; IP/UA are filled in by the logging service from the current request. **Passwords are never logged, under any circumstance.**
