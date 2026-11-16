---
id: 0134
date: 2026-11-16
status: accepted
tags: [agents, conversations, privacy, ephemeral, incognito, persona, persistence]
---

# Offer incognito on per-chat agent chats

## Context
Incognito (ADR 0034) existed only on a fresh regular chat: `lockPicker` hid the toggle in every agent chat. Three traps
stood in the way of simply unhiding it. (1) `AgentGrain.BindAgentAsync` ends in an unconditional `WriteStateAsync`, so an
incognito agent chat would leave a stored record holding the persona snapshot and flip `RecordExists`, which other
incognito guards read. (2) The client sent `agentId` only on the first turn, and an incognito binding has nothing to
rehydrate, so a grain that deactivated between turns would run the next turn as an unbound plain chat. (3) A continuous
agent's id (`agent-<id>`) names one shared persisted grain, where an incognito turn stays in the in-memory session and the
next normal turn persists it.

## Decision
Per-chat agents of any kind except Copilot can be incognito. `BindAgentAsync(binding, ephemeral = false)` rebuilds in
memory but skips the write when ephemeral. While incognito the client resends `agentId` on every turn, so the (idempotent)
bind re-applies after a deactivation. The chat endpoint answers 400 to an ephemeral turn on a continuous conversation id
before any touch, bind or turn. Copilot stays refused (ADR 0085) and continuous agents stay excluded; the client hides
the toggle for both and resets a leaked flag when an agent chat starts.

## Alternatives considered
- **Unhide the toggle only, let the bind persist** — leaves a stored record and flips `RecordExists`, breaking "not persisted".
- **Bind ephemerally once and accept persona loss on deactivation** — the next turn silently runs unbound (no prompt, no tools,
  default model), a worse failure than ADR 0034's accepted history loss.
- **Carry the binding on every turn in a new `ChatRequest` field or `TurnContext`** — resending the existing `agentId` reuses the
  idempotent bind with no contract change.
- **Allow incognito on continuous agents** — the shared persisted grain would absorb the turn into durable history.

## Consequences
- Amends ADR 0034 (the toggle now also shows on fresh per-chat agent chats) and ADR 0003. **Exception to 0003's "first persona
  wins":** that holds only while the activation lives. A rebind after deactivation takes the agent's *current* definition
  (prompt, tools, kind), so an agent edited mid-chat can change persona. A deleted agent resolves to null and the turn runs unbound,
  as on the existing deleted-agent path.
- A refused incognito Copilot turn now also leaves no record, since the bind before the refusal no longer writes.
- Webhooks need no change: delivery is gated on the persisted flag, so an incognito agent turn never fires one.
- Documents stay blocked client-side only, as for every incognito chat.
- Accepted residual: the in-memory bind leaves the persona in `_state.State`, so any later `WriteStateAsync` on that activation
  (for example `SetEnabledToolsAsync`) would persist it. The client cannot reach those in an agent chat (`lockPicker` hides the tools
  popover); a hand-crafted API call could. Same class as the out-of-scope hand-crafted non-ephemeral turn in ADR 0034.
