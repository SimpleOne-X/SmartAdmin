# API Docs

Start the backend and `/scalar` is already sitting there in the browser — every endpoint expandable, every request sendable on the spot, nothing extra to install. What it renders is exactly the `/openapi/v1.json` that the frontend's `npm run gen:api` pulls from, so the docs and the contract can't drift apart. In production, neither endpoint is mounted by default.

## Toggle & configuration

```jsonc
{
  "SmartAdmin": {
    "Scalar": {
      "EnabledInProduction": true   // default false — development ignores this switch
    }
  }
}
```

While it's off, production doesn't map `/scalar` or `/openapi/{documentName}.json` at all: requesting either one gets a 404, not a 401. Development never consults this switch — both endpoints are always there and both are anonymous, because the contract is the source `npm run gen:api` reads, and putting a gate in front of it on a dev machine only adds a login step to code generation.

The route is fixed at `/scalar`; there's no prefix setting. It lives on the backend side: in development the frontend template's Vite proxy forwards it through verbatim, and when the frontend and backend are deployed separately, the address to open is the backend's, not the frontend site's.

## Authentication: the shell and the data are different things

Once production has it enabled, the two endpoints are treated differently:

| Endpoint | What's in it | Once enabled in production |
| --- | --- | --- |
| `/scalar` | A static renderer; carries no API information | Always anonymous |
| `/openapi/{documentName}.json` | The full API contract | Requires the permission code `GET:/openapi/{documentname}.json` |

A full contract that anyone can fetch anonymously hands over a reconnaissance surface, which is why that's the one that gets locked down. Locking the shell down would buy nothing — it knows nothing by itself — and it would break the page outright: a browser tab opened fresh doesn't carry the Bearer token the SPA holds, so it would take a 401 on arrival.

Getting the token into Scalar is a manual step: click Authentication in the top right, pick Bearer, paste. The kernel offers no `?access_token=` query-string fallback, which would leave a long-lived JWT in plaintext in browser history and gateway access logs. The [realtime](/backend/realtime) hub pays that price because a WebSocket handshake can't carry a request header at all; a docs UI has other options.

## Authorization: who gets the code

The permission code sits on the "查看契约" (View contract) button under the built-in "系统运维 → 接口文档" (System Ops → API Docs) menu entry — the page row itself only controls visibility, following this repo's rule that capabilities hang off buttons. Grant that button to a role in role management and everyone in that role can fetch the contract JSON in production; a super admin bypasses permission codes and can always fetch it. Someone granted the page but not the button can still open `/scalar` — they just can't pull the contract, so the page stays empty.

Not logged in gives a 401; logged in without the code gives a 403. The two status codes stay distinct, so troubleshooting never comes down to guessing whether the token failed to paste or the permission was never granted.

## The admin entry page

"系统运维 → 接口文档" is a thin entry page: opening it opens `/scalar` in a new tab, and the body holds only two things — a manual link for when the popup gets blocked, and a "copy my API token" button. What it copies is the access token of the current session, ready to paste into Scalar's Authentication panel.

The page doesn't know anything about the backend's environment switch. With production opt-in off, the new tab gets a 404 and clicking the button does nothing harmful. Having the frontend guess at the backend's configuration would cost more to maintain.

## Dependency: the core packages' one third-party exception

`Scalar.AspNetCore` is the single named exception to the core four packages' "SqlSugarCore plus Microsoft.\* only" line. It has zero transitive dependencies, its UI assets are embedded in the package, and it does nothing but map an endpoint and render a page — no serialization, no storage, no authentication.

Shipping it as an optional package was rejected. `SmartAdmin.Excel` and `SmartAdmin.Caching.Redis` back business capabilities most consumers never use, so "not installing it" is itself the payoff. A docs UI is dev tooling that every consumer is meant to have by default, and making it optional would turn "everyone has it" into "whoever knows it exists and is willing to install a second package has it".
