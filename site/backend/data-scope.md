# Multi-Org Data Scope

Data scope carries the whole org-filtering burden on behalf of business code, and that is where the kernel makes its name. It answers one question: given the same endpoint and the same SQL, why do different users see different rows? The answer sits in a global query filter, which trims results automatically by the **effective org set** resolved for the current request.

## Five data-scope types

Data-scope types are defined in `Core/Security/DataScopeType.cs` — the org dimension of what data a role can see, stored as an `int`:

| Value | Type | Meaning |
| --- | --- | --- |
| 1 | `All` | All data, not constrained by org |
| 2 | `Org` | Current org only — the user's primary org |
| 3 | `OrgAndChildren` | Current org and below — primary org plus all descendant orgs |
| 4 | `Self` | Self only — data created by the user themself |
| 5 | `Custom` | Custom orgs — an explicitly specified set of orgs |

Scope is attached to **roles** (`sys_role_data_scope`); a user can hold multiple roles. Merging follows a **widest-wins** rule:

- If any role is `All` → unrestricted overall, sees everything;
- Otherwise, union the org sets across roles (current org = primary org; current org and below = primary org + descendants; custom = the specified set);
- If any role is `Self` → an additional "self only" dimension is unioned in (can also see data they created themselves).

Say a user holds two roles: one `Org` role whose primary org is "East China Branch," and one `Custom` role scoped to "South China Branch" and "North China Branch." Neither role is `All`, so the merge is the union of all three orgs — queries filter to just those three. Neither role is `Self`, so this user doesn't see the extra "created by me" layer on top.

## Resolution: merging multiple roles into one result

`IDataScopeProvider.ResolveAsync(userId)` merges a user's multi-role scopes into a single immutable `DataScopeResult`. The default implementation, `DataScopeProvider`, caches results per user (invalidated on permission/org changes), only aggregating from the DB on a cache miss:

```csharp
protected virtual async Task<DataScopeResult> ComputeAsync(long userId)
{
    // fetch the user's enabled roles
    var roleIds = ...;
    if (roleIds.Count == 0) return DataScopeResult.Restricted([], includeSelf: true, userId); // no roles → self only

    var scopes = ...;   // each role's data-scope configuration
    if (scopes.Any(s => s.ScopeType == DataScopeType.All)) return DataScopeResult.Unrestricted; // widest wins
    if (scopes.Count == 0) return DataScopeResult.Restricted([], includeSelf: true, userId);    // has roles but none configured → self only

    // accumulate org set per role: Org adds the primary org; OrgAndChildren expands descendants; Self sets includeSelf; Custom unions the specified set
    return DataScopeResult.Restricted(orgSet, includeSelf, userId);
}
```

::: tip Secure by default
No roles, or roles with no scope configured, both collapse to "self only" — never widened.
When both are empty (org set empty + self not included), that means "sees no data at all"
(rows marked shared on an `OrgAuditEntity` excepted) — deny by default, not allow by default.
:::

`DataScopeResult` (`Core/Security/DataScopeResult.cs`) is an immutable value object, safe to cache across requests:

```csharp
public sealed record DataScopeResult
{
    public bool IsUnrestricted { get; init; }                       // unrestricted: sees everything
    public IReadOnlyCollection<long> OrgIds { get; init; } = [];    // visible org Id set (matched against CreateOrgId)
    public bool IncludeSelf { get; init; }                          // whether "self only" is appended
    public long UserId { get; init; }                              // matched against CreateUserId when IncludeSelf is true

    public static readonly DataScopeResult Unrestricted = new() { IsUnrestricted = true };
    public static DataScopeResult Restricted(IReadOnlyCollection<long> orgIds, bool includeSelf, long userId) => ...;
}
```

It's a `record` with `init` properties whose parameter names match property names so it round-trips cleanly through `System.Text.Json` — needed because when switching to Redis for multi-instance deployment, this result must serialize into the cache and back out.

## Anchor field: `CreateOrgId`

The anchor for data-scope filtering is the `CreateOrgId` field on the entity — **the org the creator belonged to at creation time**. Business tables that inherit `DataEntity` (`SqlSugar/Entities/DataEntity.cs`) get this field automatically:

```csharp
public abstract class DataEntity : BaseEntity, IOrgScoped
{
    [SugarColumn(IsNullable = true, ColumnDescription = "归属机构 Id(数据范围锚点)")]
    public long? CreateOrgId { get; set; }
}
```

Tables that don't need org isolation (global dictionaries, the org tree itself) continue to use `BaseEntity` — no such field, not subject to data-scope filtering.

`CreateOrgId` **is never set by business code**; it's auto-filled on insert by the audit AOP hook from the current user's `org` claim (`SqlSugarSetup.cs`):

```csharp
// CreateOrgId not specified → fill with the current user's owning org (data-scope anchor)
else if (info is { PropertyName: nameof(DataEntity.CreateOrgId),
                   EntityValue: DataEntity { CreateOrgId: null } }
         && currentUser.OrgId is { } insOrgId)
    info.SetValue(insOrgId);
```

::: warning An unfilled anchor means you can't see the row you just inserted
If `CreateOrgId` isn't filled (e.g. the kernel's token flow was bypassed, or the user has no `org` claim), that row's org-dimension visibility ends up empty — org-scoped users can't query it. This is exactly why the audit AOP's auto-fill is a prerequisite for data scope to work at all.
:::

## Global filter: zero filter conditions in business code

The filter is registered once in `SqlSugarSetup.cs` against entities implementing `IOrgScoped`
(`DataEntity` and its subclasses), and takes effect on every query.
Tables inheriting `OrgAuditEntity` implement `IOrgShared` instead:
same org-scope rule, handled by a second filter (see "Sharing rows" below):

```csharp
client.QueryFilter.AddTableFilter<IOrgScoped>(e =>
    scope.Current.IsUnrestricted == true
    || (e.CreateOrgId != null && scope.Current.OrgIds.Contains(e.CreateOrgId.Value))
    || (scope.Current.IncludeSelf == true && e.CreateUserId == scope.Current.UserId));
```

The three branches correspond to the three visibility cases: if unrestricted, the whole predicate is always true (no filtering); otherwise `CreateOrgId ∈ org set`, or (when "self only" is enabled) `CreateUserId == current user`.

::: details Two implementation details
**Matched by interface, not by base class**:
SqlSugar's `AddTableFilter<T>` matches an interface or an exact type, not a base class —
which is why the anchor field is exposed through the `IOrgScoped` interface.
`IOrgShared` exposes the same `CreateOrgId` and `CreateUserId`, plus `IsShared`.
The soft-delete filter `AddTableFilter<ISoftDelete>` works the same way.

**Boolean flags written as `== true`**: the three `scope.Current` properties in the expression are independent of the entity parameter, so SqlSugar evaluates them locally into constants first (the org set becomes a SQL `IN`), then splices them into the `WHERE` clause. The two booleans are written as `== true` rather than as bare booleans because SqlServer's predicate context doesn't accept a bare scalar — it must be a comparison expression.
:::

With this in place, business code that writes the plainest possible query is already constrained by org isolation:

```csharp
// In a business service: no org-filter condition written anywhere
var orders = await orderRepo.AsQueryable()
    .Where(o => o.Status == OrderStatus.Pending)
    .ToListAsync();
// The actual SQL executed already has this appended: AND (CreateOrgId IN (...) OR CreateUserId = ...)
```

With the exact same code, a user with `All` scope sees every pending order, a user with `Org` scope sees only their own org's, and a `Self`-scoped user sees only what they created — the difference comes entirely from the `DataScopeResult` resolved earlier in the request; not a single line of business logic changes.

::: warning Write-path guard
The global filter only applies to **queries (SELECT)** — not to primary-key-based `Updateable` / `Deleteable`.
To cover this, the `SqlSugarRepository` repository has a built-in write-path guard
for `UpdateAsync` / `DeleteAsync` on `IOrgScoped` and `IOrgShared` entities:
before writing, it queries through the scope-filtered path
to confirm the target row is within the current scope.
A row the user can't see (another org's private row) returns 0 rows;
updating or deleting a shared row without owning it throws `ErrorCode.OrgOutOfScope`.
Writes that bypass the repository via the `Db.Updateable/Deleteable` escape hatch aren't covered by this guard
and must validate ownership themselves.
:::

## Sharing rows: `IsShared`

Most rows in a table belong privately to their own org, but a few have to be visible to every org,
such as a process route that all of them use.
For a table inheriting `OrgAuditEntity`, set a row's `IsShared` to `true` and that's it;
neither the base class nor any role's data scope needs to change.

```csharp
public class ProcessRoute : OrgAuditEntity { /* business fields */ }

route.IsShared = true;   // every org can query this row
```

### Query rules

`OrgAuditEntity` implements `IOrgShared`, and its filter lets one more branch through beyond the org scope:

```csharp
client.QueryFilter.AddTableFilter<IOrgShared>(e =>
    scope.Current.IsUnrestricted == true
    || (e.CreateOrgId != null && scope.Current.OrgIds.Contains(e.CreateOrgId.Value))
    || (scope.Current.IncludeSelf == true && e.CreateUserId == scope.Current.UserId)
    || e.IsShared == true);
```

For a restricted user, whether a row is returned depends only on four combinations of `IsShared` and `CreateOrgId`:

| `IsShared` | `CreateOrgId` | Returned? |
| --- | --- | --- |
| `true` | set or empty | Yes, to users of every org |
| `null` or `false` | inside the user's org scope | Yes |
| `null` or `false` | outside the scope | No, except for the creator when the scope includes "self only" |
| `null` or `false` | empty | No, only a creator whose scope includes "self only" |

`null` and `false` both mean not shared,
so adding the column to a table that already holds data does not change what any row shows.
The "all" scope and super admins are unrestricted and see every row.

A shared row is visible under a "self only" scope,
and also under the empty scope that an unbound request falls back to.
Endpoint permissions are still checked as usual; sharing relaxes only the data-scope layer.
So "an empty scope sees no data" holds only for tables inheriting `DataEntity`.

### Who can write a shared row

Other orgs can view and reference a shared row but not change it.
Two kinds of user can write it:
those whose data scope contains the row's owning org,
and the creator themselves when their scope includes "self only".
This is the same rule as for ordinary org data:

| Operation | Scope contains the owning org, or includes "self only" and is the creator | Other restricted users |
| --- | --- | --- |
| Query a shared row | Visible | Visible |
| Update or delete a shared row | Allowed | Throws `ErrorCode.OrgOutOfScope` (`42030`) |
| Update or delete a private row | Allowed | Returns 0 rows |
| Turn the share flag on or off | Allowed | Same as update, rejected |

A shared row throws and a private row returns 0 because other orgs can read the shared one.
If `UpdateAsync` silently returned 0 there too,
business code that ignores the return value would think the save succeeded.
A private row is already invisible to other orgs, so it keeps the existing return-0 behavior.

A shared row with an empty `CreateOrgId` has no owning org:
among restricted users only a creator whose scope includes "self only" can write it,
and anyone else gets `OrgOutOfScope` on update or delete as well.
Calls with no HTTP context, such as background jobs and startup, are unrestricted.

Ownership is decided in `DataScopeResult.Owns(createOrgId, createUserId)`.
The write guard uses it, and a business service can use it too, to compute an "editable" flag on a DTO.
It matches the first three branches of the filter, without the shared allowance.

The write guard has to read the current data scope,
so a repository for an `OrgAuditEntity` must carry an `IDataScopeContext`.
A repository injected through DI already does;
one you `new` yourself, or a subclass written as `: SqlSugarRepository<T>(db)`,
has to pass it on to the constructor,
otherwise an update or delete throws `InvalidOperationException` instead of passing silently.
Insert does not go through the write guard, so this does not affect it.

Writes that bypass the repository through `Db.Updateable` / `Deleteable` are not covered by this guard,
and shared rows need their ownership checked by hand too.
`OrgAuditEntity` deletes physically with no recycle bin:
once a shared row is deleted, no org sees it any more, and it cannot be restored.

### Conventions when adopting it

The kernel can't enforce these, so business code has to:

- Detail tables carry no org field (inherit `PrimaryId` or `BaseEntity`),
  and the endpoints that read or write details check the parent table first.
  Filters take effect per table;
  if a detail table carried an org field too, another org could see the parent row but not its details.
- A shared row must not reference one org's private data.
  If a shared route points at A's private work center, B sees nothing for that part when opening the route.
  The kernel doesn't know the business foreign keys, so the business must check at save time.

### Clearing the filter

A query that must bypass the org scope (a database-wide duplicate check, for example) uses `ClearFilter`.
It matches exactly the interface a filter was registered against,
and the `OrgAuditEntity` filter is registered on `IOrgShared`, so both interfaces have to be cleared together:

```csharp
db.Queryable<ProcessRoute>().ClearFilter<IOrgScoped, IOrgShared>()
```

This form works on `DataEntity` tables as well.
Writing only `ClearFilter<IOrgScoped>()` does not clear the `OrgAuditEntity` filter:
it compiles fine, and the query is quietly still restricted by org scope.

## Context carrier: why not `AsyncLocal`

The effective scope is passed through the request via `IDataScopeContext`. `Current` is never null, and an authenticated request must explicitly resolve and write it before any query — a step guaranteed by the authorization pipeline. What happens when nothing set it is the next section.

The kernel provides two implementations, depending on whether an HTTP context is available:

- **`HttpContextDataScopeContext`** (AspNetCore layer, HTTP path): stores the current request's scope in `HttpContext.Items`.
- **`DataScopeContext`** (SqlSugar layer, non-HTTP fallback): `AsyncLocal`-based, used by background jobs / self-checks that have no HTTP context.

The HTTP path **deliberately avoids `AsyncLocal`**, for the reason documented in `HttpContextDataScopeContext.cs`:

> The authorization filter is a callee within the MVC pipeline; an `AsyncLocal` set after an `await` inside it won't flow back up to the pipeline's caller (a classic pitfall), so queries made in the action wouldn't see it. `HttpContext.Items` is attached to the request object itself and is reliably visible across the whole pipeline, avoiding this problem.

In other words, scope is written in the **authorization filter**, while the query happens in the **action further up the call chain**. An `AsyncLocal`'s value only flows downstream along the execution flow — a value set in a downstream filter is invisible to an upstream action; whereas `HttpContext.Items` is attached to the request object, so the entire pipeline can reliably read the same value. This is exactly why `Items` has to be chosen over `AsyncLocal`.

## Unbound requests fail closed

When an authenticated request has no scope bound, the fallback is an **empty scope** (empty org set, plus "self only"), so it sees only rows it created itself. It is not "unrestricted".

On the normal path the scope is always bound during authorization, so this fallback only catches the exceptional path. The fallback direction is chosen deliberately: an unbound endpoint shows "no data comes back," which someone reports within the hour, rather than "every org's data is visible," which says nothing at all and is far more dangerous.

Three kinds of call are exempt: calls with no `HttpContext` (background jobs, startup, seeding — all trusted contexts), anonymous requests, and super admins.

A consumer endpoint carrying only `[Authorize]`, with neither `[RolePermission]` nor `[ActiveSession]`, relies on a **global binding**: a global authorization filter binds the user's real scope, which is what keeps its own `DataEntity` queries from showing every org's rows. It is idempotent, so stacking it with either attribute resolves only once, and anonymous requests skip it outright.

```json
{
  "SmartAdmin": {
    "Security": { "DataScopeFailOpen": true }
  }
}
```

Set to `true` and an unbound request falls back to unrestricted instead. The switch exists to give an upgrade an escape hatch, not to be left on.

## System tables: the half the filter can't reach

The global filters only match `IOrgScoped` and `IOrgShared`, and the kernel's own `Sys*` tables all inherit `BaseEntity`, so not a single row of theirs is filtered. Which means "who can see which logs, which sessions" has to be decided by each service.

These tables show a non-super-admin only the rows produced by users inside their scope:

| Table | What a non-super-admin sees |
| --- | --- |
| Operation log, login log, exception log | Only records produced by in-scope users |
| Online sessions | Only in-scope users' sessions; force-logout can only kick in-scope users, and a non-super-admin cannot kick a super admin |
| Notification management list | Only notifications published by in-scope users |

A force-logout on a session that never existed still returns success idempotently (the row an admin clicked may simply have just expired). A session that exists but sits outside the operator's data scope, or a non-super-admin trying to kick a super admin, both throw the same `42024` — the response never reveals which kind of overreach it was. Session ids are high-entropy random tokens, unguessable on their own, so the behavioral difference between "doesn't exist → success" and "exists but forbidden → error" isn't an enumerable probing surface by itself.

The decision is funnelled into `IDataScopeGuard` (an out-of-scope org assertion, the scope predicate for user queries, and the set of in-scope user ids), shared by the user, org, position, file, log and session services alike. Centralizing it in one place is what lets logs and sessions enforce the same boundary too: without it, a sub-admin granted the log-viewing permission could read the whole company's operation records, parameter JSON included, and force-logout anyone.

A super admin account is the exception: it is invisible to a signed-in non-super-admin, even when the data scope is "all". The user list and export leave it out. Reading, editing, resetting the password, enabling or disabling and deleting it by Id are all treated as "user not found" (`42001`). The read endpoints of individual grants return `41005`.

A data scope only speaks about orgs and can't tell who is a super admin, so this rule ignores the scope. It lives in `ScopeUsers` and `IsUserInScopeAsync` on `IDataScopeGuard`, and the endpoints that address a user by Id check it once more in `UserService.IsHiddenFromCaller`. Without it, an ordinary admin holding the "User - Reset password" button could reset the super admin's password and sign in as the super admin.

The in-scope user id set **includes soft-deleted users**. Logs and sessions are historical fact, and what someone did before they left shouldn't vanish because their account was soft-deleted.

## Extension point

`IDataScopeProvider` is the replacement point. The default implementation aggregates `sys_role_data_scope`; a typical replacement swaps in a different isolation dimension (e.g. per-tenant). Both `ResolveAsync` and the internal `ComputeAsync` are `virtual`, so a consumer can override just one step. Per the kernel's replaceability convention, a consumer registers its own `IDataScopeProvider` before `AddSmartAdmin()` to take over, without forking anything.
