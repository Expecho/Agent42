# Plan — custom chat groups in the sidebar

planned-against: main @ 534033b

## Overview

Let the user file conversations into their own named groups in the transmissions sidebar.
Today the only grouping is automatic and implicit: agent-bound chats collect under the agent
that produced them. There is no way to say "these four chats are Work". Groups are user-created,
user-named, user-ordered, and a chat belongs to at most one.

This is the second feature to reorder the sidebar after ADR 0045's pinning, and it lands on the
same load-bearing trap: `renderSidebar` does ONE walk that places an agent group at its
most-recent member, so anything that hoists rows out of that walk must hoist them *out*, never
sort them *within*.

## User decisions taken in the interview (do not relitigate)

1. **First-class group entities**, not a free-text label per chat. A group is
   `ChatGroup(Id, Name, Order)` living in `ConversationIndexState`; a conversation carries a
   nullable `GroupId`. Rejected: a denormalized `GroupName` string on `ConversationInfo` (cheaper,
   but an empty group cannot exist and there is nowhere to hang a manual order).
2. **A custom group wins over the agent group.** A grouped agent chat renders inside its custom
   group, is skipped by the agent-grouping walk, and **keeps its `.conv__agent` badge**. See
   "Badges" below — this is not a one-line change and it has its own AC.
3. **HTML5 drag-and-drop on desktop; a "Move to group…" dialog on touch.** See "Refinement" — it
   ships as an always-present row action plus a mouse-pointer-only drag.
4. **A "+" control in the transmissions header creates a group**; each custom group header carries
   rename and delete actions. Deleting a group **ungroups its chats, never deletes them**.
5. **Manual group order.** Custom groups render as a stable block in the user's chosen sequence,
   after the pinned block and before the recency walk. Group headers are draggable to reorder.
   `ChatGroup.Order` is therefore real and used, not vestigial.

### Refinement to decision 3 — carry it, don't re-decide it

The interview option said "on touch, a Move-to-group item added to the existing long-press
reveal". Implement that as a **fourth row action glyph present on every row at every width**:

- A touch-only glyph needs a device test. A **hybrid laptop** (touchscreen + mouse) would be
  classed as desktop and get drag-and-drop and *no* move action — yet its long-press reveal is
  alive, because `wireRowReveal` ignores only `e.pointerType === "mouse"` (`sidebar.js:177`). The
  user would reveal three actions and find no way to file the chat.
- An always-present glyph rides the reveal machinery that already exists (hover on desktop,
  ADR 0052 long-press on touch) and adds **no new gesture**. Drag-and-drop is then a desktop
  *accelerator* layered on top, which is the right relationship.

Drag-and-drop moves a chat **into** a group only. Clearing is done from the dialog ("No group");
there is deliberately no "drag back to the ungrouped area" drop zone — the target would be an
unlabelled region of the list.

## Affected areas

Verified against the tree at 534033b.

**Agents (state + grain)**
- `src/Agent42.Agents/Conversations/IConversationIndexGrain.cs`
- `src/Agent42.Agents/Conversations/ConversationIndexGrain.cs`

**Backend (API)**
- `src/Agent42.Backend/Conversations/AgentApi.ChatGroups.cs` — **new** partial
- `src/Agent42.Backend/Conversations/ChatGroupContracts.cs` — **new**
- `src/Agent42.Backend/Api/AgentApi.cs`

**Frontend**
- `src/Agent42.Backend/wwwroot/js/groups.js` — **new**
- `src/Agent42.Backend/wwwroot/js/sidebar.js`
- `src/Agent42.Backend/wwwroot/js/dom.js`
- `src/Agent42.Backend/wwwroot/js/main.js`
- `src/Agent42.Backend/wwwroot/index.html`
- `src/Agent42.Backend/wwwroot/styles.css`
- `src/Agent42.Backend/wwwroot/sw.js`

**Tests — new**
- `tests/Agent42.Tests/ChatGroupApiTests.cs`
- `tests/Agent42.Tests/ChatGroupAssetsTests.cs`

**Tests — existing files that MUST be edited (each one is a finding the critic caught; none is optional)**
- `tests/Agent42.Tests/GrainStorageSerializerTests.cs` — two new tests (backward + round-trip)
- `tests/Agent42.Tests/SerializationCompatibilityTests.cs` + `tests/Agent42.Tests/GoldenStates.cs` —
  the binary-path twin ADR 0045 added at `SerializationCompatibilityTests.cs:151` / `GoldenStates.cs:51`
- `tests/Agent42.Tests/EasyAuthExclusionTests.cs` — negative rows in
  `Exclusions_DoNotCoverAnyAuthenticatedPath` (the ADR 0045/0062/0064 convention)
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — `AgentApiParts()` + `FrontEndWiredModules()` rows
- `tests/Agent42.Tests/PwaAssetsTests.cs` — cache name
- `tests/Agent42.Tests/PinChatAssetsTests.cs` — **`HoistedRow_CarriesTheAgentBadge` asserts the
  literal `hoisted && conv.agentId`, which this feature changes.** Updating it is a deliberate,
  reviewed evolution of the guard, not a weakening — the replacement must be at least as strict.
- `tests/Agent42.Tests/RowActionsRevealAssetsTests.cs` — verify (do not weaken) its whole-stylesheet
  `Assert.DoesNotContain("touch-action:", css)` and the shared `Assert.DoesNotContain(":has(", css)`

**Docs**
- `.agentic/decisions/0073-custom-chat-groups.md` — **new**
- `.agentic/gotchas.md` — one new entry; amend the ADR 0045 hoist entry to point at it
- `CLAUDE.md` — one repo-notes bullet

## Context & constraints

### The governing gotcha — read it first
`.agentic/gotchas.md`, "A pinned agent chat must be HOISTED out of its group, never sorted into
it". Pass 2 and pass 3 below are that entry applied a second time. Read the entry immediately
after it ("Hiding a hover-reveal on touch") before adding the fourth glyph: the unrevealed state
needs `pointer-events: none`, or an invisible control is still tappable.

### Governing ADRs
`0004` (the 2 MB item — why groups are capped), `0016` (continuous agents: exactly one
conversation — the badge case below is their *normal* state), `0032` (the IndexedDB read mirror),
`0039`/`0040` (flat namespace, feature folders, the composition-root failure mode, and the Cosmos
`$type` scar), `0041` (ES modules, `sw.js` CACHE bump, orphan modules invisible to the guards),
`0045` (pinning; the two-pass render), `0052` (long-press reveal), `0055` (one Orleans cluster per
test class). No ADR is contradicted and **no supersession step is needed** — only the new ADR 0073.

**ADR 0073's `extends:` list is `[0045]`, not `[0003, 0045]`.** ADR 0003 is entirely about
`BindAgentAsync` pinning a persona and never mentions the sidebar; agent *grouping* is justified
only by the comment at `IConversationIndexGrain.cs:12–15`. Cite that comment, not 0003.

### One ADR 0045 rejection this plan does NOT reverse — ADR 0073 must say so
ADR 0045 §4 rejected **manual drag ordering of conversations** ("a much larger feature needing a
stored sort key and DnD"). This introduces a stored sort key and DnD at the **group** level only;
chats inside a group still sort by recency and the flat list is still recency-ordered. The 0045
rejection stands untouched. Likewise `0045-pinned-conversations.md:219` rejects *an empty-group
placeholder for a fully-pinned agent group* — rendering **empty custom groups** is a different
thing (a first-class entity the user created, versus a derived group with no members), and ADR 0073
must say that explicitly or the next reader reads it as a quiet reversal.

### Investigation findings the executor cannot cheaply re-derive

1. **`GET /api/conversations` returns a bare JSON array, deserialized as `List<ConversationInfo>`
   at 12 call sites** across `tests/Agent42.Tests/AgentApiTests.cs` and `AgentMcpServerTests.cs`.
   Do **not** reshape it into `{conversations, groups}`. Groups get their own endpoint, and the
   ADR 0032 cache key `conversations` keeps its shape (a mirror written before this ships must
   still render).

2. **The golden pre-pin document already predates groups too.**
   `GrainStorageSerializerTests.PrePinConversationIndexStateJson` (line 376) carries no `Pinned`
   key, no `Groups` key and no `GroupId` — verified. Reuse that committed constant for the
   missing-key direction and widen its doc comment to name both ADRs. A fixture the test writes for
   itself agrees with whatever the code does and can never fail.
   **That proves only the backward direction.** ADR 0039's scar is about what gets *written* — the
   `$type` names for `List<ChatGroup>` and `Agent42.Agents.ChatGroup`. The house pattern is **two
   tests**: see `WebDocumentNamesAndUrls_RoundTripWithoutSwapping` (line 130) and
   `SeededFetchableUrls_RoundTripWithoutSwapping` (line 173) sitting beside their backward-compat
   twins. Write both.

3. **`ConversationIndexState` is a class with property initializers**, so a missing `Groups` key
   binds to `= []`. `ConversationInfo` *is* a positional record, which is why `GroupId` must be
   **trailing and optional** (`= null`) — the ADR 0045 `[Id(5)]` precedent.

4. **The collection-expression trap** (`.agentic/gotchas.md:2671`): a collection expression into
   `IReadOnlyList<T>` written from **inside** a grain is unreadable from Cosmos.
   `ReorderGroupsAsync` is exactly "state a grain constructs inside itself and writes directly",
   and `GetGroupsAsync()` returns `IReadOnlyList<ChatGroup>` one line away. Persisted state must be
   `List<ChatGroup>`; only the **return** may be `IReadOnlyList`.

5. **Route precedence.** `AgentApi.cs`'s summary records the house rule: a literal segment out-ranks
   a `{param}`, which is why `PUT /api/default-model` exists rather than `/api/providers/default`.
   Group ids are server-minted so `…/order` could not actually collide — follow the rule anyway and
   use **`PUT /api/conversation-group-order`**.
   The summary's **"all 58 route registrations" figure is itself suspect**: 57 `app.Map*` calls sit
   across the sixteen `AgentApi.*.cs` partials, and 58 holds only if ADR 0070's `MapMcp` route is
   counted (it is deliberately not an `AgentApi` part). **Recount it, correct it, and state the
   counting rule in the comment** rather than adding six to a number nobody can reproduce.

6. **`ProjectLayoutTests` has three name tables that silently pass if forgotten**: `AgentApiParts()`
   (line 162), `FrontEndWiredModules()` (line 237), `ServiceWorkerShell_ListsExactlyTheModuleDirectory()`
   (line 295 — both directions; a *stale* SHELL entry makes `addAll` reject and the SW never
   activates, taking down ADR 0032/0023/0028).

7. **CSS ordering guards use a plain `IndexOf` that does not skip comments.**
   `PreferencesLayoutAssetsTests.PrefsCss_IsOrderedAgainstBothConstraints` (line 137) requires
   `.agent-dialog {` before `.prefs {` and requires the prefs `@media (max-width: 760px)` block not
   to be the first one; `TopbarActionsAssetsTests.TopbarBrandAndMenu_AreMobileOnly` (line 185) reads
   that first block. **Add no new `@media (max-width: 760px)` block.** Two whole-stylesheet negative
   assertions are also live and are natural to trip when writing drop-target and drag CSS:
   `Assert.DoesNotContain(":has(", css)` and `Assert.DoesNotContain("touch-action:", css)`.

8. **Row action geometry.** `.conv` reserves `padding-right: 82px` for three absolutely-positioned
   actions at `right: 7 / 31 / 55` (`styles.css:259,296,337,377`). A fourth goes at `right: 79` and
   the padding becomes `106px`. The `@media (hover: none)` block lists all three actions twice (once
   for `pointer-events: none`, once for `.is-revealed`) — the new glyph must appear in **both**, and
   needs its own `@media (hover: hover)` reveal block plus ungated `:focus-visible` and `:hover`.

9. **`.agents__head` / `.agents__add` (`index.html:55–59`, `styles.css:561–591`) is the exemplar**
   for the transmissions "+" control. The `.conversations` nav is label → `#searchInput` →
   `#conversationList` (`index.html:63–68`); the new head **replaces the bare label and stays above
   the search box**, so the search field keeps its position.

10. **`#shareLinkDialog` (`index.html:602–631`) + `sharelinks.js` is the dialog exemplar**:
    `<dialog class="agent-dialog">` wrapping `<form class="agent-form" method="dialog">`, opened
    with `showModal()`, closed by an explicit `close()` on a `type="button"` control, every button
    wired in the module's `wire*()`.

11. **Minimal API infers every mapped endpoint's parameters up front, across ten `MapAgentApi` call
    sites** (two hosts + eight test fixtures) — a parameter one host does not register breaks
    routing for that **whole host**. Inject only `IGrainFactory` and `HttpContext`. **No host gains
    a mandatory registration.**

12. **`groups.js` ↔ `sidebar.js` is a deliberate import cycle and that is fine.** ADR 0041 §5
    permits cycles and several already ship — do not reach for a setter-slot to dodge it. Every
    binding is used at call time, never at module-evaluation time. **`buildConvRow` must become an
    exported function** (`sidebar.js:48`, currently module-private), and
    `ProjectLayoutTests.EveryNamedImport_IsActuallyExportedByItsTargetModule` is the guard that
    catches a missed export — the one static error that yields a *blank* app.

13. **`renderSidebar` is called from four other modules** (`agents.js:121`, `conversations.js:35,117`,
    `main.js:47`, `search.js:45,59`) and none of them change: `groups` is module state that
    `renderSidebar` reads, exactly as `conversations` already is.

14. **`TouchAsync` must stay untouched, and that must be pinned.** It rewrites the entry with
    `conversations[existing] with { UpdatedAt = ... }` (`ConversationIndexGrain.cs:34`), so `GroupId`
    rides along for free — which is precisely why ADR 0045 pinned the same property for `Pinned`
    with a regression test (`CLAUDE.md`: "`TouchAsync` is **untouched** — its `with` expression
    carries `Pinned` forward for free, which is exactly why a regression test pins that"). A chat
    filed into a group and then used must not lose its filing.

15. **Test-class shapes a cold session will otherwise get wrong.** `ChatGroupApiTests` must be
    `public sealed class ChatGroupApiTests(AgentApiFixture fixture) : SharedClusterTests<AgentApiFixture>(fixture)`
    (`AgentApiTests.cs:20`). `ChatGroupAssetsTests` must carry `[Collection("StaticAssets")]` and
    take `StaticAssetsServerFixture` (`StaticAssetsServerFixture.cs:141`). The gotcha at
    `.agentic/gotchas.md:2474` — xUnit v2 runs `IAsyncLifetime` **per test**, so a cluster built
    there is deployed per test — is triggered by exactly the default an executor would reach for.

### Scope guards
- **Incognito chats (ADR 0034) are structurally ungroupable** — never touched into the index, so
  there is no entry to carry a `GroupId`. Same as pinning. No code; say so in the ADR.
- **Groups are not surfaced in the public share payload** (ADR 0035). `AgentApi.Sharing.cs:62` reads
  `ConversationInfo` for a title only; it must stay that way.
- **Search hides groups**: `renderSidebar`'s search branch returns before any pass — unchanged, agent
  groups already vanish during a search.
- **No new egress, container, secret, Bicep change, `excludedPaths` entry, Cosmos container, grain
  or package.** Every endpoint is Easy-Auth gated and scoped by the grain key.

## Steps

Steps 1 and 3 touch disjoint files and are **parallelizable** (worktree isolation required if run
concurrently). Everything else is ordered.

### Step 1 — State and grain
Files: `src/Agent42.Agents/Conversations/IConversationIndexGrain.cs`, `ConversationIndexGrain.cs`.

- `ChatGroup(Id, Name, Order)` — `[GenerateSerializer]`, `[Alias("Agent42.Agents.ChatGroup")]`,
  `[property: Id(0..2)]`, flat `Agent42.Agents` namespace (ADR 0039 — folders and namespaces
  deliberately disagree).
- `ConversationInfo` gains trailing `[property: Id(6)] string? GroupId = null`, with the ADR 0045
  no-migration comment shape.
- `ConversationIndexState` gains `[Id(1)] public List<ChatGroup> Groups { get; set; } = [];`
  (`List`, not a collection expression into `IReadOnlyList` — finding 4).
- `TouchAsync` is **not modified**. Its `with` expression carries `GroupId` forward for free.
- Grain methods, all following `SetPinnedAsync`'s discipline — **no write unless something actually
  changed**, and an unlisted/unknown id is a silent no-op, never an error (an unlisted id must not
  be distinguishable from an unchanged one):
  - `Task<ChatGroup?> CreateGroupAsync(string name)` — trims, truncates to the **existing
    `MaxTitleLength` constant** (do not add a second constant holding the same literal; two
    constants with one value is what *permits* drift), appends at `Order = Groups.Count`, returns
    the new group so the client gets its minted id. Returns **null** at the cap.
  - `const int MaxGroups = 50` — bounds `ConversationIndexState` against ADR 0004's 2 MB item.
    Far past human use, ~10 KB serialized; a scripted client must not grow this without limit.
  - `Task RenameGroupAsync(string groupId, string name)` — trim + truncate; unknown id no-op;
    unchanged name writes nothing.
  - `Task DeleteGroupAsync(string groupId)` — removes the group **and clears `GroupId` on every
    member in the same write**. Chats are never deleted. Unknown id no-op.
  - `Task ReorderGroupsAsync(IReadOnlyList<string> groupIds)` — assigns `Order` by position for ids
    it recognises; unknown ids ignored; groups the caller omitted keep their relative order **after**
    the named ones. A stale client list must never drop a group.
  - `Task SetGroupAsync(string conversationId, string? groupId)` — a non-null `groupId` naming no
    existing group is a **no-op**, not a silent clear: clearing is what `null` means, and conflating
    them lets a stale client wipe a filing the user still has.
  - `Task<IReadOnlyList<ChatGroup>> GetGroupsAsync()` — ordered by `Order`.
- `GetConversationsAsync` ordering is **unchanged**. The server still owns recency and pinning;
  group *placement* is a client render concern the server cannot express, because an empty group has
  no member to sit at. Partitioning by `groupId` in JS is not re-sorting — the ADR 0045 rule stands.

Verify: `dotnet build Agent42.slnx` (S3776 gate) then
`dotnet test Agent42.slnx --filter "FullyQualifiedName~GrainStorageSerializerTests|FullyQualifiedName~SerializationCompatibilityTests"`.

### Step 2 — Endpoints
Files: `AgentApi.ChatGroups.cs` (new), `ChatGroupContracts.cs` (new), `Api/AgentApi.cs`.

Six routes, all in the new partial, all Easy-Auth gated (**no `excludedPaths` entry** — that list
stays at six, and negative rows in `EasyAuthExclusionTests` prove it):
- `GET  /api/conversation-groups` → `IReadOnlyList<ChatGroup>`
- `POST /api/conversation-groups` `{name}` → `200` with the created group; `400` blank name; `409` at cap
- `PUT  /api/conversation-groups/{groupId}` `{name}` → `204` (`400` blank)
- `DELETE /api/conversation-groups/{groupId}` → `204`
- `PUT  /api/conversation-group-order` `{groupIds}` → `204`
- `PUT  /api/conversations/{conversationId}/group` `{groupId}` → `204` (null clears)

Register `MapChatGroupEndpoints(app);` (16 → 17 groups), add the `AgentApiParts()` row, and recount
+ correct the route figure in the `AgentApi.cs` summary per finding 5.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ChatGroupApiTests|FullyQualifiedName~ProjectLayoutTests|FullyQualifiedName~EasyAuthExclusionTests"`.

### Step 3 — Markup, DOM exports and CSS
Files: `index.html`, `js/dom.js`, `styles.css`.

- `<nav class="conversations">`: replace the bare `<p class="conversations__label">` with a
  `.conversations__head` holding that same `<p>` plus
  `<button class="conversations__add" id="addGroupBtn" type="button" aria-label="New group">+</button>`.
  The head stays **above `#searchInput`**, which keeps its position.
- Two dialogs on the `#shareLinkDialog` shape:
  - `#groupNameDialog` (create **and** rename; `#groupNameTitle`, `#groupNameInput`,
    `#groupNameSaveBtn`, `#groupNameCancelBtn`)
  - `#moveToGroupDialog` (`#moveToGroupConvTitle`, `#moveToGroupList` — radios injected at open
    time, including "No group" — `#moveToGroupSaveBtn`, `#moveToGroupCancelBtn`)
- `dom.js`: one `const` per id, matching the file's shape.
- `styles.css`, exact anchors at 534033b: `.conversations__head` / `.conversations__add` beside
  `.conversations__label` (~line 184), copying `.agents__head` / `.agents__add` (561–591) in shape;
  `.conv__move` after the `.conv__pin` family (~line 410) so the four action rules read in geometric
  order; group rules after `.conv-group.is-collapsed .conv-group__body` (~line 549). New rules:
  `.conversations__head`, `.conversations__add`, `.conv__move` (+ `(hover: hover)` reveal,
  `:focus-visible`, `:hover`), `.conv` padding-right `82px` → `106px`, `.conv__group` badge (reuse
  the `.conv__agent` treatment with a non-amber border so two badges on one row are
  distinguishable), `.conv-group__actions` / `.conv-group__action`, `.conv-group.is-drop-target`,
  `.conv-group__handle`. Add `.conv__move` to **both** lists in the existing `@media (hover: none)`
  block. **No new `@media (max-width: 760px)` block; no `:has(`; no `touch-action:`.**

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AssetsTests"` — this step alone must
not break `PinChatAssetsTests`, `RowActionsRevealAssetsTests`, `PreferencesLayoutAssetsTests` or
`TopbarActionsAssetsTests`.

### Step 4 — `groups.js`, the three passes, and the mandatory extraction

Files: `js/groups.js` (new), `js/sidebar.js`, `js/main.js`, `sw.js`.

#### 4a — the name collision that would ship a blank sidebar
`renderSidebar` **already declares `const groups = new Map()`** at `sidebar.js:310`. A
function-scoped `const` shadows the module import for the whole body, so a module-level
`import { groups }` read anywhere in that function hits the temporal dead zone —
`Cannot access 'groups' before initialization` — and the sidebar paints nothing. Every frontend
guard is source-text, so **no test in this repo would catch it.**
**Rename the existing local to `agentGroups`**, and keep the module export named `groups`.

#### 4b — extraction is REQUIRED, not conditional
`renderSidebar` measures **exactly 15** cognitive complexity at HEAD — the threshold. A third pass
plus a widened empty-state guard trips `dotnet build` (S3776 at `error`). Extract **the three passes
only**, as three named functions in `sidebar.js`:
`renderPinnedBlock(...)` (returns `lastPinned`), `renderCustomGroups(...)`, `renderAgentWalk(...)`.

**Keep inside `renderSidebar` itself**: `clearReveal()`, `conversationList.innerHTML = ""`, the
search branch, the empty-state guard, the three calls, and the `.is-pinned-last` divider marker.
That is not stylistic — two existing tests assert against `Body(app, "export function renderSidebar()")`,
which returns the brace-balanced body only:
- `PinChatAssetsTests.cs:88` — `Assert.Contains("conversationList.lastElementChild !== lastPinned", render)`
- `RowActionsRevealAssetsTests` (`Reveal_IsClearedOnRerenderAndOnOutsidePointerDown`) — `Assert.Contains("clearReveal();", render)`

Both stay green under this split, and AC12/AC13 anchor on the **call order** in `renderSidebar`'s
body plus the skip predicate in `Body(app, "function renderAgentWalk(")`.

#### 4c — the three passes
1. **Pinned block**, unchanged in placement.
2. **Every group in `groups` order, including empty ones** (valid drop targets — what first-class
   entities bought), each a collapsible `.conv-group` holding its unpinned members.
3. The existing agent-grouping walk, skipping
   `conv.pinned || (conv.groupId && knownGroupIds.has(conv.groupId))`. A `groupId` naming a group
   that no longer exists **falls through to pass 3** and renders ungrouped — self-healing.

Empty-state guard becomes `conversations.length === 0 && groups.length === 0`.

#### 4d — Badges: one boolean cannot carry two facts
`buildConvRow(conv, hoisted)` appends the agent badge under `hoisted && conv.agentId`
(`sidebar.js:70`). Two independent facts now exist:
- **agent badge** ⇔ the row is rendered outside its *agent* group ⇔ pinned-block **or** inside a
  custom group;
- **group badge** ⇔ the row is rendered outside its *custom* group ⇔ pinned-block only.

Signature becomes `buildConvRow(conv, hoisted, grouped)`; agent badge under
`(hoisted || grouped) && conv.agentId` with the unchanged `conv.agentName || "Agent"` fallback
(keying on `agentName` is the bug the badge exists to prevent); group badge under
`hoisted && conv.groupId`. This is load-bearing, not cosmetic:
`0045-pinned-conversations.md:57` records that a fully-pinned agent group disappearing is correct
*because of* the badge, and for a **continuous** agent (ADR 0016) that state is normal — filing a
continuous agent's one chat into a custom group reproduces it exactly.
`PinChatAssetsTests.HoistedRow_CarriesTheAgentBadge` pins the old literal and must be updated to the
new expression; the replacement must be at least as strict.

#### 4e — drag gating: use the rule the file already has, not a media query
Do **not** gate on `matchMedia("(pointer: fine)")`. It reports the *primary* pointer, so a
touchscreen laptop — the exact device the Refinement paragraph is built on — matches it, leaving
the native touch-drag-versus-400 ms-reveal race reachable. And it is the wrong exemplar:
`canNativeShare` is a *capability* detect, and `.agentic/gotchas.md:2253` records that ADR 0047
deliberately chose that over a media query.

Instead decide **per interaction, from the same source of truth `wireRowReveal` already uses**: in
the existing `pointerdown` handler set `li.draggable = (e.pointerType === "mouse")`. A mouse press
enables the drag; a touch press disables it and starts the reveal timer. One rule, no device class,
correct on hybrids. `draggable` defaults to `false` in the markup.
**Honest gap:** that `draggable` is re-read at drag initiation (after `pointerdown`) is an
assumption — verify it in the DevHost browser leg and record the measured result in ADR 0073. The
claim that Android Chrome long-press-drags a `draggable` element is likewise **unmeasured**; treat
it as the hazard being defended against, not as an established fact.

#### 4f — `groups.js` contents
`export let groups`, `refreshGroups()` (fetch → render → `cachePut("groups", …)`, with
`refreshConversations`'s offline-fallback shape), `setGroups()`, `buildChatGroup(group, convs)`,
expanded-set persistence under `agent42.sidebar.expandedGroups` (persist the **expanded** set, as
`expandedAgents` does, so a new group starts collapsed but an explicit expand survives),
`openMoveToGroup(convId, currentGroupId)`, the create/rename/delete flows, the drag wiring, and
`wireGroups()`.

**Drag listeners must be delegated on `conversationList`, wired once in `wireGroups()`** — never
per-row. `wireGroups()` runs from `main.js` before any row exists (`main.js:57–74` precede
`refreshConversations()` at line 81), and `renderSidebar` rebuilds every row on every call.

#### 4g — wiring
`main.js`: `wireGroups();` in the wiring block, `refreshGroups();` in init, and `hydrateFromCache`
extended to read the `groups` key. **`setGroups(...)` must run before the `renderSidebar()` call at
`main.js:47`**, or the offline first paint shows every grouped chat in the recency walk and then
reflows.

`sw.js`: add `"/js/groups.js"` to `SHELL` and bump `CACHE` to `agent42-shell-v37`.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ProjectLayoutTests|FullyQualifiedName~PwaAssetsTests|FullyQualifiedName~PinChatAssetsTests|FullyQualifiedName~RowActionsRevealAssetsTests"`.

### Step 5 — Frontend asset guards
`tests/Agent42.Tests/ChatGroupAssetsTests.cs` (new, `[Collection("StaticAssets")]`) plus the
`PwaAssetsTests` cache-name line. Source-text guards only — that is what this suite can prove, and
ADR 0073 must say so rather than implying behavioural coverage.

### Step 6 — Docs
`.agentic/decisions/0073-custom-chat-groups.md` (`extends: [0045]`), a new `gotchas.md` entry
covering the `groups` shadowing trap, the mandatory extraction and what it must leave behind, the
two-facts badge rule and the per-interaction drag gate; an amendment note on the ADR 0045 hoist
entry pointing at it; one `CLAUDE.md` repo-notes bullet; and move this spec to
`.agentic/specs/completed/`.

### Step 7 — Gates
```
dotnet build Agent42.slnx
dotnet test Agent42.slnx
dotnet format Agent42.slnx --verify-no-changes
dotnet test Agent42.slnx --collect:"XPlat Code Coverage"
node .agentic/scripts/check-new-code-coverage.mjs origin/main
```
The `--collect` run is **required** before the gate script — it reads the cobertura report that run
produces (`.agentic/scripts/check-new-code-coverage.mjs:9–12`); a plain `dotnet test` leaves it
stale or absent and the gate reports nothing useful.

Then the offline browser leg: `dotnet run --project tools/Agent42.DevHost`, drive
http://localhost:61326 (headless Chrome over CDP is the house pattern) — create a group, file a chat
by drag **and** by dialog, reorder, rename, delete-and-confirm-ungrouped, an empty group as a drop
target, both themes. Record measured values in ADR 0073, including the 4e assumption. If no browser
driver is available, say so and record it as an **unverified residual** (the ADR 0043 treatment) —
do not claim a pass that did not happen.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A group can be created and is listed | `CreateGroup_ReturnsTheGroup_AndItIsListed` | `ChatGroupApiTests.cs` |
| AC2 | Blank names rejected, long names truncated, on **create and rename** | `CreateGroup_BlankName_Returns400`, `CreateGroup_TrimsAndTruncatesTheName`, `RenameGroup_BlankName_Returns400`, `RenameGroup_TruncatesTheName` | `ChatGroupApiTests.cs` |
| AC3 | Groups are capped so state stays bounded (ADR 0004) | `CreateGroup_BeyondTheCap_Returns409` | `ChatGroupApiTests.cs` |
| AC4 | Rename works; unknown id and unchanged name are silent no-ops | `RenameGroup_ChangesTheName`, `RenameGroup_UnknownId_Returns204`, `RenameGroup_UnchangedName_IsANoOp` | `ChatGroupApiTests.cs` |
| AC5 | Deleting ungroups its chats and deletes none; unknown id no-ops | `DeleteGroup_UngroupsItsConversations_AndNeverDeletesThem`, `DeleteGroup_UnknownId_Returns204` | `ChatGroupApiTests.cs` |
| AC6 | A chat can be filed and unfiled | `AssignConversation_SetsGroupId_AndNullClearsIt` | `ChatGroupApiTests.cs` |
| AC7 | An unknown group id is a no-op, not a silent clear | `AssignConversation_UnknownGroup_IsANoOp` | `ChatGroupApiTests.cs` |
| AC8 | An unlisted conversation id is an indistinguishable no-op | `AssignConversation_UnknownConversation_Returns204` | `ChatGroupApiTests.cs` |
| AC9 | Manual order applies and survives a stale client list | `ReorderGroups_AppliesThePositionalOrder`, `ReorderGroups_IgnoresUnknownIds_AndKeepsUnlistedGroups` | `ChatGroupApiTests.cs` |
| AC10 | **A later turn does not lose a chat's filing** (`TouchAsync` carries `GroupId`) | `TouchAsync_PreservesTheGroupAssignment` | `ChatGroupApiTests.cs` |
| AC11 | Pinning still works unchanged alongside grouping | existing `PinConversation_*` (must stay green) | `AgentApiTests.cs` |
| AC12 | **No migration, backward**: a pre-groups document reads as ungrouped | `ConversationIndexDocument_WrittenBeforeGrouping_StillDeserializes_AsUngrouped` | `GrainStorageSerializerTests.cs` |
| AC13 | **No migration, forward**: a groups-carrying state round-trips through the Cosmos JSON path | `ChatGroups_RoundTripWithoutSwapping` | `GrainStorageSerializerTests.cs` |
| AC14 | The binary path carries the new fields too (ADR 0045's secondary assertion) | extend the `ConversationIndexState` case | `SerializationCompatibilityTests.cs`, `GoldenStates.cs` |
| AC15 | The six new routes are NOT Easy-Auth excluded | new rows in `Exclusions_DoNotCoverAnyAuthenticatedPath` | `EasyAuthExclusionTests.cs` |
| AC16 | Groups render between the pinned block and the agent walk | `RenderSidebar_CallsThePassesInOrder` (call order in `renderSidebar`'s body) | `ChatGroupAssetsTests.cs` |
| AC17 | The agent walk skips pinned **and** grouped rows (no double render) | `AgentWalk_SkipsPinnedAndGroupedRows` (over `Body(app, "function renderAgentWalk(")`) | `ChatGroupAssetsTests.cs` |
| AC18 | A dangling group id falls back to the ungrouped walk | `UnknownGroupId_FallsBackToTheUngroupedWalk` | `ChatGroupAssetsTests.cs` |
| AC19 | **A grouped agent chat still carries its agent badge** (decision 2; ADR 0016's normal case) | `GroupedAgentChat_CarriesTheAgentBadge` | `ChatGroupAssetsTests.cs` |
| AC20 | A pinned grouped row carries a group badge | `HoistedRow_CarriesTheGroupBadge` | `ChatGroupAssetsTests.cs` |
| AC21 | The updated agent-badge rule is still keyed on `agentId`, not `agentName` | updated `HoistedRow_CarriesTheAgentBadge` | `PinChatAssetsTests.cs` |
| AC22 | An empty group renders and is a drop target | `EmptyGroups_AreRenderedAndAreDropTargets` | `ChatGroupAssetsTests.cs` |
| AC23 | The move action is on every row and opens the dialog | `MoveAction_IsWiredIntoConvRow_AndOpensTheDialog` | `ChatGroupAssetsTests.cs` |
| AC24 | Row drag is enabled only for a **mouse** pointer, from the same rule as the reveal | `RowDrag_IsEnabledOnlyForMousePointers` | `ChatGroupAssetsTests.cs` |
| AC25 | Drag listeners are delegated, not per-row | `DragWiring_IsDelegatedOnTheList` | `ChatGroupAssetsTests.cs` |
| AC26 | The move glyph is inert unrevealed and revealed on touch | `MoveAction_IsRevealedOnTouch_AndInertWhenUnrevealed` | `ChatGroupAssetsTests.cs` |
| AC27 | The row reserves room for four actions | `ConvPadding_ReservesRoomForFourActions` | `ChatGroupAssetsTests.cs` |
| AC28 | Deleting a group confirms first | `DeleteGroup_ConfirmsBeforeUngrouping` | `ChatGroupAssetsTests.cs` |
| AC29 | Groups are mirrored for offline and hydrate before the first paint | `Groups_AreMirroredForOffline`, `Groups_HydrateBeforeTheFirstRender` | `ChatGroupAssetsTests.cs` |
| AC30 | The module is wired from the composition root and cached | `FrontEndModule_ExportsItsEntryPoint_AndIsCalledFromTheCompositionRoot`, `ServiceWorkerShell_ListsExactlyTheModuleDirectory`, `ServiceWorker_CacheNameIsCurrent` | `ProjectLayoutTests.cs`, `PwaAssetsTests.cs` |
| AC31 | The endpoint group is registered from the composition root | `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` | `ProjectLayoutTests.cs` |

**Known coverage limit, to be stated plainly in ADR 0073:** every frontend assertion here is
source-text (the suite has no DOM). "Renders exactly once", the drag gesture, and the 4e
`draggable` timing assumption are provable only by the manual DevHost leg — the honest position ADR
0045 and ADR 0052 both took.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (534033b):

| Gate | Threshold | Measuring tool |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `dotnet test --collect:"XPlat Code Coverage"` then `node .agentic/scripts/check-new-code-coverage.mjs origin/main` |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time (`error` — blocks `dotnet build`) |
| `vulnerable_packages` | **0** | `dotnet list Agent42.slnx package --vulnerable --include-transitive` |
| `outdated_packages` | report | soft, CI-surfaced only |
| Branch policy | `main` protected | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

Coverage note: coverlet measures C# only, so the gate lands on the grain methods and the six
endpoints. AC1–AC10 are sized to cover every branch of all six grain methods **including the no-op
and unchanged arms** — the critic's count found `RenameGroupAsync` and `DeleteGroupAsync` branches
originally unclaimed, which is not rounding error against twelve new members.

Baseline at plan time: `dotnet build Agent42.slnx` on `main` @ 534033b is **green** (0 errors,
59 pre-existing warnings).

## Out of scope

- **Nested groups / subgroups.** One level only.
- **A chat in more than one group.** `GroupId` is singular by design.
- **Grouping agent groups**, or filing a whole agent group at once.
- **Drag-to-ungroup.** Clearing is a dialog choice; there is no unlabelled drop region.
- **Auto-scrolling the list while dragging near its edges.** A real polish gap on a long list;
  record it as a residual in ADR 0073 rather than shipping a half-built version.
- **Groups in the public share view (ADR 0035), in search results, or in `/logs`.**
- **Reordering chats *within* a group.** Recency wins inside a group, as everywhere else.
- **Any server-side reordering of `GetConversationsAsync`.** Deliberately untouched.
- **Changing the pinned block's precedence.** Pinned still hoists out of everything, groups included.

## Review

A `critic` pass ran against this plan at 534033b and returned **nine findings, four blocking**. All
nine were accepted; none was rejected. Where each landed:

| Finding | Resolution |
|---|---|
| **B1** `const groups` at `sidebar.js:310` shadows the module import → TDZ `ReferenceError`, invisible to every source-text guard | Step 4a: rename the local to `agentGroups`. This was a real bug in the first draft. |
| **B2** Decision 2's agent badge had no implementation path and no AC; one boolean cannot carry two facts | Step 4d: `buildConvRow(conv, hoisted, grouped)`; new AC19, AC20, AC21. |
| **B3** S3776 trips at **exactly 15** today, so extraction is certain — and extraction destroys AC12/AC13's evidence and two existing tests | Step 4b: extraction made mandatory *and* bounded — `clearReveal`, the search branch, the empty guard and the divider marker stay in `renderSidebar`, so `PinChatAssetsTests.cs:88` and `RowActionsRevealAssetsTests` stay green; AC16/AC17 re-anchored. |
| **B4** the coverage gate command cannot produce a result without `--collect` | Step 7 + the gate table now run `--collect:"XPlat Code Coverage"` first. |
| **S1** `TouchAsync` unnamed, though ADR 0045 pinned exactly this property | Finding 14 + AC10. |
| **S2** only the backward migration direction was proved; the `$type` write direction and the binary twin were missing | Finding 2 + finding 4 (collection-expression trap) + AC13, AC14. |
| **S3** no `EasyAuthExclusionTests` rows | AC15 + the file added to Affected areas. |
| **S4** `(pointer: fine)` does not fix hybrids and cites an exemplar ADR 0047 rejected | Step 4e rewritten to reuse `wireRowReveal`'s own `e.pointerType === "mouse"` rule; the unmeasured Android premise is now labelled as such; AC24 restated. |
| **S5** three uncovered grain branches against an 80% gate | AC2, AC4, AC5 expanded. |
| **S6** `buildConvRow` is private; the cycle and the drag-wiring reach were unnamed | Findings 12 + Step 4f (delegated listeners on `conversationList`). |
| **S7** test-class shapes unspecified despite citing ADR 0055 | Finding 15. |
| Minors (12 call sites not 8; the "58 routes" figure is itself wrong; ADR 0003 mis-cited; duplicate constant; `:has(`/`touch-action:` negative assertions; empty-group vs 0045's rejection; head position; `hydrateFromCache` ordering) | All folded into findings 1, 5, 7, 9, Step 1, Step 4g, and the ADR-0045-rejections section. |

## Divergence from the plan (filled in during execution)

Seven places where the implementation differs from the spec above. None changes the feature; each is
recorded because the spec was the cold handoff and a future reader should not have to re-derive them.

1. **`TouchAsync` was lightly refactored**, against the spec's "not modified". Its inline
   trim-and-truncate became a shared `Cap()` helper, because the spec also forbade a second constant
   holding `MaxTitleLength`'s value and the group names need the same cap. The behaviour is identical
   and — the part that actually mattered — the `with` expression is untouched, with AC10 now pinning
   that `GroupId` rides it.

2. **Four existing asset guards had to be updated, not two.** The spec named `PinChatAssetsTests` and
   `RowActionsRevealAssetsTests`; `ShareLinkAssetsTests` (two methods) and `SearchAssetsTests` (three)
   also anchor on `buildConvRow`'s signature, on the `sw.js` cache name, or on the "Pass 1"/"Pass 2"
   **prose comments** that the extraction moved out of `renderSidebar`. `SearchAssetsTests` was the
   instructive one: its ordering assertion had to be re-anchored on the **call** `= renderPinnedBlock();`
   rather than the bare name, because the extracted declarations now sit *above* `renderSidebar` and a
   bare-name search matched the declaration — which would have inverted the comparison and left the
   guard passing vacuously.

3. **A comment tripped a negative assertion.** `RowDrag_IsEnabledOnlyForMousePointers` asserts the
   fine-pointer media query is absent from the whole concatenated script, and the comment in
   `sidebar.js` *explaining why it is not used* quoted it verbatim. The comment was reworded rather than
   the assertion weakened — the documented "a plain `IndexOf` does not skip comments" trap, met live.

4. **The cap test needed its own principal.** `CreateGroup_BeyondTheCap_Returns409` fills a user's 50
   group slots, and the class shares one cluster (ADR 0055) with no guaranteed test order — so under the
   default principal it would have made every sibling `CreateGroup` fail depending on execution order.
   It drives `X-MS-CLIENT-PRINCIPAL-ID` instead, which is what `CurrentUser.ResolveUserId` reads.

5. **The assign route lives in the new partial**, not beside `/pin` in `AgentApi.Conversations.cs` as an
   earlier draft had it. All six routes in one part is what keeps `MapConversationEndpoints` clear of the
   S3776 ceiling — the same reason the part exists at all.

6. **The route-count comment in `AgentApi.cs` was wrong before this change.** The spec predicted this;
   the measurement is 57 `app.Map*` calls across the `AgentApi.*.cs` parts before, 63 after. The comment
   now states its counting rule.

7. **Line endings.** Several files were written LF while the working tree is CRLF (`core.autocrlf=true`,
   no `.gitattributes`), which `dotnet format` rejects as whitespace errors. All touched files were
   normalized to CRLF; worth knowing before scripting edits in this repo.

### The step that was NOT completed
**Step 7's DevHost browser leg was not run** — no browser driver was available in this session. Every
automated gate passes, but the drag-and-drop path has never been exercised in a browser, and ADR 0073
records that as its largest residual rather than implying coverage that does not exist.
