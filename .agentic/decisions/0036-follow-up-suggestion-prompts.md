---
id: 0036
date: 2026-07-31
status: accepted
tags: [agents, conversations, ux, suggestions, preferences, streaming, egress]
---

# Follow-up suggestion prompts

## Context
After the assistant replies, there was no affordance to keep a conversation going except typing the next
message. The user asked for **up to 3 suggested prompts they can click to continue an ongoing
conversation** — the "suggested follow-ups" pattern. It was greenfield: no suggestion concept existed on
either side (searches for `suggest`/`follow-up`/`starter` in the frontend and `AgentGrain` returned nothing).

Two facts frame the design:

- **The turn already streams a typed event list.** `AgentGrain.ChatCoreAsync` yields `AgentStreamEvent`s
  (`TextDelta`/`ToolCall*`/`Image`/`Done`) as NDJSON over `POST /api/chat/{id}`; adding a new event kind is
  the natural transport, needing no endpoint change.
- **There is exact prior art for a turn-scoped, non-persisted signal and for a user-default read on
  activation.** Ephemerality (`_ephemeral`, ADR 0034) / location (`_turnLocation`, ADR 0012) are turn-scoped
  grain fields cleared in a `finally`; the default selectable-tools set (ADR 0021) is read once on activation
  from `IUserProfileGrain`. The gate and the preference reuse both shapes.

## Decision
After a reply completes on the **interactive** path, the grain makes **one extra model call** on the
conversation's own model — streamed via `IChatClient.GetStreamingResponseAsync` and aggregated, the same
path (and same cached client instance) the reply itself uses — to produce up to 3 short follow-up
prompts, and streams them as a new **`AgentStreamEventType.Suggestions`** event (carrying `[Id(11)]
IReadOnlyList<string>? Suggestions`) just **before** `Done`. The browser renders them as clickable chips
under the newest assistant bubble; **clicking one sends it immediately** as the next message (a click is
explicit intent, unlike the review-before-send Share Target of ADR 0023).

Three properties make this safe:

- **Interactive-path-only.** A turn-scoped `_interactiveTurn` flag defaults OFF and is set ON only in
  `ChatAsync` (cleared in its `finally`, defensively false in `EnqueuePromptAsync`). The agent-run (ADR 0022)
  and reminder drains of the same `ChatCoreAsync` therefore skip generation — their streamed events have no
  listener, so a suggestion call there would be a wasted model call for a discarded event. Image-generation
  turns and the no-model path `yield break` earlier, so they are excluded by construction.
- **Best-effort, side-effect-free.** `GenerateFollowUpsAsync` builds a **throwaway** `List<ChatMessage>`
  (never `_session`), writes no state, and owns a `try/catch` that returns `null` on any failure — so a slow
  or failing suggestion call never affects the reply or `Done`. (The `try/catch` lives in the helper, not the
  iterator, because C# forbids `yield` inside a `try` with a `catch`.)
- **Opt-out preference, default ON.** A new `bool? SuggestFollowUps` on `UserProfile` uses the codebase's
  **null-sentinel** (`null`/`true` = ON, `false` = OFF), so legacy profiles deserialize to `null` and stay
  ON with **zero migration** — read once on activation into `_suggestFollowUpsEnabled` (`!= false`), alongside
  the ADR 0021 default-tools read. It is settable from **Preferences → General** (a checkbox) via the existing
  read-modify-write `PUT /api/profile` (absent-means-keep, `ProfileUpdateRequest.SuggestFollowUps`).

The chips are **live-only**: they are not replayed by `renderTurns`, so they exist only under the freshly
streamed turn and are cleared when the next turn starts (`clearSuggestions` in `send()`).

## Alternatives considered
- **Attach suggestions to the `Done` event** instead of a distinct type. Rejected: a dedicated event keeps
  `Done`'s meaning ("turn finished, here is the usage") clean and lets the client render chips independently
  of the usage line, at a natural point in the stream.
- **Generate client-side** (ask the browser to make its own model call). Rejected: the browser has no chat
  client of its own — all model access is server-side through the per-user factory (ADR 0018) — and it would
  duplicate the egress decision the grain already owns.
- **Always on, no preference.** Rejected per the user's request: each turn spends an extra small model call
  and re-sends recent conversation text to the model (ADR 0013 egress), so it must be opt-out.
- **Gate on a separate `GetSuggestionsAsync` grain method** the client calls after the stream. Rejected:
  a second round-trip and a second activation-scoped read for no benefit; inlining before `Done` is one call.

## Consequences
- **Extra cost + egress per interactive reply (ADR 0013).** Every interactive turn with the preference on
  makes one additional small completion and sends the last exchange to the model again — outside the Entra
  tenant on a third-party model. The preference exists precisely to turn this off. Bounded by
  `MaxOutputTokens = 200`.
- **A small latency tail after the reply.** The suggestions call runs after the reply is fully streamed and
  the session is persisted, so the user reads the answer first; chips (and `Done`) follow after a brief pause.
  A failing/slow call degrades to no chips, never a broken turn.
- **Preference staleness matches ADR 0021.** `_suggestFollowUpsEnabled` is read once on activation, so
  toggling it reflects in a warm conversation only on its next activation — accepted, consistent with the
  default-tools and default-model reads.
- **Async paths are unaffected.** Agent-run and reminder replies never generate suggestions (no live client),
  the correct behaviour.
- **The call streams, deliberately.** An initial version used the non-streaming `GetResponseAsync`, which
  returned nothing on the prod Foundry **Responses** adapter (ADR 0014) — the reply path only ever exercises
  the streaming surface, so the non-streaming one was untested there. Streaming and aggregating makes the
  suggestion call behave exactly like the working reply. The failure was invisible because the helper logged
  at Debug; it now logs at **Warning** so a silent degrade surfaces in telemetry.
