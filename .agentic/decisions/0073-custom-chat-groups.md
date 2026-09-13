---
id: 0073
date: 2026-09-13
status: accepted
tags: [frontend, orleans, conversations, sidebar, ux, persistence, css, no-migration]
supersedes: []
extends: [0045]
amends: []
---

# Chats are filed into the user's own named groups

## Context
The transmissions sidebar has exactly one kind of grouping and the user cannot author it: agent-bound
chats collect under the agent that produced them (the mechanism is documented at
`IConversationIndexGrain.cs:12–15`, not in an ADR of its own). There is no way to say "these four chats
are Work". `ConversationIndexGrain` already owns the per-conversation metadata and is already the thing
the sidebar reads, so this costs no new grain, no new Cosmos container and no state migration.

## Decision
A group is a **first-class entity** — `ChatGroup(Id, Name, Order)` in `ConversationIndexState` — and a
conversation carries a nullable `GroupId`. Six Easy-Auth-gated routes in a new
`src/Agent42.Backend/Conversations/AgentApi.ChatGroups.cs`, and a new `src/Agent42.Backend/wwwroot/js/groups.js` that owns the group list, the
two dialogs and the drag-and-drop while `sidebar.js` keeps the conversation-list DOM.

Five scoping decisions, taken up front with the user:

1. **First-class entities, not a label per chat.** A denormalized `GroupName` on `ConversationInfo`
   would have been cheaper and would have made rename a single grain call — but an empty group could
   not then exist, and there would be nowhere to hang a manual order. Both turned out to matter: an
   empty group is a valid drop target, and groups have no recency of their own to sort by.
2. **A custom group wins over the agent group.** A filed agent chat renders in its custom group and is
   skipped by the agent walk. Rejected: forbidding agent chats from being filed (a *continuous* agent's
   single chat, ADR 0016, is the one you would most want to file).
3. **Drag-and-drop on a mouse; a "Move to group…" dialog everywhere.** See the gesture section below.
4. **A "+" in the transmissions header creates a group**, and each group header carries rename and
   delete. **Deleting a group ungroups its chats and deletes none of them.**
5. **Manual group order**, applied by dragging a group header. Chats inside a group still sort by
   recency.

### What this does NOT reverse
ADR 0045 §4 rejected **manual drag ordering of conversations** ("a much larger feature needing a stored
sort key and DnD"). That rejection stands: the sort key and the DnD introduced here order *groups*, and
the conversation list is still recency-ordered everywhere, including inside a group. Separately, ADR
0045 rejected *an empty-group placeholder for a fully-pinned agent group*; rendering **empty custom
groups** is a different thing — an entity the user deliberately created, versus a derived group with no
members — and is the point of decision 1, not a quiet reversal.

### The load-bearing part: the hoist trap, a second time
ADR 0045 found that `renderSidebar` does one walk placing an agent group at its most-recent member, so
pinning implemented as server-side re-sorting would drag an agent's *entire group* to the top. Filing
has the identical shape, and the render is now **three passes**: the pinned block, the user's custom
groups in manual order, then the agent walk over what is left —
`conv.pinned || (conv.groupId && knownGroupIds.has(conv.groupId))` is skipped. `GetConversationsAsync`'s
ordering is **unchanged**; partitioning by group id in JS is not re-sorting.

Testing membership against the *known* ids rather than `conv.groupId` alone is what makes a dangling
filing self-healing: a conversation pointing at a deleted group renders ungrouped instead of vanishing.

### One flag could not carry two facts
`buildConvRow(conv, hoisted)` appended the agent badge under `hoisted && conv.agentId`. Two independent
facts now exist — a row is outside its *agent* group when hoisted **or** filed, and outside its *custom*
group only when hoisted — so the signature is `buildConvRow(conv, hoisted, grouped)` and the agent badge
is `(hoisted || grouped) && conv.agentId`, still keyed on `agentId` with the `|| "Agent"` fallback.
ADR 0045 records that a fully-pinned agent group disappearing is correct *because of* that badge; for a
continuous agent, filing its one chat reproduces that state exactly.

### The gesture: decided per interaction, never per device
`draggable` is set in the existing `pointerdown` handler as `e.pointerType === "mouse"` — the same
source of truth `wireRowReveal` already uses for the ADR 0052 long press. A fine-pointer media query was
rejected on measurement of its semantics, not on taste: it reports the **primary** pointer, so a
touchscreen laptop matches it, and that is precisely the device where a live `draggable` would put the
browser's own long-press drag in a race with the 400 ms reveal. It would also have cited the
`canNativeShare` idiom, which ADR 0047 chose *instead of* a media query.

For the same reason the move action is a **fourth row glyph present at every width**, not a touch-only
one: a hybrid laptop matches `(hover: hover)` yet still raises the long-press, so a gated glyph would
leave that device revealing three actions with no way to file the chat. Drag-and-drop is an accelerator
layered on top. Dragging files a chat *into* a group only; clearing is a dialog choice, because a
"drag back out" target would be an unlabelled region of the list.

## Alternatives considered
- **A `GroupName` label on each conversation** — cheaper and rename-friendly, but no empty groups and no
  manual order. Lost to decision 1.
- **A separate `IChatGroupGrain`** — a second state root to keep consistent with the conversation index,
  for a list that is at most 50 records. The index grain already holds everything needed.
- **Extending `GET /api/conversations` to `{conversations, groups}`** — twelve test call sites
  deserialize it as a bare `List<ConversationInfo>`, and the ADR 0032 cache mirror stores that shape, so
  a reshape breaks every mirror written before this deployment. Groups got their own endpoint.
- **Server-side ordering of grouped chats** (extending `GetConversationsAsync`) — the server cannot
  express where an *empty* group sits, since it has no member to sit at.
- **`PUT /api/conversation-groups/order`** — a literal segment out-ranks `{groupId}`. Group ids are
  server-minted so it could not collide today, but the house rule that produced `PUT /api/default-model`
  is followed anyway: the route is `PUT /api/conversation-group-order`.
- **Six more lambdas in `MapConversationEndpoints`** — that method sits near the S3776 ceiling, and a
  feature's endpoints belong beside the feature (ADR 0040). Endpoint groups: 16 → 17.
- **A touch-only move glyph, gated by a media query** — rejected above.

## Consequences
- **No migration.** `ConversationInfo` gains a trailing optional `[Id(6)] GroupId`; `ConversationIndexState`
  gains `[Id(1)] List<ChatGroup> Groups` on a property with an initializer. Both directions are pinned:
  the committed pre-pin golden document (which predates groups too) must read as ungrouped with an
  **empty, not null**, group list, and a groups-carrying state must round-trip through the real Cosmos
  serializer — `List<ChatGroup>` is asserted by runtime type, because `ReorderGroupsAsync` rebuilds that
  list *inside* the grain, the exposed case in the collection-expression trap.
- **`renderSidebar` had to be split.** It measured exactly 15 cognitive complexity — the threshold — so a
  third pass would have failed `dotnet build` (S3776 at `error`, ADR 0051). The three passes are named
  functions; `clearReveal`, the search branch, the empty guard and the `.is-pinned-last` marker stay in
  `renderSidebar` because existing guards in two other test classes assert against that body, and moving
  them would make those guards vacuous rather than failing.
- **Groups are capped at 50 per user**, bounding `ConversationIndexState` against ADR 0004's 2 MB item.
  Far past human use; the cap exists because nothing else stops a scripted client.
- **Every mutator follows `SetPinnedAsync`'s discipline** — no write unless something changed, and an
  unknown id is a silent 204. A non-null `GroupId` naming no group is a **no-op, not a clear**: clearing
  is what `null` means, and conflating them lets a stale client wipe a filing the user still has.
- **Incognito chats (ADR 0034) are structurally ungroupable** — never touched into the index, so there is
  no entry to carry a `GroupId`. Same as pinning; no code.
- **No new egress, secret, container, Cosmos container, Bicep change, `excludedPaths` entry or package.**
  The exclusion list stays at six, proved by four new negative rows.
- **Groups are invisible in the public share view (ADR 0035) and during a search**, where the branch
  returns before any pass — as agent groups already are.
- Shell cache bumped `agent42-shell-v36` → `-v37`; `groups.js` is the 29th ES module.
- `AgentApi.cs`'s "58 route registrations" figure was wrong under either counting rule. Recounted to
  **63**, with the rule now stated in the comment so the next reader can reproduce it.

### Unverified residuals — stated rather than implied
- **Every frontend assertion here is source-text.** "A filed chat renders exactly once", the drag
  gesture itself, and **whether `draggable` set in `pointerdown` is re-read at drag initiation** are not
  covered by any test in this repo. The last of these is an assumption the implementation rests on.
- **The manual DevHost browser leg was NOT run** — no browser driver was available in the session that
  built this. Unlike ADR 0045, 0052 and 0072, this ADR therefore records **no measured values**, and the
  drag-and-drop path in particular has never been exercised. This is the ADR 0043 treatment, and it is
  the largest known gap in this change.
- **No auto-scroll while dragging near the list edges.** On a long conversation list a group above or
  below the viewport cannot be reached by dragging; the move dialog is the workaround. Deliberately out
  of scope rather than half-built.
- The premise that Android Chrome long-press-drags a `draggable` element is **unmeasured**. It is the
  hazard the per-interaction gate defends against, not an established fact — but the gate costs nothing
  and is the same rule the reveal already uses, so it stands either way.
