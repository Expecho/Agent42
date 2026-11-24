---
id: 0057
date: 2026-08-29
status: accepted
tags: [memory, egress, tools, selection, third-party, no-migration]
supersedes: []
extends: [0034, 0048, 0053]
amends: [0021, 0056]
amended_by: [0131]
---

# Long-term memory is a selectable tool

## Context
ADR 0056 shipped Hindsight long-term memory as **always-on**. Its Decision reads: *"A Hindsight context
provider is attached to every agent whenever an API key is configured."* The only way to turn it off is
for the **operator** not to set `Hindsight:ApiKey` — a deployment-wide switch, not a user-facing one.

That was a deliberate scope call, taken with the user up front. ADR 0056 records it twice:

- Scope call 1 (`0056:24`): *"**Port it *and* wire it in** — not a parked library, and not a
  Preferences UI."*
- Alternatives (`0056:162-164`): *"**A per-user opt-out, or a `ToolCatalog` key.** Scoped out by the
  user. Given the egress and the durable-injection risk this is a plausible and arguably desirable
  follow-up; it is the first thing to add if this graduates from experiment."*

(The stronger wording — "no Preferences tab, no per-user enable/disable, no `ToolCatalog` key" — lives
in the plan that produced 0056, `.agentic/specs/completed/plan-hindsight-long-term-memory.md:571-575`,
not in the ADR. ADR 0056 has **no "Out of scope" section**; do not go looking for one.)

**That scope call is now reversed by the user.** The reasons 0056 gave for wanting the reversal are its
own Consequences. Memory is the widest egress in the app after ADR 0054's `/logs`: with a key set,
*every* non-incognito turn POSTs the user's message and the assistant's reply to a third party —
automatic, always-on, invisible in the UI, unlike ADR 0013 (only when the user *picks* a third-party
model) or ADR 0027 (only when the model *calls* fetch). And because retain **persists** the transcript,
a prompt injection landing once becomes a durable memory re-injected into every future conversation, a
risk 0056 itself called *"the strongest argument for the per-user opt-out this iteration does not have"*
(`0056:129`).

## Decision
Long-term memory becomes a **selectable tool**: a new static `ToolCatalog` key, `memory`, controllable
at all three ADR 0021 layers — the user default (Preferences → default tools), the per-conversation
override (the composer's "Tools" popover, non-agent chats), and the per-agent snapshot.

Deselecting it attaches **no provider at all**, so the turn makes **zero** Hindsight calls: no recall
*and* no retain. The operator's `Hindsight:ApiKey` remains the outer gate and the only one that stops
the egress deployment-wide; the key narrows within it.

The shape is one catalog key. **No new grain, endpoint, state field, secret, container, Bicep or
frontend change** — the key rides the machinery ADRs 0021/0026/0048 already built. `EnabledTools` is an
existing nullable list on `UserProfile` / `AgentDefinition` / `AgentBinding` / `AgentGrainState`; no new
`[Id(n)]`, no migration of the state shape.

### Five decisions inside that, each with its rejected alternative

**1. `memory` goes IN `AllSelectableToolKeys` — ON by default.** A user who has never narrowed their
picker resolves through the `null == all` sentinel and gets memory, exactly as before this ADR.
*Rejected: opt-in-only.* It would have been the safer default in isolation, but the operator's
`Hindsight:ApiKey` already gates everything underneath, and a key that behaves differently from every
other key in the catalog is a worse trap than a uniform one.

**2. Off means both recall AND retain.** No provider is attached — the incognito semantics of ADR 0056
scope call 4, reached by the same mechanism. *Rejected: "recall only, keep retaining."* It leaves the
always-on egress and the durable-injection risk fully intact while the UI says memory is off, which is
worse than not offering the control.

**3. The checkbox renders even with no `Hindsight:ApiKey` configured** — today's production state.
Precedent: `code_interpreter` renders on models that cannot run it. The catalog expresses *intent*; an
outer gate decides. *Rejected: hide it when unconfigured* (the `GET /api/push/config` → `enabled:false`
shape). It would make the per-user catalog endpoint depend on operator config for the first time, and a
key that appears and disappears interacts badly with stored explicit selections. Preferences →
Configuration already reports memory's real state, so the truth stays discoverable.

**4. The picker, not a `UserProfile` toggle.** *Rejected: the `SuggestFollowUps` shape* — a
null-sentinel `bool?` on `UserProfile` (ADR 0036). It is simpler, and it is more honest about memory not
being a tool. But it buys only **one user-wide switch**. The picker also buys per-conversation and
per-agent control, and "this agent doesn't remember" is worth having.

**5. The gate is read at BUILD time, not through a delegate.** `_ephemeral` must stay
`IsEnabled = () => !_ephemeral` — it is turn-scoped and nothing rebuilds between turns of a warm
activation (the ADR 0056 gotcha). The *selection* is different: the two layers a user changes
deliberately — the per-conversation override (`SetEnabledToolsAsync`, which does the
snapshot/rebuild/restore/write dance explicitly) and the agent snapshot (`BindAgentAsync`) — both
**rebuild the agent**, so a value read in `BuildMemoryProvider` cannot go stale on them. The two gates
have deliberately different lifetimes; folding either into the other is wrong.

**The user-default layer is the exception, and it is stale by design — see Consequences.**

### Memory is the first non-tool key, and the documented exception to `selection ∩ capability`
`ToolCatalog`'s class docstring asserted two things that this ADR falsifies, and both were rewritten
rather than left silently wrong:

- *"the RAG provider is not a tool … so it never appears in this catalog"* — still true of RAG, but its
  implication (non-tools never appear here) is not. Memory is not a tool and **is** in the catalog.
- *"The intersection — selection ∩ capability — keeps the ADR 0013/0015 gates authoritative."* Memory is
  the exception. It gates an `AIContextProvider` added outside `BuildAgent`'s tool list, so it never
  passes through `Tools = capabilities.SupportsTools ? tools : []`. **A text-only model with `memory`
  selected still gets memory.**

The gating story is now a **three**-way split, not tools-vs-not:

| provider | is a tool? | gated by capability | gated by selection |
|---|---|---|---|
| `AgentSkillsProvider` | yes (`load_skill`) | yes | yes (`skill:{name}` / `skills`) |
| RAG `TextSearchProvider` | no | no | no — document presence only (ADR 0008) |
| Hindsight memory | no | **no** | **yes** (`memory`) |

`memory` maps to **no** `ToolNameToKey` entries and must never gain any. That is safe because the only
guard over that map, `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`, runs name → key
only; there is no reverse guard requiring a descriptor to own tool names.

The label is **"Long-term memory"**, not "Memory": plain "Memory" reads as the conversation's own
history (the ADR 0004 session), which this is not, and that misreading makes the checkbox look like it
wipes the current chat.

The gate lives in `BuildMemoryProvider`, not as a fourth `if` in `BuildAgent`'s `contextProviders`
block. `cognitive_complexity_max` is 15, enforced at build time by S3776 at `error` severity (ADR
0050/0051), and ADR 0056 divergence #3 records this same feature already tipping a composition root over
by adding one branch. Passing an argument adds none.

## Consequences

**The key lands OFF for anyone who has ever pressed Save, and there is deliberately no migration.**
This is the ADR 0048/0053 divergence: `null == all` only helps a user who has never narrowed a
selection, because the picker materialises "all" as the full **explicit** list, so a stored selection
cannot contain a key that did not exist when it was saved. ADR 0049's auto-enable is not a precedent —
an upload is an event to hang a rewrite on; a shipped key is not. The remedy for regular chats is one
press of Save in Preferences → Tools, and it is documented rather than automated.

**For agent chats it is worse than the calendar precedent, permanently, with no in-UI remedy — and that
was put to the user, who chose to accept and document it.** `BindAgentAsync` **always** materialises a
concrete list (`binding.EnabledTools ?? ToolCatalog.AllSelectableToolKeys`), and a re-bind **returns
early** because a bound persona is immutable (ADR 0003). Meanwhile the composer's Tools popover is
hidden on agent chats (`agents.js:227` sets `toolsBtn.hidden = true`). So *every conversation already
bound to an agent* holds a pre-`memory` explicit snapshot and cannot re-enable it from the UI at all.
"One press of Save" is **false** for that layer. New agent chats are fine — a never-narrowed agent
re-materialises the current key list at bind time. The stuck case is a **continuous** agent (ADR 0016):
one permanent grain, so memory is off there until `DELETE /api/conversations/agent-{agentId}`.

It **fails safe**: the widest egress in the app stays off on pre-existing agent chats until deliberately
re-enabled. Two remedies were considered and rejected — un-hiding the popover on agent chats
(contradicts ADR 0003/0021's immutable bind snapshot and would need its own ADR), and back-filling the
key into existing snapshots (silently re-enables that egress on conversations nobody opted in for).

**What this ADR does NOT fix.** Deselecting memory stops *future* recall and retain. It does **not**
remove what was already retained: ADR 0056's no-delete-path residual is untouched, and remains
unmitigable rather than merely unmitigated. A user who deselects memory after an injection landed has
stopped the bleeding, not cleaned the wound. The operator's `Hindsight:ApiKey` also remains the only
gate that stops the egress for the whole deployment.

**Switching the Preferences default off does not reach an already-warm chat until it deactivates, and
for this key that matters more than for any other.** `_userDefaultEnabledTools` is assigned only in
`OnActivateAsync` (`AgentGrain.cs:224`) and nothing rebuilds a warm grain when `UserProfile.EnabledTools`
changes. This is pre-existing, documented, accepted staleness — `docs/features/tool-selection.md` has
said so since ADR 0021, and it applies identically to every key. But the *stakes* differ: for
`web_search` a stale default means a tool lingers one activation too long, whereas for `memory` it means
a user who has just switched off the widest egress in the app **keeps POSTing their conversation to a
third party** in every already-warm chat until it deactivates. The per-conversation override is the
immediate control (it rebuilds eagerly), and it is the one the composer exposes.

This was **not** fixed here — making the user default eagerly invalidate warm grains is a change to ADR
0021's resolution machinery affecting all eight keys, and it needs its own decision. It is recorded
rather than hidden, and the earlier draft of this ADR asserted the opposite ("can never go stale"),
which a review caught. Do not restore that claim.

**Reminder and agent-run turns still write memory when selected.** ADR 0056's inclusion of the two
async paths is unchanged — memory is written on turns with no user present, if the resolved selection
keeps it on. Incognito chats remain fully excluded, by the separate turn-scoped gate.

**Config health stops over-promising.** The present-branch message used to read *"long-term memory
enabled — every non-incognito turn is recalled and retained"*, which is now false for a user who has
deselected it. It reports the key as configured and points at the per-user control instead. Nothing
pinned that string before, so AC12 is a **new** assertion, not an updated one.

**No frontend change, and therefore no `sw.js` `CACHE` bump.** Labels are server-driven
(`renderToolChecks` uses `tool.label`), `allToolKeys()` is catalog-derived, and `memory` carries no
`mcp:`/`skill:` prefix so it renders as a builtin automatically. The "every MR touching `wwwroot/js/`
must bump `CACHE`" rule is conditioned on touching those files; this MR does not. `agent42-shell-v23`
is deliberately unchanged, and the executor asserted the absence of a `wwwroot/` diff rather than
assuming it.

## Alternatives considered
- **A `UserProfile` null-sentinel `bool?`, the ADR 0036 `SuggestFollowUps` shape.** See decision 4.
- **Opt-in-only (`memory` outside `AllSelectableToolKeys`).** See decision 1.
- **Hiding the key when `Hindsight:ApiKey` is unset.** See decision 3.
- **"Recall only, keep retaining."** See decision 2.
- **Migrating existing explicit selections to include the key.** Rejected — see Consequences.
- **Un-hiding the composer popover on agent chats**, or a re-sync path from an edited agent definition
  into an already-bound conversation. Both contradict ADR 0003/0021's immutable bind snapshot; either
  would need its own ADR.
- **Per-user Hindsight API keys** (the ADR 0018/0024 DP-encrypted overlay shape). Still out of scope,
  as in 0056.
- **A delete path for memories, or any UI to inspect a bank.** Still out of scope; this ADR gates the
  provider, not the bank, which stays one per user (ADR 0056 scope call 3).

## Verification
Seven new tests. Every "assert nothing was recorded" test here shares the trivial-pass shape ADR 0056
divergence #5 recorded — a gate stuck permanently OFF satisfies it without executing anything — so the
set was **mutated in both directions** (2026-08-29) rather than assumed:

| test | gate stuck OFF | gate stuck ON |
|---|---|---|
| `MemoryDeselected_NeitherRecallsNorRetains` | pass (trivially) | **FAIL** |
| `MemorySelected_RecallsAndRetains` | **FAIL** | pass |
| `MemorySelection_IsNotIntersectedWithCapability` | **FAIL** | pass |
| `TogglingMemoryMidChat_DetachesOnTheSameActivation` | **FAIL** | **FAIL** |
| `LegacyAgentSnapshot_DoesNotEnableMemory` | pass (trivially) | **FAIL** |

`TogglingMemoryMidChat_DetachesOnTheSameActivation` is the load-bearing one — the only test that fails
in **both** directions, and the only one that proves the *rebuild* rather than merely the predicate. It
must stay the two-turn shape; written as "deselect, then one turn, assert nothing" it collapses into
`MemoryDeselected_…` and proves nothing. It is the `MemoryResumes_…` of this feature.

Measured, mutation 1 (selection term deleted): 3 failed — exactly the three predicted. Mutation 2
(provider never attached): 8 failed — the three predicted new ones plus the five pre-existing ADR 0056
memory tests, with `MemoryDeselected_` and `LegacyAgentSnapshot_` passing trivially as recorded.

**AC13 has no automated proof and that is recorded, not hidden**: "the picker renders memory with no
frontend change" is an assertion about the *absence* of a diff, in the ADR 0043 `enterkeyhint`
tradition. See the unverified residual below.

## Divergences from the plan
All six planned steps executed as written and **every one of findings A–J was confirmed rather than
corrected** — including finding E, the one a prior critic pass had already rewritten: only
`Tools_Catalog_ReturnsSelectableKeys` failed, and the four tests a careless executor would have
"fixed" (`AllKeysExpanded()`'s two consumers and the two `Sanitize_OrdersStaticFirst_*`) passed
untouched. `AllKeysExpanded()` was not edited. Three things are worth recording:

1. **Finding D's ≈14/15 cognitive-complexity estimate was never spent, so it stays unmeasured.** Putting
   the gate inside `BuildMemoryProvider` meant `BuildAgent` gained **zero** branches; `dotnet build`
   stayed at 0 errors and S3776 never fired. That was the point of finding D.
2. **One unrelated test flakes, and it is PROVEN pre-existing rather than assumed.**
   `OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline` (ADR 0054) failed
   on 2 of 11 full-suite runs on this branch. Rather than argue from "the diff touches no telemetry
   files", clean `main` was checked out into a throwaway worktree and run five times: it failed **2 of
   5**, the same test, with the same shape. So the flake is entirely pre-existing and this branch — which
   adds seven span-producing grain tests — does **not** worsen it (2/11 here vs 2/5 there). Both failing
   runs on each side were the slow ones (38–55 s vs a ~27 s median), which fits the likely mechanism: the
   test's `Assert.Empty(buffer…)` reads a process-wide OTel listener that can catch a span from a
   concurrently-running test. **Not fixed here** — it belongs to ADR 0054's suite and wants its own
   change; flagged rather than absorbed.
3. **A review pass caught the ADR asserting the opposite of the truth on the user-default layer** (see
   Consequences). The plan had flagged it as a "caveat to verify, not assume"; the first draft asserted
   the absolute claim in three places. All three were corrected.

## Unverified residual
**The DevHost browser pass was NOT run.** ADR 0052 and 0054 both ran a headless-Chrome-over-CDP leg and
recorded measured values; this change did not, so AC13 — "the picker renders the memory checkbox and it
round-trips" — rests on indirect evidence only: labels are server-driven (`renderToolChecks` uses
`tool.label`), `allToolKeys()` is catalog-derived, `memory` carries no `mcp:`/`skill:` prefix so it
renders as a builtin, and `Tools_Catalog_IncludesMemory` proves the descriptor reaches the wire with the
right label. What is untested is only the last hop: the checkbox actually painting and saving. Given
that the alternative was to claim a pass that was not run, this is stated plainly instead.
