---
id: 0045
date: 2026-08-21
status: accepted
tags: [frontend, orleans, conversations, sidebar, ux, persistence, css, pwa]
supersedes: []
extends: []
amends: []
---

# Pin a conversation so it leads the sidebar

## Context
The transmissions list is purely recency-ordered — `GetConversationsAsync` returns
`OrderByDescending(c => c.UpdatedAt)` and nothing else. A chat the user returns to often but has not
touched today sinks below whatever they opened since. Pinning is the standard fix, and
`ConversationIndexGrain` already owns exactly the per-conversation metadata it needs, so this costs no
new grain, no new Cosmos container and no state migration.

## Decision
A pin toggle on every sidebar row, persisted per user on the conversation index entry, honoured by the
server's ordering and by a **two-pass** client render.

Four scoping decisions, taken up front:

1. **The control lives on the sidebar row**, beside the ADR 0035 share action and the delete action.
   Rejected: a topbar toggle in the ADR 0044 cluster (you cannot pin a chat you are not looking at), and
   both entry points (more state-sync transitions and the ADR 0044 `startAgentChat` trap, for no gain).
2. **Any listed chat is pinnable** — regular or agent-bound. No per-row capability branching.
3. **No cap** on pin count. There is therefore no error path and no validated failure mode.
4. **Pinned chats sort among themselves by last activity**, not by pin time. Rejected: a stored
   pinned-at timestamp (an extra persisted field for behaviour nobody asked for) and manual drag
   ordering (a much larger feature needing a stored sort key and DnD).

### The load-bearing part: a pinned agent chat must be HOISTED out of its group, not sorted into it
`renderSidebar` does **one walk** over the server-ordered list and reserves a per-agent group's slot at
its **first-encountered (= most recent) member**, filling it through a placeholder `<li>` replaced
afterwards. So if pinning were implemented as *server-side re-sorting alone*, pinning a single agent chat
would drag **that agent's entire group** to the top, because the group's slot follows its most-recent
member.

The render is therefore two passes: a flat pinned block first, then the **existing** grouping walk over
the **unpinned remainder only** (`if (conv.pinned) continue;`). The server still does the ordering — it
returns pinned-first, sorted by recency within each half — so pass 1 emits in order and JS never
re-sorts.

Two consequences of hoisting, both handled rather than discovered:

- **Hoisting costs the agent label, and a badge repays it.** An agent's name is rendered *only* by
  `.conv-group__name` in `buildAgentGroup`; `buildConvRow` draws title and time. A hoisted agent chat
  would be visually indistinguishable from a regular one. `buildConvRow` gained a second parameter
  (`hoisted`) and renders a `.conv__agent` badge when it is set and `conv.agentId` exists, falling back
  to the literal `"Agent"` when the name is blank. **Keyed on `agentId`, not `agentName`, deliberately:**
  the grouping walk keys on `agentId` and `buildAgentGroup` already falls back to `"Agent"`, so keying
  the badge on the name would let a blank-named agent chat group correctly but hoist with **no label at
  all** — the exact bug the badge exists to prevent. Existing call sites pass nothing and are unchanged.
- **A fully-pinned agent group disappears, and that is correct *because of* the badge.** If every one of
  an agent's chats is pinned, the walk sees zero members and no group is constructed. For a
  **continuous** agent (ADR 0016 — exactly one conversation, id `agent-{agentId}`, and the most
  pin-worthy chat in the app) that is the *normal* case, not an edge case. Without the badge it would be
  a disappearing-agent bug. No empty-group placeholder is added.

### `TouchAsync` needed no change, and that is exactly why it needed a test
`TouchAsync` updates an existing entry with a non-destructive copy —
`conversations[existing] = conversations[existing] with { UpdatedAt = … }`. A `with` expression carries
every other member forward, so `Pinned` survives a touch for free. A later refactor to an explicit
`new ConversationInfo(...)` would silently unpin every chat on its next turn, so
`ConversationIndex_Touch_PreservesThePinnedFlag` pins the free behaviour.

### The persistence proof is on the JSON path, not the binary one
`ConversationInfo` gained a trailing optional `[Id(5)] bool Pinned = false`, matching the `[Id(3)]`/
`[Id(4)]` agent-identity pair whose own comment records the same reasoning: an existing document has no
`pinned` key and deserializes to `false`. Zero migration.

But **`SerializationCompatibilityTests` cannot prove that**, and this is the specific mistake the
"`$type` is part of the on-disk format" gotcha exists to prevent. Those golden blobs go through Orleans'
**binary** serializer; Cosmos writes grain state through `JsonGrainStorageSerializer` (Newtonsoft). The
real risk here is Newtonsoft constructor-binding a **missing** property onto a **positional record** —
only the JSON path exercises it. `GrainStorageSerializerTests` previously committed a golden document for
`AgentGrainState` alone; this adds `ConversationIndexDocument_WrittenBeforePinning_StillDeserializes_AsUnpinned`
over a hand-written pre-pin `ConversationIndexState` payload carrying no `pinned` key on either entry.
The binary assertion (`Assert.False(actual.Conversations[0].Pinned)` in
`ConversationIndexState_RoundTripsFieldForField`, with the blob **not** regenerated) is kept as secondary
completeness, explicitly not as the data-at-rest guard.

### "Pinned" already meant something else here — the name was kept anyway
This repo already says "pinned" for **model binding**: "the already-pinned persona stands"
(`AgentApi.Conversations.cs`), "the model is pinned server-side" (`src/Agent42.Backend/wwwroot/js/send.js`), and ADR 0018's "the
first turn calls `RebindModelAsync` and pins the model". The collision is real, and a reviewer proposed
`Starred`/`Favourite`. Those uses are **prose and comments only** — never a member name — and they sit on
a different type (`AgentGrainState`/`AgentBinding`) from `ConversationInfo`. "Pin" is the user's own word
for this affordance and the universal UI term for it; a private coinage is worse for anyone searching the
codebase. **Name kept, cost recorded honestly:** this one lands on a *persisted* member, so it becomes an
on-disk JSON key that is cheap to choose now and awkward to change later.

### The hover-only row actions were unreachable on touch, and that had to be fixed here
`.conv__share` and `.conv__delete` are `opacity: 0`, revealed only by `.conv-row:hover` /
`:focus-visible`, and `styles.css` carried **no `hover: none` override anywhere**. A naive `.conv__pin`
copy would have been unreachable on the installed Android PWA — and *asymmetrically* so, because
`.conv-row.is-pinned .conv__pin { opacity: 1 }` makes an already-pinned row's control visible: a user
could **un**pin on touch but never pin. Android/Chromium is a first-class surface here (ADR 0023 share
target, ADR 0028 push) and the reachable-on-mobile alternative was declined in decision 1, so this is
fixed in CSS rather than left as a residual: an `@media (hover: none)` block reveals `.conv__pin`,
`.conv__share` and `.conv__delete` permanently. That **also fixes the same latent gap in share and
delete**, which predates this change — a deliberate in-family fix, not scope creep.

**Scope it honestly, though: the block covers the three *conversation-row* actions and nothing else.**
The same `opacity: 0` + hover-reveal idiom is used by `.agent__action` (the agents panel's edit / key /
delete) and `.msg__copy`, and both remain unreachable on a pointerless device. That gap is **pre-existing
and untouched here** — widening the rule to controls this feature does not add, in features whose touch
behaviour this MR's manual pass never exercised, is a change that should stand on its own. Recorded as a
known residual and an obvious follow-up rather than smuggled in.

### Endpoint
`PUT /api/conversations/{conversationId}/pin` with a `PinConversationRequest(bool Pinned)` body, returning
204. Behind Easy Auth like every other `/api/*` write — **not** on `excludedPaths` — with userId from
`http.GetUserId()`, so the grain key scopes the write to that user and pinning someone else's
conversation is structurally impossible. Idempotent, and a **silent no-op for an unlisted id**: an
unlisted id is indistinguishable from an unchanged one, so the endpoint discloses nothing about what
exists. It follows the canonical minimal-API endpoint group (`src/Agent42.Backend/Users/AgentApi.Users.cs`, body-bound with
no 400-validation branch) and its nearest sibling route (`PUT …/tools`, same route family, same
`GetUserId()` scoping).

Note precisely what the Easy-Auth guard does: `EasyAuthExclusionTests` pins matcher *semantics*, **not**
an entry count, so leaving the Bicep untouched proves nothing about a new route. An
`[InlineData("/api/conversations/x/pin")]` row was added to
`Exclusions_DoNotCoverAnyAuthenticatedPath` so the route is positively asserted to stay gated.

## Consequences
- **Not a security change and not an egress change (ADR 0013).** A pin is owner-side sidebar state. It is
  deliberately **not** surfaced into the ADR 0035 public share payload, and the other
  `GetConversationsAsync` caller (`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`, resolving a shared conversation's title
  on the owner's index) is an **id lookup** over the returned list, so the ordering change is inert
  there. It must stay a lookup — a pinned-first ordering must not tempt an index-based shortcut.
- **Ephemeral chats are structurally unpinnable.** ADR 0034 chats never enter the index at all, so there
  is no row to pin and nothing to guard.
- **Offline (ADR 0032) needed no code change** — `refreshConversations` mirrors the whole
  `/api/conversations` response, so `pinned` rides along. Known, accepted degradation: a list cached
  *before* this ships has no `pinned` key, so those entries read `undefined` → falsy → render unpinned
  until the next online refresh.
- Returning PWA users need a new cache generation (`agent42-shell-v15` → `-v16`): subresources are served
  cache-first and this diff changes `sidebar.js`, so a version-skewed module graph is a blank app, not a
  degraded one. `SHELL` is unchanged — no new module file.
- `renderSidebar`'s cyclomatic complexity goes 6 → 9 and `buildConvRow`'s 2 → 5 (lizard), both well under
  the gate; no helper extraction was needed.
- **The pinned/unpinned divider is marked in JS (`.is-pinned-last`), not selected in CSS.** The obvious
  `:not(:has(+ .conv-row.is-pinned))` was written first and then removed for two independent reasons.
  It would have been the **only** `:has()` in the stylesheet — a new browser-baseline dependency taken
  silently for one hairline rule — and it cannot see *past* the block, so an all-pinned list would draw a
  trailing rule under its last row with nothing beneath it. Pass 1 already knows which row is last and
  can check `conversationList.lastElementChild`, so the class costs three lines and answers both. The
  rule itself uses **`margin-bottom`, never `padding-bottom`**: `.conv-row` is the containing block for
  the three absolutely-positioned row actions, whose `top: 50%` resolves against its **padding** box, so
  padding here would push pin/share/delete 4px off-centre on that one row.
- **What the automated tests can and cannot reach.** Every frontend assertion in `PinChatAssetsTests` is
  a source-text heuristic — the repo has no JS runner by design. They prove the two-pass structure, the
  unpinned guard, the badge, the endpoint target and the CSS declarations *shipped*. They do **not**
  prove that a pinned chat renders exactly once, which is a runtime DOM property and this change's
  central design constraint; nor the visual result; nor that `hover: none` behaves as a device fact.
  Those are the manual pass below.

### The manual pass was run, and these are the measured values
Driven the same way as ADR 0044 — DevHost on `localhost:61326`, headless Chrome over the DevTools
protocol (throwaway driver, deliberately not committed; this MR adds no browser test harness). Six
seeded conversations: three regular, two under agent "Marvin", one under continuous agent
"Deep Thought".

- **Renders exactly once.** With three chats pinned, tallying every `.conv__title` across the whole list
  yielded **no duplicate titles** (6 rows, 6 distinct). Top-level order was the three pinned rows, then
  the recency list.
- **Pinned agent chat carries its badge and leaves its group.** The hoisted row's `.conv__agent` read
  `Marvin`, and the Marvin group's `.conv-group__count` went **2 → 1**.
- **Continuous agent: the group disappears and the top row still names it.** With `agent-{id}` pinned,
  the groups list contained **only** Marvin — no Deep Thought group at all — and the hoisted row's badge
  read `Deep Thought`.
- **Three actions fit the row.** `.conv` computes `padding-right: 82px`; in a 275px row the title's right
  edge is 209px and the pin's left edge 214px (**no overlap**), with pin 214–236, share 238–260, delete
  262–284. Identical at 1400×900 and at 390×800, in both themes.
- **Touch reachability, verified with real touch input.** Under `Emulation.setTouchEmulationEnabled` +
  mobile metrics (`matchMedia("(hover: none)")` → `true`), `.conv__pin` / `.conv__share` /
  `.conv__delete` all compute `opacity: 1` on an unpinned row. A pure `Input.dispatchTouchEvent`
  tap — touch events only, no mouse — on the pin control pinned that chat: the server then reported
  `pinned: ["Notes on Vogon poetry"]` and the row hoisted to the top. **Note the lever:**
  `Emulation.setEmulatedMedia` with `hover`/`pointer` features did **not** flip `matchMedia` in
  Chrome 151 and produced a false negative on the first attempt; touch emulation plus mobile device
  metrics is what actually applies the media query.
- **Unpin round-trips through the UI**, restoring the chat to its recency position.
- **Re-measured after the review fixes** (the divider mechanism and the badge condition both changed
  after the first pass, so those legs were re-driven rather than carried over). With two of six pinned,
  the second pinned row carries `is-pinned-last` and exactly one divider exists; on that row
  `.conv`, `.conv__pin`, `.conv__share` and `.conv__delete` all centre on **y = 402** with
  `padding-bottom: 0px` and `margin-bottom: 8px` — i.e. the off-centre nudge the padding version would
  have caused is absent. With **all six** pinned the divider count is **0** (no trailing rule) and all
  three agent chats carry badges (`Deep Thought`, `Marvin`, `Marvin`), both groups having vanished.
  Injecting a blank `agentName` through the real render path produces the badge text **`Agent`**, the
  fallback the group header already used.
- One incident worth recording, because it is this repo's own documented trap seen live: the first
  re-verification run reported the divider missing. The code was correct and the server was serving it —
  the browser profile still had the **`agent42-shell-v15`** generation active and served the previous
  `sidebar.js` from the SW cache until the v16 worker took over. That is precisely the version-skew
  window the "bump `CACHE`" gotcha describes, and it is why the bump ships in this MR.
- Both themes captured at both widths (light theme requires `PUT /api/profile {"theme":"light"}` —
  the server profile wins over the `agent42.theme` localStorage cache, ADR 0005).

Still unverified, the same class ADR 0043 and 0044 record: real touch input on a physical Android
device.

## Alternatives considered
- **Server-side re-sorting alone, with no client change.** Rejected — it is the bug described above:
  pinning one agent chat hoists that agent's whole group, because the group's slot follows its
  most-recent member.
- **A dedicated pinned-conversations grain.** Rejected: the index entry already carries per-conversation
  metadata and a `with`-preserving update path, so a second grain would add a write to keep in sync for
  nothing.
- **Sorting pinned chats by pin time.** Rejected (decision 4): an extra persisted field for behaviour
  nobody asked for.
- **Renaming the member to `Starred`/`Favourite` to dodge the model-pinning collision.** Partly accepted:
  the collision is real and is now recorded, but the name is kept — see above.
- **An empty-group placeholder when every one of an agent's chats is pinned.** Rejected: the badge on the
  hoisted row already names the agent, and a placeholder would be visual noise in the continuous-agent
  case, which is the *normal* one.
- **Leaving the touch gap as a residual.** Rejected: it would make pinning impossible on the installed
  PWA while unpinning worked, and the mobile-reachable alternative was already declined in decision 1.
