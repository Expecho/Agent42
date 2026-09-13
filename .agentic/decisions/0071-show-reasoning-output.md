---
id: 0071
date: 2026-09-13
status: accepted
tags: [chat, streaming, models, reasoning, frontend, foundry, no-migration]
supersedes: []
extends: [0013, 0018, 0030, 0036, 0038]
amends: [0014]
---

# A reasoning model's thinking is shown, and never stored

> **Do not write "following ADR 0036".** The suggestion-chip shape is the exemplar for a *live-only stream
> event* and that half is copied deliberately. The gating is the opposite: suggestions are gated on
> `_interactiveTurn` because they cost an **extra model call**, while reasoning already rides the turn's own
> stream and costs nothing. So reasoning is **not** gated, and this feature adds **no** turn-scoped field —
> deliberately staying clear of the `_turnLocation`/`_ephemeral`/`_interactiveTurn`/`_webDocumentIngested`
> clear-it-in-three-places trap.

## Context

`ProjectStreamedContent` switches on the streamed content parts, and everything that is not text, a function
call or usage falls into `default: return null`. A reasoning model's "thinking" is therefore discarded
silently — the user sees a pause and then an answer, with no indication that the model worked.

Two things were unknown and both had to be measured before anything was written, because the answers decide
the design rather than merely inform it:

1. whether reasoning even survives the pinned client stack (`OpenAI` is held at 2.12.0 and **must not be
   lifted** — see the pin gotcha);
2. whether making a **Foundry** reasoning model emit anything requires
   `includeReasoningEncryptedContent: true`, which would push reasoning into our own session and contradict
   ADR 0004's 2 MB item.

A third question surfaced during planning and turned out to be the important one: whether the framework was
*already* persisting reasoning, before this feature shipped a line.

## Decision

Reasoning is streamed to the browser as its own event and rendered in a collapsed disclosure above the reply.
It is **live-only**: never appended to the reply, never written to `SerializedSession`, never replayed on
reload. A model declared as a reasoning model is asked to produce it.

### The three measurements, and what each settled

All three were run against the pinned stack with stub transports and no network.

**(a) Reasoning survives the pin.** A chat-completions `delta.reasoning_content` chunk drained through
`AsIChatClient()` arrives as `Microsoft.Extensions.AI.TextReasoningContent`. This is the *affirmative* twin of
`StreamingEmptyChoicesTests`, and the distinction matters: that test's `found=False` was measured against an
**empty-choices** chunk, where absence is correct, and says nothing about a chunk that really carries
reasoning. Pinned as `ReasoningStreamTests.Reasoning_SurfacesFromAChatCompletionsStream`.

**(b) `includeReasoningEncryptedContent` is not the mechanism — it stays `false`.** Measured on both values
against the exact Responses client `Provider.Foundry` builds:

| flag | request body | reasoning surfaced |
|---|---|---|
| `false` | no `include` array | `TextReasoningContent` |
| `true` | `include:["reasoning.encrypted_content"]` | `TextReasoningContent` |

The flag only carries *encrypted reasoning items across turns*, which a stateless client (ADR 0014) neither
needs nor wants. What actually turns reasoning on is **`ChatOptions.Reasoning = new ReasoningOptions { Output
= ReasoningOutput.Summary }`**, which the adapter renders as `"reasoning": {"summary": "concise"}`. So this
ADR **amends ADR 0014 narrowly**: its "reasoning Foundry models are deferred" (spec LOW-5) is lifted, while
its stateless mandate and the `false` flag are untouched. ADR 0004 is **not** amended.

**(c) Reasoning was ALREADY being persisted at HEAD.** `SerializeSessionAsync` retained the part on **both**
run paths — the streaming loop and `RunAsync` (the reminder path) — round-tripping as:

```json
{ "role": "assistant", "contents": [ { "$type": "reasoning", "text": "…" }, { "$type": "text", "text": "…" } ] }
```

Nothing surfaced it, so it was invisible rather than absent. Any user who had added an OpenAI-compatible
provider with a `reasoning_content` model (ADR 0018) was already writing reasoning into their 2 MB session.
Live-only therefore needs an **explicit strip**, not merely an absence of code.

### The strip, and where it lives

`StripReasoningFromStorage` is wired as the history provider's **`StorageInputResponseMessageFilter`** — the
response-side twin of the `StorageInputRequestMessageFilter` that already excludes injected context messages
in the same options block, so this is an existing shape rather than a new mechanism. It rebuilds only a
message that actually carries a reasoning part and passes every other message through by reference.

It must be a **storage filter, not a check in `ProjectStreamedContent`**: the projection cannot reach the
session at all. Agent Framework fills the session from the response message independently of what we project.

### `reply` is not the persistence path — and that is a separate concern

The first draft of the plan asserted that `ProjectStreamedContent`'s `reply` StringBuilder becomes the
persisted assistant turn. **It does not.** Its only consumers are the ADR 0036 follow-up-suggestion call;
persistence is `SerializeSessionAsync(_session)`. Both properties are wanted, they are different, and they
fail differently — so they have separate tests:

- reasoning must not reach **the session** (a durability and 2 MB concern) — `ReasoningText_IsNotPersisted`;
- reasoning must not reach **`reply`** (a *third-party egress* concern, ADR 0013: it would be re-sent to the
  model as if the assistant had said it) — `ReasoningText_DoesNotReachTheFollowUpCall`.

A gotcha records the correction, because it misled this plan once already.

### The capability flag is a THIRD pattern

`SupportsReasoning` is threaded through the model representations exactly as `SupportsSpeech` was (ADR 0038),
and the authoritative site list is `grep -rn "SupportsSpeech" --include=*.cs src/` — **nine** production
sites, not the five a first reading suggests. But its *role* matches neither existing pattern:

- it is **not** a `ModelCapabilities` bit, because `GetCapabilities` short-circuits a Foundry provider to
  `ModelCapabilities.Full with { … }` and never consults the declared models — a bit added there would read
  true for every Foundry model (the shortcut ADRs 0030 and 0038 sidestepped);
- it is **not** picker-filter-only like `SupportsEmbeddings`/`SupportsSpeech`, which never reach client
  construction. A reasoning model *is* a chat model and belongs in the ordinary picker.

It is read at **client-construction time by model id**, via the pure helper
`Provider.RequestsReasoning(options, model)`.

### Wired in `Build`, not in each provider's lambda

The plan called for `Provider.Foundry`'s client lambda to consult the flag. It is wired one level up instead,
in `ChatClientFactory.Build`, which is the single place that already has **both** the resolved provider and
the bare model id, and where the other cross-cutting decoration (the OTel wrapper) lives. One wiring covers
Foundry and OpenAI-compatible alike.

Measured, and the reason this is safe to apply uniformly: on Chat Completions `ChatOptions.Reasoning` is
**inert** — the request body is byte-identical with and without it — because those providers send
`reasoning_content` unsolicited. It is load-bearing only on the Responses path. So the checkbox means one
thing to the user on every provider.

**The flag does not gate display.** `ProjectStreamedContent` projects any `TextReasoningContent` it sees, so
an unticked model that reasons anyway still shows its thinking. The flag only *asks*.

## Alternatives considered

**Persist reasoning and replay it on reload.** Rejected by the user up front, and the measurements made the
cost concrete: reasoning is verbose, the session is capped at 2 MB (ADR 0004), and persisting it would carry
the model's deliberation into public share links (ADR 0035), the search projection (ADR 0061) and the
IndexedDB mirror (ADR 0032).

**Set `includeReasoningEncryptedContent: true`.** Falsified by measurement (b) — it does not produce visible
reasoning, and it would write encrypted reasoning blobs into our own session.

**A Preferences toggle for reasoning visibility.** Rejected: it is collapsed by default, so the cost of
showing it is one line of chrome, and a setting for that is a setting nobody would find.

**Put `SupportsReasoning` on `ModelCapabilities`.** Rejected for the Foundry short-circuit above.

**A source-text pin for the call site.** The plan expected the wiring to be untestable and settled for pinning
the source text. It is testable: `ConfigureOptions` wraps the client in a `ConfigureOptionsChatClient` that
`GetService` finds through the delegating chain, so the built client is asserted directly, with a
non-reasoning control. Both mutations — hardcoded `false` and hardcoded `true` — fail.

## Consequences

**Reasoning is model output rendered outside the sanitizer.** The reply goes through markdown + DOMPurify;
reasoning deliberately does not, so its only sink is `textContent`. An `innerHTML` there would put
unsanitized model output into the DOM, and a test scoped to the renderer pins it.

**It must not be spoken.** The `textDelta` arm calls `voice.onDelta(chunk.text)`; copying that line into the
reasoning arm is the obvious mistake and a real one — hands-free voice (ADR 0037) would read the model's
deliberation aloud ahead of its answer, and with a speech model configured (ADR 0038) would pay a third party
per character to do it. Guarded by a test scoped to that dispatch arm, mutation-checked.

**A recovered turn has to settle the disclosure too** (ADR 0046). When a backgrounded PWA's stream dies,
the turn still completes server-side and `recoverPersistedReply` adopts the persisted reply — but the `done`
event that closes the disclosure went down with the socket, so without an explicit `finishReasoning` there
the recovered answer sits under a pulsing "Thinking…" forever. The reasoning itself is *not* recovered, and
cannot be: it is never persisted. Whatever streamed before the drop stays as it is.

**No new egress, and slightly less of it.** Reasoning already crossed the wire; it was being thrown away.
Keeping it out of `reply` *removes* content that would otherwise have been re-sent on the follow-up call.

**Existing conversations keep the reasoning already written into them.** The strip applies at write time, so a
session persisted before this ships stays as it is until its next turn re-serializes. Bounded and self-healing
on any active chat; named here rather than migrated.

**It still reaches `/logs` in full** (ADR 0054), which captures span content regardless — as it does for every
other kind of conversation content.

**Reasoning tokens are not metered.** The OTel emitter emits no reasoning token count, so that field stays 0
in the ADR 0031 usage dashboard. Separate work.

**Infra: none.** No new secret, Cosmos container, grain, folder, `excludedPaths` entry, Bicep change, package
or JS module — rendering went into `thread.js`, which is already in `SHELL` and already reachable from
`main.js`, avoiding both the SHELL all-or-nothing trap and the orphan-module-invisible-to-asset-guards trap.
No migration: `[Id(7)] SupportsReasoning` is trailing and defaulted. Service-worker cache bumped
`agent42-shell-v34` → `-v35`.

**Out of scope:** persisting or replaying reasoning; a visibility toggle; reasoning-effort selection
(low/medium/high) as a user-facing control; metering reasoning tokens; redacting reasoning from `/logs`;
lifting the `OpenAI` 2.12.0 pin.

## Verification

`dotnet build Agent42.slnx` clean, `dotnet format --verify-no-changes` clean, `dotnet test Agent42.slnx` —
**1303/1303 green** (1293 before this branch), including under `--collect:"XPlat Code Coverage"`.
New-code coverage **100.0% (47/47 lines, threshold 80)**.

**Tests.** 5 in `tests/Agent42.Tests/ReasoningStreamTests.cs` (AC1 and the two Step-1 measurements, with a
negative control proving no `reasoning` field is sent when none is asked for); 4 in `AgentGrainTests.cs`
(AC2 on both run paths, AC2b, AC3); 1 in `AgentApiTests.cs` (AC4 — the wire shape); 2 in
`ModelProviderTests.cs` (AC5, helper and call site); 1 in `ProvidersApiTests.cs` (AC6); 6 in
`ReasoningAssetsTests.cs` (AC7, plus the ADR 0046 recovery guard); AC8 and AC10 extend existing tests.

**Mutation-checked**, five times, because each guard is the only thing holding a decision in place:

| Mutation | Result |
|---|---|
| Remove `StorageInputResponseMessageFilter` from `BuildAgent` | both AC2 tests fail; the other two reasoning grain tests still pass (different properties) |
| `RequestsReasoning(…)` → hardcoded `false` | AC5 call-site test fails |
| `RequestsReasoning(…)` → hardcoded `true` | AC5 call-site test fails (the control catches it) |
| Add `voice.onDelta(chunk.text)` to the reasoning arm | AC7 no-speak test fails |
| Remove `finishReasoning` from `recoverPersistedReply` | the ADR 0046 recovery guard fails |

**A test-authoring trap worth naming:** the negative assertions in `ReasoningAssetsTests` are tripped by the
*comments* in the code under test, which name `voice.onDelta` and `innerHTML` in order to say "not these".
The only way to make a naive `DoesNotContain` pass would be to delete the comment documenting the rule — the
same shape as the comment-blind `IndexOf` guards ADR 0065 records. They go through a `CodeOnly` helper that
strips `//` line comments first.

**Offline browser leg — run, not skipped.** Headless Chrome over CDP against DevHost (`CannedChatClient`
gained a prompt-marker reasoning stream), **17/17 checks**, both themes:

- the disclosure is a `<details>`, **collapsed**, positioned **above** `.prose`, present *while the reply is
  still streaming*;
- summary reads `Thinking…` mid-stream and `Thought process` on `done`, with the running class dropped;
- 5 streamed deltas accumulate into one 394-char body — the append-into-an-existing-element path, not just
  the create path;
- reasoning is **not** in the reply text (`leaked=False`);
- per-turn scoping: a second, non-reasoning turn leaves **exactly one** disclosure and does not extend the
  first turn's body;
- the provider dialog renders **6** capability checkboxes, labels
  `[tools, vision, image gen, embeddings, speech, reasoning]`, reasoning **last** — which is what pins
  `capDefs` against the positional destructuring in `collectModels`.

The browser pass also caught a defect no assertion would have: `▸`/`▾` are **not in the mono font stack** and
fell back to a dot, so the expand affordance read as a bullet. The marker is now drawn with CSS borders and
rotated on `[open]`, which is font-independent.

**Unverified residual, named as ADR 0038 did for TTS:** DevHost wires a `StaticModelCatalog` and a canned chat
client and has no Foundry Responses endpoint, so the *Foundry* half — a real reasoning deployment answering a
real `reasoning.summary` request — is prod-only and unverifiable in CI. What is verified in CI is the request
the adapter builds and the content type it surfaces, against a stub transport.
