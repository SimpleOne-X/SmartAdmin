# External Login (SSO)

The first time someone scans in through WeCom, they get rejected with `OAuthAccountNotBound`. That's the default policy, not a misconfiguration. The kernel's standing position is that accounts are provisioned by an admin, never self-registered — and external identity follows the same rule. To let SSO provision accounts on its own, the switch has to be turned on explicitly, per provider.

## Five providers, two packaging models

| Provider | Where it lives | How it connects |
| --- | --- | --- |
| `oidc` | Built into the kernel (AspNetCore layer) | Standard OIDC — works with Keycloak, Entra, Authing, Auth0, and anything else that speaks the protocol |
| `wecom` | Optional package `SmartAdmin.Auth.WeCom` | QR login in a desktop browser, web authorization inside the WeCom client, picked automatically by User-Agent |
| `dingtalk` | Optional package `SmartAdmin.Auth.DingTalk` | DingTalk desktop QR / web authorization |
| `github` | Optional package `SmartAdmin.Auth.GitHub` | GitHub OAuth App, scope limited to `read:user` |
| `wechat` | Optional package `SmartAdmin.Auth.WeChat` | WeChat Open Platform website-app QR login (`snsapi_login`), identity resolved from `unionid` alone |

Built-in OIDC adds zero new dependencies: discovery document, JWKS, and `id_token` signature verification all ride on the `Microsoft.IdentityModel.*` stack that JwtBearer already pulls in. Each vendor package depends only on `Core` plus Microsoft.\*, talking to the vendor's API over a bare `HttpClient`. That's what lets them ship on their own release cadence without dragging a vendor SDK into the kernel.

Register the four optional packages before `AddSmartAdmin()`. They only register the vendor's type descriptor (`IExternalAuthProviderType`); they read no configuration and throw nothing. The built-in OIDC type descriptor is registered by `AddSmartAdmin()` itself, so there's nothing extra to call.

```csharp
builder.Services.AddSmartAdminWeComAuth();
builder.Services.AddSmartAdminDingTalkAuth();
builder.Services.AddSmartAdminGitHubAuth();
builder.Services.AddSmartAdminWeChatAuth();
builder.Services.AddSmartAdmin(builder.Configuration);
```

Connection settings for third-party login have a single source, the database: the vendor and OIDC connection nodes under `SmartAdmin:ExternalAuth` in appsettings are not read, and startup logs one warning if they are present. The overloads that take an options object such as `WeComAuthOptions` register a provider explicitly in code without consulting the database, and win over a database configuration with the same `Code`.

A package's row on the Login methods page is usable only once the package is installed; a provider whose package isn't installed shows "Not installed". Login, callback and binding all fetch providers from one registry, `IExternalAuthProviderRegistry`, whose result is the union of two sets: instances built on demand from the connections saved in the database, and any `IExternalAuthProvider` the consumer implemented and registered itself. When both sides have the same `Code`, the one registered in code wins.

## Where configuration lives

### Connection details and secrets

Connection details and secrets are entered under System Config → Login methods: click Settings on a provider, fill it in, save, and it takes effect immediately. Each row shows one of three states: "Not installed" (the server doesn't have the package), "Not configured" (installed, but the database has no complete configuration yet; the switch is greyed out), and "Configured" (the switch is usable). Gitee and QQ have no backend package and don't appear in the list. Connection details and secrets come from the database only; there is no equivalent in `appsettings`. The provider connection nodes under `SmartAdmin:ExternalAuth` (`Oidc`, `WeCom`, `DingTalk`, `GitHub`, `WeChat`) are not read, and if any are still present at startup, a warning is logged telling you to fill them in on the page.

The fields each type asks for, with secret fields encrypted:

| Type | Plain fields | Secret fields (encrypted) |
| --- | --- | --- |
| `wecom` | CorpId, AgentId | CorpSecret |
| `dingtalk` | AppKey | AppSecret |
| `github` | ClientId | ClientSecret |
| `wechat` | AppId | AppSecret |
| `oidc` | Authority, ClientId, Scopes, UsePkce | ClientSecret |

Plain fields are echoed back on the page so you can check which app you filled in. Secret fields are encrypted as one unit with `ISecretProtector` (AES-GCM) and stored in the `sys_external_auth_provider` table. The page and the API return only "configured" plus the last four characters, and no response or operation log ever contains a secret in plaintext. A secret shorter than 8 characters gets no last-four hint at all, so a short secret isn't exposed in full. Leaving a secret field empty means "keep the saved value"; on the first save it's required.

This table is separate from `sys_config` because values in `sys_config` show up in the config list, the Advanced tab, exports and operation logs, and a secret stored there would be in plaintext.

Rules for `Code`:

- An official vendor type's `Code` is fixed to the type name (`wecom`, `dingtalk`, `github`, `wechat`), and each can be configured once.
- `oidc` can be configured multiple times; click "Add OIDC" on the page. The admin picks the `Code`, in the form `^[a-z][a-z0-9-]{1,31}$`, and it can't equal any of the four type names above.
- `Code` and type can't be changed after saving.

Rules that apply when saving:

- **A data-protection master key must be configured.** The master key is `SmartAdmin:Security:DataProtection:Key`. When it's a process-local ephemeral key, the page refuses to save, because the ephemeral key is lost on restart and the stored secrets could never be decrypted again.
- **Changing an OIDC Authority requires re-entering the Client Secret.** Otherwise someone with the permission could point Authority at an address they control, and the server would carry the saved secret there to exchange tokens at login. Authority also goes through the `HttpFence` address check before saving and before testing; by default that blocks only loopback and link-local addresses, so an intranet Keycloak is unaffected.
- **Saving and clearing a configuration require a recent identity re-verification.** Clearing deletes only that configuration; it doesn't delete the external accounts users have already bound.

The master key still lives outside the database. This encryption protects against "someone obtained only the database or a backup", not "someone obtained the server". With multiple instances, all of them must use the same master key and share a cache (Redis): the configuration rows go through a whole-table read-through cache, and without a shared cache, a change made on one instance is invisible to the others.

::: warning Whoever can edit this decides who can log in
The button permissions for these operations hang under the System Config menu. Reading the catalog is folded into "Config - Query"; save, clear and test connection are one button each. Grant them carefully.

The risk is highest when "Link by account" is also on: someone with the permission adds an OIDC IdP they control, and a person whose account name matches a local account signs in as that local account. On a system with linking on, grant this permission only to whoever manages the IdP.
:::

### Test connection

The Test connection button at the bottom left of the settings panel checks with the values currently in the form, without requiring a save; leave a secret empty and the saved one is used. The result is a list of check items, each `ok`, `fail` or `skipped`, shown one by one in the panel rather than as a blanket "success". It reports as far as it can verify, and says plainly what it can't:

| Type | Checks | What it can't verify |
| --- | --- | --- |
| `wecom` | Exchange CorpId + CorpSecret for an access_token, then look up the app by AgentId | Nothing |
| `dingtalk` | Exchange AppKey + AppSecret for an app access_token | Nothing |
| `github` | Request the token endpoint with a fake authorization code: "invalid code" means the credentials are valid, "bad client credentials" means they aren't | Doesn't start a real login |
| `wechat` | Request the token endpoint with a fake authorization code to confirm WeChat recognizes the AppId | The AppSecret is marked "unverified" |
| `oidc` | Fetch the discovery document, confirm `issuer` matches and that authorization and token endpoints exist | The Client Secret is marked "unverified" |

An OIDC Client Secret can't be verified because standard OIDC has no general way to check a secret without a user. A WeChat AppSecret can't be verified because WeChat checks the AppId and the authorization code first, and never reaches the secret check when the code is invalid. When a vendor offers no usable way to verify something, that check is marked `skipped` rather than forced into a pass.

Test requests are sent by the server, carry no user's login state, write nothing to the database, and are subject only to the global rate limit. Apart from OIDC, which goes through the address fence, they target fixed vendor domains. Test connection is a trigger for outbound requests, so it's only available to people with the permission. It saves nothing, so it doesn't require re-verification.

### Callback base URL and frontend result page

`CallbackBaseUrl` and `FrontendResultPath` live in `appsettings`, the same pattern as Database, Jwt, and Email. They aren't secrets, and the startup validation is worth having: a bad value refuses to start, which beats finding out at runtime.

```jsonc
{
  "SmartAdmin": {
    "ExternalAuth": {
      "CallbackBaseUrl": "https://admin.example.com"
    }
  }
}
```

The settings panel computes the full callback URL and shows it read-only with a copy button; paste it into the vendor console. In production without `CallbackBaseUrl`, the page shows a warning at the top, because the controller throws at login in that case.

`CallbackBaseUrl` takes only the backend's public root address; the kernel appends the callback path `/api/v1/auth/external/{provider}/callback` itself. Leave it unset in development and it falls back to the request host; in production it's required, because the Host header can be forged. Get it wrong and the vendor still redirects — vendors only check the domain — so the user lands on a path that doesn't exist, and it looks as if authorization finished and nothing happened. That's why it's validated at startup: a full callback URL, the frontend result page (`FrontendResultPath`), a query string or `#` fragment, or anything that isn't an absolute `http(s)` URL makes the app refuse to start. A gateway sub-path is fine, e.g. `https://gw.example.com/admin`. The frontend's `apiBase` must then go through the same prefix. The kernel uses two cookies to confirm that the callback and the pending-link claim come from the browser that started the login, and their Path follows the prefix (`/admin/api/v1/auth/external`). A frontend that calls the API around the prefix doesn't carry the cookies, and the login callback or the pending-link claim fails with 40014.

`GET /api/v1/auth/external/providers` only returns the non-secret fields (code, display name, icon) — just enough for the frontend to light up a button. A login button appears only for a provider that is configured and enabled; an unconfigured one doesn't show.

### Operational settings

Operational settings go through `sys_config`, changeable at runtime from the config page, keyed by provider code:

| Config key | Default | Controls |
| --- | --- | --- |
| `sys.externalauth.{code}.enabled` | Enabled | Whether this provider is turned on |
| `sys.externalauth.{code}.provisioning` | Deny | Whether an unbound identity's first login provisions a new account |
| `sys.externalauth.{code}.linkByAccount` | Off | Whether an unbound identity is linked to the local account with the same name |
| `sys.externalauth.{code}.defaultRoleIds` | Empty | Roles granted on auto-provisioning |
| `sys.externalauth.{code}.defaultOrgId` | Empty | Org assigned on auto-provisioning |
| `sys.externalauth.{code}.notify` | On | Whether in-site notices are also pushed to this provider; nothing is pushed while `enabled` is off either, see [Pushing notices to WeCom](#pushing-notices-to-wecom) |

Leave every key unset and the default behavior is **enabled + deny provisioning**: once the connection is configured, you have a working, binding-first SSO setup. `wecom` also pushes notices on top of that.

`enabled` is the switch at the right of each row on the Login methods page, independent of the connection configuration: the switches go through the save bar at the bottom of the page, while Save in the settings panel writes directly. The WeCom row has two more switches: "Link by account" for its `linkByAccount`, and "Push messages too" for `notify`. None of the other keys are seeded, and that includes `linkByAccount` for any other provider. Add them from the Advanced tab when you need them, with the group set to `externalauth`.

Reading these keys is consolidated in `ISysUserExternalService` — both the controller and `AuthService` call through it rather than each reading config keys on their own.

## What happens to an unbound identity

The `sys_user_external` table is unique on `(Provider, Subject)` — it records which external identity maps to which local user. When a first external login finds no binding, a provider with `linkByAccount` on first tries to link by account name; if it's off or finds no match, the `provisioning` switch decides what happens next:

- **Deny** (default): internally throws `OAuthAccountNotBound` (40016), but the login callback never hands that error straight to the user — it converts into a one-time "pending-link" ticket instead, steering the user toward a password or SMS login; once that succeeds, `POST pending-link/claim` with the ticket is what actually binds the external identity to the account (claiming requires the same browser that started the login, to block phishing-based hijacking). The other direction works too: get a local account first, then start a binding from the personal-center page with `POST {provider}/bind`.
- **Auto-provision (JIT)**: creates a local account with a random placeholder password and no forced password change, assigning the role and org from the two config keys above.

### Linking by account name

When the local accounts already are the WeCom accounts, the pending-link claim is a wasted step: employees have no password to type in. Set `sys.externalauth.{code}.linkByAccount` to `true`, and when the external identifier (the `userid`, for WeCom) is identical to a local account name, the first login binds the two and signs the person straight in. From then on only the binding counts; names are never matched again. The comparison is the same one password login uses to look up an account, so whether case matters comes down to the database collation. WeCom `userid`s are case-insensitive themselves, so keep the spelling consistent on both sides. Switching it on from the WeCom row on the Login methods page asks for confirmation first; for other providers, add the key from the Advanced tab as described above.

Super-admin accounts, disabled accounts, and accounts already bound for this provider are never linked automatically, and neither is an identity with no same-name account; all of them fall through to `provisioning`. A super admin who wants external login has to bind it from the personal center. With auto-provisioning on as well, linking is tried first and an account is provisioned only when linking comes up empty. To link on some other attribute, such as email or phone, override `AuthService.LinkByAccountAsync` or `ResolveExternalUserAsync` — no kernel changes needed.

::: warning Make sure both sides are the same people before turning it on
With it on, "who is this person" is answered by the IdP's accounts.

- Both account sets must come from the same source and belong to the same people, e.g. local accounts synced from the WeCom directory. Where each side creates accounts on its own, the same name doesn't mean the same person, and a link puts someone into a colleague's account. That's the most realistic risk; compare the two lists before enabling.
- Whoever can rename an account in the IdP can make someone sign in as the local account of that name. In WeCom, only admins and the directory-sync API can change a member's account; ordinary members can't change their own. Inside a company, that permission usually sits with IT.
- When someone's WeCom account is renamed, follow up locally by renaming the local account or unbinding and rebinding. Otherwise the renamed account's next first login goes looking for a different local account with that name.
- Only turn it on for providers whose identifiers the organization assigns and users can't pick. WeCom `userid` fits: non-members only get an openid, and members of interconnected enterprises arrive as `CorpId/userid`, so neither matches an ordinary account. DingTalk, WeChat and GitHub identifiers are unionids or numeric ids that won't match anyway. An OIDC IdP that allows self-registration, with `sub` being the username, must not have it on.
:::

## Signing in from inside the WeCom client

The one `wecom` provider serves two authorization pages, chosen by the User-Agent of the request that starts authorization. A UA containing `wxwork` is the WeCom client's built-in browser, desktop and mobile alike. The QR page is useless there, so it goes through OAuth web authorization instead, with `scope` set to `snsapi_base`: silent, no confirmation page. Everything else gets the QR login. The `code` from either page is exchanged for a `userid` through `auth/getuserinfo`, and the rest of the flow is identical.

For employees to land in the system by opening the app inside WeCom, configure two things in the WeCom admin console:

- Point the app's homepage at the site.
- Make the trusted domain cover `CallbackBaseUrl`'s domain, registering the port too if there is one; otherwise the redirect fails with a `redirect_uri` parameter error.

Inside the WeCom client, the frontend package's built-in login page starts a WeCom login on its own, once per browser session. After a failed login, or after signing out and returning to the login page, it stays put and lets the person choose — no bouncing back and forth.

The first login still has to get past the unbound check. With the default deny, the employee lands back on the login page to go through the pending-link claim. If the local accounts are the WeCom accounts, turn on "Link by account" on the WeCom row of the Login methods page. Employees then get straight in the first time they open the app, with no password claim. If they have no local account yet, turn on auto-provisioning.

## Pushing notices to WeCom

Once WeCom login is configured, in-site notices are pushed to WeCom as well, with nothing else to set up. Announcements from admins, scheduled-job alerts, and notices that business modules publish through `INoticeService.PublishAsync` are all covered.

### Setup steps

1. Install `SmartAdmin.Auth.WeCom` and call `AddSmartAdminWeComAuth()` before `AddSmartAdmin()`.
2. Configure WeCom under System config → Login methods, and set `CallbackBaseUrl` in production.
3. In the WeCom admin console, check two things: the app's visibility range includes the members who should receive messages, and the deployment server's egress IP is listed under "Trusted enterprise IP".
4. Members have a WeCom account bound: they have signed in through WeCom successfully, or bound one in the personal center. That binding is how the system knows which `userid` a notice goes to.
5. On the WeCom row of the Login methods page, click "Send test message" and check in WeCom that the card arrived.

The test message goes only to the WeCom account the current admin has bound, using the saved configuration, so save first and then click. It is sent once, directly: not queued, not retried, and the result comes back within seconds. No binding, no `CallbackBaseUrl`, a rejection by WeCom, or a send failure each show up as a message on the page, and a failure carries WeCom's error code and description. "Test connection" doesn't actually send anything, so it can't catch visibility-range problems. That is what this button is for.

The "Push messages too" switch on that row maps to `notify` and is on by default. Turn it off and notices stop being pushed; in-site notices are unaffected.

### Who receives it, and what happens on failure

Recipients are the enabled users in the notice's audience (everyone, by role, or specific users) who have a WeCom account bound. Anyone without a binding gets the in-site notice only, with no error.

Pushing happens in the background. The publish request only puts the notice on an in-memory queue and returns, so a slow or unreachable WeCom neither slows down publishing nor affects the caller's request. A failed send is only logged, never thrown to the caller, and is retried by these rules:

- Network errors, timeouts, HTTP 5xx and WeCom's `-1` (system busy): up to 3 attempts, waiting 5 seconds after the first failure and 30 seconds after the second.
- An invalid `access_token` (`40014`, `42001`): the cache is cleared and the message is resent once with a fresh token, without using up the attempts above.
- Any other error code (`60020`, `81013`, bad credentials and so on): no retry, logged at Error.

Requests carry WeCom's duplicate-message check (10 minutes). A request that timed out but actually went through won't reach the user twice when it is retried. As a result, two messages with identical content within 10 minutes arrive as one.

The queue lives in memory and is processed in order. When the process restarts, pushes that haven't gone out yet are lost; in-site notices are unaffected. While one message is being retried, those behind it wait. A full queue (capacity 10000) drops new messages and logs at Error. With multiple replicas, only the instance that published the notice sends it, so nothing is duplicated.

`PublishAsync` hands the notice to the queue before it returns. If a caller invokes it inside its own transaction and the transaction rolls back, the push may already have gone out. A caller that needs rollback safety should publish the notice after the transaction commits. The kernel's own two call sites (an admin publishing an announcement, a scheduled-job alert) both run outside a transaction.

### Limits on the WeCom side

- **Rate**: no more than 30 messages per minute and 1000 per hour to the same member through one app. WeCom drops anything above that without returning an error, so the log shows no failure. When many notices go out in a short burst, the later cards may never arrive, while the in-site notices are all there. See the official [rate limits](https://developer.work.weixin.qq.com/document/path/90312).
- **Trusted enterprise IP**: a self-built app needs "Trusted enterprise IP" set on its detail page, with the deployment server's egress IP entered. Without it, sending fails with `60020`. The background log records it at Error, and the failure message of the test button carries it too. Apps created long ago may not have this requirement. See the official [article](https://developer.work.weixin.qq.com/community/article/detail?content_id=16393566093602657085).
- **App visibility range**: a recipient outside the app's visibility range is rejected by WeCom. If all recipients are outside it, the error is `81013`; if only some are, the message still goes out and the ones left out are written to the log at Warning.
- **Card length**: the title is capped at 128 characters and the body at 512. Anything longer is truncated, and a truncated body ends with an ellipsis.

### The card link and what happens on tap

The card's title is the notice's title, and its body is the notice content as plain text with the Markdown markup stripped. The address it opens is `CallbackBaseUrl` plus a site-relative path: the first site-relative link in the notice body, or `/personal/notice` if there is none. Without `CallbackBaseUrl` (which can only happen in development) no address can be built, so the notice isn't pushed and one Warning is logged.

`CallbackBaseUrl` is the backend's public root address, so a deployment where frontend and backend have different origins must make page paths under that address (such as `/personal/notice`) open the frontend. Same-origin deployments need nothing.

When the card is tapped and the person is still signed in, the page simply opens. If not, the flow from the previous section takes over: silent authorization inside the WeCom client, then back to the page the card points at. Once the trusted domain is configured, nothing extra is needed.

Multi-app deployments have one limitation: a user with neither a default app nor a remembered last app lands on the app chooser when tapping a card, not on the page the card points at. A user whose sign-in has expired is usually in that group too, because the remembered app is cleared along with the session. Setting a default app for those users avoids it: click "Switch app" in the top bar to open the app chooser, then click "Set default" on an app's card. Single-app deployments, and users who have a default app, go straight to the page the card points at.

### Custom channels and replacement

The kernel knows interfaces, not WeCom. Any provider that implements both `IExternalAuthProvider` and `IExternalNoticeSender` receives pushes, and `WeComExternalAuthProvider` in the WeCom package does exactly that.

```csharp
public class MyProvider : IExternalAuthProvider, IExternalNoticeSender
{
    // members of IExternalAuthProvider omitted

    public Task<ExternalNoticeResult> SendAsync(
        ExternalNoticeMessage message, IReadOnlyList<string> subjects, CancellationToken cancellationToken = default)
    {
        // subjects are the recipients' identifiers on the channel, the same value as Subject in the binding table, at most 1000 per batch
        // throw ExternalNoticeException when the whole send fails; its retryable flag tells the kernel whether to retry
        // put recipients the channel refuses into Rejected; the kernel only logs them
        return Task.FromResult(new ExternalNoticeResult([]));
    }
}
```

The message of an `ExternalNoticeException` is written to the kernel log as is, so keep request URLs that carry tokens out of it. For the Login methods page to show the push switch and the test button for a type, its type descriptor `IExternalAuthProviderType` must return `true` from `SupportsNotice`, which defaults to `false`. `notify` applies to every provider alike and is on when unset.

To replace the default behavior:

- `INoticeDispatcher` is registered with `TryAddSingleton`, so registering your own implementation before `AddSmartAdmin()` replaces it entirely.
- To change only how a message looks, subclass `NoticeDispatcher` and override `BuildMessage` (title, body and link are all produced there); overriding `ResolveSubjectsAsync` changes who receives it. Register the subclass as `NoticeDispatcher`, before `AddSmartAdmin()`.
- An app that replaces `INoticeService` outright gets no automatic pushing. The push is triggered by `NoticeService.PublishAsync` calling `INoticeDispatcher.Enqueue` after publishing, so your own implementation has to make that call.

## Endpoints

The login, callback and binding endpoints are mounted under `api/v1/auth/external`:

| Endpoint | Purpose |
| --- | --- |
| `GET providers` | List available providers, for the frontend to render login buttons |
| `GET providers/all` | Admin-side: list every configured provider in the registry (disabled ones included) with its enabled and link-by-account switches, for the switches on the Login methods page |
| `GET {provider}/authorize` | Get the redirect URL, carrying a one-time state |
| `GET {provider}/callback` | Where the vendor calls back |
| `POST exchange` | Exchange a one-time ticket for a token |
| `POST pending-link/claim` | Claim an unbound external identity: after a successful password/SMS login, bind the identity resolved during the callback to the current user |
| `GET bindings` | The current user's bound external identities |
| `POST {provider}/bind` | Personal center: start binding an external identity |
| `DELETE {provider}/binding` | Personal center: unbind an external identity |

The endpoints that manage provider configuration for the Login methods page are mounted under `api/v1/sys/external-auth/providers`, with the module name `ExternalAuth`:

| Endpoint | Purpose |
| --- | --- |
| `GET` | Catalog: the installed types, plus each provider's configuration state, plain fields, `hasValue` with the last four characters, and callback URL |
| `PUT {code}` | Create or update a configuration; the request carries `type`; an empty secret field means keep the saved value; requires a recent identity re-verification |
| `DELETE {code}` | Clear a configuration without deleting the external accounts users have bound; requires a recent identity re-verification |
| `POST test` | Test the connection with the values in the form; an empty secret uses the saved one |
| `POST {code}/test-notice` | Send a test message: push one card to the account the current admin has bound for this provider, using the saved configuration. The outcome is in the response body's `status` (`ok`, `rejected`, `failed`, `notBound`, `noBaseUrl`, `unsupported`), and the envelope code stays 0 |

Both `state` and the one-time ticket reuse the same pattern as SMS verification codes: stored in the cache, consumed via an atomic `GetAndRemoveAsync`, valid exactly once. `state` uses only letters and digits, the only characters WeCom's and WeChat's OAuth accept.

Once external login resolves a `SysUser`, it hands off to `AuthService.CreateTokenAsync` — the same tail end used by password login and SMS login: session creation, token issuance, all of it shared. So session concurrency policy, force-logout, and refresh-token rotation apply to it identically.

## Callback and token-exchange example

`authorize` and `callback` are both full-page browser redirects, not JSON endpoints a frontend can `fetch`. `authorize` sends a 302 to the IdP's authorization page; once the IdP has authenticated the user, it redirects back to `callback`, which 302s again to the frontend's result page (`FrontendResultPath`, `/oauth/callback` by default) with whatever the next step needs, in the query string:

```
Success: GET /oauth/callback?ticket=<one-time ticket>
Failure: GET /oauth/callback?error=40015
```

The frontend reads `ticket` off that result page and exchanges it for a token — this is the step that's actually a `fetch`-able endpoint:

```bash
curl -X POST http://localhost:5100/api/v1/auth/external/exchange \
  -H "Content-Type: application/json" \
  -d '{"ticket":"<ticket from the callback redirect>"}'
```

The response envelope has the same shape as [password login](/guide/getting-started):

```json
{ "code": 0, "data": { "accessToken": "eyJ...", "expiresAt": "...", "refreshToken": "...", "mustChangePassword": false } }
```

The ticket is single-use — `exchange` consumes it via an atomic `GetAndRemoveAsync`, and a second exchange attempt gets `OAuthStateInvalid` (40014).

## Error codes

| Code | Name | When |
| --- | --- | --- |
| 40013 | `OAuthProviderDisabled` | This provider has been switched off at runtime, or has no complete configuration |
| 40014 | `OAuthStateInvalid` | The state doesn't match, or has already been consumed |
| 40015 | `OAuthExchangeFailed` | The token exchange with the vendor failed |
| 40016 | `OAuthAccountNotBound` | No binding exists, and this provider doesn't allow auto-provisioning |
| 40017 | `OAuthAlreadyBound` | This external identity is already bound to a different account |

Per the [frontend contract](/frontend/api-contract) convention, each of these codes needs a matching `msgKey` string configured in both language packs. Miss one, and the backend's consistency test turns red.
