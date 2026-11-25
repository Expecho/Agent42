---
id: 0002
date: 2026-07-05
status: accepted
tags: [auth, azure, container-apps]
amended_by: [0042, 0070, 0074, 0076, 0121, 0145]
---

# Gate the Container App behind Entra ID Easy Auth instead of app-level auth

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** The premise that every signed-in user is one of two trusted accounts stops being
> true when sign-in opens to any Microsoft account (the identity flip, Phase B of that plan). Easy Auth still
> authenticates; who reaches the privileged surfaces is now `Access:Owners`, an **advisory** tier (any signed-in user can
> forge the principal header through the Copilot shell, so it is not a security boundary).

> **Amended by [ADR 0022](0022-per-agent-api-key-and-async-run-endpoint.md) (2026-07-17),
> [ADR 0035](0035-read-only-share-links.md) (2026-07-30),
> [ADR 0042](0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md) (2026-08-20),
> [ADR 0070](0070-expose-stored-agents-as-an-mcp-server.md) (2026-09-11) and
> [ADR 0076](0076-local-bridge-for-machine-local-mcp-servers.md) (2026-09-17) — seven carve-outs.**
> `globalValidation.excludedPaths` now holds `/api/agent-run`, `/api/share/*`, `/shared`, `/shared/*`,
> `/api/agent-mcp`, `/api/agent-mcp/*` and `/api/local-bridge`.
> Each is NOT ingress-gated and each therefore SELF-ENFORCES access in the handler and fails closed:
> `/api/agent-run` on a per-agent API key (401), the share routes on the share token (404), `/shared*`
> is the static public viewer, which carries no data of its own, and the MCP pair on the same per-agent key
> (401) — enforced by MIDDLEWARE over the prefix, not in a handler, because one endpoint serves the whole
> JSON-RPC surface — and `/api/local-bridge` (an exact entry, the first WebSocket) on a per-server
> bridge key (401). Every other endpoint (all UI + API) stays
> fully Easy-Auth-gated, so the "no auth code in handlers" consequence below holds everywhere except those
> self-enforcing paths. **The matching rule is exact-unless-`/*`** — see ADR 0042; assuming prefix matching
> is what made ADR 0035 non-functional in production, and `EasyAuthExclusionTests` now guards the list.

> **Also amended by [ADR 0074](0074-identity-alias-for-a-second-entra-account.md) (2026-09-13) — one
> identity, two principals.** `excludedPaths` is unchanged and no carve-out is added. What changes is what
> the app does with the identity Easy Auth hands it: an `Identity:Aliases` map may rewrite the incoming
> object id to another account's, so one person's two Entra accounts reach one dataset. Identity still
> originates **only** from the Easy Auth headers — the alias is applied after extraction, in
> `CurrentUser.ResolveUserId`, and is never a substitute for authentication. The `local-dev` fallback is
> returned before the map is consulted, so the rule that a header-less (excluded-path) request must never
> be trusted as a user is untouched.

## Context
The API was fully open while being publicly reachable (chat.agent42.nl), exposing
LLM spend and private conversations. Needed: sign-in for exactly two people with
minimal app code (commit 57790a1, 2026-07-05).

## Decision
Use Container Apps built-in authentication (Easy Auth) with Entra ID. Access is
restricted twice: assignment-required on the `agent42-easyauth` enterprise app AND
an `allowedPrincipals` allowlist in the auth config. The app reads the injected
`X-MS-CLIENT-*` headers in `CurrentUser.cs` — `X-MS-CLIENT-PRINCIPAL-ID` for grain
keying, and additionally `-NAME`, `-IDP` and the `X-MS-CLIENT-PRINCIPAL` claims blob
for telemetry enrichment (ADR 0009) — the ingress strips inbound `X-MS-CLIENT-*`
headers, so the values are trustworthy.

## Alternatives considered
- **ASP.NET Core OIDC middleware in-app** — more code and config to maintain for a
  two-user personal app; Easy Auth gives the same gate declaratively in Bicep.
- **No auth / obscurity** — unacceptable: public endpoint burning Azure OpenAI quota.

## Consequences
- Every endpoint (UI + API) is protected at the ingress; no auth code in handlers — with the single
  exception of the ADR 0022 key-gated `/api/agent-run` (excluded from the ingress gate, self-enforced).
- Local dev has no Easy Auth, so all requests map to the `local-dev` pseudo-user.
- The deploy workflow must keep passing `AAD_CLIENT_SECRET`; rotating that app
  registration secret means updating the GitHub secret too.
