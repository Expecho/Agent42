planned-against: main @ 7d76f16

# Plan: incognito for per-chat agent chats

## Overview

Today the incognito toggle (ADR 0034) exists only on a fresh **regular** chat: `lockPicker` hides it in every agent
chat. The user wants to start an incognito chat with a stored agent. Scope is **per-chat agents only**, any kind except
Copilot. Continuous agents (one shared `agent-<id>` history, ADR 0016) and Copilot agents (whose runtime writes its
transcript during the turn, ADR 0085) stay excluded. The guarantee stays "not persisted, not listed" for the whole
conversation, including the persona binding.

## Affected areas

- `src/Agent42.Agents/Conversations/IAgentGrain.cs`: `BindAgentAsync` signature (trailing defaulted `bool ephemeral`).
- `src/Agent42.Agents/Conversations/AgentGrain.cs`: `BindAgentAsync` skips its `WriteStateAsync` when ephemeral.
- `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs`: `ChatAsync` handler. Passes the flag to the bind and
  refuses an ephemeral turn on a continuous conversation id.
- `src/Agent42.Backend/wwwroot/js/agents.js`: `startAgentChat` shows and resets the toggle on the fresh per-chat path.
- `src/Agent42.Backend/wwwroot/js/send.js`: keeps `pendingAgentId` across turns while incognito.
- `src/Agent42.Backend/wwwroot/sw.js`: `CACHE` bump. Four tests pin its value: `PwaAssetsTests`,
  `ClientToolsAssetsTests`, `ResearchAssetsTests`, `SearchAssetsTests`.
- Tests: `tests/Agent42.Tests/AgentGrainTests.cs`, `tests/Agent42.Tests/AgentApiTests.cs`,
  `tests/Agent42.Tests/TopbarActionsAssetsTests.cs`.
- Docs: new ADR (next free number; 0133 is the latest on the base), amendment banner on
  `.agentic/decisions/0034-ephemeral-chat.md`, `docs/features/conversation-history.md`, `docs/features/custom-agents.md`.

## Context & constraints

Investigation findings (each verified on the base commit):

1. **Binding writes state.** `AgentGrain.BindAgentAsync` ends in an unconditional `_state.WriteStateAsync()`
   (`AgentGrain.cs`, the method). The API handler calls it before the turn on any request carrying `AgentId`
   (`AgentApi.Conversations.cs`, `ChatAsync`). If you only unhide the toggle, an incognito agent chat therefore
   leaves a stored grain record holding the persona snapshot, which breaks ADR 0034's "not persisted". It would also
   flip `_state.RecordExists` to true, and other incognito guards read that: the shared-state `PUT` answers 409
   only when there is no record (`SharedState/AgentGrain.SharedState.cs`), and ADR 0092's edit rule is keyed on
   "never written". **The bind must skip the write on an ephemeral turn.** The in-memory rebuild still runs, so the
   persona, model and tool snapshot apply for the activation's lifetime.
2. **The client sends `agentId` only on the first turn.** `send.js` clears `pendingAgentId` after a successful turn.
   A persisted chat rehydrates its binding from state on reactivation. An incognito one has nothing to rehydrate.
   If the grain deactivates between turns (idle or memory pressure), the next turn would run as an **unbound plain
   chat**: no system prompt, no tool snapshot, and `model: null` falling back to the grain default. Losing history
   is accepted under ADR 0034. Silently losing the persona is not. **Decision:** while incognito, the client keeps
   `pendingAgentId` and sends it on every turn. The handler then looks up the definition and calls
   `BindAgentAsync(..., ephemeral: true)` each turn. This is a no-op while bound (the existing early return) and a
   rebind after deactivation. Accepted residuals:
   - A rebind takes the agent's *current* definition, so an agent edited mid-chat can change persona.
   - A deleted agent resolves to null, and the turn runs unbound. This matches the existing deleted-agent path.
3. **Continuous agents must be refused server-side, not only hidden.** Their id `agent-<agentId>`
   (`RetentionLimits.ContinuousConversationIdPrefix`) names one shared, persisted grain. An ephemeral turn there
   stays in the in-memory `_session`, and the next normal turn persists it. This is the trap in gotchas
   "Skipping an incognito turn's index write is NOT enough — it stays in the in-memory session". Today the API
   accepts `Ephemeral` on such an id. The handler answers **400** before any touch, bind or turn.
4. **Copilot is already refused server-side** (`Copilot/AgentGrain.Copilot.cs`, `CopilotIncognitoRefusal`). After
   this change the bind before that refusal no longer writes, so a refused incognito Copilot turn also leaves no
   record. The client keeps the toggle hidden for Copilot.
5. **Client toggle mechanics.** `lockPicker` hides `#incognitoBtn` for every agent chat. That stays right for an
   *opened* agent chat: it is persisted, and incognito is fixed at creation. `startAgentChat` currently never
   resets `ephemeral`; only the Copilot branch calls `setIncognito(false, false)`. So incognito switched on in a
   fresh regular chat survives a click on an agent, and is sent with the button hidden. `startAgentChat` reaches its
   fresh path for a continuous agent too, when that agent has no conversation yet. The gate must test
   `agent.continuous`, not only the early-return branch.
6. **Research kind is included.** The loop writes no state mid-run (no `WriteStateAsync` under `Research/`), and its
   turns go through `PersistTurnUnlessEphemeralAsync` like any chat.
7. **Webhooks need no change.** `NotifyAgentWebhookAsync` is gated on the *persisted* flag
   (`Webhooks/AgentGrain.Webhooks.cs`), so an incognito agent turn never fires the agent's webhook. AC 6 pins this.

Governing docs: ADR 0034 (incognito), ADR 0003 (persona snapshot), ADR 0016 (continuous), ADR 0085 (Copilot refusal),
ADR 0106 (per-run `TurnContext`), ADR 0094 (webhook), ADR 0041 + gotchas "Every MR that touches `wwwroot/js/` must
bump `CACHE`" and "A frontend module unreachable from `main.js` is invisible to every source-text guard".

Interface change: adding a trailing defaulted parameter to `BindAgentAsync` keeps `[Alias("BindAgentAsync")]`. It
follows the ADR 0034 precedent of a trailing `bool ephemeral = false` on `ChatAsync`. The other callers (agent-run,
fleet analyzer, MCP runner, chat gateway) keep the default `false` and are unchanged.

## Steps

1. **Grain: ephemeral bind.** `IAgentGrain.BindAgentAsync(AgentBinding binding, bool ephemeral = false)`. In
   `AgentGrain.BindAgentAsync`, skip `WriteStateAsync` when `ephemeral` and keep everything else, including the
   rebuild. Update the XML doc. Tests in `AgentGrainTests.cs` (shape: `Binding_PinsPersonaAndModel_SurvivesReactivation`
   and `EphemeralChat_RunsTheTurn_ButPersistsNothing`):
   - `BindAgent_Ephemeral_AppliesPersonaButWritesNothing`: binding is returned, and the stored state has no
     `AgentId` (no record).
   - `BindAgent_Ephemeral_IsGoneAfterReactivation`: after `DeactivateOnIdle`, `GetAgentBindingAsync()` is null.
   - `EphemeralAgentChat_TurnUsesPersona_AndPersistsNothing`: bind ephemerally, run an ephemeral turn. The canned
     client sees the persona's system prompt (or the bound model is selected), and nothing is stored.

   Verify: `dotnet test tests/Agent42.Tests --filter "FullyQualifiedName~AgentGrainTests.BindAgent|FullyQualifiedName~AgentGrainTests.EphemeralAgentChat"`.
2. **API: pass the flag, refuse continuous.** In `AgentApi.Conversations.ChatAsync`:
   (a) a small helper (e.g. `RefusesIncognito(conversationId, request)`) called at the very top of the handler,
   before `ResolveAttachmentsAsync`, answers 400 `{ error }` when `request.Ephemeral` and `conversationId` starts
   with `RetentionLimits.ContinuousConversationIdPrefix`. The extraction is **unconditional**: the handler is already
   at roughly 13 cognitive complexity (critic's count), and an inline guard lands on the S3776 limit of 15;
   (b) pass `request.Ephemeral` into `BindAgentAsync`.
   Tests in `AgentApiTests.cs` (shape: `CopilotChat_DocumentUpload_IsRefused`, using `CreateAgentAsync`):
   - `IncognitoAgentChat_IsNotListed_AndLeavesNoRecord`
   - `IncognitoAgentChat_RebindsAfterDeactivation`: two incognito turns with `agentId`, deactivating the grain
     between them. The second turn still has the binding (via `/api/conversations/{id}/agent` or the grain).
   - `IncognitoTurn_OnContinuousConversation_Is400`: also asserts that nothing was written, by reading the grain's
     `GetAgentBindingAsync()` (null) and `GetHistoryAsync()` (empty) for the `agent-<id>` key after the 400.

   And in `AgentWebhookTurnTests.cs` (exemplar: `EphemeralTurn_NeverDelivers_NextTurnDoes`):
   - `IncognitoAgentChat_DoesNotFireWebhook`: pins finding 7. An incognito turn on a bound agent delivers nothing.

   Verify: `dotnet test tests/Agent42.Tests --filter "FullyQualifiedName~AgentApiTests.Incognito|FullyQualifiedName~AgentWebhookTurnTests"`.
3. **Client: toggle and agentId.** Parallelizable with steps 1–2.
   - `agents.js` `startAgentChat`: after `lockPicker(...)`, compute
     `const incognitoAllowed = !agent.continuous && agent.kind !== "copilot";`, set
     `incognitoBtn.hidden = !incognitoAllowed;`, and call `setIncognito(false, incognitoAllowed)`. This replaces the
     Copilot-only line and always resets a leaked flag.
   - `send.js`: change `setPendingAgentId(null)` on the success path to `if (!ephemeral) setPendingAgentId(null);`.
     Update its comment to the finding-2 rationale.
   - `sw.js`: bump `CACHE` and update the four pinning tests.

   Tests in `TopbarActionsAssetsTests.cs` (source-text, same disciplines as the file header: positive anchor plus
   index ordering):
   - `IncognitoToggle_IsOfferedOnFreshPerChatAgentChats`: `startAgentChat` body contains `agent.continuous` and
     `"copilot"` in the gate, and `setIncognito(false, incognitoAllowed)` sits after `lockPicker(`.
   - `IncognitoAgentChat_KeepsAgentIdAcrossTurns`: the `send` body contains `if (!ephemeral) setPendingAgentId(null)`.

   Verify: `dotnet test tests/Agent42.Tests --filter "FullyQualifiedName~TopbarActionsAssetsTests|FullyQualifiedName~PwaAssetsTests|FullyQualifiedName~ClientToolsAssetsTests|FullyQualifiedName~ResearchAssetsTests|FullyQualifiedName~SearchAssetsTests"`.
4. **Decision and docs.** Run `/decide`: write ADR 0134 "Incognito for per-chat agent chats" amending 0034 and
   0003. It records findings 1–3, the rejected alternatives below, and the residuals. State plainly that a rebind
   after deactivation takes the agent's *current* definition (prompt, tools, kind): a deliberate exception to ADR
   0003's "first persona wins", which holds only while the activation lives. Add an "Amended by" banner to **both**
   0034 and 0003. Update `docs/features/conversation-history.md` (incognito section) and `docs/features/custom-agents.md`:
   per-chat agents support incognito; continuous and Copilot agents don't. Add the ADR to the CLAUDE.md feature-map
   row "Conversations". Run `/sync-docs`.
5. **Full gates.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
   `dotnet format Agent42.slnx --verify-no-changes`, and `node .agentic/scripts/check-new-code-coverage.mjs`.
6. **Manual check (DevHost).** `dotnet run --project tools/Agent42.DevHost`:
   - Start a per-chat agent chat: the toggle shows. Switch it on and send two turns: the persona answers, and no
     sidebar row appears.
   - A continuous agent shows no toggle. A Copilot agent (if the DevHost offers one) shows no toggle.
   - Switch incognito on in a regular chat, then click an agent: the toggle shows off.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | An ephemeral bind applies the persona but writes no state | `BindAgent_Ephemeral_AppliesPersonaButWritesNothing` | `AgentGrainTests.cs` |
| 2 | An ephemeral binding is gone after deactivation | `BindAgent_Ephemeral_IsGoneAfterReactivation` | `AgentGrainTests.cs` |
| 3 | An incognito agent turn uses the persona and persists nothing | `EphemeralAgentChat_TurnUsesPersona_AndPersistsNothing` | `AgentGrainTests.cs` |
| 4 | An incognito agent chat via the API is unlisted and leaves no record | `IncognitoAgentChat_IsNotListed_AndLeavesNoRecord` | `AgentApiTests.cs` |
| 5 | The persona survives a deactivation mid-incognito-chat (rebind) | `IncognitoAgentChat_RebindsAfterDeactivation` | `AgentApiTests.cs` |
| 6 | An incognito agent turn fires no webhook | `IncognitoAgentChat_DoesNotFireWebhook` | `AgentWebhookTurnTests.cs` |
| 7 | Incognito on a continuous agent's conversation is refused with 400 and writes nothing | `IncognitoTurn_OnContinuousConversation_Is400` | `AgentApiTests.cs` |
| 8 | The toggle is offered only on fresh per-chat, non-Copilot agent chats, and resets on start | `IncognitoToggle_IsOfferedOnFreshPerChatAgentChats` | `TopbarActionsAssetsTests.cs` |
| 9 | The client resends `agentId` on every incognito turn | `IncognitoAgentChat_KeepsAgentIdAcrossTurns` | `TopbarActionsAssetsTests.cs` |
| 10 | Existing incognito, bind and Copilot-refusal behaviour is unchanged | existing `AgentGrainTests` Ephemeral*/Bind* tests, `CopilotAgentTests` | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (error) |
| `vulnerable_packages` | 0 | CI package-freshness job (no new packages planned) |
| Lint | clean | `dotnet format Agent42.slnx --verify-no-changes` |

## Alternatives rejected

- **Unhide the toggle only, and let the bind persist.** Rejected: it leaves a stored record (finding 1) and flips
  `RecordExists`.
- **Bind ephemerally once and accept persona loss on deactivation.** Rejected: the next turn silently runs as an
  unbound chat (finding 2). That is a worse failure than ADR 0034's accepted history loss.
- **Carry the binding on every turn in a new `ChatRequest` field / `TurnContext`.** Rejected: resending the existing
  `agentId` reuses the idempotent bind with no contract change.
- **Allow incognito on continuous agents.** Rejected: the shared persisted grain would absorb the turn into durable
  history on the next normal turn (finding 3).

## Out of scope

- The server-side document gate. Documents are still blocked client-side only (`attachments.js`), for regular and
  agent chats alike, unchanged.
- A hand-crafted non-ephemeral turn on a conversation that started incognito (it would persist the in-memory
  history). This is pre-existing ADR 0034 behaviour for regular chats too, and not widened here.
- Incognito for Copilot agents, continuous agents, the fleet analyzer, agent-run, MCP `ask_agent`, and chat apps.
- The Android app (it uses the same web UI. No separate change).

## Review

`critic` pass on base 7d76f16: no blocking issues, no ADR conflicts. It independently confirmed that the bind is the
only added state write, that resending `agentId` per turn is safe (idempotent bind, no effect on edit/regenerate or
stream recovery), that the continuous guard belongs at the top of the handler, and that the client gate holds for
continuous (with and without a conversation), Copilot and research. All four findings were accepted:

1. MEDIUM, S3776 risk from an inline guard: step 2 now extracts a helper unconditionally.
2. LOW, AC 6 file left open and the "no grain record" mechanism unnamed: the webhook test is pinned to
   `AgentWebhookTurnTests.cs`, and the 400 test names its mechanism.
3. LOW, ADR 0003 asymmetry: step 4 adds the banner to both ADRs.
4. LOW, rebind-takes-current-definition wording: step 4 states it as an exception to ADR 0003.

Nothing rejected.

## Divergences from the plan

None of substance. Implemented as written: steps 1-3 as specified (`RefusesIncognito` helper extracted unconditionally,
`CACHE` bumped to `agent42-shell-v82` with the four pinning tests), ADR 0134 with banners on 0034 and 0003, docs and the
CLAUDE.md feature-map row. `/sync-docs` found no gotcha invalidated (the "re-bind returns early" claims still hold) and
no new gotcha is warranted, since ADR 0134 records the traps. The step 6 manual DevHost check is not done by the agent.
