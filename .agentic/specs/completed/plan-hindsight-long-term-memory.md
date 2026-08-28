# Plan — Hindsight long-term memory: a reusable .NET package, consumed by Agent42

planned-against: main @ f58ecf8

## Overview

Port the Python package `hindsight-integrations/agent-framework` from
[vectorize-io/hindsight](https://github.com/vectorize-io/hindsight) to C#, **as a standalone
open-source NuGet package in its own public repo**, and then consume that package from `AgentGrain`
so Agent42 conversations gain **persistent long-term memory across chats**.

Before each model call, memories relevant to the user's message are recalled from a Hindsight *bank*
and injected into the agent's instructions; after each run, the turn's transcript is retained so
future runs build on it. No MCP, no tool the model must remember to call.

The work spans **two repositories** and must run in that order:

- **Phase A — new public repo `Hindsight.AgentFramework.Net`.** The reusable package: provider, HTTP
  client, options, tests, MIT licence, CI, published as `v0.1.0`. Knows nothing about Agent42,
  Orleans, or Azure.
- **Phase B — this repo.** Consume the published package, wire it into `AgentGrain`, config, DI,
  infra, docs.

**Phase B's `PackageReference` cannot resolve until Phase A has published.** That ordering is hard,
not stylistic — see "Bootstrapping" below.

## Interview decisions (the user's explicit scope calls)

Round 1 — the feature:
1. **Port *and* wire it in** — not a parked library, and not a Preferences UI.
2. **Operator config, optional secret** — `Hindsight:*` + a `HINDSIGHT_API_KEY` GitHub secret,
   the `GOOGLE_MAPS_API_KEY` shape: **no key ⇒ the app boots normally and memory is off**.
3. **One bank per user** — `bankId` = the userId parsed off the grain key.
4. **Memory fully disabled in incognito chats** — no recall *and* no retain.

Round 2 — the packaging:
5. **A new public repo**, not a project inside this private one. Agent42 consumes it via
   `PackageReference`.
6. **Package id `Hindsight.AgentFramework.Net`.** Verified available on nuget.org, and the
   `Hindsight.` prefix is **not reserved** (the sole existing `Hindsight.*` package,
   `Hindsight.Behave`, shows `verified: false`), so `nuget push` will not be rejected on prefix
   grounds. Note `Hindsight.Behave` is an **unrelated behaviour-tree library by a different owner** —
   the package description must therefore be unambiguous about what this is (see A2).
7. **MIT, with upstream attribution.**
8. **Publish from CI on a `v*` tag**, using a `NUGET_API_KEY` secret.

## Licensing — a hard requirement, not a nicety

- **Upstream is MIT** (`vectorize-io/hindsight`, root `LICENSE`; the `agent-framework` integration
  folder carries no licence of its own, so it inherits the root). A port is a derivative work, and
  **MIT obliges us to ship their copyright notice and permission text**. This is not optional and
  cannot be satisfied by a link.
- **This repo has no LICENSE file and is private.** That is why the package goes to a new public
  repo: a package whose source nobody can read, with no issue tracker and no licence grant, does not
  meet the stated goal ("so other people can use it").
- The new repo therefore ships **both**: its own `LICENSE` (MIT, the user's copyright) **and** a
  `NOTICE` / `THIRD-PARTY-NOTICES.md` reproducing Vectorize's MIT notice and stating plainly that
  this is an independent .NET port of their Python package, not affiliated with or endorsed by them.

---

# Phase A — the package (new public repo)

> **Execution gate.** Creating a public GitHub repository and publishing to nuget.org are
> outward-facing and effectively irreversible (a pushed nuget.org version can be unlisted but never
> deleted). The executor must **confirm with the user before `gh repo create` and again before the
> first `nuget push`**, and must not create either from its own judgement.

## A — Affected areas

All new, in the new repo:

```
LICENSE                     MIT, the user's copyright
NOTICE                      Vectorize's MIT notice + "independent port, not affiliated"
README.md                   usage, and the same non-affiliation statement
.gitignore                  standard dotnet
Directory.Packages.props    CPM, mirroring this repo's convention
Hindsight.AgentFramework.Net.slnx
src/Hindsight.AgentFramework/          assembly + namespace Hindsight.AgentFramework
    Hindsight.AgentFramework.csproj    <PackageId>Hindsight.AgentFramework.Net</PackageId>
    HindsightOptions.cs
    IHindsightClient.cs
    HindsightClient.cs
    NullHindsightClient.cs
    HindsightMemoryProvider.cs
tests/Hindsight.AgentFramework.Tests/
.github/workflows/ci.yml      build + test on push/PR
.github/workflows/release.yml pack + push on v* tag
```

Assembly/namespace `Hindsight.AgentFramework` with package id `Hindsight.AgentFramework.Net` is a
deliberate, ordinary split — the `.Net` suffix disambiguates the *package* from the Python one
without uglifying every `using`.

## A — Steps

### A1 — Repo scaffolding

`gh repo create` (public, **after user confirmation**), then the licence/notice/README trio above,
`.gitignore`, CPM, and the solution. Set up the same branch discipline this repo uses: work on a
branch, never commit straight to `main`.

**Verify:** `dotnet build`

### A2 — The package project

`src/Hindsight.AgentFramework/Hindsight.AgentFramework.csproj`:

- **Multi-target `net8.0;net10.0`.** `Microsoft.Agents.AI` supports both; net8.0 is LTS and is what
  most consumers are on, net10.0 matches Agent42. Do **not** ship net10.0 only — it would make the
  package unusable to most of the audience this whole phase exists for.
- `PackageId` = `Hindsight.AgentFramework.Net`; `PackageLicenseExpression` = `MIT`;
  `PackageReadmeFile`, `PackageProjectUrl`, `RepositoryUrl`, `PublishRepositoryUrl`,
  `EmbedUntrackedSources`, `ContinuousIntegrationBuild`, `IncludeSymbols` +
  `SymbolPackageFormat=snupkg`, SourceLink (`Microsoft.SourceLink.GitHub`).
- **`Description` must be unambiguous** — it is the mitigation for the naming risk the user
  accepted: state that it is an *independent, community* .NET port of Vectorize's
  `hindsight-agent-framework`, providing long-term memory for Microsoft Agent Framework agents, and
  that it is **not affiliated with or endorsed by Vectorize**. This also distinguishes it from the
  unrelated `Hindsight.Behave` package.
- Resolve `Microsoft.Agents.AI` from the feed at add time (`dotnet add package`), **never** by
  writing a version from memory — this repo's hard rule 4, and it applies to the new repo too. Pin
  it in `Directory.Packages.props`. Agent42 is on `1.19.0`; use a floor that permits it.

Public API:

- **`HindsightOptions`** — `BankId` (required), `ApiKey`, `BaseAddress`, `Budget` (`low|mid|high`,
  default `mid`), `MaxTokens` (4096), `Context` (default `"agent-framework"`), `Tags`, `RecallTags`,
  `RecallTagsMatch` (`any`), `Mission`, `AutoRecall`/`AutoRetain` (both true), and
  **`Func<bool>? IsEnabled`** — evaluated per invocation, defaulting to always-on.
  `IsEnabled` is the one addition beyond the Python surface, and it is a **genuine generalization,
  not an Agent42 wart**: "don't remember this particular exchange" is a normal requirement. It is
  what Phase B's incognito gate hangs off (finding C).
- **`IHindsightClient`** — `RecallAsync(bankId, query, ct)`, `RetainAsync(bankId, content, ct)`,
  `EnsureBankAsync(bankId, mission, ct)`. This is the seam consumers fake in tests.
- **`HindsightClient`** — the HTTP implementation (`HttpClient` ctor, `System.Net.Http.Json`, no
  extra package). **Never throws**; logs and degrades.
- **`NullHindsightClient`** — a no-op for disabled/test states. Ships in the package because every
  consumer needs it, exactly as Phase B does.
- **`HindsightMemoryProvider : AIContextProvider`** — ctor `(IHindsightClient, HindsightOptions)`.

**Verify:** `dotnet build`

### A3 — The wire contract

Implement against the upstream OpenAPI spec (in the hindsight repo at
hindsight-clients/go/api/openapi.yaml). Auth is `Authorization: Bearer <apiKey>`; the spec declares
**no** `securityScheme`, so do not go looking for one.

- **Recall** — `POST /v1/default/banks/{bankId}/memories/recall`
  body `{ query, budget, max_tokens, tags, tags_match }` → `{ results: [ { id, text, type, … } ] }`
  `tags_match` ∈ `any|all|any_strict|all_strict|exact`.
- **Retain** — `POST /v1/default/banks/{bankId}/memories`
  body `{ items: [ { content, context, metadata, tags } ], async }` → `{ success, bank_id, items_count, … }`
  (`items` is the only required field; an item's `content` is its only required field).
  **Always send `async: true`** — see finding (D).
- **Create bank** — `PUT /v1/default/banks/{bankId}` body `{ name, mission, … }`. Note it is **`PUT`
  on the id**, *not* `POST /v1/default/banks` (that path is GET-only, a list). Only called when a
  `Mission` is set; once per instance, best-effort, behind a flag (the upstream `_ensure_bank` shape).
- **Health** — `GET /health` (also `/health/ready`, `/health/live`), unauthenticated. Phase B's
  config-health probe target.
- Default base URL `https://api.hindsight.vectorize.io`; self-hosted is `http://localhost:8888`.

**Verify:** `dotnet test --filter HindsightClient`

### A4 — `HindsightMemoryProvider` (the actual port)

Port faithfully from the upstream `provider.py`, against the .NET contract in finding (A):

- `ProvideAIContextAsync`: return early when `IsEnabled?.Invoke() == false` or `AutoRecall` is off;
  build the query from **user-role `RequestMessages` only**, `\n`-joined, falling back to all
  messages when there are none; return an empty `AIContext` if the query is blank or recall yields
  nothing; otherwise return `Instructions =` the header
  (`"## Memories\nConsider the following memories when responding. Ignore any that are not relevant:"`)
  followed by one `- {text}` line per memory.
- `StoreAIContextAsync`: return early on `IsEnabled == false` or `AutoRetain` off; **skip when
  `context.InvokeException is not null`** (the .NET-only addition — never retain a failed run);
  format `RequestMessages` + `ResponseMessages` as `[{role}]\n{text}` blocks joined by a blank line;
  skip when the transcript is whitespace; truncate to the options' cap; retain.
- **Both hooks swallow every exception.** A memory hiccup must never block the agent. This is the
  load-bearing behaviour of the original and two of its tests.

**Verify:** `dotnet test --filter HindsightMemoryProvider`

### A5 — Tests

Mirror the upstream `tests/test_provider.py` contract (AC1–AC9 below) plus the .NET-specific ones,
driving the **real** `AIContextProvider` base class with a fake `IHindsightClient` — so framework
drift fails loudly, which is exactly why the Python suite subclasses the real base too.

**Verify:** `dotnet test`

### A6 — CI and release

- `ci.yml` — restore/build/test on push + PR.
- `release.yml` — on a `v*` tag: pack (`-p:Version` from the tag), push `.nupkg` + `.snupkg` to
  nuget.org with `NUGET_API_KEY`. **The secret is a manual prerequisite the user adds**; the agent
  must not create or handle it.
- Tag `v0.1.0` and publish — **after the second user confirmation**.

**Verify:** the workflow run is green and the package resolves:
`dotnet add package Hindsight.AgentFramework.Net --version 0.1.0` in a scratch project.

## A — Acceptance criteria → test mapping

| # | Acceptance criterion | Test |
|---|---|---|
| AC1 | Recalled memories are injected as a `## Memories` instruction block | `ProvideAIContext_InjectsMemories` |
| AC2 | No memories ⇒ nothing injected | `ProvideAIContext_NoMemories_InjectsNothing` |
| AC3 | The recall query is built from **user** messages only, `\n`-joined | `RecallQuery_IsBuiltFromUserMessagesOnly` |
| AC4 | A recall failure never blocks the agent | `ProvideAIContext_RecallFailure_IsSilent` |
| AC5 | The turn's user + assistant text is retained as a `[role]` transcript | `StoreAIContext_RetainsUserAndAssistant` |
| AC6 | An empty/whitespace transcript is not retained | `StoreAIContext_EmptyTranscript_SkipsRetain` |
| AC7 | A retain failure never blocks the agent | `StoreAIContext_RetainFailure_IsSilent` |
| AC8 | Injected memories are **not** retained (no feedback loop) | `InjectedMemories_AreNotRetained` |
| AC9 | A **failed** run is not retained (`InvokeException`) | `StoreAIContext_FailedRun_SkipsRetain` |
| AC10 | `AutoRecall`/`AutoRetain` false disable their halves | `AutoRecallOff_SkipsRecall`, `AutoRetainOff_SkipsRetain` |
| AC11 | **`IsEnabled` returning false disables both halves** | `IsEnabledFalse_NeitherRecallsNorRetains` |
| AC12 | Recall/retain hit the documented URLs with `Bearer` auth; retain sends `async:true` | `Recall_PostsToTheRecallUrl`, `Retain_SendsAsyncTrue` |
| AC13 | A non-2xx or thrown HTTP failure degrades, never propagates | `HttpFailure_DegradesQuietly` |
| AC14 | Injected instructions do **not** accumulate across turns | `Memories_DoNotAccumulateAcrossTurns` |
| AC15 | The package targets net8.0 **and** net10.0, and carries licence + notice | `dotnet pack` inspection (manual, recorded in the MR) |

---

# Phase B — Agent42 integration (this repo)

## Bootstrapping

Phase B's `PackageReference` to `Hindsight.AgentFramework.Net` **only resolves once Phase A has
published**. Consequences for the executor:

- Do **not** start Phase B's MR until `v0.1.0` is on nuget.org and restores in a scratch project.
- For local iteration before that, a temporary local feed (`dotnet nuget add source` pointing at
  Phase A's `bin/Release`) is fine — but **the MR must reference the published version**, or CI
  cannot restore.
- Pin the version in `Directory.Packages.props`, resolved from the feed at add time (hard rule 4).

## B — Affected areas

Verified against the tree at `f58ecf8`.

**New — `src/Agent42.Agents/Memory/`** (the **19th** Agents *feature* folder — `Skills/` is a
content folder, not a feature one; flat `Agent42.Agents` namespace per ADR 0039):
- `HindsightIntegration.cs` — a one-line singleton `record HindsightIntegration(IHindsightClient? Client)`
- `AgentGrain.Memory.cs` — the **12th** `AgentGrain` partial

There is deliberately **no `IMemoryService` port and no Backend adapter folder**. The package's
`IHindsightClient` *is* the seam, and adding an Agent42 interface over it would be a second
abstraction for the same thing. This is the main structural change from the pre-packaging plan.

**Modified — source:**
- `Directory.Packages.props` — pin `Hindsight.AgentFramework.Net`
- `src/Agent42.Agents/Agent42.Agents.csproj` — the `PackageReference`
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — ctor param, field, assignment
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — attach the provider
- `src/Agent42.Backend/Program.cs` — named `HttpClient` + `HindsightIntegration` registration + config
- `src/Agent42.Backend/appsettings.json` — a `Hindsight:` section
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` — presence check, probe, `ConfigHealthProbeSet`
- `tools/Agent42.DevHost/Program.cs` — `HindsightIntegration(null)`

**Modified — tests** (all are *edits*, not just verification targets):
- `tests/Agent42.Tests/TestSiloConfigurator.cs`, `NoModelSiloConfigurator.cs`,
  `CapSizedWebPageFetcher.cs` — the three silo configurators (B3 explains the registration shape)
- `tests/Agent42.Tests/SharedClusterFixture.cs` — `ResetSiloState()` must reset the recorder
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — **both** `ExpectedFolders()` arrays (exact
  `Assert.Equal` sets) **and** `AgentGrainParts()` (11 → 12)
- `tests/Agent42.Tests/ConfigHealthTests.cs`, `ConfigHealthProbeTests.cs`, `ConfigHealthApiTests.cs`
  — `ConfigHealthProbeSet` is a **positional record**, so every construction site breaks

**Modified — infra & docs:**
- `.github/workflows/deploy.yml`, `infra/main.bicep`, `infra/modules/container-app.bicep`
- `.agentic/decisions/0056-*.md` (new), `.agentic/decisions/0039-*.md` (stale counts),
  `.agentic/gotchas.md`, `CLAUDE.md`

## B — Context & constraints

### ⚠ This feature is an AMENDMENT to ADR 0008, not an application of it

**Read this before writing the ADR.** It is tempting — and wrong — to present memory as "the ADR 0008
always-on-provider precedent". ADR 0008 lists **"Always-on but *ungated*"** under *Alternatives
considered* and **rejects it**, saying the `DocumentNames.Count > 0` gate "is what makes the reversal
safe". Its per-conversation gate is the load-bearing property.

This feature **has no per-conversation analogue** — memory spanning conversations is exactly what
decision 3 asked for — so it adopts the ungated shape ADR 0008 rejected. The only gates are
operator-wide (is a client configured) and per-turn (`_ephemeral`).

**ADR 0056 must explicitly record that it amends ADR 0008's gating discipline.** 0008's concern was a
per-turn *embedding* round-trip; here the per-turn cost is **two third-party HTTP calls carrying
conversation content** — strictly worse on egress, no better on blast radius. Do not write
"following ADR 0008".

### Findings the executor cannot cheaply re-derive

**(A) The .NET context-provider contract, measured by reflection against `Microsoft.Agents.AI`
1.19.0.** `InvokingContext`/`InvokedContext` are **nested types of `AIContextProvider`** with no XML
docs, so this was reflected, not read. *(Phase A depends on this too.)*

| Python (`provider.py`) | .NET 1.19.0 |
|---|---|
| `before_run` + `context.extend_instructions(...)` | `protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext, CancellationToken)` → `new AIContext { Instructions = block }` |
| `after_run` + `context.get_messages(...)` | `protected override ValueTask StoreAIContextAsync(InvokedContext, CancellationToken)`, reading `ctx.RequestMessages` / `ctx.ResponseMessages` |
| `source_id` / `exclude_sources=` | **no equivalent** — see (B) |
| — | `InvokedContext.InvokeException` — **new**, no Python counterpart |

`AIContext` has exactly three settable properties: `Instructions` (string), `Messages`, `Tools`.
`InvokedContext` exposes `Agent`, `Session`, `RequestMessages`, `ResponseMessages`, `InvokeException`.

**(B) No feedback loop, and injected instructions do not accumulate — both measured.** Two spikes ran
a real `ChatClientAgent` over a fake `IChatClient` with a provider injecting `## Memories\n- …`:

- *Spike 1 (one turn):* the injected text was **appended to `ChatOptions.Instructions`**, never
  turned into a message. `InvokedContext.RequestMessages` contained **only** `[user] a question`.
  `InvokeException` was `null`.
- *Spike 2 (three turns through one held `ChatOptions`):* the model saw `## Memories` **exactly once
  every turn** (`instructions length=60`, unchanged), and the held instance was **never mutated**.
  The framework composes a **per-run copy**.

**Consequences:** port `exclude_sources` as **nothing**; recall injection **never enters the
session**, so it costs nothing against ADR 0004's 2 MB cap; and there is **no unbounded system-prompt
growth** across a warm activation, even though `BuildAgent` holds one `ChatOptions` for the
activation's life.

**(C) THE ORDERING TRAP — the ephemeral gate must be a delegate, not a captured value.**
`BuildAgent` runs on **activation** (`AgentGrain.cs:237`) and five rebuild paths
(`BindAgentAsync`:297, `SwitchModelAsync`:586, `RebindModelAsync`:904, `RegisterDocumentAsync`,
`SetEnabledToolsAsync`) — but **never per turn**. `_ephemeral` is **turn-scoped**, set at the top of
`ChatAsync` and cleared in its `finally` (`AgentGrain.cs:334-350`). A provider capturing an ephemeral
*value* at construction would be wrong on every later turn of a warm activation.

The repo's answer is the `BuildDocumentSearchProvider` shape — a method group closing over `this`,
read at invoke time (`AgentGrain.Documents.cs:19-29`). **This is exactly what `HindsightOptions.IsEnabled`
exists for:** Phase B passes `IsEnabled = () => !_ephemeral`. This is the single most important
detail in the plan.

**(D) Retain is synchronous by default and `AgentGrain` is non-reentrant.** Hindsight's
`retain_memories` performs LLM fact extraction inline unless `async: true` is sent.
`StoreAIContextAsync` is awaited inside the agent run, so a synchronous retain stalls the turn by
seconds. Handled in the package (A3), but Phase B must not undo it.

**(E) The `[sys:…]` echo already surfaces injected instructions — no fake-client change needed.**
`FakeChatClient.InstructionsEcho` (`tests/Agent42.Tests/FakeChatClient.cs:139-151`) emits
`[sys:{instructions}]` from `options.Instructions`, **untruncated**. Per (B) the injected block lands
there. Do **not** reach for the `[ctx:…]` echo — that surfaces injected *messages*, which is how
`TextSearchProvider` works and is **not** how this provider works.

**(F) The doc-path CI gate will reject foreign-repo paths.**
`.agentic/scripts/check-doc-paths.mjs` scans `.agentic/decisions/*.md` and `.agentic/gotchas.md` for
backticked path-shaped tokens and **exits 1** on any that do not exist in this repo (a required PR
check). Its `SKIP` regex exempts `https?:` URLs. So every reference to the upstream Python package
**or to the new package's own repo** must be a **full URL** or left **un-backticked**. Verify with
`node .agentic/scripts/check-doc-paths.mjs`; there is **no** `DocPath` xUnit test.

**(G) Governing ADRs and gotchas:**

- `.agentic/decisions/0008-*.md` — see the amendment warning above.
- gotcha *"`AgentSkillsProvider` is a TOOL … and the RAG provider must NOT be dropped"*
  (gotchas.md:513) — `HindsightMemoryProvider` is **not** a tool, so like `TextSearchProvider` it
  stays attached **regardless of `SupportsTools`**. This also means **ADR 0021 needs no
  supersession** — its scope is tools, and it already carves out the non-tool provider.
- gotcha *"A named HttpClient silently inherits AddServiceDefaults' retry pipeline"* (gotchas.md:252)
  — **`RemoveAllResilienceHandlers()` is mandatory**. The grain is non-reentrant; 4× a stalled memory
  call is 4× a stalled turn.
- gotchas.md:328 + :348 — the turn-scoped-field traps behind finding (C).
- **gotchas.md:2106** — *"assert on state scoped to your own user or grain, never on a cluster-wide
  total"*. Shapes every negative assertion in the B ACs.
- **gotchas.md:447** — *"Any new client whose PATH carries an identifier needs the same treatment"*
  (the ADR 0053 span-suppression rule). This feature is a deliberate **exception**; see Risks.
- `.agentic/decisions/0034-ephemeral-chat.md`, `0013-*.md`, `0053-*.md`, `0012-*.md`.

**(H) Exemplars to open before writing code:**
- provider attachment: the `AIContextProviders` switch in `AgentGrain.BuildAgent.cs:252-258`
- the conditional-on-key DI branch in `src/Agent42.Backend/Program.cs` (opens at :275)
- mutable per-silo recorder: `RecordingPushNotificationSender` in `TestSiloConfigurator.cs:89-90`
  — **not** `FakeGeocoder`, which is stateless

### Risks this plan accepts, and what must be recorded

**The headline consequence, bigger than any existing feature's:** with a key configured, **every
conversational turn** — user message *and* assistant reply — is POSTed to a third party, and every
turn sends the user's message as a recall query. Unlike ADR 0013 (only when the user *picks* a
third-party model) and ADR 0027 (only when the model *calls* fetch), this is **automatic, always-on,
and invisible in the UI**. It carries whatever the conversation carries: document excerpts, the
user's street address (ADR 0012), calendar content (ADR 0053).

Two precision points the ADR must get right rather than overclaim:
- **Image-generation turns are excluded.** `RecordImageTurnAsync`
  (`src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs:146-159`) builds a **separate**
  `ChatClientAgent` with **no `AIContextProviders`**.
- **The reminder and agent-run paths are included** — `_ephemeral` is reset on both
  (`AgentGrain.cs:361-365, 830-831`), which is correct and deliberate.

**Second, and worse than a normal injection channel:** recalled memories are untrusted third-party
text injected into the **system instructions**. Because retain *persists* the transcript, an
injection landing once — from an ADR 0027 followed link (allowlist-**exempt**), an ADR 0024 MCP tool
result, or an ADR 0049 uploaded SKILL.md — becomes a **durable memory re-injected into every future
conversation of that user**, including `ReceiveReminder` and `/api/agent-run` runs where no user is
present (ADR 0022). Cross-conversation, persistent, with **no delete path** (out of scope). A
materially different risk class from ADR 0011/0027/0049; the ADR must say so plainly.

**Not suppressing the outbound span (considered, rejected).** ADR 0053 added
`CalendarSpanSuppressingHandler` because the calendar id in the path is a real email address. Here
the path carries the **Easy Auth principal id** — opaque, not an email, already exported deliberately
as a `user.id` span attribute (ADR 0009/0010, `src/Agent42.Agents/Telemetry/UserTelemetryContext.cs`).
Query text rides in the request *body*, which OTel does not capture. Because gotchas.md:447 states
the rule as absolute, **record this exception in that gotcha entry as well as in ADR 0056**.

**New: this repo now has an external consumer.** CLAUDE.md's "Personal/experimental project; no
external consumers" is no longer wholly true — a published package creates a support surface and a
compatibility obligation. Update that line and say what is owned.

## B — Steps

### B1 — Take the dependency

Add the `PackageReference` to `Agent42.Agents.csproj`, pin in `Directory.Packages.props` (resolved
from the feed, hard rule 4). Read config in `Program.cs`: `Hindsight:ApiKey`, `Hindsight:BaseAddress`,
`Hindsight:Mission`, defaulting to `""` / the package default. Add the `Hindsight:` section to
`appsettings.json` with a `"//"` comment noting the key is a **secret supplied by env var**, never
committed (the `Geocoding:` shape at :26-35).

**Verify:** `dotnet build Agent42.slnx`

### B2 — Wire into `AgentGrain`

- `AgentGrain.cs`: add `HindsightIntegration memory` to the ctor (after `ICalendarService calendar`,
  before `IPushNotificationSender pushSender`), the field, and the assignment.
- **New** `src/Agent42.Agents/Memory/AgentGrain.Memory.cs` — the 12th partial — with
  `BuildMemoryProvider()`, constructing `HindsightMemoryProvider` with
  `BankId = UserIdFromGrainKey(this.GetPrimaryKeyString())` (`AgentGrain.cs:254-267`) and
  **`IsEnabled = () => !_ephemeral`** (finding (C) + decision 4).
- `AgentGrain.BuildAgent.cs`: attach when `_memory.Client is not null`. The existing two-tuple
  `switch` at `:252-258` becomes a three-way decision — prefer a `List<AIContextProvider>` with three
  conditional `Add`s over a 3-dimensional tuple switch, for readability and because
  `cognitive_complexity_max` is **15**, enforced at build time. **Do not gate on
  `capabilities.SupportsTools`** (gotchas.md:513).

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentGrainTests`

### B3 — DI registration

`HindsightIntegration` is a **mandatory grain dependency**, so every silo must register it.

**The test double must be a per-grain *recorder*** — B's ACs assert whether recall/retain happened
and *for whose bank*. It records `(bankId, query)` tuples so assertions can be scoped per user, and
exposes a `Reset()`. A mutable per-silo singleton a test reads back must be resolvable **by its
concrete type** — the `RecordingPushNotificationSender` shape, registered in each silo configurator
directly, **not** in `AddWebToolFakes()` (which is for stateless fakes):

```csharp
services.AddSingleton<RecordingHindsightClient>();
services.AddSingleton(sp => new HindsightIntegration(sp.GetRequiredService<RecordingHindsightClient>()));
```

| File | What |
|---|---|
| `src/Agent42.Backend/Program.cs` | named `HttpClient` + `RemoveAllResilienceHandlers()`; `HindsightIntegration(new HindsightClient(...))` when a key is present, `HindsightIntegration(null)` otherwise (the geocoder branch opening at :275) |
| `tools/Agent42.DevHost/Program.cs` | `HindsightIntegration(null)` (beside :121/:128) |
| `tests/Agent42.Tests/TestSiloConfigurator.cs` | the dual registration above (shape at :82-83 / :89-90) |
| `tests/Agent42.Tests/NoModelSiloConfigurator.cs` | same (shape at :59 / :65-66) |
| `tests/Agent42.Tests/CapSizedWebPageFetcher.cs` | same (shape at :75 / :84-87) |
| `tests/Agent42.Tests/SharedClusterFixture.cs` | add the recorder to `ResetSiloState()` (:50-59) |

`ResetSiloState` is **not optional**: the cluster is shared per test class (ADR 0055), so without a
reset the recorder accumulates across tests and every count assertion silently rots. ADR 0055's own
safety check 2 is *"no assertion depends on a mutable per-silo singleton the reset does not clear."*

**Verify:** `dotnet test Agent42.slnx`

### B4 — Config health

A `PresenceCheck` in `GroupOptional` for `Hindsight:ApiKey` (`missing:` "not set — long-term memory
is off; the agent recalls and retains nothing", `missingStatus: Warning`), a `NotProbed` entry, and a
probe in `ConfigHealthProbeSet` hitting `GET /health` (evidenced in A3) with **`billable: false`** —
it is unauthenticated and free, unlike the geocoding/context.dev probes.

Two rules easy to miss:
- **Build the probe only when the key is present.** B3 registers the named client conditionally, so
  `CreateClient("hindsight")` with no key returns a client that **inherits the resilience pipeline**
  (gotchas.md:252). `ConfigHealth.cs:250-270` documents exactly this rule for geocoding — follow it.
- The probe **must return a curated constant**, never the exception, body, or URL (`:145-199`).

`ConfigHealthProbeSet` (`ConfigHealth.cs:246-249`) is a **positional record**, so a fourth member
breaks `ConfigHealthTests.cs:160,181,199` and **`ConfigHealthApiTests.cs:56`**.
`ConfigHealthProbeTests.cs:94` needs a fourth assertion.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~ConfigHealth`

### B5 — Infra

Thread `HINDSIGHT_API_KEY` through as an **optional** secret — the `googleMapsApiKey` shape, *not*
`searxngSecret`'s (no `@minLength`, empty default, conditional emission):
- `.github/workflows/deploy.yml:94-100` — `hindsightApiKey="${{ secrets.HINDSIGHT_API_KEY }}"`
- `infra/main.bicep:40-53` — `@secure() param hindsightApiKey string = ''` + a `@description` saying
  why it is optional and off the availability path; pass to the module (:195-199)
- `infra/modules/container-app.bicep` — param (:78-95), conditional secret (:151-205), conditional
  env var `Hindsight__ApiKey` (:239-264)

**Verify:** `az bicep build --file infra/main.bicep`. The GitHub secret is a **manual prerequisite
for the user**.

### B6 — Docs

- **`/decide`** → `.agentic/decisions/0056-hindsight-long-term-memory.md`. Must record: that it
  **amends ADR 0008's gating discipline** (the most important line); the extraction to a public
  package and why (decisions 5–8, and the licence obligation); the always-on egress escalation and
  why it is off-by-default; the image-turn exclusion; the durable cross-conversation injection risk;
  the incognito exclusion; one-bank-per-user; and the rejected span suppression with its ADR 0053
  asymmetry.
- **`/gotcha`** → two entries: (i) **the provider is built at activation but the ephemeral gate is
  per-turn, so it must be a delegate (`IsEnabled`), never a captured value** — finding (C);
  (ii) **injected `AIContext.Instructions` neither come back in `InvokedContext.RequestMessages` nor
  accumulate across turns** — finding (B), with both spikes' measured values. Also **amend
  gotchas.md:447** with the span-suppression exception.
- **Doc counts.** `Memory/` makes Agents' **19th** directory but its **18th→19th feature folder**.
  **CLAUDE.md's existing "18 feature folders" is CORRECT as of `f58ecf8`** (`Skills/` is content —
  `ProjectLayoutTests.cs:85-90`, gotchas.md:1904) — update it to 19, do **not** "fix" it as drift.
  The genuinely stale doc is `.agentic/decisions/0039-*.md`: *"Seventeen top-level feature folders"*
  (:31) and *"ten partials"* (:86). Update the partial count (11 → 12) in CLAUDE.md.
- **CLAUDE.md's "no external consumers" line** — now false; see Risks.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~ProjectLayoutTests` **and**
`node .agentic/scripts/check-doc-paths.mjs`.

## B — Acceptance criteria → test mapping

**Scoping rule for every negative assertion.** Per gotchas.md:2106, a shared-cluster class must
"assert on state scoped to your own user or grain, never on a cluster-wide total" — a reminder fired
by another test can drain `ChatCoreAsync` and retain. So B2/B4 below assert **"the recorder logged no
call for bank `X`"**, where `X` is that test's own userId from a grain key unique to the test
(e.g. `"memory-off-user/conv"` → bank `"memory-off-user"`). Never assert on a total count.

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| BC1 | With a client configured, memories reach the model on a real grain turn | `AgentChat_InjectsRecalledMemories` | `AgentGrainTests.cs` |
| BC2 | **An incognito turn records neither a recall nor a retain for its own bank** | `EphemeralTurn_NeitherRecallsNorRetains` | `AgentGrainTests.cs` |
| BC3 | A normal turn **after** an incognito one on the same activation **does** retain | `MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` | `AgentGrainTests.cs` |
| BC4 | With `Client is null`, the provider is never attached — **no recall recorded for that bank** | `UnconfiguredMemory_MakesNoCalls` | `AgentGrainTests.cs` |
| BC5 | The bank id is the **userId**, not the conversation id | `BankId_IsTheUserId` | `AgentGrainTests.cs` |
| BC6 | A memory-service failure does not break the turn | `MemoryFailure_DoesNotBreakTheTurn` | `AgentGrainTests.cs` |
| BC7 | Config health reports the key's presence/absence without leaking it | `ConfigHealth_ReportsHindsight` | `ConfigHealthTests.cs` |
| BC8 | The folder/partial layout assertions still hold | `ProjectLayoutTests` (updated arrays) | `ProjectLayoutTests.cs` |

**BC2 + BC3 together** are the regression guard for finding (C) — neither alone catches a
captured-value bug. **BC4 asserts on the recorder, not the `[sys:…]` echo**: "not attached" and
"attached but recall returned nothing" are indistinguishable in the echo, trivially distinguishable
in the recorder.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (`f58ecf8`) — do not recall from memory.
**These govern Phase B (this repo).** Phase A's new repo starts with no gate file; mirror these
thresholds there rather than inventing new ones.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` **S3776** at build time — **blocking** since ADR 0051 |
| `vulnerable_packages` | **0** | CI package-freshness job — hard block |
| `outdated_packages` | report | soft |
| `add_policy` | latest-stable | resolved at add time, never from memory (hard rule 4) |

**Coverage exposure to watch.** Extraction *helps* here: the provider and client logic now live in
Phase A's repo and are covered there. Phase B adds mostly DI/config wiring to
`src/Agent42.Backend/Program.cs` — uncovered by default, and the likely squeeze against the 80%
floor. Budget for it.

## Out of scope

- **Any user-facing UI in Agent42.** No Preferences tab, no per-user enable/disable, no
  `ToolCatalog` key. The operator's off switch is *not setting the key*. Given the egress and the
  durable-injection risk, a per-user opt-out is a plausible and arguably desirable follow-up — the
  user explicitly scoped it out.
- **Per-user Hindsight API keys** (the ADR 0018/0024 DP-encrypted overlay shape).
- Per-conversation or per-agent banks.
- Hindsight's other surfaces: reflect, entities, mental models, knowledge base, documents,
  directives, observations. Only recall / retain / create-bank / health are implemented.
- The Python package's `configure()` / `get_config()` / `reset_config()` process-global machinery —
  superseded by `HindsightOptions` + DI. Its `HindsightError` is dropped: the client degrades rather
  than throwing, so nothing would catch it.
- Deleting or migrating memories, and any UI for inspecting a bank. **This is what makes the
  durable-injection risk unmitigable in this iteration** — say so in the ADR.
- Any change to how the session is persisted (ADR 0004) — per finding (B) this does not touch it.
- **Creating the `HINDSIGHT_API_KEY` and `NUGET_API_KEY` secrets** — manual prerequisites.
- A DevHost/browser leg. DevHost registers `HindsightIntegration(null)`, so memory is off offline.
- A real end-to-end run against a live Hindsight server. Both repos' tests stub the HTTP layer. An
  honest unverified residual, in the ADR 0053 tradition — record it.

## Review

A `critic` subagent reviewed the **pre-packaging** version of this plan against the repo's knowledge
files. Verdict then: **do not approve as written** — one ADR misuse, three test-layer defects, one
doc step that would have introduced drift. All findings were verified in the main session before
being applied or rejected, and all fixes survive into this revision.

**Accepted and fixed:**

| # | Finding | Fix |
|---|---|---|
| H1 | Plan cited ADR 0008 as precedent, but 0008 explicitly **rejects** "always-on but ungated"; its per-conversation gate is the load-bearing property | Added the "⚠ AMENDMENT to ADR 0008" section; B6 requires ADR 0056 to record it |
| H2 | Negative ACs were cluster-wide assertions — forbidden by gotchas.md:2106; `SharedClusterFixture` reset was missing | Added the AC scoping rule; added `SharedClusterFixture.cs` to Affected areas and B3 |
| H3 | The "unconfigured" AC had nowhere to live and was unfalsifiable (the echo cannot distinguish "not attached" from "recalled nothing") | BC4 now asserts on the **recorder**; the `HindsightIntegration(null)` shape makes it trivial |
| H4 | `ProjectLayoutTests` is an **edit** site (both `ExpectedFolders()` arrays are exact; `AgentGrainParts()` lists 11) | Added to Affected areas; BC8 added |
| M1 | The "fix the CLAUDE.md drift" instruction was itself wrong — `Skills/` is a content folder, so 18 was correct | Corrected throughout; B6 says update 18→19, not "fix drift", and points at ADR 0039 as the stale doc |
| M2 | `check-doc-paths.mjs` would fail CI on backticked foreign-repo paths; the named `~DocPath` filter matches no test | Finding (F); all upstream references de-backticked; verify command corrected |
| M3 | `ConfigHealthProbeSet` is a positional record — `ConfigHealthApiTests.cs` and `ConfigHealthProbeTests.cs` break; the build-probe-only-when-key-present rule was missing | Both added to Affected areas; B4 states both rules |
| M5 | Risk statement overstated coverage (image turns are excluded) and understated the injection risk (retain makes it durable and cross-conversation) | Both corrected in Risks |
| L1 | `IsConfigured` justification cited a false analogy to RAG's gate | Obsolete after extraction — replaced by `HindsightIntegration(Client?)` |
| L2 | The limits type was justified by a session cap that finding (B) says does not apply | Now a package concern (A2); justified by prompt cost + outbound body size |
| L3 | Span-suppression exception recorded only in the ADR, leaving gotchas.md:447 reading as absolute | B6 now also amends that gotcha |
| L4 | Coverage-gate squeeze unaccounted for | Added to the gate checklist |
| L5 | Line-ref slips | Corrected (`:252-258`, `:275`) |

**Rejected, with reason:**

| # | Finding | Why rejected |
|---|---|---|
| M3 (part) | "The `/health` probe target is asserted from nowhere" | **Factually wrong** — the critic could not see the upstream OpenAPI spec, which declares `/health`, `/health/ready` and `/health/live` as unauthenticated. Now recorded in A3 so the executor has the evidence. |
| M4 | "Nothing tests multi-turn instruction accumulation; the held `ChatOptions` may grow unboundedly" | **Good catch, but the risk does not exist.** Settled by a second spike: across three turns the model saw `## Memories` exactly once each time and the held instance was never mutated. Kept as **AC14**, reframed from open risk to regression pin. |

**Not re-reviewed.** The two-repo restructure landed *after* the critic ran. The extraction resolves
some findings and moves others, but **Phase A has had no adversarial pass**. Re-running `/plan`'s
critic step against this revision — or dispatching a `critic` at the start of execution — is
advisable before Phase A's first MR.

---

# Execution record — where the implementation diverged from this plan

Executed 2026-08-28. Both phases shipped: `Hindsight.AgentFramework.Net` **0.1.0** is on nuget.org
(https://github.com/Expecho/Hindsight.AgentFramework.Net), and Agent42 consumes it on
`feature/hindsight-long-term-memory`.

## Corrections to the plan's stated findings

**1. Finding (A) was wrong about `InvokingContext`.** The plan's contract table put the run's messages
on `InvokingContext.RequestMessages`. That member does not exist. Reflected against
`Microsoft.Agents.AI` 1.19.0, `InvokingContext` exposes exactly `Agent`, `Session` and **`AIContext`**;
the run's incoming messages arrive on **`context.AIContext.Messages`**. `InvokedContext` *does* have
`RequestMessages`/`ResponseMessages`/`InvokeException` as the plan said — the two halves of the API are
simply not symmetric, which is the trap. Caught at first compile; the provider reads `AIContext.Messages`.

Finding (B) was confirmed exactly, by the same spike (a real `ChatClientAgent` over a fake `IChatClient`):
injected text is appended to `ChatOptions.Instructions`, never becomes a message, and does not accumulate —
`instructions length=71` on turn 1 *and* turn 2, held instance never mutated.

**2. There are SEVEN silo configurators, not three.** B3's table named `TestSiloConfigurator`,
`NoModelSiloConfigurator` and `CapSizedWebPageFetcher`. `AgentGrain` is also activated by
`CountingStoreSiloConfigurator` (`AttachmentRehydrateTests.cs`), `CoordinatesOnlyGeocoderSiloConfigurator`
(`FakeGeocoder.cs`), `ActivityPropagationSiloConfigurator` (delegates to `TestSiloConfigurator`, so covered
for free), and the new `UnconfiguredMemorySiloConfigurator`. Missing two of them failed **activation** on
those silos — the correct failure mode for a mandatory dependency, and how they were found.

**3. `CapSizedWebPageFetcher`'s silo gets `HindsightIntegration(null)`, not the recorder.** B3 said the
dual recorder registration for all three. That silo deliberately uses minimal fakes
(`NullPushNotificationSender`), and a recorder there would attach a real context provider and inject a
`## Memories` block into the very instructions its session-budget test measures.

**4. The registration lives in `HindsightRegistration`, not inline in `Program.cs`.** B3's table put the
conditional branch in `Program.cs` beside the geocoder. Doing that took the composition root from 15 to
**16** cognitive complexity and **failed the build** — `cognitive_complexity_max` blocks (S3776 at
`error`). Extracted into `src/Agent42.Backend/Memory/HindsightRegistration.cs`, which is what ADR 0050/0051
established and what `DocumentIndexRegistration` already does. Side effect: `Memory/` becomes Backend's
**20th** feature folder, so `ProjectLayoutTests`' Backend array and ADR 0040's count needed updating too —
neither was in the plan's affected areas.

**5. `HindsightIntegration` carries `Mission` as well as `Client`.** The plan specified a one-line
`record HindsightIntegration(IHindsightClient? Client)` while B1 read `Hindsight:Mission` from config.
Without a second member that value is read and dropped on the floor.

**6. The BC2 + BC3 "neither alone" claim was false.** The plan said the two are a regression pair where
"neither alone catches a captured-value bug". Mutation-tested rather than assumed:

| Mutation | BC2 | BC3 |
|---|---|---|
| value captured at build time (**the realistic bug** — activation has `_ephemeral` false) | FAIL | FAIL |
| gate stuck OFF (the capture-of-true direction) | **pass** | FAIL |

So **BC3 is the load-bearing one** — the only test that fails in both directions. BC2 asserts that nothing
was recorded, which a provider that never records anything satisfies trivially. The claim had been copied
into a code comment, a gotcha and CLAUDE.md before being checked; all three now state the measured result
and warn that deleting BC3 as "redundant with BC2" is exactly backwards.

## Things the plan predicted correctly and are worth crediting

- **The coverage squeeze happened.** The plan warned Phase B would be mostly uncovered DI/config wiring.
  First gate run: **61.3%** against the 80% floor, with `HindsightRegistration` at 0/21. Fixed with
  `HindsightRegistrationTests` (both branches, blank-key handling, mission threading, and the
  named-client-only-when-keyed asymmetry) plus Hindsight arms on `ConfigHealthProbeTests` → **98.8%**.
- **M3's positional-record warning was exact.** Widening `ConfigHealthProbeSet` broke precisely the four
  sites it named.
- **Finding (F) was right**: every foreign-repo reference is a full URL, and `check-doc-paths.mjs` passes.

## Deviations of process, not design

- **Publishing uses nuget.org trusted publishing (OIDC), not a stored `NUGET_API_KEY`.** The user chose
  this when the secret was raised. `release.yml` uses `NuGet/login@v1` with `id-token: write`; the only
  repo secret is `NUGET_USER`, a profile name rather than a credential. A6's `NUGET_API_KEY` prerequisite
  is therefore obsolete — the manual prerequisite is a trusted-publishing **policy** on nuget.org naming
  owner, repo and the workflow file `release.yml`.
- **Phase A's genesis commit is on `main`.** The plan asked for the same branch discipline this repo uses.
  A brand-new repository has nothing to branch *from*, so the initial commit landed on `main` and CI ran on
  the push before the tag was cut. Branch discipline applies from here on.
- **`Microsoft.SourceLink.GitHub` was not added as a package.** The .NET 8+ SDK references it implicitly;
  the packaging properties alone produce a source-linked `.snupkg`, verified by inspecting the pack output.
- **Test project multi-targets `net8.0;net10.0`.** The plan only required the library to. A package
  claiming net8.0 support should have run its tests there; both runtimes are present locally and on CI.

## Incidental fix carried in this MR

CLAUDE.md claimed `cognitive_complexity_max` "does not block yet … S3776 is at `warning`". ADR 0051 made
it blocking and this branch had it **fail a build** as an `error`. Corrected in the same MR under hard
rule 2 (trust code, fix the doc), with the `Program.cs`-tips-over case written down since it will recur.

## Honest unverified residual

**Neither repo's tests ever reach a real Hindsight server.** Both stub the HTTP layer; the wire contract
is implemented against the upstream OpenAPI document and pinned by URL/body assertions, not a round trip.
DevHost registers `HindsightIntegration(null)`, so there is no offline browser leg either. The first real
call this code makes will be in production, with a key set. Recorded in ADR 0056 rather than papered over.

## Findings from the critic pass, and what they cost

A `critic` subagent reviewed the finished diff (the pass the plan's own *Review* section said Phase A and
the two-repo restructure had never had). It confirmed the hard call — that ADR 0056 amends ADR 0008 rather
than citing it — and cleared the gotchas, the exemplar shapes and BC1–BC8. It also found nine things worth
fixing, all fixed in `d5efbc3`:

- **Two counts were bumped without their inventories.** ADR 0039's folder list and partial list, and ADR
  0040's folder list, still enumerated the old sets after this branch changed the numbers above them —
  the exact outcome those ADRs' own *"a folder list is a factual inventory … corrected in place"* rule
  exists to prevent. Both were *already* missing `Calendar/` from ADR 0053, so this branch made a
  pre-existing contradiction worse rather than creating one.
- **A retraction that missed its main target.** Commit `aefe8ef` said it had corrected the false
  "neither test alone" claim in "the code comment, the gotcha and CLAUDE.md" — and had not touched the
  code comment, which sits on the very method under discussion. The commit message was wrong about its
  own diff.
- **A half-thread-safe recorder.** `RecordingHindsightClient` locked its writers and left readers walking
  live `List<T>`s, in a shared-cluster suite whose own remarks describe the racing case. Worse than no
  lock, because it reads as safe. Replaced with the exemplar's `ConcurrentQueue` + snapshots.
- **A silo configurator that lied about itself.** `UnconfiguredMemorySiloConfigurator` claimed to be
  "otherwise identical to `TestSiloConfigurator`" while omitting the image generator, the capability
  predicate and the embedding generator — so BC4 ran against a differently-capable grain than every other
  memory AC. Now delegates, per `ActivityPropagationSiloConfigurator`.
- **A real startup bug.** A blank `Hindsight:BaseAddress` threw `UriFormatException` from
  `AddHindsightMemory`, because an unset optional value arrives as `""` and not null — so the host
  designed to boot with every optional value blank would have failed to boot over an empty optional knob.
  Two lines below a correctly blank-guarded key. Regression test added.
- Plus: no `amended_by` back-pointer on ADR 0008; `Hindsight:Mission` plumbed but unreachable in prod;
  a stale comment on the rewritten `AIContextProviders` assignment; a missing blank line in `gotchas.md`.

**The pattern worth carrying forward:** every one of the documentation findings was a claim written
confidently and not checked — a count without its list, a commit message about its own diff, a "shape"
citation that did not match the exemplar. The two code findings were the same failure in a different
medium. Mutation-testing the three behavioural claims (the incognito pair, the no-tools guard) caught two
more before the critic ran, and is the cheapest tool here for the same class of mistake.
