# Plan — MCP client elicitation (form + URL mode)

planned-against: main @ 2c1dbf3

## Overview

**Goal:** a remote MCP server can ask the user for input mid-turn via MCP elicitation (`elicitation/create`). Form mode renders a flat JSON schema as an inline dialog; URL mode shows a link the user consents to open in a new tab. The answer (`accept` / `decline` / `cancel`) goes back to the server and the tool call completes. Runs with no human attached (agent-run, agent-MCP server, reminders, Copilot chats, a browser that didn't advertise the capability) answer `cancel` without asking anyone.

**Why now:** ADR 0089 built the pause-on-the-browser machinery (per-run `ClientToolBroker`, interleaved result endpoint, recovery, capped waits). Elicitation is the protocol's own human-in-the-loop channel. Today Agent42 connects with `McpClient.CreateAsync(transport, loggerFactory: …)` and **no options** (`src/Agent42.Backend/Mcp/McpConnectionFactory.cs`, `src/Agent42.Backend/Mcp/LocalBridgeConnectionFactory.cs`), so it advertises no elicitation capability, and servers that need it fail or silently fall back to defaults.

## Affected areas

- `src/Agent42.Backend/Mcp/McpConnectionFactory.cs` — build `McpClientOptions` (capabilities + handler); return the router on `McpServerConnection`
- `src/Agent42.Backend/Mcp/LocalBridgeConnectionFactory.cs` — same options on the channel transport (ADR 0076)
- `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` — copy the connection's router onto `McpServerTools`
- `src/Agent42.Backend/Mcp/McpConnectionTester.cs` — no change expected: it uses the factory, so it declares the capability too, and with no scope its router answers `cancel`. Verify only.
- `src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs` (`McpServerTools`) — new trailing optional `McpElicitationRouter? Elicitation = null`
- `src/Agent42.Agents/Mcp/` (new) — `McpElicitation.cs` (request/answer records + `McpElicitationLimits`), `McpElicitationRouter.cs`, `ElicitingMcpTool.cs`
- `src/Agent42.Agents/ClientTools/ClientToolBroker.cs` + `AgentGrain.ClientTools.cs` — a thread-safe elicitation queue owned by the broker; submit + pending paths; `OpenClientToolBroker` / `CloseClientToolBroker` (closing must close the queue — see Limits)
- `ClientToolTimeouts` (the per-tool-name `For(toolName)` test seam) — add `mcp_elicitation` so tests can shorten the elicitation wait
- `src/Agent42.Agents/ClientTools/ClientToolLimits.cs` — `McpElicitation` capability name and an `AdvertisableCapabilities` set
- `src/Agent42.Agents/Conversations/AgentGrain.cs` (`StreamModelReplyAsync`, `PullAsync` callers) — yield elicitation events while a pull is pending
- `src/Agent42.Agents/Conversations/AgentStreamEvent.cs` — new `AgentStreamEventType.McpElicitation` and trailing `[Id(18)] string? Elicitation = null`
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — wrap selected MCP tools in `ElicitingMcpTool`
- `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` (`KnownClientTools`) — intersect with the advertisable set
- Tool-result endpoint `src/Agent42.Backend/ClientTools/AgentApi.ClientTools.cs` (route line 17, `MaxResultRequestBytes` line 28) — no shape change; the answer rides the same body. Confirm `ParseToolResult`'s object-to-raw-text behaviour yields the `{action, content}` JSON the broker parses
- Frontend: `src/Agent42.Backend/wwwroot/js/clienttools.js` (`CLIENT_TOOLS`, handler map), new `src/Agent42.Backend/wwwroot/js/elicitation.js`, `src/Agent42.Backend/wwwroot/js/send.js` (dispatch `mcpElicitation`), `src/Agent42.Backend/wwwroot/js/dom.js`, `src/Agent42.Backend/wwwroot/index.html` (new `<dialog>`), CSS, `src/Agent42.Backend/wwwroot/sw.js` (`CACHE` bump + `SHELL` entry)
- Tests (new): `tests/Agent42.Tests/McpElicitationRouterTests.cs`, `McpElicitationTests.cs` (grain), `McpElicitationIntegrationTests.cs` (real SDK against an in-process Kestrel MCP server), `McpElicitationAssetsTests.cs`. Extend: `ClientToolApiTests.cs`, `UserMcpToolProviderTests.cs`. (No `GrainStorageSerializerTests` change: `AgentStreamEvent`, `McpServerTools` and `McpServerConnection` are not grain state, so rule 2 is not tripped.)
- Doc comments that go stale (rule 2 — fix in the same MR): `IAgentGrain.cs:49` ("already intersected with `ClientToolLimits.ToolNames`"), the `ClientToolLimits.ToolNames` summary ("The chat endpoint intersects … with this"), the `PendingClientToolCall` / `GetPendingClientToolCallsAsync` docs (now also list `mcp_elicitation` entries), and the `AgentGrain.ClientTools.cs` header ("There are three")
- Docs: new ADR (next free number — 0091 at plan time; **re-check at execution**, sibling plans written the same day may claim it), `.agentic/gotchas.md` entry, CLAUDE.md feature-map "MCP client" row

## Context & constraints

**SDK facts, verified in the NuGet cache** (`Z:\Packages\.nuget\packages\modelcontextprotocol.core\2.2.0\lib\net10.0\ModelContextProtocol.Core.xml`; pinned `2.2.0` in `Directory.Packages.props`). **No version bump needed.**
- `McpClientOptions.Handlers.ElicitationHandler` exists (`McpClientHandlers`), alongside `Capabilities`, `ProtocolVersion`, etc.
- `ElicitationCapability.Form` (`FormElicitationCapability`) and `ElicitationCapability.Url` (`UrlElicitationCapability`) — **both modes are in 2.2.0.**
- `ElicitRequestParams` has `Mode` ("form"/"url"), `Message`, `RequestedSchema` (`RequestSchema` with `Properties`/`Required`; primitive defs: string with `Format`/`MinLength`/`MaxLength`, number with `Minimum`/`Maximum`, boolean, single/multi-select enums in titled/untitled/legacy forms), `Url`, `ElicitationId`.
- `ElicitResult` has `Action` (`accept`/`decline`/`cancel`, **default `cancel`**) and `Content` (dictionary of primitives / string arrays).
- `McpErrorCode.UrlElicitationRequired` (-32042), `UrlElicitationRequiredException`, `NotificationMethods.ElicitationCompleteNotification`.
- The 2.2.0 client defaults to protocol **2026-07-28** (MRTR: the server returns an input-required result; the client resolves it via `ResolveInputRequestsAsync` and re-sends). It falls back to the initialize handshake (≤2025-11-25, elicitation as a server→client JSON-RPC request) for older servers. **Both paths end in the same `ElicitationHandler`, but very likely on different execution contexts** — the open question Step 0 measures.

**Routing is the hard problem, not rendering.**
1. **The `McpClient` is shared per user, not per conversation.** `UserMcpToolProvider` caches live connections by `(userId, version)` and hands the same tool instances to every conversation of that user (gotcha "Remote MCP servers: a live-socket cache"; `UserMcpToolProvider.cs`). A handler bound at connect time cannot know which conversation or run is asking — which is why the per-conversation part has to happen in the grain, as `WithUrlSeeding` does (comment in `AgentGrain.BuildAgent.cs`).
2. **The broker is reached only through `AgentGrain.CurrentClientToolBroker`**, an `AsyncLocal` set by `PullAsync` around the run's own pulls (ADR 0089; gotcha "A client tool's wait: route the broker per RUN"). A grain-side wrapper invoked by FICC sees it; the SDK's `ElicitationHandler` may not, depending on transport and protocol version.
3. **Design:** `ElicitingMcpTool : DelegatingAIFunction` is built in the grain with a delegate that reads `CurrentClientToolBroker.Value`. On invoke it opens a scope on the connection's `McpElicitationRouter` holding the sink (the run's broker, or null), the FICC call id and the server display name, and also sets the router's own `AsyncLocal` scope. The handler resolves in order: (a) the `AsyncLocal` scope if it flowed into the handler; (b) otherwise the **only** in-flight scope on that connection; (c) otherwise — zero scopes, more than one, or a scope whose sink is null — **`cancel`**. Two concurrent calls to the same server from two chats of one user are the one case that cannot be attributed; it gets `cancel`, which is honest. A guessed routing could hand one chat's form to another chat's tab. **The fallback's real limit:** "in-flight scope" means *any* concurrent call to that server on the user's connection — another chat, a headless run (agent-run, reminder; null sink), or a **parallel tool call inside the same FICC turn**. If the `AsyncLocal` does not flow into the handler (E1 false), an interactive elicitation is cancelled whenever such a call overlaps. Safe and honest, but user-visible flakiness; the ADR must state it.
4. **Thread affinity.** The handler runs on the MCP session's thread, **not** the grain scheduler. `ClientToolBroker` documents "touched only on the grain's scheduler … takes no lock"; do not break that invariant for tool calls. Add a separate **locked** `McpElicitationQueue` owned by the broker: the handler enqueues and awaits a `TaskCompletionSource(RunContinuationsAsynchronously)`; the grain reads the queue on its own scheduler. `SubmitClientToolResultAsync` (already `[AlwaysInterleave]`) tries the tool-call map first, then the queue.
5. **Emitting mid-pull.** For `confirm_action` the `toolCallStart` is yielded **before** the tool runs. An elicitation arrives **while** `MoveNextAsync` is pending, and nothing can be yielded until it returns. `StreamModelReplyAsync` must race the pending `MoveNextAsync()` (started once inside `PullAsync` so the `AsyncLocal` is captured; use `.AsTask()` — a `ValueTask` must not be awaited twice) against the queue's "item available" signal, yield any queued `McpElicitation` events, and keep waiting on the **same** move-next task. The method has `catch`/`yield` constraints (CS1626, explained in its own comment) and sits close to S3776's limit of 15 — put the race in a helper.

**User decisions (interview 2026-10-10):** support both form and URL mode. Headless runs answer automatically: agent-run (`EnqueuePromptAsync`), agent-MCP server (ADR 0070, `AgentMcpRunner` drains with `interactive: false`), reminders (`ReceiveReminder`), Copilot chats (ADR 0089: Copilot invokes tools on its own RPC thread outside the run's `AsyncLocal`, so the wrapper reads null), and browsers that did not advertise `mcp_elicitation`. All get no broker → null sink → `cancel`. The user said "auto-decline"; this plan returns **`cancel`**, not `decline`, because the spec defines `decline` as an *explicit* user refusal, which never happened. The intent (no data, no human prompt) is unchanged. Swap the constant if the user objects at approval.

**Security (MCP spec mandates, none optional):**
- URL mode: show the **host** prominently plus the full URL in monospace. **Never auto-open, never prefetch.** Open only on an explicit click, with `window.open(url, "_blank", "noopener,noreferrer")`. Accept only absolute `https:` (plus `http:` for `localhost`/`127.0.0.1` only, matching the local bridge). Any other scheme (`javascript:`, `data:`, …) is refused server-side before an event is emitted and answered `cancel`.
- The model never sees an elicitation URL: the `-32042` tool-result text carries the host only.
- Form mode: "Requested by **{server display name}**"; all server-supplied text set via `textContent`; a fixed notice that the user should not enter passwords or secrets here. No heuristic secret detector (a later ADR decision). Before anything is sent, validate the submitted content server-side against the requested schema's primitive types, enum members and `required`. On a mismatch, answer `cancel` and log a warning; never forward partly invalid data.
- **Egress (CLAUDE.md rule 13 / ADR 0013):** the user's typed form values go to a **user-configured** MCP server, only on an explicit Submit. That is an ADR 0013 extension and must be recorded as such in the new ADR. It is neither always-on nor ungated.

**Limits:** the wait reuses `ClientToolLimits.ConfirmTimeout` (120 s), which fits under ACA's 240 s request timeout (ADR 0089 consequences); timeout → `cancel`. The answer body reuses the endpoint's `MaxResultRequestBytes` (8 KB). Elicitation answers are **not** truncated: an answer over `MaxResultChars` becomes `cancel`, because cutting JSON corrupts it. A turn-level Stop or end of turn closes the broker, which cancels the queue → the handler answers `cancel`. **A closed queue rejects new enqueues immediately with `cancel`** — an SDK-thread wait must never hang (unlike `WaitFor` after `Close`, which harmlessly re-adds a grain-side entry). Tests shorten the wait through `ClientToolTimeouts.For("mcp_elicitation")`. A late browser answer gets a 404 and is ignored (existing client behaviour).

**Wire shape:** a new live-only stream event `mcpElicitation` with `toolCallId` = the FICC call id of the waiting MCP tool (so the UI can anchor it), `mcpServerId`, `text` = server display name, and `elicitation` = JSON string `{id, mode, message, schema?, url?, host?}`. `id` is a server-minted `elicit_<guid>` — never the server's `ElicitationId`, which is attacker-chosen and could collide with a call id. **Answer and recovery reuse ADR 0089's path:** `POST /api/chat/{id}/tool-result` with `toolCallId = id` and `result = {action, content?}`. `GET …/pending-tool-calls` lists a waiting elicitation as a `PendingClientToolCall` with `ToolName = "mcp_elicitation"` and `ToolInput` = the same JSON, so `clienttools.js`'s existing `runClientTool`/`resumePendingClientTools` dispatch handles it with one new handler. The event is **not** a `toolCallStart`, so it is never rendered as a tool chip or folded (ADR 0072).

**`-32042 UrlElicitationRequired`:** when a tool call fails with it, `ElicitingMcpTool` presents each contained URL elicitation through the same sink, one at a time, then returns a short result to the model: "{server} needs the user to finish a step in their browser at {host}; the user {opened it | declined | could not be asked}. Ask them to tell you when done, then retry." Step 0 measures the exact client-side exception type.

**Governing docs:** `.agentic/decisions/0089-client-tools-and-shared-state.md` (broker, interleave, recovery, Copilot exclusion), `0024` (MCP client), `0026` (per-server gate), `0076` (local bridge — same `McpClient.CreateAsync` on a channel transport), `0081` (`ReauthNoticeMcpTool` must stay **outermost**), `0067` (URL-seeding wrapper order), `0013` (egress). Gotchas: "Remote MCP servers: a live-socket cache …", "A client tool's wait …", "A grain's turn-scoped field lives for the ACTIVATION" (the router scope is per call, **not** a grain field), "Every MR that touches `wwwroot/js/` must bump `CACHE`", "A frontend module unreachable from `main.js` is invisible …". ADR 0088 (AG-UI deferred) only confirms the NDJSON stream is the wire.

**Wrapper order in `BuildAgent`:** `WithReauthNotice(server, WithUrlSeeding(server, WithElicitation(server, t)))` — elicitation **innermost**, so the reauth notice stays outermost (ADR 0081) and the harvester never sees a URL (the -32042 text holds the host only).

## Steps

**Step 0 — spike (blocking; `spike-dotnet` skill, a file-based app outside the repo, pinned to `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` 2.2.0).** An in-process Kestrel MCP server on loopback with a tool that calls `server.ElicitAsync(...)`, and a client with an `ElicitationHandler`. Measure:
- **E1:** is an `AsyncLocal` set around `CallToolAsync` visible inside the handler? (a) default (2026-07-28 / MRTR), (b) forced `ProtocolVersion = "2025-11-25"`.
- **E2:** when the server throws `UrlElicitationRequiredException`, what does the client's `McpClientTool.InvokeAsync` throw (type, how to read `Elicitations`)?
- **E3:** does a server-side `ElicitAsync` against a client declaring **no** capability throw, and does declaring `Form` only make a URL request fail server-side? (Confirms both must be declared.)
- **E4:** over a channel `ITransport` (mirror `LocalBridgeConnectionFactory.ChannelClientTransport` with a `Pipe`/in-memory pair), does the handler fire?

Record the results in this spec under **Spike results** before Step 1. If E1 is true on the default (2026-07-28) path, the fallback stays as the documented secondary path. **If E1 is false on the default path, stop and report to the user before Step 1**: the feature would then degrade for concurrent calls, and the choice (accept the flakiness, force the older protocol for elicitation-using servers, or another routing) is the user's.

**Step 1 — Agents-side types + router** (`src/Agent42.Agents/Mcp/McpElicitation.cs`, `McpElicitationRouter.cs`).
- Records `McpElicitationRequest(Mode, Message, SchemaJson?, Url?, ServerElicitationId?)` and `McpElicitationAnswer(Action, ContentJson?)` — no SDK types, because Agents has no MCP package reference (see the `IUserMcpToolProvider` doc).
- `McpElicitationLimits` (wait = `ClientToolLimits.ConfirmTimeout`, `Cancel` constant, the URL scheme rule as static `TryValidateUrl`).
- Interface `IMcpElicitationSink { Task<McpElicitationAnswer> AskAsync(string serverName, string toolCallId, McpElicitationRequest, CancellationToken) }`.
- Router: a locked scope set + its own `AsyncLocal`; `Enter(sink, callId, serverName) : IDisposable`; `RouteAsync(request, ct)` resolves as described in Context.

Tests `McpElicitationRouterTests`: `SingleScope_RoutesThere`, `TwoScopes_AnswerCancel`, `NoScope_AnswersCancel`, `NullSink_AnswersCancel`, `DisposedScope_NoLongerRoutes`, `AsyncLocalScope_WinsOverFallback`, `TryValidateUrl_RejectsNonHttpsSchemes` (`[Theory]`: `javascript:`, `data:`, non-loopback `http:`, relative; accepts `https:` and loopback `http:`).
Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~McpElicitationRouterTests`.

**Step 2 — Backend wiring** (`McpConnectionFactory.cs`, `LocalBridgeConnectionFactory.cs`, `UserMcpToolProvider.cs`, `IUserMcpToolProvider.cs`).
- One internal static helper (e.g. `McpClientOptionsFactory.Create(router)`) used by **both** factories so the two paths cannot drift (the ADR 0064 "two ways to pass one credential" lesson). It declares `Capabilities.Elicitation = { Form, Url }` and maps `ElicitRequestParams` ↔ the request/answer records.
- Map the schema with the SDK's own serializer (`McpJsonUtilities`) into `SchemaJson`.
- `McpServerConnection` gains a trailing `McpElicitationRouter? Elicitation = null`; the provider copies it onto `McpServerTools(… Elicitation: connection.Elicitation)`.
- `FakeMcpConnectionFactory` in `UserMcpToolProviderTests` keeps compiling (optional param).

Tests `McpElicitationIntegrationTests` (real `McpConnectionFactory` against an in-process Kestrel MCP server on `127.0.0.1:0`): `FormElicitation_ReachesTheScopeSink_AndTheAnswerReturnsToTheServer`, `NoScope_AnswersCancel`, `UrlElicitation_WithJavascriptScheme_IsCancelledWithoutAsking`, `ClientDeclaresFormAndUrl` (asserts the server-side `ClientCapabilities`). Plus `UserMcpToolProviderTests.ProviderCopiesTheConnectionsRouterOntoTheGroup`. Mirror E4 if the spike shows a channel test is cheap; otherwise a structural test `BothFactories_UseTheSharedOptionsHelper` (source-text, `Hosts_RegisterLocalBridge` style in `ProjectLayoutTests.cs`).
Verify: `dotnet test --filter "FullyQualifiedName~McpElicitationIntegrationTests|FullyQualifiedName~UserMcpToolProviderTests"`.

**Step 3 — grain** (`ClientToolBroker.cs`, `AgentGrain.ClientTools.cs`, `ClientToolLimits.cs`, `AgentGrain.cs`, `AgentStreamEvent.cs`, new `ElicitingMcpTool.cs`, `AgentGrain.BuildAgent.cs`).
- `ClientToolLimits.McpElicitation = "mcp_elicitation"` and `AdvertisableCapabilities = ToolNames ∪ {McpElicitation}`. **Leave `ToolNames` unchanged** — `AgentGrain.Copilot.cs` and `ToolCatalog.cs` read it as the set of *model tools*.
- The broker owns a locked `McpElicitationQueue` (enqueue from any thread; dequeue, `TryComplete`, `Pending`, `Close` on the grain). The broker implements `IMcpElicitationSink` only when `Supports(McpElicitation)`; otherwise the wrapper passes a null sink.
- `ElicitingMcpTool` (with an S3776-friendly extraction like `WithUrlSeeding`) enters the router scope with `CurrentClientToolBroker.Value` and the FICC call id, and handles -32042 per E2.
- `StreamModelReplyAsync` races the move-next task against the queue signal and yields `McpElicitation` events (trailing `[Id(18)]` field on `AgentStreamEvent`, default `null` so the NDJSON writer omits it).
- `SubmitClientToolResultAsync` and `GetPendingClientToolCallsAsync` include the queue.

Tests `McpElicitationTests` (shared cluster; a fake MCP group via `SingleUserMcpToolProvider.Servers` with a real `McpElicitationRouter`; the fake tool calls `router.RouteAsync` from `Task.Run` to mimic the SDK thread): `Elicitation_MidTool_StreamsAnEvent_AndTheSubmittedAnswerReachesTheServer`, `Elicitation_BackgroundRun_AnswersCancelWithoutAnEvent`, `Elicitation_ReminderRun_AnswersCancelWithoutAnEvent`, `Elicitation_AgentMcpRun_AnswersCancelWithoutAnEvent` (drain via `AgentMcpRunner` / `interactive: false`), `Elicitation_BrowserDidNotAdvertise_AnswersCancel`, `Elicitation_Timeout_AnswersCancel_AndTheTurnCompletes`, `Elicitation_AfterTurnEnd_AnswersCancelImmediately`, `Elicitation_Stop_CancelsTheWait`, `Elicitation_PendingCalls_ListIt_AsMcpElicitation`, `Elicitation_InvalidContent_AnswersCancel`, `Elicitation_OversizedAnswer_AnswersCancel_NotTruncated`, `UrlElicitationRequired_ResultCarriesHostNotUrl`, `UrlElicitationRequired_PresentsTheUrlToTheSink` (the sink receives a URL-mode request with the full URL), `ElicitingWrapper_IsInnermost_ReauthStaysOutermost`. `ClientToolTests` must stay green unchanged.
Verify: `dotnet test --filter "FullyQualifiedName~McpElicitationTests|FullyQualifiedName~ClientToolTests|FullyQualifiedName~McpReauthTests|FullyQualifiedName~McpUrlSeedingTests"`.

**Step 4 — endpoint.** `KnownClientTools` intersects with `ClientToolLimits.AdvertisableCapabilities`. Tests in `ClientToolApiTests`: `AdvertisedMcpElicitation_IsKept`, `UnknownCapability_IsDropped`, `ElicitationAnswer_ThroughToolResultEndpoint_Returns204ThenLateIs404`.
Verify: `dotnet test --filter FullyQualifiedName~ClientToolApiTests`.

**Step 5 — frontend** (parallelizable with Step 4 once Step 3 fixes the event shape).
- `elicitation.js`: schema-driven form for string (`email`/`uri`/`date`/`date-time` → input types; `minLength`/`maxLength`), number/integer (`min`/`max`), boolean, single and multi enum (titled `oneOf`/`anyOf`, untitled `enum`, legacy `enumNames`), `required` markers, `default` values. An unsupported property type makes the whole form read "This request can't be shown" with only a Decline action. URL mode: host, full URL and an Open button; the URL opens on click, and that click answers `accept`.
- All server text via `textContent`. Decline / Cancel / Esc each map explicitly. Reset `returnValue` before `showModal()` (the ADR 0089 Esc trap).
- `clienttools.js`: add `"mcp_elicitation"` to `CLIENT_TOOLS` and a `mcp_elicitation` entry to the `handlers` map (it dispatches into `elicitation.js`, which therefore is reachable from `main.js`); close the new dialog in `closeClientToolDialogs`.
- `send.js`: `chunk.type === "mcpElicitation"` → build `{toolCallId: payload.id, toolName: "mcp_elicitation", toolInput: chunk.elicitation}` and call `runClientTool`.
- `index.html`: `<dialog id="mcpElicitationDialog">`. `dom.js`: exports. `sw.js`: bump `CACHE`, add `/js/elicitation.js` to `SHELL`.

Tests `McpElicitationAssetsTests` (source-text guards, `ClientToolsAssetsTests` style): `ElicitationModule_IsReachableFromMain`, `ServerText_IsSetWithTextContent_NeverInnerHtml`, `UrlMode_OpensOnlyOnClick_WithNoopener`, `UrlMode_NeverPrefetches` (no `fetch(` of the elicitation URL), `Dialog_ResetsReturnValueBeforeShowModal`, `ClientTools_AdvertisesMcpElicitation`, `ShellListsElicitationModule`. The CACHE bump is guarded by the existing PWA tests. Then manual: `dotnet run --project tools/Agent42.DevHost` with a local MCP test server that elicits, configured as a user MCP server (use the `run` skill); record the observed result in the MR.
Verify: `dotnet test --filter "FullyQualifiedName~McpElicitationAssetsTests|FullyQualifiedName~ClientToolsAssetsTests|FullyQualifiedName~PwaAssetsTests"`.

**Step 6 — docs** (`/decide`, `/gotcha`, `/sync-docs`).
- ADR "MCP elicitation on the client": extends 0024/0076/0089 and records the ADR 0013 egress extension. Alternatives: a per-conversation `McpClient` (lost: socket cost, routing point 1); elicitation as a model tool (lost: the model would relay user secrets); `decline` for headless (lost: semantics); a per-server opt-out toggle (deferred — see Out of scope).
- Gotcha: the "handler runs off the grain scheduler on a per-USER connection" routing trap, with E1's measured answer.
- CLAUDE.md feature-map MCP-client row: add the ADR number.

Finally: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, `node .agentic/scripts/check-new-code-coverage.mjs`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Both factories declare form + URL elicitation via one shared helper | `ClientDeclaresFormAndUrl`, `BothFactories_UseTheSharedOptionsHelper` | `McpElicitationIntegrationTests.cs` |
| 2 | A form elicitation during an interactive turn streams `mcpElicitation`; the submitted answer reaches the server | `Elicitation_MidTool_StreamsAnEvent_AndTheSubmittedAnswerReachesTheServer`, `FormElicitation_ReachesTheScopeSink_AndTheAnswerReturnsToTheServer` | `McpElicitationTests.cs`, `McpElicitationIntegrationTests.cs` |
| 3 | Headless runs (background, reminder, agent-MCP, non-advertising browser) answer `cancel` and emit nothing; Copilot is covered by construction (its tools run outside the run's `AsyncLocal`, so the wrapper reads a null sink — ADR 0089) | `Elicitation_BackgroundRun_…`, `Elicitation_ReminderRun_…`, `Elicitation_AgentMcpRun_…`, `Elicitation_BrowserDidNotAdvertise_AnswersCancel`, `NoScope_AnswersCancel` | `McpElicitationTests.cs`, `McpElicitationIntegrationTests.cs` |
| 4 | Ambiguous routing (two in-flight scopes) answers `cancel` | `TwoScopes_AnswerCancel` | `McpElicitationRouterTests.cs` |
| 5 | The wait is capped (timeout → `cancel`, turn completes); Stop cancels the wait; an enqueue after the turn ends answers `cancel` immediately | `Elicitation_Timeout_AnswersCancel_AndTheTurnCompletes`, `Elicitation_Stop_CancelsTheWait`, `Elicitation_AfterTurnEnd_AnswersCancelImmediately` | `McpElicitationTests.cs` |
| 6 | Recovery lists a waiting elicitation | `Elicitation_PendingCalls_ListIt_AsMcpElicitation` | `McpElicitationTests.cs` |
| 7 | Invalid or oversized answers become `cancel`, never truncated | `Elicitation_InvalidContent_AnswersCancel`, `Elicitation_OversizedAnswer_…` | `McpElicitationTests.cs` |
| 8 | A URL with a non-https scheme (except loopback http) is refused without asking | `UrlElicitation_WithJavascriptScheme_IsCancelledWithoutAsking`, `TryValidateUrl_RejectsNonHttpsSchemes` | `McpElicitationIntegrationTests.cs`, `McpElicitationRouterTests.cs` |
| 9 | -32042 shows the user the URL and gives the model the host only | `UrlElicitationRequired_PresentsTheUrlToTheSink`, `UrlElicitationRequired_ResultCarriesHostNotUrl` | `McpElicitationTests.cs` |
| 10 | Wrapper order: elicitation innermost, reauth outermost | `ElicitingWrapper_IsInnermost_ReauthStaysOutermost` | `McpElicitationTests.cs` |
| 11 | The endpoint keeps `mcp_elicitation`, drops unknown names; a late answer is a 404 | `AdvertisedMcpElicitation_IsKept`, `UnknownCapability_IsDropped`, `ElicitationAnswer_…` | `ClientToolApiTests.cs` |
| 12 | UI: textContent only, URL opens only on click with noopener, no prefetch, module reachable, SW updated | `McpElicitationAssetsTests` (all seven) + existing PWA guards | `McpElicitationAssetsTests.cs`, `PwaAssetsTests.cs` |
| 13 | Existing client tools unaffected | the whole `ClientToolTests` class, unchanged | `ClientToolTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build (error → `dotnet build` fails); watch `StreamModelReplyAsync` and `BuildAgent` |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| `dependencies.add_policy` | latest-stable | n/a — no new package (2.2.0 already has both modes) |
| CI hard blocks | build, tests, `dotnet format`, doc-path integrity | `pr-checks.yml` |

## Out of scope

- **Sampling** (`sampling/createMessage`) and **roots** — separate capabilities; sampling is a model-egress decision of its own.
- A per-server "allow this server to ask me" toggle — would be a new persisted `StoredMcpServer` field (trailing `[Id]`). Deferred until a server abuses elicitation; recorded in the ADR as the revisit trigger.
- Acting on `notifications/elicitation/complete` (auto-dismissing a URL prompt) — optional per spec.
- Hard-gating any tool on elicitation or `confirm_action` (ADR 0089 already calls that a separate decision).
- Elicitation for the Copilot agent kind (blocked for ADR 0089's reason: the RPC thread is outside the run's async local).
- A heuristic secret detector on form field names.
- Persisting anything about an elicitation in the session or grain state (the event is live-only; only the tool's eventual result is persisted, as today).

## Review

Critic pass 2026-10-10 (fresh-context `critic` agent). No blocking defects; all SDK 2.2.0 and code claims checked held (factory call sites, `CurrentClientToolBroker`, next free `[Id(18)]`, wrapper order, test classes, next ADR 0091).

| Finding | Severity | Handling |
|---|---|---|
| F1 fallback routing cancels on *any* concurrent call (headless runs, parallel tool calls), not only two chats; "plan unchanged either way" too strong | Medium | **Fixed** — limit stated in Context; Step 0 now stops for a user decision if E1 is false on the default path |
| F2 AC 3 claimed reminder / agent-MCP / Copilot tests that didn't exist | Medium | **Fixed** — reminder and agent-MCP tests added; Copilot narrowed to "by construction" with the ADR 0089 reason |
| F3 unnamed tests; AC 9 user side untested; no timeout seam | Low-med | **Fixed** — tests named; `UrlElicitationRequired_PresentsTheUrlToTheSink`; `ClientToolTimeouts.For("mcp_elicitation")` added |
| F4 enqueue after `Close` could hang the SDK thread | Low | **Fixed** — closed-queue rule + `Elicitation_AfterTurnEnd_AnswersCancelImmediately`; broker open/close listed |
| F5 stale doc comments | Low | **Fixed** — listed under Affected areas |
| F6 tool-result endpoint mislocated | Low | **Fixed** — `ClientTools/AgentApi.ClientTools.cs` named |
| F7 hedged serializer-test item | Low | **Fixed** — removed with the reason |
| F8 -32042 client exception type unproven | Info | **Accepted** — already a Step 0 measurement (E2) |

## Spike results

Spike run 2026-10-10: a file-based app (`ModelContextProtocol` + `.AspNetCore` 2.2.0, net10.0) with an in-process Kestrel MCP server on loopback, a client with an `ElicitationHandler` that records an `AsyncLocal` set around `McpClientTool.InvokeAsync`, and a `Pipe`-backed stream transport pair. Observed output:

```
STATEFUL HTTP server (WithHttpTransport, Stateless=false)
--- http ver=default    negotiated=2025-11-25
E1 form: ok: accept:name=Bob  handlerSaw=<null> mode=form
E1 url : ok: accept           handlerSaw=<null> mode=url
E2 -32042: THREW ModelContextProtocol.UrlElicitationRequiredException: Request failed (remote): auth needed | code=UrlElicitationRequired | urlEx=https://example.com/auth
caps: elic=form=True,url=True
--- http ver=2025-11-25 / 2025-06-18: identical (handlerSaw=<null>)
--- E3 no capability declared: server logs "Client does not support elicitation requests."; tool returns isError; caps: elic=null
STATELESS HTTP server (Stateless=true)
--- negotiated=2026-07-28: server logs "Elicitation is not supported in stateless mode."; handler never called
STREAM transport (Pipe pair, mirrors the local bridge)
--- E4 stream negotiated=2026-07-28
E4 form: ok: accept:name=Bob  handlerSaw=SCOPE
```

- **E1: FALSE on the path that matters.** A stateful streamable-HTTP server (the only HTTP shape where 2.2.0 server-side `ElicitAsync` works) negotiates **2025-11-25** even when the client defaults to 2026-07-28. Elicitation then arrives as a server→client request, and the handler does **not** see the caller's `AsyncLocal`. Over the stream transport the session negotiates 2026-07-28 (MRTR), and the `AsyncLocal` **does** flow (`handlerSaw=SCOPE`). Remote MCP servers therefore always fall back to "the only in-flight scope", while the local bridge gets exact routing.
- **E2:** `McpClientTool.InvokeAsync` throws `ModelContextProtocol.UrlElicitationRequiredException` (`ErrorCode == UrlElicitationRequired`), and `.Elicitations` carries the `ElicitRequestParams` with the `Url`. Same result on every protocol version.
- **E3:** with no capability declared, the server's `ElicitAsync` throws "Client does not support elicitation requests." The tool fails with `isError`. Both modes have to be declared (`caps` reports `form` and `url` separately).
- **E4:** the handler fires over a stream (`ITransport`) pair, and the `AsyncLocal` flows there.

**Stop condition hit (E1 false on the default HTTP path).** Reported to the user for a decision before Step 1.
**User decision (2026-10-10): accept the fallback.** Build as planned: the `AsyncLocal` scope gives exact routing on the local bridge, and remote HTTP servers use the single-in-flight-scope fallback, where any overlap answers `cancel`. ADR 0095 must state this limit together with the measured E1 split. The rejected alternatives were serializing calls per connection (it costs latency for every parallel call) and a per-conversation client.

## Divergences from the plan (execution, 2026-10-10)

- **Drift.** Executed against `main @ 5f36418`, not `2c1dbf3`. ADRs 0091–0093 landed in between, and location became a fourth client tool. Every change was additive to the affected files, and the user confirmed going ahead. ADR **0095** replaces 0091; 0094 is claimed by the webhooks branch.
- **-32042 translation.** It lives in the Backend's `TruncatingMcpTool` (every MCP tool already passes through it) and becomes a new Agents-layer `McpUrlElicitationRequiredException`, because the Agents project has no MCP package reference. Test: `UserMcpToolProviderTests.UrlElicitationRequired_IsTranslatedForTheGrain`.
- **The sink is its own class** (`ClientTools/McpElicitationSink.cs`), not the broker implementing `IMcpElicitationSink`. It needs the grain's timeout and logger, and keeping it separate leaves the broker free of the elicitation wait logic. The grain builds one per call in `CurrentElicitationSink()`.
- **Content validation** has its own type, `Mcp/McpElicitationContent.cs`, with a `[Theory]` in `McpElicitationTests`.
- **Event shape.** `mcpElicitation` carries `toolCallId`, `text` (server display name) and `elicitation`. **No `mcpServerId`:** the router scope holds the display name only, and nothing in the UI needs the id. The request JSON also carries `server`, so the recovery path, which has no `text`, can still show who is asking.
- **Wiring.** `wireElicitation()` is called from `main.js`, following the ADR 0041 composition-root rule. It is not called from `wireClientTools`, and a `FrontEndWiredModules` row was added.
- **Tests renamed or merged.** `UrlElicitationRequired_ResultCarriesHostNotUrl` and `UrlElicitationRequired_PresentsTheUrlToTheSink` became one test (`…_PresentsTheUrlToTheSink_AndTheResultCarriesHostNotUrl`). `AdvertisedMcpElicitation_IsKept` and `ElicitationAnswer_…_Returns204ThenLateIs404` became one end-to-end HTTP test. `Elicitation_AfterTurnEnd_AnswersCancelImmediately` is a direct sink-and-broker test. `ElicitingWrapper_IsInnermost_ReauthStaysOutermost` is a source-text check on `BuildAgent`.
- **No reminder test.** `Elicitation_ReminderRun_AnswersCancelWithoutAnEvent` was not written. `ReceiveReminder` runs `_agent.RunAsync` without `PullAsync`, so `CurrentClientToolBroker` is null and the sink is null by construction, the same argument as for Copilot. Driving a reminder into a scripted MCP call would have needed new fake-model plumbing.
- **The existing cache-name assertions** (`PwaAssetsTests`, `ClientToolsAssetsTests`, `ResearchAssetsTests`, `SearchAssetsTests`) were bumped to `v56`. `ClientToolList_MatchesTheServer` now pins the five advertisable names.
- **Feature doc.** A short section was added to `docs/features/remote-mcp-servers.md`.
- **Fixes from the critic pass (not in the plan):**
  - **Disconnect mid-pull.** A browser that disconnects at an elicitation's yield used to leave the iterator disposed during a running pull. The stream `finally` now closes the broker and awaits the pull first (`SettlePendingPullAsync`).
  - **Enter key.** Enter in a one-field form submitted implicitly, and that closed the dialog as `cancel`. The submit event now goes through the Submit button.
  - **Request size.** Requests are capped at `McpElicitationLimits.MaxRequestChars` (16 KB); a bigger one answers `cancel`.
  - **Dead code.** The unused `McpElicitationLimits.Wait` and `ServerElicitationId` were dropped.
  - **Tests added:** `Elicitation_ConsumerDisposesTheStream_AnswersCancel_AndTheNextTurnRuns`, `Elicitation_OversizedRequest_AnswersCancelWithoutAsking`, `ImplicitSubmit_IsRoutedThroughTheSubmitButton`.
  - **Not fixed:** the per-update race cost on runs with no MCP router (correct, small) and the JS queue showing a stale request after Stop (rare; its late answer 404s).
