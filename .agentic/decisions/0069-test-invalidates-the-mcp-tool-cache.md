---
id: 0069
date: 2026-09-07
status: accepted
tags: [mcp, cache, diagnostics, no-migration, no-infra]
supersedes: []
extends: []
amends: [0064]
---

# A successful MCP connection test invalidates that user's cached tool set

> **Amends [ADR 0064](0064-mcp-connection-test-endpoint.md).** Nothing is overturned and no endpoint is
> added. `POST /api/mcp-servers/{id}/test` gains one side effect. **ADR 0058's rule that
> `GET /api/tools` never calls `IUserMcpToolProvider` is untouched**, as is ADR 0058's reason that
> `RecordToolNamesAsync` must not bump the version — see the Decision for why this is not a contradiction
> of that rule but its complement.

## Context

`UserMcpToolProvider` caches a whole `McpToolSet` per user, keyed on the overlay `Version`, and a cache hit
**returns without connecting at all**. The version is bumped only by a *configuration* write
(`SaveAsync`/`DeleteAsync`); `RecordToolNamesAsync` deliberately does not bump it, because it runs on every
activation from the provider itself and bumping there would tear down every warm grain's live MCP sockets
(ADR 0058).

The consequence was that two states could not be recovered without an unrelated config write:

1. **A server's tool list changed remotely** — a tool added to or removed from a server we already know about
   is never seen. Both MCP servers in the sibling `Expecho/mcpserver` repo are under active development, so
   this is routine, not hypothetical.
2. **A server was unreachable when the version was first resolved.** `ConnectAllAsync` records a skipped
   server as an **empty group**, and that whole set is cached under the version. The server recovers; the
   user keeps getting no tools from it. This one needs no tool-list change at all — a transient blip becomes
   a persistent outage.

ADR 0064 shipped a Test button whose whole purpose is to tell the user whether their server works. In state
2 it reports **"connected, 12 tools"** while the chat path serves nothing. The diagnostic and the behaviour
disagree, and **both are correct** — which is the worst failure mode a diagnostic can have short of a false
negative (the thing ADR 0064 went to some length to avoid by sharing `McpOAuthTokenResolver`).

## Decision

Add `IUserMcpConfigGrain.InvalidateToolCacheAsync()` — it bumps the version and changes nothing else — and
call it from `McpConnectionTester` **after a successful test that found at least one tool**.

**Why this is not a contradiction of ADR 0058's non-bumping rule.** That rule is about *who* is calling and
*how often*: `RecordToolNamesAsync` runs on every activation, from the provider, as a side effect of ordinary
discovery — bumping there would make discovery destroy the thing it discovered. This runs only when a human
pressed Test: once, deliberately, against one server. It is exactly the distinction ADR 0064 already drew to
let a user-pressed POST connect where `GET /api/tools` may not.

**Bump, not targeted eviction.** The cache holds live sockets, and disposing them out from under a warm grain
is unsafe while retaining them without disposal leaks. A version bump reuses the only invalidation path that
already handles both: the new set is stored under a new version, and the old one's connections are disposed
by `EvictBeyondAsync` once it falls beyond the retained 2. The blast radius is per **overlay**, so it
re-resolves all of that user's servers — the same blast radius every config save already has.

**Three carve-outs, each load-bearing:**

- **A failed test does not bump.** It learned nothing, and a bump costs every warm grain of that user a
  reconnect.
- **A zero-tool connect does not bump** (and, per ADR 0064, does not record either). Re-resolving to an empty
  tool set would strip a working chat of that server's tools on a transient empty answer — the same hazard as
  ADR 0064's `[]`-erases-the-names, one layer down.
- **The bump is NOT conditional on the names having changed.** This is the one most likely to be "optimized"
  away, because `RecordToolNamesAsync` right next to it *is* conditional. State 2 above is a recovered server
  reporting **exactly the names it always had** — gating on a name change leaves the case this exists for
  broken forever.

## Alternatives considered

- **A separate `POST /api/mcp-servers/{id}/refresh`.** Rejected. It needs its own UI or it is curl-only (the
  `POST /api/search/reindex` wart ADR 0061 records), and it would leave Test still reporting healthy while
  the chat is stale — the actual defect — unless the user knew to press a second button. Making the existing
  diagnostic *true* is both smaller and more correct.
- **Bump on every test, including failures.** Rejected: a failing test tells us nothing new, and the cost is
  real.
- **Invalidate the provider's cache directly** rather than through the version. Rejected: it would require a
  new disposal path for connections a warm grain may still hold, re-deriving `EvictBeyondAsync`'s retention
  reasoning badly.
- **Do nothing; document the re-save workaround.** Rejected — it is undiscoverable, and nothing in the UI
  hints at it.

## Consequences

- Pressing Test now costs that user's warm grains one reconnect on their next activation. Accepted: it is a
  user-pressed action, and ADR 0064 already accepted exactly this cost for the near-expiry OAuth case.
- Test is run automatically after a successful save (ADR 0064), and a save already bumps — so that path now
  bumps twice. Harmless: the version is a monotonic counter and only one resolve follows.
- **It only fixes the first of the two caches.** `AgentGrain._mcpServers` is an activation snapshot, so this
  gives "correct on the next activation", not "correct now". A warm chat stays stale until the grain
  deactivates. Fixing that means the ADR 0066 deferred-rebuild shape and is deliberately not attempted here.
- No new endpoint, no frontend change, **no service-worker cache bump**, no new Cosmos container, no secret,
  no Bicep and no `excludedPaths` change. No migration: the new grain method writes the same state shape.

## Verification

`dotnet test Agent42.slnx` — 1233/1233 green; `dotnet format --verify-no-changes` clean.

Four new tests in `tests/Agent42.Tests/McpConnectionTesterTests.cs`
(`ASuccessfulTest_BumpsTheVersion_SoTheToolCacheIsReResolved`, `ASecondTestWithUnchangedNames_StillBumps`,
`AZeroToolConnect_DoesNotBump`, `AFailedTest_DoesNotBump`) and one in
`tests/Agent42.Tests/UserMcpConfigGrainTests.cs`
(`InvalidateToolCache_BumpsTheVersion_AndLeavesTheServersAlone`).

**Follow-up (2026-09-08): the composition is now executed, not read.** As shipped, this ADR's tests asserted
only that the version *bumps*, while `UserMcpToolProviderTests` separately asserted that *a bump reloads* —
nothing joined the two, so the chain the feature actually is had never run. Two tests in
`tests/Agent42.Tests/UserMcpToolProviderTests.cs` now drive it end to end through the real provider:
`ARecoveredServer_StaysEmpty_UntilInvalidation` (the motivating case) and
`AChangedToolList_IsInvisible_UntilInvalidation`. Each **pins the defect in a middle assertion** before
asserting the fix — without that, both would pass against a provider that simply always reconnected, proving
nothing about invalidation. **Mutation-checked:** stubbing `InvalidateToolCacheAsync` to `Task.CompletedTask`
fails all five of this ADR's tests, both new ones included.

**Unverified residual, narrowed to what it always really was:** every test still drives a **stubbed**
`IMcpConnectionFactory`, so the MCP **wire** — a real server over a real socket reporting a changed tool
list — remains unexercised here, as it has been for every MCP feature since ADR 0024. What is no longer
unverified is Agent42's own behaviour, which is what this ADR changed. Also still unobserved: an
`AgentGrain` picking the new set up on its next activation, because the test silo injects
`SingleUserMcpToolProvider` rather than the real provider. A live run against `Expecho/mcpserver` on
loopback via the AppHost (DevHost cannot — it fakes both MCP seams) would close both and has not been done.
