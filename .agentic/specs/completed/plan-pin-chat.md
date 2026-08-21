# Plan — Pin a chat so it stays on top of the history list

planned-against: main @ 5740bbe

## Overview

A user can **pin** any listed conversation from its sidebar row; pinned chats are hoisted to
the top of the transmissions list and stay there regardless of recency. Today the list is
purely recency-ordered (`OrderByDescending(c => c.UpdatedAt)`), so a chat the user returns to
often but hasn't touched today sinks below noise. Pinning is the standard fix and the index
grain already owns exactly the per-conversation metadata this needs.

Goal: a pin toggle per row, persisted per user, honoured by the sidebar ordering, surviving
reloads, reachable on touch as well as with a mouse.

## Affected areas

Verified against the tree at `5740bbe`.

**Backend / grains**
- `src/Agent42.Agents/Conversations/IConversationIndexGrain.cs` — `ConversationInfo` record + grain interface
- `src/Agent42.Agents/Conversations/ConversationIndexGrain.cs` — `TouchAsync`, `GetConversationsAsync`, `RemoveAsync`
- `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` — new `PUT /api/conversations/{id}/pin`
- `src/Agent42.Backend/Conversations/ChatContracts.cs` — new request record

**Frontend** (`src/Agent42.Backend/wwwroot/`)
- `js/sidebar.js` — `buildConvRow`, `renderSidebar`, new `setPinned` action
- `styles.css` — `.conv` padding, new `.conv__pin`, `.conv-row.is-pinned`, pinned-block rule
- `sw.js` — `CACHE` bump

**Tests** (`tests/Agent42.Tests/`)
- `AgentGrainTests.cs` — index-grain pin behaviour (the three existing `ConversationIndex_*` tests live here)
- `AgentApiTests.cs` — endpoint round-trip
- `GrainStorageSerializerTests.cs` — **the real** pre-pin persistence proof (Cosmos JSON path)
- `SerializationCompatibilityTests.cs` — binary-path assertion (secondary)
- `EasyAuthExclusionTests.cs` — one new `[InlineData]` row for the new route
- `PwaAssetsTests.cs` — `ServiceWorker_CacheNameIsCurrent` literal
- `PinChatAssetsTests.cs` — **new** source-text guard for the sidebar wiring

**Verified non-impact** (checked, deliberately not changed): `Sharing/AgentApi.Sharing.cs:61–62`
(the other `GetConversationsAsync` caller — see below), `wwwroot/shared/` viewer,
`sharetarget.js`, `push.js`, `main.js` `hydrateFromCache`, `agents.js`'s continuous-chat lookup,
`ProjectLayoutTests`, `GrainIdentityTests`. No host registration is needed — the endpoint injects
only `IGrainFactory` + `HttpContext` + body, so DevHost needs no new DI (unlike the
`IMcpSecretProtector` / `PushConfig` route-matcher traps). No Bicep, secret, container or RBAC change.

**Docs**
- `.agentic/decisions/0045-pinned-conversations.md` — **new**
- `.agentic/gotchas.md` — one new entry
- `CLAUDE.md` — one repo-note bullet

## Context & constraints

### Decisions taken in the interview (do not relitigate)
- **Control lives on the sidebar row**, beside the existing share + delete actions — not in the
  ADR 0044 topbar cluster. Rejected: topbar-only (can't pin a chat you aren't looking at) and
  both (more state-sync transitions, the ADR 0044 `startAgentChat` trap for no gain).
- **Any listed chat is pinnable**, regular or agent-bound. No per-row capability branching.
- **No cap** on pin count. No error path, no validated failure mode.
- **Pinned chats sort among themselves by last activity**, not by pin time. Rejected:
  pinned-at timestamp (extra stored field for no asked-for behaviour) and manual drag ordering
  (a much larger feature).

### The load-bearing finding — a pinned agent chat must be hoisted OUT of its group

`renderSidebar` (`js/sidebar.js`) does **one walk** over the server-ordered list and reserves a
per-agent group's slot in the list at its **first-encountered (= most recent) member**, filling
it via a placeholder `<li>` replaced afterwards. Consequence: if pinning were implemented as
*server-side re-sorting alone*, pinning a single agent chat would drag **that agent's entire
group** to the top of the sidebar, because the group's slot follows its most-recent member.

So the render must be **two passes**: a flat pinned block first, then the existing grouping walk
over the **unpinned remainder only**. A pinned agent chat therefore appears as a flat row at the
top and is **absent from its group below** — it must not render twice, and the group's
`.conv-group__count` then reflects only its unpinned members. This is the single design
constraint the executor must not get wrong.

**Two consequences of hoisting that must be handled, not discovered (Step 6):**
1. **The agent label is lost on hoist.** An agent's name is rendered *only* by
   `.conv-group__name` in `buildAgentGroup` (`sidebar.js:113–115`); `buildConvRow` renders title
   + time only. A hoisted agent chat would therefore be visually indistinguishable from a
   regular chat. `buildConvRow` must render an agent-name badge when `conv.agentName` is set
   **and** the row is being rendered in the pinned block.
2. **A fully-pinned agent group disappears — and that is correct only because of (1).** If every
   one of an agent's chats is pinned, the grouping walk sees zero members and `buildAgentGroup`
   is never constructed. For a **continuous** agent (ADR 0016 — exactly one conversation, id
   `agent-{agentId}`, and the most pin-worthy chat in the app) this is the *normal* case, not an
   edge case. It is acceptable because the hoisted row carries the agent's name via (1); without
   (1) it would be a disappearing-agent bug. Do not add an empty-group placeholder.

### Why `TouchAsync` needs no change
`ConversationIndexGrain.TouchAsync` updates an existing entry with a non-destructive copy —
`conversations[existing] = conversations[existing] with { UpdatedAt = DateTimeOffset.UtcNow }`
(`ConversationIndexGrain.cs`). A `with` expression carries every other member forward, so a new
`Pinned` member survives a touch **for free**. That is worth a test precisely because it is
free — a later refactor to an explicit `new ConversationInfo(...)` would silently unpin every
chat on its next turn.

### Persistence constraints (read before touching the record)
- `ConversationInfo` is persisted inside `ConversationIndexState` in the Cosmos `agent-state`
  container, state name `"conversations"`. Per the "`$type` is part of the on-disk format"
  gotcha, the type's **name and namespace must not move** — it stays `Agent42.Agents`, flat,
  with its existing `[Alias]` frozen. This change **only appends** a member, which is safe.
- The new member must be a **trailing optional `[Id(5)]` with a `false` default**, exactly like
  the `[Id(3)]`/`[Id(4)]` agent-identity pair whose own comment records the same reasoning.
  Zero state migration: an existing document has no `pinned` key and deserializes to `false`.
- **The proof must be on the JSON path, not the binary one.** `SerializationCompatibilityTests`
  exercises Orleans' **binary** serializer; Cosmos writes grain state through
  `JsonGrainStorageSerializer` (Newtonsoft). The `[Alias]` gotcha is explicit that "a green
  `SerializationCompatibilityTests` says nothing here", and its whole reason for existing is that
  ADR 0039 nearly shipped data loss by trusting exactly that test. The real risk in *this* change
  is **Newtonsoft constructor-binding a missing property onto a positional record** — which only
  the JSON path exercises. `GrainStorageSerializerTests` today commits a golden document for
  `AgentGrainState` only (`GrainStorageSerializerTests.cs:118`); there is **no**
  `ConversationIndexState` payload. Step 5 adds one. The binary-path assertion is kept as a
  secondary, not as the proof.

### The other `GetConversationsAsync` caller (verified non-impact — do not "fix" it)
`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs:62` also calls `GetConversationsAsync()`, on
the **owner's** index, to resolve a shared conversation's title for the public ADR 0035 viewer.
It is an id lookup over the returned list, so the ordering change is inert there and no change
is needed. It must stay a lookup — do not let a pinned-first ordering tempt an index-based
shortcut, and do not surface `Pinned` into the public share payload (pins are owner-side
sidebar state; ADR 0035's payload should not grow a field).

### "Pinned" is already an overloaded word here — keep the name, record the ambiguity
This repo already uses "pinned" for **model binding**: "the already-pinned persona stands"
(`AgentApi.Conversations.cs:71`), "the model is pinned server-side" (`js/send.js:60`), and
CLAUDE.md's ADR 0018 bullet "the first turn calls `RebindModelAsync` and pins the model".
Those are all **prose and comments**, never a member name, and they sit on a different type
(`AgentGrainState`/`AgentBinding`) from `ConversationInfo`. Decision: **keep `Pinned`** — it is
the user's own word for the feature and the universal UI term for this affordance, and
`Starred`/`Favourite` would be a private coinage nobody searching the codebase would guess.
But note the cost honestly: this name lands on a persisted member, so it becomes an on-disk
JSON key that is cheap to choose now and awkward to change later. The ADR must record the
collision and this reasoning, so the next reader isn't left wondering.

### The row actions are hover-only — on touch, pin must still be reachable
`.conv__share` and `.conv__delete` are `opacity: 0`, revealed only by `.conv-row:hover` /
`:focus-visible` (`styles.css:292`, `299–302`). There is **no `hover: none` override anywhere
in `styles.css`** (the only media blocks are two `max-width: 760px` ones at lines 1932 and
2217). A naive `.conv__pin` copy would therefore be unreachable on the installed Android PWA —
and worse, *asymmetrically* so: Step 7's `.conv-row.is-pinned .conv__pin { opacity: 1 }` makes
an already-pinned row's control visible, so a user could **un**pin on touch but never pin.
Android/Chromium is a first-class surface here (ADR 0023 share target, ADR 0028 push), and the
reachable-on-mobile alternative (a topbar toggle) was declined in the interview — so this must
be fixed in CSS, not left as a residual. Step 7 adds an `@media (hover: none)` block revealing
the row actions permanently. This incidentally also fixes share and delete, which have the same
latent gap today; that is a deliberate in-family fix, and the ADR should say so rather than let
it look like scope creep.

### Endpoint constraints
- The route is **behind Easy Auth** like every other `/api/*` read/write. It must **not** be
  added to `container-app.bicep`'s `excludedPaths`. Note precisely what the guard does and does
  not do: `EasyAuthExclusionTests` pins matcher *semantics*, **not** an entry count — so leaving
  it untouched proves nothing about the new route. `Exclusions_DoNotCoverAnyAuthenticatedPath` is
  a theory already carrying `[InlineData("/api/conversations/x/share")]`
  (`EasyAuthExclusionTests.cs:79`); add `[InlineData("/api/conversations/x/pin")]` to it so the
  new route is actually asserted to stay gated.
- userId comes from `http.GetUserId()` (the ordinary gated path), and the grain key scopes the
  write to that user — a pin on someone else's conversation id is structurally impossible.
- **Exemplars to copy (two, and they agree):** the gotchas table names
  `src/Agent42.Backend/Users/AgentApi.Users.cs` as the canonical minimal-API endpoint group —
  its body-bound `MapPut("/api/profile", (ProfileUpdateRequest request, …)` (line 27) is the
  shape without a 400-validation branch, which is what the pin endpoint wants. The nearest
  *sibling route* is `PUT /api/conversations/{conversationId}/tools`
  (`src/Agent42.Backend/Tools/AgentApi.Tools.cs:37`) — same route family, same `http.GetUserId()`
  scoping. Copy these; do not invent a shape.
- There are already eleven `/api/conversations/{conversationId}/…` routes across five partials
  (`Conversations`, `Sharing`, `Tools`, `Attachments`, `StoredAgents`). `pin` is a fresh literal
  second segment, so it collides with none of them.

### Frontend constraints
- **Every MR touching `wwwroot/js/` must bump `CACHE` in `sw.js`** (currently
  `agent42-shell-v15`) — this adds a named export, and a version-skewed module mixture is a
  blank app, not a degraded one. `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` pins the
  literal, so the bump must land in the same MR. No new file is added, so the `SHELL` array
  itself does not change.
- Offline (ADR 0032) needs **no code change**: `refreshConversations` already mirrors the whole
  `/api/conversations` response under the `conversations` key, so `pinned` rides along. Known
  and accepted degradation: a list cached before this ships has no `pinned` key, so those
  entries read `undefined` → falsy → render unpinned until the next online refresh.
- Follow the existing action-button idiom in `buildConvRow` — `e.stopPropagation()` in the
  handler (the row button underneath navigates), an `aria-label` naming the conversation, and
  author-controlled inline SVG markup.
- `deleteConversation` in the same file is the model for the mutate-then-reconcile shape:
  guard on `streaming`, `try`/`catch` that leaves the row alone on failure, mutate the local
  `conversations` array, re-render, then `refreshConversations()`.

### Governing docs to read
- `.agentic/gotchas.md`: "Cosmos grain storage is JSON with `$type`", "Every MR that touches
  `wwwroot/js/` must bump `CACHE`", "Offline data for agents/chats lives in an app-module
  IndexedDB store", "A frontend module unreachable from `main.js` is invisible to every
  source-text guard", "An Easy-Auth `excludedPaths` endpoint is publicly reachable".
- ADRs: `0032` (offline cache), `0035` (the share row action this sits beside), `0041`
  (ES modules), `0044` (topbar cluster — the rejected alternative), `0039` (flat namespace).

## Steps

### Step 1 — Add `Pinned` to `ConversationInfo` and the grain
Files: `src/Agent42.Agents/Conversations/IConversationIndexGrain.cs`,
`src/Agent42.Agents/Conversations/ConversationIndexGrain.cs`

- Append `[property: Id(5)] bool Pinned = false` to `ConversationInfo`, with a comment in the
  style of the existing `[Id(3)]`/`[Id(4)]` note (trailing optional → no migration).
- Add `SetPinnedAsync(string conversationId, bool pinned)` to `IConversationIndexGrain` with an
  `[Alias("SetPinnedAsync")]`, documented as a no-op for an unlisted conversation.
- Implement it: find the entry, `with { Pinned = pinned }`, and **write only when the value
  actually changed** (mirrors `RemoveAsync`'s conditional `WriteStateAsync`).
- Change `GetConversationsAsync` to
  `.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt)`, and update its
  `<summary>` ("pinned first, then most recently used").
- Leave `TouchAsync` **untouched** — its `with` expression already carries `Pinned` forward.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentGrainTests"`

### Step 2 — Grain tests (depends on step 1)
File: `tests/Agent42.Tests/AgentGrainTests.cs` — beside the three existing `ConversationIndex_*` tests
(`ConversationIndex_ListsTouchedConversations_MostRecentFirst` at line 145 is the exemplar to
copy). Follow its convention: **each test uses its own distinct grain key** (`"test-user"`,
`"agent-index-user"`, `"remove-user"`) so state never leaks between tests — give each new test
a fresh key too. Ordering relies on sequential `TouchAsync` calls producing distinct
`DateTimeOffset.UtcNow` values, exactly as the existing test already does.

- `ConversationIndex_SetPinned_HoistsTheConversationAboveMoreRecentOnes` — touch A then B (B is
  newer), pin A, assert A is first.
- `ConversationIndex_PinnedChats_SortAmongThemselvesByRecency` — pin two, assert the more
  recently touched pinned one leads. **Force distinct timestamps rather than assuming them:**
  `OrderByDescending` is stable and `TouchAsync` stamps `DateTimeOffset.UtcNow`, so two touches
  landing in the same clock tick preserve *insertion* order — the opposite of what this test
  asserts. The existing `ConversationIndex_ListsTouchedConversations_MostRecentFirst` never
  exposed this because its expected leader is also its first-inserted entry, so a tie passes it.
  This test has no such luck; make the ordering unambiguous (e.g. a small delay between touches).
- `ConversationIndex_Touch_PreservesThePinnedFlag` — pin, touch again, assert still pinned.
  **This is the regression test for the free-`with` finding above.**
- `ConversationIndex_SetPinned_UnknownConversation_IsANoOp` — no throw, list unchanged.

Verify: same filter as step 1.

### Step 3 — `PUT /api/conversations/{conversationId}/pin` (depends on step 1; parallelizable with step 2)
Files: `src/Agent42.Backend/Conversations/ChatContracts.cs`,
`src/Agent42.Backend/Conversations/AgentApi.Conversations.cs`

- Add `public record PinConversationRequest(bool Pinned);` beside `ChatRequest`.
- Map the route in the conversations partial, next to the `MapDelete`, following the
  `PUT …/tools` exemplar (`Tools/AgentApi.Tools.cs:37`): resolve `http.GetUserId()`, call
  `SetPinnedAsync(conversationId, request.Pinned)`, return `Results.NoContent()`.
- Comment it as idempotent and a no-op for an unlisted id (no existence disclosure).
- Do **not** touch `infra/modules/container-app.bicep`.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentApiTests"`

### Step 4 — Endpoint tests (depends on step 3)
File: `tests/Agent42.Tests/AgentApiTests.cs`

- `PinConversation_HoistsItInTheListing` — create two chats, `PUT .../pin` `{pinned:true}` on
  the older, `GET /api/conversations`, assert it leads and `Pinned` is true.
- `PinConversation_Unpin_RestoresRecencyOrder`.
- `PinConversation_UnknownId_Returns204` — idempotent no-op.

Also add `[InlineData("/api/conversations/x/pin")]` to
`EasyAuthExclusionTests.Exclusions_DoNotCoverAnyAuthenticatedPath` (the theory already carries
the sibling `/api/conversations/x/share` row at `EasyAuthExclusionTests.cs:79`), so the new
route is positively asserted to stay Easy-Auth gated rather than merely assumed to be.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentApiTests|FullyQualifiedName~EasyAuthExclusionTests"`

### Step 5 — Prove pre-pin state still reads, on the JSON path (depends on step 1; parallelizable with steps 2–4)
Files: `tests/Agent42.Tests/GrainStorageSerializerTests.cs` (**the real proof**),
`tests/Agent42.Tests/SerializationCompatibilityTests.cs` (secondary)

- **Primary:** commit a pre-pin `ConversationIndexState` JSON document (hand-written, in the
  `$type`-carrying Newtonsoft shape the existing `AgentGrainState` fixture at
  `GrainStorageSerializerTests.cs:118` uses, with **no `pinned` key**) and assert it
  deserializes with `Pinned == false`. This is the only assertion that exercises the path
  Cosmos actually uses, and Newtonsoft binding a *missing* property onto a **positional record**
  is the specific mechanism at risk.
- Add `"Agent42.Agents.ConversationInfo"` to the pinned type-name list if it is not already
  there (`GrainStorageSerializerTests.cs:58` pins the state-root names).
- **Secondary:** in `SerializationCompatibilityTests.ConversationIndexState_RoundTripsFieldForField`,
  add `Assert.False(actual.Conversations[0].Pinned)` and **do not regenerate
  `ConversationIndexStateBlob`**. Understand that this is the *binary* path and is explicitly
  **not** the data-at-rest guard — it is here for completeness only.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~GrainStorageSerializerTests|FullyQualifiedName~SerializationCompatibilityTests"`

### Step 6 — Sidebar pin action, two-pass render, its tests, and the cache bump (depends on step 3)
Files: `src/Agent42.Backend/wwwroot/js/sidebar.js`, `src/Agent42.Backend/wwwroot/sw.js`
Tests: `tests/Agent42.Tests/PinChatAssetsTests.cs` (**new**), `tests/Agent42.Tests/PwaAssetsTests.cs`

**All of this lands in one step** — the JS, the `sw.js` bump, the `PwaAssetsTests` literal that
pins it, and the new guards. Splitting the `sw.js` bump from its assertion leaves a red gate in
between (the ADR 0044 plan hit exactly this and called it out); splitting the JS from its
guards defers tests, which hard rule 3 forbids.

Implementation:
- In `buildConvRow`: add a `.conv__pin` button as the **third** action (appended before `share`
  and `del`, sitting left of them), with `aria-pressed` reflecting `conv.pinned`, an
  `aria-label` naming the conversation, and a click handler that `stopPropagation()`s and calls
  `setPinned(conv.id, !conv.pinned)`. Add `is-pinned` to the row class when `conv.pinned`.
- `buildConvRow` takes a second parameter (e.g. `hoisted`) and, when it is set and
  `conv.agentName` exists, renders an agent-name badge — **the fix for the lost-label
  consequence above**. Existing call sites pass nothing and are unchanged.
- Add `async function setPinned(id, pinned)` following `deleteConversation`'s shape: guard on
  `streaming`, `PUT` the flag, return on failure without mutating, else update the entry in the
  local `conversations` array, `renderSidebar()`, then `refreshConversations()`.
- Rework `renderSidebar` into **two passes**: (1) render every `conv.pinned` entry as a flat
  `buildConvRow(conv, true)` — server order already puts them first and sorts them by recency,
  so **do not re-sort in JS**; (2) run the **existing** placeholder/grouping walk over the
  **unpinned remainder only**. Keep the empty-state early return. Comment *why* the split exists.
- `sw.js`: `agent42-shell-v15` → `agent42-shell-v16`. `SHELL` is unchanged (no new file).

Tests — new class following `ShareLinkAssetsTests` exactly (`StaticAssetsServerFixture`, the
`[Collection("StaticAssets")]` fixture, `GetAppScriptAsync()` + `Body(app, "function …")`):
- `buildConvRow` contains `conv__pin`, `setPinned(conv.id`, `is-pinned`, `aria-pressed`.
- `buildConvRow` renders the agent badge on the hoisted path (`conv.agentName`).
- `setPinned` targets `/pin` with `method: "PUT"`.
- **The two-pass ordering claim** — use the repo's named idiom for order/containment assertions:
  `IndexOf` slicing over the `renderSidebar` body, as in `ConfigHealthAssetsTests.cs:41–48`.
  Assert the pinned-block emit appears **before** the grouping walk, and that the walk's source
  text contains the unpinned guard. Add a comment stating plainly that this is a **source-text
  heuristic** — it proves the guard shipped, **not** that no row renders twice at runtime (see
  the residuals section; that half is covered by the manual pass, not by this test).
- Update the `CACHE` literal in `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` to `-v16`.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~PinChatAssetsTests|FullyQualifiedName~PwaAssetsTests"`

### Step 7 — Styles, including touch reachability (depends on step 6)
Files: `src/Agent42.Backend/wwwroot/styles.css`
Tests: `tests/Agent42.Tests/PinChatAssetsTests.cs` (extend the class from step 6)

- Widen `.conv`'s right padding from `10px 58px 9px 14px` to `10px 82px 9px 14px` and update
  its inline comment — three hover-revealed actions now, not two. **Without this the pin glyph
  overlaps long titles.**
- Add `.conv__pin` mirroring `.conv__share` (`right: 31px`) at `right: 55px` — this continues
  the existing 24px step exactly.
- Add `.conv-row.is-pinned .conv__pin { opacity: 1; }` so a pinned row reads as pinned without
  hovering — a hover-only reveal suits an action but not a state indicator.
- **Add an `@media (hover: none)` block making `.conv__pin`, `.conv__share` and `.conv__delete`
  permanently visible.** Without it the control is unreachable on the Android PWA and pinning is
  impossible there while unpinning works — see the constraint above. Comment it as covering the
  pre-existing share/delete gap too.
- Style the hoisted agent badge, and give the pinned block visual separation from the recency
  list, both in the existing amber/phosphor idiom.

Tests (source-text, via the fixture client on `/styles.css`): contains
`.conv-row.is-pinned .conv__pin`, contains `hover: none`, and no longer contains
`10px 58px 9px 14px`.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~PinChatAssetsTests"`, then the
manual DevHost pass in step 8.

### Step 8 — Manual verification (depends on steps 6–7)
Run `dotnet run --project tools/Agent42.DevHost` and check what no source-text test can:
- a pinned chat sits at the top and **renders exactly once** (the AC9 runtime half);
- a pinned *agent* chat shows its agent name and is gone from its group;
- a **continuous** agent chat pinned → its group disappears and the top row still names the agent;
- three actions fit the row at ≤760px, both themes;
- the pin control is tappable with no pointer (emulate a touch device / `hover: none`).

**Record the measured outcome in the ADR.** If a leg is not run, say so — ADR 0043 shipped an
unverified interactive claim and that is the cautionary precedent; ADR 0044 ran its pass and
recorded real values, which is the standard to meet.

### Step 9 — Docs
- `/decide` → `.agentic/decisions/0045-pinned-conversations.md`. It must record: the pinned flag
  on the index entry rather than a new grain; **server-side ordering plus a client-side two-pass
  render**, and why the split is required (the group-slot mechanic) rather than ordering alone;
  the four interview decisions and their rejected alternatives; the **`Pinned` naming collision**
  with model-pinning and why the name was kept anyway; that a fully-pinned agent group
  **disappears by design**, safe only because the hoisted row carries the agent badge; that the
  `@media (hover: none)` block also fixes the pre-existing share/delete touch gap (in-family, not
  scope creep); that offline lists cached before this ships render unpinned until the next
  refresh; and the **measured** step-8 outcomes plus any leg not run.
- `/gotcha` → one entry: *a pinned agent chat must be hoisted out of its group, never sorted
  into it — and hoisting costs the agent label, which the badge repays*. Evidence:
  `js/sidebar.js` (`renderSidebar`, `buildConvRow`), `tests/…/PinChatAssetsTests.cs`.
- `CLAUDE.md` → one repo-note bullet in the established house style.

Verify: `dotnet test Agent42.slnx` (whole suite) + `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A pinned chat leads the listing regardless of recency | `ConversationIndex_SetPinned_HoistsTheConversationAboveMoreRecentOnes` | `AgentGrainTests.cs` |
| AC2 | Pinned chats sort among themselves by last activity | `ConversationIndex_PinnedChats_SortAmongThemselvesByRecency` | `AgentGrainTests.cs` |
| AC3 | A new turn on a pinned chat does not unpin it | `ConversationIndex_Touch_PreservesThePinnedFlag` | `AgentGrainTests.cs` |
| AC4 | Pinning an unlisted conversation is a silent no-op | `ConversationIndex_SetPinned_UnknownConversation_IsANoOp` / `PinConversation_UnknownId_Returns204` | `AgentGrainTests.cs`, `AgentApiTests.cs` |
| AC5 | `PUT …/pin` round-trips through `GET /api/conversations` | `PinConversation_HoistsItInTheListing` | `AgentApiTests.cs` |
| AC6 | Unpinning restores recency order | `PinConversation_Unpin_RestoresRecencyOrder` | `AgentApiTests.cs` |
| AC7 | Pre-pin persisted state still deserializes from **Cosmos JSON**, defaulting to unpinned | **new** `ConversationIndexState` golden-document test (primary) + `ConversationIndexState_RoundTripsFieldForField` (secondary, binary path) | `GrainStorageSerializerTests.cs`, `SerializationCompatibilityTests.cs` |
| AC8 | Every row carries a pin toggle wired to the endpoint | `buildConvRow` + `setPinned` assertions | `PinChatAssetsTests.cs` |
| AC9 | A pinned agent chat renders flat at the top, not inside its group | `renderSidebar` two-pass `IndexOf` ordering + unpinned-guard assertions (**source text only** — the "not twice" half is manual, step 8) | `PinChatAssetsTests.cs` |
| AC10 | A pinned row shows its state without hover, and the control is reachable on touch | `.conv-row.is-pinned .conv__pin` + `hover: none` CSS assertions | `PinChatAssetsTests.cs` |
| AC11 | The shell cache was bumped with the JS change | `ServiceWorker_CacheNameIsCurrent` | `PwaAssetsTests.cs` |
| AC12 | The new route stays Easy-Auth gated | `Exclusions_DoNotCoverAnyAuthenticatedPath` + **new** `[InlineData("/api/conversations/x/pin")]` | `EasyAuthExclusionTests.cs` |
| AC13 | Agent grouping still works after the render split | `Frontend_GroupsAgentConversationsCollapsibly` (existing, must stay green untouched — it asserts `buildAgentGroup`, `conv.agentId`, `EXPANDED_AGENTS_KEY`) | `PwaAssetsTests.cs` |
| AC14 | A hoisted agent chat still shows which agent it belongs to | `buildConvRow` agent-badge assertion (source text) + manual pass (step 8) | `PinChatAssetsTests.cs` |

**Untestable residuals — state these honestly in the ADR, do not let the table imply otherwise.**
The repo has no JS runner by design, so every frontend assertion above is a source-text
heuristic: it proves the code *says* the right thing, never that it *does* it. Specifically
unproved by any automated test:
1. **That a pinned chat renders exactly once.** This is a runtime DOM property and the plan's own
   central design constraint. AC9's test can only prove the two-pass structure and the unpinned
   guard shipped. Step 8's manual pass is the actual evidence.
2. **The visual result** — pinned-block separation, the agent badge, three actions fitting the
   row at ≤760px, both themes.
3. **Touch reachability** — `hover: none` behaviour is a device fact; the test pins the media
   query string only (ADR 0037's untestable-half treatment).

Run the step 8 pass and record measured outcomes, as ADR 0044 did. Do **not** report any of
these as verified if the leg was skipped — ADR 0043 is the cautionary precedent.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (@ `5740bbe`):

| Gate | Threshold |
|---|---|
| New issues | 0 |
| New security issues | 0 |
| Duplication % max | 3 |
| Coverage on new code % min | 80 |
| Cognitive complexity max | 15 |
| Vulnerable packages | 0 (hard block) |
| Outdated packages | report (soft) |

Enforcement: local `soft_warning` (render the table, ask proceed/abort on FAIL); CI
`hard_block` on build, tests, `dotnet format`, vulnerable packages, doc-path integrity.
Coverage tooling: `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` (coverlet).

Branch policy: `main` is protected — work on `feature/pin-chat`, MR to `main`.

**Complexity watch:** `renderSidebar` gains a pass. If it crosses cognitive complexity 15,
extract the grouping walk into a helper rather than raising the gate.

## Out of scope

- **Pin cap / limit** — explicitly declined in the interview.
- **Manual drag ordering of pinned chats** — declined; would need a stored sort key and DnD.
- **A pinned-at timestamp** — declined; pinned chats sort by recency.
- **A topbar pin toggle** (ADR 0044 cluster) — declined; the row action is the only entry point.
- **Pinning agents** (the `/api/agents` list) — this is conversations only.
- **Renaming a conversation** — there is no rename endpoint today and this plan does not add one.
- **Pinning an ephemeral/incognito chat** — structurally impossible: ADR 0034 chats never enter
  the index at all, so there is no row to pin and nothing to guard.
- **Reflecting pins in the read-only share view** (ADR 0035) — pins are owner-side sidebar state.
- **Any Bicep, secret, container or RBAC change** — this feature needs none.

## Review

A `critic` pass ran against this spec before approval. It found no invented path, function, CSS
value, test name or gate threshold, and confirmed the central group-slot finding holds against
the real `renderSidebar`. Twelve findings; how each was handled:

**Accepted and fixed in this spec**

| # | Finding | Fix |
|---|---|---|
| 1 | AC7 proved on the **binary** serializer path — the exact mistake the `$type` gotcha exists to prevent; no `ConversationIndexState` JSON fixture exists | Step 5 rewritten: a committed pre-pin JSON golden document in `GrainStorageSerializerTests` is now the primary proof; the binary assertion is explicitly secondary |
| 2 | AC9 ("not rendered twice") is a runtime property a source-text guard cannot prove, and Step 8 left the assertion unspecified | AC9 re-scoped to what the test can prove; the runtime half moved into an explicit residual + the manual pass. Assertion idiom named: `IndexOf` slicing per `ConfigHealthAssetsTests.cs:41–48` |
| 3 | Step 6 deferred its tests and forward-referenced Step 8 (hard rule 3) | Steps 6 and 7 now each carry **Files** + **Tests** and verify themselves |
| 4 | `sw.js` bump and its pinned assertion split across steps → a red gate in between | Merged into Step 6, as the ADR 0044 plan explicitly warned |
| 5 | False claim that `EasyAuthExclusionTests` pins the list "at exactly four entries" | Corrected — it pins matcher *semantics*, not a count. AC12 now adds a real `[InlineData]` row for the new route instead of claiming an untouched test proves it |
| 6 | Hoisting an agent chat discards its only label; a **continuous** agent's group vanishes entirely | Two named consequences added to the design section; Step 6 now adds an agent badge on the hoisted path; AC14 added |
| 7 | Row actions are hover-only with no `hover: none` override → on Android a chat could be un-pinned but never pinned | New constraint section + an `@media (hover: none)` block in Step 7; AC10 extended |
| 11 | "surviving offline replay" stated as a goal with no AC | Dropped from the goal; offline stays documented as a no-code-change non-impact with a named degradation |
| 12 | AC2's test assumes distinct `UtcNow` stamps; a same-tick tie asserts the opposite of stable-sort behaviour | Step 2 now requires forcing distinct timestamps, with the reasoning recorded |

**Accepted, already fixed before the pass landed** — 9 (endpoint exemplar: now cites the
canonical `Users/AgentApi.Users.cs` *and* the sibling `Tools/AgentApi.Tools.cs:37`, replacing the
weaker `AgentApi.Models.cs:80` reference) and 10 (the `Sharing/AgentApi.Sharing.cs:61–62` caller,
now documented as a verified non-impact with a do-not-"fix" note).

**Partly rejected**

- **8 — rename `Pinned` to `Starred`/`Favourite`.** The collision is real (this repo already says
  "pinned" for model binding) and the critic is right that a persisted member name is cheap now
  and awkward later. But those uses are prose and comments only, never a member name, and they
  live on a different type. "Pin" is the user's own word and the universal term for this
  affordance; a private coinage would be worse for anyone searching the code. **Name kept; the
  ambiguity is now recorded in the constraints and required in the ADR.**

## Divergence from the plan (recorded at execution)

The plan was followed step for step. Six deviations, all small:

1. **`Agent42.Agents.ConversationInfo` was NOT added to `GrainStorageSerializerTests.PersistedTypeNames`**
   (Step 5 said "if it is not already there"). That list is documented as *"Every persisted state root"*
   and contains only roots — nested persisted types (`ReminderInfo`, `AttachmentRef`, …) are absent by
   the same convention, and are covered instead by golden-document deserialization. Adding one nested
   type would have made the list's own summary false. `ConversationInfo`'s exact `$type` string is pinned
   by the new `ConversationIndexDocument_WrittenBeforePinning_StillDeserializes_AsUnpinned` fixture,
   which is the coverage the plan was after.
2. **`ShareLinkAssetsTests` needed a two-line edit the plan did not anticipate.** It slices
   `buildConvRow`'s body via `Body(app, "function buildConvRow(conv)")`; Step 6's new `hoisted` parameter
   changes that declaration string, so both call sites were updated to
   `"function buildConvRow(conv, hoisted)"`. Without it two existing tests fail — the plan's "existing
   call sites pass nothing and are unchanged" held for the JS, not for the source-text guard.
3. **`PinConversation_Unpin_RestoresRecencyOrder` needed the same forced-timestamp treatment as the grain
   test.** The plan flagged the same-clock-tick tie only for AC2 (`ConversationIndexTests`), but the API
   test makes an identical recency claim and `DateTimeOffset.UtcNow` is coarse on Windows. A 20 ms delay
   between the two conversation starts makes the ordering unambiguous, with the reasoning in a comment.
4. **The API pin tests assert RELATIVE order, not absolute index.** `AgentApiTests` shares one
   conversation index across the class, so a sibling test's leftover pinned chat would otherwise decide
   the outcome. They compare `FindIndex` of their own two ids.
5. **`SetPinnedAsync` also short-circuits when the value is unchanged**, not only when the id is unlisted.
   The plan asked for "write only when the value actually changed"; both conditions collapse into the one
   guard that skips `WriteStateAsync`.
6. **Step 8's touch leg needed a different CDP lever than expected.** `Emulation.setEmulatedMedia` with
   `hover`/`pointer` features does **not** flip `matchMedia("(hover: none)")` in Chrome 151 and produced
   a false negative (all three actions reading `opacity: 0`). `Emulation.setTouchEmulationEnabled` plus
   mobile device metrics is what actually applies the media query. The leg then passed, and was
   strengthened beyond the plan: a pure `Input.dispatchTouchEvent` tap (touch events only, no mouse) on
   the pin control was confirmed to pin the chat server-side.

Everything else — the record member, the grain, the endpoint, the two-pass render, the badge, the CSS
including the `@media (hover: none)` block, the cache bump, and all fourteen ACs — landed as planned.
Measured Step 8 outcomes are recorded in `.agentic/decisions/0045-pinned-conversations.md`.
