---
planned-against: main @ 44413e7
---

# Let a second Entra account sign in as an existing user (identity alias)

## Overview

The operator holds two Entra accounts — `primary@example.com` (the one every byte of
production data is keyed to) and `secondary@example.com` (the one they want to use going
forward) — and wants the second to reach the first's data. **Both must keep working.**

`userId` is the Entra object id read from `X-MS-CLIENT-PRINCIPAL-ID`
(`src/Agent42.Backend/Api/CurrentUser.cs:59`), and it is the key for effectively all
durable state. A real data migration was considered and **rejected by the user** (see
Context). Instead, a config-driven **alias map** rewrites the incoming oid to the
canonical one inside `ResolveUserId` — the choke point the code already maintains for
exactly this reason — so **no data moves at all**.

Both object ids are **already committed** at `infra/main.bicep:107-111`, so this ships
populated and complete in one MR:

| Role | Account | Object id |
|---|---|---|
| **alias** (incoming) | `secondary@example.com` | `22222222-2222-2222-2222-222222222222` |
| **canonical** (owns the data) | `primary@example.com` | `11111111-1111-1111-1111-111111111111` |

> **Confirm this direction with the user before implementing.** It is inferred from
> "I want to use another one … but keep all my data", not stated outright. Reversing it
> would strand the data rather than expose it, but it would strand it.

Why now: the user wants to switch accounts, and the alternative loses the Hindsight
long-term memory bank outright.

## Affected areas

Verified against the tree at `44413e7`.

| Path | Change |
|---|---|
| `src/Agent42.Backend/Api/UserIdAliases.cs` | **New.** The pure rule + its binding/validation. `Api/` already exists — no new folder, so `ProjectLayoutTests.ExpectedFolders()` is untouched. |
| `src/Agent42.Backend/Api/CurrentUser.cs` | `ResolveUserId` consults the map; `GetUserId` + the telemetry entry points thread it in. **Also fix two now-false doc comments** — line 58 (`The Entra ID object id of the signed-in user`) and the class summary's "single home of Easy Auth header knowledge" framing (hard rule 2). |
| `src/Agent42.Backend/Program.cs` | Build the map as a **local**, register it, and **capture that local** in the `EnrichWithHttpRequest` lambda (line 62). |
| `src/Agent42.Backend/Telemetry/UserTelemetryMiddleware.cs` | Pass the map into `GetUserTelemetryContext` (the ADR 0010 `RequestContext` carrier). |
| `src/Agent42.Backend/appsettings.json` | New committed `Identity` section, **populated** with the pair above. |
| `src/Agent42.Backend/Configuration/ConfigHealth.cs` (step 6 only) | One check reporting dropped alias entries. `BuildStatic` is a **pure function** of `IConfiguration` + `IHostEnvironment` + `PushConfig` (ADR 0029, and its own comment at line 25) — re-derive from `IConfiguration`, never take the singleton as a parameter, or it stops being pure and becomes a mandatory dependency for every host. |
| `tests/Agent42.Tests/UserIdAliasTests.cs` | **New.** |
| `tests/Agent42.Tests/UserTelemetryTests.cs` | Extended. |
| `tests/Agent42.Tests/SharedApiFixture.cs` / `AgentApiFixture` | **Must be edited** — `AgentApiTests` cannot register config into its own host; the host is built once per class in the fixture (`SharedApiFixture.cs:46-61`). Note the fixture is **shared with `ChatGroupApiTests`**, so both classes then run with a map configured. |
| `tests/Agent42.Tests/AgentApiTests.cs` | Extended (end-to-end two-principal test). |
| `tests/Agent42.Tests/DashboardOwnerGateTests.cs` | Extended; its `BuildGatedHostAsync` must register the map. |
| `tests/Agent42.Tests/ProjectLayoutTests.cs` | Extended — the `Program.cs` source-text wiring guard (AC16). |
| `.agentic/decisions/0074-*.md` | **New ADR** (0073 is the highest taken). |
| `.agentic/gotchas.md`, `CLAUDE.md` | Doc debt paid in the same MR (hard rule 2). |

**Explicitly NOT touched:** no Bicep, no `excludedPaths`, no GitHub secret, no
deploy-workflow change, no Cosmos container, no grain, no migration, no frontend, no
`sw.js` `CACHE` bump (verified: `wwwroot/js/confighealth.js:37-52` renders whatever checks
arrive, so step 6 needs no JS change).

## Context & constraints

### Why an alias and not a migration (settled with the user — do not relitigate)

The oid is baked into: Cosmos `agent-state` grain keys (`{userId}/{convId}` for
conversations, bare `userId` for 11 other state types); `doc-chunks` partition keys
(`CosmosDocumentIndex.cs:36`); `chat-messages` partition key `/userId`
(`CosmosChatSearchIndex.cs:36`); the `reminders` container; blob `attachments`
(`BlobAttachmentStore.cs:48`); blob `skill-packages` (`BlobSkillPackageStore.cs:65`); the
`UserId` stored *inside* `AgentKeyDirectoryGrain`/`ShareLinkDirectoryGrain` state; and the
Hindsight `BankId` (`src/Agent42.Agents/Memory/AgentGrain.Memory.cs:68`). Cosmos partition
keys are immutable, and the Hindsight bank is third-party with **no delete or rename path**
(ADR 0056) — so long-term memory is unmigratable. The alias moves nothing.

### Key investigation findings (each with evidence)

- **`CurrentUser` is the only reader of the principal id** — verified by grep; only
  `Web/SearxngProxy.cs:20,90` mentions the headers, and only to strip them outbound. The
  comment at `CurrentUser.cs:116` states the single-choke-point design is deliberate.
- **No endpoint compares a stored userId to the request user** — grepped for `GetUserId()`
  next to `==`/`Equals`, and for `userId ==|OwnerUserId`: zero hits. The alias cannot break
  an ownership check.
- **`user.email` / `user.name` / `user.full_name` / `user.roles` are NOT derived from
  `ResolveUserId`.** They come from `X-MS-CLIENT-PRINCIPAL-NAME` and the claims blob
  (`CurrentUser.cs:74-90`); only `Id` (line 90) comes from `ResolveUserId`. **Only `user.id`
  is aliasable.** Re-verified at the source after a scout reported the opposite.
- **`/usage` does NOT key on `user.id`.** `UsageSpanProjector.Project`
  (`src/Agent42.Agents/Telemetry/UsageSpanProjector.cs:54-57`) resolves the user bucket as
  `user.email → user.name → user.id`, and in production the first two are always present.
  **Consequence: `/usage` shows two rows for one human whether or not the alias is applied**
  (see D1).
- **Both oids are already committed** at `infra/main.bicep:107-111` as
  `authAllowedUserObjectIds`, with `secondary@example.com` named in the comment. Nothing
  overrides it (grepped `infra/` and `.github/`). This is why the map ships populated —
  and it enables the strongest available safety test (AC10).
- **`Dashboard:OwnerOid` needs no Bicep** — it lives only in
  `src/Agent42.Backend/appsettings.json:59`, read via `IConfiguration[key]`
  (`DashboardOwnerGate.cs:61`), overridden offline by `AppHost.cs:62` and
  `tools/Agent42.DevHost/Program.cs:211`. The precedent for the new section.
- **Shipped-config safety-test precedent:**
  `DashboardOwnerGateTests.ShippedConfiguration_NeverNamesTheOfflineFallbackUserAsOwner()`
  (`DashboardOwnerGateTests.cs:88-108`) reads `appsettings.json` from disk.
- **Source-text wiring-guard precedent:** `ProjectLayoutTests.cs:534-546` — *"No test
  executes `Program.cs`, so this source-text guard is the only thing standing between that
  mistake and production."* That is this situation exactly (see D5/AC16).
- **Test identity pattern:** xUnit; `request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", …)` —
  `AgentApiTests.cs:584-602`, `:512`. The shared `Client` sends no principal header, so it
  resolves to `local-dev`. Naming: `Subject_Condition_ExpectedBehavior`.

### Governing ADRs — and the ones this amends

- **ADR 0001 lines 34-35 is the sentence this amends:** "Grain keys are
  `{userId}/{conversationId}`, so one user can never address another's conversation." Still
  true of *userIds*; what changes is that two Entra principals now map to one. An alias
  entry is therefore an **authorization grant**.
- **ADR 0002** — identity still originates only from Easy Auth headers; the alias is
  applied after extraction and is never a substitute for authentication.
- **ADR 0009** — describes `user.id` as the Entra oid throughout, and lists "`user.id`
  only" among its alternatives. D1 makes it no longer the requesting principal's oid, so
  0009 must be amended, not merely "not contradicted".
- **ADR 0062** — its whole security argument is that `/dashboard` *"is **not** a third
  cross-user surface … it is **one principal**."* D6 makes it two Entra principals. Amend it.
- **ADR 0005 / 0016** assume a 1:1 oid↔user correspondence; name them as affected.
- **ADR 0022 / 0035 / 0070 are unaffected — verified, not assumed:**
  `Mcp/AgentMcpRunner.cs:53,58,103` uses `caller.UserId`; `Sharing/AgentApi.Sharing.cs:61,79`
  uses `target.UserId`; `StoredAgents/AgentApi.StoredAgents.cs:95` uses `owner.UserId`. None
  calls `GetUserId` on an excluded path, so the gotcha at `gotchas.md:1786-1824` is not
  tripped.
- **Nothing in the repo has ever discussed aliasing/impersonation** — no prior art.

### Decisions made in this planning session

- **D1 — the alias applies to BOTH grain keying and telemetry `user.id`.** Reason: the
  single resolution path is an explicit design property (`CurrentUser.cs:116`: kept in one
  place "so [both readers] can never drift"). Attribution survives because
  `user.name`/`user.email` are never aliased — a span reads `user.id = <canonical oid>` next
  to `user.name = secondary@example.com`.
  **Recorded consequence, NOT a benefit:** `/usage` still shows **two rows for one human**,
  because `UsageSpanProjector` keys on email first. Unifying it would require aliasing
  email/name too, which is rejected — that would destroy attribution on the app's widest
  read surfaces. AC11b pins this so the claim stays falsifiable.
  *Rejected:* aliasing only the grain key (reintroduces the drift that comment exists to
  prevent, for no gain — `/usage` splits either way); a separate `user.alias_of` attribute
  (a new tag on the widest read surface for no operational need).
- **D2 — config shape:** `Identity:Aliases`, a `Dictionary<string,string>` **keyed by the
  incoming (alias) oid**, value = the canonical oid. Committed in
  `src/Agent42.Backend/appsettings.json`. Keyed by alias so the request path is one O(1)
  lookup with no ambiguity about which side is canonical.
- **D3 — validation drops, never throws.** A malformed map must not take the app down (the
  `GOOGLE_MAPS_API_KEY` shape, not `SEARXNG_SECRET`'s; eager-throw here is reserved for the
  availability path, `Program.cs:226-231`). Rules, all `OrdinalIgnoreCase`:
  blank key or value · `local-dev` on either side · self-alias · **a value that is not a
  parseable GUID, or that contains `/`** (it flows into a grain key split on the *first*
  slash — `AgentApi.cs:53` → `AgentGrain.cs:332` — and into a blob prefix) · **chains and
  cycles: if any value is also a key, drop EVERY entry in that component**, never just the
  head. Single-hop resolution plus whole-component dropping is what makes "never
  half-applied" true rather than aspirational.
- **D4 — the `local-dev` fallback is preserved exactly:** the map is consulted **only when
  a principal header is present**. The no-header path returns `local-dev` before any lookup,
  so DevHost/AppHost and the offline `/dashboard` story are untouched.
- **D5 — optional DI resolution** (`GetService`, null ⇒ passthrough), the `/api/speech` /
  `/api/config-health` pattern, so no host gains a mandatory registration.
  **Its real failure mode is not access-widening but silent total inertness:** forget the
  `AddSingleton` in `Program.cs` and the alias never applies in production while every
  runtime test passes, because no test executes `Program.cs` (`Program.cs:94` says so).
  AC16's source-text guard is therefore **required**, not a substitute for a runtime test.
- **D6 — `/dashboard` inherits the alias, deliberately.** `DashboardOwnerGate.InvokeAsync`
  compares `context.GetUserId()`, so the aliased account reaches the dashboard when the
  canonical oid is the configured owner. Correct (same person), asserted rather than
  accidental. `Dashboard:OwnerOid` ships blank, so it is inert today.
- **D7 — accepted residual, to be named in the ADR.** An alias **key** that is another
  allowlisted principal's oid would silently strand that person's data *and* hand them the
  canonical user's entire dataset — conversations, RAG excerpts, street address (ADR 0012),
  Hindsight memory (ADR 0056, no delete path), share-link minting (ADR 0035). No validation
  can detect this; GUID-parsing certainly cannot. AC10's cross-check bounds the blast radius
  to principals Easy Auth already admits, but does not eliminate it. This is what makes
  "an alias entry is an authorization grant" more than a slogan.
- **D8 — a grant added via env var is invisible to AC9.** `Identity__Aliases__<oid>=<oid>`
  set as a container env var or portal edit never appears in `appsettings.json`. Name this
  in the ADR's consequences; it is also why AC10 cross-checks against Bicep, which *is* in git.

### Manual prerequisite — the user's, not the agent's

Only one remains, and it is a **verification**, not a task: `infra/main.bicep:107-111`
warns that `authAllowedUserObjectIds` *"Must stay in sync with the user assignments on the
agent42-easyauth enterprise app."* Confirm `secondary@example.com` is actually assigned there.
Per hard rule 5 the agent prepares any `az` command; the user runs it.

**Operational warning to give the user before deploy:** signing in as
`secondary@example.com` *before* the alias ships creates a second, permanently unreachable
dataset under its own oid — and if `Hindsight:ApiKey` is set and a turn runs, a memory bank
with no delete path (ADR 0056). Check whether this has already happened.

## Steps

1. **`UserIdAliases` — the pure rule.** New `src/Agent42.Backend/Api/UserIdAliases.cs`:
   a `SectionName` constant, `FromConfiguration(IConfiguration)` that binds and validates
   per D3, a `DroppedEntries` collection the caller logs (step 6 consumes it), `IsEmpty`,
   and `Resolve(string rawUserId)` — a **single-hop** `OrdinalIgnoreCase` lookup. **Rebuild
   the validated map with `StringComparer.OrdinalIgnoreCase` explicitly**: a bare
   `GetSection(...).Get<Dictionary<string,string>>()` returns an *ordinal* dictionary
   (`ModelProvidersOptions` keeps its comparer only because the binder fills an existing
   instance — `ModelProviderOptions.cs:157-158`), which would make AC4 pass by accident.
   Keep it free of DI and `HttpContext`, the `DashboardOwnerGate.IsOwner` shape.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserIdAliasTests`.

2. **Wire it in.** `CurrentUser.ResolveUserId` takes a `UserIdAliases?` and applies it
   **only on the header-present branch**; `GetUserId(this HttpContext)` resolves it
   optionally from `http.RequestServices`; `GetUserTelemetryContext`/`GetUserTelemetryTags`/
   `ApplyUserTelemetryTags` take it as a parameter. In `Program.cs` build the map as a
   **local**, `AddSingleton` it, and **capture that local** in the `EnrichWithHttpRequest`
   lambda (line 62) — that delegate receives an `HttpRequest`, not an `HttpContext`, and
   runs at `HttpRequestIn.Start`, *before* the middleware pipeline, so **never resolve DI
   inside it**. Log dropped entries once at startup. Fix the two stale doc comments in
   `CurrentUser.cs`. Watch S3776 (threshold 15) on `Program.cs`; if it tips, lift the
   registration into its own file the `HindsightRegistration` way, never a ternary.
   *Verify:* `dotnet build Agent42.slnx`, then
   `dotnet test Agent42.slnx --filter FullyQualifiedName~UserTelemetryTests`.

3. **Ship the config + its safety tests.** Add the `Identity` section to
   `src/Agent42.Backend/appsettings.json`, **populated** with the pair from the Overview,
   with `//` comment keys in the house style (not a secret; an alias entry is an
   authorization grant; `local-dev` forbidden both sides and why; D7's residual). Tests read
   `appsettings.json` from disk — the `ShippedConfiguration_*` shape — and additionally
   **cross-check every key and value against `authAllowedUserObjectIds` in
   `infra/main.bicep`** (the `EasyAuthExclusionTests` regex-over-Bicep pattern).
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~UserIdAliasTests`.

4. **End-to-end proof through the API.** Register the map in the fixture (see Affected
   areas — `AgentApiTests` cannot do it itself). Drive `/api/profile` as the alias principal
   and read it back as the canonical one and vice versa — one profile, both ids. Then the
   security half: a **third, unlisted** principal stays fully isolated.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentApiTests`.
   *Parallelizable with step 5.*

5. **Dashboard-gate interaction (D6).** In `DashboardOwnerGateTests`, register the map in
   `BuildGatedHostAsync`; assert the aliased account reaches `/dashboard` when the canonical
   oid is the owner, and an unlisted principal still gets 404.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~DashboardOwnerGateTests`.
   *Parallelizable with step 4.*

6. **Surface dropped entries in Configuration health (ADR 0029).** One check: `ok` (no
   aliases) · `ok` (N active) · `warning` (M dropped as invalid). Re-derive from
   `IConfiguration` to keep `BuildStatic` pure; run **zero** probes; never echo an oid
   beyond what is already committed. **Severable** — drop it if it grows past one check.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~ConfigHealth`.

7. **Pay the doc debt in the same MR.** `/decide` → ADR **0074**, recording D1–D8, the
   amendment to ADR 0001's isolation sentence, the "alias entry = authorization grant"
   framing, D7's residual, D8's env-var blind spot, the honest `/usage` consequence, and the
   rejected data migration with the Hindsight reason. Add "Amended by" headers to ADRs
   **0001, 0002, 0009 and 0062**. `/gotcha` → one entry: *the alias is applied at the single
   choke point and therefore reaches grain keys, telemetry `user.id`, the ADR 0010
   `RequestContext` carrier and the dashboard gate alike — while `user.name`/`user.email`
   stay the real account, and `/usage` therefore still splits.* Add a `CLAUDE.md` bullet.
   *Verify:* the doc-path integrity script `pr-checks.yml` invokes, plus the full suite.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A configured alias oid resolves to the canonical oid | `Resolve_MapsAConfiguredAlias_ToTheCanonicalId` | `UserIdAliasTests.cs` |
| AC2 | **An unlisted principal is never rewritten** | `Resolve_LeavesAnUnlistedPrincipal_Unchanged` | `UserIdAliasTests.cs` |
| AC3 | An empty/absent map is a pure passthrough | `Resolve_WithNoAliasesConfigured_IsAPassthrough` | `UserIdAliasTests.cs` |
| AC4 | Lookup is case-insensitive, and the map is built with an explicit ignore-case comparer | `Resolve_MatchesTheAliasIdCaseInsensitively` | `UserIdAliasTests.cs` |
| AC5 | A blank value never rewrites an id to empty | `FromConfiguration_DropsAnEntryWithABlankValue` | `UserIdAliasTests.cs` |
| AC6 | `local-dev` is refused on **both** sides | `FromConfiguration_DropsAnyEntryNamingTheOfflineFallbackUser` | `UserIdAliasTests.cs` |
| AC7a | A chain drops **every entry in the component**, not just the head | `FromConfiguration_DropsEveryEntryOfAChain` | `UserIdAliasTests.cs` |
| AC7b | A cycle drops likewise | `FromConfiguration_DropsACyclicAlias` | `UserIdAliasTests.cs` |
| AC7c | A chain written in **mixed case** is still detected | `FromConfiguration_DetectsAChainCaseInsensitively` | `UserIdAliasTests.cs` |
| AC8a | A malformed map never throws at startup | `FromConfiguration_WithMalformedEntries_DoesNotThrow` | `UserIdAliasTests.cs` |
| AC8b | A non-GUID value, or one containing `/`, is dropped (grain-key/blob-prefix safety) | `FromConfiguration_DropsAValueThatIsNotABareGuid` | `UserIdAliasTests.cs` |
| AC9 | The shipped `appsettings.json` holds no unsafe entry | `ShippedConfiguration_ContainsNoUnsafeAlias` | `UserIdAliasTests.cs` |
| AC10 | **Every shipped alias key and value is an oid Easy Auth already admits** (cross-checked against `authAllowedUserObjectIds` in `infra/main.bicep`) | `ShippedAliases_NameOnlyPrincipalsEasyAuthAdmits` | `UserIdAliasTests.cs` |
| AC11a | Telemetry `user.id` is aliased while `user.name`/`user.email` remain the real account (D1) | `GetUserTelemetryTags_WithAnAlias_RewritesOnlyTheId` | `UserTelemetryTests.cs` |
| AC11b | **`/usage` still splits one human across two rows** — the projector keys on email first (D1's honest consequence) | `UsageSpanProjector_WithAnAliasedId_StillSplitsOnEmail` | `UsageProjectionTests.cs` (confirm actual class) |
| AC12 | The ADR 0010 `RequestContext` carrier is aliased too, so in-grain spans match server spans | `UserTelemetryMiddleware_WithAnAlias_PublishesTheCanonicalId` | `UserTelemetryTests.cs` |
| AC13 | The `local-dev` fallback is byte-identical with no header, even with aliases configured | existing `GetUserTelemetryTags_NoHeaders_FallsBackToLocalDev` (must stay green) + `ResolveUserId_WithNoPrincipalHeader_IgnoresTheAliasMap` | `UserTelemetryTests.cs` |
| AC14 | Two principals reach one profile end-to-end | `ProfileEndpoint_IsSharedAcrossAnAliasedPrincipal` | `AgentApiTests.cs` |
| AC15 | **A third, unlisted principal stays isolated end-to-end** | `ProfileEndpoint_KeepsAnUnlistedPrincipalIsolated_WhenAliasesAreConfigured` | `AgentApiTests.cs` |
| AC16 | **`Program.cs` actually builds, registers and captures the map** — source text, because no test executes `Program.cs` (D5) | `Program_BuildsAndRegistersTheUserIdAliasMap` | `ProjectLayoutTests.cs` |
| AC17 | An unregistered map behaves exactly as before (D5's safe direction) | `GetUserId_WithNoAliasServiceRegistered_IsAPassthrough` | `UserIdAliasTests.cs` |
| AC18 | The aliased account reaches `/dashboard`; an unlisted one still 404s (D6) | `Gate_AllowsAnAliasedPrincipal_OfTheConfiguredOwner` | `DashboardOwnerGateTests.cs` |

**Security set — AC2, AC5, AC6, AC7a-c, AC8b, AC10, AC15, AC16.** If any is weakened during
execution, stop and re-plan rather than adjusting the assertion.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (`44413e7`):

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura × `git diff` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — an `error`, so it fails `dotnet build` |
| `vulnerable_packages` | **0** | CI package-freshness job |
| Lint | no diff | `dotnet format Agent42.slnx --verify-no-changes` |

Branch: `feature/*` off `main` + MR. **Never commit to `main`** (hard rule 1).

## Out of scope

- **The real data migration.** Rejected by the user this session; do not revive it.
- **Merging or moving the Hindsight memory bank** — no delete/rename path (ADR 0056).
- **Removing `primary@example.com`'s access.** The user chose to keep both signing in.
- **Unifying `/usage` rows** — would require aliasing email/name, destroying attribution.
- **Cleaning up any dataset already created under the alias oid** by a prior sign-in.
- **Any UI.** No account-linking screen, no Preferences surface, no frontend change, and
  therefore no `sw.js` `CACHE` bump.
- **A general multi-identity feature** (self-service linking, N users, an invite flow).
- **Renaming the canonical id** — the old oid stays the internal identifier permanently.

## Review

A `critic` subagent reviewed this plan against the repo's knowledge files and the code.
**Three blocking, six should-fix, three nits — all accepted and folded in.** The three
blocking findings were independently re-verified at the source before acceptance:

| Finding | Disposition |
|---|---|
| **B1** — D1's `/usage` rationale was false; `UsageSpanProjector.cs:54-57` keys on `user.email → user.name → user.id` | **Accepted.** Verified. Benefit struck; replaced with a recorded consequence + falsifiable AC11b. |
| **B2** — both oids are already committed at `infra/main.bicep:107-111`; the prereqs were largely done and the map could ship populated | **Accepted.** Verified. Prereqs rewritten to one verification, map ships populated, and the Bicep cross-check became AC10 — the strongest safety test available, and one the plan had missed. |
| **B3** — D5 + "no test executes `Program.cs`" means the feature can ship completely inert with all ACs green | **Accepted.** Verified the precedent at `ProjectLayoutTests.cs:534-546`. Added AC16. |
| **S1** — the chain rule half-applied; no casing or shape validation | **Accepted.** D3 now drops the whole component, `OrdinalIgnoreCase`, plus GUID/slash shape validation. AC7 split three ways; AC8b added. |
| **S2** — amended-ADR set incomplete (0009, 0062) + stale `CurrentUser.cs` doc comments | **Accepted.** Both added to step 7; doc comments added to Affected areas. |
| **S3** — no AC covered the ADR 0010 `RequestContext` carrier | **Accepted.** AC12 added. |
| **S4** — `Program.cs:62` receives an `HttpRequest`, not an `HttpContext`, and runs before the pipeline | **Accepted.** Step 2 now mandates capturing a local and forbids DI resolution in the lambda. |
| **S5** — wrong config-health path | **Accepted.** Corrected to `Configuration/ConfigHealth.cs` with the `BuildStatic` purity constraint. |
| **S6** — the test fixture must be edited, not just the test file | **Accepted.** Added to Affected areas, with the `ChatGroupApiTests` sharing note. |
| **N1** — the dictionary precedent does not supply `OrdinalIgnoreCase` | **Accepted.** Step 1 now says rebuild explicitly. |
| **N2** — an env-var grant is invisible to AC9 | **Accepted.** Recorded as D8. |
| **N3** — the unmitigable residual was unrecorded | **Accepted.** Recorded as D7. |

**Nothing was rejected.** The critic also explicitly confirmed sound: D1's telemetry premise
at the source; ADR 0022/0035/0070 being unaffected; the `excludedPaths`/`local-dev` gotcha
not tripped; no ownership comparison anywhere; the `$type`/flat-namespace Cosmos hazard not
in play; the grain-activation ordering hazard not applicable; "drop and log, never throw"
being right for this repo; and that two aliased principals are indistinguishable from two
browser tabs of one account (with ADR 0028 push merging both devices into one subscription
list — the intended behaviour).

## Implementation notes — where it diverged from the plan

Executed on `feature/second-entra-account-identity-alias` off `main` @ `44413e7` (no drift).
All 18 ACs implemented and tested, plus six tests the plan did not call for. Divergences:

1. **The plan's chain rule would have dropped legitimate fan-in — corrected.** D3 said "drop every
   entry in that component", which for `{A→C, B→C}` (two accounts onto one dataset, the
   three-accounts-one-person shape) drops both. The shipped rule is the intersection **keys ∩ values**,
   `OrdinalIgnoreCase`: every entry touching an id that is both a key and a value is dropped, so chains
   and cycles still go whole, while fan-in — where the canonical id is never itself a key — survives.
   `FromConfiguration_KeepsTwoAliasesPointingAtOneCanonicalId` pins it.
2. **Value validation is `Guid.TryParseExact(value, "D")`**, not "a GUID, and no `/`". The exact
   hyphenated form Entra emits subsumes the separator check, so a re-partitioned grain key is
   structurally impossible rather than checked for. AC8b covers `{braced}` and slash-bearing inputs.
3. **A real defect the plan did not anticipate, caught by an existing test.**
   `HttpContext.RequestServices` is **null** on a bare `DefaultHttpContext`, and `GetService<T>()`
   *throws* on a null provider rather than returning null — so the first draft broke
   `UserTelemetryProcessorTests` (2 failures). Both resolutions are now null-conditional. This is not
   only a test artifact: `UserTelemetryMiddleware` is registered **first**, before request services are
   guaranteed. `GetUserId_WithNoRequestServices_DoesNotThrow` is the regression pin.
4. **The telemetry methods take a REQUIRED `UserIdAliases?`**, not an optional defaulted one. That
   forced every one of the seven existing call sites to be visited rather than silently keeping the old
   behaviour — the compiler doing the work a source-text guard would otherwise have to.
5. **`UserIdAliases` skips `//`-prefixed keys silently.** This repo documents `appsettings.json` with
   `//` sibling keys; one written inside the section is prose, not a mis-typed alias, and reporting it
   as dropped would make documenting the section raise a health warning.
6. **Step 6 (config health) was kept**, not severed: it stayed a single check plus a private helper, and
   `BuildStatic` remains pure by re-deriving from `IConfiguration`.
7. **`dotnet format` had to be run.** The edits introduced 8 whitespace diffs in `Program.cs`
   (verified against a clean tree: 0 before, 8 after). Lint is clean at the commit.

Extra tests beyond the AC table: fan-in preservation, self-alias, blank-key, null-`RequestServices`,
and two more config-health states.
