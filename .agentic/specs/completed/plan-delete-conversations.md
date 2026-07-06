# Plan — Delete conversations from the sidebar

## Overview

Let the user delete a conversation from the sidebar (a delete control on the right
edge of each conversation row). A delete is a **full purge**: it removes the index
entry, clears the agent grain's persisted session, and unregisters any durable
Orleans reminders so a deleted chat can never re-fire. The UI asks for confirmation
first. Today conversations can only be created and reopened — there is no way to
remove one, so orphaned sessions and reminders accumulate.

## Affected areas (verified against the tree)

| File | Change |
|------|--------|
| `src/Agent42.Agents/IConversationIndexGrain.cs` | Add `RemoveAsync(conversationId)` to the interface (with `[Alias]`). |
| `src/Agent42.Agents/ConversationIndexGrain.cs` | Implement `RemoveAsync`: drop the entry, `WriteStateAsync`. |
| `src/Agent42.Agents/IAgentGrain.cs` | Add `PurgeAsync()` to the interface (with `[Alias]`). |
| `src/Agent42.Agents/AgentGrain.cs` | Implement `PurgeAsync`: unregister all reminders, clear `ScheduledPrompts`, null the session, `ClearStateAsync`, `DeactivateOnIdle`. |
| `src/Agent42.Backend/AgentApi.cs` | Add `DELETE /api/conversations/{conversationId}`: purge the agent grain, remove from the index, return `204`. |
| `src/Agent42.Backend/wwwroot/app.js` | Render a delete button per row; `deleteConversation(id)` → confirm → DELETE → refresh; reset to a new chat if the active conversation was deleted; guard while `streaming`. |
| `src/Agent42.Backend/wwwroot/styles.css` | Style `.conv__delete` (right-aligned, reveal on row hover/focus). |
| `tests/Agent42.Tests/AgentGrainTests.cs` | Tests for `PurgeAsync` and index `RemoveAsync`. |
| `tests/Agent42.Tests/AgentApiTests.cs` | Test for the `DELETE` endpoint contract. |

No `index.html` change — the delete button is created in `app.js` like the rest of
each row. No Bicep/infra change — no new container or reminder store.

## Steps (ordered; grain + API steps 1–3 are backend, step 4 is frontend)

1. **Index grain — `RemoveAsync`.**
   Files: `IConversationIndexGrain.cs`, `ConversationIndexGrain.cs`.
   `RemoveAsync(string conversationId)` removes the matching entry from
   `_state.State.Conversations` (no-op if absent) and `await _state.WriteStateAsync()`.
   Verify: new grain test (step 5) `dotnet test` green.

2. **Agent grain — `PurgeAsync`.**
   Files: `IAgentGrain.cs`, `AgentGrain.cs`.
   `PurgeAsync()`: iterate `await this.GetReminders()` and
   `await this.UnregisterReminder(r)` each; `_state.State.ScheduledPrompts.Clear()`;
   `_state.State.SerializedSession = null`; `await _state.ClearStateAsync()`; then
   `DeactivateOnIdle()` so the next activation starts from clean state. This is the
   critical step — reminders are durable and keep firing unless unregistered
   (see `AgentGrain.ReceiveReminder`). Follows the reminder API already used in
   `SchedulePromptAsync`.
   Verify: new grain test (step 5).

3. **DELETE endpoint.**
   File: `AgentApi.cs`.
   `app.MapDelete("/api/conversations/{conversationId}", …)`: resolve `userId` via
   `http.GetUserId()`, call `IAgentGrain` (keyed with the existing private
   `AgentKey`) `.PurgeAsync()`, then `IConversationIndexGrain(userId).RemoveAsync(id)`,
   return `Results.NoContent()`. Per-user keying already prevents cross-user deletes.
   Verify: new API test (step 6).

4. **Frontend delete control.** *(depends on step 3 for the endpoint)*
   Files: `wwwroot/app.js`, `wwwroot/styles.css`.
   In `renderSidebar`, append a `<button class="conv__delete" aria-label="Delete conversation">`
   to each `li` (sibling of the `.conv` button so its click doesn't select the row);
   its handler calls `deleteConversation(conv.id)`. `deleteConversation`: return early
   if `streaming`; `confirm(...)`; `await fetch(DELETE)`; on success remove it from the
   local `conversations` array and, if `id === activeId`, run `startNewConversation()`;
   `renderSidebar()` / `refreshConversations()`. Style `.conv__delete` right-aligned,
   revealed on `.conv:hover`/`:focus-within`.
   Verify: `dotnet run --project tools/Agent42.DevHost`, create two chats, delete one
   (confirm fires, row disappears, list persists on reload); delete the active chat
   (thread resets to empty state).

5. **Grain tests.**
   File: `AgentGrainTests.cs`.
   - `Purge_ClearsHistoryAndUnregistersReminders`: chat + schedule a reminder, `PurgeAsync`,
     assert `GetHistoryAsync` empty, `GetScheduledPromptsAsync` empty, and
     `GetReminder("drink-water")` is null.
   - `ConversationIndex_RemoveAsync_DropsTheConversation`: touch two, remove one, assert
     the list contains only the other.
   Verify: `dotnet test Agent42.slnx`.

6. **API test.**
   File: `AgentApiTests.cs`.
   `DeleteConversation_RemovesItFromTheListAndClearsHistory`: POST a chat to create the
   conversation, `DELETE /api/conversations/{id}` (assert `204`), then
   `GET /api/conversations` no longer contains it and
   `GET /api/conversations/{id}/messages` returns an empty history.
   Verify: `dotnet test Agent42.slnx`.

Steps 1–3 and their tests (5–6) are backend and can be built together; step 4 is the
only frontend change and depends on step 3's endpoint existing.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Removing a conversation drops it from the index list | `ConversationIndex_RemoveAsync_DropsTheConversation` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | Purging a conversation clears its session history | `Purge_ClearsHistoryAndUnregistersReminders` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | Purging unregisters the conversation's durable reminders | `Purge_ClearsHistoryAndUnregistersReminders` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | `DELETE /api/conversations/{id}` returns 204 and the chat is gone from the list and its history | `DeleteConversation_RemovesItFromTheListAndClearsHistory` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC5 | Sidebar shows a delete control; confirming removes the row; deleting the active chat resets to the empty state | Manual (DevHost, step 4) — vanilla-JS frontend has no test harness in this repo | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- `new_issues`: **0**
- `new_security_issues`: **0**
- `duplication_pct_max`: **3**
- `coverage_new_code_pct_min`: **80** (grain + API deletion paths are covered by AC1–AC4; the JS handler is the only uncovered new code, consistent with the existing untested frontend)
- `cognitive_complexity_max`: **15**
- Dependencies: no new packages — nothing to resolve.
- Enforcement: local = soft warning; CI = hard block on build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope

- Bulk/multi-select delete, or an "archive" (soft-delete) state — this is a single,
  permanent delete per conversation.
- Undo / restore after delete.
- Renaming conversations or any other sidebar row actions.
- Deleting across users or any admin surface — deletes are scoped to the signed-in
  user by the existing per-user grain key.
- A JS test harness for the frontend — the repo deliberately has none (AC5 is manual).
