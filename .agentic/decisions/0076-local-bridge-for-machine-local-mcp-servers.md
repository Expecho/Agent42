---
id: 0076
date: 2026-09-17
status: accepted
tags: [mcp, local-bridge, websocket, browser-automation, api-key, excluded-paths, security, egress, prompt-injection, no-migration]
supersedes: []
extends: [0022]
amends: [0002, 0024, 0042, 0070]
---

# Reach a machine-local MCP server through a helper that dials out, and confirm writes on that machine

## Context

The user wants Agent42 conversations — running on a cloud model, in production — to drive **BrowserOS
neo**, a browser-automation MCP server that listens only on their own machine (`127.0.0.1:9210`, measured;
its docs say 9239). The earlier research (`.agentic/specs/completed/plan-browseros-neo-as-an-mcp-tool.md`)
closed the two cheap routes: production cannot reach the user's loopback, and **a browser tab cannot relay
either** — neo answers 403 to any request carrying `Origin` or `Sec-Fetch-*` (measured). A non-browser
process on that machine that dials **out** to Agent42 is the remaining shape that works without exposing the
machine.

Measured on neo 0.0.53 (2026-09-17): 20 tools, ~31 KB of `tools/list`; `readOnlyHint: true` on `history,
snapshot, diff, read, grep, screenshot, pdf, wait`; explicit `false` on `name_session, save_skill,
mark_skill_run`; **no hint at all** on `tabs, tab_groups, navigate, act, download, upload, windows, evaluate,
run` — the last two execute arbitrary JavaScript in the signed-in browser. Neo negotiates MCP `2026-07-28`
(stateless; tab ownership rides a `session` argument echoed in `_meta["com.browseros.neo/session"]`).

## Decision

A console app, **`tools/Agent42.LocalBridge`**, runs on the user's machine, opens an authenticated WebSocket
to **`/api/local-bridge`** (mapped for every method, not `MapGet`: a WebSocket over HTTP/2 arrives as
`CONNECT`, RFC 8441), and relays MCP JSON-RPC between the backend and the local server. To the rest
of Agent42 it is one more user MCP server (ADR 0024) with a new auth mode, **`LocalBridge`**. Before relaying
any `tools/call` that is not declared read-only, **the helper asks the person at that machine**.

### The shape

- **Multiplexed channels over one socket.** The backend opens several MCP sessions to one server (the tool
  provider retains two overlay versions; the tester opens its own), so every `ConnectAsync` opens a
  channel — `{ch, type:"open"|"message"|"close", msg}`, one envelope per text frame — and the helper maps
  each channel to its own upstream session. Caps: 16 channels, 1 MiB per frame; overflow closes the
  **channel**, never the bridge (`LocalBridgeLimits`).
- **The backend stays the MCP client.** `LocalBridgeChannelTransport` is a `TransportBase`, so
  `McpClient.CreateAsync` + `ListToolsAsync` run exactly as in `McpConnectionFactory`, and bridged tools are
  ordinary `McpServerTools` — per-server/per-tool gating (0026/0058), truncation (`TruncatingMcpTool`) and
  seeding (0067) apply unchanged. `LocalBridgeConnectionFactory` **decorates** `IMcpConnectionFactory` and is
  registered after it in both silo hosts.
- **Not connected fails fast.** With no live socket, `ConnectAsync` throws `LocalBridgeNotConnectedException`
  at once; the provider skips the server without spending its 10 s budget, and Test says **"local bridge not
  connected"** — the user's next action is to start the helper, not to fix a URL.
- **Auth is a per-server bridge key** (`sk_bridge_`, `BridgeKey` — a copy of ADR 0022's `ApiKey`): minted on
  create or a switch to the mode, shown once, only its SHA-256 stored (`StoredMcpServer [Id(14)]
  BridgeKeyHash`), resolved through a global `ILocalBridgeKeyDirectoryGrain`. It arrives **only** in the
  `X-Api-Key` header — a query string would leak into logs and, the real reason, would let a web page open
  the socket (browsers cannot set headers on a WebSocket). The endpoint is on `excludedPaths`, so it
  self-enforces and fails closed: directory hit **and** the owner's overlay still holds that server,
  enabled, `LocalBridge`, with that exact hash — else one uniform 401, decided before the upgrade check.
- **The grain owns the hash.** `SaveAsync` carries it forward **unconditionally** and never writes it (a
  `?? prev` clause would make clearing impossible); only `MintBridgeKeyAsync` / `ClearBridgeKeyAsync` /
  `DeleteAsync` change it, each doing the directory work inside the one non-reentrant grain in ADR 0022's
  order (regenerate deregisters the old hash first; clear/delete write first).
- **Revocation reaches live sockets.** The directory is consulted only at upgrade, so every revoking
  management write — delete, disable, switch-away, regenerate — evicts the server's socket from the
  in-process `LocalBridgeRegistry` (resolved optionally; most `MapAgentApi` hosts have none). A running
  helper reconnects, meets 401, and **stops** rather than retrying. The endpoint re-authenticates **after**
  registering the socket, so a revoking write that lands between the first check and registration (and so
  found nothing to evict) still closes it.
- **Sockets are found by key hash, evicted by server.** `IMcpConnectionFactory` is handed a
  `StoredMcpServer` and no user id, so the registry records the hash each socket authenticated with and the
  decorator looks it up by the owner's **stored** hash — globally unique and read from the owner's own
  overlay. A useful side effect: a socket that authenticated with a key since regenerated is never used,
  even before it is evicted.
- **A second helper replaces the first, and the displaced one stops.** The old socket is closed with the
  reason `replaced by a newer connection`, which the helper recognises and exits on (`BridgeExit.Replaced`)
  — otherwise two helpers on one key would displace each other about once a second, failing every in-flight
  call each time.
- **Dead paths are detected**, not just dropped connections: both sides set a WebSocket **keep-alive
  timeout** (20 s after a 30 s ping). Without it a ping is sent but no pong awaited, so a laptop that sleeps
  without a FIN or RST would leave the socket "open" — and a pending `tools/call` holding the non-reentrant
  grain turn — until TCP gave up.
- **A connect invalidates the tool cache** (ADR 0069's mechanism), because `AgentGrain` resolves MCP tools
  once per activation — rate-limited to once per 5 minutes per server, and never on disconnect, since each
  bump is a Cosmos write and two of them tear down the user's *other* warm sockets.
- **Regenerate is its own route**, `POST /api/mcp-servers/{id}/bridge-key`, because a Save also re-runs the
  auto-test. The save that mints a key does **not** auto-test (no helper can hold that key yet — ADR 0064's
  OAuth-no-token reasoning).

### The confirmation gate (helper)

- The helper learns annotations by **parsing the `tools/list` responses it relays**. A call passes unprompted
  only if its tool's `readOnlyHint` is exactly `true` **and** no string argument looks like a URL
  (`scheme:` or `//`) — absent hint, `false`, an unknown tool and a call before any listing all prompt.
- Prompts are serialized and time out after 60 s → deny. A deny is answered **by the helper** with a JSON-RPC
  **result** `{content:[…], isError:true}` on the same id, so the model reads why; the call never reaches the
  upstream. `--auto-approve <tool,…>` pre-approves named tools; there is no approve-everything switch.
- The prompt text is attacker-influenced, so C0/C1 controls (ESC included), DEL and Unicode format
  characters (Cf — the bidi overrides) are stripped, the tool name stands alone on its own line, and the
  arguments are shown **in full**. Past 20,000 characters the call is denied, never truncated — a prefix
  would let a payload hide behind a harmless start.
- The console reader is one background thread; each prompt discards lines typed before it appeared, so a
  stale `y` can never approve a call the user has not seen.

### Distribution (user request, 2026-09-17)

The helper is downloaded, not built: `.github/workflows/release-local-bridge.yml` publishes one
**self-contained, single-file** executable per platform (win-x64/arm64, linux-x64/arm64, osx-x64/arm64,
compressed, ~38 MB) as a GitHub Release when a **`local-bridge-vX.Y.Z`** tag is pushed (or on manual dispatch
with a version, which creates the tag). The version is stamped from the tag and printed by `--version`; the
native runners smoke-test that. `SHA256SUMS.txt` is attached. Windows and Linux assets are cross-published
from Ubuntu; **macOS assets are built on a macOS runner**, where the SDK ad-hoc signs the apphost that Apple
Silicon requires. The SelfContained/PublishSingleFile settings live in the csproj **conditional on a runtime
identifier**, so `dotnet publish -r <rid>` reproduces an asset locally while the solution's plain
build/test stays framework-dependent. `deploy.yml` is untouched: it triggers on pushes to `main` only and its
Dockerfile never copies the helper. The UI's key panel links to the releases page and shows
`./Agent42.LocalBridge --server <origin>`.

## Alternatives considered

- **Reach the user's machine from production** (tunnel, port-forward, a public neo). Rejected in the prior
  research: it exposes a signed-in browser to the internet, which is the thing being protected.
- **Relay from a browser tab** (a page on our origin talking to `127.0.0.1`). Rejected on measurement: neo
  403s any `Origin`/`Sec-Fetch-*` request — deliberately, and correctly.
- **Approve in the chat UI.** Rejected (user decision): the person approving should be at the browser being
  driven, and a backend gate needs an approval loop this app does not have (ADR 0049). It would also not
  exist for reminder / agent-run / agent-mcp turns, which have no one watching.
- **One MCP session per socket** (a socket per `ConnectAsync`). Rejected: the helper would need to accept
  several concurrent sockets per server and "a second connection replaces the first" would stop meaning
  anything; channels keep one authenticated socket per server.
- **Relay raw bytes instead of being the MCP client.** Rejected: the backend would need a second tool path
  beside `McpClientTool`, losing the gating/truncation/seeding the existing path gives for free.
- **Compile-link one envelope file into both projects.** Rejected in review: the test project references both
  assemblies, so the same public type would exist twice (CS0433), and this repo has no `InternalsVisibleTo`.
  Two copies pinned by `Envelope_WireFormatsMatch` instead; a shared `src/` library was judged not worth a
  project for a three-field record.
- **Regenerate through the upsert** (ADR 0022's `regenerateKey` on PUT). Rejected for this surface only: it
  would force a Save, and a Save re-runs the auto-test and (on disable/switch) evicts the socket.
- **A distributed socket registry.** Out of scope — see consequences.
- **Distribute as `dotnet run` from a checkout, or as a `dotnet tool`.** Rejected (user decision): both need
  a .NET SDK/runtime on the machine being driven. A framework-dependent single file was rejected for the same
  reason; self-contained costs ~38 MB per asset, which is the price of "download and run".
- **Sign and notarize the executables.** Deferred: it needs certificates and secrets this repo does not have.
  The release notes and docs tell the user how to get past SmartScreen and Gatekeeper instead.
- **Attach assets with the `upload-artifact`/`action-gh-release` actions.** Not used: every step uses the
  preinstalled `gh` CLI with the workflow token, so the release adds no third-party action to pin.

## Consequences

- **Headline: a web page the agent reads can steer actions in the user's signed-in browser.** ADR 0024's
  inbound prompt-injection channel meets tools that click, type and run JavaScript. The helper prompt is the
  mitigation, and only for tools not declared read-only: `read`/`snapshot`/`screenshot`/`history` run
  without asking, so page content **and browsing history** reach the cloud model (ADR 0013 egress) whenever
  the model chooses. Narrow the server's tools (ADR 0058) — `history` especially. "Read-only" is the
  upstream's own claim; the URL rule is defence in depth, since measured, none of neo's read-only tools
  takes a URL or navigates (all target an existing `page` id).
- **`SeedsFetchableUrls` must stay off for a bridge server** (the ADR 0067 Gmail reasoning, at higher
  volume). Documented, not forced in code.
- **First WebSocket and a seventh `excludedPaths` entry** — `/api/local-bridge`, **exact** (the route has no
  sub-paths; ADR 0042's semantics). A key holder gets a socket into our single replica; the channel and
  frame caps bound what one socket can cost. **Amends ADR 0002/0042.**
- **`/api/agent-run` and `/api/agent-mcp` key holders can now indirectly request browser actions on the
  owner's machine**, because any turn kind may use bridged tools while the bridge is connected (user
  decision). Each write still meets the helper's prompt. **This amends ADR 0070**: its bind filter
  (`WithoutMemoryOrPush`) does not remove MCP keys, so that surface gains bridged tools.
- **A second single-replica dependency** (ADR 0054 was the first): the registry is in-process, and
  `infra/main.bicep` pins one replica. A grain on another replica would see every bridge as not connected.
- **Lifecycle, accepted rather than fixed.** A bridge that connects after a chat activated reaches that chat
  on its next activation; after a disconnect with no reconnect, a new activation may get a cache hit whose
  bridged tools fail when called; a reset socket faults pending calls promptly (the transport
  `SetDisconnected`s — measured), a silently dead one within the keep-alive timeout (configured, not
  measured), and no session resumption exists. There is **no per-call deadline**: a live helper whose
  upstream never answers holds the call as long as the socket stays healthy. Serialized prompts with a 60 s timeout can hold a
  non-reentrant turn for minutes when several writes queue.
- **Tool schemas ride every turn** (~31 KB for neo) — on GitHub Models' 16k input cap, narrowing is a
  practical requirement.
- **No new package, secret, Cosmos container or migration** (trailing `[Id(14)]`). New: one grain type
  (`localbridgekeydirectory`), one project under `tools/` (never deployed — released, see Distribution), `sw.js` `CACHE` → `v38`.

## Verification

Evidence was produced in-session on 2026-09-17.

**Step 0 spike** (MCP SDK 2.2.0, neo 0.0.53): `McpClient` ran unchanged over a `TransportBase` subclass
relaying to an `HttpClientTransport` (`relay.SessionId=<null>`, `tools=20`); no notifications crossed;
`SetDisconnected(ex)` faulted a pending request in **1 ms**; `DisposeAsync` is abstract; without
`SetDisconnected` in it, `McpClient.DisposeAsync` **never returns**. A second measurement mattered for the
tests: SDK 2.2.0 opens with a **`server/discover` probe** and falls back to `initialize` only on an error
or after a **5 s** timeout (`DiscoverProbeTimeout`) — a fake server that silently drops the probe makes every
connect take 5 s; answering `-32601` made it 121 ms. Neo answers the probe (connect 276 ms).

**Opt-in live test** (`LocalBridgeLiveTests`, `AGENT42_BRIDGE_E2E=1`, real Kestrel + real helper + real neo):

| Step | Observed |
|---|---|
| `tools/list` through the bridge | 20 tools, negotiated `2026-07-28` |
| `tabs new` (not read-only) | prompted, allowed → `opened page N` |
| `snapshot` (read-only, no URL) | no prompt, `isError=False` |
| `navigate` denied | `isError=True`, `The user declined this browser action.`; `read` shows no "Example Domain" |
| `navigate` allowed | `navigated (url) -> https://example.com/`; `read` contains "Example Domain" |
| Regenerate (grain mint + eviction) | helper: `disconnected. / reconnecting in 1 s. / …rejected the bridge key (401)… Stopping.` → `Unauthorized` |

Neo wraps every page read in `[UNTRUSTED_PAGE_CONTENT nonce=…]` markers — useful, but a mitigation in the
upstream, not one this feature relies on.

**Real console prompt** (stdin piped, stdout captured as bytes), a `run` call named
`\e[2K\e[1Gsnapshot‮nur` with ESC and U+202E in its script: the terminal showed
`Tool: [2K[1Gsnapshotnur`; **0 ESC bytes and 0 U+202E bytes** reached stdout (inside the JSON the
serializer had already escaped ESC as ``); 3 prompts for 4 reviews. Decisions: `n` → declined;
20,001-character script → "too large to review" without being shown; `y` → allowed; silence → "did not
answer in time". Harness trap recorded: input typed before a prompt appears is discarded by design, so a
first run whose piped answers arrived during the build had every answer dropped.

**DevHost UI leg** (Chrome, real buttons, `confirm` stubbed): Local bridge hides the endpoint and clears its
`required` (form valid); Save reveals a 53-character `sk_bridge_` key and the `dotnet run … --server
http://localhost:61326` command and does **not** auto-test; the row reads `local bridge · 🔑`; Test with no
helper → `✗ local bridge not connected`; the real helper started → Test → `✓ ok — 20 tools: tabs,
tab_groups, history, navigate, …`; Regenerate → key rotated, the helper exited on 401 (exit code 1), Test →
not connected. DevHost cannot run the **chat** tool-call path (`SingleUserMcpToolProvider` ignores the
overlay; `CannedChatClient` is canned), which is why the live test above exists.

**Mutation check:** removing the `FaultAll` call in `LocalBridgeSession.RunAsync`'s `finally` makes
`SocketDrop_FaultsPendingCall` (real Kestrel) fail — the pending call completed only when the 10 s guard did.
Likewise, making the helper ignore the `replaced` close reason makes `DisplacedHelper_Stops` (two real
`BridgeClient`s on one key) fail with a `TimeoutException` — the displaced helper kept reconnecting. (A first
attempt at that mutation added a `false &&` and failed to compile on S3776, so the "passing" run used the old
binary; it was redone with a string mutation after `RunAsync` was split.)

## Unverified

- **The production premise the whole feature rests on:** that ACA ingress plus the Easy Auth layer pass a
  WebSocket `Upgrade` through on an excluded exact path. ADR 0042's lesson is that `excludedPaths` behaviour
  is proven only after deploy. Post-deploy check: an upgrade to `wss://chat.agent42.nl/api/local-bridge`
  with a valid key → **101**; without one → **401 from our endpoint** (body `Missing or invalid X-Api-Key.`),
  not an Easy Auth 302/401.
- **The ACA ingress idle timeout for WebSockets** — unmeasured; both sides ping every 30 s and the helper's
  reconnect loop is the backstop.
- **The keep-alive timeout firing on a genuinely dead path** (sleep, Wi-Fi loss) — configured on both sides,
  but only a reset connection was exercised; no test simulates a silent black hole.
- **A model actually receiving and explaining a deny result** — no model runs offline.
- **The release workflow itself** — it runs only on a tag, so nothing here has executed it. Measured locally
  instead: `dotnet publish -c Release -r win-x64|linux-x64|osx-arm64 -p:Version=0.1.0-test` with the csproj
  settings alone gives ONE file each (38.4 / 40.1 / 37.3 MB, no separate `.pdb`); the win-x64 `.exe` printed
  `0.1.0-test` for `--version`, connected to DevHost, closed each channel cleanly when its upstream was down
  (neo had been closed by then, so Test said "could not connect"), and exited 1 on the 401 after Regenerate.
  **Not verified:** that the osx assets actually launch on a Mac (the ad-hoc signing claim is the SDK's
  documented macOS-host behaviour, not something run here), the arm64 assets, and the first real release run.
