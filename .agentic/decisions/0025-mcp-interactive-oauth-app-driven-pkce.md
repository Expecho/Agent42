---
id: 0025
date: 2026-07-19
status: accepted
tags: [mcp, oauth, pkce, auth, security, egress]
---

# Title: Interactive-OAuth MCP auth — app-driven PKCE with PRM discovery, not the SDK's blocking flow

> **Amended by [ADR 0081](0081-mcp-oauth-per-request-refresh-and-reconnect.md) (2026-09-21) — refresh
> mechanism only.** Connect-time refresh plus a static bearer never re-ran: the socket cache is keyed on the
> config version, so an expired token stayed stuck until a config write. The token is now resolved **per
> request** through `AuthHeaderHandler`, with one retry after a 401. A refreshed bundle is persisted by a
> non-bumping compare-and-swap write, and only `invalid_grant` marks a server as needing reconnect. PKCE,
> PRM discovery and the encrypted bundle stay authoritative here. `status: accepted` on purpose.

## Context
Phase 3 of config-driven remote MCP servers (ADR 0024) adds interactive OAuth — a human signs in so the
agent can call a server that needs delegated (not service-principal) access. The `ModelContextProtocol`
1.4.1 SDK ships an OAuth flow via `ClientOAuthOptions`, but it drives authorization through a **blocking**
`AuthorizationRedirectDelegate(authorizationUri, redirectUri, ct) → Task<string>` (Step 0 spike): the
delegate must open a browser and block until the code returns — a desktop/loopback assumption that cannot
bridge Agent 42's stateless, two-HTTP-request web flow (`/start` then a separate `/callback`). Easy Auth
(ADR 0002) also means the callback must stay authenticated to bind tokens to the right user.

## Decision
Drive the authorization-code + **PKCE** flow **in the app itself**, not via the SDK. Two endpoints, both
**behind Easy Auth** (never `excludedPaths`): `GET /api/mcp/oauth/start` does **PRM discovery** (RFC 9728
protected-resource metadata → authorization-server metadata, RFC 8414/OIDC), optional **dynamic client
registration** (RFC 7591) when no client id is set, generates the PKCE verifier + `state`, persists them in
a per-user pending grain, and 302-redirects to the authorization endpoint; `GET /api/mcp/oauth/callback`
validates `state` (one-time `TakeAsync`), exchanges the code, and persists the access + refresh tokens
**encrypted** (`IMcpSecretProtector`) as a JSON bundle in `StoredMcpServer.TokenCiphertext`. At connect the
tool provider decrypts the bundle, **refreshes at connect time** if near expiry (app-owned), and injects the
access token as a static `Authorization: Bearer` via the transport's `AdditionalHeaders`.

## Alternatives considered
- **Use the SDK's `ClientOAuthOptions` + a custom `ITokenCache`** — rejected: the interactive dance still
  can't run through the blocking redirect delegate in a web flow, so the SDK would only save refresh; it
  isn't worth coupling to the SDK's OAuth internals (and its re-trigger-interactive-on-miss footgun) when
  a refresh is a single token-endpoint POST.
- **Manual OAuth endpoint fields** (user types authorization/token URLs) instead of PRM discovery — rejected
  by the user in favour of the MCP-native discovery path (2026-07-19).
- **Per-request refresh via `AuthHeaderHandler`** (as Phase 2's ClientCredentials) — deferred: it would need
  the grain-persist coupled into the connection handler. Connect-time refresh + a static bearer is simpler,
  keeps the factory grain-free, and is sufficient because connections rebuild on activation / config-version
  change (accepted staleness; a mid-connection-life expiry is picked up next activation).
- **New `StoredMcpServer` fields for the tokens/endpoints** — rejected: everything runtime-OAuth (access +
  refresh token, token endpoint, client id, scopes) rides the already-reserved encrypted `TokenCiphertext`
  blob, so there is no schema change.

## Consequences
- Enables delegated-access MCP servers with the MCP-native discovery path and no per-server code.
- **Manual prerequisite:** the redirect URI `https://chat.agent42.nl/api/mcp/oauth/callback` must be
  registered in each OAuth provider's app registration. The callback MUST stay behind Easy Auth or
  `GetUserId()` returns `local-dev` and tokens bind to the wrong user (the `excludedPaths` gotcha).
- **Headline egress consequence (ADR 0024/0013):** once connected, the server sees conversation content and
  its tool results are an inbound prompt-injection channel — now reachable via a user-completed sign-in.
- **Outbound targets are response-controlled, not request-driven.** The starting MCP endpoint is the user's
  own config, but the authorization/token/registration URLs are DISCOVERED from that server's responses — a
  compromised MCP server could steer the token POST at another host. Accepted under the same "user configured
  their own server" rationale as a user provider endpoint (ADR 0018), and the callback never reflects an
  upstream error body into the browser redirect (a generic `token_exchange_failed` reason; the detail is
  logged server-side). Revisit if the tenant model ever broadens.
- Accepted limitations (first cut): connect-time (not per-request) refresh; a refresh persists via
  `SaveAsync`, bumping the overlay version → one extra reconnect on the next activation; discovery tries the
  common well-known locations (origin PRM; RFC 8414 path-insertion + append, OIDC fallback) — an exotic
  metadata layout is a future refinement. Spec: `.agentic/specs/completed/plan-remote-mcp-servers.md`.
