# Plan — a local bridge that lets Agent42 drive a browser on the user's machine

planned-against: main @ e133f98

## Overview

Let an Agent42 conversation — running on a cloud model, in production — use the MCP tools of a
browser-automation server that listens **only on the user's own machine** (BrowserOS neo today;
Vercel `agent-browser` later). A new console app, **`tools/Agent42.LocalBridge`**, runs on that machine,
dials **out** to Agent42 over an authenticated WebSocket, and relays MCP JSON-RPC between the backend and
the local server. To the rest of Agent42 it is one more user MCP server (ADR 0024) with a new auth mode,
**`LocalBridge`**. Before relaying any tool call that is not marked read-only, the helper **asks the person
at that machine** to confirm.

Why now: the prior research (`.agentic/specs/plan-browseros-neo-as-an-mcp-tool.md`) proved the two cheaper
routes are closed — production cannot reach the user's loopback, and **a browser tab cannot relay either**
(neo returns 403 to any request carrying `Origin`/`Sec-Fetch-*`; measured, see "Option D" there). A
non-browser process that dials out is the remaining shape that works in production without exposing the
user's machine.

## Affected areas

Verified against the tree at `e133f98`.

**New files**
- `tools/Agent42.LocalBridge/Agent42.LocalBridge.csproj` + `Program.cs` — console app (added to
  `Agent42.slnx` under `/tools/`).
- `tools/Agent42.LocalBridge/BridgeClient.cs` — WebSocket connect/reconnect loop, envelope pump.
- `tools/Agent42.LocalBridge/UpstreamChannel.cs` — one upstream MCP session per channel, over the SDK's
  `HttpClientTransport` (`ConnectAsync` → `ITransport`, message-level relay).
- `tools/Agent42.LocalBridge/ConfirmationGate.cs` — the read-only classifier + the console prompt +
  terminal-text sanitizer.
- `src/Agent42.Agents/Mcp/ILocalBridgeKeyDirectoryGrain.cs` + `LocalBridgeKeyDirectoryGrain.cs` — global
  key-hash → `(userId, serverId)` directory (copy of `IAgentKeyDirectoryGrain`'s shape, rides `agent-state`).
- `src/Agent42.Agents/Mcp/BridgeKey.cs` — generate + hash, `sk_bridge_` prefix (copy of `ApiKey`, the
  precedent `ShareToken` set).
- `src/Agent42.Backend/Mcp/LocalBridgeEnvelope.cs` — the wire contract, backend copy. The helper has its
  **own** copy in `tools/Agent42.LocalBridge/BridgeEnvelope.cs` (namespace `Agent42.LocalBridge`); a
  wire-compatibility test pins them together (see "Shared contract" below for why not a compile-link).
- `src/Agent42.Backend/Mcp/LocalBridgeRegistry.cs` — in-process `(userId, serverId)` → live bridge socket.
- `src/Agent42.Backend/Mcp/LocalBridgeSession.cs` — one accepted socket: receive loop, channel routing,
  channel cap, close.
- `src/Agent42.Backend/Mcp/LocalBridgeChannelTransport.cs` — `TransportBase` + `IClientTransport` for one
  channel, so `McpClient.CreateAsync` runs unchanged over it.
- `src/Agent42.Backend/Mcp/LocalBridgeConnectionFactory.cs` — `IMcpConnectionFactory` decorator: routes
  `LocalBridge` servers to the registry, everything else to the inner factory.
- `src/Agent42.Backend/Mcp/LocalBridgeEndpoint.cs` + `LocalBridgeRegistration.cs` — the `GET
  /api/local-bridge` WebSocket endpoint (key self-enforced, fail closed) and its `Add…`/`Use…` pair.
- `src/Agent42.Backend/Mcp/LocalBridgeLimits.cs` — every number below, in one place.
- Tests (see the AC table): `tests/Agent42.Tests/LocalBridge*Tests.cs`.
- `.agentic/decisions/0076-local-bridge-for-machine-local-mcp-servers.md` (via `/decide`).

**Modified**
- `src/Agent42.Agents/Mcp/UserMcpConfig.cs` — `McpAuthMode.LocalBridge` (appended); trailing
  `StoredMcpServer [Id(14)] string? BridgeKeyHash = null`.
- `src/Agent42.Agents/Mcp/IUserMcpConfigGrain.cs` + `UserMcpConfigGrain.cs` — `SaveAsync` **always
  preserves** `BridgeKeyHash` (it never sets or clears it); new `MintBridgeKeyAsync(serverId)` (returns the
  raw key) and `ClearBridgeKeyAsync(serverId)`; `DeleteAsync` deregisters. The grain owns the hash **and**
  the directory calls, in the per-operation order of `AgentRegistryGrain.SaveAsync` (see Context).
- `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` — `ValidateMcpServer` parses the auth mode **before** the
  endpoint check and accepts `LocalBridge` with a blank endpoint; `ToStored` coalesces a null endpoint to
  `""` (it calls `request.Endpoint.Trim()` today, line ~158); create / switch-to call `MintBridgeKeyAsync`
  (key shown once); switch-away calls `ClearBridgeKeyAsync`; new `POST /api/mcp-servers/{id}/bridge-key`
  (regenerate). **Every management write to a bridge server — update, delete, regenerate, disable,
  switch-away — evicts its live socket from `LocalBridgeRegistry`**, resolved **optionally via
  `IServiceProvider.GetService`** (the `/test` route's pattern, ~67-77), never as a handler parameter.
- `src/Agent42.Backend/Mcp/McpContracts.cs` — `McpServerResponse` (or a create/regenerate-specific
  response) gains `bridgeKey`, populated only when minted; `McpServerUpsertRequest.Endpoint` tolerates null.
- `src/Agent42.Backend/Api/AgentApi.cs` — route-count comment (~11-14) 63 → 64.
- `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` (~109-114) and `McpConnectionTester.cs` (~74-75) —
  accept `LocalBridge` in the supported-modes check; the tester maps "bridge not connected" to a curated
  reason.
- `src/Agent42.Backend/Program.cs` — decorate `IMcpConnectionFactory` (line ~195) and call the
  registration pair (beside `UseAgentMcpKeyGate`, ~568). Keep `Program.cs` under S3776 by putting logic in
  `LocalBridgeRegistration`, never inline.
- `tools/Agent42.DevHost/Program.cs` — same decoration over `OfflineMcpConnectionFactory` (line ~142,
  decorator registered **after** it) and the same registration call. **This makes only the management UI
  and the Test button exercisable offline** — DevHost's `SingleUserMcpToolProvider` (~87-97) never reads
  the user overlay, so a bridge server added in the UI never reaches `BuildAgent` there, and
  `CannedChatClient` emits only hard-coded tool calls. The tool-call path is covered by the opt-in live
  test in Step 7 instead.
- `infra/modules/container-app.bicep` (~339-346) — `excludedPaths` gains **`/api/local-bridge`** (six →
  seven).
- `tests/Agent42.Tests/EasyAuthExclusionTests.cs` — positive row for `/api/local-bridge`; negative rows
  proving `/api/mcp-servers` and `/api/mcp-servers/{id}/bridge-key` stay gated.
- `src/Agent42.Backend/wwwroot/index.html`, `wwwroot/js/mcp.js` — "Local bridge" auth option;
  `syncMcpAuthUi` hides endpoint/header/OAuth fields for it; a shown-once key panel + Regenerate (copy the
  `agents.js` ~125-229 pattern); list row shows "local bridge" instead of an endpoint.
- `src/Agent42.Backend/wwwroot/sw.js` — `CACHE` `agent42-shell-v37` → `-v38`.
- `tools/Agent42.LocalBridge` is added to `Agent42.slnx`; `tests/Agent42.Tests/Agent42.Tests.csproj`
  references it. The repo has **no `InternalsVisibleTo`**, so every helper type under test is `public`.
- Docs: `.agentic/gotchas.md` (new entries, plus the "six entries" list at ~1831-1838 → seven);
  `CLAUDE.md` (new bullet, plus every stale "six" — the ADR 0035 bullet "The six entries are…" and the ADR
  0073 bullet "the list stays at six"; grep `six` near `excludedPaths`); `docs/features/remote-mcp-servers.md`
  (a Local bridge section); `docs/features/README.md` and `docs/integrations/README.md` (check the MCP
  mentions); `.agentic/specs/plan-browseros-neo-as-an-mcp-tool.md` → `specs/completed/` (superseded by
  this plan).

**Deliberately NOT modified — verified**
- `AgentGrain.BuildAgent` / `ToolCatalog`: bridged tools are ordinary `McpServerTools`; the existing
  per-server/per-tool gating (ADR 0026/0058) and `WithUrlSeeding` apply unchanged.
- `TruncatingMcpTool`: wraps bridged tools already via `UserMcpToolProvider`.
- `ProjectLayoutTests.ExpectedFolders`/`AgentApiParts`: no new folder (all Backend files go in `Mcp/`),
  and the WebSocket endpoint is **not** an `AgentApi.*` partial (ADR 0070's reasoning — a public,
  self-enforcing route stays out of the Easy-Auth composition root). The new `bridge-key` route **is**
  inside the existing `MapMcpEndpoints`.
- `deploy.yml`: the helper is never deployed; it is run by the user.

## Context & constraints

### Decisions the user made (do not re-litigate)
1. **Confirmation lives in the helper**, on the machine whose browser is being driven — not a chat-UI
   approval loop. Works on every turn kind and needs no Agent Framework approval plumbing.
2. **Neo only in v1, upstream pluggable.** The helper's upstream is an interface; v1 ships one
   implementation (streamable-HTTP URL). `agent-browser` (a CLI) is a follow-up.
3. **The helper lives in `tools/` in this repo**, with its own copy of the envelope contract pinned to the
   backend's by a test (the compile-link first proposed was dropped in review — see Review H2).
4. **Any turn kind may use the tools while the bridge is connected** — live chat, reminders, agent-run,
   `/api/agent-mcp`. No new gate in `BuildAgent`. The helper's confirmation prompt is the control.

### Measured facts (2026-09-17, BrowserOS neo 0.0.53)
- Server process `browseros-claw-server` on **`127.0.0.1:9210`** (docs say 9239 — the helper takes the URL
  as config, never a default port it assumes silently; default it to `http://127.0.0.1:9210/mcp` with the
  help text pointing at `chrome://browseros/mcp`).
- `initialize` with no `Origin` → 200 SSE; with any `Origin` → 403. .NET `HttpClient` sends no `Origin`, so
  the helper is fine — **never add one**.
- **20 tools, ~31 KB of `tools/list` JSON.** Annotations: `readOnlyHint: true` on `history, snapshot, diff,
  read, grep, screenshot, pdf, wait`; explicit `readOnlyHint: false` on `name_session, save_skill,
  mark_skill_run`; **no readOnlyHint** on `tabs, tab_groups, navigate, act, download, upload, windows,
  evaluate, run`. `run`/`evaluate` execute arbitrary JavaScript in the signed-in browser.
- MCP SDK `ModelContextProtocol.Core` **2.2.0** (the pinned version) exposes `IClientTransport`, public
  `TransportBase(string name, ILoggerFactory)` with `WriteMessageAsync`/`SetConnected`/`SetDisconnected`,
  `JsonRpcMessage`, `HttpClientTransport`, and `ToolAnnotations.ReadOnlyHint` (checked in
  `Z:\Packages\.nuget\packages\modelcontextprotocol.core\2.2.0\lib\net10.0\ModelContextProtocol.Core.xml`).
  Nothing here needs a new package. **Step 0 still proves the custom transport end to end**, because doc
  XML is not behaviour.

### Architecture (the load-bearing choices)

**Multiplexed channels over one socket.** One bridge connection per `(userId, serverId)`, but the backend
opens **several MCP sessions** to a server at once: `UserMcpToolProvider` retains 2 versions per user
(live clients), and `McpConnectionTester` opens its own. So each `ConnectAsync` opens a **channel**: the
backend sends `{ch, type:"open"}`, then JSON-RPC as `{ch, type:"message", msg}`, and `{ch, type:"close"}`
on dispose. The helper maps each channel to **its own upstream MCP session** (a fresh
`HttpClientTransport.ConnectAsync`) and relays messages verbatim. One envelope per WebSocket text message.
Cap open channels per bridge (`LocalBridgeLimits.MaxChannels`, 16) and message size
(`MaxMessageBytes`, 1 MiB — the 8 KiB truncation happens later, in `TruncatingMcpTool`); overflow closes
the **channel**, not the bridge.

**The backend stays the MCP client.** `LocalBridgeChannelTransport` makes a channel look like any
transport, so `McpClient.CreateAsync` + `ListToolsAsync` in the factory are unchanged — mirror
`McpConnectionFactory.ConnectAsync`'s try/dispose-on-list-failure shape exactly.

**Not connected = fail fast.** If the registry has no live socket, `ConnectAsync` throws immediately (a
curated `LocalBridgeNotConnectedException`), so `UserMcpToolProvider` skips the server without spending its
10 s budget, and the tester reports **"local bridge not connected"** (not the generic "could not
connect" — the user's next action is to start the helper, not fix a URL).

**Single replica is load-bearing — a second time.** The registry is in-process; a grain on another replica
could not find the socket. `infra/main.bicep` pins `minReplicas = maxReplicas = 1` (ADR 0054 already
depends on it). Record this in the ADR; do not add a distributed registry.

**Auth: a per-server bridge key, header only.**
- Minted when a server is created (or switched) to `LocalBridge`, **shown once**, only its SHA-256 hash
  stored (`BridgeKeyHash`), registered in `ILocalBridgeKeyDirectoryGrain`. Regenerate **deregisters the old
  hash first**; delete deregisters; switching mode away deregisters and clears the hash. Read the gotcha
  "The API-key directory must be kept in sync per-operation" (`.agentic/gotchas.md` ~1799) — it applies
  verbatim.
- The key arrives **only** in the `X-Api-Key` request header on the upgrade. **Never accept it in the query
  string**: that would leak it into access logs and — the real reason — would let a *browser page* open
  the socket (browsers cannot set custom headers on a WebSocket, which is what keeps every web page out).
- `/api/local-bridge` is on `excludedPaths`, so the endpoint **self-enforces and fails closed** (gotcha
  ~1821): resolve the key → `(userId, serverId)` from the directory (**never `GetUserId`**), then re-read
  the user's overlay and require the server to exist, be `Enabled`, be `LocalBridge`, and have a matching
  `BridgeKeyHash` (a stale directory entry must not authenticate). Any failure → **uniform 401**, before
  checking `IsWebSocketRequest` (a non-upgrade request with a valid key → 400; without one → 401).
- **Exact entry, not `/*`**: the route has no sub-paths. `EasyAuthExclusionTests` pins exact-vs-prefix
  semantics (ADR 0042).
- A second connection for the same server **replaces** the first (close the old socket with a policy
  close code).
- **The directory is consulted only at upgrade time, so revocation must also close live sockets.** A
  socket that authenticated before Regenerate / delete / disable / switch-away would otherwise keep
  serving as that user's MCP server indefinitely — injecting tool results and receiving model-written
  arguments. Hence the "every management write evicts the socket" rule above; the real helper reconnects
  and re-authenticates against the new state (and a revoked one stops on 401). This is the live-socket
  form of the gotcha's invariant "a revoked key must never keep resolving".

**Key ownership and the `Keep` trap.** `UserMcpConfigGrain.SaveAsync` rebuilds the record from a request
that carries no hash, exactly like `KnownToolNames` (line ~58), so without care **every edit silently
kills the key**. But a `?? prev` clause alone would make clearing impossible (null would mean "keep"). So:
`SaveAsync` **unconditionally** carries `prev.BridgeKeyHash` forward and never writes it; the hash changes
only through the grain's dedicated `MintBridgeKeyAsync` / `ClearBridgeKeyAsync` / `DeleteAsync`, each
doing its directory work **inside the grain** in `AgentRegistryGrain`'s per-operation order (mint over an
existing hash = regenerate → **deregister old first**, write, register; clear/delete → write, then
deregister). Keeping the ordering in one non-reentrant grain avoids the read-modify-write race an API-layer
sequence would open. Unlike `SeedsFetchableUrls` (a setting — never preserved this way), the hash is
server-minted metadata. The grain generates the key via `BridgeKey`, as `AgentRegistryGrain` does via
`ApiKey`.

**Regenerate is a separate route, deliberately — record the alternative.** ADR 0022 rotates through the
upsert (`RegenerateKey` on PUT, `AgentContracts.cs`). Here a dedicated `POST …/bridge-key` rotates without
a Save, which matters because Save also re-runs the auto-test and evicts the socket. It follows the
existing `/{id}/test` literal-segment shape, so it does not collide with `{id}`.

**Shared contract: duplicated and pinned, not compile-linked.** Linking one source file into both the
Backend and the helper while `Agent42.Tests` references both yields the same public type in two assemblies
(CS0433 wherever a test names it), and `internal` does not help (no `InternalsVisibleTo`). A new shared
`src/` library costs a project, Dockerfile and layout-test churn for a three-field record. So each side
has its own envelope type in its own namespace, and `Envelope_WireFormatsMatch` round-trips every message
kind through both serializers.

**Lifecycle consequences to accept, not fix.**
- `AgentGrain` resolves `_mcpServers` **once, on activation** (`AgentGrain.cs:330`). A bridge that
  connects after a chat activated reaches that chat only on its next activation. On bridge **connect**,
  call `IUserMcpConfigGrain.InvalidateToolCacheAsync()` (ADR 0069's mechanism) so the next activation does
  not reuse a cached "skipped" set — **but rate-limited**: at most once per
  `LocalBridgeLimits.InvalidateMinInterval` (5 min) per `(userId, serverId)`, tracked in the registry.
  Each bump is a Cosmos write, and with only 2 retained versions two bumps dispose the warm sockets of
  **every other** MCP server of that user; routine reconnects (idle timeout, revision swap, a flapping
  helper, two helpers fighting over "replaces the first") would otherwise become write-and-teardown churn.
  Do **not** invalidate on disconnect, for the same reason. Accepted consequence: after a disconnect with
  no reconnect, a new activation may get a cache hit whose bridged tools fail when called.
- A warm grain holding tools from a socket that has since dropped gets a failing tool call. The channel
  transport must `SetDisconnected` on socket loss so pending requests fault promptly rather than hang the
  non-reentrant grain. Session resumption is out of scope.
- WebSocket keep-alive: `UseWebSockets(new WebSocketOptions { KeepAliveInterval = 30 s })`; the helper
  also sets `ClientWebSocket.Options.KeepAliveInterval`. The ACA ingress idle timeout is **unmeasured** —
  record as a prod-only residual; the helper's reconnect loop is the backstop.
- **The whole feature rests on an unmeasured production premise:** that ACA ingress plus the Easy Auth
  layer pass a WebSocket `Upgrade` through on an **excluded exact path**. ADR 0042's lesson is that
  `excludedPaths` behaviour is only proven by the post-deploy curl matrix (gotchas ~1846-1849). ADR 0076's
  Unverified section must carry a post-deploy check: upgrade with a valid key → 101, without → 401 from
  **our** endpoint (not an Easy Auth 302/401), and the idle-timeout observation.
- Prompt queueing: with serialized prompts, a 60 s timeout each and a non-reentrant grain waiting on the
  call, several pending write calls can hold a turn for minutes. Accepted; one sentence in the ADR.

**The confirmation gate (helper).**
- The helper learns annotations by **parsing the `tools/list` responses it relays** (per channel, merged
  into one name → `readOnlyHint` map). A `tools/call` for a tool whose `readOnlyHint` is not **exactly
  `true`** — including an unknown tool, or a call before any `tools/list` — requires confirmation.
  Fail-closed: absence of the hint is not permission.
- Prompts are **serialized** (one at a time) and time out after `ConfirmTimeout` (60 s) → **deny**.
- **Deny** → the helper answers the backend itself with a JSON-RPC **result** (same `id`)
  `{"content":[{"type":"text","text":"The user declined this browser action."}],"isError":true}` — a tool
  result, not a protocol error, so the model sees and can explain it. The call never reaches the upstream.
- `--auto-approve <tool,…>` lets the user pre-approve named tools; there is **no** "approve everything"
  switch in v1.
- **"Read-only" is the upstream's own claim, and a read-only call can still be an outbound channel.** If
  `read`/`snapshot`/`pdf`/`screenshot` accept a URL or navigate, an injected instruction can make the
  signed-in browser GET an attacker URL carrying conversation data — unprompted. So a call to a read-only
  tool **also** prompts when any string argument looks like a URL (`^[A-Za-z][A-Za-z0-9+.-]*:` or starts
  with `//`). Step 0 records every read-only tool's input schema; the ADR states which of them navigate.
- **The prompt text is attacker-influenced** (arguments are model-written, and the model reads web pages):
  strip C0/C1 control characters **including ESC** (ANSI escape injection can rewrite the terminal, e.g.
  hide the real tool name) **and Unicode format characters (Cf)** — the bidi overrides such as U+202E
  reorder rendered text, the reason ADR 0075's sanitizer strips Cf. Show the tool name first, on its own
  line, then the arguments as sanitized pretty-printed JSON.
- **Never truncate what the user is approving.** A 400-character cap would let a `run` payload put a
  harmless prefix first and the real script after it. Show the **full** sanitized arguments, wrapped. If
  they exceed `MaxReviewableArgChars` (20,000), do not show a prefix — **deny outright** with the result
  text "The browser action was too large to review, so it was declined."
- Why the gate sits in the helper and not the backend: the person approving is at the browser being
  driven, and a backend gate would need the approval loop ADR 0049 notes this app does not have. This is
  the **first confirmation step on any MCP write** in the app; ADR 0053/0063 rejected one *for the
  calendar* — that is a different server and a user decision there, not a contradiction to supersede.

**Helper configuration.** `--server https://chat.agent42.nl` (required), `--upstream <url>` (default
above), `--auto-approve`, key from env var **`AGENT42_BRIDGE_KEY`** only (never argv: visible in the
process list). Reconnect with exponential backoff 1 s → 60 s; on **401 stop** with a clear message (a
revoked key must not hammer the endpoint). Log to console; never log the key or tool-call bodies above a
debug level.

**Security consequences to record in the ADR honestly.**
- Headline: **a web page the agent reads can steer actions in the user's signed-in browser.** ADR 0024's
  inbound prompt-injection channel meets tools that click, type and run JavaScript. The helper prompt is
  the mitigation, and only for non-read-only tools: `read`/`snapshot`/`history`/`screenshot` run without
  asking, so page content **and browsing history** flow to the cloud model (ADR 0013 egress) whenever the
  model chooses. Recommend narrowing (ADR 0058) — `history` especially.
- `SeedsFetchableUrls` must stay **off** for a bridge server (same reasoning as the Gmail note in ADR
  0067, at higher volume). Do not force it in code; document it.
- `/api/local-bridge` is a new public endpoint and the **first WebSocket** in the app; a key holder gets a
  socket into our single replica. The channel/message caps bound what one socket can cost.
- `/api/agent-run` and `/api/agent-mcp` key holders can now indirectly request browser actions on the
  owner's machine — each still meets the helper's prompt for writes. Accepted (decision 4). **This amends
  ADR 0070**: its bind list is filtered (`WithoutMemoryOrPush`), and ADR 0075 treated adding a capability
  to that surface as an amendment; here the surface gains bridged tools because MCP keys pass through the
  filter. Say so in both ADRs rather than leaving it implicit.
- Tool schemas (~31 KB) ride every turn with the server enabled; the GitHub Models 16k input cap (gotchas)
  makes narrowing a practical requirement there, not just hygiene.

### Governing documents to read before starting
- ADRs 0022 (key shape), 0024/0026/0058/0064/0069 (MCP client stack), 0070 (public MCP endpoint, gate
  outside `AgentApi`), 0042 (`excludedPaths` semantics), 0054 (single replica), 0067 (seeding).
- `.agentic/gotchas.md`: "Remote MCP servers: a live-socket cache…" (~1637), "The MCP connection test…"
  (~1716), "The API-key directory must be kept in sync…" (~1799), "An Easy-Auth `excludedPaths`
  endpoint…" (~1821), "A named HttpClient silently inherits…" (~388 — the helper is not an ASP.NET host,
  but do not build the backend side on a DI-named client either).
- Exemplars: `McpConnectionFactory.cs` (connect/list/dispose), `AgentMcpKeyGate.cs` +
  `AgentApi.StoredAgents.cs` `ResolveAgentRunCallerAsync` (fail-closed key resolution),
  `IAgentKeyDirectoryGrain.cs` (directory), `agents.js` (shown-once key UI), DevHost
  `OfflineMcpConnectionFactory`.

### Naming (settled)
Auth mode `LocalBridge`; route `/api/local-bridge`; regenerate route `POST
/api/mcp-servers/{id}/bridge-key`; key prefix `sk_bridge_`; env var `AGENT42_BRIDGE_KEY`; project
`Agent42.LocalBridge`; ADR 0076. Branch `feature/local-browser-bridge` — no command text may contain the
lowercase token `push` while on `main` (`.claude/hooks/protect-main.mjs:91` treats it as a push).

## Steps

### Step 0 — spike the custom transport (blocks everything)
`/spike-dotnet`: a file-based app pinned to `ModelContextProtocol.Core` 2.2.0 that subclasses
`TransportBase` + `IClientTransport` over an in-memory pipe, relays to neo at `127.0.0.1:9210` through an
`HttpClientTransport` session, and runs `McpClient.CreateAsync` → `ListToolsAsync` → one read-only
`CallToolAsync` (`snapshot` on a tab the spike opens — `tabs` is not annotated read-only). Record: does
`McpClient` require anything of `SessionId`; does `SetDisconnected` fault a pending request; does relaying
`notifications/*` both ways need anything; and **every read-only tool's input schema** (which accept a URL
or navigate — feeds the URL-argument rule). If the subclass route fails, fall back to
`StreamClientTransport` over a duplex pipe per channel and record why.
**Verify:** spike output pasted into the ADR's evidence section.

### Step 1 — contracts, key, directory, stored shape, grain key ops
Both envelope copies + `Envelope_WireFormatsMatch`, `LocalBridgeLimits`, `BridgeKey`,
`ILocalBridgeKeyDirectoryGrain` + grain, `McpAuthMode.LocalBridge`, `StoredMcpServer [Id(14)]
BridgeKeyHash`, `SaveAsync` preservation, `MintBridgeKeyAsync` / `ClearBridgeKeyAsync` and the
`DeleteAsync` deregister — in `AgentRegistryGrain`'s per-operation order (read it first).
**Verify:** `dotnet test --filter "FullyQualifiedName~LocalBridgeKey|FullyQualifiedName~UserMcpConfigGrain|FullyQualifiedName~GrainStorageSerializer|FullyQualifiedName~SerializationCompatibility|FullyQualifiedName~LocalBridgeContract"`.

### Step 2 — management API + validation
`ValidateMcpServer` (mode parsed before the endpoint check), `ToStored` null-endpoint coalesce, mint on
create / switch-to, clear on switch-away, the `bridge-key` route, `bridgeKey` in the response only when
minted, tester + provider mode checks, the tester's curated "local bridge not connected" branch (its
generic catch says "could not connect" today). Socket eviction on every management write, via
`GetService<LocalBridgeRegistry>()` (null-tolerant — 10 `MapAgentApi` hosts, most without the registry).
`AgentApi.cs` route-count comment → 64.
**Verify:** `dotnet test --filter "FullyQualifiedName~McpServersApi|FullyQualifiedName~McpConnectionTester|FullyQualifiedName~UserMcpToolProvider|FullyQualifiedName~LocalBridgeApi"`,
then the **full** suite once (a new handler dependency is exactly what breaks unrelated fixtures).

### Step 3 — backend socket side
`LocalBridgeRegistry` (incl. eviction and the invalidate rate limit), `LocalBridgeSession`,
`LocalBridgeChannelTransport`, `LocalBridgeConnectionFactory`, `LocalBridgeEndpoint`,
`LocalBridgeRegistration`; wire into **both** `Program.cs` files with the decorator registered **after**
the inner factory. Tests use this repo's host shape — `WebApplication.CreateSlimBuilder` + `UseTestServer`
(`McpConnectionTestApiTests.cs:31-33`) and `app.GetTestServer().CreateWebSocketClient()` with the key set
via `ConfigureRequest` — with an in-test fake helper that speaks **raw JSON** (never names the backend
envelope type). **Socket-drop and replacement (AC16/AC17) run on real Kestrel**, in the shape of
`ChatStreamDisconnectTests`: ADR 0046 measured that TestServer's in-memory pipe cannot reproduce a dead
transport. Mutation-check AC16 (remove the `SetDisconnected` call → the test must fail).
**Verify:** `dotnet test --filter "FullyQualifiedName~LocalBridge"` and `dotnet build Agent42.slnx` (S3776).

### Step 4 — infra
`excludedPaths` entry + `EasyAuthExclusionTests` rows. **Verify:** `dotnet test --filter
"FullyQualifiedName~EasyAuthExclusion"`. *(parallelizable with Step 5 in a worktree)*

### Step 5 — the helper
Project (in `Agent42.slnx`), `BridgeEnvelope`, `BridgeClient`, `UpstreamChannel`, `ConfirmationGate`
(an injectable prompt seam so it is testable without a console; all types `public`), reconnect/401
policy. **Verify:** `dotnet test --filter "FullyQualifiedName~LocalBridgeHelper"`. Coverage note: the
new-code gate counts `src/` only, so helper tests are required by hard rule 3, not by the gate.
*(parallelizable with Step 4)*

### Step 6 — frontend
Auth option; `syncMcpAuthUi` hides endpoint/header/OAuth fields **and drops `required` from
`#mcpEndpoint`** (`index.html` ~746-747) for Local bridge; the save path's `!payload.endpoint` early
return (`mcp.js` ~239) is skipped for Local bridge and sends `endpoint: ""`; shown-once key panel +
Regenerate button calling the new route; list row label; `sw.js` bump. Existing guards in
`McpServersAssetsTests` must stay green (duplicate-create, disabled and OAuth-no-token auto-test
carve-outs); add: **the auto-test on save is skipped on the save that minted a Local bridge key** — the
helper cannot be connected yet (ADR 0064's OAuth-no-token reasoning).
**Verify:** `dotnet test --filter "FullyQualifiedName~McpServersAssets|FullyQualifiedName~PwaAssets"`.

### Step 7 — end-to-end legs (opt-in live test + manual, recorded)
DevHost cannot run the tool-call path (see Affected areas), so:
1. **Committed opt-in live test** `LocalBridgeLiveTests`, skipped unless `AGENT42_BRIDGE_E2E=1`: starts a
   real Kestrel host with the endpoint, registry and a real `MintBridgeKeyAsync`, runs the helper's
   `BridgeClient` in-process against **real neo**, then through `LocalBridgeConnectionFactory` +
   `McpClient`: list tools; call `snapshot` (no prompt); call `navigate` with a scripted **deny** (assert
   the `isError` result and that the tab did not change); scripted **allow** (tab changed); Regenerate →
   the live socket closes and the helper stops on 401.
2. **Manual, with the real console prompt:** the same run with the console prompt seam, plus a crafted
   `run` argument containing ESC, U+202E and more than 20 000 characters — record what the terminal showed.
3. **DevHost UI leg:** add a Local bridge server, copy the key, start the helper with
   `--server http://localhost:61326`, press Test (expect "N tools"); stop the helper → Test says "local
   bridge not connected"; Regenerate → the running helper exits on 401.

Record measured values in ADR 0076. **Unverified, to be stated:** a model actually receiving and
explaining the deny result (no model offline), and every production premise in Context.

### Step 8 — docs and decision
`/decide` ADR 0076 (amends 0024 — a reverse-connected server and a new auth mode; amends 0002/0042 — a
seventh `excludedPaths` entry; amends 0070 — `/api/agent-mcp` turns gain bridged tools; extends 0022 —
key shape, with the separate regenerate route as a recorded alternative; notes 0054 — a second
single-replica dependency; records the post-deploy upgrade check as Unverified).
`/gotcha` for: the hash-preservation/ownership trap, header-only key, live-socket revocation, terminal
escape + Cf sanitizing and never-truncate, URL-bearing read-only calls, the invalidate rate limit,
activation-scoped tools vs a late-connecting bridge, and "any `Origin` → 403" for anyone tempted to relay
from the browser again. Update the docs listed in Affected areas, including every stale "six". Move
`plan-browseros-neo-as-an-mcp-tool.md` to `specs/completed/`. `/sync-docs`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | `BridgeKey` generates `sk_bridge_` keys; hash is SHA-256 hex; raw never stored | `BridgeKey_GeneratesPrefixedKeyAndStableHash` | `LocalBridgeKeyTests.cs` |
| 2 | Directory register/resolve/deregister round-trips | `Directory_RegisterResolveDeregister` | `LocalBridgeKeyTests.cs` |
| 3 | The two envelope types produce and accept identical JSON for open/message/close | `Envelope_WireFormatsMatch` | `LocalBridgeContractTests.cs` |
| 4 | A pre-feature `StoredMcpServer` document deserializes with `BridgeKeyHash == null` (Cosmos JSON path) | `StoredMcpServer_WithoutBridgeKeyHash_Deserializes` | `GrainStorageSerializerTests.cs` |
| 5 | `SaveAsync` preserves the hash on edit and never sets or clears it | `Edit_PreservesBridgeKeyHash` | `UserMcpConfigGrainTests.cs` |
| 6 | Mint over an existing hash deregisters the old hash before registering the new; clear and delete deregister | `MintBridgeKey_RegenerateRevokesOld` / `ClearAndDelete_Deregister` | `UserMcpConfigGrainTests.cs` |
| 7 | Creating a `LocalBridge` server with a blank/omitted endpoint succeeds and returns `bridgeKey` once; GET never returns it; a non-bridge server still requires an endpoint | `CreateLocalBridge_ReturnsKeyOnce` / `NonBridge_StillRequiresEndpoint` | `LocalBridgeApiTests.cs` |
| 8 | Regenerate route returns a new key and the old key gets 401; switch-away clears | `Regenerate_RevokesOldKey` / `SwitchAway_ClearsKey` | `LocalBridgeApiTests.cs` |
| 9 | Update, delete, disable, switch-away and regenerate each **close the live socket** | `ManagementWrites_CloseLiveSocket` (theory) | `LocalBridgeApiTests.cs` |
| 10 | Hosts without the registry still route; eviction is null-tolerant | full suite green + `ManagementWrites_WithoutRegistry_Succeed` | `LocalBridgeApiTests.cs` |
| 11 | Endpoint: missing / unknown / query-string key → uniform 401; valid key but server disabled, deleted, not `LocalBridge`, or hash mismatch → 401 | `Endpoint_FailsClosed` (theory) | `LocalBridgeEndpointTests.cs` |
| 12 | Valid key, non-WebSocket request → 400 | `Endpoint_NonUpgrade_Is400` | `LocalBridgeEndpointTests.cs` |
| 13 | With a fake helper connected, the factory lists its tools and a `tools/call` round-trips | `ConnectedBridge_ListsAndCallsTools` | `LocalBridgeEndpointTests.cs` |
| 14 | Two concurrent channels on one socket are isolated | `Channels_AreIsolated` | `LocalBridgeEndpointTests.cs` |
| 15 | No bridge connected → `ConnectAsync` throws `LocalBridgeNotConnectedException` without waiting; tester reports "local bridge not connected" (decorator + empty registry wired explicitly) | `NotConnected_FailsFast` / `Tester_ReportsBridgeNotConnected` | `LocalBridgeEndpointTests.cs` / `McpConnectionTesterTests.cs` |
| 16 | Socket drop faults a pending tool call promptly — **real Kestrel**, mutation-checked | `SocketDrop_FaultsPendingCall` | `LocalBridgeKestrelTests.cs` |
| 17 | A second connection replaces the first — **real Kestrel** | `SecondConnection_ReplacesFirst` | `LocalBridgeKestrelTests.cs` |
| 18 | Channel cap and message-size cap close the channel, not the bridge | `Limits_CloseChannelNotBridge` | `LocalBridgeEndpointTests.cs` |
| 19 | Connect invalidates the tool cache at most once per interval per server; disconnect never does | `Connect_InvalidatesToolCache_RateLimited` | `LocalBridgeEndpointTests.cs` |
| 20 | Other auth modes still go to the inner factory | `Decorator_RoutesOnlyLocalBridge` | `LocalBridgeEndpointTests.cs` |
| 21 | `/api/local-bridge` is excluded (exact); `/api/mcp-servers*` and `/bridge-key` are not | new rows | `EasyAuthExclusionTests.cs` |
| 22 | Both hosts register the decorator **after** the inner factory and call the registration pair | `Hosts_RegisterLocalBridge` (source-text, ordered) | `ProjectLayoutTests.cs` |
| 23 | Gate: `readOnlyHint: true` passes without prompting; false / absent / unknown tool / before `tools/list` → prompt | `Gate_ClassifiesByReadOnlyHint` (theory) | `LocalBridgeHelperTests.cs` |
| 24 | Gate: a read-only call with a URL-like string argument prompts | `Gate_ReadOnlyWithUrlArgument_Prompts` | `LocalBridgeHelperTests.cs` |
| 25 | Deny, timeout and over-`MaxReviewableArgChars` all answer with an `isError` tool result carrying the same id; upstream never called | `Gate_DenyTimeoutOversize_AnswerLocally` | `LocalBridgeHelperTests.cs` |
| 26 | The prompt shows the **full** sanitized arguments (no prefix truncation) | `Prompt_ShowsFullArguments` | `LocalBridgeHelperTests.cs` |
| 27 | `--auto-approve` skips the prompt for named tools only | `Gate_AutoApprove_IsPerTool` | `LocalBridgeHelperTests.cs` |
| 28 | Sanitizer strips ESC/C0/C1 and Cf (incl. U+202E) | `Sanitizer_StripsTerminalEscapesAndFormatChars` | `LocalBridgeHelperTests.cs` |
| 29 | Prompts are serialized | `Gate_SerializesPrompts` | `LocalBridgeHelperTests.cs` |
| 30 | Helper stops (no retry) on 401; backs off on other failures | `Client_StopsOn401` / `Client_BacksOff` | `LocalBridgeHelperTests.cs` |
| 31 | Key from `AGENT42_BRIDGE_KEY` only, sent only as `X-Api-Key`; no `Origin` upstream | `Client_KeyFromEnvHeaderOnly` / `Upstream_SendsNoOrigin` | `LocalBridgeHelperTests.cs` |
| 32 | UI: Local bridge option, field hiding incl. `required`, blank-endpoint save, shown-once key, Regenerate, auto-test skipped after mint; `CACHE` bumped | source-text guards | `McpServersAssetsTests.cs`, `PwaAssetsTests.cs` |
| 33 | Live: list, read-only call, scripted deny/allow, regenerate closes the socket — against real neo | `LocalBridgeLiveTests` (opt-in) | `LocalBridgeLiveTests.cs` |
| 34 | Manual: real console prompt, escape/bidi/oversize rendering, DevHost UI legs | recorded | ADR 0076 |

## Quality gate checklist

From `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (counts `src/` only — helper coverage is not gated, tests still required) |
| `cognitive_complexity_max` | 15 | `dotnet build Agent42.slnx` (S3776 at `error`) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| Lint | — | `dotnet format Agent42.slnx --verify-no-changes` |
| Tests | all green | `dotnet test Agent42.slnx` |

No new package is expected. If one turns out to be needed, add it with `dotnet add package` (hard rule 4).

## Out of scope
- Vercel `agent-browser` (CLI) upstream — the upstream interface is the seam; its adapter is a follow-up.
- A chat-UI approval loop, OS toast notifications for the prompt, and an "approve all" switch.
- MCP session resumption across a socket reconnect; a distributed registry (multi-replica).
- Excluding bridged tools from reminder / agent-run / agent-mcp turns (decision 4).
- Shipping the helper as a signed installer or `dotnet tool`; v1 is `dotnet run` / `dotnet publish`.
- Measuring the ACA ingress WebSocket idle timeout (recorded as a prod residual).
- Forcing `SeedsFetchableUrls` off in code for bridge servers (documented instead).


## Review

Critic pass on the first draft (fresh-context `critic` subagent): 4 high, 8 medium, 6 low findings; it
also confirmed most code claims (line refs, `[Id(13)]` as the last id, `CACHE` v37, the replica pin, gate
thresholds).

### Accepted and fixed
- **H1 — revocation did not close a live socket.** Every management write now evicts the socket (AC9);
  eviction resolves the registry via `GetService`, so the 10 `MapAgentApi` hosts keep routing (AC10).
- **H2 — a compile-linked contract gives CS0433 in tests.** Replaced by two envelope copies pinned by a
  wire-format test (AC3); helper types are `public` (no `InternalsVisibleTo` in this repo).
- **H3 — DevHost cannot run the tool-call path** (`SingleUserMcpToolProvider` ignores the overlay,
  `CannedChatClient` is hard-coded, Test only lists). Stated in Affected areas; Step 7 now uses an opt-in
  live test against real neo plus manual legs, and names the model-side residual.
- **H4 — a 400-char prompt cap let a payload hide past the prefix.** Full sanitized arguments are shown;
  oversize is denied, never truncated (AC25, AC26).
- **M1 — `?? prev` makes clearing impossible; API-layer ordering races.** `SaveAsync` always preserves;
  dedicated grain operations own the hash and the directory in `AgentRegistryGrain`'s order (AC5, AC6).
- **M2 — invalidate-on-connect churn.** Rate-limited per server (AC19).
- **M3 — read-only tools as an outbound channel.** URL-like arguments prompt (AC24); Step 0 records schemas.
- **M4 — Cf/bidi characters.** Added (AC28).
- **M5 — TestServer cannot prove a dead transport.** AC16/AC17 run on real Kestrel, AC16 is
  mutation-checked; the test host shape is corrected to `CreateSlimBuilder` + `UseTestServer`.
- **M6 — frontend/validation blockers for a blank endpoint.** `required`, the `!payload.endpoint` early
  return, validation order and `ToStored`'s `Trim()` are all named (Step 2, Step 6, AC7, AC32).
- **M7 — the regenerate route deviates from ADR 0022's upsert shape.** Kept and recorded as an
  alternative; the route-count comment goes to 64.
- **M8 — a WebSocket upgrade through Easy Auth on an excluded path is unmeasured.** A post-deploy check
  goes in the ADR's Unverified section.
- **Low:** every stale "six" site listed; `docs/features/remote-mcp-servers.md` is a firm step; decorator
  ordering pinned in AC22; the tester test wires the decorator and an empty registry (AC15); the
  prompt-queue delay is noted; the hook is cited directly; ADR 0070 is named as amended.

### Rejected
- None.


## Implementation divergences (executed 2026-09-17, branch `feature/local-browser-bridge`)

Where the shipped code differs from the plan above, and why. ADR 0076 is the authority on the result.

1. **Sockets are looked up by key hash, not by `(userId, serverId)`.** `IMcpConnectionFactory.ConnectAsync`
   receives a `StoredMcpServer` and no user id. The registry is still keyed and evicted by
   `(userId, serverId)`, but it also records the hash each socket authenticated with, and the decorator finds
   the socket by the owner's *stored* `BridgeKeyHash`. Side benefit: a socket opened with a since-regenerated
   key is never used, even before eviction. Changing the factory interface (and every fake of it) was the
   alternative, rejected as wider than needed.
2. **A rename does not evict (AC9 narrowed).** The plan said every management write, "update" included,
   closes the live socket. Only writes that *revoke* do: delete, disable, switch-away, regenerate. A PUT that
   leaves the server an enabled bridge (a rename) keeps the socket — evicting would make the save's own
   auto-test report "not connected" while the helper reconnects. `ManagementWrites_CloseLiveSocket` asserts
   both halves (rename first → no close; then the revoking write → one close).
3. **Critic findings on the first diff, all fixed in-branch:** (a) no WebSocket keep-alive *timeout* was set on
   either side, so a silently dead path (sleeping laptop) was never detected — both sides now set 20 s after a
   30 s ping (`LocalBridgeLimits.KeepAliveTimeout`, `BridgeClient`); (b) two helpers on one key displaced each
   other forever — the old socket now closes with `replaced by a newer connection` and the helper exits
   `BridgeExit.Replaced` (`DisplacedHelper_Stops`, mutation-checked; the reason string is pinned across both
   copies by `ReplacedCloseReason_Matches`); (c) a revoke landing between authentication and registration
   left the socket registered — the endpoint re-authenticates after `Register`; (d) the unused
   `LocalBridgeLimits.SendTimeout` was deleted.
4. **The endpoint is `app.Map`, not `MapGet`** — a WebSocket over HTTP/2 arrives as `CONNECT` (RFC 8441).
   Recorded at the call site and in ADR 0076.
5. **Extra helper file and type.** `tools/Agent42.LocalBridge/BridgeOptions.cs` (argument parsing, the
   env-only key, usage text); `ConsolePrompt` lives in `Program.cs` and uses ONE background stdin reader that
   discards stale lines, because a per-prompt `ReadLine` abandoned on timeout would swallow the next answer.
   `Program` is a class in a namespace, not top-level statements, so the test project (which references both
   the backend and the helper) does not see two global `Program` types.
6. **The test fake must answer `server/discover`.** MCP SDK 2.2.0 probes with it first and waits 5 s when the
   frame is dropped; the fake helper answers `-32601` (measured 5090 ms → 121 ms). Recorded as a gotcha.
7. **The "ordered above static files" assertion (AC22) is a convention, not a correctness rule.** The route is
   an endpoint; only `UseWebSockets` is order-sensitive. The test and the host comments now say so; the
   decorator-after-inner-factory half of AC22 is the load-bearing one.
8. **Step 7.2 harness trap.** Answers piped into the real console prompt before it appears are discarded by
   design; the first run lost all its answers to `dotnet run`'s build time. Re-run with the build separated.
9. **One neo tab left open.** The first live run failed on a regex (`opened page N` was the real format) after
   `tabs new`, leaving an `about:blank` tab (page 2) in the user's browser. The test now asserts the close.
10. **Plan-listed doc targets not changed:** `docs/integrations/README.md` needed nothing (its MCP mention is
    the calendar note). The browseros research spec was moved to `specs/completed/` with a superseded note.
11. **Distribution added after the first review (user request): GitHub Releases, not `dotnet run`.** The plan
    listed a signed installer / `dotnet tool` as out of scope and assumed `dotnet run`. The helper now ships as
    a self-contained single-file executable per platform from `.github/workflows/release-local-bridge.yml`
    (tag `local-bridge-vX.Y.Z`), with RID-conditional publish settings in the csproj, a `--version` flag, a
    releases link in the key panel, and `LocalBridgeReleaseTests` pinning the source text. Code signing stays
    out of scope. See ADR 0076 "Distribution".
    Later the same day it became automatic too: a push to `main` that changes the helper (or
    `Directory.Packages.props`) releases the next patch version; tags and manual dispatch still pick a version.
