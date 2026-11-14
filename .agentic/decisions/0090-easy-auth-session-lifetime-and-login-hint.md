---
id: 0090
date: 2026-10-10
status: accepted
tags: [auth, easy-auth, infra, frontend, pwa]
supersedes: []
extends: [0002, 0074]
amends: []
---

# Easy Auth session lasts 14 days, Entra gets a login hint, and the sidebar has Sign out

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** `authLoginHint` is removed in the identity flip (Phase B): it would pre-fill the owner's
> email for every stranger. The cost is the Entra account picker returning for the owner's two accounts.

## Context
Opening the app showed the Entra account picker almost every time. There were two causes:

1. The `authConfigs` resource set no `login` block, so the `AppServiceAuthSession` cookie kept the
   platform default: a fixed **8 hours**. After that, every open went back through Entra.
2. Both allowed accounts (ADR 0074) are in **one tenant**, and the redirect sent no hint. So Entra could
   not choose for you and showed its picker on every sign-in. A `domain_hint` would not help.

The app had no sign-out at all.

## Decision
- **Session lifetime `14.00:00:00`** (`authSessionLifetime` in `infra/main.bicep`), as `FixedTime`
  `cookieExpiration`. This is safe because the app reads only `X-MS-CLIENT-PRINCIPAL` (`CurrentUser.cs`) and
  never an Entra access or refresh token. With no token store configured, nothing depends on the tokens
  staying fresh.
- **`login_hint=Expecho@users.noreply.github.com`** (`authLoginHint`) in the Entra provider's `loginParameters`. That is
  the canonical account ADR 0074 maps the alias onto. An empty value sends no hint.
- **Sign out**: an icon link in the sidebar footer to `/.auth/logout?post_logout_redirect_uri=/`. It is plain
  HTML with no JS. This is how you reach the alias account: sign out, then pick it at Entra.
- **The SW bypasses `/.auth`** before any `respondWith`, like `/api`, `/searxng` and `/dashboard`. Otherwise
  an offline sign-out would paint the cached `/index.html` and look signed in.

## Alternatives considered
- **Hint only, keep 8 h.** Fewer clicks per sign-in, but you would still sign in at least daily.
- **Lifetime only, no hint.** Switching accounts stays one click in the picker, but the picker still appears
  every 14 days. The user asked for both.
- **Remember the last account chosen.** Not possible with `RedirectToLoginPage`: Easy Auth redirects before
  any app code runs, so there's nowhere to keep the choice. The hint is static.
- **`IdentityProviderDerived` expiry.** This ties the session to the token lifetime (about an hour), which
  is the opposite of the goal.

## Consequences
- A stolen session cookie is valid for up to 14 days instead of 8 hours. The allowlist of allowed users
  (`allowedPrincipals`) and the assignment requirement on the enterprise app are unchanged. Sign out ends
  the session early.
- **Unmeasured until deploy:**
  - Easy Auth must accept `14.00:00:00`. `EasyAuthSessionTests` proves that it parses as a .NET TimeSpan,
    not that the platform accepts it.
  - `/.auth/logout` must end the Entra session as well as the app cookie. If it only clears the cookie, the
    hint signs you straight back in as the canonical account, and the alias is reachable only from a
    separate browser profile.

  Post-deploy check: sign out, then confirm that Entra asks rather than signing in silently.
- DevHost has no Easy Auth, so Sign out 404s locally.
- An installed iOS PWA keeps its own cookie jar, so it needs its own sign-in. It then lasts the same 14 days.

## Tests
`EasyAuthSessionTests` (Bicep source text). `PwaAssetsTests.ServiceWorker_BypassesEasyAuthEndpointsBeforeCaching`
and `PwaAssetsTests.Sidebar_HasASignOutLinkToEasyAuthLogout`.
