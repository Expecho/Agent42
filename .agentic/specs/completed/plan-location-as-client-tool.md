# Plan: `get_current_location` becomes a client tool

planned-against: main @ 4ce0c58

## Overview

ADR 0012 sends the browser's position on every chat request (`ChatRequest.Location`). It does this because, at the
time, a tool could not ask the browser for anything mid-turn. ADR 0089 removed that limit: a tool can now pause the
turn, have the browser run something, and resume with the result. This plan moves `get_current_location` onto that
mechanism. The browser answers only when the model actually calls the tool. The server still validates the
coordinates and reverse-geocodes them. As a result, the position no longer goes out with every message, and the
`_turnLocation` field, the most hazardous member of the turn-scoped-field family, is deleted.

Branch: `feature/location-as-client-tool` from `main` @ `4ce0c58`.

## Affected areas

Server (Agents):
- `src/Agent42.Agents/ClientTools/ClientToolLimits.cs`: add a `GetCurrentLocation` constant and include it in
  `ToolNames`.
- `src/Agent42.Agents/Location/AgentGrain.LocationTool.cs`: the tool now waits on the browser through
  `AwaitClientResultAsync`, then parses, validates and geocodes the answer.
- `src/Agent42.Agents/Location/UserLocation.cs`: doc comment only. The record stays: it is still the geocoder's
  input type.
- `src/Agent42.Agents/Conversations/AgentGrain.cs`: delete `_turnLocation`, its assignment and its three clears
  (`ChatAsync` finally, `EnqueuePromptAsync`, `ReceiveReminder`), and the `location:` term in the "received a
  message" log. Re-point the doc comments that cite `_turnLocation` (class remarks, `_ephemeral`, `_interactiveTurn`
  and the others in that block) to `_ephemeral`.
- `src/Agent42.Agents/Conversations/IAgentGrain.cs`: remove the `location` parameter from `ChatAsync`, along with
  its doc paragraph.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`: update the `get_current_location` description and comment.
  The tool must keep exactly zero model-visible parameters.
- `src/Agent42.Agents/Tools/ToolCatalog.cs`: no change to the mapping (`get_current_location` → `location` stays).
  Update the `Location` descriptor text only if it is now inaccurate.
- `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs`: no code change. `IsExcludedFromCopilot` already excludes
  every `ClientToolLimits.ToolNames` member. Update its comment to mention location.

Server (Backend):
- `src/Agent42.Backend/Conversations/ChatContracts.cs`: remove `ChatRequest.Location`.
- `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs`: remove the location validation and pass-through
  (around lines 156–162).
- `src/Agent42.Backend/Mcp/AgentMcpRunner.cs`: remove `location: null` from the `ChatAsync` call (line 132) and
  `_turnLocation` from the doc remark (line 202).

Frontend:
- `src/Agent42.Backend/wwwroot/js/location.js`: replace `currentLocationForSend()` with an async
  `locationForTool()` (see step 3).
- `src/Agent42.Backend/wwwroot/js/clienttools.js`: add `get_current_location` to `CLIENT_TOOLS` and a handler.
- `src/Agent42.Backend/wwwroot/js/send.js`: drop the `currentLocationForSend` import and the `location:` field
  (around line 234).
- `src/Agent42.Backend/wwwroot/sw.js`: bump `CACHE` (CLAUDE.md rule 3). It is `agent42-shell-v51` at plan time, so
  bump whatever it is when the step runs.

DevHost:
- `tools/Agent42.DevHost/Program.cs`: update the location comment (line ~177).
- `tools/Agent42.DevHost/CannedChatClient.ClientTools.cs`: add a `LocationTrigger` ("where am i") that calls
  `get_current_location`, so the round trip can be clicked through offline.

Tests (all in `tests/Agent42.Tests/`):
- `AgentGrainTests.cs`: rewrite the location block (lines ~1886–2050), the `ChatAt` helper (~2842), and the
  comments at ~764 / ~1967 / ~2455.
- `AgentApiTests.cs`: replace `Chat_ForwardsLocation` and `Chat_DropsOutOfRangeLocation` (~81–113).
- `ClientToolTests.cs`, `ClientToolApiTests.cs`: new location cases.
- `ClientToolsAssetsTests.cs`: the `ClientToolList_MatchesTheServer` list and count become 4.
- `ToolCatalogTests.cs`: the two tests at ~201–230 iterate `ClientToolLimits.ToolNames` and assume every member
  maps to `browser_tools` and is OFF for an older saved selection. Location must be excluded from both loops and
  asserted separately (see Context).
- `CopilotAgentTests.cs`: `CopilotFunctions_ExcludeClientAndStateTools` gains `get_current_location`.
- `LocationAssetsTests.cs`: rewrite for the tool path.
- `ProjectLayoutTests.cs`: drop `[InlineData("_turnLocation")]` (~217) and fix its comment (~212).
- `FakeChatClient.cs`: comments near `BuildGetCurrentLocationCall` (~569) and `BuildLocationReminderCall` (~595).
  The triggers stay.

Docs:
- New ADR (`/decide`) amending 0012 and 0089, plus an amendment note in
  `.agentic/decisions/0012-user-location-google-geocoding.md`.
- `.agentic/gotchas.md`: four entries plus every other `_turnLocation` citation (step 4).
- `docs/features/current-location.md` (rewrite), `docs/features/scheduled-reminders.md` (one snippet line).
- `CLAUDE.md` feature map: add the new ADR number to the Location row.

## Context & constraints

User decisions (from the interview, 2026-10-09):
- **Copilot chats lose location.** They already cannot run client tools (`AgentGrain.Copilot.cs:95`,
  `IsExcludedFromCopilot`). The rejected alternative was keeping the request-borne path for Copilot only, because it
  would have kept `ChatRequest.Location`, `_turnLocation` and all three clears, which is most of what this plan
  removes. Making client tools work under Copilot needs its own spike (the ADR 0089 note on the unmeasured 120 s
  wait under Copilot's RPC timeout) and is out of scope. Record this as a consequence in the ADR.
- **On a stale or missing cache, the browser takes one fresh reading.** The ADR 0012 rule "nobody waits for a GPS
  fix" existed because the read sat on every send. It now runs only when the model asks, and that turn is already
  paused. Conditions: only when capture is already authorized (`isCaptureAuthorized()`). On the Permissions-API leg that
  means no prompt is ever shown. On the WebKit leg the gate is only a stored flag, so a site permission reset to
  "ask" can still prompt mid-turn. This is accepted and documented, and device-verified only. Bound it below the server wait: `ClientToolLimits.AutomaticTimeout` is 15 s, so give
  `getCurrentPosition` a timeout of about 10 s. A cached fix ≤ `LOCATION_MAX_AGE_MS` (30 min) is answered
  immediately. A stale fix is never returned.

Design decisions made in planning (redirect them in review if wrong):
- **Keep the `location` selectable key.** `get_current_location` joins `ClientToolLimits.ToolNames`, which gives
  the endpoint intersection and the Copilot exclusion for free, but its `ToolCatalog` key stays `location`, not
  `browser_tools`. Mapping it to `browser_tools` would turn location OFF for everyone who ever pressed Save (ADR
  0021, CLAUDE.md rule 11). This is why the two `ToolCatalogTests` loops must exclude it.
- **The tool keeps zero model-visible parameters.** The SSRF argument from ADR 0012 still holds, but its input now
  arrives through the client tool-result POST instead of the chat request. The coordinates are parsed from the
  browser's JSON as `double`s and range-checked with `UserLocation.IsValid` before the geocoder sees them. Anything
  else (a non-number, out of range, garbage, oversize) is treated as "no usable fix" and never reaches the
  geocoder. `GetCurrentLocation_ExposesNoParameters` must stay green, unchanged in intent.
- **The browser's answer shape** is either `{latitude, longitude, accuracyMetres, capturedAt}`, or
  `{"status":"not_shared"}` (not authorized, or permission denied), or `{"status":"no_fix"}` (authorized but no
  fresh fix could be taken). The server also gets `{"status":"unavailable"}` (no broker: a reminder, a background
  run, `ask_agent`, MCP, a browser that did not advertise the tool) or `{"status":"timeout"}` from
  `AwaitClientResultAsync`. The tool turns each into plain language for the model:
  - `not_shared`: keep today's text exactly ("has not shared their location … enable location sharing in
    Preferences"). Tests assert on it.
  - `no_fix`: say the device could not get a position right now, and not to guess.
  - `unavailable`: say no browser is connected to this chat, so the location cannot be read, and not to guess.
  - `timeout`: same as `no_fix`.
  Never surface the raw status JSON as the result. The model needs the instruction "don't guess".
- **Server-side staleness is not re-checked.** The browser is the user's own client and already drops stale fixes.
  `capturedAt` still goes back to the model as an absolute timestamp (keep the existing comment about why it is not
  a relative age).
- **The background refresh stays** (on grant, on visibility, and on the 5-minute interval while visible). It is
  what lets most tool calls be answered instantly. Tuning or removing the interval is out of scope.
- **`UserLocation` keeps its Orleans attributes.** It no longer crosses a grain boundary, but removing them is
  churn with no benefit. Only its doc comment changes ("why it rides the request" becomes "the browser answers the
  tool").
- **No migration.** Nothing about location was ever persisted. Existing chat histories that contain an older
  `get_current_location` result stay as they are.
- **Transition:** a PWA still running the old cached JS sends `location` and does not advertise the tool. The new
  `ChatRequest` has no `Location` property, and System.Text.Json ignores unknown members by default, so the request
  still binds. The tool answers `unavailable` until the service worker swaps the shell, which the `CACHE` bump
  forces.

Governing knowledge:
- `.agentic/decisions/0012-user-location-google-geocoding.md`: the SSRF argument, privacy costs, the WebKit
  two-legged grant, and the 5 s geocode timeout with no retries. All still stand except the transport and the
  never-wait-for-GPS rule.
- `.agentic/decisions/0089-client-tools-and-shared-state.md`: broker per run via async local, wait capped and never
  the turn, results truncated to 4000 chars, and Copilot exclusion.
- Gotchas: "A grain's turn-scoped field lives for the ACTIVATION" (~line 525), "`get_current_location` is NOT an
  SSRF sink" (~617), "WebKit has no geolocation Permissions API" (~639), "A client tool's wait: route the broker per
  RUN" (~3975), and the OTel query-redaction entry (unchanged; the geocode call is unchanged).
- Exemplar for a client tool: `GetBrowserContextAsync` in `src/Agent42.Agents/ClientTools/AgentGrain.ClientTools.cs`
  (a zero-parameter tool → `AwaitClientResultAsync`). Exemplar for a browser handler: `browserContext()` in
  `clienttools.js`. Exemplar for tests: `ClientToolTests.cs` (`ReadToClientCallAsync` and
  `SubmitClientToolResultAsync` while parked) and `ClientToolApiTests.cs`
  (`Stream_DeliversTheClientToolCallBeforeWaiting_ThenResumesOnPost`).
- Copilot: tools excluded by `IsExcludedFromCopilot` are filtered in `CopilotFunctions`. Adding the name to
  `ToolNames` is the whole change there.
- S3776 limit is 15. Parsing the browser answer plus the four status branches can push `GetCurrentLocationAsync`
  over. Split out a `ParseBrowserFix(string) → (UserLocation? fix, string? status)` helper rather than gaming the
  metric.

## Steps

Tests go in the step that changes the behavior (CLAUDE.md rule 3). Each step ends green.

### Step 1 — Server: location as a client tool, request transport removed (Agents + Backend + server tests)
This is one step on purpose. As soon as the tool stops reading `_turnLocation`, every existing location test goes
red until it is rewritten, so the switch, the removal and the test rewrite land together.

Files (src):
- `ClientToolLimits.cs`
- `Location/AgentGrain.LocationTool.cs`
- `Location/UserLocation.cs` (doc only)
- `Tools/AgentGrain.BuildAgent.cs` (description and comment)
- `Copilot/AgentGrain.Copilot.cs` (comment only)
- `Conversations/AgentGrain.cs`
- `Conversations/IAgentGrain.cs`
- `Backend/Conversations/ChatContracts.cs`
- `Backend/Conversations/AgentApi.Conversations.cs`
- `Backend/Mcp/AgentMcpRunner.cs`

Code:
- Add `public const string GetCurrentLocation = "get_current_location";` and include it in `ToolNames`. Replace the
  `"get_current_location"` literals in `BuildAgent.cs` and `ToolCatalog.cs` with the constant.
- `GetCurrentLocationAsync(CancellationToken)`: call `AwaitClientResultAsync(ClientToolLimits.GetCurrentLocation,
  ct)`, parse the answer with a `ParseBrowserFix` helper, and on a valid fix run the existing geocode-and-compact-record
  path unchanged. Map each status to the plain-language texts in Context. A truncated answer (a raw prefix cut at 4000
  chars, which is not valid JSON) falls into "no usable fix".
- Description: say it runs on the user's device and keep "don't guess". Still zero parameters.
- Delete `_turnLocation` and every assignment and clear. Keep the other clears exactly where they are (`_ephemeral`,
  `_interactiveTurn`, `_webDocumentIngested`, `_pushesThisTurn`). Do not reorder or merge them.
- Remove `location` from `IAgentGrain.ChatAsync` and `AgentGrain.ChatAsync`, and remove `ChatRequest.Location` and
  its use in the endpoint and in `AgentMcpRunner.cs:132`.
- Re-point the comments that use `_turnLocation` as the family archetype to `_ephemeral` (`AgentGrain.cs` class
  remarks and field block, `AgentMcpRunner.cs:202`).
- Update the comments that count "three" client tools: `ClientToolLimits.cs:30` ("The other two are answered by code
  in the page") and `AgentGrain.ClientTools.cs:12` ("There are three:").

Compile-break sites when the 4th positional parameter goes away. A positional `null` then lands on `bool ephemeral`.
Found by the critic; re-grep, because line numbers drift:
- `AgentGrainTests.cs` lines ~845, ~2432, ~2448, ~2465, ~2469, ~2485, ~4133, and the `ChatAt` helper at ~2845.
- Check the multi-line calls in `ArtifactToolTests.cs:47` and `:270`, `ArtifactsApiTests.cs:33` and
  `ChatSearchIndexingTests.cs:233`.
- `git grep -n "location:" -- src tools tests` and `git grep -n "ChatAsync(" -- tests`.

Tests, rewritten in the `ClientToolTests` shape (park at the client call with `ReadToClientCallAsync`, then
`SubmitClientToolResultAsync`, then drain):
- `AgentGrainTests.cs` location block (~1886–2050):
  - `GetCurrentLocation_ReturnsAddress_WhenBrowserAnswersAFix` (AC1).
  - `GetCurrentLocation_WhenNotShared_TellsModelToAskTheUser`: the browser answers `not_shared`, and the existing
    assertions are kept (AC3).
  - `GetCurrentLocation_InvalidFix_NeverReachesTheGeocoder` (AC5): a theory over out-of-range values, non-numbers,
    garbage and truncated JSON. Add a call counter to `FakeGeocoder` if it lacks one.
  - `GetCurrentLocation_NoBrowser_SaysUnavailable` (AC7).
  - `ReceiveReminder_HasNoUserLocation` (AC7): same intent, with the `unavailable` wording.
  - `Location_IsNotPersisted` (AC11): the reflection check is kept, and the turn-scoped half is dropped.
  - `GetCurrentLocation_WithoutAGeocodingKey_ReturnsCoordinatesWithoutAnAddress` (AC12): driven by a browser answer.
  - `GetCurrentLocation_ExposesNoParameters` (AC6): unchanged.
  - Fix the comments at ~764, ~1967 and ~2455.
- `AgentApiTests.cs`:
  - Delete `Chat_ForwardsLocation` and `Chat_DropsOutOfRangeLocation`.
  - Add `Chat_OldClientBodyWithLocation_StillBinds` (AC13): post a raw JSON body with a `"location"` member and
    expect 200 with a streamed reply. This pins the transition claim.
- `ClientToolApiTests.cs`:
  - `Chat_LocationToolCall_StreamsAsClientTool_AndPostedFixGeocodes` (AC1).
  - `PostResult_OutOfRangeFix_IsNotGeocoded` (AC5).
- `ToolCatalogTests.cs`:
  - Exclude `ClientToolLimits.GetCurrentLocation` from both loops (~206 and ~221).
  - Add `Location_IsAClientTool_ButKeepsItsOwnKey` (AC8).
- `CopilotAgentTests.cs`: assert `get_current_location` is absent in `CopilotFunctions_ExcludeClientAndStateTools`
  (AC9).
- `ProjectLayoutTests.cs`: drop `[InlineData("_turnLocation")]` and fix its comment.
- `FakeChatClient.cs`: comments only. The triggers stay.

Verify:
- `dotnet build Agent42.slnx` (S3776 is a build error).
- `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentGrainTests|FullyQualifiedName~ClientTool|FullyQualifiedName~AgentApiTests|FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~CopilotAgentTests|FullyQualifiedName~ProjectLayoutTests"`.
  Every test in that filter must pass, except the client-tool asset test, which goes green in step 2. The
  `ClientToolsAssetsTests` count fails here because `ToolNames` is 4 while `CLIENT_TOOLS` is still 3. Either do
  step 2 in the same commit, or accept that one red test until step 2 and say so.
- Mutation check: point `GetCurrentLocationAsync` straight at the geocoder with a hardcoded fix. Then
  `…WhenNotShared…` and `…NoBrowser…` must go red. Revert after.

### Step 2 — Frontend and asset tests
Depends on step 1. Do it in the same commit if you want no red test in between.
Files:
- `wwwroot/js/location.js`
- `wwwroot/js/clienttools.js`
- `wwwroot/js/send.js`
- `wwwroot/sw.js`
- `tests/Agent42.Tests/LocationAssetsTests.cs`
- `tests/Agent42.Tests/ClientToolsAssetsTests.cs`

Code:
- `location.js`: delete `currentLocationForSend`, and export `async function locationForTool()`:
  1. If `!(await isCaptureAuthorized())`, return `{ status: "not_shared" }`. Do not call `readPosition` before
     this check.
  2. If `lastFix` is within `LOCATION_MAX_AGE_MS`, return it.
  3. Otherwise make one fresh capture bounded by a new `LOCATION_TOOL_TIMEOUT_MS` (about 10000, below the server's
     15 s wait). Make `readPosition` take the timeout, since it hardcodes 15000 today. On success, cache the fix and
     return it. On error, call `handlePositionError(error)` and return `not_shared` for code 1, or `no_fix`
     otherwise.
  Rewrite the module comments. The send path never touches geolocation. The tool path may take one bounded capture
  under an existing grant. On the WebKit leg that grant is only a stored flag, so a site permission reset to "ask"
  can still prompt mid-turn. Say this honestly, and do not claim "never prompts".
- `clienttools.js`: add `"get_current_location"` to `CLIENT_TOOLS` and `get_current_location: () => locationForTool()`
  to `handlers`.
- `send.js`: remove the import and the `location:` field.
- `sw.js`: bump `CACHE` (it was `agent42-shell-v51` at plan time).

Tests:
- `LocationAssetsTests.cs`:
  - `Send_NeverTouchesGeolocation` (AC2).
  - `LocationTool_ChecksTheGrantBeforeCapturing` (AC3): `isCaptureAuthorized()` appears before `readPosition(`
    in the `locationForTool` body.
  - `LocationTool_DropsStaleFix_AndBoundsTheCapture` (AC4).
  - Keep `App_AuthorizesCaptureOnBothLegs`, `App_RefreshesTheFixInTheBackground` and the Preferences test, and
    update the class doc.
- `ClientToolsAssetsTests.cs`: 4 names, `Count == 4` (AC10).

Verify: `dotnet test Agent42.slnx` (full suite green).

### Step 3 — DevHost
Parallelizable with step 2.
Files: `tools/Agent42.DevHost/CannedChatClient.ClientTools.cs`, `tools/Agent42.DevHost/Program.cs`.
- Add `LocationTrigger = "where am i"` → `(ClientToolLimits.GetCurrentLocation, [])`. The critic verified there is
  no collision.
- Update the Program.cs location comment.

Verify: `dotnet build Agent42.slnx`. Then do a manual run of `dotnet run --project tools/Agent42.DevHost`: enable
location in Preferences and send "where am i". Coordinates without an address should come back.

### Step 4 — Docs
Depends on steps 1–3.
- `/decide`: ADR 0090 (re-check `ls .agentic/decisions`; 0088 is an untracked local file at plan time, 0089 is on
  main), `amends: [0012, 0089]`. It must record:
  - The premise change.
  - The alternatives: keep the request path, keep it for Copilot only, or client tools under Copilot.
  - The consequences:
    - Copilot chats lose location, and their picker still shows a Location checkbox that does nothing there.
      Either add a note in the `ToolCatalog` Location descriptor or record it as accepted.
    - The position leaves the browser only when asked. This is a privacy gain, with no egress change, because
      Google still gets coordinates exactly when the tool runs.
    - The `location` key is kept.
    - The tool path takes one bounded GPS capture, which can prompt on WebKit if the site permission was reset.
    - The SSRF input now arrives through the tool-result POST, parsed as doubles and range-checked.
    - The turn-scoped family shrinks to three.
- ADR 0012: an amendment note pointing at 0090, in the style of its 2026-07-17 amendment.
- ADR 0089: line 17 "The three first client tools" stays true historically. Add one line noting that 0090 adds a
  fourth.
- `.agentic/gotchas.md`:
  - The turn-scoped-field entry (~525): the family is now three, re-anchored on `_ephemeral`, and its evidence tests
    are updated.
  - The SSRF entry (~617): the input now arrives via `POST /api/chat/{id}/tool-result`.
  - The WebKit entry (~639): the send/tool split, and the evidence at ~655 stops naming `currentLocationForSend`.
  - The client-tool entry (~3975): a client tool may keep its own selectable key, so loops over `ToolNames` must not
    assume `browser_tools`.
  - Also the other `_turnLocation` citations at ~570–573, ~2081, ~2169–2172, ~2901 and ~3718–3722: re-point each to
    `_ephemeral`, or phrase it as history ("the former `_turnLocation`").
  - Update the `Verified` dates.
- `docs/features/current-location.md`: rewrite. The whole doc describes the request-borne path and `_turnLocation`
  (lines ~33–38, 53, 67–69, 112, 124, 127).
- `docs/features/scheduled-reminders.md:59`: drop the `_turnLocation = null;` line from its snippet.
- `CLAUDE.md`: add 0090 to the Location row of the feature map.
- `.agentic/specs/plan-deferred-tool-loading.md` (an active, untracked plan): it lists `get_current_location`
  (~83) and cites the `_turnLocation` family (~159, ~212). Add a one-line note there that this plan landed first, and
  that location is now a client tool and the family is three. It is untracked, so this edit stays local unless the
  user commits it.
- Run `/sync-docs`. Exit check: `git grep -n "_turnLocation\|currentLocationForSend\|ChatRequest.Location"` returns
  hits **only** in `.agentic/decisions/` (ADR history), `.agentic/specs/completed/` and this spec. Those are exempt as
  history.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~DocPath|FullyQualifiedName~QualityGate"`, then the
full suite, then `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | The model calling `get_current_location` streams a `toolCallStart` flagged `clientTool`, and the browser's fix is geocoded into the result | `GetCurrentLocation_ReturnsAddress_WhenBrowserAnswersAFix`; `Chat_LocationToolCall_StreamsAsClientTool_AndPostedFixGeocodes` | `AgentGrainTests.cs`; `ClientToolApiTests.cs` |
| AC2 | No location is sent on a chat request; the send path never touches geolocation | `Send_NeverTouchesGeolocation` | `LocationAssetsTests.cs` |
| AC3 | A browser that is not authorized answers `not_shared` without calling `getCurrentPosition` (no prompt on the Permissions-API leg), and the model is told to ask the user | `LocationTool_ChecksTheGrantBeforeCapturing`; `GetCurrentLocation_WhenNotShared_TellsModelToAskTheUser` | `LocationAssetsTests.cs`; `AgentGrainTests.cs` |
| AC4 | A stale cache is never returned; the fresh capture is bounded below the server wait | `LocationTool_DropsStaleFix_AndBoundsTheCapture` | `LocationAssetsTests.cs` |
| AC5 | An invalid or garbage fix never reaches the geocoder | `GetCurrentLocation_InvalidFix_NeverReachesTheGeocoder`; `PostResult_OutOfRangeFix_IsNotGeocoded` | `AgentGrainTests.cs`; `ClientToolApiTests.cs` |
| AC6 | The tool still exposes zero model-visible parameters | `GetCurrentLocation_ExposesNoParameters` (unchanged) | `AgentGrainTests.cs` |
| AC7 | A reminder, a background run, or a browser without the tool gets "unavailable" and no address | `ReceiveReminder_HasNoUserLocation`; `GetCurrentLocation_NoBrowser_SaysUnavailable` | `AgentGrainTests.cs` |
| AC8 | Location keeps its own `location` key and stays ON for an older saved selection that includes it | `Location_IsAClientTool_ButKeepsItsOwnKey` | `ToolCatalogTests.cs` |
| AC9 | Copilot chats never get `get_current_location` | `CopilotFunctions_ExcludeClientAndStateTools` | `CopilotAgentTests.cs` |
| AC10 | The browser and server client-tool lists agree (4 tools) | `ClientToolList_MatchesTheServer` | `ClientToolsAssetsTests.cs` |
| AC11 | No location state is persisted, and `_turnLocation` is gone | `Location_IsNotPersisted`; `ProjectLayoutTests` (inline data removed); `git grep _turnLocation` is empty outside ADRs | `AgentGrainTests.cs`; `ProjectLayoutTests.cs` |
| AC12 | Without a geocoding key the tool returns coordinates without an address | `GetCurrentLocation_WithoutAGeocodingKey_ReturnsCoordinatesWithoutAnAddress` | `AgentGrainTests.cs` |
| AC13 | An old cached PWA that still posts `location` still gets a reply (the unknown member is ignored) | `Chat_OldClientBodyWithLocation_StillBinds` | `AgentApiTests.cs` |

Device-only (state this in the MR, not as a passing test): on iOS Safari and Android Chrome, with location granted,
"where am i" answers; with it revoked in site settings, it answers "not shared" and the Preferences Enable button
returns. The asset tests are source-text heuristics (ADR 0012).

## Quality gate checklist

From `.agentic/quality-gates.yaml` at plan time:
- `coverage_new_code_pct_min: 80`, measured by `node .agentic/scripts/check-new-code-coverage.mjs` over
  `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"`.
- `cognitive_complexity_max: 15`, enforced as a build error by S3776 (`dotnet build Agent42.slnx`).
- `dependencies.vulnerable_packages: 0`. No packages are added by this plan.
- CI also blocks on: build, tests, `dotnet format Agent42.slnx --verify-no-changes`, and doc-path integrity.

## Out of scope

- Client tools under Copilot (needs its own spike; until then Copilot chats have no location).
- Tuning or removing the 5-minute background refresh.
- Server-side staleness re-checks, coordinate rounding, location history, forward geocoding (all ADR 0012
  out-of-scope items stay out).
- A hard approval gate on location (`confirm_action` stays advisory, per ADR 0089).
- Rewriting existing chat histories that contain old `get_current_location` results.

## Review

Critic pass (2026-10-09): no ADR contradiction and no blocker. The four targeted checks all held: the `ToolNames`
consumers, the zero-arg broker path, the other location readers, and the truncation/size caps. Findings were
handled as follows:
- **M1 unpaid doc debt.** Fixed. Step 4 now lists `docs/features/current-location.md`,
  `docs/features/scheduled-reminders.md`, every `_turnLocation` citation in gotchas, the "three client tools"
  comments and ADR 0089 line 17. The sweep exit check now names its exempt history paths.
- **M2 steps without a test gate.** Fixed. The server switch, the transport removal and the server tests are now one
  step. The frontend and asset tests form another. The one cross-step red test (the asset count) is called out.
- **L3 compile-break sites.** Fixed. They are listed in step 1.
- **L4 unpinned transition claim.** Fixed. AC13 and `Chat_OldClientBodyWithLocation_StillBinds`.
- **L5 "never prompts" overstated on WebKit.** Fixed. The claim is softened in Context, AC3, step 2 and the ADR.
- **L6 Copilot picker shows an inert Location checkbox.** Fixed. It is recorded in the ADR, with a descriptor note
  as the option.
- **L7 overlap with `plan-deferred-tool-loading.md`.** Fixed. Step 4 adds a note to that plan.
- **L8 asset tests are text heuristics.** No change; the device-only note already covers it.

## Implementation notes — where it diverged from the plan

Executed 2026-10-09 on `feature/location-as-client-tool`. All four steps landed as planned. These are the differences:

- **`ParseBrowserFix` signature.** It is `private static (UserLocation? Fix, string Status)`, not `string? status`.
  An unusable answer gets the status `"invalid"` and a fix gets `"ok"`, so the log line always names a reason. A
  `ReadFix`, `ReadDouble` and `ReadInstant` trio keeps the parse under S3776.
- **The geocoder counter is process-wide.** `FakeGeocoder.InvalidCalls` counts every out-of-range fix handed to
  any silo's fake. It is asserted to be 0, as an invariant, because a per-test count would race on the shared
  cluster.
- **The `InvalidFix` theory also covers `no_fix` and `timeout`.** They share the "could not get a position"
  wording, so one theory pins all of them.
- **`ChatAt` kept its signature.** It now serializes the fix into the browser's answer shape and goes through a
  new `ChatAnsweringLocation` helper, which answers the flagged call as soon as it streams. So
  `ToolResults_DoNotBlowTheSessionSizeBudget` and the no-geocoder test needed no change beyond that.
- **The `CACHE` bump touched four more tests.** `PwaAssetsTests`, `SearchAssetsTests`, `ResearchAssetsTests` and
  `ClientToolsAssetsTests` pin the cache name, so they moved from `v51` to `v52`. The plan did not list them.
- **Extra doc debt was paid:**
  - `docs/features/streaming-replies.md` had the old `ChatAsync(…, location)` call.
  - ADR 0089 got `amended_by: 0090` in its frontmatter.
  - The CLAUDE.md "Client tools" feature-map row names location and 0090.
  - The client-tool gotcha gained trap (5), on the own-selectable-key point.
- **The DevHost check was done over HTTP, not by clicking in a browser.** A script posted "where am i" with
  `clientTools: ["get_current_location"]`, read the flagged call, and posted a fix (coordinates came back without
  an address) and a `not_shared` (the Preferences text came back). This ran on DevHost. The real
  `navigator.geolocation` path is still device-only, as the plan says.
- **The exit-check grep has one extra hit.** `LocationAssetsTests.cs` contains
  `Assert.DoesNotContain("currentLocationForSend", app)`, which asserts absence. The deferred-tool-loading plan
  keeps its historical citations, under the note added at its top.
