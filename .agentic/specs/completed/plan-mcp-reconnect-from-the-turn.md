# Plan — detect a dead MCP OAuth token and offer the reconnect inside the turn

planned-against: main @ 4ccc7e1

## Overview

An InteractiveOAuth MCP server (the user's Google Calendar / Gmail servers) silently loses its tools
and the only way back is Preferences → MCP → Connect. Two distinct defects hide behind that one
symptom:

1. **The refresh can never run on its own.** The access token is stamped once per connection as a
   static header, and `UserMcpToolProvider`'s cache is keyed **only** on the config-grain version, so
   a new activation is a cache hit that returns the same socket with the same expired bearer. The
   refresh lives on the cache-*miss* path, so nothing refreshes until a config write or a restart.
   ADR 0025 assumed "a mid-connection-life expiry is picked up next activation"; it is not.
2. **A genuinely dead grant is indistinguishable from a dead server.** A rejected refresh is swallowed
   as a warning and the server is skipped with no signal anywhere the user looks.

This plan fixes (1) so the common case never reaches a human, classifies (2) so the rare case is named,
and puts a **Reconnect button in the transcript** for the one failure a human must resolve.

## Affected areas

Verified against the tree at `4ccc7e1`.

| File | Change |
|---|---|
| `.agentic/decisions/0081-*.md` | NEW — amends ADR 0025's deferred per-request refresh |
| `src/Agent42.Backend/Mcp/McpOAuth.cs` | `McpOAuthTokens.ReauthRequiredAt`; typed `invalid_grant` failure |
| `src/Agent42.Backend/Mcp/McpOAuthTokenResolver.cs` | returns a *state*, re-reads the bundle, non-bumping persist, classification |
| `src/Agent42.Backend/Mcp/McpAuthHeader.cs` | NEW `InteractiveOAuthTokenProvider : IAuthHeaderProvider` |
| `src/Agent42.Backend/Mcp/McpConnectionFactory.cs` | InteractiveOAuth → `AuthHeaderHandler` instead of a static header |
| `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` | builds the token provider; carries the state onto the group |
| `src/Agent42.Backend/Mcp/McpConnectionTester.cs` | curated "needs reconnect" reason; user-pressed retry |
| `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` | `Mask` gains the protector; `NeedsReauth` on the DTO |
| `src/Agent42.Backend/Mcp/McpContracts.cs` | `McpServerResponse.NeedsReauth` |
| `src/Agent42.Agents/Mcp/IUserMcpConfigGrain.cs` + `UserMcpConfigGrain.cs` | NEW non-bumping `RecordOAuthTokensAsync` |
| `src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs` | `McpServerTools` gains `ServerName` + `ReauthRequired` |
| `src/Agent42.Agents/Mcp/ReauthNoticeMcpTool.cs` | NEW grain-side wrapper (mirrors `UrlHarvestingMcpTool`) |
| `src/Agent42.Agents/Conversations/AgentStreamEvent.cs` | `McpReauthRequired` event type + `McpServerId` |
| `src/Agent42.Agents/Conversations/AgentGrain.cs` | notice emission, activation set, `_mcpVersion`, mid-turn re-resolve, shared rebuild helper |
| `src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs` (`SingleUserMcpToolProvider`) | settable `Version` (today hard-coded `0`) so a test can simulate a reconnect |
| `tests/Agent42.Tests/SharedClusterFixture.cs` | reset that `Version` in `ResetSiloState` (ADR 0055, property 2) |
| `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` | wraps MCP tools with the reauth notice |
| `wwwroot/js/mcp.js`, `thread.js`, `send.js`, `styles.css`, `sw.js` | the button, the row badge, CACHE bump |
| `.agentic/gotchas.md`, `CLAUDE.md` | two entries now describe reversed behaviour |

## Context & constraints

Findings the executor should not have to re-derive (each with its evidence path):

- **The cache is the bug, not the refresh skew.** `UserMcpToolProvider.cs:58-70` (`GetForUserAsync`
  reads the version, `cache.TryGet(version)` returns before connecting) + `McpOAuthTokenResolver.cs:49`
  (refresh only inside the connect path) + `UserMcpToolProvider.cs:28` (eviction is by *count*, never
  by time). No miss → no refresh → no write → no miss.
- **`McpConnectionFactory` is deliberately grain-free** (ADR 0025 rejected per-request refresh precisely
  to keep it so). Preserve that: the **caller** (`UserMcpToolProvider` / `McpConnectionTester`, both of
  which already hold `IGrainFactory` and the resolver) constructs the `IAuthHeaderProvider` and passes it
  on `McpConnectionSecrets`; the factory only wires it into `AuthHeaderHandler`. This is why the ADR's
  stated objection no longer applies and the reversal is honest rather than a shrug.
- **Refresh from a snapshot is unsafe once refreshes are per-request.** `ResolveAsync` today refreshes
  from the `StoredMcpServer` handed to it at connect time (`McpOAuthTokenResolver.cs:44`). With two
  retained cache versions there can be two live connections per server; if the authorization server
  rotates refresh tokens, the second one refreshes from a stale token and gets a **false** `invalid_grant`.
  The per-request path must re-read the current bundle from the config grain before refreshing.
- **Only `invalid_grant` means "a human must sign in again."** RFC 6749 §5.2. A 5xx, a timeout or a DNS
  failure must never mark the server — a transient blip that nags the user to re-consent is worse than
  the bug being fixed. `PostTokenAsync` (`McpOAuth.cs:191-204`) currently flattens every failure into one
  `InvalidOperationException` carrying the response body; it needs a typed case.
- **Non-bumping writes are an established shape, and the reason matters.** `RecordToolNamesAsync`
  (`UserMcpConfigGrain.cs:140-186`, and the long doc comment on `IUserMcpConfigGrain.cs:52-68`) exists
  because the version keys a cache of **live sockets**. A refreshed token is in the same class: writing it
  must not tear down every warm grain's connections. This also retires ADR 0025's "one extra reconnect on
  the next activation" churn.
  ⚠ Unlike `RecordToolNamesAsync` it is a **compare-and-swap**: `RecordOAuthTokensAsync(serverId,
  expectedCiphertext, newCiphertext) → bool`, writing only if the stored ciphertext string still equals
  `expectedCiphertext`. The resolver's read → token POST → write is not atomic, and the OAuth callback's
  `SaveAsync` (`AgentApi.Mcp.cs:384`) can land inside it; an unconditional write would overwrite the
  user's fresh re-consent with a stale (possibly marked) bundle and nag them again. Re-encryption yields
  new bytes each time, but the *stored* string compares fine as-is.
- **Refreshes are single-flighted per (userId, serverId) in the resolver**, which is already a
  process singleton (`Program.cs:183`, `DevHost/Program.cs:143`). A gate per provider instance is not
  enough: two retained cache versions plus the Tester's `force` path can each hold a provider for the
  same server, and a rotating authorization server with reuse detection turns that race into a false
  `invalid_grant`.
- **Two turns can be in flight on one activation** (`AgentGrain.cs:230-256`): `ChatAsync` suspends between
  `yield return`s, and an `EnqueuePromptAsync` run (which drains the same `ChatCoreAsync` and discards its
  events, `:554`) or a reminder executes in the gap. So nothing the notice needs may live in a
  turn-scoped *field*, and the mid-turn rebuild must never run from a background drain — the same reason
  `:248-256` gives for why a rebuild "cannot happen inside the tool".
- **The grain must keep its hands off `IUserMcpConfigGrain`** (verified: zero matches under
  `src/Agent42.Agents/Conversations/` and `Tools/`). That is what keeps one source of MCP truth
  (gotchas ~line 1629). So: persistence of the "needs re-consent" mark lives in the **Backend** (the
  resolver); the **in-turn notice** lives in the grain and is fed by the provider's projection and by a
  grain-side tool wrapper. Do not "simplify" by having the grain write the mark.
- **`McpServerTools` is not an Orleans-serialized type** (`IUserMcpToolProvider.cs:11-13`) — extending it
  needs no `[Id]`s and no migration.
- **No new persisted grain field is needed.** The mark rides the already-encrypted `TokenCiphertext`
  bundle as a new JSON property, which is what ADR 0025 designed that blob for. Consequences: it is
  cleared for free when a fresh bundle is written on re-consent, and it costs nothing in Orleans state —
  no trailing `[Id(n)]`, no Cosmos `$type` exposure (gotchas ~line 2134). Rejected alternative: a trailing
  field on `StoredMcpServer`, which would need a `Keep`-clause decision (the `BridgeKeyHash` trap,
  gotchas ~line 3458) and a JSON round-trip test, for the sole benefit of being readable without decryption.
- **A tool-call 401 must be caught where the grain can see it.** `WithUrlSeeding`
  (`AgentGrain.BuildAgent.cs:318-321`, applied at `:190` *after* the per-tool filter) is the exemplar: a
  grain-constructed delegate whose callback closes over `this`. Copy that shape. The Backend-side
  `TruncatingMcpTool` is the wrong seam for the same reason ADR 0067 gave — its instances are shared
  across every conversation of the user.
- **The reconnect must open a new tab.** The active conversation lives only in a module variable
  (`conversations.js:16`, `export let activeId`) — nothing puts it in the URL — so the settings flow's
  full navigation (`mcp.js:24`) would drop the user's open chat and return them to `/` with Preferences
  open. From the transcript, `window.open(url, "_blank")`; the chat tab is untouched and the next message
  picks the tools up via the mid-turn re-resolve.
- **Markdown/DOM plumbing already supports this.** The stream is NDJSON with camelCase enum names
  (`AgentApi.Conversations.cs:160-165`), dispatched by an if-chain in `send.js:204-236`; `thread.js`
  already renders non-message notices in the transcript (`markStopped`, `thread.js:135-150`) — the
  Reconnect block is that shape.
- **The SDK has moved since ADR 0025 was written** — that ADR reasons about `ModelContextProtocol` 1.4.1;
  `Directory.Packages.props:44` now pins **2.2.0**. In Step 0 (before the ADR is written), check whether 2.2.0's
  `ClientOAuthOptions` exposes a non-blocking token-refresh seam worth using instead of our own handler
  (the *interactive* dance stays hand-rolled either way — that objection is about the redirect delegate,
  not refresh). Also read the 2.2.0 gotcha ("`server/discover` probe waits 5 s", `TransportBase` must
  `SetDisconnected`) before touching transports.
- **Decisions taken in the interview (2026-09-21):** a dedicated stream event with a real Reconnect
  button (not an inline markdown link, not a model-relayed hint); all three parts in **one** MR; the
  notice covers **only** OAuth re-consent — unreachable servers and a stopped local bridge stay silent
  skips exactly as today.
- Governing docs to read before touching each area: `.agentic/decisions/0025-*` (the decision being
  amended), `0024`, `0026`, `0058`, `0064`, `0069`, `0067` (the wrapper exemplar), `0076`, `0080`
  (replay — the new event is *not* replayed); `.agentic/gotchas.md` entries "Remote MCP servers: a
  live-socket cache…", "MCP interactive OAuth is hand-rolled…", "A grain's turn-scoped field lives for
  the ACTIVATION…", "Every MR that touches `wwwroot/js/` must bump `CACHE`…".

## Steps

**Step 0 — Check the SDK first.** Before anything is written, confirm whether `ModelContextProtocol`
2.2.0 offers a non-blocking refresh seam (see Context). If it does and it fits, stop and revise Steps 1/4
with the user; the ADR must describe the design actually built. Also run the Step 6 exception-shape
spike here — its answer decides whether Step 4's handler can see the 401 at all.
*Verify:* a short written note of both answers (API names, observed exception type) in the MR description.

**Step 1 — Record the decision (ADR 0081).** Run `/decide`. This is a **partial supersession** of ADR
0025 (precedent: 0017/0018), with `amends: [0025]` in the front matter as 0080 does — ADR 0025's
*Decision* paragraph itself says "refreshes at connect time … static `Authorization: Bearer`", and the ADR
README forbids editing an accepted decision. Be honest about the objection: 0025 deferred this because
grain-persist would be "coupled into the connection handler", and **the handler now does persist**,
through provider → resolver → grain. What stays true is narrower: the factory *type* remains grain-free
because the caller builds the provider. Record why that coupling is now worth paying (the cache makes
connect-time refresh unreachable). Also record: the non-bumping CAS token write, `invalid_grant`-only
classification, and the live-only `McpReauthRequired` event.
*Verify:* the new ADR exists, `/decide`'s conflict grep names 0025, and `dotnet test --filter DocPath`
(doc-path integrity) passes.

**Step 2 — The bundle and the non-bumping write.**
- `McpOAuthTokens` gains a trailing `[property: JsonPropertyName("reauth_required_at")] DateTimeOffset?
  ReauthRequiredAt = null` — an older bundle deserializes it as null.
- `IUserMcpConfigGrain.RecordOAuthTokensAsync(string serverId, string expectedCiphertext, string
  newCiphertext) → Task<bool>` + impl next to `RecordToolNamesAsync`: false on an unknown server or a
  ciphertext mismatch (compare-and-swap, see Context), **never** `BumpAsync`, `[Alias]` on the method.
  Document *why* it does not bump by pointing at the live-socket cache, not at write frequency, and
  update the `GetVersionAsync` doc comment, which currently names `RecordToolNamesAsync` as the exception.
*Files:* `McpOAuth.cs`, `IUserMcpConfigGrain.cs`, `UserMcpConfigGrain.cs`.
*Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~UserMcpConfigGrainTests|FullyQualifiedName~McpOAuthClientTests"`.

**Step 3 — Resolver: one rule, three outcomes.** Replace `Task<string?>` with
`Task<McpOAuthResolution>` (`AccessToken`, `State` ∈ `NotConnected | Connected | ReauthRequired`).
- Add `McpOAuthRefreshRejectedException`, thrown by `PostTokenAsync` only for a 4xx whose body carries
  `"error":"invalid_grant"`. Everything else keeps today's `InvalidOperationException`.
- `ResolveAsync(userId, serverId, force = false)` re-reads the server from the config grain; a bundle
  already marked returns `ReauthRequired` **without** contacting the authorization server unless
  `force` (the user-pressed Test path).
- On `invalid_grant`: persist `tokens with { ReauthRequiredAt = now }` via `RecordOAuthTokensAsync`,
  return `ReauthRequired`. On any other refresh failure: log, return the existing token, mark nothing.
  On success: persist the fresh bundle (mark implicitly cleared) via `RecordOAuthTokensAsync`.
  Every write is CAS against the ciphertext read at the start; a lost CAS means someone else (a
  concurrent refresh, or the OAuth callback) wrote first — re-read and use theirs, never retry the write.
- A `SemaphoreSlim` per `(userId, serverId)` (a `ConcurrentDictionary` in the singleton) wraps
  read → refresh → write. Inside the gate, re-read before refreshing: a waiter usually finds the token
  already fresh.
- No stored token → `NotConnected`. Expired with no refresh token → `ReauthRequired`.
*Files:* `McpOAuth.cs`, `McpOAuthTokenResolver.cs`, + both call sites compile-updated.
*Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~McpOAuth"`.

**Step 4 — Per-request bearer.** New `InteractiveOAuthTokenProvider : IAuthHeaderProvider` in
`McpAuthHeader.cs`, beside `ClientCredentialsTokenProvider`: holds `(userId, serverId, resolver)`, caches
the access token + expiry in memory, and asks the resolver (which owns the single-flight gate) when the
token is within skew. While in `ReauthRequired` it asks the resolver on **every** request (cheap: a grain
read, no AS call) so a fresh bundle from the OAuth callback is picked up by a *live* connection; it returns
the last known token otherwise and lets the server answer 401.
**401 → refresh → retry once.** `AuthHeaderHandler` gains an optional retry: on a 401 response it asks
the provider to invalidate and re-resolve (forcing a re-read/refresh), and resends once if the token
changed. This is what recovers a live connection after re-consent elsewhere, and it removes most false
Reconnect buttons: a token that merely expired early gets refreshed silently. The ClientCredentials
provider keeps today's behaviour (retry is opt-in per provider).
⚠ The request body must be re-sendable — check how the SDK's Streamable HTTP transport builds
`HttpRequestMessage` content (buffer it, or clone the request) as part of the Step 0 spike.
**Residual, accepted and documented in the ADR:** if the token endpoint is down *after* the access token
has expired, the retry cannot help, the 401 stands, and the turn shows Reconnect. That is honest (the
credential really is unusable right now) and re-consenting is harmless; not worth plumbing transient
state into the grain.
`McpConnectionSecrets` gains `IAuthHeaderProvider? OAuthTokenProvider`; `McpConnectionFactory` uses
`AuthHeaderHandler` over a **plain `new HttpClient`** for InteractiveOAuth (never DI-resolved — the
`AddServiceDefaults` retry-inheritance rule, CLAUDE.md #8), exactly as the ClientCredentials branch does,
and the static-bearer branch for that mode goes away. Callers build the provider.
*Files:* `McpAuthHeader.cs`, `McpConnectionFactory.cs`, `UserMcpToolProvider.cs`, `McpConnectionTester.cs`.
*Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~McpAuthTests|FullyQualifiedName~UserMcpToolProviderTests|FullyQualifiedName~McpConnectionTesterTests"`.

**Step 5 — Surface the state out of the Backend.** `McpServerTools` gains `ServerName` and
`ReauthRequired` (both trailing, defaulted). The provider skips a `ReauthRequired` server **without
connecting** and records the group with the flag set and `KnownToolNames` intact (the never-erased rule).
The tester gains the curated reason `"authorization expired — reconnect this server"` and calls the
resolver with `force: true`, so a server marked by a one-off upstream lie recovers on a user-pressed Test.
*Files:* `IUserMcpToolProvider.cs`, `UserMcpToolProvider.cs`, `McpConnectionTester.cs`.
*Verify:* same filter as Step 4.

**Step 6 — See a 401 at tool-call time.** New `ReauthNoticeMcpTool` in `src/Agent42.Agents/Mcp/`,
shaped like `UrlHarvestingMcpTool` (a grain-constructed `DelegatingAIFunction`): wraps an `AIFunction`,
catches the 401 exception chain (shape per the Step 0 spike — it fires only after Step 4's retry has
already failed), and returns a plain-text result that **begins with a fixed marker constant** (`McpReauthNotice.ResultMarker`,
the `McpToolLimits.TruncationMarker` idiom) followed by the server id, telling the model the server is
disconnected and the user has been shown a Reconnect button (so the model does not invent a URL). It
takes **no callback** — the turn detects the marker in the `ToolCallEnd` stream it already walks (Step 7),
which keeps all per-turn state in a local.
`BuildAgent` wraps with it **after** the per-tool filter and as the **outermost** wrapper, outside
`UrlHarvestingMcpTool`: nested inside, the harvester would treat the notice text as a successful result,
breaking ADR 0067's "a failing call seeds nothing".
The exception shape comes from the Step 0 spike — match on what `McpClientTool.InvokeAsync` actually
throws, not on a guess.
*Files:* `src/Agent42.Agents/Mcp/ReauthNoticeMcpTool.cs`, `AgentGrain.BuildAgent.cs`.
*Verify:* new unit test over the wrapper + `dotnet test Agent42.slnx --filter "FullyQualifiedName~BuildAgent|FullyQualifiedName~McpUrlSeedingTests"`.

**Step 7 — The event and the turn.**
- `AgentStreamEventType.McpReauthRequired` + `AgentStreamEvent.McpServerId`; `Text` carries the server
  name. **Live-only** — never appended to the reply, never persisted, never replayed (ADR 0080 replays
  tool calls only); document that at the enum member like `Suggestions`/reasoning do.
- `AgentGrain`: one **activation-scoped** `HashSet<string> _mcpReauthNotified` — and **no** turn-scoped
  field. Servers that 401'd this turn are collected in a **local** in `ChatCoreAsync`, by spotting
  `McpReauthNotice.ResultMarker` in the `ToolCallEnd` results the core already walks. A field would be
  wiped or polluted by an interleaved background run (Context: two turns in flight).
- **Everything is gated on the `interactive` local captured at `AgentGrain.cs:614`** — both the emission
  and the insert into `_mcpReauthNotified`, exactly as follow-up suggestions are gated (`:957`). An
  `EnqueuePromptAsync` or `ask_agent` run emits to no one; letting it consume the once-per-activation
  notice would hide it from the user's next real turn.
- Emit at most once per activation per server: before the model call for servers flagged in
  `_mcpServers` that this conversation uses — **any** tool enabled, i.e.
  `KnownToolNames.Any(n => ToolCatalog.IsMcpToolEnabled(id, n, enabled))`, **not** `IsMcpServerEnabled`,
  which misses a partial per-tool selection (`mcp:{id}:{tool}` keys, ADR 0058) and would leave exactly
  those users with a silently dead server — and after the model call for servers that 401'd this turn.
- Mid-turn recovery: keep the resolved version in a new `_mcpVersion` field (today `AgentGrain.cs:363`
  keeps only `.Servers`). At the top of `ChatCoreAsync`, **only when some group is flagged, `interactive`
  is true and `_turnCts is null`** (no other interactive run is live to have its `_agent`/`_session`
  swapped mid-iteration), re-resolve `_mcpToolProvider.GetForUserAsync(_userId)`; if the version changed,
  rebuild the agent preserving the session and clear `_mcpReauthNotified`. Extract the snapshot →
  `BuildAgent` → restore body out of `RebindModelAsync` (`AgentGrain.cs:1503-1515`) into a shared helper
  and call it from both. This follows the `_chatClientFactory` re-resolve precedent at `AgentGrain.cs:625-646`.
- `SingleUserMcpToolProvider` gains a settable `Version` (reset in `SharedClusterFixture.ResetSiloState`)
  so AC17 can be written at all.
*Files:* `AgentStreamEvent.cs`, `AgentGrain.cs`, `IUserMcpToolProvider.cs`, `tests/Agent42.Tests/SharedClusterFixture.cs`.
*Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentGrainTests|FullyQualifiedName~McpReauth"`.

**Step 8 — API + settings row.** `Mask` takes `IMcpSecretProtector` and sets `NeedsReauth` by decrypting
the bundle (null on an undecryptable ciphertext → false); `McpServerResponse.NeedsReauth` trailing and
defaulted. `mcp.js` renders `⚠ reconnect needed` in place of `🔗 connected`, and exports
`mcpOAuthStartUrl(serverId)` — one source for the URL, used by the existing full navigation and by Step 9.
*Files:* `AgentApi.Mcp.cs`, `McpContracts.cs`, `wwwroot/js/mcp.js`.
*Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~McpServersApiTests|FullyQualifiedName~McpServersAssetsTests"`.
*Parallelizable with Steps 6–7 only under `isolation: "worktree"`.*

**Step 9 — The button.** `thread.js` gains `renderMcpReauth(msg, serverId, name)` — a `.msg__reauth`
block in the bubble (the `markStopped` shape) with a Reconnect button calling
`window.open(mcpOAuthStartUrl(id), "_blank", "noopener")`; `send.js` dispatches `mcpReauthRequired`;
`styles.css` gets the block; **`sw.js` CACHE `agent42-shell-v41` → `v42`**.
⚠ `thread.js` importing `mcp.js` closes a module cycle (`mcp.js` → `profile.js` → … ). ESM tolerates it
when the binding is only used inside a function — the existing `thread.js ↔ send.js` cycle proves the
pattern — but confirm the app boots (no `SyntaxError: … does not provide an export named …`, which is a
*blank* app) before moving on; fall back to inlining the URL in `thread.js` if it does not.
*Files:* `wwwroot/js/thread.js`, `send.js`, `styles.css`, `sw.js`.
*Verify:* `dotnet test --filter "FullyQualifiedName~PwaAssetsTests"` (pins the CACHE value) + the new
asset tests + `dotnet run --project tools/Agent42.DevHost` and load the UI.

**Step 10 — Pay the doc debt in the same MR.** Two gotchas entries now describe reversed behaviour:
"MCP interactive OAuth is hand-rolled…" trap (2) (connect-time refresh + the `SaveAsync` bump) and
"Remote MCP servers…" point (5), whose second reason for never calling the provider from `GET /api/tools`
is "resolving a token refreshes and **saves** it, so the GET becomes a write that bumps the version".
That reason dies with Step 2 — **the rule itself does not** (the sequential 10 s-per-server connect stands);
rewrite the justification, do not weaken the rule. Also in "Remote MCP servers…": point (4) calls
`RecordToolNamesAsync` "the ONE write that must not bump" — now false (and already so for
`MintBridgeKeyAsync`); name the class of non-bumping writes instead. The OAuth entry's evidence names a
method that has moved (`ResolveOAuthAccessTokenAsync` → `McpOAuthTokenResolver.ResolveAsync`). In the
turn-scoped-field entry ("family is now four…"), add a line that `_mcpReauthNotified` is deliberately
**activation**-scoped and **not** a member, and why the per-turn 401 set is a local. Add one new entry for
the classification trap (`invalid_grant` only; a transient failure must never mark) and the CAS write.
Add ADR 0081 to the CLAUDE.md feature map. Run `/sync-docs`.
*Verify:* `dotnet test Agent42.slnx` (full suite) + `dotnet format Agent42.slnx --verify-no-changes`.

## Existing tests that must change (expected churn, not breakage)

`McpConnectionSecrets.OAuthAccessToken` is **replaced** by `OAuthTokenProvider`, not kept alongside it —
two ways to pass the same credential is how the connect path and the test path drift apart, which is the
one defect ADR 0064 says this feature cannot afford. Three fakes observe the old field and must instead
pull from the provider (`await secrets.OAuthTokenProvider.GetHeaderValueAsync(default)`):

- `FakeMcpConnectionFactory` — `tests/Agent42.Tests/UserMcpToolProviderTests.cs:371-395` (`:386`, `ObservedOAuthTokens`)
- `FakeConnectionFactory` — `tests/Agent42.Tests/McpConnectionTesterTests.cs:281-304` (`:296`)

(`McpConnectionTestApiTests`' fake does not read the field and only needs to keep compiling.)

Behavioural assertions to re-point, not delete:
`OAuthServer_NearExpiry_RefreshesAndPersists` (`UserMcpToolProviderTests.cs:270-291`) and
`Test_RefreshesANearExpiryToken` (`McpConnectionTesterTests.cs:175-189`) still hold; the refreshed token
now arrives through the provider, and the persisted bundle must additionally be shown **not** to have
bumped the version (AC2/AC3). `McpOAuthTokens`' positional constructor gains a *trailing optional*
parameter, so every existing 6-argument construction keeps compiling.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | An expired access token on a cached connection is refreshed without any config write | `PerRequestBearer_RefreshesAnExpiredToken_OnALiveConnection` | `InteractiveOAuthTokenProviderTests.cs` (new, cluster-backed like `UserMcpToolProviderTests` — `McpAuthTests` has no cluster and the resolver needs the config grain) |
| AC2 | Persisting a refreshed token does **not** bump the overlay version | `RecordOAuthTokens_DoesNotBumpTheVersion` | `UserMcpConfigGrainTests.cs` |
| AC2b | The token write is compare-and-swap: a stale expected ciphertext writes nothing | `RecordOAuthTokens_StaleExpected_WritesNothing` | `UserMcpConfigGrainTests.cs` |
| AC3 | …and therefore does not evict/tear down the cached tool set | `ARefresh_DoesNotInvalidateTheToolCache` | `UserMcpToolProviderTests.cs` |
| AC4 | Concurrent refreshes of one (user, server) — across two providers — hit the token endpoint once | `Resolver_SingleFlights_AcrossProviders` | `McpOAuthTokenResolverTests.cs` (new, cluster-backed) |
| AC4b | A refresh racing the OAuth callback never overwrites the callback's fresh bundle | `ARefreshLosingTheRace_KeepsTheCallbacksBundle` | `McpOAuthTokenResolverTests.cs` |
| AC4c | A 401 triggers one re-resolve and resend; a live connection recovers after re-consent elsewhere | `A401_ReResolvesAndRetriesOnce_RecoveringALiveConnection` | `InteractiveOAuthTokenProviderTests.cs` |
| AC5 | The refresh re-reads the current bundle, not the connect-time snapshot | `Refresh_UsesTheCurrentStoredBundle_NotTheSnapshot` | `McpOAuthTokenResolverTests.cs` (new) |
| AC6 | `invalid_grant` marks the server as needing re-consent | `InvalidGrant_MarksTheBundleReauthRequired` | `McpOAuthTokenResolverTests.cs` |
| AC7 | A 503/timeout from the token endpoint marks **nothing** | `ATransientRefreshFailure_NeverMarksReauth` | `McpOAuthTokenResolverTests.cs` |
| AC8 | A marked server is skipped without connecting, keeps its `KnownToolNames`, and its group says so | `AReauthRequiredServer_IsSkipped_AndFlagged` | `UserMcpToolProviderTests.cs` |
| AC9 | Test reports the distinct curated reason (never an exception or a response body) | `Test_ReportsReauthRequired_WithACuratedReason` | `McpConnectionTesterTests.cs` |
| AC10 | A user-pressed Test retries the refresh and clears a stale mark on success | `Test_ForcesARetry_AndClearsTheMark` | `McpConnectionTesterTests.cs` |
| AC11 | `GET /api/mcp-servers` reports `needsReauth` and still leaks no ciphertext | `NeedsReauth_IsReported_AndNoSecretLeaks` | `McpServersApiTests.cs` |
| AC12 | The settings row renders the reconnect badge | `Row_ShowsReconnectNeeded_ForAMarkedServer` | `McpServersAssetsTests.cs` |
| AC13 | A tool-call 401 yields a result carrying the marker and a model-readable explanation | `A401_ReturnsTheMarkedNotice` | `McpReauthTests.cs` (new) |
| AC13b | The notice wrapper is outermost: a 401 on a seeding server seeds no URL | `A401OnASeedingServer_SeedsNothing` | `McpUrlSeedingTests.cs` |
| AC14 | The turn streams `McpReauthRequired` once per activation per server | `ReauthEvent_IsStreamedOncePerActivation` | `McpReauthTests.cs` |
| AC15 | No event for a server the conversation has not enabled | `ADeselectedServer_RaisesNoReauthEvent` | `McpReauthTests.cs` |
| AC15b | A server with only **some** tools selected (per-tool keys) still raises the event | `APartiallySelectedServer_StillRaisesTheEvent` | `McpReauthTests.cs` |
| AC15c | A non-interactive run (`EnqueuePromptAsync`) neither emits nor consumes the notice; the next interactive turn still gets it | `ABackgroundRun_DoesNotConsumeTheNotice` | `McpReauthTests.cs` |
| AC16 | The event is never persisted and never replayed from history | `ReauthEvent_IsLiveOnly_NotInHistory` | `McpReauthTests.cs` |
| AC17 | After a reconnect elsewhere, the next turn re-resolves and the tools are back with no reactivation | `AReconnect_IsPickedUpMidTurn_WithoutReactivation` | `McpReauthTests.cs` |
| AC19 | The transcript renders a Reconnect button that opens the flow in a new tab | `Transcript_RendersReconnectButton_OpeningANewTab` | `McpReauthAssetsTests.cs` (new) |
| AC20 | The service-worker cache name was bumped | `ServiceWorker_CacheNameIsCurrent` (update pinned value) | `PwaAssetsTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-09-21) — do not recall these numbers:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura × git diff) |
| `cognitive_complexity_max` | **15** | SonarAnalyzer.CSharp **S3776**, build-time `error` |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |
| `add_policy` | latest-stable | n/a — this plan adds **no** packages |

Watch S3776 on `ResolveAsync` (three outcomes × force × classification) and on `ChatCoreAsync`, which is
already long — if either crosses 15, split into a helper the way `HindsightRegistration` does; do not
game the metric. Local enforcement is `soft_warning`, CI is `hard_block`.

## Out of scope

- Returning the user to the conversation they were in after the OAuth round trip (we open a second tab
  instead of navigating away; carrying a conversation id through the OAuth `state` is a separate change).
- Auto-retrying the turn that failed. The user re-asks; the tools are back by then (AC17).
- Any notice for non-OAuth failures — unreachable server, stalled handshake, local bridge off. Explicit
  user decision; they stay silent skips.
- ClientCredentials and LocalBridge auth modes; a background/proactive refresh sweep; a `/api/mcp/refresh`
  route; push-notifying the user that a server went stale.
- Google-specific handling (e.g. `access_type=offline`, or detecting a Testing-status OAuth client whose
  refresh token dies after 7 days). This plan treats Google as one `invalid_grant` producer among many.

## Review

`critic` pass, 2026-09-21. It confirmed the core diagnosis independently: no path re-resolves on its
own. The only other version bumps are a successful Test (ADR 0069) and a local-bridge connect, and a
deploy restarts the container. Those explain "it sometimes fixed itself" and leave the diagnosis
unchanged. It also confirmed that neither the per-request handler nor the non-bumping persist breaks
ADR 0064/0069, and that none of the 20 test names collide.

All 13 findings were **accepted**, and three spot-checked against code first (`AgentGrain.cs:230-256`,
`:554`, `SingleUserMcpToolProvider` version `0`):

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | High | `IsMcpServerEnabled` misses partial per-tool selections, so the notice never fires | Gate on `KnownToolNames` × `IsMcpToolEnabled` (Step 7); AC15b |
| 2 | High | Background runs would consume the once-per-activation notice | Gate emission and insert on `interactive` (Step 7); AC15c |
| 3 | Medium | Turn-scoped field + in-core rebuild vs. two turns in flight | Per-run local via marker in `ToolCallEnd`; rebuild gated on `interactive && _turnCts is null`; old AC18 dropped (could not fail) |
| 4a | Medium | Per-provider gate does not single-flight across providers | Gate per (user, server) in the singleton resolver (Step 3); AC4 |
| 4b | Medium | Unconditional token write can clobber a re-consent | CAS write (Step 2/3); AC2b, AC4b |
| 5 | Medium | Token-endpoint outage still shows Reconnect | Mostly removed by 401 → refresh → retry (Step 4); the remaining case (AS down *after* expiry) accepted and recorded in the ADR |
| 6 | Medium | AC17 unwritable with the fake's fixed version | Settable `Version` + fixture reset + `_mcpVersion` (Affected areas, Step 7) |
| 7 | Medium | No recovery path for a live connection after re-consent | Provider re-asks the resolver every request while `ReauthRequired`, plus the 401 retry (Step 4); AC4c |
| 8 | Medium | AC1/AC4 placed in a cluster-less test class | Moved to new cluster-backed test classes |
| 9 | Low | ADR framing inaccurate | Partial supersession, `amends: [0025]`, honest about the coupling (Step 1) |
| 10 | Low | SDK check sequenced after the ADR | New Step 0 (also runs the exception/re-send spike) |
| 11 | Low | Undercounted doc debt | Four more items added to Step 10 |
| 12 | Low | Wrapper order vs. URL seeding unspecified | Outermost; AC13b; `McpUrlSeedingTests` in Step 6's filter |
| 13 | Nit | Churn listed a third fake that does not read the field | Corrected |

No findings rejected.

## Implementation — where it diverged from the plan

Executed 2026-09-21 on `feature/mcp-reconnect-from-the-turn`, based on `main @ 1863826`. Since `4ccc7e1`, only
the local-bridge release workflow (#160) had changed, and it touched none of this plan's areas.

**Step 0 answers (evidence for ADR 0081):**
- The SDK seam: `ModelContextProtocol` 2.2.0 has `ClientOAuthOptions.TokenCache` (`ITokenCache`). Its internal
  `ClientOAuthProvider.GetAccessTokenAsync` refreshes after a 401, but when the refresh fails it falls back to
  the *interactive* redirect. It also has no `invalid_grant` classification, no single-flight across our
  retained connections, and no CAS against our store. Rejected, so Steps 1 and 4 went ahead as planned.
- The exception shape: a spike (a file-based app with an ASP.NET MCP server on 2.2.0, where a middleware
  answers 401) shows that a `tools/call` answered with 401 throws a **bare** `HttpRequestException` with
  `StatusCode == Unauthorized`. Re-sending the same `HttpRequestMessage` from a `DelegatingHandler` works,
  because its content is `JsonContent`.

**Divergences:**
1. **`McpServerTools` gained a third trailing field, `InteractiveOAuth`.** Without it the grain cannot tell
   which servers' tools to wrap, and wrapping every MCP tool would show Reconnect for a Header-mode 401, which
   is outside this plan's scope.
2. **`ReauthNoticeMcpTool`, `McpReauthNotice` and `McpReauthTurnObserver` are public.** The repo has no
   `InternalsVisibleTo`, and this is the same convention as `SkillCatalog` and `TruncatingSkillsProvider`.
3. **The marker is trusted narrowly.** `McpReauthTurnObserver` accepts a marker only when the call id maps back,
   through its `ToolCallStart`, to a tool of the InteractiveOAuth server the marker names. A fetched page or
   another tool's result that starts with the same prefix could otherwise raise a Reconnect button. It has its
   own test: `AForgedMarker_FromAnotherServersTool_RaisesNoEvent`.
4. **The resolver takes `rejectedAccessToken`.** The 401 → renew path needs "refresh this token now, whatever
   its expiry says" whenever the stored token is still the one the server rejected. When the stored token
   differs (a re-consent happened elsewhere), it is returned without contacting the authorization server.
5. **S3776 extractions.** `UserMcpToolProvider.ResolveOAuthBearerAsync` (the connect loop reached 17), and
   `AgentGrain.TrailingEventsAsync` (the post-reply notices and follow-up suggestions share one seam;
   `ChatCoreAsync` reached 16).
6. **One rule for "needs reauth".** `McpOAuthTokens.RequiresReauth(now)` (marked, or expired with no refresh
   token) is read by the resolver and by `Mask`, so the settings row cannot disagree with the transcript. This
   came from a critic finding.
7. **`RebuildAgentPreservingSessionAsync` has three callers**, not two: the web-document rebuild (ADR 0066) now
   uses it as well.
8. **ADR 0081 amends 0064 as well as 0025.** 0064's "Test on a near-expiry OAuth server bumps the version"
   consequence is retired, so 0064 carries a banner too.
9. **The cache pin is bumped in `SearchAssetsTests` too**, alongside `PwaAssetsTests`.
10. **Step 9's DevHost boot was replaced by a module-graph link check.** The browser extension was not
    connected. Node's `vm.SourceTextModule` linked the whole `main.js` graph (31 modules) cleanly, and it fails
    with `does not provide an export named …` when an import is mutated. `mcp.js` has no top-level statements
    other than declarations, so the new `thread.js → mcp.js` cycle is safe to evaluate. DevHost did start and
    served `/` with a 200. **The Reconnect button's rendering and click still need a manual check in a browser.**
