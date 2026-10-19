---
id: 0040
date: 2026-08-18
status: accepted
tags: [refactor, structure, backend, minimal-api, tooling]
supersedes: []
amends: []
extends: [0039]
---

# Feature folders in `Agent42.Backend`, and `MapAgentApi` split into per-feature partials

> **Amended by [ADR 0105](0105-shared-silo-registration.md) (2026-10-19): §4's guard is now a runtime test.**
> `AgentApiReachabilityTests` maps each reflected part alone and requires its routes in `MapAgentApi`. The source-text
> call check is gone; the folder theory, renamed `AgentApiPart_ExistsInItsFeatureFolder`, keeps its list, which had
> fallen four parts behind and is now pinned equal to the reflected set.

## Context
ADR 0039 reorganised `Agent42.Agents` into feature folders with a deliberately flat namespace, and
explicitly scoped itself out of `Agent42.Backend` ("34 flat files … its own follow-up MR"). This is
that follow-up.

`Agent42.Backend` had the same two problems, in the same shapes: 34 `.cs` files in one directory, and
one file — `AgentApi.cs`, 1268 lines — holding the entire HTTP surface. Its `MapAgentApi` method alone
was ~940 lines registering 44 endpoints, with 25 request/response records trailing after the class.
Finding the MCP OAuth routes or the share-link contract meant knowing a line range.

## Decision

### 1. Twenty-eight feature folders, flat namespace, same as ADR 0039
`Api/`, `Artifacts/`, `Attachments/`, `ClientTools/`, `Configuration/`, `Conversations/`, `Copilot/`,
`Documents/`, `Export/`, `Fleet/` (ADR 0097), `ImageGeneration/`, `Location/`, `Mcp/`, `Memory/`, `Models/`, `Orleans/`, `Push/`, `Search/`,
`SharedState/`, `Sharing/`, `Speech/`, `StoredAgents/`, `Telemetry/`, `Tools/`, `UserSkills/`, `Users/`, `Web/`, `Webhooks/` (ADR 0094).

*(This heading read "Twelve" while listing seventeen — it had picked up the count of the endpoint
groups in §3. Corrected, along with `UserSkills/` (ADR 0049), `Calendar/` (ADR 0053) and `Memory/`
(ADR 0056), then again for `Search/` (ADR 0061), `Orleans/` (ADR 0062) and the REMOVAL of `Calendar/`
(ADR 0063), and for `Artifacts/` (ADR 0084, missing until ADR 0085), `Copilot/` (ADR 0085) and `ClientTools/` and
`SharedState/` (ADR 0089), `Export/` (ADR 0091), `Webhooks/` (ADR 0094) and `Fleet/` (ADR 0097) — `Memory/` holding `HindsightRegistration`, which lives in its own folder because adding
its branch to `Program.cs` inline breached `cognitive_complexity_max`, the same reason
`DocumentIndexRegistration` sits in `Documents/`. A folder list is a factual inventory, not a
decision, so it is corrected in place; `ProjectLayoutTests.ExpectedFolders` asserts it by name so the
two cannot drift.)* The vocabulary deliberately mirrors `Agent42.Agents`, so the canonical
pairing this repo already relies on — contract plus in-memory implementation in Agents, production
implementation in Backend — is now visible as the same folder name in both projects
(`src/Agent42.Agents/Attachments/IAttachmentStore.cs` ↔ `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`).

`Program.cs` stays at the project root as the composition root of the host. `wwwroot/`, `Properties/`,
`Dockerfile` and the `appsettings` files are untouched.

**Every type stays in `namespace Agent42.Backend;`.** That is what made the move a pure relocation:
all 33 files are byte-identical renames, and not one consuming file needed a `using` change.

### 2. The namespace stays flat here too, even though Backend persists nothing
ADR 0039's flat namespace is forced: Cosmos grain storage writes the full CLR type name into every
document, so a persisted type's namespace is part of the on-disk format. Backend has no such type, and
this was verified rather than assumed before moving anything:

- no `[GenerateSerializer]` / `[Alias]` / `[Id]` types in the project at all, so nothing of Backend's
  reaches Orleans grain storage or the binary path;
- the Cosmos vector documents (`CosmosDocumentIndex.ChunkDocument`) go through the SDK's default
  serializer, which writes property names and no `$type` — and the record is `private` and nested;
- both DataProtection purposes are hard-coded literals (`"Agent42.Mcp.Secret.v1"`,
  `"Agent42.ModelProviders.ApiKey.v1"`), not derived from type names, so encrypted provider and MCP
  secrets are unaffected by any rename;
- the OpenTelemetry source name is the literal `"Agent42.Backend"`;
- no JSON polymorphism, no assembly scanning, no reflection over type names.

So folder-matching namespaces would probably have been safe here. They were still rejected:

- **Consistency.** Two sibling projects with opposite namespace conventions is a trap in itself; a
  reader who learns the Backend rule and applies it to Agents causes the ADR 0039 outage.
- **"Probably safe" is what ADR 0039 thought.** The cost of being wrong is total data loss and the
  benefit is cosmetic. A type that later moves from Backend into a persisted position must not carry a
  folder-shaped namespace with it.
- **It is free.** A flat namespace means no `using` churn in the 54 test files that reference Backend
  types — the reorganisation touches no consuming file at all.

### 3. `MapAgentApi` splits into per-feature partials (twelve here; twenty today)
Each feature's endpoints move to `<Folder>/AgentApi.<Feature>.cs`, all completing the one
`public static partial class AgentApi`. `src/Agent42.Backend/Api/AgentApi.cs` drops from 1268 to 50 lines and becomes a
composition root: twelve `Map*Endpoints(app)` calls plus the two members genuinely shared across
features — `MaxAttachmentBytes` (used by both the upload endpoint and agent-run) and `AgentKey`.
Twenty calls today: ADR 0049 added one, and several since, with ADR 0063 removing the calendar
group again and ADR 0089 adding `MapClientToolEndpoints` and `MapSharedStateEndpoints`. Same in-place-correction rule as §1 — `ProjectLayoutTests.AgentApiParts` asserts the
set by name, so the prose and the tree cannot drift.

The DTO records likewise move to their feature as `*Contracts.cs` files, following the existing
`UsageContracts.cs` shape in `Agent42.Agents` rather than inventing a convention.

**This part is an EXTRACTION, not a relocation, and that distinction was made explicitly before doing
it.** Unlike ADR 0039's `AgentGrain` split — which relocated whole members and could be proved inert by
reconstruction alone — this creates twelve methods and twelve call sites. What is verified instead:

- **Statement identity.** The 804 non-blank, non-brace, non-doc lines of the old file and of the twelve
  new ones are identical as a multiset. The only new code is the declarations and their calls.
- **Route identity.** All 45 route registrations are byte-identical before and after, each appearing
  exactly once — nothing lost, duplicated or altered.
- **Order independence.** Registration order is preserved by first appearance, but nothing depends on
  it: minimal-API routing resolves by template precedence, not registration order, and all templates
  are distinct. The one precedence subtlety in this API is handled by naming rather than ordering —
  `PUT /api/default-model` is a distinct path rather than `/api/providers/default`, because a
  user-chosen provider key would out-rank a literal segment in `/api/providers/{key}`.

### 4. A new failure mode the split introduces, and its guard
An endpoint group that exists but is never called from `MapAgentApi` **compiles cleanly and silently
removes every endpoint in it**. No build, lint or type check would notice; only a functional test of
each affected route would. `ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot`
asserts, per group, that the part exists, declares the flat namespace, defines its `Map*Endpoints`
method, and that the composition root calls it. Its negative control — deleting one call — fails as
expected.

This is the one genuinely new risk the Backend split carries that the Agents split did not, which is
why it gets a dedicated guard rather than a comment.

## Consequences

- The 44-endpoint HTTP surface is now readable per feature, and the composition root fits on a screen.
- Both projects now share one folder vocabulary, making the contract/implementation pairing visible.
- `ProjectLayoutTests` is parameterised over both projects, so the folder set, the root-file rule and
  the flat-namespace rule are asserted by name for each. Adding a folder means updating that list —
  and therefore the prose it mirrors, which is how the "sixteen folders" drift was caught in ADR 0039.
- Backend's namespace is now frozen flat by convention rather than by necessity. The reason is recorded
  above so a future reader does not "fix" it on the grounds that Backend persists nothing — that
  reasoning is correct today and would still be a mistake.

## Alternatives considered

- **Folder-matching namespaces for Backend only**, since nothing here is persisted. Rejected on
  consistency and blast-radius grounds — see decision 2.
- **Leave `MapAgentApi` whole and move only the DTOs.** This was the recommended option when the choice
  was put, because it keeps the entire change a provable relocation. The maintainer chose the full
  split for the readability benefit, accepting the extraction risk with the verification above.
- **Split the endpoints into separate static classes** rather than partials of `AgentApi`. Cleaner in
  principle, but it would change the public shape (`MapAgentApi` is the single entry point the host
  calls) and force either a new public surface or a chain of internal classes. A partial split keeps
  one entry point and one class, exactly as ADR 0039 did for `AgentGrain`.
- **Reorganising `Agent42.Tests` (81 flat files) in the same MR.** Rejected as scope; it is the obvious
  next follow-up and has no persistence considerations at all.
