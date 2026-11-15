planned-against: main @ d09be0a

# Plan: MCP Apps host support (SEP-1865, extension `io.modelcontextprotocol/ui`)

## Overview

Agent42 is an MCP *client*. Today an MCP tool result is flattened to bounded text (ADR 0004/0109). MCP Apps lets a
tool declare an interactive HTML view (`_meta.ui.resourceUri` -> a `ui://` resource, MIME
`text/html;profile=mcp-app`) that the host renders in a sandboxed iframe, fed the tool input/result over a
postMessage JSON-RPC dialect. The goal: when a user's own MCP server ships such a tool, the view renders inline in
the chat bubble, can call that server's tools, and can (after confirmation) message the model. The spec is final
(2026-01-26); Claude, VS Code Copilot, Goose and others already host it, so servers exist to test against.

Sources (read at plan time, re-read before implementing, they are the contract):
- https://modelcontextprotocol.io/extensions/apps/overview
- https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx (host method tables,
  `hostContext`, `styles.variables` names, CSP field -> directive mapping)

## Decisions made with the user (interview, 2026-11-15)

| Question | Decision | Rejected |
|---|---|---|
| Sandbox | **Server-served frame + CSP header**, the ADR 0084 `/frame` pattern. A route serves the view HTML with `Content-Security-Policy: sandbox allow-scripts; ...` built from the resource's declared csp. No second origin. Record as a **deliberate deviation** from the spec's "MUST use a double iframe on a different origin" in the ADR: equivalent isolation (opaque origin, header CSP the view cannot loosen), views still just talk to `window.parent`. | spec-literal proxy page (same origin anyway), separate sandbox subdomain (DNS/cert/Bicep/Easy Auth) |
| Egress | **Honor the declared CSP** (`connectDomains`, `resourceDomains`, `frameDomains`, `baseUriDomains`). This is an **ADR 0013 extension** and must be written as one. Plus a per-server on/off switch. | static-only, deny-all |
| Replay | **Persist the tool result to blob** (capped) so a card re-renders after reload. | live-only |
| View capabilities | `tools/call` to its own server, `ui/open-link`, `ui/message` + `ui/update-model-context` (both **confirm-gated**), display modes (inline + fullscreen) | - |

Judgment calls I made that the user has NOT explicitly confirmed (called out for approval): (1) the per-server switch
`AppsEnabled` is a trailing persisted field that **defaults OFF for every stored server** (no silent egress
widening for servers that exist today; CLAUDE.md cross-cutting 13 / ADR 0008), while the server editor **pre-ticks it
for a newly added server** so new servers get apps by default; (2) `pip` display mode is declined (returns the
current mode); (3) public share links and Android do not render apps (text chip only); (4) replay is inert until a
user gesture (see finding 11).

## Affected areas (verified against the tree at d09be0a)

Backend MCP (Backend has the `ModelContextProtocol` package, **Agents does not**, `src/Agent42.Backend/Agent42.Backend.csproj:38-39`):
- `src/Agent42.Backend/Mcp/McpElicitationClient.cs` - client options; add the `Extensions` capability here so the remote and local-bridge factories cannot drift (ADR 0064/0095 lesson). `ClientCapabilities.Extensions` exists in SDK 2.2.0.
- `src/Agent42.Backend/Mcp/McpConnectionFactory.cs` - `McpServerConnection`, `TruncatingMcpTool`; `LocalBridgeConnectionFactory.cs` - the second factory.
- `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` - tool filtering (`ConnectAllAsync` ~:139-149) and the version cache that already owns live `McpClient`s.
- `src/Agent42.Backend/Mcp/McpConnectionTester.cs` - second writer of tool names; must apply the same visibility rule.
- `src/Agent42.Backend/Mcp/McpToolResultShaper.cs` - model-facing shaping stays as is.
- `src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs:50-58` - `McpServerTools` (no `McpClient` on it, by design).
Agents/grain:
- `src/Agent42.Agents/Conversations/AgentStreamEvent.cs:111-160` - last id is `[Id(21)]`; new trailing `[Id(22)]`.
- `src/Agent42.Agents/Conversations/AgentGrain.cs:1085-1091` - `ProjectStreamedContent` makes `ToolCallEnd`.
- `src/Agent42.Agents/Conversations/ChatToolCall.cs` - replay projection; trailing `[Id(4)]`.
- `src/Agent42.Agents/Conversations/TurnContext.cs` - per-run side channel (rule 6); it has NO conversation/user/call id, so the call id comes from `FunctionInvokingChatClient.CurrentContext?.CallContent.CallId` as `TruncatingMcpTool` already does.
- `src/Agent42.Agents/Artifacts/AgentGrain.ArtifactTools.cs:189-195` + `ArtifactRecord.cs` - the blob + state-record exemplar.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:294-345` - final MCP tool list (wrap order ReauthNotice(UrlSeeding(Elicitation(t)))).
Endpoints: `src/Agent42.Backend/Artifacts/AgentApi.Artifacts.cs` (exemplar), `src/Agent42.Backend/Api/AgentApi.cs:33-65` (`MapAgentApi` composition root), `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs:207-216` + `ChatStreamJson.cs` (wire).
Frontend: `src/Agent42.Backend/wwwroot/shared/lib/artifactpane.js` (iframe exemplar), `js/send.js` (~:285 `toolCallEnd`), `js/artifacts.js`, `js/main.js` (composition root), `js/mcp.js` (server editor), `sw.js` (`CACHE = "agent42-shell-v80"`, SHELL), `js/clienttools.js` (confirm UI to reuse).
Offline: `tools/Agent42.DevHost/Program.cs:330-346` `OfflineMcpConnectionFactory` (fixed `AIFunctionFactory` tools, cannot host a resource today).
Android: `tools/Agent42.Android` (chat is native; WebView is sign-in only, `SignInScreen.kt`); NDJSON golden file pinned by `ChatStreamJson.cs:8`.
Tests/guards to extend: `AgentApiReachabilityTests`, `ProjectLayoutTests` (`ServiceWorkerShell_ListsExactlyTheModuleDirectory`, `EveryFrontEndModule_IsReachableFromTheEntryPoint`), `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` (asserts the literal CACHE), `UserMcpToolProviderTests` (`FakeMcpConnectionFactory`), `ArtifactsApiTests` (`AssertSandboxHeaders` shape), `GrainStorageSerializerTests`.

## Context & constraints (what the executor cannot cheaply re-derive)

Governing ADRs: 0004 (2 MB session; tool results persisted), 0013 (egress; **this plan extends it**), 0024/0026/0058 (MCP config + per-server/per-tool gate), 0064 (one code path for connect and test), 0084 (artifact pane, `/frame` CSP, no second origin), 0089 + 0106 (client tools, `TurnContext`), 0095 (elicitation: `McpElicitationRouter` is how a Backend-only MCP concept reaches the grain), 0104 (trailing context, never instructions), 0109 (MCP result shaper, images, `TurnContext` side channel), 0127 (local storage path), 0039 (flat namespace). Gotchas to read first (headings in `.agentic/gotchas.md`): "`/frame` safety is the CSP `sandbox` HEADER", "A multi-part MCP result is an `AIContent[]`...", "Remote MCP servers: a live-socket cache...", "An agent tool's RETURN value is persisted...", "Per-run state is a `TurnContext`...", "Every MR that touches `wwwroot/js/` must bump `CACHE`", "A frontend module unreachable from `main.js`...", "Cosmos grain storage is JSON with `$type`", "A collection expression into `IReadOnlyList<T>`...", "MCP SDK 2.2.0 opens with a `server/discover` probe".

Key findings:
1. **The grain cannot reach an `McpClient`** (`McpServerTools` carries tools + `McpElicitationRouter` only; the Agents project has no MCP package). Everything that needs the live client (resource read, view-initiated `tools/call`) therefore lives **Backend-side**, behind a new registry that resolves `(userId, serverId)` through `UserMcpToolProvider`'s cached connections. The grain only ever sees plain records (a small `McpAppRef` in Agents, mirroring how `McpElicitationRouter` crosses the boundary).
2. **The model's tool result and the view's tool result differ.** The model gets today's shaped, bounded text. The view needs the raw `CallToolResult` (content + `structuredContent`). Capture the raw result inside `TruncatingMcpTool` for app tools only, bound it, and hand it to the run via a `TurnContext` side channel keyed by call id (the ADR 0109 `RecordToolImage` shape). It must never enter the persisted session (ADR 0004). Whether `AIFunction.InvokeAsync` exposes `structuredContent` is **unverified**; Step 0 decides between `McpClientTool.CallAsync` (public, returns `CallToolResult`) inside the wrapper vs another route.
3. **Visibility.** Tools whose `_meta.ui.visibility` lacks `"model"` must be absent from the agent's tool list. Filter at the one chokepoint (`UserMcpToolProvider.ConnectAllAsync`), dropping from BOTH `tools` and `discovered` names so the picker, the gate and the model agree (`McpConnectionTester` applies the same rule: second writer, ADR 0064). App-only tools stay reachable through the registry for the view. Calls from a view are accepted only for tools whose visibility includes `"app"`, only on the same server; never cross-server.
4. **Only interactive, browser-capable runs get app payloads.** Gate on `TurnContext.Interactive` plus a client capability the browser advertises on the chat request (like `clientTools`). Background runs, reminders, agent-run, email, chat gateway, fleet and Copilot kind keep text-only behavior. The payload lives on the stream event, not in `FunctionResultContent`, so the session is unchanged.
5. **Reload replay.** `ToolCallEnd` is not stored; history is projected from the session. Persist per app call: blob `{callId}-app.bin` (octet-stream, via `IAttachmentStore`, owner key `{userId}/{conversationId}`, exactly like artifact versions) + a trailing `[Id(n)]` record list on `AgentGrainState` (default empty, written at the end-of-turn persist site like `Artifacts`; no migration; prove on the Cosmos JSON path, rule 2) holding `{CallId, ServerId, ToolName, ResourceUri, AttachmentId, Size}`. `GetHistoryAsync` projects it onto `ChatToolCall.McpApp`. **Incognito refuses to persist** (the card renders live only), per ADR 0034 and the artifact precedent. `IAttachmentStore` has no delete and artifacts' blobs already survive purge: accept the same here and write it in the ADR; do not invent a delete path. Forks do not copy apps (the stub chip remains). Cap per result: 64 KB; over-cap = refuse to render an app for that call, text chip only. Lifecycle (cap, purge, prune, edit tail, fork, share projection): see finding 13.
6. **Frame route is untrusted-HTML-from-a-third-party served on our origin.** Same traps as ADR 0084 and the SearXNG proxy gotcha ("Proxying a third-party app onto our own origin", `.agentic/gotchas.md`, which MEASURED that an opaque-origin page can still submit a cookie-bearing form to a same-origin URL and that `'self'` matches every same-origin URL, so it is useless here). The header CSP must be on EVERY response of the route including errors. **The literal policy** (the test asserts the exact string, the way `ArtifactsApiTests.AssertSandboxHeaders` does for `AgentApi.ArtifactCsp`):
   `sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline' {resourceDomains}; style-src 'unsafe-inline' {resourceDomains}; img-src data: blob: {resourceDomains}; font-src data: {resourceDomains}; media-src data: blob: {resourceDomains}; connect-src {connectDomains or 'none'}; frame-src {frameDomains or 'none'}; base-uri {baseUriDomains or 'none'}; object-src 'none'; form-action 'none'; frame-ancestors 'self'`.
   NEVER `allow-same-origin`, and NEVER `'self'` anywhere (the spec's own restrictive default uses `'self'`; we deliberately substitute `'none'`/`data:` and record that in the ADR). **Declared-domain validator** (`McpAppCsp.Validate`, pure, unit-tested): accept only `https://host[:port]` / `wss://host[:port]` and `https://*.host` wildcards with at least two labels after the `*.`; reject everything else, including any entry with a quote, semicolon, comma, whitespace or control char; reject schemes `http`, `ws`, `data`, `blob`, `javascript`, `file`; **reject our own origin/host** (compare against the request host and the forwarded host, case-insensitively, including the `*.` parent), `localhost`, loopback, link-local, RFC1918/ULA literals and `*.local`/`*.internal` suffixes (a third-party view must not probe the user's intranet or this app); reject bare-TLD or global wildcards (`https://*`, `https://*.com`); cap each list (e.g. 16 entries, 256 chars). An invalid entry is dropped with a log line (fail closed to a narrower policy), never passed through. No new authenticated GET with side effects (the artifact gotcha applies: a framed same-origin GET carries the cookie).
7. **Origin of messages is opaque** (`"null"`), so the host must authenticate messages by `event.source === iframe.contentWindow` plus message-shape validation, and reply with `targetOrigin "*"`. Say so in the gotcha. Send NOTHING to the view before `ui/notifications/initialized`; send `tool-input` before `tool-result`; send `ui/resource-teardown` before removing the iframe.
8. **`ui/message` and `ui/update-model-context` are a prompt-injection channel from third-party HTML into the conversation.** Both are confirm-gated with the existing confirm UI and its sanitization (`js/clienttools.js`, gotcha "bridge confirmation gate": prompt is sanitized and never truncated). `ui/message` becomes an ordinary user turn through the existing send path, only when no turn is streaming (else answer the view with an error). `ui/update-model-context` is held client-side per card (last write wins, bounded 8 KB) and sent as `appContext` on the next chat request; the server injects it as **trailing context, labeled as untrusted app-supplied data** (ADR 0104 shape, never the instructions, never persisted in the session).
11. **Replay must not re-execute tools.** Many views call `tools/call` on init to refresh or poll, so a reload would re-run server tools (possibly side-effecting). Decision: a card rendered from a **stored** result starts *inert*: the host renders the view and feeds it the stored `tool-input`/`tool-result`, but answers `tools/call` with a JSON-RPC error until the user clicks an explicit "Activate app" control on the card (a real user gesture). A live card (rendered during the turn that produced it) is active at once. Test it (AC16).
12. **The registry may connect on demand.** `UserMcpToolProvider` fills its cache only when a grain activation calls `ConnectAllAsync` (it keeps 2 versions per user and disposes older), so after a restart or a config save no Backend connection exists for an old card. The registry therefore resolves through `GetForUserAsync(userId)` (the same `ConnectAllAsync` path, ADR 0064, same bounded cache, same 10 s connect cap), not a cache peek, and fails closed only when the server is disabled/`AppsEnabled` is off/unreachable. The view HTML is fetched live while only the result is stored, so a server that changes its `ui://` resource can show an old result in a new view: accepted, written in the ADR (not persisting the HTML).
13. **State lifecycle is its own design, not the artifact precedent.** `Artifacts` is a keyed dictionary with a per-conversation cap and fork-copy logic; the app record list is keyed by call id. Required: a cap (`MaxMcpAppsPerConversation`, 20: past it the OLDEST record is dropped, its blob orphaned like artifact blobs, and that card degrades to the text chip), `purge` clears the list next to `Artifacts` (`AgentGrain.cs` purge block), retention prune and edit/regenerate-last-turn tail truncation drop the records of the dropped turns (find both sites: `AgentGrain.Retention.cs`, `AgentGrain.EditFork`/`ConversationForking.cs`), `ConversationFork` does NOT copy apps (name the code path and test it), and the share projection drops `ChatToolCall.McpApp` (a shared turn must never leak the stored result). Tests for each.
9. **No Easy Auth change.** All new routes are normal authenticated `/api/...` routes under the existing cookie; none goes in `excludedPaths`. Sub-resources the view loads from `resourceDomains` come from the third party, not from us.
10. Single replica / per-run state / flat namespace rules (CLAUDE.md cross-cutting 1, 6, 7, 12) apply: new persisted types stay in the flat root namespace with `[Alias]`; no grain field for per-run app state.

## Steps

Branch: `feature/mcp-apps` from `main @ d09be0a`. Each step lands with its tests (no deferred tests).

**Step 0 - Spikes (use `/spike-dotnet`, one file-based app outside the repo, pinned to `Directory.Packages.props` versions).** Prove, with numbers, before any design hardens:
 (a) `Tool.Meta` is a `JsonObject` carrying `ui.resourceUri` / `ui.visibility` after `ListToolsAsync` against a real MCP App server (the `ext-apps` repo's `basic-server-vanillajs` is the fixture); the legacy flat `ui/resourceUri` key is also read.
 (b) `McpClientTool.CallAsync` returns `structuredContent` and `content`; what `AIFunction.InvokeAsync` returns for the same call; whether `DelegatingAIFunction` can reach the inner `McpClientTool` (decides how `TruncatingMcpTool` captures the raw result).
 (c) `ClientCapabilities.Extensions` serializes as `capabilities.extensions["io.modelcontextprotocol/ui"].mimeTypes` and a server then exposes UI-bearing behavior; check it does not trigger the 5 s `server/discover` probe regression (gotcha) and that servers ignoring the extension still connect.
 (d) `ReadResourceAsync("ui://...")` returns `TextContent`/`BlobResourceContents` with `Meta.ui.csp` accessible.
 Verify: spike output pasted into the step report. A failed assumption changes the plan; stop and report instead of working around.

**Step 1 - Capability + metadata model (Backend + Agents types).** Land `StoredMcpServer.AppsEnabled` here (trailing `[Id(n)]`, default false; JSON-path serializer test) so the gate exists before anything depends on it. Introduce the Backend seam `IMcpAppClient` (read a `ui://` resource, call a tool; no MCP SDK types in the signature) as an optional trailing member of `McpServerConnection`, implemented over the live `McpClient` by `McpConnectionFactory`/`LocalBridgeConnectionFactory`, and over fixed data by `FakeMcpConnectionFactory` and DevHost's `OfflineMcpConnectionFactory` (update `SingleUserMcpToolProvider` and any other `IUserMcpToolProvider` fake). Advertise the extension in `McpElicitationClient.Options` only for a server with `AppsEnabled` (shared by both factories; Options takes the flag). Add `McpAppRef` (Agents, flat namespace, `[GenerateSerializer]` + `[Alias]`) and a Backend parser for `_meta.ui` (resourceUri, visibility defaulting `["model","app"]`, both key spellings). Apply the visibility filter in `UserMcpToolProvider.ConnectAllAsync` and `McpConnectionTester` (names + tools), keep app-only tools in a per-connection map. Carry the per-tool `McpAppRef` to the grain on `McpServerTools` (trailing optional field, like `Elicitation`). Verify: new unit tests over `FakeMcpConnectionFactory` (visibility hides a tool from tools AND discovered names; tester agrees; legacy key honored; no `_meta` unchanged).

**Step 2 - Backend registry for live access.** `IMcpAppRegistry` (Backend): `ReadUiResourceAsync(userId, serverId, uri)` (must be `ui://`, MIME must be `text/html;profile=mcp-app`, bounded HTML size, bounded cache keyed by config version + uri) and `CallToolAsync(userId, serverId, tool, args)` (visibility must include `"app"`; same server only; bounded args/result; honors the per-server tool selection from the user's config where the model path does). Resolves connections through `UserMcpToolProvider.GetForUserAsync` (on-demand connect via the same `ConnectAllAsync` path, finding 12; never a second socket; the cache entry exposes the `IMcpAppClient` handle per server id). Fails closed when the server is disabled / `AppsEnabled` false / unreachable. Register `IMcpAppRegistry` in `AddAgent42Api()` or resolve via `GetService` so DevHost and test hosts work (rule 7). Verify: unit tests with a stub connection (app-only call allowed, model-only call from a view rejected, cross-server rejected, non-`ui://` rejected, wrong MIME rejected, oversize rejected).

**Step 3 - Raw result capture + stream event.** In `TruncatingMcpTool` (app tools only) capture the raw `CallToolResult` per Step 0(b), bound to the 64 KB cap, and record it on the run's `TurnContext` keyed by call id (new `RecordAppResult`, fails closed on a closed context, mirrors `RecordToolImage`). `ProjectStreamedContent` attaches `McpApp` (`{serverId, toolName, resourceUri, result}`) to `ToolCallEnd` as `AgentStreamEvent [Id(22)]` only when `TurnContext.Interactive` and the browser advertised app support. Update `ChatStreamJson` consumers and the Android NDJSON golden file; check `StreamEvent.kt` tolerates unknown keys (if not, make it do so; Android renders text only). Also emit the tool INPUT for the view: `toolCallStart` already carries `toolInput`; reuse it, no new event. Verify: grain test (an app tool call on an interactive, app-capable run yields `McpApp` on `ToolCallEnd`; a background run, a reminder, a browser that did not advertise app support, and an `AppsEnabled`-off server each yield none; an incognito interactive run yields it live but persists nothing (Step 4); session JSON contains no raw result: assert size) + `GrainStorageSerializerTests` for the new event field.

**Step 4 - Persistence for replay.** At the end-of-turn persist site save the capped raw result to blob and add the `[Id(n)]` record list to `AgentGrainState` (find the next free id; trailing, default empty, never a collection expression into `IReadOnlyList<T>`). Skip when ephemeral (incognito). Project into `ChatToolCall.McpApp` (trailing `[Id(4)]`) in `GetHistoryAsync`. Implement the full lifecycle of finding 13 (cap, purge, retention prune, edit/regenerate tail, fork, share projection). Verify: grain tests (turn then reload history shows `McpApp` with the right result; incognito shows none; the 21st app call drops the oldest record and degrades that card to the chip; purge clears the list; a pruned turn and an edited-away tail leave no dangling record; a fork carries none), a Cosmos JSON-path round-trip test of the new state field, and a share-projection test (the shared turn has no `McpApp`).

**Step 5 - Endpoints (`AgentApi.McpApps.cs`, new `MapMcpAppEndpoints` called from `MapAgentApi`).**
 - `GET /api/mcp-apps/{serverId}/frame?uri=` serves the view HTML; CSP header built by a pure `McpAppCsp.Build(meta.ui.csp)` (validation per finding 6) on **every** response including 4xx/5xx, plus nosniff, `Referrer-Policy: no-referrer`, `Cache-Control: private, no-store`, `frame-ancestors 'self'`. Injects nothing into the HTML except what Step 6 needs (theme goes via `hostContext`, not injection).
 - `GET /api/conversations/{c}/mcp-apps/{callId}/result` returns the stored raw result (owner only, `AgentKey(GetUserId(), c)`; the userId comes from the principal, never the request).
 - `POST /api/mcp-apps/{serverId}/tools/call` and `POST /api/mcp-apps/{serverId}/resources/read` (ui:// only) via the registry. `GetService` resolution of the registry (rule 7), not a new required handler parameter.
 Verify: `McpAppsApiTests` on `SharedApiFixture` (copy `ArtifactsApiTests` + `AssertSandboxHeaders` style: exact CSP for default and for declared domains, injection attempts in declared domains are rejected, header present on 404, other user's call id gets 404, unauthenticated rejected). `AgentApiReachabilityTests` must stay green (and bump `MinimumParts` if the floor needs to rise).

**Step 6 - Frontend host (`wwwroot/js/mcpapps.js`, no build step, ADR 0041).** Hand-rolled bridge (do **not** vendor `@modelcontextprotocol/ext-apps`; it needs a bundler). Card in the assistant bubble under the tool chip, `<iframe sandbox="allow-scripts" referrerpolicy="no-referrer" src=".../frame?uri=...">`, optional `allow=` from declared permissions. Protocol: validate `event.source`, handle `ui/initialize` (reply `protocolVersion "2026-01-26"`, `hostCapabilities`, `hostInfo`, `hostContext`: theme, `styles.variables` mapped from `themes.css` tokens to the spec's standard variable names, `displayMode`, `availableDisplayModes: ["inline","fullscreen"]`, `containerDimensions`, locale, timeZone, platform), wait for `ui/notifications/initialized`, then `tool-input` then `tool-result` (live from the stream event, replay from the result route), `host-context-changed` on theme flip, `tool-cancelled` on stop, `ui/resource-teardown` before removal. Handle `size-changed`, `ui/open-link` (https/http only, user-gesture window), `ui/request-display-mode` (fullscreen reuses the pane's CSS-class toggle; others return current), `tools/call` and `resources/read` through the Step 5 endpoints, `ping`, `notifications/message` (log). `ui/message` and `ui/update-model-context` per finding 8. Wire the `toolCallEnd` chunk (`send.js`) and history replay (`thread.js`) to render the card; fall back to the normal chip when the frame fails or `AppsEnabled` is off. Import it from `main.js`; add to `sw.js` SHELL; **bump `CACHE`** (v80 -> v81) and update **all four** tests that pin the literal: `PwaAssetsTests.cs:269`, `ClientToolsAssetsTests.cs:19`, `ResearchAssetsTests.cs:19`, `SearchAssetsTests.cs:68`. Implement the inert-until-activated replay of finding 11. The chat request advertises app support (a new field beside `clientTools`). Verify: source-text guard tests (follow `ClientToolsAssetsTests` / `McpElicitationAssetsTests`: protocol constants, `event.source` check present, no `postMessage` before initialized, confirm gate wraps both injection methods, `allow-same-origin` absent), `ProjectLayoutTests` reachability/SHELL, and the DevHost manual check in Step 8.

**Step 7 - `appContext` injection + per-server switch.** `appContext` on the chat request: validate (<= 8 KB, string/JSON text), inject as labeled untrusted trailing context via the ADR 0104 provider shape; never into instructions, never into the persisted session; **allowed on incognito turns** (it is per-turn and never stored, so it leaves no trace, consistent with ADR 0034). The `AppsEnabled` field already exists from Step 1: this step adds the editor toggle in `js/mcp.js` (pre-ticked for a newly added server, unticked for existing ones) + API contract, and the provider/registry behavior with the switch off (extension not advertised, app-only tools not filtered, no app payloads). Saving bumps the config version as today. Verify: tests for the injection shape (trailing, labeled, not in session, size cap, incognito allowed), `McpServersApiTests` round-trip of `AppsEnabled`, provider test that an off server is not advertised the extension and a stored server defaults off.

**Step 8 - DevHost fixture + device check.** Extend DevHost's offline MCP stand-in with a fixture app: one model-visible tool with `_meta.ui.resourceUri` returning `structuredContent`, one app-only tool, and a `ui://` HTML view (self-contained, uses the postMessage protocol directly) that renders the result, calls the app-only tool, and exercises open-link/message/context. It rides the `IMcpAppClient` seam from Step 1 (offline factory returns fixed resource + tool data), so the same fixture serves tests and DevHost; DevHost is one of two `MapAgentApi` hosts, so confirm the registry resolves there. Device check (run, don't claim): `dotnet run --project tools/Agent42.DevHost`, screenshot the card, click through each capability, flip theme, reload and confirm replay, confirm the confirm gate blocks `ui/message`. Note: `/run` skill; if SDK 11 breaks the DevHost build locally (memory note), say so and substitute the test host. Also do ONE live check against a real third-party MCP App server (the `ext-apps` examples) and report honestly if not done.

**Step 9 - Docs (same MR; hard rule 2).** `/decide`: new ADR (take the next free number from `origin/main` right before merging; tags `mcp, security, egress, frontend`; `extends 0024, 0084, 0089, 0095, 0109`; **ADR 0013 extension** stated plainly: CSP-declared connect/resource/frame domains are an egress path honored from third-party servers, and the artifact-style self-navigation hole also applies; records the double-iframe deviation, default-ON switch, blobs surviving purge, confirm-gated message/context, Android/share-link omission). `/gotcha` entries: (1) the frame route CSP header is the safety (declared-csp injection traps + `allow-same-origin` never), (2) postMessage origin is `null` so authenticate by `event.source`, (3) the model's result and the view's result differ and the raw one must stay out of the session, (4) visibility filter has TWO writers (provider + tester). CLAUDE.md feature-map row (MCP client) gains the ADR number; add `docs/features/` page and its site-nav entry (docs are published as a site since #249/#250; check how pages are listed before adding). Run `/sync-docs`.

Parallelism: Steps 1->2->3->4 are a dependency chain; Step 5 needs 2 and 4; Step 6 needs 3 and 5; Step 7 needs 6; Step 8 can start (fixture HTML) alongside Step 6. Do not run write-capable subagents concurrently in one checkout (worktree isolation or sequential).

## Acceptance criteria -> test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | The extension and `text/html;profile=mcp-app` are advertised on connect, by BOTH factories | `McpElicitationClient.Options` capability test + local-bridge factory test | `tests/Agent42.Tests/McpAppsCapabilityTests.cs` (new) |
| AC2 | A tool without `"model"` visibility is absent from tools, discovered names and the tester; app-only tools remain callable by the view | provider + tester tests over `FakeMcpConnectionFactory` | `UserMcpToolProviderTests.cs`, `McpConnectionTesterTests.cs` |
| AC3 | View `tools/call` is refused for model-only tools, other servers, disabled servers | registry unit tests | `McpAppRegistryTests.cs` (new) |
| AC4 | `ui://` read enforces scheme, MIME and size caps | registry unit tests | `McpAppRegistryTests.cs` |
| AC5 | Frame CSP equals the exact literal string of finding 6 (incl. `form-action 'none'`, no `'self'`), built only from valid declared origins; the validator rejects injection attempts, our own origin, localhost/loopback/private/link-local hosts, bare-TLD/global wildcards, non-https/wss schemes; header on every response incl. errors; never `allow-same-origin` | `McpAppCsp` unit tests + API test | `McpAppCspTests.cs`, `McpAppsApiTests.cs` |
| AC6 | An interactive, app-capable run attaches `McpApp` to `ToolCallEnd`; background / non-capable / reminder / gateway runs do not | grain tests on the shared cluster | `McpAppsGrainTests.cs` (new) |
| AC7 | The raw view result never enters the persisted session | grain test asserting session JSON size/content | `McpAppsGrainTests.cs` |
| AC8 | Reload replays the card; incognito persists nothing; other users get 404 | grain + API tests | `McpAppsGrainTests.cs`, `McpAppsApiTests.cs` |
| AC9 | New persisted fields (`McpApps` list, `AppsEnabled`, `ChatToolCall.McpApp`) round-trip on the Cosmos JSON path | serializer tests | `GrainStorageSerializerTests.cs` |
| AC10 | `appContext` is trailing, labeled untrusted, size-capped, not persisted | grain test | `McpAppsGrainTests.cs` |
| AC11 | Frontend: no message before `initialized`, `event.source` check, confirm gate on `ui/message` + `ui/update-model-context`, no `allow-same-origin` | source-text guards | `McpAppsAssetsTests.cs` (new) |
| AC12 | Module reachable, in SHELL, CACHE bumped (all four pinning tests updated) | existing guards updated | `ProjectLayoutTests.cs`, `PwaAssetsTests.cs`, `ClientToolsAssetsTests.cs`, `ResearchAssetsTests.cs`, `SearchAssetsTests.cs` |
| AC16 | A replayed card is inert until the user activates it; a live card is active at once; a view `tools/call` on a replayed card is refused before activation | source-text guard + (if feasible) a DevHost check | `McpAppsAssetsTests.cs` |
| AC17 | State lifecycle: cap (oldest dropped), purge, retention prune, edit-tail, fork and share projection leave no dangling or leaked record | grain tests | `McpAppsGrainTests.cs` |
| AC18 | A stored server defaults `AppsEnabled` off and is not advertised the extension; an enabled server is | provider tests + JSON-path serializer test | `McpAppsCapabilityTests.cs`, `GrainStorageSerializerTests.cs` |
| AC19 | The registry connects on demand after a cache miss (restart/config save) and fails closed when disabled | registry test over a stub provider | `McpAppRegistryTests.cs` |
| AC13 | New endpoint part is reachable from `MapAgentApi` | existing guard | `AgentApiReachabilityTests.cs` |
| AC14 | Android golden NDJSON file updated; the app already tolerates unknown keys (`StreamEvent.kt:69` `ignoreUnknownKeys = true`, verified) | Android unit test | `tools/Agent42.Android` golden fixture + `./gradlew test` |
| AC15 | End-to-end in a browser against DevHost fixture and one real server | manual device check, evidence in the MR | (reported, not claimed) |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|---|---|---|
| New-code coverage | `coverage_new_code_pct_min: 80` | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet x git diff) |
| Cognitive complexity | `cognitive_complexity_max: 15` (S3776, build error) | `dotnet build Agent42.slnx` (the host bridge and CSP builder are the likely offenders: split early, ADR 0051) |
| Vulnerable packages | 0 | CI package-freshness job (no new package is planned; if one is added, resolve via `dotnet add package`, never from memory) |
| Lint | clean | `dotnet format Agent42.slnx --verify-no-changes` |
| Tests | green | `dotnet test Agent42.slnx` (+ `./gradlew test lint assembleDebug` in `tools/Agent42.Android` for AC14) |

Never write an "Actual" number from memory; report SKIPPED if a tool did not run.

## Out of scope

- Public share links and the Android app rendering apps (text chip only); the Android WebView path is a separate ADR.
- Agent fleet, Copilot kind, chat gateway, agent-run, email: no app rendering (text results as today).
- `pip` display mode; `ui/notifications/tool-input-partial` streaming (optional in the spec); the view reading arbitrary non-`ui://` resources.
- A blob delete path (the store has none; artifacts share the limitation).
- MCP server side (Agent42 exposing apps from `/api/agent-mcp`), the `mcp-ui` embedded-resource flavor (`ui://` content inside tool results), and A2A.
- A separate sandbox origin.

## Review

Critic review (fresh-context `critic` subagent, 2026-11-15). Verdict on the first draft: not ready, two HIGH. All twelve findings were checked against the tree; the CACHE-pin count and the `form-action`/`'self'` gotcha were reproduced by grep before accepting.

| # | Finding | Handling |
|---|---|---|
| 1 HIGH | Frame CSP omitted `form-action 'none'` and left the spec's `'self'` defaults | **Accepted.** Literal policy now in finding 6, AC5 asserts the exact string, gotcha citation added |
| 2 HIGH | Declared-domain validation too weak (own origin, intranet, wildcards) | **Accepted.** Validator rules in finding 6 and AC5 |
| 3 MED | Replay can re-run tools | **Accepted.** Finding 11, Step 6, AC16 |
| 4 MED | Registry fails closed after restart/config save | **Accepted.** Finding 12, Step 2, AC19; stale-HTML-vs-result mismatch accepted and documented |
| 5 MED | State lifecycle incomplete (cap, purge, edit tail, fork, share) | **Accepted.** Finding 13, Step 4, AC17 |
| 6 MED | Default-ON `AppsEnabled` conflicts with cross-cutting 13 | **Accepted.** Stored servers default OFF, editor pre-ticks new servers; still surfaced to the user for approval |
| 7 MED | Gate field landed after its users | **Accepted.** Field moved to Step 1, AC18 |
| 8 MED | Three more tests pin the CACHE literal | **Accepted.** Step 6 and AC12 name all four |
| 9 LOW | Fixture seam unnamed | **Accepted.** `IMcpAppClient` seam in Step 1, Step 8 rides it |
| 10 LOW | Step 3 verify wording | **Accepted.** Reworded |
| 11 LOW | `appContext` incognito left open | **Accepted.** Allowed, reasoned in Step 7 |
| 12 LOW | Android/share consumers | **Accepted.** Android claim verified fine; share projection drop added to finding 13 |

No finding was rejected.

## Open risks (resolve in Step 0 / report if they bite)

- If `AIFunction` results cannot yield `structuredContent` and `DelegatingAIFunction` cannot reach the inner `McpClientTool`, capture needs a different seam (a custom `McpClient` handler or a second `CallToolAsync`, which would double-execute side effects: **not acceptable**; stop and report).
- Honoring declared CSP means a malicious or compromised user-configured server can exfiltrate whatever the tool result and view contain to its own declared domains. This is the accepted ADR 0013 extension, but the model-facing result the view receives is the same data the server produced, so the realistic new exposure is `tools/call` arguments the view builds from conversation context via `ui/update-model-context` / `ui/message` confirmations. Keep the confirm text honest about what is being sent.
- Servers that behave differently when the extension is advertised (e.g. return UI-only content with little text) may degrade the model's view: the model-facing text must keep the server's `content` blocks verbatim.

## Divergences from the plan (as built, 2026-11-15)

- **Capability name, not a new field.** The browser advertises `mcp_app` in the existing `clientTools` list (the ADR 0095
  `mcp_elicitation` shape): it is in `ClientToolLimits.AdvertisableCapabilities`, never in `ToolNames`. No grain-signature field.
  `appContext` IS a new trailing `ChatAsync` parameter and request field.
- **No `McpServerTools.Apps`.** The provider hands each `TruncatingMcpTool` its own `McpAppRef`; the grain never needed the map.
  `TruncatingMcpTool` is now public (no `InternalsVisibleTo`, tests wrap their own tools).
- **Step 0(b) was better than feared:** `InvokeAsync` already returns the whole `CallToolResult` as a `JsonElement` when there is
  `structuredContent`, so no second call and no `DelegatingAIFunction` reach-through. Text-only results are rebuilt by
  `McpAppResultCapture`. Extension capability values must be `JsonElement` (anonymous objects throw).
- **View `tools/call` ignores per-tool selection.** The selection is per agent and the request has no agent context; the gate is
  server enabled + `AppsEnabled` + visibility `"app"` on the route's server. Written into ADR 0133.
- **Registry handle source:** `UserMcpToolProvider` implements `IMcpAppHandleSource` (per-connection `McpAppHandle`, which is also the
  view-cache key). `IMcpAppRegistry` is registered in `Program.cs`, DevHost registers its own over a fixture.
- **Share projection** needed no code: ADR 0080 already nulls `ToolCalls` on a share link; `ShareLink_CarriesNoMcpApp` pins it.
- **Not honored:** iframe `allow=` from declared permissions (optional in the plan). **Wire:** `ui/message` accepts both the spec's
  single content object and an array.
- **Device check (AC15) NOT done in a browser:** the Chrome extension was not connected. Verified instead against a running DevHost:
  the frame route (CSP header on the wire), `tools/call` through the registry, and the 400 for an unknown tool. The click-through
  (card, each capability, theme flip, reload and inert replay, confirm gate) and the one live third-party server check
  (which also covers the `server/discover` probe against an HTTP server that ignores the extension) are still open.
- **Local build:** only SDK 11 is installed; DevHost needed `-p:StaticWebAssetsEnabled=false`. The Android `./gradlew test` was not run
  (no JDK/Android SDK checked); the golden fixture is pinned by `AndroidStreamContractTests`, which passes.
