---
id: 0081
date: 2026-09-21
status: accepted
tags: [mcp, oauth, auth, refresh, cache, frontend, stream-events, no-migration]
supersedes: []
amends: [0025, 0064]
---

# Refresh an MCP OAuth token per request, and offer Reconnect in the turn when the grant is dead

## Context

ADR 0025 refreshed an InteractiveOAuth token **at connect time** and sent it as a static bearer. It
assumed that "a mid-connection-life expiry is picked up next activation". In practice that never
happens: `UserMcpToolProvider` caches live sockets keyed **only** on the config-grain version, so a new
activation is a cache hit that reuses the socket and its expired bearer. The refresh only runs on a cache
miss, and a miss needs a config write. So a Google Calendar or Gmail server loses its tools about an hour
after sign-in, and stays that way until a restart or a settings save.

A grant that is really dead (revoked, or a Testing-status client's refresh token after 7 days) produced
the same symptom: the rejected refresh was logged as a warning and the server was dropped silently.

## Decision

**Partial supersession of 0025. It replaces the refresh mechanism only;** PKCE, PRM discovery and the
encrypted bundle are unchanged.

1. **A per-request bearer.** The caller (`UserMcpToolProvider` or `McpConnectionTester`) builds an
   `InteractiveOAuthTokenProvider : IAuthHeaderProvider`, and the factory wires it into
   `AuthHeaderHandler`, as ClientCredentials already does. When a request gets a 401, the handler
   re-resolves once and **resends the same request** if the token changed.
2. **A non-bumping, compare-and-swap token write.** The new
   `IUserMcpConfigGrain.RecordOAuthTokensAsync(serverId, expectedCiphertext, newCiphertext)` writes only
   when the stored ciphertext is still the expected one, and it **never** bumps the version. The version
   keys a cache of live sockets, so it belongs to the same class of write as `RecordToolNamesAsync`.
3. **Only `invalid_grant` means "sign in again"** (RFC 6749 §5.2). That case marks the bundle
   `reauth_required_at`, inside the encrypted blob. A 5xx, a timeout or a DNS failure marks nothing.
   Refreshes are single-flighted per (user, server) in the singleton resolver, which re-reads the current
   bundle before it refreshes.
4. **A live-only `McpReauthRequired` stream event.** The UI shows it as a transcript block with a
   Reconnect button that opens the OAuth flow **in a new tab**. It is emitted at most once per activation
   per server and only on interactive runs. It is never persisted and never replayed (ADR 0080 replays tool
   calls only). A settings row shows `⚠ reconnect needed`.

## Alternatives considered

- **Keep connect-time refresh and add time-based cache eviction.** Rejected. Every eviction tears down
  sockets and re-runs the sequential 10 s per-server connect. The refresh would still only happen on a
  miss, and a token that expires between misses would still be dead.
- **Use the SDK's refresh (`ClientOAuthOptions.TokenCache` in `ModelContextProtocol` 2.2.0, checked
  2026-09-21).** Rejected. Its internal `ClientOAuthProvider.GetAccessTokenAsync` refreshes on a 401, but
  when the refresh fails it falls back to the **interactive** redirect. It also has no `invalid_grant`
  classification, no single-flight across our retained connections, and no CAS against our store. These
  are 0025's reasons, and they still hold.
- **Mark the server on any refresh failure.** Rejected. A transient token-endpoint blip would ask the user
  to consent again, which is worse than the bug being fixed.
- **A trailing `StoredMcpServer` field for the mark.** Rejected. It would need a `Keep`-clause decision
  and a Cosmos JSON round-trip test, only to make the mark readable without decryption. The bundle is what
  0025 designed for runtime OAuth state, and a fresh consent clears the mark for free.
- **An inline markdown link, or a hint the model relays, instead of a button.** Rejected by the user in
  the 2026-09-21 interview. The model could invent a URL, and a link that navigates away loses the open
  chat (the active conversation is not in the URL).

## Consequences

- **The objection 0025 made still holds in part, and it is paid knowingly.** 0025 deferred per-request
  refresh because it "would need the grain-persist coupled into the connection handler". The handler now
  does persist, through provider → resolver → grain. What stays true is narrower: the factory **type** is
  still grain-free because the caller builds the provider. The coupling is worth it because the cache
  makes connect-time refresh unreachable.
- A refreshed token no longer bumps the version, so 0025's "one extra reconnect on the next activation"
  is gone as well, and so is ADR 0064's accepted consequence that a Test on a near-expiry OAuth server bumps
  the version (0064 carries the amendment banner). A Test now calls the resolver with `force: true`, the one
  caller that retries a marked bundle.
- **Only InteractiveOAuth servers get the notice.** `BuildAgent` wraps only their tools (the
  `McpServerTools.InteractiveOAuth` flag): a 401 from a Header-mode server is a configuration fault, and a
  Reconnect button would send the user nowhere.
- **Evidence from the 2026-09-21 spike on 2.2.0:** a tool call that is answered with a 401 throws a bare
  `HttpRequestException` with `StatusCode == Unauthorized`. The grain-side `ReauthNoticeMcpTool` matches on
  that. Resending the same `HttpRequestMessage` (its content is `JsonContent`) from a `DelegatingHandler`
  succeeds.
- **Accepted residual:** if the token endpoint is down *after* the access token has expired, the retry
  cannot help. The 401 stands and the turn shows Reconnect. That is honest, because the credential really
  is unusable right now, and consenting again is harmless.
- **Out of scope:** notices for non-OAuth failures (an unreachable server, a stopped local bridge), which
  stay silent skips by the user's decision; returning to the same conversation after the OAuth round
  trip; auto-retrying the failed turn. Spec: `.agentic/specs/completed/plan-mcp-reconnect-from-the-turn.md`.
