---
id: 0039
date: 2026-08-18
status: accepted
tags: [refactor, structure, agents, orleans, serialization, tooling]
supersedes: []
amends: []
extends: []
---

# Feature folders and folder-matching namespaces in `Agent42.Agents`, with `AgentGrain` split into partials

## Context
`src/Agent42.Agents` had absorbed roughly twenty ADRs' worth of features onto a completely flat
layout: 73 `.cs` files in one directory, all in `namespace Agent42.Agents;`, plus a 2070-line
`AgentGrain.cs` that inlined every feature's tool logic. Finding the web-search shims, the location
tool or the image-generation turn meant knowing a line range rather than opening a file, and the next
feature would have made that worse rather than better.

Nothing here is a behaviour change. It is a move, a namespace change, and a `partial` split, with the
documentation debt paid in the same MR (hard rule 2).

The load-bearing question was not aesthetics but **whether the move is inert for persisted state**,
since this project has live conversations in Cosmos. That question is answered by measurement below,
not by assumption.

## Decision

### 1. Sixteen top-level feature folders, with namespaces that match
`StoredAgents/`, `Attachments/`, `Conversations/`, `Documents/`, `ImageGeneration/`, `Location/`,
`Mcp/`, `Models/`, `Push/`, `Scheduling/`, `Sharing/`, `Speech/`, `Telemetry/`, `Tools/`, `Users/`,
`Web/` — folder-matching namespaces (`Agent42.Agents.Web`), not folders alone. `Skills/` does not
move: its csproj `Content Include="Skills\**\*"` glob must keep resolving next to the built assembly.

The stored-agent folder is `StoredAgents/`, not `Agents/`, which would have produced the absurd
`Agent42.Agents.Agents`.

Cost accepted: 79 files across `Agent42.Backend`, `Agent42.DevHost` and `Agent42.Tests` get `using`
line changes. Four references were qualified rather than `using`-based and were re-qualified by hand
(`OpenAIImageGenerator`'s two `Agent42.Agents.GeneratedImage` mentions, qualified to dodge
`OpenAI.Images.GeneratedImage`; and the `using PushSubscription = ...` alias in `WebPushClientAdapter`
and its test).

### 2. The invariant is enforced by tooling, not by discipline
A new root `.editorconfig` sets `dotnet_style_namespace_match_folder = true` and
`dotnet_diagnostic.IDE0130.severity = error`, **scoped to `[src/Agent42.Agents/**.cs]`**, plus a
`ProjectLayoutTests` source-tree walk as a second, tool-independent guard.

The scoping is not tidiness — a repo-wide `[*.cs]` rule breaks the build. `src/Agent42.ServiceDefaults/Extensions.cs`
declares `namespace Microsoft.Extensions.Hosting;` on purpose (the Aspire shared-project convention),
which is exactly why Backend, AppHost and DevHost call `AddServiceDefaults()` with no
`using Agent42.ServiceDefaults;`. A repo-wide rule flags that file, and auto-fixing it breaks three
projects. The `.editorconfig` records this reason inline so nobody "simplifies" the glob later.

The `.editorconfig` is deliberately free of whitespace/formatting keys: `dotnet format` is a CI gate,
so any style key added there reformats the whole repo on the next run.

**Enforcement was verified in both directions, not asserted.** On the untouched tree
`dotnet format --verify-no-changes` exits 0; with a deliberate wrong-namespace file under
`src/Agent42.Agents/Tmp/` it exits 2 with `error IDE0130`. (The fallback path in the plan — "if
IDE0130 has no fixer, rely on the layout test alone" — was not needed.) Note `dotnet build` will not
catch this: no project sets `EnforceCodeStyleInBuild`.

### 3. `AgentGrain` splits into ten partials that live in their feature folders
`src/Agent42.Agents/Conversations/AgentGrain.cs` (core) plus
`src/Agent42.Agents/Conversations/AgentGrain.FollowUps.cs`,
`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`,
`src/Agent42.Agents/Documents/AgentGrain.Documents.cs`,
`src/Agent42.Agents/Web/AgentGrain.WebTools.cs`,
`src/Agent42.Agents/Location/AgentGrain.LocationTool.cs`,
`src/Agent42.Agents/Scheduling/AgentGrain.Scheduling.cs`,
`src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs`,
`src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs` and
`src/Agent42.Agents/Push/AgentGrain.Push.cs`.

A partial class is one namespace, so every part declares `namespace Agent42.Agents.Conversations;`
regardless of the folder it sits in. That is the second, deliberate `.editorconfig` exception: the
`[src/Agent42.Agents/**/AgentGrain.*.cs]` glob sets IDE0130 to `none`.

Rules that shaped the split:
- **All instance fields stay in the core part** — every dependency and every turn-scoped flag. Three
  static/const members move with their feature by design (`ExcludeInjectedContextMessages`,
  `AssistantImageIdKey`, `WithheldImagePlaceholder`).
- **`ReceiveReminder` stays in the core part, beside `ChatAsync`**, even though it is the reminder
  callback and `Scheduling/` exists. The safety of `_turnLocation` and `_ephemeral` rests on *two*
  clears that must be read together — the `finally` in `ChatAsync` and the nulling on entry to
  `ReceiveReminder` — and both gotchas exist precisely to prevent reading one without the other.
  Only the schedule/cancel tool methods moved.
- **Two residual splits of a documented invariant were unavoidable:** `_ephemeral`'s write-skip in
  `GenerateImageTurnAsync` now lives in `ImageGeneration/`, and its `SchedulePromptAsync` refusal in
  `Scheduling/`, while the flag and its clear stay in core. The gotcha entry names all three files
  explicitly, both the `ImageTurn` and `Scheduling` parts carry a cross-file pointer, and four named
  regression tests cover the behaviour.
- **No logic edits in the split commit.** `SerializeOrNull` stayed in core because its only two call
  sites are in `ChatCoreAsync`.

### 4. Scope stops at this project
`Agent42.Backend` (34 flat files) and `Agent42.Tests` (81 flat files) keep their layout; only their
`using` lines change. Each is its own follow-up MR if wanted.

## Measurements that justify calling this inert

Two guards were authored **before** anything moved, so they are genuine before-pictures.

**Grain identity — the mechanism that actually keys a Cosmos document.** The persisted item id is
`{GrainType}/{key}`, and **no grain in this repo declares `[GrainType]`**; all twelve fall through to
Orleans' naming convention. `GrainIdentityTests` pins all twelve type tokens plus the full
`agent/{userId}/{conversationId}` id, with **hard-coded literal** expectations — computing them from
the type would make the test move with the code it guards. Values are unchanged after the move.

**Serialization — measured by mutation, and the results are mixed.** Golden blobs for all twelve
persisted state roots were generated on the old layout by a throwaway generator and committed
verbatim (a test that writes its own fixture when absent would pass by construction). Three mutations
were then run:

| Mutation | Caught by the golden blobs? |
|---|---|
| Renumber one `[Id(n)]` (`StoredModel.SupportsSpeech` 6 → 7) | **yes** |
| Change an `[Alias]` string (`StoredMcpServer`) | no |
| Move a serialized type + its grain to another namespace | no |

So the blobs guard `[Id(n)]` numbering and member drift, and nothing else — the class doc says exactly
that. The third row is the one that matters here: **the move is inert for serialization, measured
rather than hoped**, and grain identity is the real safety net.

A trap this exposed: the blobs visibly contain strings like
``ReadOnlyListInterfaceCodec`1[[Agent42.Agents.StoredMcpServer]]`` for interface-typed members. That
token looks alias-derived, but the alias mutation proves it is not load-bearing on the read path.
Do not infer alias safety from a green suite.

A fixture detail worth keeping: `StoredModel`'s five capability bits **alternate** rather than all
being `true`. Five adjacent same-typed `[Id]`s are exactly where a renumbering swaps two values
silently, and an all-true fixture could not tell a swap from a correct read — that choice is what
made the first mutation detectable at all.

## Consequences

- **The 54 `[Alias("Agent42.Agents.*")]` strings are now frozen and deliberately out of step with the
  real namespaces.** "Correcting" one orphans persisted state, and — per the table above — neither the
  compiler nor the test suite will stop you. This is booked as a gotcha entry and a comment on
  `AgentGrainState`, not just a line in this ADR, because the trap spans 54 attributes.
- `AgentGrain.StorageProviderName` is consumed by eleven `[PersistentState(...)]` attributes spread
  across seven feature folders, so most feature namespaces now carry `using Agent42.Agents.Conversations;`
  for that one const. Deliberately **not** relocated: one storage provider genuinely is shared, and
  moving the const buys a tidier using-block at the cost of churn.
- Every part file carries only the `using` lines it needs, established by removing each candidate and
  rebuilding rather than by eye.

## Alternatives considered

- **Folders without namespace changes.** Cheapest (no `using` churn anywhere), but the folder/namespace
  relationship then has no enforcement and drifts back within a few features. Rejected: the point was a
  structure tooling can hold, not a one-time tidy.
- **A `Feature/` prefix directory.** Rejected as a redundant level; the project *is* the feature set.
- **Extracting the `AgentGrain` slices into separate collaborator classes.** This is the real
  decoupling, and it remains open — but it moves turn-scoped state across object boundaries, which is a
  behavioural change. A partial split is deliberately the non-behavioural step, and keeps this MR
  reviewable as a pure move.
- **Reorganising Backend and Tests in the same MR.** Rejected as scope; a 153-file diff is already at
  the limit of what is reviewable.

## What this explicitly does NOT settle

**Which serializer the Cosmos grain-storage provider actually uses.** A hand-built `Serializer<T>` is
not the `IGrainStorageSerializer` that `AddCosmosGrainStorage` installs in
`src/Agent42.Backend/Program.cs`. That open question (raised on `AgentGrainState.DeploymentName`,
ADR 0013) needs a test against the configured serializer, which nothing here builds. Do not cite
`SerializationCompatibilityTests` for it.
