# Plan — Replay a turn's tool calls when a chat is reloaded from history

`planned-against: main @ 0f97d08`

## Overview

Today a turn's tool chips vanish the moment you reload or reopen a chat: `renderTurns` never replays
them, so the folded tool section ADR 0072 built exists only for the turn being streamed. This plan makes
a reloaded chat render the **same** section the finished live turn showed — grouped and collapsed above
the threshold, flat below it — by projecting the tool calls that are **already in the persisted session**
into `ChatTurn`.

Why now: the fold (ADR 0072) is what makes replay affordable. Before it, replaying five chips would have
pushed every historical reply below the fold; with it, a long chain costs one 25 px line. The data has
been sitting in the session all along — it is what the model reads back every turn — so this is a
projection + render change, **not** a migration.

## Affected areas (verified against the tree @ 0f97d08)

| File | Change |
|------|--------|
| `src/Agent42.Agents/Conversations/ChatToolCall.cs` | **New.** The per-call DTO, mirroring `AttachmentRef`'s Orleans-serializable shape. |
| `src/Agent42.Agents/Conversations/ChatTurn.cs` | Trailing `[property: Id(3)] IReadOnlyList<ChatToolCall>? ToolCalls = null`. |
| `src/Agent42.Agents/Conversations/AgentGrain.cs` | `GetHistoryAsync` (:1195) harvests `FunctionCallContent`/`FunctionResultContent` and attaches them to the assistant turn they precede. |
| `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs` (:61) | Strip `ToolCalls` before the public response. |
| `src/Agent42.Backend/wwwroot/js/thread.js` | **New export** `replayToolCalls(msg, calls)` — the one replay path, with de-dupe. |
| `src/Agent42.Backend/wwwroot/js/conversations.js` (:80) | `renderTurns` calls it (a reopened chat). |
| `src/Agent42.Backend/wwwroot/js/send.js` (:51) | `recoverPersistedReply` calls it (a turn whose stream died, ADR 0046). |
| `src/Agent42.Backend/wwwroot/sw.js` (:5) | `CACHE` `agent42-shell-v40` → `-v41` (ADR 0041). |
| `tests/Agent42.Tests/PwaAssetsTests.cs` (:255) | Pinned cache literal. |
| `tests/Agent42.Tests/SearchAssetsTests.cs` (:127) | **Second** pinned cache literal — easy to miss. |
| `tests/Agent42.Tests/AgentGrainTests.cs` | New history-projection tests. |
| `tests/Agent42.Tests/ChatSearchIndexingTests.cs` | Ordinal-stability regression. |
| `tests/Agent42.Tests/AgentApiTests.cs` | Share-link non-disclosure test. |
| `tests/Agent42.Tests/ToolCallFoldAssetsTests.cs` | Replay-wiring source-text guards. |
| `.agentic/decisions/0080-*.md` | New ADR — `amends: [0072, 0035, 0046]`, `extends: [0041]`. |
| `.agentic/decisions/0072-*.md`, `0046-*.md` | Now-false claims corrected in this MR (hard rule 2) — list in step 6. |

**No `infra/` change, no new endpoint, no new secret, no Cosmos change, no new JS module, no CSS change.**
If execution finds one is genuinely needed, that is drift from this plan — stop and flag it.

## Context & constraints (what the executor can't cheaply re-derive)

**There is no migration surface, because `ChatTurn` is not grain state.** It appears in no `*State.cs`
file — it is a pure projection DTO built fresh from the session on every call. So the "new persisted
field needs a Cosmos JSON round-trip proof" rule does **not** apply here, and `GrainStorageSerializerTests`
is the wrong test to reach for. The trailing `[property: Id(3)]` with a `= null` default is still required
because the record crosses the **grain call boundary** (Orleans wire form). The thing that actually
persists — `FunctionCallContent`/`FunctionResultContent` in `SerializedSession` — has been persisted since
the feature shipped; this plan only stops discarding it on the way out.

**The hard constraint: the projected list's LENGTH is load-bearing for TWO features.** `ProjectForSearch`
(`AgentGrain.Search.cs:282`) numbers turns by their **position** in `GetHistoryAsync()`'s list, and that
ordinal is half of the Cosmos document id `{conversationId}:{ordinal}` (ADR 0061); get it wrong and nothing
throws — the live path and `ReindexAsync` silently diverge by one and double-index forever
(`.agentic/gotchas.md:3031`). **And** `recoverPersistedReply` decides that a turn is recoverable with
`turns.length >= priorTurns + 2` (`send.js:72-73`), so a turn count that shifted would break stream
recovery too. Therefore: **tool calls attach to an existing assistant turn and must never add, remove, or
reorder a turn.** A tool-only assistant message is blank-text and already projects to `null`
(`ProjectAssistantTurn`, :1247) — keep it that way.

**There are TWO frontend painters of a `ChatTurn`, not one.** This is the thing most likely to be missed:
- `renderTurns` (`conversations.js:80`) — a reopened chat, and the offline-cache replay.
- `recoverPersistedReply` (`send.js:51-84`) — a turn whose stream died (ADR 0046). It re-fetches the
  same `/api/conversations/{id}/messages` and re-paints `text` + `attachments` into the **already
  rendered** `agentMsg`.

ADR 0046 explicitly justifies the recovered turn's missing chips by equivalence with a reload
(`0046:82-84`: *"tool-call rows … that arrived after the disconnect … do not come back. **That is exactly
what a reload gives**"*). Once a reload replays everything, that equivalence is gone unless recovery
replays too — so **both painters go through one shared helper**, and the ADR's consequence line gets
corrected. This is why `replayToolCalls` lives in `thread.js` rather than being inlined in `renderTurns`.

**The de-dupe is not optional, and a naive loop is wrong in two different ways.** The recovered bubble may
already hold chips that arrived *before* the disconnect. `addToolCall` appends unconditionally
(`thread.js:269-290`), so re-adding duplicates them; and `completeToolCall` appends a result block
unconditionally too, so re-completing an already-finished chip gives it **two** result bodies. The helper
must branch on all three states — absent, present-but-running, present-and-done — which is also what lets
recovery *heal* a chip the disconnect left pulsing (a strict improvement on today's behaviour):

```js
for (const call of calls ?? []) {
  const existing = call.toolCallId
    ? msg.querySelector(`.tool-call[data-call-id="${CSS.escape(call.toolCallId)}"]`)
    : null;
  if (existing?.classList.contains("is-done")) continue; // already painted live, in full
  if (!existing) addToolCall(msg, call);
  completeToolCall(msg, call); // ALWAYS, never conditional on toolResult — see below
  }
finishToolCalls(msg);
```

**Why `completeToolCall` runs for every call, unconditionally.** A call whose result never came back (a
turn stopped mid-chain, ADR 0078) would otherwise replay as a chip stuck `is-running`, pulsing forever in
a finished transcript — `finishToolCalls` clears the *group's* lamp but not the chip's. Passing a call
with a null `toolResult` through `completeToolCall` marks it done and simply appends no result body.

**The message shape to walk.** Assistant messages are **not** 1:1 with turns — a tool turn emits a
standalone `FunctionCallContent` message plus a separate text message (stated at
`ImageGeneration/AgentGrain.ImageTurn.cs:23-26`). The session sequence for a tool turn is
`[User] → [Assistant: FunctionCallContent×N] → [Tool: FunctionResultContent×N] → [Assistant: text]`,
possibly repeating before the text arrives. The projection therefore harvests calls/results into a
pending list **as it walks**, and flushes that list onto the next assistant turn it actually projects.
Harvest **before** projecting each message, so a model that emits text and a call in one message gets its
own chips (which is what the live renderer does — chips go above the text of that same bubble).

**Two deliberate drops, both worth a comment, neither worth code.** (1) Calls still pending at a **user**
turn boundary are discarded: their reply never materialised, and chips have no bubble to live in without
one. (2) A call whose result never came back replays without a result body, per above. ADR 0068 retention
is safe here without special handling: `RemoveLeadingTurns` (`AgentGrain.Retention.cs:205-218`) always
leaves a prefix starting at a user message, so no orphan `FunctionResultContent` can flush onto a later
turn.

**Why the share-link strip has to be server-side.** The public viewer (`wwwroot/shared/viewer.js:114`) is a
standalone IIFE with its own `renderTurns` that reads only `role`/`text`/`attachments` — so it would
ignore a `toolCalls` field without being told to. That is **not** the same as not disclosing it: the field
would still be in the JSON at `/api/share/{token}`, one DevTools tab away. `SharedConversationResponse`
(`Sharing/ShareContracts.cs:15`) reuses `ChatTurn` verbatim, so the endpoint must map
`turn with { ToolCalls = null }` (valid — positional record properties are `init`-settable).
`[JsonIgnore]` is the wrong tool here (unlike `AttachmentRef.BlobPath`) — it would hide the field from the
owner's `/messages` response too. `/api/share/*` is the only Easy-Auth-excluded surface carrying
`ChatTurn`; ADR 0070's `AgentMcpRunner` and ADR 0022's `/api/agent-run` never call `GetHistoryAsync`.

**ADR 0035 says a share link exposes "full current history — text, images, and anything it contains"**
(`0035:109`). After this change that is no longer quite true: tool calls are the first `ChatTurn` content
deliberately withheld. Tool arguments and results carry search queries, fetched URLs, and MCP/calendar
payloads; widening a public link to include them is an ADR 0013 egress extension the user explicitly
declined.

**Offline replay comes free, and so does a slightly fatter mirror (ADR 0032).** `renderTurns` has two
callers — the network path (`conversations.js:48`) and the offline-cache path (`:73-74`) — and `cachePut`
mirrors the turn array verbatim (`:49`, `send.js:79`), so a cached chat replays its chips with no extra
work. The flip side: the IndexedDB mirror now also holds every serialized tool argument and result, and
`cache.js` has no pruning and no size ceiling (best-effort `put`, quota errors resolve silently). Bounded
by the same tool-boundary caps as the HTTP response, and a quota failure degrades to "no offline copy",
which is the behaviour `cache.js` already documents. Worth one line in the new ADR; not worth code.

**Incognito is already handled; do not add a guard.** An ephemeral turn's tool calls sit in the live
`_session` and will replay in the owner's own UI for the rest of that activation — exactly as its *text*
already does (`.agentic/gotchas.md:3060`). Nothing new leaks: ADR 0034 promises "not persisted, not
listed", `ProjectForSearch` reads only `Text` so tool calls never reach the index, and the share strip
covers the public path. Adding a fresh `_ephemeral` check here is the mistake ADR 0034 warns about.
(`recoverPersistedReply` already returns early on `ephemeral`, `send.js:53`.)

**Degradation is free and should be asserted, not engineered.** `StripToolHistory` (`AgentGrain.cs:1435`)
empties tool content when a conversation moves to a tools-less model — the projection then finds nothing
to attach and the reply renders exactly as it does today. Write the test; write no code for it.

**Frontend guard mechanics.** The `*AssetsTests` do not read files off disk: `StaticAssetsServerFixture
.GetAppScriptAsync()` walks `main.js`'s import graph **over HTTP**, so an assertion only sees code that is
actually served and reachable (`.agentic/gotchas.md:2196`). `conversations.js` is imported at `main.js:14`,
so it is in the corpus; `viewer.js` is **not** (standalone, outside `/js/`), so there is no competing
`renderTurns` anchor — use the full marker `"function renderTurns(id, turns)"` anyway. Slice bodies with
`StaticAssetsServerFixture.Body(...)` (brace-depth counting) and strip comments with `CodeOnly(...)` —
ADR 0072 records a source-text guard that passed against deleted code because its marker anchored in the
wrong module, and the comments this plan prescribes sit inside the slices being asserted on.

## Steps

**Step 1 — The DTO and the `ChatTurn` field.** New `ChatToolCall` in
`src/Agent42.Agents/Conversations/ChatToolCall.cs`, `namespace Agent42.Agents;` (**flat** — ADR 0039,
cross-cutting rule 1, pinned by `ProjectLayoutTests.EveryFileStaysInTheFlatNamespace`),
`[GenerateSerializer]` + `[Alias("Agent42.Agents.ChatToolCall")]`, mirroring `AttachmentRef.cs`.
Members, each with `[property: Id(n)]` — **`property:` is not optional**, without it the attribute lands
on the constructor parameter and the Orleans member goes unannotated:
`[property: Id(0)] string ToolCallId`, `[property: Id(1)] string ToolName`,
`[property: Id(2)] string? ToolInput`, `[property: Id(3)] string? ToolResult`.

**The names are deliberate**: serialized with `JsonSerializerOptions.Web` they come out as
`toolCallId`/`toolName`/`toolInput`/`toolResult`, which is byte-for-byte the live
`toolCallStart`/`toolCallEnd` chunk shape `send.js:211-214` already hands to
`addToolCall`/`completeToolCall`. The replay path can then pass the object straight through with no
mapping layer. Add `[property: Id(3)] IReadOnlyList<ChatToolCall>? ToolCalls = null` as the **trailing**
member of `ChatTurn`. *Verify:* `dotnet build Agent42.slnx`.

**Step 2 — The projection.** In `GetHistoryAsync`, harvest each message's `FunctionCallContent` (name,
`CallId`, `SerializeOrNull(call.Arguments)`) and `FunctionResultContent` (matched onto the pending call by
`CallId`, `SerializeOrNull(result.Result)`) into a pending list, then flush it onto the next projected
assistant turn and clear it; clear without flushing at a user turn. Reuse `SerializeOrNull` (:1192) so the
replayed payload is identical to the streamed one. **Keep `GetHistoryAsync` under
`cognitive_complexity_max`** by putting the harvest in its own private helper — S3776 is a build error, so
this is compiler-checked, not a judgement call. *Verify:* `dotnet test Agent42.slnx --filter
"FullyQualifiedName~AgentGrainTests"`.

**Step 3 — The share-link strip.** In `AgentApi.Sharing.cs:61`, map the turns through
`turn with { ToolCalls = null }` before constructing `SharedConversationResponse`. One line plus a comment
saying why it is not `[JsonIgnore]`. *Verify:* `dotnet test Agent42.slnx --filter
"FullyQualifiedName~AgentApiTests"`.

**Step 4 — The replay path** *(parallelizable with steps 2–3 once step 1 fixes the JSON shape)*.
1. Add `replayToolCalls(msg, calls)` to `thread.js`, exactly the three-state shape in Context above, with
   a comment naming both callers and why the de-dupe exists.
2. `conversations.js` `renderTurns`, assistant arm: `replayToolCalls(msg, turn.toolCalls)`. Order within
   the arm does not matter — `toolGroup` self-positions with
   `insertBefore(group, bubble.querySelector(".prose"))`.
3. `send.js` `recoverPersistedReply`, in the success branch beside the `attachments` loop:
   `replayToolCalls(agentMsg, last.toolCalls)`.
4. Extend both import lists.

`finishToolCalls` + `syncToolGroup` already deliver the agreed fold rule with no new logic: `is-grouped`
above `TOOL_GROUP_THRESHOLD` (3), collapsed only when grouped. **No CSS change** — a replayed chip is
`is-done` and the group is not `is-running`, both already styled (`styles.css:1205-1262`).

Bump `CACHE` to `agent42-shell-v41` in `sw.js` and move the pinned literal in **both**
`PwaAssetsTests.cs:255` **and** `SearchAssetsTests.cs:127`. *Verify:* `dotnet test Agent42.slnx --filter
"FullyQualifiedName~AssetsTests"`.

**Step 5 — Tests.** Per the AC table below. Write them alongside, not after.

**Step 6 — Docs.** `/decide` for the new ADR (next number: **0080**; frontmatter fields `id`, `date`,
`status`, `tags`, `supersedes`, `extends`, `amends` — copy `0079`'s shape; `amends: [0072, 0035, 0046]`,
`extends: [0041]`). Then correct **seven** now-false claims (hard rule 2 — same MR, not `/audit-docs`):

| Site | Claim to fix |
|---|---|
| `0072:26-27` | "Tool chips are **live-only** — `renderTurns` never replays them" |
| `0072:49-50` | "a tool call that scrolled away is unrecoverable (nothing persists it)" — the premise of this whole plan is that it *is* persisted |
| `0072:62` | "nothing heals a stuck group afterwards because chips are never replayed" — **keep the `finally` wiring**, it is still right for the live turn; correct the stated reason |
| `0072` consequences | "What DevHost cannot show is a real model producing more than two tool calls in one turn" — stale: ADR 0075's push-cap demo emits `MaxPushesPerTurn + 1` = **4** (`CannedChatClient.cs:251`, trigger `"spam notifications"`), above the threshold |
| `0046:82-84` | "tool-call rows … do not come back. That is exactly what a reload gives" — now they do, and recovery additionally heals a stuck chip |
| `send.js:283` | comment: "tool chips are live-only (renderTurns never replays them), so nothing heals it either" |
| `ToolCallFoldAssetsTests.cs:78` | doc comment: "permanently, since tool chips are live-only and `renderTurns` never replays them" |

Also `thread.js:125` — "Live-only, like tool chips and reasoning" (the stopped-turn marker): the analogy
breaks, tool chips leave that set. Then `/sync-docs` over the diff. A gotchas entry is **not** expected —
the ordinal entry (`:3031`) and cache-bump entry (`:2179`) already cover the traps; add one only if
execution hits something neither covers.

**Step 7 — The offline browser leg.** `dotnet run --project tools/Agent42.DevHost`, send
**`spam notifications`** (4 calls — above threshold), watch it stream open and fold shut, then **reload the
page and reopen the chat** and confirm the folded `"4 tool calls"` section is there, collapsed, with all
four chips inside and the lamp green. Repeat with **`look up something`** (1 call) to confirm the
sub-threshold turn replays flat and open. Both themes. This is the step ADR 0072 could not do; say so in
the new ADR.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | A turn's tool calls come back from `GetHistoryAsync` attached to that turn's assistant reply, with name, input and result | `History_CarriesTheTurnsToolCalls` | `AgentGrainTests.cs` |
| 2 | A 4-call turn yields **one** assistant turn carrying 4 `ToolCalls` — turn count unchanged | `AMultiCallTurn_AddsNoExtraTurns`, driven with `FakeChatClient.PushCapTrigger`. **Call `await SubscribeDeviceAsync(...)` first** — see the precedent at `AgentGrainTests.cs:630`, or the tool is unregistered and you get no calls | `AgentGrainTests.cs` |
| 3 | Search ordinals are identical before and after — live path and reindex still agree on a history containing tool calls | extend `Reindex_AfterLiveIndexing_ProducesNoDuplicates` with a tool-calling turn | `ChatSearchIndexingTests.cs` |
| 4 | A public share link discloses **no** tool calls | `ShareLink_DisclosesNoToolCalls`. **Must not pass vacuously**: assert the owner's `/messages` for that same conversation is non-empty and carries tool calls, then `Assert.All(shared.Turns, t => Assert.Null(t.ToolCalls))` | `AgentApiTests.cs` |
| 5 | The owner's `/messages` response **does** carry them | `Messages_CarryToolCalls` — same conversation as AC 4 | `AgentApiTests.cs` |
| 6 | A conversation moved to a tools-less model replays no tool calls (`StripToolHistory`) | `AfterStrippingToolHistory_HistoryCarriesNoToolCalls` | `AgentGrainTests.cs` |
| 7 | One shared replay path, reached by **both** painters | `BothHistoryPainters_ReplayToolCalls` — `Body(app, "function renderTurns(id, turns)")` and `Body(app, "async function recoverPersistedReply")` each contain `replayToolCalls` | `ToolCallFoldAssetsTests.cs` |
| 8 | Every replayed call is completed, so none can pulse forever | `EveryReplayedCall_IsCompleted` — on `CodeOnly(Body(app, "export function replayToolCalls"))`, assert `completeToolCall` present and `Assert.DoesNotContain("toolResult", ...)` (i.e. no conditional on the result). `CodeOnly` is required: the prescribed comment mentions `toolResult` | `ToolCallFoldAssetsTests.cs` |
| 9 | A recovered turn does not double-render chips already painted live | `ReplayDeduplicates_ByCallId` — same slice contains `data-call-id` and the `is-done` skip | `ToolCallFoldAssetsTests.cs` |
| 10 | The shell cache was bumped | `ServiceWorker_CacheNameIsCurrent` + `ServiceWorker_ListsTheSearchModule_AndBumpsTheCache` (both existing, literals updated) | `PwaAssetsTests.cs`, `SearchAssetsTests.cs` |

AC 3 is the one that must not be skipped: it is the only guard against the silent double-indexing
described in `.agentic/gotchas.md:3031`.

**Known coverage limit, state it rather than fake it:** ACs 7–9 are *source-text* guards, not behavioural
tests — this repo ships no JS test runner by design (ADR 0041). The de-dupe and fold behaviour are proven
by hand in step 7, and that is what the new ADR should record.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` @ 0f97d08 — re-read the file at execution time, never recall
these numbers:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura × git diff) |
| `cognitive_complexity_max` | **15** | SonarAnalyzer.CSharp S3776 at build time — an `error`, so `dotnet build` blocks |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |

Local enforcement is `soft_warning`: render the gate table, ask proceed/abort on FAIL. Never estimate an
"Actual" — report SKIPPED and fix the gate (hard rule 3).

## Out of scope

- **Replaying reasoning** (ADR 0071). `StripReasoningFromStorage` keeps it out of the session on purpose;
  there is nothing to replay, and putting it back is a separate egress/storage decision.
- **Replaying follow-up suggestions** (ADR 0036), the token-usage line, and the stopped-turn marker
  (`thread.js:125`) — all still live-only. This plan moves *only* tool chips out of that set.
- **Showing tool calls on public share links.** Declined; revisit only as an ADR 0013 extension.
- **A preference for the fold threshold.** ADR 0072 rejected it; nothing here changes that argument.
- **Truncating replayed payloads.** Tool returns are already capped at the tool boundary for the 2 MB
  session budget (`McpToolLimits`, `WebToolLimits`, `SkillToolLimits`, `GeoToolLimits`,
  `TruncatingSkillsProvider`), so both the response and the offline mirror are bounded by construction. If
  a real conversation's `/messages` payload proves to be a problem, that is a new, measured decision — not
  a guess to build in now.
- **Pruning the ADR 0032 offline mirror.** `cache.js` has no ceiling today; this change does not add one.
- **Backfilling anything.** Old conversations replay whatever their session still holds; those pruned by
  ADR 0068 retention simply show fewer chips.

## Review

Reviewed by a `critic` subagent against the knowledge files and the tree @ 0f97d08. It verified — and I
re-verified by hand — that `ChatTurn` is in no grain state, that `sw.js` is at `v40`, that
`"spam notifications"` really produces 4 calls in one turn, that a tool-only assistant message already
projects to `null`, and that the `Body(...)` guards are expressible. Findings and dispositions:

| # | Severity | Finding | Disposition |
|---|---|---|---|
| 1 | Blocker | `recoverPersistedReply` (`send.js:51`) is a **second** consumer of `/messages`, unlisted; ADR 0046:82-84 justifies its missing chips by equivalence with a reload, which this change falsifies | **Accepted and fixed.** `send.js` added to Affected areas; both painters routed through one `replayToolCalls` helper; `amends` extended to include 0046; the ADR consequence line added to step 6. The critic's double-render warning was right and the fix goes further than it proposed: re-completing an already-done chip would also have appended a **second result body**, so the helper branches on three states and, as a bonus, heals a chip the disconnect left running |
| 2 | Major | `SearchAssetsTests.cs:127` pins the cache literal too; `PwaAssetsTests` line was `:255`, not `:245` | **Accepted.** Both rows corrected in Affected areas, step 4 and AC 10 |
| 3 | Major | Four more now-false doc sites beyond the three listed | **Accepted.** Step 6 is now a seven-row table plus `thread.js:125` |
| 4 | Minor | `[Id(n)]` on a positional record targets the parameter — the exemplar uses `[property: Id(n)]` | **Accepted.** Step 1 corrected and the reason spelled out |
| 5 | Minor | ADR 0032 offline path neither acknowledged nor tested | **Partly accepted.** Context now states both consequences (free offline replay; a fatter unbounded mirror) and Out of scope names the pruning non-goal. **No AC added**: the offline leg is the same `renderTurns` call, so an AC would test `cacheGet` rather than this change |
| 6 | Minor | AC 2's driver needs `SubscribeDeviceAsync` setup | **Accepted**, written into AC 2 with the precedent line |
| 7 | Minor | ACs 4 and 8 weakly specified / vacuously passable | **Accepted.** AC 4 pinned to the same conversation as AC 5 with a non-empty precondition; AC 8 names its assertion and the `CodeOnly` requirement |
| 8 | Nit | Flat-namespace rule not restated for the new file | **Accepted**, added to step 1 |

Nothing was rejected.

## Execution notes — where the implementation diverged from the plan

Executed 2026-09-20 on `feature/replay-tool-calls-from-history`, from `main @ 0f97d08` — the exact ref the
plan was written against, so there was no drift to reconcile. The plan held up: no affected-area surprises,
no new endpoint, no infra, no CSS, no new JS module, no gotchas entry needed.

Five things came out differently from the written steps.

1. **`GetHistoryAsync` needed a SECOND helper, not one.** Step 2 prescribed putting the harvest in its own
   private method to stay under `cognitive_complexity_max`. Inlining the flush-and-clear logic in the loop
   would still have piled a role test, an else-branch, a `&&` and a nested `if` on top of two ternaries
   already inside a `foreach`. The flush is therefore its own `AttachPendingToolCalls(message, turn, pending)`
   alongside `HarvestToolCalls`, which leaves the loop body at roughly the complexity it had before.

2. **The two share tests could not both be `Assert.Single`.** The AC table put ACs 4 and 5 on the same
   conversation, which is right — AC 4 is vacuous over a tool-less history. But `AgentApiTests` share a
   fixture and each test drives its own turn, so the call COUNT depends on execution order.
   `Messages_CarryToolCalls` initially used `Assert.Single` and failed with "the collection contained 2
   items". It now asserts `NotEmpty` plus `Assert.All` over every call — order-independent, and the claim
   under test is unchanged. `ShareLink_DisclosesNoToolCalls` keeps its own non-vacuity precondition inline.

3. **ADR 0035 was corrected too**, though step 6's table listed only 0072 (×4), 0046, `send.js` and
   `ToolCallFoldAssetsTests`. The plan's own Context flagged 0035's "full current history — text, images,
   and anything it contains" as no longer literal, and hard rule 2 makes that a same-MR fix. It was one
   the table simply missed. `thread.js:125` was corrected as the plan separately noted.

4. **The browser leg ran over CDP, not by hand.** The Chrome extension was not connected in this session,
   so step 7 was driven the way ADR 0046 and 0072 drove theirs: headless Chrome over CDP against DevHost,
   importing the real shipped modules. That turned out to be strictly better than the prescribed manual
   check — it compares the LIVE group against the REPLAYED one field by field rather than by eye. All four
   combinations (4-call and 1-call × light and dark) came back identical. Numbers are in ADR 0080.

5. **The `renderTurns` guard was mutation-checked.** Not asked for, but the plan's own Context cites ADR
   0072's guard that passed against deleted code. Deleting the `replayToolCalls` call from `renderTurns`
   fails `BothHistoryPainters_ReplayToolCalls`; restored and re-verified green.

6. **The critic pass found a real defect the plan's AC table had no criterion for**, and it changed the
   projection's shape. The plan prescribed matching each `FunctionResultContent` back onto a pending call
   as the walk proceeds. That drops the result whenever a model emits `content` **and** `tool_calls` on one
   assistant message: that message projects to a real turn, the chain flushes onto it there and then, and
   the result — which always arrives on the NEXT, tool-role message — has nothing left to match against.
   The chip would have replayed with an input and no result, silently, on an ordinary provider shape. The
   projection now collects results over the whole history in their own pass first and joins them by id,
   which is both correct and simpler than what was planned. `FakeChatClient` gained a `NarratingCallTrigger`
   to express the shape at all (it previously `yield break`ed on any tool call, so a call message could
   never carry text), and `ANarratedToolCall_StillCarriesItsResult` pins it — mutation-checked.

7. **The same pass showed a justification that was simply false**, repeated across five sites: "a call
   whose result never came back — a turn stopped mid-chain (ADR 0078)". `gotchas.md` records, measured on
   AF 1.19 and verified the same day, that cancelling a run persists *nothing* — not even the user message
   — so ADR 0078 cannot leave a dangling `FunctionCallContent`. The code was right; the reason was not. All
   five sites now say what is actually reachable (a tool returning null, or no result in the history at
   all). Two further doc corrections came from the same pass: ADR 0072's do-not-copy callout still said
   "both are live-only", and `ShareContracts.cs` still claimed the share payload was the same shape as
   `/messages`. `CLAUDE.md`'s feature map gained `0080` on the tool-call-folding row.

8. **A gotchas entry was warranted after all.** The plan expected none, on the grounds that the ordinal
   entry and the cache-bump entry already cover the traps here — correct for everything the plan foresaw.
   Finding 6 above is a trap neither covers, and the nearest existing entry ("an assistant generated image
   is id-keyed…") describes only the *canned clients'* call-only message shape, which is precisely what
   makes the real one invisible. Added: "A tool result never arrives on the message that made the call".

Everything else — the DTO shape and its `[property: Id(n)]`, the three-state de-dupe, the unconditional
`completeToolCall`, the server-side share strip, the `v40 → v41` bump across both pinned literals, and all
ten acceptance criteria — landed as written.
