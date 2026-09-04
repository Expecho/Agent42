---
id: 0064
date: 2026-09-04
status: accepted
tags: [mcp, diagnostics, ux, endpoint, no-migration, oauth]
supersedes: []
extends: [0025, 0029]
amends: [0024, 0026, 0058]
---

# Tell the user whether a newly added MCP server actually works

> **Amends [ADR 0024](0024-config-driven-remote-mcp-servers.md), [ADR 0026](0026-per-mcp-server-selectable-tools.md)
> and [ADR 0058](0058-per-mcp-tool-selectable-tools.md).** Nothing is overturned. What changes is narrow and
> must be named exactly: ADR 0024's management endpoints were **pure config CRUD that never opened a socket**,
> and **one of them now does, on explicit user request**. ADR 0058's rule that `GET /api/tools` never calls
> `IUserMcpToolProvider` is **unchanged and still load-bearing**; ADR 0058's recorded **cold-start
> consequence** is what this closes.

## Context

`POST/PUT /api/mcp-servers` validates only *structurally* (`ValidateMcpServer`): absolute http/https URL, a
parseable auth mode, the fields that mode needs. A typo'd host saves cleanly. The first real connection
happens lazily on the next `AgentGrain` activation inside `UserMcpToolProvider.ConnectAllAsync`, where a
failure is best-effort by design — a `LogWarning` and an empty tool group, so one dead server never breaks
activation or the rest of the tool set.

The result is that the only user-visible signal is the picker's `"tools not discovered yet"`, which reads
identically for four different situations: *"I haven't opened a chat yet"*, *"the server is down"*, *"the URL
is wrong"* and *"I never completed the OAuth Connect flow"*. The user has no way to ask.

## Decision

A **user-pressed** `POST /api/mcp-servers/{id}/test` that connects to one stored server, lists its tools,
records the discovered names, and disconnects. It is reached from the edit dialog's **Test** button and runs
**automatically after a successful save** and **on the interactive-OAuth return**.

### Why this does not weaken ADR 0058's rule

ADR 0058 established that `GET /api/tools` never connects, for two measured reasons: `ConnectAllAsync` is a
sequential `foreach` with a 10 s per-server timeout behind a per-user semaphore (three dead servers ⇒ ~30 s
before the picker is usable), and resolving an InteractiveOAuth token **refreshes and saves it**, so the GET
would become a **write** that bumps the overlay version and invalidates the socket cache on a page load.

Neither objection survives the move to an explicit, user-pressed POST on **one named** server: the latency is
the thing being asked for, and a write on a POST is unremarkable. The rule was never "never connect" — it was
"never connect *on a page load, for every server, implicitly*". That rule stands, unweakened: `GET /api/tools`
still reads persisted `KnownToolNames` and never calls `IUserMcpToolProvider`.

### Shape

- **`McpOAuthTokenResolver`** (new) — `ResolveOAuthAccessTokenAsync` lifted out of `UserMcpToolProvider`, which
  now takes it by constructor and delegates. Behaviour unchanged.
- **`McpConnectionTester`** (new) — the probe. Loads the overlay, finds the server (a `null` result is the
  endpoint's 404), resolves the OAuth token via the shared resolver, decrypts secrets, connects under a 10 s
  linked CTS mirroring `UserMcpToolProvider.ConnectTimeout`, lists tools, records non-empty names, and
  **disposes the connection in a `finally`**.
- **The endpoint** resolves the tester **optionally out of `IServiceProvider`** — the `/api/speech` pattern —
  and 503s where it is absent. Taking it as a minimal-API *parameter* would be treated as a body by
  `MapAgentApi`'s up-front inference and break routing for the whole host.
- **Status codes:** 503 unconfigured · 404 unknown-or-foreign id · **200 with `ok:false`** for a
  reachable-but-failing test. The request succeeded; the diagnosis is the payload (the config-health
  convention).

### The token resolver is shared, not re-implemented

`ResolveOAuthAccessTokenAsync` refreshes a token within a 2-minute skew of expiry and persists the refreshed
bundle. A test path that skipped that refresh would report **failure on a server the chat path connects to
fine** — a false negative in a diagnostic tool, the worst defect this feature could ship. So it is extracted
and shared, and there is exactly one implementation of the rule.

**Accepted consequence:** pressing Test on an OAuth server with a near-expiry token **bumps the overlay
version and invalidates that user's warm MCP socket cache**. Bounded, rare, deliberate — and exactly what the
next activation would have done anyway.

### Never record an empty tool list

`UserMcpConfigGrain.RecordToolNamesAsync` no-ops when the list is **unchanged**, not when it is **empty**.
`[]` differs from `[A,B]`, so recording an empty list would **erase** the names. ADR 0058 rests on two
invariants — *"names are never erased"* and *"per-tool keys cannot exist before names do"* — and erasing makes
its unreachable state reachable: the picker renders the server as a bare checkbox reading "tools not
discovered yet" with the master **unchecked**, while the client preservation rule keeps the stored
`mcp:{id}:{tool}` keys. A server that reads OFF while still granting a tool, one tick from silently
re-granting a deselected one.

So the tester records **only a non-empty list**. A successful connect returning zero tools is reported to the
user (`"connected, but the server exposes no tools"`) and writes nothing.

**Observed, deliberately not fixed here:** `UserMcpToolProvider` has the identical hole on its own path — it
records whatever it discovered, including `[]`. It is pre-existing, fires only on an activation, and fixing it
is a behaviour change to the chat path, so it belongs in its own MR. Guarding inside the grain was considered
— it is the true one-source fix — and rejected for the same reason.

### Curated failure text only

The MCP server's response body is response-controlled. The browser gets a reason from a **closed set of six**,
none of which is derived from the server's response: `ok` · `ok, but this server is switched off` ·
`connected, but the server exposes no tools` · `not connected (no OAuth token yet)` · `could not connect` ·
`auth mode {x} is not yet supported`. The detail goes to `LogWarning(ex, …)`. This adds no exposure —
`UserMcpToolProvider` already logs the same detail on every failed connect, so `/logs` (ADR 0054) is unchanged.

The last of the six is the only one that interpolates anything, and what it interpolates is the **stored
`Auth` string of the user's own server** — never upstream content. It also mirrors the provider's own `Skip`
branch and is effectively unreachable for any server saved through the API, since `ValidateMcpServer` rejects
an unparseable auth mode at write time. It is kept as defensive symmetry with the activation path, not as a
live diagnosis.

### Client rules

- **One renderer, two targets.** The dialog has its own result line; the OAuth return has no dialog open, so
  the MCP tab needs one. `#mcpTabResult` sits **outside** `#mcpServerList`, because `renderMcpServers()` clears
  that list's `innerHTML` and `openProfileDialog()` calls `loadMcpServers()` un-awaited — a result rendered
  inside it would be wiped by the in-flight render.
- **The dialog stays open after a save** so the auto-test has somewhere to report, and Cancel relabels to
  Close. Which forces the next rule.
- **The duplicate-create trap.** `POST /api/mcp-servers` **always inserts** (the id is server-minted). With the
  dialog staying open, it **must** adopt the response id and flip into edit mode, or a second Save silently
  creates a duplicate. No server-side guard exists; that one line is the whole defence.
- **The save path's `refreshToolCatalog()` stays unconditional** (ADR 0026): a save whose auto-test then fails
  must still update the pickers, because the server's own checkbox is driven by the save, not by reachability.

## Alternatives considered

- **A row on `ConfigHealth.ProbeAsync`** (the "Run live checks" button, ADR 0029) — this repo's existing home
  for user-triggered reachability probes, and MCP is conspicuously absent from it. Rejected because that path
  probes **operator** configuration with no per-item target: it answers "is geocoding configured", not "does
  *this* server of *mine* work". The diagnosis here is per-server, per-user, and belongs beside the thing being
  diagnosed — the edit dialog — not on a different tab behind a global button.
- **A per-row Test action** in the server list. Declined by the user: reached from the dialog and automatically
  after a save is enough, and it keeps the row quiet.
- **Persisting the result** on `StoredMcpServer`. Rejected: a stale "ok" from three days ago reads as "ok
  now", and it would add trailing `[Id]`s to an Orleans-persisted record for cosmetic state. The result is
  **ephemeral**.
- **Test before save.** Rejected: the endpoint takes a stored id because a blank secret means "keep", which
  cannot be resolved from an unsaved form.
- **Periodic/background health checking.** Out of scope. User-pressed only.

## Consequences

- **The cold start is closed.** A successful test records the names, so the per-tool checkboxes (ADR 0058)
  appear without waiting for a chat activation. Measured in DevHost: a tested server yields
  `mcp:{id}`, `mcp:{id}:demo_search`, `mcp:{id}:demo_delete` from `GET /api/tools` while an untested control
  yields only `mcp:{id}`.
- **One management route now opens a socket.** That is the ADR 0024 property being amended. It is
  user-pressed, one server at a time, bounded at 10 s, and disposed in a `finally`.
- **No new egress for an enabled server.** The test dials the same server the chat path already dials, with
  the same credentials; it changes *when* that happens, not *whether*.
  **A disabled server is the exception, and it is bounded deliberately.** `UserMcpToolProvider` connects only
  to enabled servers (`Where(s => s.Enabled)`), so a disabled server's decrypted header value or OAuth token
  has **never** reached its endpoint. Pressing **Test** on one now sends it — accepted, because diagnosing a
  switched-off server is exactly what the button is for and the press is explicit. What is **not** accepted is
  reaching it without a press, so **the auto-test on save is gated on the Enabled checkbox**: saving a
  disabled server renders "not tested — this server is switched off" and makes **zero** network calls. Without
  that gate, saving a server with the box unchecked would have delivered its credentials to a host the app had
  never contacted, on a path the user never asked for.
- **No infra change at all:** no `excludedPaths` entry (the route stays behind Easy Auth, with a new
  `EasyAuthExclusionTests` row proving it), no Bicep, no secret, no Cosmos container, no new folder and no new
  `AgentApi.*` partial.
- **A disabled server is still testable** — the user is diagnosing it — and the outcome says it is off, or
  "ok" would read as "and it is in use". The follow-up `refreshToolCatalog()` is a no-op for it, since
  `GET /api/tools` emits a descriptor only per management-enabled server.

## Verification

- 1163/1163 tests green; `dotnet format --verify-no-changes` clean; `dotnet build` clean (S3776 at `error`).
- **The DevHost browser leg was run** (headless Chrome over CDP, the ADR 0058/0052 precedent): 13/13 checks,
  including the C9 duplicate-create defence verified live (6 servers before a second Save, 6 after) and the
  failure leg rendering `"✗ could not connect"` with no exception text.
- Two client rules have **no runtime test** and are guarded by source text only, as ADR 0058's
  collapse/preservation rules were — the duplicate-create defence and the unconditional save-path
  `refreshToolCatalog()`. **Both were mutation-checked**: removing the id assignment fails
  `SaveFlow_AdoptsTheResponseId_SoASecondSaveDoesNotDuplicate`, and moving the refresh after the auto-test
  fails `SaveFlow_RefreshesTheToolCatalogUnconditionally`.

### Unverified residuals

- **DevHost's `IMcpConnectionFactory` is an offline fake.** It is registered precisely so the feature is
  exercisable offline (without it every Test press offline returns "could not connect" and no leg is
  reachable), but it proves the **wiring**, not an MCP handshake. The real `McpConnectionFactory` wire path
  stays production-verified, as it has been since ADR 0024.
- **The InteractiveOAuth auto-test on the callback return** is covered server-side (the redirect carries
  `serverId`, asserted by `Callback_RedirectCarriesTheConnectedServerId`) and client-side by source text, but
  the full round trip needs a real authorization server and was not run.
