# Plan — Tell the user whether a newly added MCP server actually works

planned-against: main @ b5410c2

## Overview

**Goal:** after adding or editing a remote MCP server (ADR 0024), the user is told — in the UI,
immediately — whether the server was reachable and how many tools it exposes, instead of finding
out indirectly (or not at all) on some later chat.

**Why now:** today `POST/PUT /api/mcp-servers` validates only *structurally* (`ValidateMcpServer`,
`src/Agent42.Backend/Mcp/AgentApi.Mcp.cs:74`) — a typo'd host saves cleanly. The first real
connection happens lazily on the next `AgentGrain` activation inside
`UserMcpToolProvider.ConnectAllAsync`, where a failure is best-effort: a `LogWarning` and an empty
tool group. The only user-visible signal is the picker's `"tools not discovered yet"`
(`src/Agent42.Backend/wwwroot/js/tools.js:125`), which reads identically for *"haven't opened a chat
yet"*, *"server is down"*, *"wrong URL"* and *"OAuth not connected"*. That ambiguity is ADR 0058's own
recorded cold-start consequence; this plan closes it, and closes the cold start with it — a successful
test records the discovered tool names, so the per-tool checkboxes appear without waiting for a chat
activation.

## Affected areas

Verified against the tree at `b5410c2`.

| Path | Change |
|---|---|
| `src/Agent42.Backend/Mcp/McpOAuthTokenResolver.cs` | **new** — extracted from `UserMcpToolProvider` |
| `src/Agent42.Backend/Mcp/McpConnectionTester.cs` | **new** — the one-shot probe |
| `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` | uses the extracted resolver; stale comment corrected (C7) |
| `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` | one new route in the existing `MapMcpEndpoints`; `serverId` on the OAuth return redirect |
| `src/Agent42.Backend/Mcp/McpContracts.cs` | `McpServerTestResponse` |
| `src/Agent42.Backend/Program.cs` | register the tester + resolver (`:184` neighbourhood) |
| `tools/Agent42.DevHost/Program.cs` | register an **offline fake `IMcpConnectionFactory`** (H1) |
| `src/Agent42.Backend/wwwroot/index.html` | `#mcpTestBtn`, `#mcpTestResult` (dialog), `#mcpTabResult` (MCP tab) |
| `src/Agent42.Backend/wwwroot/js/dom.js` | three exports |
| `src/Agent42.Backend/wwwroot/js/mcp.js` | test call + shared renderer; save-flow change |
| `src/Agent42.Backend/wwwroot/styles.css` | success modifier beside `.agent-form__note--error` (`:1640`) |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` bump — **read the current value at execution time (M2)** |
| `.agentic/decisions/0064-*.md` | **new ADR — 0064, NOT 0063 (C1)** |
| `.agentic/decisions/0024,0026,0058` | amendment back-reference banners (M1) |
| `CLAUDE.md`, `.agentic/gotchas.md` | doc debt paid in the same MR (M1 names the exact claims) |
| `tests/Agent42.Tests/*` | see the AC table |

`infra/` is **not** touched — `excludedPaths` is unchanged. `ProjectLayoutTests` needs nothing: no new
folder, no new `AgentApi.*` partial, no new frontend module.

## Context & constraints

Judgment and findings only. Code facts are pointed at, not restated. **Cite by quoted content, not by
line number, when editing** — the numbers below drift.

**C1. Take ADR number 0064, not 0063.** `.agentic/decisions/` ends at 0062, so 0063 *looks* free — it is
not. `.agentic/specs/plan-calendar-mcp-server-extraction.md` reserves it in nine places
(`:26,141,306,307,343,358,462,682,687` — `:307` is the `superseded_by: 0063` edit to ADR 0053). This ADR
is **0064**.

**C2. The property being amended, and why the amendment is sound.** ADR 0058 (`:67-74`) established that
`GET /api/tools` never calls `IUserMcpToolProvider`, for two measured reasons: `ConnectAllAsync` is a
sequential `foreach` with a 10 s timeout per server behind a per-user semaphore (three dead servers ⇒
~30 s before the picker is usable), and resolving an InteractiveOAuth token *refreshes and saves it*, so
the GET would become a **write** that bumps the version and invalidates the socket cache on a page load.
Neither objection survives the move to an **explicit, user-pressed POST on one named server**: the latency
is asked for, and a write on a POST is unremarkable. State this plainly in the ADR — the rule was never
"never connect", it was "never connect *on a page load, for every server, implicitly*". **Do not weaken
the `GET /api/tools` rule while doing so.** What 0024/0026 give up is narrower and must be named exactly:
the management endpoints were previously pure config CRUD that never opened a socket; one of them now does,
on explicit request.

**C3. `IMcpConnectionFactory` is registered in the Backend only** (`src/Agent42.Backend/Program.cs:184`;
DevHost registers `SingleUserMcpToolProvider` at `:66` and no connection factory). So the endpoint must
resolve the tester **optionally via `IServiceProvider`** — the `/api/speech` pattern
(`src/Agent42.Backend/Speech/AgentApi.Speech.cs:24`) — and return a curated 503 when it is absent. Taking
the tester as a minimal-API *parameter* would break routing for every host that cannot resolve it; that is
the recorded route-matcher trap (`.agentic/gotchas.md:1400-1405`).

**C4. Reuse the token resolver; do not re-implement it.** `UserMcpToolProvider.ResolveOAuthAccessTokenAsync`
(`:191`) refreshes an InteractiveOAuth token within a 2-minute skew of expiry and persists the refreshed
bundle. A test path that skipped the refresh would report **failure on a server the chat path connects to
fine** — a false negative in a diagnostic tool, the worst defect this feature could ship. Extract and share
(Step 1). Accepted consequence, to be recorded in the ADR: pressing Test on an OAuth server with a
near-expiry token **bumps the overlay version and invalidates that user's warm MCP socket cache** — bounded,
rare, deliberate, and exactly what the next activation would have done anyway.

**C5. The probe must dispose its connection and must never enter the version cache.**
`IMcpConnectionFactory.ConnectAsync` hands back an `IAsyncDisposable` alongside the tools
(`McpConnectionFactory.cs`, `McpServerConnection`). A test is not an activation: dispose in a `finally`.
Never call `IUserMcpToolProvider` from this path — that would populate the socket cache from a management
action and defeat C2's argument. This half is guarded **structurally**, not at runtime (M6).

**C6. Curated failure text only — but log the detail.** The MCP server's response body is
response-controlled. Follow `ConfigHealth`'s discipline (`Configuration/ConfigHealth.cs:176-184` — the catch
discards the exception and emits a fixed string) and `CompleteMcpOAuthAsync`'s (`token_exchange_failed`): a
fixed reason to the browser, `LogWarning(ex, …)` server-side. This adds no exposure —
`UserMcpToolProvider` already logs the same detail on every failed connect, so `/logs` (ADR 0054) is
unchanged.

**C7. Two writers of the picker's name list — and one comment becomes false.** `UserMcpToolProvider`
carries the comment *"A SUCCESSFUL connect is the only place names are learned."* (locate by that string,
not by line). After this change it is wrong; fix it in the same MR (hard rule 2) and add the gotcha: **two
writers now feed the picker's names and they must agree** — the one-source invariant
(`.agentic/gotchas.md:1271-1340`) is what stops the picker minting per-tool keys with no checkbox. Use
`RecordToolNamesAsync`, never `SaveAsync`; it deliberately does not bump the version
(`src/Agent42.Agents/Mcp/IUserMcpConfigGrain.cs:43-57`).

**C8. NEVER record an empty tool list.** *(Found in review — this would have shipped a real bug.)*
`UserMcpConfigGrain.RecordToolNamesAsync` no-ops only when the list is **unchanged**
(`current.SequenceEqual(toolNames, StringComparer.Ordinal)`, `UserMcpConfigGrain.cs:~93`). `[]` differs
from `[A,B]`, so recording an empty list **erases** the names. ADR 0058 (`:105-107`) rests on two
invariants — *"names are never erased"* and *"per-tool keys cannot exist before names do"* — and erasing
makes its unreachable state reachable: `tools.js:122-128` then renders the server as a bare checkbox
reading "tools not discovered yet" with the master **unchecked** (`isChecked`, `tools.js:75-78`) while the
client preservation rule keeps the stored `mcp:{id}:{tool}` — a server that reads OFF while still granting
a tool, one tick from silently re-granting a deselected one. **So: record only a non-empty list.** A
successful connect returning zero tools is reported to the user and writes nothing.
*Observed, deliberately not fixed here:* `UserMcpToolProvider` has the identical hole on its own path (it
records whatever it discovered, including `[]`). It is pre-existing and fires only on an activation;
fixing it is a behaviour change to the chat path and belongs in its own MR. Record the observation in ADR
0064 rather than silently widening scope. Guarding inside the grain was considered — it is the true
one-source fix — and rejected for the same reason.

**C9. The duplicate-create trap in the new save flow.** `POST /api/mcp-servers` **always inserts** — the id
is server-minted (`AgentApi.Mcp.cs:22`). This plan keeps the dialog **open** after a successful save, so on
a successful *create* `editingMcpId` **must** be set from the response id, flipping the dialog into edit
mode. Miss it and a second Save silently creates a duplicate. `saveMcpServer` currently discards the
success body; `Mask` does return `Id`. No server-side guard exists — this is the client's responsibility.

**C10. User's scope decisions (interview, this session).**
- **No per-row test button.** Reached from the dialog, and automatically after a successful save.
- **The result is ephemeral.** No new persisted fields on `StoredMcpServer`. Rejected because a stale "ok"
  from three days ago reads as "ok now", and because it would add trailing `[Id]`s to an Orleans-persisted
  record for cosmetic state.
- **The OAuth return auto-tests** — forcing Step 5 (the return redirect must carry which server connected).

**C11. Two render targets, one renderer.** The dialog has its own result line; the OAuth return has no
dialog open, so the MCP *tab* needs one. Write the renderer once, pass it the target. Do **not** solve this
by opening the edit dialog on the OAuth return — landing a user in an edit form after signing in is a
non-sequitur.

**C12. Watch the cognitive-complexity gate.** S3776 is an `error` at 15. Put the probe logic in
`McpConnectionTester`, not in a fat lambda inside `MapMcpEndpoints` — the
`DocumentIndexRegistration`/`HindsightRegistration` precedent.

## Steps

**Step 1 — Extract the OAuth token resolver.** Move `ResolveOAuthAccessTokenAsync` out of
`UserMcpToolProvider` (`:191`) into `McpOAuthTokenResolver` (`src/Agent42.Backend/Mcp/`), taking
`IGrainFactory`, `IMcpSecretProtector`, `McpOAuthClient`, `ILogger`. `UserMcpToolProvider` takes it by
constructor and delegates. Behaviour must not change (C4). **This changes the ctor**, so
`UserMcpToolProviderTests`' `NewProvider()` helper and its two call sites need updating — the *behaviour*
is unchanged, the test file is not untouched (M5).
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserMcpToolProviderTests` — all green.

**Step 2 — The tester.** New `McpConnectionTester`: loads the user's overlay, finds the server by id
(**null result ⇒ the endpoint 404s**), resolves the OAuth token via Step 1, decrypts secrets, connects with
a 10 s linked-CTS bound (mirroring `UserMcpToolProvider.ConnectTimeout`, with a comment saying so), lists
tools, **records the names only when non-empty (C8)**, disposes the connection in a `finally` (C5), and
returns an outcome. Curated reasons only (C6), distinguishing at minimum: `ok` ·
`connected, but the server exposes no tools` · `not connected (no OAuth token yet)` · `could not connect`.
A disabled server is still tested and the outcome says it is off.
*Verify:* new `McpConnectionTesterTests` with a fake `IMcpConnectionFactory` — reuse the shape at
`tests/Agent42.Tests/UserMcpToolProviderTests.cs:311-335` (`TrackingDisposable` at `:336` proves disposal).

**Step 3 — Contract + endpoint.** Add `McpServerTestResponse(bool Ok, int ToolCount,
IReadOnlyList<string> ToolNames, string Reason)` to `McpContracts.cs`. Add `POST /api/mcp-servers/{id}/test`
to the existing `MapMcpEndpoints` — tester resolved optionally via `IServiceProvider` (C3): **503** when
absent, **404** for unknown/foreign id, **200 with `ok:false`** for a reachable-but-failing test (the request
succeeded; the diagnosis is the payload — the config-health convention). Keep the lambda thin (C12). No new
partial, no new endpoint group (`Api/AgentApi.cs:38` already calls `MapMcpEndpoints`).
*Verify:* new `McpConnectionTestApiTests` — see Step 7.

**Step 4 — Registration, both hosts.** Register the tester + resolver in `src/Agent42.Backend/Program.cs`
beside `IMcpConnectionFactory` (`:184`). **And register an offline fake `IMcpConnectionFactory` in
`tools/Agent42.DevHost/Program.cs`** returning a couple of canned tools (H1) — the seam is documented as
existing for exactly this, DevHost already seeds MCP servers into the config grain (`:215-232`), and without
it *no leg of this feature is exercisable offline*. Note the honest residual: the fake proves the wiring,
not a real MCP handshake — the real `McpConnectionFactory` wire path stays production-verified, as it has
been since ADR 0024.
*Verify:* `dotnet run --project tools/Agent42.DevHost` boots; Preferences → MCP servers → Test returns a
tool list rather than a 503.

**Step 5 — OAuth return carries the server id.** In `CompleteMcpOAuthAsync`, change the success redirect to
`/?mcp_oauth=connected&serverId={id}`. Failure redirects untouched (they carry only curated reasons).
Parallelizable with Steps 1–4.
*Verify:* AC13 — an assertion on `serverId` in the redirect; the existing
`Assert.Contains("mcp_oauth=connected", …)` (`McpOAuthEndpointTests.cs:92`) passes either way and proves
nothing here (M4).

**Step 6 — Frontend.** `index.html`: `#mcpTestBtn` in `.agent-form__actions` beside `#mcpSaveBtn`
(`:687-688`) — **`type="button"`**, or it submits the form and silently saves (L2); `#mcpTestResult` beside
`#mcpError` (`:684`); `#mcpTabResult` in the MCP tab, **outside `#mcpServerList`** — `renderMcpServers()`
clears that list's `innerHTML` and `openProfileDialog` calls `loadMcpServers()` un-awaited, so a result
rendered inside it can be wiped by the in-flight render (L3). `dom.js`: three exports in the existing
pattern (`:139-160`). `styles.css`: success modifier beside `.agent-form__note--error` (`:1640`). `mcp.js`:
- one `renderTestResult(target, outcome)` (C11), and **clear both result elements in `openMcpDialog`**
  alongside the existing `mcpError` reset, or server A's result renders under server B's form (L1);
- `testMcpServer(id, target)` calling the endpoint;
- `saveMcpServer()` — on success set `editingMcpId` from the response (**C9**), keep the dialog open,
  relabel Cancel → Close, `loadMcpServers()`, **keep the existing unconditional `refreshToolCatalog()`
  call** (M3 — a save whose auto-test then fails must still update the pickers, which is what that line's
  ADR 0026 comment guarantees), then auto-test into `#mcpTestResult`;
- Test button enabled only when `editingMcpId` is set (an unsaved server has no id);
- `initMcpOAuthReturn()` — read `serverId`, auto-test into `#mcpTabResult`;
- an **additional** `refreshToolCatalog()` after a successful test, so newly recorded names become
  checkboxes. Note this is a no-op for a **disabled** server: `GET /api/tools` emits a descriptor only per
  management-enabled server (L4).
Bump `sw.js` `CACHE` — **read its current value first (M2)**: this plan and
`plan-calendar-mcp-server-extraction.md:61,327` both claim `v27 → v28`, and both edit the same literal at
`PwaAssetsTests.cs:255`. Whichever lands second takes the next number.
*Verify:* `McpServersAssetsTests` + `PwaAssetsTests`, then a DevHost pass (headless Chrome over CDP, the
ADR 0058/0052 precedent) — now meaningful because of Step 4.

**Step 7 — Tests.** New `McpConnectionTestApiTests` following **`SpeechApiTests`' exact shape**: a
`SharedClusterTests(fixture)` class with a private `HostAsync(McpConnectionTester?)` that builds a
`WebApplication` per test and registers the tester only on the configured path (`SpeechApiTests.cs:21-56`).
That is what lets AC7 (404) and AC8 (503) coexist **without a second Orleans cluster** (H2). Add
`[InlineData("/api/mcp-servers/test-id/test")]` to
`EasyAuthExclusionTests.Exclusions_DoNotCoverAnyAuthenticatedPath` (`:127`). Extend `McpServersAssetsTests`
with the new ids and `PwaAssetsTests` with the new cache version.

**Step 8 — ADR 0064 + doc debt.** Write `.agentic/decisions/0064-*.md` (C1, C2, C4's accepted consequence,
C8's observation, and L5: record *why this is a separate endpoint rather than a `ConfigHealth.ProbeAsync`
row* — that opt-in "Run live checks" path is this repo's existing home for user-triggered reachability
probes and MCP is conspicuously absent from it). Add the amendment **back-reference banners to ADRs 0024,
0026 and 0058** in the same commit — the mechanical repo convention (`0021:10,16`, `0024:10`, `0013:10,18`).
Then fix the three now-stale cold-start claims (M1): `.agentic/gotchas.md:1419-1420`, ADR
`0058:155-158`, and CLAUDE.md's ADR 0058 bullet ("*a newly added server has no per-tool checkboxes until
that user's next chat activation*"). Correct the false comment in `UserMcpToolProvider` (C7), add the
two-writers + never-record-empty gotcha (C7/C8), update CLAUDE.md's MCP bullets. Move this spec to
`.agentic/specs/completed/`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A reachable server returns `ok`, its tool count and names | `Test_ReportsToolsForAReachableServer` | `McpConnectionTesterTests.cs` (new) |
| AC2 | An unreachable server returns `ok:false`, a curated reason, and **no exception text** | `Test_ReturnsACuratedReason_NeverTheException` | `McpConnectionTesterTests.cs` |
| AC3 | A successful test records the discovered names | `Test_RecordsDiscoveredToolNames` | `McpConnectionTesterTests.cs` |
| AC4 | **A zero-tool connect records NOTHING — previously known names survive (C8)** | `Test_NeverErasesKnownToolNamesOnAZeroToolConnect` | `McpConnectionTesterTests.cs` |
| AC5 | The test disposes its connection | `Test_DisposesItsConnection` (`TrackingDisposable`) | `McpConnectionTesterTests.cs` |
| AC6 | The tester never depends on `IUserMcpToolProvider` (C5) — **structural, not runtime** (M6) | `Tester_DoesNotDependOnTheToolProvider` (reflection over ctor params) | `McpConnectionTesterTests.cs` |
| AC7 | An OAuth server with no token reports "not connected", not a failure | `Test_DistinguishesNotConnectedFromUnreachable` | `McpConnectionTesterTests.cs` |
| AC8 | A near-expiry OAuth token is refreshed, so Test never false-negatives (C4) | `Test_RefreshesANearExpiryToken` | `McpConnectionTesterTests.cs` |
| AC9 | Unknown id ⇒ 404; another user's id ⇒ 404 | `Test_Returns404ForAnUnknownOrForeignServer` | `McpConnectionTestApiTests.cs` (new, tester registered) |
| AC10 | The endpoint 503s (curated) when the tester is unregistered | `Test_Returns503WhenUnconfigured` | `McpConnectionTestApiTests.cs` (tester omitted) |
| AC11 | `/api/mcp-servers/{id}/test` is NOT Easy-Auth excluded | `Exclusions_DoNotCoverAnyAuthenticatedPath` (new row) | `EasyAuthExclusionTests.cs` |
| AC12 | Dialog carries the test button + result ids; `mcp.js` calls the endpoint | extend existing assertions | `McpServersAssetsTests.cs` |
| AC13 | The OAuth success redirect carries `serverId` (M4) | `Callback_RedirectCarriesTheConnectedServerId` | `McpOAuthEndpointTests.cs` |
| AC14 | Shell cache bumped | existing cache assertion | `PwaAssetsTests.cs` |
| AC15 | `UserMcpToolProvider` still resolves and refreshes tokens identically after the extraction | existing suite (**edited for the new ctor**, M5) | `UserMcpToolProviderTests.cs` |

**Two client rules have no runtime test** and are guarded by source text only, as ADR 0058's
collapse/preservation rules were: the **duplicate-create trap (C9)** and the **unconditional save-path
`refreshToolCatalog()` (M3)**. Pin both with source-text assertions and **mutation-check them** (remove the
line, confirm the assertion fails) — the ADR 0058 precedent.

## Quality gates

Copied from `.agentic/quality-gates.yaml` at plan time — do not recall these.

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776, at build (blocks) |
| `vulnerable_packages` | 0 | CI package-freshness job (hard block) |
| `outdated_packages` | report | CI (soft) |

Branch policy: `branches.protected: [main]` — needs a `feature/*` branch and an MR.

## Out of scope

- **No per-row test action, and no persisted test status** (C10) — both considered and declined.
- **No test-before-save.** The endpoint takes a stored id because a blank secret means "keep", which cannot
  be resolved from an unsaved form.
- **No periodic/background health checking.** User-pressed only.
- **No change to `GET /api/tools`** — it still never connects (C2).
- **No fix to `UserMcpToolProvider`'s identical empty-list erasure** (C8) — pre-existing, activation-only,
  its own MR.
- **No change to `excludedPaths`, Bicep, secrets, or any Cosmos container.**
- **No change to the best-effort activation path.** A dead server is still silently skipped at chat time;
  this adds a way to *ask*, it does not make activation loud.

## Review

A `critic` subagent reviewed this plan against the ADRs, gotchas and the actual tree. Verdict: **not a
clean pass** — 3 blocking, 6 substantive, 6 low. All 15 were accepted and folded in; **none rejected**.

| Finding | Resolution |
|---|---|
| **H1** DevHost verify impossible (no connection factory offline), no residual recorded | Step 4 now registers an offline fake in DevHost; residual recorded |
| **H2** AC7+AC8 cannot coexist in `McpServersApiTests` | Step 7 adopts `SpeechApiTests`' per-test-host-over-shared-cluster shape; no second cluster |
| **H3** Recording `[]` erases `KnownToolNames`, re-opening ADR 0058's "unreachable" state | **C8** — record only non-empty; AC4 added; provider's identical hole recorded as out-of-scope |
| **M1** `amends` needs back-reference banners + three stale cold-start claims | Step 8 names all six edits |
| **M2** `sw.js` v28 collides with the calendar plan | Step 6 defers the number to execution time |
| **M3** Save-path `refreshToolCatalog()` could be read as *moved*, regressing ADR 0026 | Step 6 states it stays unconditional; mutation-checked |
| **M4** Step 5 unverifiable | AC13 added |
| **M5** AC12/Step 1 contradiction on whether the test file changes | Step 1 + AC15 now say the ctor changes |
| **M6** AC4's "adds nothing to the socket cache" unobservable | Split into AC5 (runtime) + AC6 (structural) |
| **L1–L6** stale result lines · `type="button"` · `#mcpTabResult` placement · disabled-server no-op · missing ADR 0029 rejected-alternative · citation drift | All folded into Steps 6/8 and the citations corrected; cite-by-content rule added |

The critic separately confirmed as sound: C1's 0064 (finding a ninth reservation this plan had missed),
C2's core argument, C3, C9's trap, the gate thresholds, the unchanged `excludedPaths`, and that no
named-`HttpClient` retry trap applies.

---

## Execution notes — where the implementation diverged from the plan

Executed 2026-09-04 on `feature/mcp-test-connection`, against `main @ b5410c2` — **exactly** the spec's
`planned-against`, so the drift check was a no-op.

**The plan was followed as written.** All 15 acceptance criteria are implemented and green, no step was
skipped, nothing in *Out of scope* was pulled in. Six differences are worth recording:

1. **A second `sw.js` cache assertion the plan did not name.** M2 correctly anticipated a collision on the
   version *number* and told the executor to read the current value at execution time (v27 → **v28**). What it
   missed is that **two** test files pin that literal: `PwaAssetsTests.cs:255` (which the plan named) and
   `SearchAssetsTests.ServiceWorker_ListsTheSearchModule_AndBumpsTheCache` (ADR 0061), which additionally
   asserts `DoesNotContain` on the *previous* version. It was found by the full suite, not by inspection —
   the first full run failed 1/1163 on exactly that. Both were updated; the ADR 0061 one gained a comment
   saying the version moves on with every MR that touches `wwwroot/js/`, so what that test owns is
   `search.js` being in `SHELL`, not a particular number.

2. **Three curated reasons shipped, not four.** The plan's minimum set was `ok` · `connected, but the server
   exposes no tools` · `not connected (no OAuth token yet)` · `could not connect`. All four exist, plus two
   the plan implied but did not enumerate: `ok, but this server is switched off` (Step 2 required a disabled
   server to be tested and to say so — a bare `ok` would read as "and it is in use") and
   `auth mode {x} is not yet supported`, mirroring the provider's own skip branch.

3. **`McpTestOutcome` is an internal record, distinct from the wire contract.** The plan specified
   `McpServerTestResponse(bool Ok, int ToolCount, IReadOnlyList<string> ToolNames, string Reason)` and that
   ships unchanged. The tester returns its own `McpTestOutcome(Ok, ToolNames, Reason)` — `ToolCount` is
   derived at the endpoint — so the probe does not depend on a wire shape and `null` cleanly means "no such
   server" without an ambiguous sentinel on the response type.

4. **The C7 comment fix landed in Step 1, not Step 8.** The false *"A SUCCESSFUL connect is the only place
   names are learned"* comment was corrected while the file was already open for the resolver extraction,
   rather than being deferred to the documentation step. Same MR either way (hard rule 2).

5. **Two small client details the plan did not specify.** Cancel is relabelled to **Close** after a
   successful save (the save already happened, so "Cancel" would misdescribe the button) and reset to
   "Cancel" in `openMcpDialog`; and the Test button carries a title explaining *why* it is disabled on an
   unsaved server ("Save the server first").

6. **The CRLF trap, worth knowing for any future execution.** Patching files with a Python
   read-then-write round trip silently converts the repo's CRLF endings to LF, which `dotnet test` does not
   notice but `dotnet format --verify-no-changes` fails on with ~11 opaque `WHITESPACE` errors pointing at
   *unrelated* lines of `Program.cs`. Every touched file was normalized back to CRLF before the lint passed.

### Verification actually performed

| What | Result |
|---|---|
| `dotnet build Agent42.slnx` | 0 errors — so S3776 (cognitive complexity, `error` at 15) passes |
| `dotnet test Agent42.slnx` | **1163 passed, 0 failed** |
| `dotnet format --verify-no-changes` | clean |
| `node .agentic/scripts/check-doc-paths.mjs` | all referenced paths exist |
| `node .agentic/scripts/check-new-code-coverage.mjs` (`coverage_new_code_pct_min`, threshold 80) | **84.6%** — 115/136 new lines covered — PASS |
| `dotnet list package --vulnerable --include-transitive` (`vulnerable_packages`, threshold 0) | **0** across all six projects — PASS |
| DevHost endpoint legs (curl) | 200 ok+2 tools · 200 `ok:false` "could not connect" · 404 unknown id |
| Cold-start closure (measured) | tested server → `mcp:{id}` + `:demo_search` + `:demo_delete`; untested control → `mcp:{id}` only |
| DevHost browser leg (headless Chrome/CDP) | **13/13** — incl. the C9 duplicate defence live (6 servers → 6 after a second Save) and `"✗ could not connect"` with no exception text |
| Mutation checks (the two source-text-only rules) | both fail when the guarded line is removed/moved, pass when restored |

### Critic findings, and what changed because of them

A `critic` subagent reviewed the diff before the MR. Verdict: **not a clean pass** — 1 substantive, 2 medium,
4 low. All were addressed; **one was a real defect, not a wording problem**:

1. **The "no new egress" claim was FALSE for a disabled server — and the auto-test dialled one with no
   press.** `UserMcpToolProvider` connects only to enabled servers (`Where(s => s.Enabled)`), so a disabled
   server's decrypted credentials have never reached its endpoint. Testing one on an explicit press is
   intended (the plan required it) — but the save-path auto-test ran unconditionally, so saving a server with
   the Enabled box unchecked would have delivered its credentials to a host the app had never contacted, from
   a *save*. **Fixed in code**, not just in prose: the auto-test is now gated on the checkbox and a disabled
   save renders "not tested — this server is switched off" with zero network calls. ADR 0064 and CLAUDE.md
   now state the enabled/disabled distinction exactly, and `SaveFlow_DoesNotAutoTestADisabledServer` pins it.
2. **`extends: [0025, 0049]` in ADR 0064's frontmatter was wrong** — 0049 (uploaded skills) is unrelated and
   appears nowhere in the body; the ADR actually discussed at length is **0029** (config-health `ProbeAsync`,
   the rejected home, whose status-code convention this adopts). Corrected to `extends: [0025, 0029]`.
3. **The `sw.js` version collision was resolved in only one direction.** M2 predicted it and this plan took
   v28, but nothing was written back into the still-active `plan-calendar-mcp-server-extraction.md`, which
   still said "bump v27 to v28" — now a **no-op that ships a stale shell** — and still pointed at
   `styles.css:1644-1648`, which this diff turned into ADR 0064's new `.agent-form__note--ok` rule. Both
   references fixed in that spec (hard rule 2), and it is now told to locate the block by content.
4. **ADR 0064 under-enumerated the curated reasons** (four listed, six emitted) and did not note that one
   interpolates the user's own stored `Auth` string. Both corrected; the closed set is now stated as six.
5. **The coverage gate was neither reported nor marked SKIPPED** in the table above. It had been run; it is
   now recorded (84.6%), along with the vulnerable-packages gate.
6. **`#mcpTabResult` was never cleared on entering Preferences**, so an OAuth-return verdict outlived a
   close/reopen and even the deletion of the server it described. Now cleared by an exported
   `clearMcpTabResult()`, called **synchronously** at the top of `openProfileDialog` (which is why it cannot
   race the un-awaited `loadMcpServers()`) and on delete.
7. **The gotchas size guardrail** (~≤40 entries; the file is at 96 on `main`) — pre-existing drift, not
   created here. Responded to by **consolidating this MR's two entries into one**, trimming the parts that
   merely restated ADR 0064 prose. The full audit the guardrail asks for is out of scope for this MR.

The critic separately confirmed as clean: ADR 0058's `GET /api/tools` rule is genuinely unweakened; the
two-writers invariant actually holds (the provider records names *after* `WrapForTruncation`, and
`TruncatingMcpTool` forwards `Name`, so both writers emit identical strings); registration symmetry across
hosts; the DevHost fake's consistency with the seeded servers; and that no named-`HttpClient` retry trap
applies.

### Residuals carried into ADR 0064

- DevHost's `IMcpConnectionFactory` is an **offline fake**: it proves the wiring, not an MCP handshake. The
  real factory's wire path stays production-verified, as it has been since ADR 0024.
- The **InteractiveOAuth auto-test round trip** is covered server-side (`serverId` on the redirect, asserted)
  and client-side by source text; the full flow needs a real authorization server and was not run.
- `UserMcpToolProvider`'s **identical empty-list erasure** is left unfixed, deliberately and per the plan's
  own *Out of scope* — pre-existing, activation-only, its own MR.
