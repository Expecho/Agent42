---
id: 0039
date: 2026-08-18
status: accepted
tags: [refactor, structure, agents, orleans, serialization, tooling]
supersedes: []
amends: []
extends: []
---

# Feature folders in `Agent42.Agents` — files move, namespaces deliberately do not

## Context
`src/Agent42.Agents` had absorbed roughly twenty ADRs' worth of features onto a completely flat
layout: 73 `.cs` files in one directory, all in `namespace Agent42.Agents;`, plus a 2070-line
`AgentGrain.cs` that inlined every feature's tool logic. Finding the web-search shims, the location
tool or the image-generation turn meant knowing a line range rather than opening a file, and the next
feature would have made that worse rather than better.

Nothing here is a behaviour change. It is a file move and a `partial` split, with the documentation
debt paid in the same MR (hard rule 2).

The load-bearing question was never aesthetics but **whether the reorganisation is inert for persisted
state**, since this project has live conversations, agents and settings in Cosmos. It turned out that
the obvious version of this refactor — folders *and* matching namespaces — is **not** inert: it
destroys all of it. That discovery, and the reason every green signal missed it, is the substance of
this ADR and is recorded in full rather than quietly corrected.

## Decision

### 1. Twenty-eight top-level feature folders, in a single flat namespace
`StoredAgents/`, `Artifacts/`, `Attachments/`, `ClientTools/`, `Conversations/`, `Copilot/`, `Documents/`, `Email/` (ADR 0096), `Fleet/` (ADR 0097),
`ImageGeneration/`, `Location/`, `Mcp/`, `Memory/`, `Models/`, `PromptCaching/` (ADR 0104), `Push/`, `Research/`, `Scheduling/`, `Search/`,
`SharedState/`, `Sharing/`, `Speech/`, `Telemetry/`, `Tools/`, `UserSkills/`, `Users/`, `Web/`, `Webhooks/` (ADR 0094). `Skills/` does not move: its csproj
`Content Include="Skills\**\*"` glob must keep resolving next to the built assembly — which is also
why `UserSkills/` (ADR 0049) is a separate folder rather than living under it, since a `.cs` file
there would be shipped as content into the deployed `Skills/` directory.

*(Sixteen at the time of this ADR; `UserSkills/` was added by ADR 0049, `Calendar/` by ADR 0053,
`Memory/` by ADR 0056, `Search/` by ADR 0061, `Research/` by ADR 0082, `Artifacts/` by ADR 0084 (missing from this
list until ADR 0085), `Copilot/` by ADR 0085, `ClientTools/` and `SharedState/` by ADR 0089, `Webhooks/` by ADR 0094, `Fleet/` by ADR 0097, `Email/` by ADR 0096 (missing from this list until ADR 0104) and `PromptCaching/` by ADR 0104 — and `Calendar/` was removed again
by ADR 0063, which also added the `Search/` entry ADR 0061 had left out of this list. A folder list is a factual
inventory, not a decision, so it is corrected in place — `ProjectLayoutTests.ExpectedFolders` asserts
it by name so the two cannot drift. Correcting the heading's number without the list below it is the
one outcome that rule exists to prevent, and it has now happened twice.)*

**Every type stays in `namespace Agent42.Agents;`.** Folders organise the files; they do not shape the
namespace. This is the opposite of the usual convention and it is not a style preference — see
"The namespace question" below, which is the whole substance of this ADR.

The stored-agent folder is `StoredAgents/`, not `Agents/`, which would have produced the absurd
`Agent42.Agents.Agents` had namespaces followed folders.

Because the namespace is unchanged, **no consuming file needed a `using` change**: `Agent42.Backend`,
`Agent42.DevHost` and `Agent42.Tests` are untouched apart from the `AgentGrain` split's own effects.

### 2. The namespace question — why folders and namespaces deliberately disagree
`AddCosmosGrainStorage` (`src/Agent42.Backend/Program.cs`) configures no serializer, so grain state is
persisted by Orleans' default `IGrainStorageSerializer`: **`Orleans.Storage.JsonGrainStorageSerializer`**,
Newtonsoft JSON with type discriminators. Every document in the `agent-state` container looks like:

```json
{"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents","SerializedSession":"…"}
```

The **namespace of every persisted type is part of the on-disk format**. Moving `AgentGrainState` into
`Agent42.Agents.Conversations` changes that string, and every document already in Cosmos fails with
`JsonSerializationException: Error resolving type specified in JSON`. It throws on the **root** type, so
the read fails whole — every conversation, stored agent, user profile, provider config, MCP server, push
subscription, share link and usage rollup, all at once.

So the folders ship and the namespaces do not.

Two mechanisms enforce this, because the IDE actively pushes the other way:
- **`.editorconfig` sets `dotnet_style_namespace_match_folder = false` and `IDE0130` to `none`** for
  `src/Agent42.Agents/**.cs`, with the reason inline. Visual Studio and Rider offer "move to namespace
  matching folder" by default; accepting that suggestion is a production outage. This setting is what
  stops it being offered.
- **`ProjectLayoutTests.EveryFileStaysInTheFlatNamespace`** catches it if someone does it by hand.

Note the rule here is scoped to `src/Agent42.Agents/**.cs` rather than `[*.cs]` for a second, unrelated
reason: `src/Agent42.ServiceDefaults/Extensions.cs` declares `namespace Microsoft.Extensions.Hosting;`
on purpose (the Aspire shared-project convention), which is why Backend, AppHost and DevHost call
`AddServiceDefaults()` with no `using Agent42.ServiceDefaults;`. A repo-wide rule would flag it, and
auto-fixing it would break three projects.

The `.editorconfig` is deliberately free of whitespace/formatting keys: `dotnet format` is a CI gate, so
any style key added there reformats the whole repo on the next run.

### 3. `AgentGrain` splits into eighteen partials that live in their feature folders
`src/Agent42.Agents/Conversations/AgentGrain.cs` (core) plus
`src/Agent42.Agents/Conversations/AgentGrain.FollowUps.cs`,
`src/Agent42.Agents/Conversations/AgentGrain.Retention.cs`,
`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`,
`src/Agent42.Agents/Documents/AgentGrain.Documents.cs`,
`src/Agent42.Agents/Documents/AgentGrain.WebDocumentTool.cs`,
`src/Agent42.Agents/Web/AgentGrain.WebTools.cs`,
`src/Agent42.Agents/Location/AgentGrain.LocationTool.cs`,
`src/Agent42.Agents/Scheduling/AgentGrain.Scheduling.cs`,
`src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs`,
`src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs`,
`src/Agent42.Agents/Push/AgentGrain.Push.cs`,
`src/Agent42.Agents/Push/AgentGrain.PushTool.cs`,
`src/Agent42.Agents/Memory/AgentGrain.Memory.cs` (ADR 0056),
`src/Agent42.Agents/Search/AgentGrain.Search.cs` (ADR 0061),
`src/Agent42.Agents/Research/AgentGrain.Research.cs` (ADR 0082),
`src/Agent42.Agents/Artifacts/AgentGrain.ArtifactTools.cs` (ADR 0084) and
`src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs` (ADR 0085).

*(Ten at the time of this ADR. Same in-place-correction rule as the folder list above;
`ProjectLayoutTests.AgentGrainParts` asserts every path by name.)*

Every part declares `namespace Agent42.Agents;`, like every other file in the project — the split is
purely about which file a member lives in.

Rules that shaped the split:
- **All instance fields stay in the core part** — every dependency and every turn-scoped flag. *(Since
  broken twice on purpose: `src/Agent42.Agents/Search/AgentGrain.Search.cs` (ADR 0061) and `src/Agent42.Agents/Research/AgentGrain.Research.cs`
  (ADR 0082) keep the fields only their feature reads beside it, and so does `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs` (ADR 0085). The turn-scoped clear ritual still lives in
  the core part.)* Three
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
`Agent42.Backend` (34 flat files) and `Agent42.Tests` (81 flat files) keep their layout. Because the
namespace did not change, they needed no `using` sweep at all — the only edits outside
`src/Agent42.Agents` are the ones the `AgentGrain` split itself required. Reorganising either is its
own follow-up MR.

## Measurements — including the one that was wrong

**Grain identity — how a document is FOUND.** The persisted item id is `{GrainType}/{key}`, and no grain
declares `[GrainType]`, so all twelve fall through to Orleans' naming convention. `GrainIdentityTests`
pins all twelve type tokens plus the full `agent/{userId}/{conversationId}` id, with **hard-coded
literal** expectations — computing them from the type would make the test move with the code it guards.
Unchanged by this work.

**Binary serialization — measured by mutation.** Golden blobs for all twelve persisted state roots were
generated before the move and committed verbatim. Three mutations:

| Mutation | Caught by the golden blobs? |
|---|---|
| Renumber one `[Id(n)]` (`StoredModel.SupportsSpeech` 6 → 7) | **yes** |
| Change an `[Alias]` string (`StoredMcpServer`) | no |
| Move a serialized type + its grain to another namespace | no |

**The mistake, recorded because it is the most useful thing in this ADR.** That third row was read as
"the namespace move is inert — measured rather than hoped," and the folder-matching-namespace version of
this refactor was built, reviewed and opened as a PR on that basis. It passed the full 596-test suite,
`dotnet format`, an adversarial `critic` review, and CI.

It was wrong. `SerializationCompatibilityTests` builds a `Serializer<T>` — Orleans' **binary**
serializer, which carries grain-to-grain calls. **Cosmos grain storage does not use it.** Resolving the
real `IGrainStorageSerializer` from a live silo gives `JsonGrainStorageSerializer`, and feeding it a
pre-move document produces:

```
JsonSerializationException: Error resolving type specified in JSON
'Agent42.Agents.AgentGrainState, Agent42.Agents'. Path '$type', line 1, position 67.
```

The plan had listed "which serializer does Cosmos actually use" as **out of scope**, inherited from an
open question in ADR 0013. That deferral was the whole failure: the one unmeasured mechanism was the one
that mattered, and every other green signal was measuring something else. The lesson is not "test more"
— it is that a test proves a property of *the path it exercises*, and "we verified serialization" is
meaningless until you say which serializer.

**What now covers it.** `GrainStorageSerializerTests` exercises the real configured
`IGrainStorageSerializer`: it pins the JSON-with-`$type` format, pins all twelve state-root type names
as literals, and deserializes a **committed real pre-move payload** to prove a document written by an
earlier deployment still loads. Its negative control reproduces the exact production exception.

## Consequences

- **The namespace of every persisted type is now a frozen part of the on-disk format**, and the folder
  layout permanently disagrees with it. That is unusual enough that it needs two active guards
  (`.editorconfig` turning IDE0130 off, `ProjectLayoutTests` catching a hand edit) plus a gotcha entry
  and a comment at `AgentGrainState`, because every IDE will keep suggesting the change that breaks it.
- **The 54 `[Alias("Agent42.Agents.*")]` strings stay frozen too**, but for a different and lesser
  reason: they name types for the binary path used by grain-to-grain calls. They are **not** what
  protects data at rest. The gotchas now say this explicitly, since conflating the two is what produced
  the mistake above.
- `AgentGrain.StorageProviderName` is consumed by eleven `[PersistentState(...)]` attributes across
  seven folders. With a flat namespace this costs nothing — no `using` at all — which is one small,
  incidental benefit of the outcome we landed on.
- **The reviewability benefit is fully retained.** Feature folders and the ten-file `AgentGrain` split
  are what make an ADR's grain surface an openable file; the namespace was never what delivered that.
- **What was lost:** compiler-enforced folder/namespace agreement. `ProjectLayoutTests` still asserts the
  folder set by name and that nothing sits in the project root, so the layout is guarded — but a file in
  the wrong folder is now a review question, not a build error. Accepted deliberately: the enforcement
  was worth strictly less than the data.

## Alternatives considered

- **Folder-matching namespaces (`Agent42.Agents.Web`), enforced by IDE0130.** This was the original
  decision, and it was fully built: 16 folders, 82 namespaces changed, a 79-file `using` sweep, the
  enforcement rule verified in both directions. **Rejected on discovering it destroys all existing
  production data** — see the measurements section. Reverting it also removed the entire `using` sweep,
  which shrank the diff considerably.
- **Keep the namespaces and add a Newtonsoft `SerializationBinder`** mapping the ~54 old flat type names
  onto the new namespaced types. Workable and a well-known pattern, but it is a permanent compatibility
  shim that every grain host must register — the same must-register-on-every-host failure mode this repo
  already has several gotchas about — and it can never be deleted while any un-rewritten document
  exists. Rejected: a permanent runtime shim is too high a price for namespace tidiness.
- **Keep the namespaces and migrate the data**, rewriting `$type` in every `agent-state` document.
  Rejected: it needs a write pass over production Cosmos, a deployment window in which old and new code
  must not overlap, and a rollback plan — real operational risk in exchange for no behaviour change.
- **A `Feature/` prefix directory.** Rejected as a redundant level; the project *is* the feature set.
- **Extracting the `AgentGrain` slices into separate collaborator classes.** The real decoupling, still
  open — but it moves turn-scoped state across object boundaries, which is a behavioural change. A
  partial split is deliberately the non-behavioural step.
- **Reorganising Backend and Tests in the same MR.** Rejected as scope.

## What this settles, and what it does not

**Settled:** which serializer Cosmos grain storage uses. It is `JsonGrainStorageSerializer` (JSON with
`$type` discriminators), resolved from the default `IGrainStorageSerializer` because
`AddCosmosGrainStorage` configures none. This closes the open question ADR 0013 raised on
`AgentGrainState.DeploymentName` — and answers it in the direction ADR 0013 feared: **property names
and type names are both load-bearing**, so `DeploymentName` must not be renamed either.

**Not settled:** whether any document currently in production carries a `$type` written by an older
Orleans version with different settings. The analysis here is from the configured serializer in this
repo at this commit, not from reading a production document. Anyone planning a type rename should look
at a real document first.
