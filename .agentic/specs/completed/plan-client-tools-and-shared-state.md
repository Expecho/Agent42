planned-against: main @ 9dea9f0

# Plan: client (browser-executed) tools and shared agent↔UI state on the NDJSON stream

## Overview

ADR 0088 (AG-UI deferral, pending in its own MR as `.agentic/decisions/0088-defer-ag-ui-protocol-support.md`)
names AG-UI's two real differentiators: **frontend tools** and **shared state**. This plan builds both on
Agent42's own NDJSON chat stream instead of adopting the protocol:

1. **Client tools.** The model calls a tool that the **browser** runs. The server pauses that tool call, the
   browser runs it and POSTs the result, and the turn resumes. There are three first tools:
   `confirm_action` (human-in-the-loop approval), `control_artifact_pane` (drive the ADR 0084 pane), and
   `get_browser_context` (browser-only facts).
2. **Shared state.** Each conversation has a JSON object that the agent changes with a tool (JSON Patch), the UI
   renders and the user can edit, and that the model sees on the next turn. It ships with a checklist renderer.

## Affected areas

Verified against the tree at `9dea9f0`.

**Agents (`src/Agent42.Agents/`). Every type stays in the flat root namespace (CLAUDE.md rule 1, ADR 0039).**
- `ClientTools/AgentGrain.ClientTools.cs`: **new** partial with the three tool functions, the wait, and
  `SubmitClientToolResultAsync` / `GetPendingClientToolCallsAsync`.
- `ClientTools/ClientToolBroker.cs`: **new**. The pending-call registry for ONE interactive run (callId →
  name, input, `TaskCompletionSource<string>`).
- `ClientTools/ClientToolLimits.cs`: **new** constants for the timeouts and the result cap (`WebToolLimits` shape).
- `SharedState/AgentGrain.SharedState.cs`: **new** partial with the `update_shared_state` tool, the per-run
  context injection, `GetSharedStateAsync` / `SetSharedStateAsync`.
- `SharedState/SharedStatePatch.cs`: **new**, pure static. Applies RFC 6902 ops to a `JsonNode` and enforces
  the caps. Unit-testable with no grain.
- `SharedState/SharedStateLimits.cs`: **new** constants.
- `Conversations/IAgentGrain.cs`: `ChatAsync` gains a trailing `IReadOnlyList<string>? clientTools = null`. Adds
  `SubmitClientToolResultAsync`, `GetPendingClientToolCallsAsync` and `SetSharedStateAsync`, all
  `[AlwaysInterleave]` and **not** `[OneWay]` (the `StopAsync` reasoning), plus `GetSharedStateAsync`.
- `Conversations/AgentGrain.cs`: in `ChatAsync`, publish and unpublish the run's broker by identity, and set the
  ToolCallStart `ClientTool` flag in `ProjectStreamedContent` (~:1084).
- `Conversations/AgentStreamEvent.cs`: new `AgentStreamEventType.StateSnapshot`, plus trailing record params
  `[Id(15)] bool? ClientTool = null` and `[Id(16)] string? State = null`.
- `Conversations/AgentGrainState.cs`: trailing `[Id(16)] string? SharedStateJson = null` and `[Id(17)] long SharedStateVersion`.
- `Tools/AgentGrain.BuildAgent.cs`: register the four tools in `SelectableTools` (~:278-285).
- `Tools/ToolCatalog.cs`: new keys `BrowserTools = "browser_tools"` and `SharedState = "shared_state"`, appended
  LAST in `Descriptors` and mapped in `ToolNameToKey`.
- `Copilot/AgentGrain.Copilot.cs`: `CopilotFunctions` (~:96) **drops** the four new tool names.

**Backend (`src/Agent42.Backend/`)**
- `Conversations/ChatContracts.cs`: `ChatRequest` gains trailing `IReadOnlyList<string>? ClientTools`.
- `Conversations/AgentApi.Conversations.cs`: passes `request.ClientTools` (validated against the known names).
- `ClientTools/AgentApi.ClientTools.cs`: **new**. `POST /api/chat/{conversationId}/tool-result` and
  `GET /api/chat/{conversationId}/pending-tool-calls`.
- `SharedState/AgentApi.SharedState.cs`: **new**. `GET` / `PUT /api/conversations/{conversationId}/state`.
- `Api/AgentApi.cs`: `MapAgentApi` calls both new `Map*Endpoints` (CLAUDE.md rule 7: a partial it never calls
  silently deletes its endpoints).

**Frontend (`src/Agent42.Backend/wwwroot/`)**
- `js/clienttools.js`: **new** module. Handler registry, the confirm dialog, the result POST, and the pending-call
  resume used by recovery. `export function wireClientTools(` called from `main.js`.
- `js/sharedstate.js`: **new** module. Renders the state panel (editable checklist plus read-only JSON for other
  keys), `PUT`s edits, applies `stateSnapshot`, and loads on conversation open. `wireSharedState(` from `main.js`.
- `js/send.js`: the request body carries `clientTools`. The `toolCallStart` arm hands `chunk.clientTool` calls to
  `clienttools.js` **without awaiting** (the reader loop keeps reading). New `stateSnapshot` arm.
  `recoverPersistedReply` (~:86) also checks pending tool calls.
- `js/artifacts.js`: exports `openArtifactInPane(convId, artifactId, version)` and `setArtifactPaneFull(bool)`
  over `window.Agent42ArtifactPane` (`shared/lib/artifactpane.js` `open` :166, `setFull` :185, `close` :194).
- `js/main.js`: the two `wire*()` calls, and a conversation-switch hook for `sharedstate.js`.
- `index.html`: `<dialog id="confirmActionDialog">` (the `groupNameDialog` shape, :690) and the state panel container.
- `styles.css`: the panel and dialog styles.
- `sw.js`: bump `CACHE` (v50 → v51) and add both modules to `SHELL` (CLAUDE.md rule 3).

**Tests (`tests/Agent42.Tests/`)**: new `ClientToolTests.cs`, `ClientToolApiTests.cs`, `SharedStatePatchTests.cs`,
`SharedStateTests.cs`, `SharedStateApiTests.cs`, `ClientToolsAssetsTests.cs`. Edits to `GrainStorageSerializerTests.cs`,
`ToolCatalogTests.cs`, `ChatStreamDisconnectTests.cs`, `ProjectLayoutTests.cs` (`FrontEndWiredModules`),
`PwaAssetsTests.cs:256`, `ResearchAssetsTests.cs:33` and `SearchAssetsTests.cs:127` (literal `agent42-shell-v50`.
Grep for the literal again at execution time), `AgentApiTests.cs`, and
`FakeChatClient.cs` if a trigger is needed.

**Docs**: a new ADR (next free number), gotchas entries, the `CLAUDE.md` feature map row, an amendment banner
on ADR 0046, and a note in ADR 0088's revisit triggers.

**Not touched**: Bicep, secrets, containers, `excludedPaths`. All new endpoints stay behind Easy Auth and use
`GetUserId()`.

## Context & constraints

### Decisions made with the user (2026-10-09)
- **Both features in one plan.** The user chose this over splitting them.
- **First client tools: all three** use cases (confirm, artifact pane, browser data).
- **No browser present: wait, then time out.** The tool call waits for a bounded time. A reconnecting client can
  still answer (stream recovery). On timeout the tool returns an "unavailable" result and the turn continues.
- **Own protocol, not AG-UI** (ADR 0088).

### Design calls made in planning (the new ADR records each with its alternatives)
- **Server-defined tools; the browser only advertises support.** AG-UI lets the client send tool *schemas*. That
  was rejected here because: (a) browser-supplied descriptions are model-facing prompt text from outside the
  server; (b) per-request tools fight AF's `AdditionalTools` overwrite (memory `deferred-tool-loading-b2-spike`:
  AF overwrites `AdditionalTools` with the agent's tools in default mode); and (c) tools are built per
  *activation* in `BuildAgent`, not per request. The browser sends `clientTools: [names]` and the server
  intersects it with its own set. A stale SW-cached client sends nothing, so every client tool answers "unavailable".
- **The request rides the existing `toolCallStart`.** It already carries `ToolCallId`, `ToolName` and `ToolInput`
  (`AgentGrain.cs:1084-1085`), and FunctionInvokingChatClient yields the call update before invoking it. The plan
  adds only a `clientTool: true` flag, set when the name is one of the client tools **and** this run has a broker.
- **The wait caps the WAIT, never the turn** (gotchas "Bounding a drained agent turn", ~:3422; "A chat turn
  outlives its client", ~:2513). The tool awaits its TCS with
  `WaitAsync(timeout, toolCancellationToken)`. The timeout produces a normal string result. Stop (ADR 0078)
  cancels through the tool's `CancellationToken`, which is the existing stopped-turn path ("A cancelled Agent
  Framework run persists NOTHING", ~:3674). No new `CancellationToken` goes into the turn.
- **The second request must interleave.** `AgentGrain` is non-reentrant, and the tool awaits *inside* a
  `MoveNext`. `SubmitClientToolResultAsync` / `GetPendingClientToolCallsAsync` / `SetSharedStateAsync` get
  `[AlwaysInterleave]` on the **interface** declaration and are not `[OneWay]` (gotcha "The stop handle is NOT a
  turn-scoped field", ~:3715, point 3). The TCS uses `TaskCreationOptions.RunContinuationsAsynchronously`, so
  `TrySetResult` never runs the tool's continuation inline inside the interleaved call.
- **The broker is routed per RUN, not per activation.** Two runs can be in flight on one activation (a reminder or
  `EnqueuePromptAsync` in a `MoveNext` gap). A `_clientToolBroker` field read by the tool would route a
  **background** run's `confirm_action` to the live user's browser. The routing key must come from the invoking
  run. **Primary: an `AsyncLocal<ClientToolBroker?>` set by `StreamModelReplyAsync` immediately before each
  `await enumerator.MoveNextAsync()`** (and cleared after). The iterator's body, and so FICC's tool invocation,
  runs under the ExecutionContext of the code that calls `MoveNextAsync`, so only the interactive run's pulls carry
  it. That depends on nothing in AF's message handling. **Alternative** (only if 0a disproves the primary):
  `FunctionInvokingChatClient.CurrentContext.Messages` → the run's own user `ChatMessage` → a
  `ConditionalWeakTable<ChatMessage, ClientToolBroker>` (the ADR 0082 keying), which relies on reference identity
  surviving the history provider. **Both are unmeasured, so Step 0a decides between them.** If neither holds,
  stop and re-plan. Do not fall back to a field. The broker is created only in `ChatAsync` when `interactive && clientTools`
  is non-empty. `EnqueuePromptAsync`, `ReceiveReminder` and `ask_agent` (`interactive: false`) therefore never
  have one, and the tool answers "unavailable" at once. It is **not** a member of the turn-scoped-field ritual
  (gotcha ~:525). Like `_turnCts` it is identity-scoped, so do not add it to the reset block.
- **Recovery resumes a paused call.** ADR 0046 rejected a resumable stream. This plan does not add one either: the
  `GET pending-tool-calls` endpoint returns `{toolCallId, toolName, toolInput}` for the waiting calls, and
  `recoverPersistedReply` checks it while it polls. So a PWA that froze during a confirm (Android) shows the
  dialog when it comes back, if that is within the timeout. A `ChatStreamDisconnectTests` case pins the server half.
- **Timeouts** (`ClientToolLimits`): `confirm_action` 120 s (a human), `control_artifact_pane` and
  `get_browser_context` 15 s. The result cap is `MaxResultChars = 4000`, and the server truncates (CLAUDE.md
  rule 5: a tool's return value is persisted in the 2 MB session).
- **Tool result text is the user's own browser speaking.** It is not third-party content, but it is still capped
  and serialized as data. `confirm_action` returns `{"approved":true|false}`, `{"status":"unavailable"}` or
  `{"status":"timeout"}`. Its **description** is the rule the model reads (CLAUDE.md rule 11): call it before
  irreversible or outward-facing actions, and on anything but `approved:true`, do not act.
- **`get_browser_context` content**: time zone, locale, local time, color scheme, viewport, standalone-PWA flag,
  online flag, and `window.getSelection()` text capped at 2000 chars. No clipboard (that needs a user gesture),
  and no geolocation (ADR 0012 already owns location, consent-gated).
- **Copilot kind is excluded** from all four new tools. `CopilotFunctions` reuses `SelectableTools`
  (`AgentGrain.Copilot.cs:96-102`) and invokes tools from its own RPC thread (gotcha ~:3922). Its RPC timeout
  under a 120 s wait is unmeasured, and the history mirror is separate (ADR 0085). Research kind is **included**.
  `LoopAgent` wraps an ordinary `ChatClientAgent`, so FICC and the wait behave the same.
- **Selectable keys** `browser_tools` (all three client tools) and `shared_state` are appended LAST in
  `Descriptors` (the order-stability rule in `ToolCatalog.cs`). They are ON for a `null` selection and OFF for
  anyone who ever pressed Save (rule 11, ADR 0021). Accepted, and stated in the ADR.
- **Shared state is stored as a string**, `SharedStateJson`, not a `JsonNode` or `JsonElement`. Orleans cannot
  serialize `JsonNode`, and a string round-trips through Cosmos `$type` JSON unchanged. Prove it on the Cosmos
  path in `GrainStorageSerializerTests` (rule 2). Caps (`SharedStateLimits`): 16 KB serialized, depth 8, top-level
  must be an object. The version is an optimistic-concurrency counter.
- **Model writes are JSON Patch; the wire carries snapshots.** `update_shared_state(ops)` takes RFC 6902 ops
  (`add`/`remove`/`replace`/`move`/`test`) because they are cheap and exact for the model. After each successful
  apply, the grain emits one `stateSnapshot` event carrying the whole state and version. That is the live-only
  `Todos` precedent (ADR 0082 §7: the whole list on change). A `stateDelta` wire event was **deliberately not
  built**: at a 16 KB cap a snapshot costs nothing, and snapshots are idempotent under recovery and reconnection.
  This is a deviation from the original ask, so it goes in the ADR.
- **Patch library is resolved, not recalled** (hard rule 4). Step 0b checks whether
  `Microsoft.AspNetCore.JsonPatch.SystemTextJson` (first-party, .NET 10) can patch a `JsonNode` / `JsonObject`
  target. If it can, add it with `dotnet add package`. If not, use `JsonPatch.Net`, same command. Either way it is
  pinned in `Directory.Packages.props`, and `SharedStatePatch` is the only caller.
- **The model sees user edits** through a context injection that reads `_state.State.SharedStateJson` **when the
  run is invoked**: one instruction block that carries the current state JSON, added only when `shared_state` is
  enabled and the state is non-empty. It needs no run-keying, because the state belongs to the conversation, and a
  background run in a gap reads the same state, which is correct. Injected `Instructions` neither come back nor
  accumulate (gotcha ~:2934), and that is the property wanted here. The storage filter `IsInjectedOrReplayed` must
  not be touched (gotcha ~:3784).
- **Incognito: shared state is simply unavailable** (critic H1). `_ephemeral` is turn-scoped (cleared in
  `ChatAsync`'s `finally`, `AgentGrain.cs:~506`; gotcha ~:568), and `AgentGrainState` has no persisted incognito
  flag. So "persist except when incognito" cannot be decided outside a turn, and an incognito turn's in-memory
  write would be persisted by the next normal turn on the warm activation (the gotcha ~:3171 hazard). Therefore:
  (a) `update_shared_state` **refuses** during an `_ephemeral` turn with a plain-language string, exactly like
  `send_push_notification` (`Push/AgentGrain.PushTool.cs:62`), so it never mutates state; (b) the state injection
  is skipped on an `_ephemeral` turn; (c) `PUT` is refused (409, `"conversation has no saved state"`) when the
  grain's persisted record does not exist (`_state.RecordExists == false`). That covers an incognito conversation,
  which is never written, and a not-yet-saved new chat, which is harmless because the panel only appears once the
  agent has written state.
- **User edits during a running turn are allowed, and never write mid-turn** (critic H2). `PUT` →
  `SetSharedStateAsync([AlwaysInterleave])` with `baseVersion` returns 409 on a mismatch, so a model write in the
  same gap cannot be silently lost. The grain thread serializes both writers, so there is no data race. The tool
  sees the newer version. **Persistence follows the artifacts precedent (`AgentGrain.ArtifactTools.cs:16`: tools
  never call `WriteStateAsync`).** `WriteStateAsync` serialises the whole `_state.State`, so a mid-turn write would
  persist this turn's artifact records and other mutated fields next to the *previous* turn's `SerializedSession`,
  which is a torn document. So: a run-in-flight counter (`_runsInFlight`, incremented and decremented in a
  `try/finally` at **both** run sites, `ChatCoreAsync` and `ReceiveReminder`, and **not** a member of the turn-scoped
  reset ritual, gotcha ~:525). When it is zero, the PUT writes at once. When it is non-zero, the PUT updates memory
  and sets `_sharedStateDirty`. The end-of-turn persist clears the flag. A run that ends **without** persisting
  (failure, or the stopped path that skips persist) flushes the dirty state from its `finally` only when it is the
  last run in flight. Name both the counter and the flag in the new gotcha.
- **The research checklist is NOT migrated.** `TodoCompletionLoopEvaluator` reads `TodoProvider` state
  (`Research/AgentGrain.Research.cs:27, 59-60`), so moving todos into shared state means replacing the evaluator.
  That is a follow-up (Out of scope). The generic checklist renderer is built so that follow-up only has to
  supply data.
- **Egress**: none new. No third party sees anything it did not before, so this is not a rule 13 / ADR 0013 extension.
- **Frontend has no JS test runner** (`ResearchAssetsTests.cs:6-7`). Frontend behaviour is guarded by C#
  source-text tests (the `ResearchAssetsTests` / `StreamRecoveryAssetsTests` shape) plus a manual check through
  the DevHost (`/run`).

### Governing ADRs and gotchas
ADR 0004 (session cap), 0012 (location: browser data rides the request), 0021 (selection), 0034 (incognito),
0041 (ES modules), 0046 (recovery), 0078 (stop), 0082 (live-only events, per-run keying), 0083/0084 (artifacts,
pane), 0085 (Copilot exclusion), 0088 (why not AG-UI). Gotchas at ~:183, :525, :1589, :2271, :2308, :2513,
:2934, :3014, :3422, :3674, :3715, :3784, :3922.

## Steps

### Step 0: spikes (`/spike-dotnet`, outside the repo, pinned to the repo's package versions). Blocking.
- **0a. Per-run routing.** A fake `IChatClient` drives `ChatClientAgent.RunStreamingAsync` with the repo's
  `InMemoryChatHistoryProvider` setup. Pull it with the `StreamModelReplyAsync` shape (manual `MoveNextAsync`).
  Interleave a second run on the same objects, as a background run in the gap would. Check whether the tool sees
  the `AsyncLocal` broker for the interactive run's pulls only (primary). Also check whether
  `FunctionInvokingChatClient.CurrentContext.Messages` holds the same user `ChatMessage` reference (alternative).
  Record which held, with output, in the Review section. Do **not** use `AgentRunOptions.AdditionalProperties`
  (gotcha ~:3798: copied into every provider request).
- **0b. Orleans + HTTP delivery and wait** (critic M3). In the TestCluster and through the real chat endpoint on
  Kestrel, use a grain `IAsyncEnumerable` method that yields item 1 and then awaits 150 s before item 2. Assert
  **both**: (i) item 1 reaches the **HTTP client** (the NDJSON line is read) *before* the delay ends, so Orleans
  async-enumerable batching does not hold the `toolCallStart` back while the tool waits, which would time out every
  client tool; (ii) item 2 arrives rather than a `TimeoutException`, under the default messaging options. If (i)
  fails, re-plan: the request event would have to be pushed out-of-band. If (ii) fails, the fallback is to raise
  the response timeout. Also confirm in the Azure Container Apps docs that the ingress request/idle timeout is
  comfortably above `confirm_action`'s 120 s (ACA's documented HTTP request timeout is 240 s; verify, do not recall).
  Record the results.
- **0c. Patch library.** Check `Microsoft.AspNetCore.JsonPatch.SystemTextJson` against a `JsonObject` target
  (add / replace / remove / move / test, and an array `-` append). Pick the library and record why.
- Verify: the spike output is pasted into the spec's Review section. No repo change.

### Step 1: stream and contract plumbing
- `AgentStreamEvent.cs`: add `StateSnapshot`, and the trailing `ClientTool` and `State` params.
- `IAgentGrain.ChatAsync` trailing `clientTools`. `ChatRequest.ClientTools`. The endpoint passes it, after
  filtering to the known client tool names and capping the count.
- Verify: `dotnet build Agent42.slnx`. Run the existing `AgentApiTests` (NDJSON shape unchanged for old events),
  and add `ChatEndpoint_PassesClientToolsThrough` (unknown names are dropped).

### Step 2: client-tool broker and grain wiring (depends on 0a, 0b)
- `ClientToolBroker` and `ClientToolLimits`. In `AgentGrain.ChatAsync`, create the broker when
  `interactive && clientTools.Count > 0` and key it per Step 0a. Complete and unregister it in the `finally`; any
  still-pending TCS gets `TrySetCanceled`.
- `AgentGrain.ClientTools.cs`: a shared `AwaitClientResultAsync(name, input, timeout, ct)` with a cognitive
  complexity budget (S3776 < 15: split helpers). `SubmitClientToolResultAsync(callId, json)` returns
  `bool` (false if unknown or already completed), truncates to `MaxResultChars`.
  `GetPendingClientToolCallsAsync()` returns the pending calls of the **interactive** run only.
- `ProjectStreamedContent`: set `ClientTool = true` on ToolCallStart for a client tool name when the run has a broker.
- Verify: `ClientToolTests` (shared cluster, `ArtifactToolTests` shape, `CallToolPrompt`):
  `ConfirmAction_ResultPostedMidTurn_ResumesTheTurn` (the result is echoed in the reply through the scripted-result
  markers), `ConfirmAction_NoBrowser_ReturnsUnavailableImmediately` (`interactive: false`),
  `ConfirmAction_Timeout_ReturnsTimeoutAndTurnCompletes` (a shortened limit hook, or an injectable
  `TimeProvider`), `Stop_WhileWaitingForAClientTool_StopsTheTurn` (`StopGenerationTests` shape. It **also asserts
  the persisted session holds no `FunctionCallContent` without its result**, which would poison the next provider
  call; critic M5), `BackgroundRun_CannotRouteToTheInteractiveBroker` (an `EnqueuePromptAsync` run during an
  interactive wait answers unavailable, and the interactive broker sees no call),
  `PendingToolCalls_ExcludeABackgroundRunsCall`, `SubmitResult_UnknownCallId_ReturnsFalse`,
  `SubmitResult_AfterTimeoutOrStop_ReturnsFalse`, `SubmitResult_IsTruncated`,
  `ToolCallStart_IsFlaggedClientTool_OnlyWithABroker`.

### Step 3: the three client tools, registration, catalog, Copilot exclusion
- Register `confirm_action`, `control_artifact_pane` (`action: open|close|fullscreen|exitFullscreen`,
  `artifactId?`, `version?`. Validate `artifactId` against `_state.State.Artifacts` **server-side** before
  forwarding, so an unknown id returns an error without a browser round trip) and `get_browser_context`
  (no parameters, the `get_current_location` shape). Descriptions carry the policy.
- `ToolCatalog`: `BrowserTools` and `SharedState` keys appended last, plus the name→key map.
  `CopilotFunctions` drops the four names.
- Verify: `ToolCatalogTests` (new keys last, mapped, null selection = on, saved selection without them = off),
  `ControlArtifactPane_UnknownArtifact_FailsWithoutWaiting`, `CopilotFunctions_ExcludeClientAndStateTools`.

### Step 4: client-tool endpoints *(parallelizable with Step 6 after Step 3)*
- `AgentApi.ClientTools.cs`: `POST /api/chat/{conversationId}/tool-result` `{toolCallId, result}` returns 204 if
  accepted, 404 if not. The body is capped (reject > 8 KB with 413). `GET /api/chat/{conversationId}/pending-tool-calls`.
  Both use `AgentKey(http.GetUserId(), …)`. Wire them in `MapAgentApi`.
- Verify: `ClientToolApiTests` (`SharedApiFixture` subclass, `ArtifactsApiTests` shape):
  `PostResult_CompletesAWaitingCall`, `Stream_DeliversTheClientToolCallBeforeWaiting_ThenResumesOnPost` (the test
  **reads** the `clientTool:true` line from the HTTP stream and only then POSTs, so the ordering is proven end to
  end, not assumed; critic M3), `PostResult_AfterTimeout_Is404`,
  `PostResult_OtherUsersConversation_Is404` (the key is per user),
  `PendingToolCalls_ListsTheWaitingCall`, `PostResult_Oversized_Is413`, `MapAgentApi_MapsClientToolRoutes`.
  `ChatStreamDisconnectTests`: `ClientDisconnectsDuringAClientToolWait_ResultStillResumesAndPersists`
  (real Kestrel and a raw socket, per that gotcha. TestServer is vacuous here).

### Step 5: frontend client tools
- A 404 on the result POST (late answer after a timeout or Stop) is swallowed quietly: close the dialog, no error toast.
- `clienttools.js` handlers, the confirm `<dialog>` (all text via `textContent`, Esc means declined), and the artifact
  pane helpers exported from `artifacts.js`. In `send.js`: the `clientTools` list in the body, the non-blocking
  dispatch on `chunk.clientTool`, closing any open dialog for the turn on `done` or stop, and recovery calling
  `resumePendingClientTools(convId)`. `main.js` wiring. `index.html` dialog. `sw.js` CACHE bump and SHELL entry.
- Verify: `ProjectLayoutTests` (module wired, in SHELL, reachable), the `PwaAssetsTests` / `ResearchAssetsTests`
  CACHE literals updated, and new `ClientToolsAssetsTests` (source text: `send.js` sends `clientTools`, dispatches
  on `clientTool`, recovery calls the resume, the dialog uses `textContent` and never `innerHTML`). Then a
  **manual DevHost run** (`dotnet run --project tools/Agent42.DevHost`, add a canned trigger if needed). Confirm
  approve and decline, the pane opening an artifact, the browser context, and Stop while the dialog is open.
  Report what was observed.

### Step 6: shared state, server *(depends on 0c, 1)*
- Add the package (Step 0c's choice) with `dotnet add package`, pinned in `Directory.Packages.props`.
- `SharedStatePatch` (pure; caps enforced after apply; a failed `test` op leaves the state unchanged). Grain fields
  `[Id(16)]`/`[Id(17)]`. `AgentGrain.SharedState.cs`: `update_shared_state(ops)` returns
  `{version}` or an error string, emits `StateSnapshot` after the `FunctionResultContent` (the Todos emit site,
  `Research/AgentGrain.Research.cs:271-273`). Per-run state injection. `GetSharedStateAsync`,
  `SetSharedStateAsync(json, baseVersion)` with ephemeral handling.
- `AgentApi.SharedState.cs`: `GET` / `PUT /api/conversations/{conversationId}/state` (409 on a version
  mismatch, 400 on non-object, depth or size violations). Wire them in `MapAgentApi`.
- Verify: `SharedStatePatchTests` (every op, caps, atomic failure), `GrainStorageSerializerTests`
  (`SharedStateWrittenBefore_FieldExisted_Deserializes`, `SharedState_RoundTripsThroughCosmosJson`),
  `SharedStateTests` (`UpdateSharedState_EmitsSnapshot_AndPersists`,
  `UserEdit_IsVisibleToTheNextTurn` (FakeChatClient echoes instructions, gotcha ~:1400),
  `UserEdit_DuringATurn_DoesNotBlock_AndVersionConflicts409`,
  `UserEdit_DuringATurn_IsNotWrittenMidTurn_AndPersistsAtTurnEnd` (a PUT interleaved with a turn that also creates
  an artifact: the persisted document is never torn; critic H2), `UserEdit_DuringAFailedTurn_IsFlushedByTheLastRun`,
  `IncognitoTurn_UpdateSharedState_Refuses_AndNothingLeaksIntoTheNextNormalTurn`,
  `Put_OnAConversationWithNoSavedRecord_Is409` (the PUT-after-incognito-turn case; critic H1),
  `InjectedState_IsNotStoredInHistory`), and `SharedStateApiTests`.

### Step 7: shared state, frontend
- `sharedstate.js`: the panel above the composer for the current conversation, collapsible, hidden when the
  state is empty. A `checklist: [{id,title,done}]` key renders as an editable checklist (toggle, rename, add,
  remove). Other keys render as a read-only `<pre>` with `textContent`. Edits are debounced, `PUT` with
  `baseVersion`, and a 409 re-fetches. Load on conversation switch. The `stateSnapshot` arm in `send.js`. `main.js`
  wiring, SHELL entry (the same CACHE bump as Step 5 when it lands in the same MR).
- Verify: `ProjectLayoutTests`, a new source-text guard in `ClientToolsAssetsTests` (or `SharedStateAssetsTests`),
  and a manual DevHost check (agent adds items → the user ticks one → the next turn's reply reflects it).

### Step 8: docs (`/sync-docs`)
- **Dependency:** ADR 0088 (AG-UI deferral) is a separate MR. If it has merged, this ADR is **0089** and amends
  0088's trigger 2. If it has not, still take 0089 (0088 is reserved), and skip the 0088 note.
- New ADR: context, decisions with alternatives
  (AG-UI-style client-declared schemas, a field-routed broker, resumable stream, `stateDelta` on the wire,
  `JsonNode` in state, migrating research todos now), consequences (the rule 11 OFF-for-savers, the Copilot
  exclusion, the background "unavailable" answer). Amendment banner on ADR 0046 (recovery now also resumes pending
  client tool calls). ADR 0088's revisit-trigger 2 notes it is now met without AG-UI.
- Gotchas: (1) a client tool's wait: `[AlwaysInterleave]` submit, per-run routing (and why the field shape leaks
  across runs), cap the wait and never the turn, `RunContinuationsAsynchronously`; (2) shared state: string
  storage, the injection is never stored, PUT interleaves with a version check. Add an exemplar line if the
  client-tool shape is meant to be copied.
- `CLAUDE.md`: a feature-map row "Client tools and shared state" → the new ADR, 0088.
- Verify: `dotnet test Agent42.slnx` (doc-path integrity tests included), and
  `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | A client tool call pauses the turn and resumes on the posted result | `ConfirmAction_ResultPostedMidTurn_ResumesTheTurn`, `PostResult_CompletesAWaitingCall` | `ClientToolTests.cs`, `ClientToolApiTests.cs` |
| 2 | No browser (background run or no advertised support) → immediate "unavailable" | `ConfirmAction_NoBrowser_ReturnsUnavailableImmediately` | `ClientToolTests.cs` |
| 3 | Bounded wait: timeout → result, the turn completes and persists | `ConfirmAction_Timeout_ReturnsTimeoutAndTurnCompletes` | `ClientToolTests.cs` |
| 4 | A dropped connection still lets the result resume and persist the turn | `ClientDisconnectsDuringAClientToolWait_ResultStillResumesAndPersists` | `ChatStreamDisconnectTests.cs` |
| 5 | A reconnecting client can find the waiting call | `PendingToolCalls_ListsTheWaitingCall`; recovery source guard | `ClientToolApiTests.cs`, `ClientToolsAssetsTests.cs` |
| 6 | Stop works while waiting | `Stop_WhileWaitingForAClientTool_StopsTheTurn` | `ClientToolTests.cs` |
| 7 | A background run never reaches the user's browser | `BackgroundRun_CannotRouteToTheInteractiveBroker` | `ClientToolTests.cs` |
| 8 | Results are capped; another user's conversation is unreachable | `SubmitResult_IsTruncated`, `PostResult_OtherUsersConversation_Is404`, `PostResult_Oversized_Is413` | `ClientToolTests.cs`, `ClientToolApiTests.cs` |
| 9 | New keys land last, ON for null and OFF for savers; Copilot excludes them | `ToolCatalogTests` additions, `CopilotFunctions_ExcludeClientAndStateTools` | `ToolCatalogTests.cs`, `ClientToolTests.cs` |
| 10 | Artifact pane tool rejects unknown ids server-side | `ControlArtifactPane_UnknownArtifact_FailsWithoutWaiting` | `ClientToolTests.cs` |
| 11 | JSON Patch ops apply atomically under caps | `SharedStatePatchTests` | `SharedStatePatchTests.cs` |
| 12 | Shared state persists on the Cosmos JSON path, old state reads as empty | `SharedStateWrittenBefore_FieldExisted_Deserializes`, `SharedState_RoundTripsThroughCosmosJson` | `GrainStorageSerializerTests.cs` |
| 13 | Agent updates emit a snapshot and persist | `UpdateSharedState_EmitsSnapshot_AndPersists` | `SharedStateTests.cs` |
| 14 | A user edit is visible to the next turn and never stored as history | `UserEdit_IsVisibleToTheNextTurn`, `InjectedState_IsNotStoredInHistory` | `SharedStateTests.cs` |
| 15 | User edits during a turn do not block; stale base → 409 | `UserEdit_DuringATurn_DoesNotBlock_AndVersionConflicts409`, `SharedStateApiTests` | `SharedStateTests.cs`, `SharedStateApiTests.cs` |
| 16 | Incognito never creates or leaks shared state | `IncognitoTurn_UpdateSharedState_Refuses_AndNothingLeaksIntoTheNextNormalTurn`, `Put_OnAConversationWithNoSavedRecord_Is409` | `SharedStateTests.cs` |
| 16a | A user edit never tears the persisted document mid-turn | `UserEdit_DuringATurn_IsNotWrittenMidTurn_AndPersistsAtTurnEnd`, `UserEdit_DuringAFailedTurn_IsFlushedByTheLastRun` | `SharedStateTests.cs` |
| 16b | The client tool request reaches the browser before the wait | Spike 0b + `Stream_DeliversTheClientToolCallBeforeWaiting_ThenResumesOnPost` | `ClientToolApiTests.cs` |
| 16c | Stop or timeout leaves no orphan call; a late answer is a quiet 404 | `Stop_WhileWaitingForAClientTool_StopsTheTurn`, `SubmitResult_AfterTimeoutOrStop_ReturnsFalse`, `PostResult_AfterTimeout_Is404` | `ClientToolTests.cs`, `ClientToolApiTests.cs` |
| 17 | Frontend modules wired, in SHELL, CACHE bumped; safe DOM writes | `ProjectLayoutTests`, `PwaAssetsTests`, `ClientToolsAssetsTests` | same |
| 18 | End to end in a browser: confirm, pane, context, checklist edit | Manual DevHost check (reported, not claimed) | n/a |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at `9dea9f0`)

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` over `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (error) |
| `vulnerable_packages` | 0 | CI package-freshness job (the new JSON Patch package must pass) |
| CI hard block | build, tests, `dotnet format`, doc-path integrity | `.github/workflows/pr-checks.yml` |

## Out of scope
- **Migrating the research agent's checklist** (`TodoProvider` + `TodoCompletionLoopEvaluator`) into shared state.
  Follow-up. It needs an evaluator reading shared state, and it would close ADR 0082's "checklist is not replayed
  on reload".
- **`stateDelta` wire events**. Snapshots only (see Context).
- **Client-declared tool schemas** (AG-UI style) and an AG-UI endpoint (ADR 0088).
- **Client tools and shared state for Copilot-kind chats** (ADR 0085). Needs its own RPC-timeout measurement.
- **Client tools in background paths** (agent-run, reminders, `ask_agent`). They answer "unavailable" by design. A
  "park until the user answers" mode (surviving silo restarts) was the rejected third option in the interview.
- **Using `confirm_action` to gate existing tools automatically** (push, MCP writes). This plan gives the model
  the tool and the description policy; a hard approval gate wired into specific tools is a separate decision.
- Clipboard read, geolocation through client tools, push-while-waiting.

## Review

### Critic pass (2026-10-09): 2 high, 3 medium, 3 low, all accepted and folded in
- **H1 Incognito leak.** `_ephemeral` is turn-scoped, so PUT between turns could not tell an incognito chat apart,
  and an incognito turn's write would be persisted by the next normal turn. → The tool refuses on an ephemeral turn
  (the push precedent), injection is skipped, and PUT is a 409 when `RecordExists` is false. Two new tests.
- **H2 Mid-turn `WriteStateAsync` tears the document.** → No write while a run is in flight
  (`_runsInFlight` + `_sharedStateDirty`), following the artifacts "tools never write" precedent. Two new tests.
- **M3 Spike 0b missed delivery-before-wait.** → 0b asserts the first item reaches the HTTP client before the
  delay, and checks the ACA ingress timeout. New end-to-end stream test.
- **M4 Routing fallback undefined.** → Primary is now an `AsyncLocal` set around each `MoveNextAsync` (independent
  of AF message identity). The `ConditionalWeakTable` keying is the alternative. If neither holds: stop and
  re-plan, never fall back to a field. Injection reads `_state` at invoke time (stated).
- **M5 Stop / late answers.** → The stop test asserts no orphan `FunctionCallContent`. A late POST is a 404 that the
  client swallows. A gap-run pending-call test was added.
- **L6** `SearchAssetsTests.cs:127` added to the CACHE-literal edits. **L7** ADR numbering fixed to 0089, with the
  0088 dependency stated. **L8** checked: no test double implements `IAgentGrain`. `AgentMcpRunner` uses named
  args. The trailing parameter is safe.

### Step 0 spike output
**0a. Per-run routing** (file-based spike, `Microsoft.Agents.AI` 1.23.0, a fake `IChatClient`, `InMemoryChatHistoryProvider`,
manual `MoveNextAsync` with an `AsyncLocal` set around each pull, a second run interleaved while the first run's
tool awaited). Output:
```
A saw broker=brokerA
B saw broker=null
CurrentContext holds same user msg ref: A=True B=True
PRIMARY HOLDS
```
Both held; the primary (`AsyncLocal` around the pulls) was built, since it depends on nothing in AF's message handling.

**0b. Orleans + HTTP delivery and wait** (file-based spike: Orleans 10.3.1 silo co-hosted with Kestrel, a grain
`IAsyncEnumerable` yielding item 1, awaiting 150 s, yielding item 2, streamed as NDJSON with a flush per item, read by
an `HttpClient` line by line). Output:
```
[0,2s] client read: item1
[150,2s] client read: item2
[150,2s] stream ended
```
(i) item 1 reaches the HTTP client before the wait, and (ii) item 2 arrives with no `TimeoutException` under the
default messaging options. ACA ingress, from the docs (ingress-overview, updated 2026-08-31): "Request time out is
240 seconds", which leaves room for the 120 s confirm.

**0c. Patch library.** `Microsoft.AspNetCore.JsonPatch.SystemTextJson` (resolved by `#:package @*` → 10.0.12) against a
`JsonObject`. Output:
```
add: OK -> {"title":"x","checklist":[{"id":"a","done":false}],"n":1,"new":{"k":1}}
append-: OK -> {"title":"x","checklist":[{"id":"a","done":false},{"id":"b","done":false}],"n":1}
replace nested: OK -> {"title":"x","checklist":[{"id":"a","done":true}],"n":1}
remove: OK -> {"title":"x","checklist":[{"id":"a","done":false}]}
move: OK -> {"checklist":[{"id":"a","done":false}],"n":1,"name":"x"}
test ok: OK -> {"title":"x","checklist":[{"id":"a","done":false}],"n":1}
test fail atomic: JsonPatchException: The current value 'x' at path 'title' is not equal to the test value 'nope'. -> {"title":"x","checklist":[{"id":"a","done":false}],"n":2}
```
Chosen (first-party, every op works on a `JsonObject`), with two findings the code compensates for: it is NOT atomic
(`n` became 2 after the failing `test`), so `SharedStatePatch` patches a fresh copy; and (found by
`SharedStatePatchTests`) an unknown op name is silently skipped, so op names are validated first.

## Divergences from the plan (recorded at execution, 2026-10-09)

1. **`StateSnapshot` carries `StateVersion` (`[Id(17)]`) as well as `State`.** The client needs the version as the
   `baseVersion` of its next edit; folding it into the state text would make it user-editable.
2. **The call is announced to the broker when its `toolCallStart` is projected**, not only when the tool runs. The
   browser can answer between the two pulls, and that early answer was otherwise lost. Pinned by
   `ConfirmAction_ResultPostedBeforeTheToolRuns_IsKept`.
3. **A `control_artifact_pane` call the server will refuse is not flagged `clientTool`**, so the browser never acts
   on an unknown artifact. `ControlArtifactPane_UnknownArtifact_FailsWithoutWaiting` asserts both halves.
4. **Run counting wraps the two run sites rather than editing them inline:** `ChatCoreAsync` is now a thin counting
   wrapper over `ChatTurnAsync`, and `ReceiveReminder` keeps its resets and delegates the run to `RunReminderAsync`.
   This kept S3776 headroom and the reset block where the gotchas say it is.
5. **The timeout seam is an optional `ClientToolTimeouts` grain dependency** (the `TimeProvider` shape), not a
   `TimeProvider`: the test clock delegates its timers to the system clock, so it cannot shorten a wait. The test
   silo registers 3 s.
6. **The shared-state conversation hook lives in `conversations.js` (`loadSharedState` on open) and `thread.js`
   (`clearSharedState` in `clearThread`)**, beside the artifact pane's identical hooks, rather than in `main.js`.
   `main.js` calls `wireClientTools()` and `wireSharedState()` as planned.
7. **`artifactpane.js` (the shared classic script) now also returns `setFull` and `isOpen`**; the plan assumed
   `setFull` was already exposed. `ArtifactAssetsTests` pins the new return shape.
8. **The Copilot exclusion test is `CopilotAgentTests.CopilotFunctions_ExcludeClientAndStateTools`**, where the
   recording factory and helpers already live.
9. **`FakeChatClient` awaits its stream gate before the faulting throw**, so a test can hold a failing turn in flight
   (`UserEdit_DuringAFailedTurn_IsFlushedByTheLastRun`). With no gate installed the behaviour is unchanged.
10. **Extra endpoint statuses:** `PUT …/state` also answers 413 for a body over twice the cap, and `POST
    …/tool-result` answers 400 for a malformed body.
11. **The injected state is gated by the `shared_state` selection only**, not by model capability, like memory: a
    no-tools model still reads (and the user can still edit) the state; it just cannot write it.
12. **DevHost gained canned triggers** (`CannedChatClient.ClientTools.cs`) so the manual check runs offline.
13. **ADR 0088 had not merged**, so this is ADR 0089 and the note on 0088's revisit trigger was skipped, as the plan
    said. CLAUDE.md names 0088 by number only.

### Manual check (DevHost, headless Chromium driven by a Playwright script, 2026-10-09)
Observed: the confirm dialog shows the model's text, Approve returns `{"approved":true}` to the next reply, and
Esc after an earlier Approve returns `{"approved":false}` (the `returnValue` reset). `get_browser_context` returned a
time zone and selection. The pane opened the conversation's artifact, went full screen and closed. "make a checklist"
showed the panel with 3 items. Ticking one and adding a fourth in the panel reached the next turn's injected state.
Reopening the chat reloaded 4 items. Stop while the confirm was open closed the dialog, marked the turn stopped, and
the late POST was a quiet 404. Found and fixed during the check: the page-side dedupe of call ids kept ids forever,
so a provider reusing an id (the canned client did) skipped the second call. Dedupe is now per in-flight call.

### Critic pass on the diff (2026-10-09): pass, five low findings
- **Plan-named tests under other names.** The coverage exists: `Stream_DeliversTheClientToolCallBeforeWaiting_ThenResumesOnPost`
  (a POST completes a waiting call), `ProjectLayoutTests.AgentApiParts` (routes mapped), the no-broker tests (no
  `clientTool` flag), `BackgroundRun_CannotRouteToTheInteractiveBroker` (pending list), `Put_StaleBaseVersion_Is409` and
  the mid-turn conflict in `UserEdit_DuringATurn_IsNotWrittenMidTurn_AndPersistsAtTurnEnd`. The post-Stop late submit is
  asserted inside `Stop_WhileWaitingForAClientTool_StopsTheTurn`. No change.
- **`CopilotFunctions` header split by the new helper.** Fixed: the helper and its reason moved above the block.
- **The last-run flush writes a failed run's leftovers.** Accepted residual; now stated in the shared-state gotcha.
- **Two quick saves raced on `baseVersion`.** Fixed: `sharedstate.js` sends one save at a time.
- **Truncation can split a surrogate pair.** Accepted: the text stays valid for the model.
