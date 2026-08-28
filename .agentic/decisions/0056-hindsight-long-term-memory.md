---
id: 0056
date: 2026-08-28
status: accepted
tags: [memory, egress, prompt-injection, third-party, secrets, packaging, open-source, licence]
supersedes: []
extends: [0004, 0012, 0013, 0034, 0039, 0040, 0050]
amends: [0008]
---

# Long-term memory across conversations, via a .NET package we publish

## Context
Every Agent 42 conversation starts from nothing. Document RAG (ADR 0007/0008) gives a chat access to
files *uploaded into that chat*; the session (ADR 0004) carries history *within* one conversation.
Nothing carries across. A user who explained their situation last week explains it again this week.

Vectorize publish a Python package, `hindsight-agent-framework`, that solves exactly this for Microsoft
Agent Framework agents: a context provider that recalls relevant memories into the agent's instructions
before each run and retains the run's transcript after it. There is no .NET equivalent.

Four scope calls were taken with the user up front:

1. **Port it *and* wire it in** — not a parked library, and not a Preferences UI.
2. **Operator config, optional secret** — `Hindsight:*` plus a `HINDSIGHT_API_KEY` GitHub secret, the
   `GOOGLE_MAPS_API_KEY` shape: no key means the app boots normally and memory is off.
3. **One bank per user** — the bank id is the userId parsed off the grain key.
4. **Memory fully disabled in incognito chats** — no recall *and* no retain.

And four more on packaging: a **new public repo** rather than a project inside this private one; the
package id `Hindsight.AgentFramework.Net`; **MIT with upstream attribution**; published from CI on a
`v*` tag.

## Decision
A Hindsight context provider is attached to every agent whenever an API key is configured, recalling
this user's memories into the model's instructions before each call and retaining the turn afterwards.

The port ships as a **standalone open-source package in its own public repository**,
https://github.com/Expecho/Hindsight.AgentFramework.Net, published to nuget.org as
`Hindsight.AgentFramework.Net`. Agent42 consumes it by `PackageReference` like any third-party
dependency.

### ⚠ This AMENDS ADR 0008's gating discipline. It does not follow it.
It is tempting to present this as "the ADR 0008 always-on-provider precedent". That is wrong, and the
mistake matters enough to state plainly.

ADR 0008 lists **"always-on but *ungated*"** under *Alternatives considered* and **rejects it**. Its
`DocumentNames.Count > 0` gate — a document-free chat makes zero embedding calls — is the load-bearing
property that made an always-on provider safe to adopt.

This feature has **no per-conversation analogue**, because memory spanning conversations is precisely
what scope call 3 asked for. It therefore adopts the ungated shape 0008 rejected. The only gates left
are operator-wide (is a key configured) and per-turn (is this chat incognito).

And the per-turn cost is worse, not better. ADR 0008 was weighing one **embedding** round trip; here it
is **two third-party HTTP calls carrying conversation content** — strictly worse on egress and no
better on blast radius. Do not write "following ADR 0008" anywhere.

### Why a separate public repo, and why the licence is not optional
The user's stated goal was that other people can use this. A package whose source nobody can read, with
no issue tracker and no licence grant, does not meet it — and this repo is private and has no LICENSE
file.

More than preference, it is an obligation. Upstream is **MIT** (`vectorize-io/hindsight`, root
LICENSE). A port is a derivative work, so MIT requires shipping their copyright notice and permission
text; a link does not satisfy it. The new repo therefore carries **both** its own MIT LICENSE and a
NOTICE reproducing Vectorize's notice verbatim, alongside a plain statement that this is an independent
port, **not affiliated with or endorsed by Vectorize**. That statement is also the mitigation for a
naming risk the user accepted: the only existing `Hindsight.*` package on nuget.org, `Hindsight.Behave`,
is an unrelated behaviour-tree library by a different author, and the prefix is unreserved.

The assembly and namespace are `Hindsight.AgentFramework` while the **package id** carries a `.Net`
suffix — it disambiguates the package from the Python one without uglifying every `using`.

### No Agent42 port over the package
There is deliberately **no `IMemoryService` and no Backend adapter folder**, unlike `IGeocoder`
(ADR 0012) or `ICalendarService` (ADR 0053). The package's `IHindsightClient` already *is* that seam —
an interface over the third party, faked in tests exactly as a port would be. A second abstraction over
it would buy nothing. What Agent42 adds is a one-line
`record HindsightIntegration(IHindsightClient? Client, string? Mission)`, whose **null carries meaning**:
no key means no provider is attached at all, so a turn makes *zero* Hindsight calls rather than calls
that quietly fail.

### The ephemeral gate is a delegate, and that is the single most important detail
`BuildAgent` runs on **activation** and on five rebuild paths — **never per turn**. `_ephemeral` is
**turn-scoped**, set at the top of `ChatAsync` and cleared in its `finally`. A provider that captured
`_ephemeral` as a *value* at construction would freeze whatever the flag happened to be when the agent
was built, and then be wrong for every later turn of a warm activation — silently retaining an
incognito chat, or silently refusing to remember a normal one.

`HindsightOptions.IsEnabled` is a `Func<bool>` evaluated per invocation for exactly this reason, and
Agent42 passes `IsEnabled = () => !_ephemeral`. This is the `BuildDocumentSearchProvider` shape — a
closure over the grain, read at invoke time.

`IsEnabled` is the one addition beyond the Python surface, and it is a genuine generalization rather
than an Agent42 wart: "do not remember this particular exchange" is a normal requirement.

## Consequences

### The headline: an always-on egress, wider than anything shipped before
With a key configured, **every non-incognito turn** POSTs the user's message *and* the assistant's
reply to a third party, and sends the user's message again as a recall query.

Unlike ADR 0013 (only when the user *picks* a third-party model) and ADR 0027 (only when the model
*calls* fetch), this is **automatic, always-on, and invisible in the UI**. It carries whatever the
conversation carries: document excerpts, the user's street address (ADR 0012), calendar content
(ADR 0053).

That is why it is **off by default and off in production until a key is deliberately set**. Not
setting the key is the only off switch there is.

Two precision points, so the risk is neither overclaimed nor understated:

- **Image-generation turns are excluded.** `RecordImageTurnAsync` builds a separate `ChatClientAgent`
  with no `AIContextProviders`.
- **The reminder and agent-run paths are included.** `_ephemeral` is reset on both, deliberately — so
  memory is recalled and retained on turns where **no user is present**.

### Worse than a normal injection channel: the injection becomes durable
Recalled memories are untrusted third-party text injected into the **system instructions**. Because
retain *persists* the transcript, an injection landing once — from an ADR 0027 followed link
(allowlist-**exempt**), an ADR 0024 MCP tool result, or an ADR 0049 uploaded SKILL.md — becomes a
**durable memory re-injected into every future conversation of that user**, including reminder and
agent-run turns with nobody watching.

Cross-conversation, persistent, and with **no delete path**: deleting or inspecting memories is out of
scope for this iteration, which is exactly what makes this unmitigable here rather than merely
unmitigated. That is a materially different risk class from ADR 0011/0027/0049, and it is the strongest
argument for the per-user opt-out this iteration does not have.

### The outbound span is NOT suppressed — a deliberate exception
ADR 0053 added `CalendarSpanSuppressingHandler` because a calendar id in the URL path is a real email
address, and OpenTelemetry redacts a query string but not a path. The gotcha states that rule
absolutely: *any new client whose path carries an identifier needs the same treatment.*

This is an exception. The path here carries the **Easy Auth principal id** — opaque, not an email, and
already exported deliberately as a `user.id` span attribute (ADR 0009/0010). Query text rides in the
request **body**, which OTel does not capture. Recorded in that gotcha entry as well as here, so the
rule does not read as absolute when it no longer is.

### This repo now has an external consumer
CLAUDE.md's "personal/experimental project; no external consumers" is no longer wholly true. A
published package creates a support surface and a compatibility obligation: the public API of
`Hindsight.AgentFramework` is now something other people's builds depend on. What is owned is that
package's API and its issue tracker — **not** Agent42's internals, which stay private and unstable.

### Smaller consequences
- **Retain is always sent `async: true`.** Hindsight runs LLM fact extraction inline otherwise, and
  `StoreAIContextAsync` is awaited inside the agent run — a synchronous retain would stall the turn by
  seconds. `AgentGrain` is non-reentrant, which is also why the named `HttpClient` calls
  `RemoveAllResilienceHandlers()`: four attempts at a stalled call is four times a stalled turn.
- **Nothing here can break a turn.** Both hooks swallow every exception and the HTTP client never
  throws; a memory outage degrades the agent to no memory.
- **A failed run is not retained** (`InvokedContext.InvokeException`) — no Python counterpart, because
  the Python context-provider API exposes no such signal.
- **Recall costs nothing against the 2 MB session cap** (ADR 0004). Injected text lands in
  `ChatOptions.Instructions`, never in the session, and does not accumulate across turns.
- **Config health** gains a presence check and a free `/health` probe — free because that endpoint is
  unauthenticated, unlike the billable geocoding and context.dev probes.

## Alternatives considered
- **A per-user opt-out, or a `ToolCatalog` key.** Scoped out by the user. Given the egress and the
  durable-injection risk this is a plausible and arguably desirable follow-up; it is the first thing to
  add if this graduates from experiment.
- **Per-user Hindsight API keys** (the ADR 0018/0024 DP-encrypted overlay shape). Out of scope.
- **A project inside this repo rather than a package.** Rejected: it fails the stated goal and leaves
  the MIT attribution obligation awkward in a private repo with no LICENSE.
- **Suppressing the outbound span.** Considered and rejected — see above.
- **Porting `exclude_sources`.** Ported as *nothing*. Measured on `Microsoft.Agents.AI` 1.19.0:
  injected instructions never appear as messages, so there is no feedback loop to defend against.

## Divergences from the plan, all measured
1. **`InvokingContext` has no `RequestMessages`.** The plan's finding (A) said it did. Reflection plus a
   spike against a real `ChatClientAgent` show it exposes only `Agent`, `Session` and `AIContext`; a
   run's messages arrive on **`AIContext.Messages`**. The provider reads them there.
2. **Seven silo configurators, not three.** The plan's B3 table named three; `AgentGrain` is activated
   by seven. Missing two of them failed activation on those silos — which is the good failure mode for
   a mandatory dependency, and how they were found.
3. **The registration lives in `HindsightRegistration`, not inline in `Program.cs`.** Adding the
   `if`/`else` inline took the composition root from 15 to 16 cognitive complexity and **failed the
   build** on S3776. Extracting into the feature's folder is what ADR 0050/0051 established and what
   `DocumentIndexRegistration` already does. The gate worked exactly as designed.
4. **`HindsightIntegration` carries `Mission` as well as `Client`.** Otherwise `Hindsight:Mission` would
   be read from config and dropped on the floor.

## Unverified residual
**Neither repo's tests ever talk to a real Hindsight server.** Both stub the HTTP layer; the wire
contract is implemented against the upstream OpenAPI document and pinned by tests that assert URLs and
bodies, not by a round trip. DevHost registers `HindsightIntegration(null)`, so memory is off offline
and there is no browser leg either. In the ADR 0053 tradition this is recorded rather than papered
over: the first real call this code makes will be in production, with a key set.
