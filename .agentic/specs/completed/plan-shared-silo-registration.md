planned-against: main @ 49f5153

# Plan: one shared silo registration + a runtime `Map*` reachability guard

## Overview

`src/Agent42.Backend/Program.cs` is 591 lines with ~40 `Add*` calls; `tools/Agent42.DevHost/Program.cs`
repeats ~35 of them, and 12 test `ISiloConfigurator`s repeat the grain-side subset again. CLAUDE.md
cross-cutting rule 7 ("a new mandatory grain dependency must be registered in every silo host") exists
*because* these drift, and today it is enforced only by memory: nothing in the suite runs `Program.cs`, and a
host missing a grain dependency fails at first **activation**, not at boot. Separately, the guard that every
`AgentApi.Map*Endpoints` partial is called from `MapAgentApi` is a hand-maintained list that has already
fallen four parts behind.

Goal: one shared `AddAgent42Silo()` that (a) owns every registration whose implementation is identical across
hosts and (b) **fails fast at startup** naming any grain dependency the host did not supply — used by Backend,
DevHost and every test silo — plus a self-maintaining runtime test that every `Map*Endpoints` part is
reachable from `MapAgentApi`.

## Affected areas

Verified against the tree at 49f5153.

- `src/Agent42.Backend/Program.cs` — invariant registrations removed, `AddAgent42Silo()` / `AddAgent42Api()` called.
- `tools/Agent42.DevHost/Program.cs` — same.
- **New:** `src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs` (beside `DashboardRegistration.cs`), and
  `src/Agent42.Backend/Api/Agent42ApiRegistration.cs` for the endpoint-side half.
- Test silo configurators (all 12 files implementing `ISiloConfigurator` in `tests/Agent42.Tests/`):
  `TestSiloConfigurator.cs`, `NoModelSiloConfigurator.cs`, `UnconfiguredMemorySiloConfigurator.cs`,
  `WebDocumentProbeSilo.cs`, `CapSizedWebPageFetcher.cs`, `ActivityPropagationTests.cs`, `AgentGrainTests.cs`,
  `AttachmentRehydrateTests.cs`, `EditRegenerateForkTests.cs`, `FakeGeocoder.cs`, `LiveModelChatTests.cs`,
  `LiveCopilotTests.cs` (re-grep `ISiloConfigurator` at execution time — this list is a snapshot).
- `tests/Agent42.Tests/TestServiceRegistration.cs` (`AddDocumentRagFakes` overlaps the shared doc pipeline).
- `tests/Agent42.Tests/SharedApiFixture.cs` (`AddRouteMatcherServices`, lines ~104-129).
- `tests/Agent42.Tests/ProjectLayoutTests.cs` (`AgentApiParts()` ~168-190, `Hosts_*` source-text tests ~558-640).
- **New tests:** `tests/Agent42.Tests/SiloRegistrationTests.cs`, `tests/Agent42.Tests/AgentApiReachabilityTests.cs`.
- Docs: `CLAUDE.md` rule 7, `.agentic/gotchas.md` (entries at ~1786 and ~2825 plus a new exemplar row), a new ADR.

## Context & constraints

**Investigation findings (each with evidence):**

1. **The hosts share service types, not implementations.** Backend registers `BlobAttachmentStore`,
   `SearxngWebSearch`, `WebPushNotificationSender`, `HttpAgentWebhookSender`, …; DevHost registers
   `InMemoryAttachmentStore`, `InMemoryWebSearch`, `NullPushNotificationSender`, `NullAgentWebhookSender`, …
   (compare the two `Program.cs` files). Test configurators register *recording* fakes that tests read back
   (`TestSiloConfigurator.cs`: `RecordingPushNotificationSender`, `RecordingHindsightClient`, …). So a shared
   method **cannot pick implementations** — it can only own the invariant ones and verify the varying ones.
2. **The mandatory grain dependencies can be read from the grains by reflection.** Every grain lives in
   `Agent42.Agents` (grep `: Grain` under `src/` → only `src/Agent42.Agents/**`). Only two grains inject services:
   `AgentGrain` (`src/Agent42.Agents/Conversations/AgentGrain.cs` ctor ~301-318: 13 mandatory services plus
   optional `TimeProvider?` / `ClientToolTimeouts?`) and `AgentWebhookGrain`
   (`src/Agent42.Agents/Webhooks/AgentWebhookGrain.cs` ~36-39: `IAgentWebhookSender`). The other 14 take only
   `IPersistentState<…>`.
3. **The reachability guard is hand-maintained and stale.** `ProjectLayoutTests.AgentApiParts()` lists 20 parts;
   `MapAgentApi` (`src/Agent42.Backend/Api/AgentApi.cs:33-58`) calls 24. Missing from the theory: `Artifacts`,
   `Export`, `Webhooks`, `Fleet`. The check is also source text (`"{mapMethod}(app);"`), so a commented-out
   call passes. No part uses `MapGroup`, so each part's routes can be enumerated by mapping it alone.
4. **The MCP connection cluster stays host-side.** `AddLocalBridge()` decorates the *last* `IMcpConnectionFactory`
   and throws if there is none (`src/Agent42.Backend/Mcp/LocalBridgeRegistration.cs`). **No test silo registers
   one** (grep `IMcpConnectionFactory` under `tests/`: only API hosts and unit tests). It is also not a grain
   dependency, because `AgentGrain` takes `IUserMcpToolProvider`. So `IMcpConnectionFactory`, `AddLocalBridge()`,
   `McpOAuthTokenResolver`, `McpConnectionTester` and `AddAgentMcpServer()` all **stay in each host's
   `Program.cs`**, untouched. `Hosts_RegisterLocalBridge` (`ProjectLayoutTests.cs` ~590-612) and
   `Hosts_GateTheAgentMcpSurfaceBeforeServingIt` (~559-580) therefore keep passing unchanged.
   `AddDataProtection()` also stays host-side, because Backend chains `PersistKeysToAzureBlobStorage` onto the
   builder it returns (`Program.cs:158-165`).
4a. **Layered test configurators.** `ActivityPropagationSiloConfigurator` (`ActivityPropagationTests.cs` ~131) and
   `UnconfiguredMemorySiloConfigurator` (~30) call `new TestSiloConfigurator().Configure(...)`.
   `LiveCopilotSiloConfigurator` (`LiveCopilotTests.cs` ~44) calls `new LiveModelSiloConfigurator().Configure(...)`.
   Only **leaf** configurators call `AddAgent42Silo()`. A layered one gets it through its inner configurator, and
   its own later registrations only *replace* ports and never remove them, so the inner validation still holds.
   The method is idempotent anyway, through a marker singleton.
4b. **Validation is shallow, by design.** It checks the grain constructors' direct parameters only. A transitive
   dependency, such as `UserSkillProvider`→`ISkillPackageStore` or `DocumentIngestionService`→the user's embedding
   generator, still fails at first resolve, not at boot. The ADR and rule 7 must say "direct grain dependencies
   are validated at startup", not "fails fast" without qualification.
5. **Source-text tests read `Program.cs` and must keep passing:** `ProjectLayoutTests` (`Program_*`, `Hosts_*`),
   `LiveTelemetryCaptureWiringTests`, `HttpAgentWebhookSenderTests` (~185), `WebDocumentToolTests` (~388, the
   guarded-fetcher wiring). Named `HttpClient`s with guards or `RemoveAllResilienceHandlers` (rule 8) are
   **host-varying** and stay in `Program.cs`. Do not move them to make `Program.cs` shorter.
6. **Test silo and test API host are separate DI containers**, unlike prod and DevHost where one container serves
   both (`SharedApiFixture.cs` builds a `WebApplication` beside the `TestCluster`). Hence two methods:
   grain-side `AddAgent42Silo()` and endpoint-side `AddAgent42Api()`. Prod and DevHost call both, test silos call
   the first, and test API hosts (`AddRouteMatcherServices`) call the second.
7. **Coverage benefit:** registrations in `Program.cs` score 0% on new-code coverage (gotchas.md ~2825). Logic
   moved into the registration classes becomes covered by every test cluster start.

**Design decisions (made at plan time; the user approves them with the plan):**

- **D1: Membership rule.** A registration moves into a shared method **iff its implementation is identical in
  Backend and DevHost, AND it is resolvable in every container that calls that method.** That gives two halves:
  - **Silo half (`AddAgent42Silo`):** `IUserSkillProvider→UserSkillProvider`, `IDocumentTextExtractor→DocumentTextExtractor`,
    `DocumentChunker`, `DocumentIngestionService`, `IDocumentIngestor→WebDocumentIngestor`. Then the D2 validation.
  - **API half (`AddAgent42Api`):** `IProviderSecretProtector→ProviderSecretProtector`,
    `IMcpSecretProtector→McpSecretProtector`, `McpOAuthClient(new HttpClient())`. These are the route-matcher
    invariants `SharedApiFixture.AddRouteMatcherServices` duplicates. The test fixture's instances use
    `EphemeralDataProtectionProvider` and are registered first, so `TryAdd` keeps them.
  - **Explicitly not shared** (Context 4): the MCP connection cluster, `AddLocalBridge`, `AddAgentMcpServer`,
    `AddDataProtection`, `IWebhookSecretProtector` (Backend gets it inside `AddAgentWebhooks`), and every port.
  The executor confirms each line against both `Program.cs` files before moving it. If one turns out to differ,
  it stays as a port. **Honest scope note:** the shared methods remove only about 10 duplicated lines. The value
  of this plan is the validator and the reachability test, not a shorter `Program.cs`.
- **D2: Fail fast on missing ports, derived by reflection.** `AddAgent42Silo()` ends by computing the required
  set: every non-optional constructor parameter of every concrete `Grain` subclass in the `Agent42.Agents`
  assembly, excluding `IPersistentState<>`, `ILogger<>` and `ILoggerFactory`. It throws
  `InvalidOperationException` listing **every** missing type (not just the first) and the grain that needs it.
  An Orleans-injected parameter attribute (`[PersistentState]`) is excluded by type, not by name.
- **D3: Shared invariants use `TryAdd*`.** A test host that registered its own instance first keeps it.
  Production behaviour is unchanged because Backend registers none of these itself after the move.
- **Rejected: a compile-time `required`-member ports record** (`new Agent42SiloPorts { AttachmentStore = …, … }`).
  It is stronger, because a missing port fails to compile, but the 12 test configurators register concrete
  recording types they resolve back. Every port would need a `Func<IServiceProvider, T>`, which is churn
  across every configurator and a second source of truth beside the grain constructors. Reflection keeps the
  constructor as the one source.
- **Rejected: `TryAdd` in-memory or `Null*` defaults for ports.** A prod host that forgot a port would boot
  silently onto a no-op implementation, for example a null push sender. That is strictly worse than the
  activation failure it replaces.
- **Rejected: run `Program.cs` under `WebApplicationFactory`.** `Microsoft.AspNetCore.Mvc.Testing` is not
  referenced. Boot needs Cosmos, Blob and `WebSearch:Endpoint` (it throws when that endpoint is missing), and
  Orleans Cosmos storage. Validating at startup plus a source-text `Hosts_CallAddAgent42Silo` guard gets the
  same safety without that.

**Governing docs:** ADR 0039/0040 (flat namespace, feature folders, an uncalled Map group "silently deletes",
0040 ~101-105), ADR 0050/0051 (S3776 ≤ 15, split registration into its own folder), ADR 0062 (dashboard,
never the community package), ADR 0070 (`MapAgentMcpServer` deliberately **not** in `MapAgentApi`, so it stays
outside the reachability test's scope), ADR 0076 (local bridge order). Gotchas: route-matcher deps break the
whole host (~1786), and `Program.cs` is untested (~2825). Read both before Step 2.

## Steps

### Step 1: Runtime `Map*` reachability test (independent; can run in parallel with Step 2)
- **New** `tests/Agent42.Tests/AgentApiReachabilityTests.cs`:
  - Reflect `typeof(AgentApi)` for `private static void Map*Endpoints(IEndpointRouteBuilder)`. Assert ≥ 24 are
    found, so a rename that defeats the reflection cannot pass vacuously.
  - For each method: build a slim `WebApplication` with `SharedApiFixture.AddRouteMatcherServices` plus an
    `IGrainFactory` stub, invoke **only that method** via reflection, and collect `(HttpMethods, RoutePattern.RawText)`
    from `EndpointDataSource`. Assert the set is non-empty.
  - Build one more app, call `MapAgentApi()`, and collect the same. Assert each part's set is a **subset**, and
    report the part name and missing routes on failure.
  - Mutation check (manual, recorded in the MR): comment out `MapFleetEndpoints(app);` and confirm the test
    fails naming `MapFleetEndpoints`.
  - Host setup: slim builder + `UseTestServer` + `IGrainFactory` stub + `SharedApiFixture.AddRouteMatcherServices`
    (which already registers `PushConfig`; `EmailConfig` and the Fleet services are resolved through
    `GetService`, so they need nothing). Enumerate `EndpointDataSource.Endpoints` to force endpoint building,
    and wrap each part's mapping and building in a try/catch. The failure message must name the part. An
    opaque RequestDelegateFactory error is not acceptable.
- `ProjectLayoutTests.AgentApiParts()`: add the four missing rows exactly:
  `{ "Artifacts/AgentApi.Artifacts.cs", "MapArtifactEndpoints" }`, `{ "Export/AgentApi.Export.cs", "MapExportEndpoints" }`,
  `{ "Webhooks/AgentApi.Webhooks.cs", "MapWebhookEndpoints" }`, `{ "Fleet/AgentApi.Fleet.cs", "MapFleetEndpoints" }`.
  Verify the paths exist before adding them. Add a
  test that the theory's method set **equals** the reflected set, so the folder and namespace theory cannot fall
  behind again. Drop the source-text `"{mapMethod}(app);"` assertion, which the runtime test supersedes.
- **Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentApiReachability|FullyQualifiedName~ProjectLayoutTests"`

### Step 2: `AddAgent42Silo()` with port validation (no host changes yet)
- **New** `src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs`, namespace `Agent42.Backend` (flat, per rule 1).
  Shape: copy `src/Agent42.Backend/Memory/HindsightRegistration.cs`, the registration-class precedent named
  in CLAUDE.md rule 9.
  - `public static IServiceCollection AddAgent42Silo(this IServiceCollection services)`: return early if its
    marker singleton is already present (idempotent, Context 4a), `TryAdd` the silo-half invariants from D1,
    then validate (D2). It does **not** call `AddLocalBridge` (Context 4).
  - Optional parameters are detected with `ParameterInfo.HasDefaultValue`. Nullable annotation is not used.
  - `internal static IReadOnlyList<(Type Service, Type Grain)> RequiredGrainServices()` is the reflection, cached.
    Keep each method ≤ 15 cognitive complexity.
- **New** `tests/Agent42.Tests/SiloRegistrationTests.cs`:
  - an empty `ServiceCollection` with only an `IMcpConnectionFactory` throws, and the message names
    `IAttachmentStore`, `IPushNotificationSender` and `IAgentWebhookSender`, each with its grain;
  - `RequiredGrainServices()` contains `HindsightIntegration` and `IUserChatClientFactoryProvider`, and does
    **not** contain `TimeProvider`, `ClientToolTimeouts`, `IPersistentState<>` or `ILogger<>`;
  - a collection with every port registered does not throw, and a pre-registered invariant (for example a
    custom `IProviderSecretProtector`) survives (D3);
  - calling it twice registers each invariant once (the marker makes it idempotent).
- **Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~SiloRegistrationTests`

### Step 3: Test silos call `AddAgent42Silo()` (after Step 2)
- Each **leaf** `ISiloConfigurator` (Context 4a) calls `AddAgent42Silo()` **last** inside `ConfigureServices`.
  Layered ones (`ActivityPropagationSiloConfigurator`, `UnconfiguredMemorySiloConfigurator`,
  `LiveCopilotSiloConfigurator`, and any others the executor finds by grepping for `.Configure(` inside a
  `Configure`) do not. Any leaf that fails validation lacks a real grain dependency. That gap was latent before,
  because only activation would hit it. Add the missing fake, and do not weaken the validator.
- `TestServiceRegistration.AddDocumentRagFakes` stays **unchanged** in this plan. It is called from many API-only
  hosts (`AgentApiTests` ~1192, `ChatStreamDisconnectTests` ~75, `ConfigHealthApiTests` ~50, `DiagnosticsApiTests`
  ~73, `DocumentUploadProviderErrorTests` ~74, …) that never call `AddAgent42Silo`. Its plain `AddSingleton`s
  come first in silo containers, so the shared method's `TryAdd` leaves them in place. Shrinking it is not
  worth the coupling.
- **Verify:** full `dotnet test Agent42.slnx`. Every cluster start now runs the validator.

### Step 4: `AddAgent42Api()` for the endpoint-side invariants (after Step 2)
- **New** `src/Agent42.Backend/Api/Agent42ApiRegistration.cs`: `TryAdd` the API-half invariants from D1, and
  nothing else.
- `SharedApiFixture.AddRouteMatcherServices` calls it in place of its duplicated lines. Keep the test-specific
  values (`StaticModelCatalog`, `PushConfig`) in the fixture.
- **Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~Api"` and then the full suite.

### Step 5: Hosts adopt both methods (after Steps 3 and 4)
- `Program.cs` (Backend) and DevHost `Program.cs`: delete the moved lines and call `AddAgent42Api()` and then
  `AddAgent42Silo()` **after** every port registration and before `UseOrleans`. Keep each moved comment that
  explains a non-obvious *why* by moving it with its line into the registration class. Host-varying
  registrations keep their comments in place.
- `ProjectLayoutTests`: add `Hosts_CallAddAgent42Silo`, a theory over both hosts. It asserts that
  `AddAgent42Api()` and `AddAgent42Silo()` are present, and that `AddAgent42Silo()` comes **after** each
  host's `IPushNotificationSender` and `IAttachmentStore` registrations, which stand in for "after the ports".
  `Hosts_RegisterLocalBridge` and `Hosts_GateTheAgentMcpSurfaceBeforeServingIt` are **not** modified, because
  their anchors stay in `Program.cs` (Context 4). Before editing, re-grep every
  `ReadAllText`/`StrippedSource` reader of `Program.cs` and confirm that no anchor line is being moved.
- Smoke: `dotnet run --project tools/Agent42.DevHost` boots, a chat turn streams, and an image-model turn
  produces the canned PNG. Then temporarily delete DevHost's `IGeocoder` line and confirm boot fails with the
  validator's message. Record this in the MR.
- **Verify:** full `dotnet test Agent42.slnx`, `dotnet build Agent42.slnx`, and `dotnet format Agent42.slnx --verify-no-changes`.

### Step 6: Decision + docs (after Step 5)
- `/decide`: write a new ADR. Take the next free number at execution time; 0104 is the latest at plan time,
  and ADR numbering is guarded (#200). It records D1-D3 and the three rejected alternatives. It amends
  ADR 0040, because reachability is now runtime-checked.
- `CLAUDE.md` rule 7: replace "must be registered in every silo host" with the mechanism: **direct** grain
  constructor dependencies are validated at startup by `AddAgent42Silo()`, and transitive ones are not (Context 4b), a host-invariant registration goes in the shared method, and
  a host-varying one is a port in each host. Keep the `GetService` endpoint-dependency advice and the
  "`AgentApi.*` partial that `MapAgentApi` never calls" warning, now pointing to the runtime test.
- `.agentic/gotchas.md`: update the route-matcher entry (~1786) and the `Program.cs`-untested entry (~2825).
  Add an exemplar-table row for "host registration (shared invariants + ports)" →
  `src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs`. Run `/sync-docs`.
- **Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~Doc|FullyQualifiedName~QualityGate|FullyQualifiedName~Adr"` (doc-path integrity + ADR numbering guards), then the full suite.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Every `Map*Endpoints` part contributes ≥ 1 route, and all of them are mapped by `MapAgentApi` (runtime) | `EveryEndpointPart_IsReachableFromMapAgentApi` | `AgentApiReachabilityTests.cs` |
| AC2 | The reflected part count is ≥ 24, so the reflection cannot pass vacuously | `ReflectsAllEndpointParts` | `AgentApiReachabilityTests.cs` |
| AC3 | The folder and namespace theory lists exactly the reflected parts | `AgentApiParts_MatchesReflectedMapMethods` | `ProjectLayoutTests.cs` |
| AC4 | A missing grain dependency fails at registration, naming every missing type and its grain | `AddAgent42Silo_MissingPorts_ThrowsListingAll` | `SiloRegistrationTests.cs` |
| AC5 | The required set comes from the grain constructors, and optional, state and logger parameters are excluded | `RequiredGrainServices_ReflectsCtors_ExcludesOptionalAndFramework` | `SiloRegistrationTests.cs` |
| AC6 | A host's own registration of a shared invariant wins (D3) | `AddAgent42Silo_KeepsPreRegisteredInvariant` | `SiloRegistrationTests.cs` |
| AC7 | Every test silo goes through the shared method and passes validation | the existing suite (every `TestCluster` start runs the validator) + `AllSiloConfigurators_CallAddAgent42Silo` (source text over `ISiloConfigurator` files, with an explicit exemption list for layered configurators that must each contain a `new <Inner>SiloConfigurator().Configure(` call) | `ProjectLayoutTests.cs` |
| AC8 | Both hosts call `AddAgent42Api()` and then `AddAgent42Silo()` after their ports, and the local bridge and MCP surface guards are unchanged and green | `Hosts_CallAddAgent42Silo`, plus the unchanged `Hosts_RegisterLocalBridge` and `Hosts_GateTheAgentMcpSurfaceBeforeServingIt` | `ProjectLayoutTests.cs` |
| AC8a | `AddAgent42Api()` keeps a pre-registered ephemeral protector, and supplies the invariants when it is absent | `AddAgent42Api_TryAddsRouteMatcherInvariants` | `SiloRegistrationTests.cs` |
| AC9 | Existing `Program.cs` wiring guards stay green (guarded fetcher, webhook client, telemetry order, forwarded headers) | the existing `WebDocumentToolTests`, `HttpAgentWebhookSenderTests`, `LiveTelemetryCaptureWiringTests`, `ProjectLayoutTests.Program_*` | (existing) |
| AC10 | DevHost boots and chats, and a removed port fails boot with the validator's message | manual smoke, recorded in the MR. Out of unit scope, because nothing runs a host `Program.cs` | n/a |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|------|-----------|-------------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build (`dotnet build Agent42.slnx`, error) |
| `vulnerable_packages` | 0 | CI package-freshness job |
| Build / tests / format | must pass | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` (CI hard block) |

Lines removed from `Program.cs` do not count against coverage. The new registration classes run on every
cluster start, so they should be covered. If a branch in them is not, cover it. Never estimate an "Actual".

## Out of scope

- Moving **host-varying** registrations, such as guarded named `HttpClient`s, Cosmos and Blob wiring, push and
  VAPID, and geocoding, out of `Program.cs`. They are ports, and rule 8 and the SSRF guards keep them visible
  where the source-text tests look.
- Sharing the `UseOrleans` silo-builder half (storage, reminders, `AddActivityPropagation`, `AddDashboard`).
  It differs per host by design (Cosmos vs memory).
- Running `Program.cs` under test (`WebApplicationFactory`). That was rejected above.
- A validator for **route-matcher** (endpoint parameter) dependencies. Deriving it from handler signatures is a
  separate problem. `AddAgent42Api()` reduces the drift, and the existing gotcha stays.
- `MapAgentMcpServer`, `MapOrleansDashboardUi` and `MapDefaultEndpoints`. They are deliberately outside
  `MapAgentApi` (ADR 0070/0062) and keep their own `Hosts_*` guards.

## Review

Critic pass (pre-approval) found one blocker, three high and three medium findings. **All were accepted and
fixed in this spec:**

- **B1** `AddLocalBridge` inside the shared method would throw in every test silo, because none registers an
  `IMcpConnectionFactory`. **Fixed:** the whole MCP connection cluster stays host-side (Context 4, D1).
- **H1** Layered configurators would double-call the method or validate a partial collection. **Fixed:** only
  leaves call it, the method is idempotent through a marker, and AC7 has an exemption list (Context 4a).
- **H2** Several D1 candidates were not identical (`AddDataProtection`) or not grain-side (MCP tester, resolver,
  OAuth client, `AddAgentMcpServer`), and transitive dependencies are not validated. **Fixed:** D1 is split into
  silo and API halves with an explicit "not shared" list, and the validation is labelled shallow (Context 4b).
- **H3** Step 1 was under-specified. **Fixed:** the host setup is spelled out, and each part's failure must name
  the part.
- **M1** `Hosts_GateTheAgentMcpSurfaceBeforeServingIt` would have broken. **Moot:** `AddAgentMcpServer` no
  longer moves. A re-grep before Step 5 was added.
- **M2** The four missing theory rows are now spelled out.
- **M3** `AddDocumentRagFakes` stays unchanged (Step 3).

Gate thresholds were checked against `.agentic/quality-gates.yaml` at plan time (80 / 15). No findings were rejected.

## Implementation notes (where the build diverged from the plan)

Executed 2026-10-19 on `feature/shared-silo-registration`. HEAD had moved to `5d89a50` (#202, legal pages); that
diff touched none of the affected areas.

- **ADR number:** 0105.
- **`RequiredGrainServices()` is `public`, not `internal`.** The repo has no `InternalsVisibleTo`
  (the `CosmosDocumentIndex` precedent), so the test project could not read it otherwise.
- **The source-text theory was renamed** `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` →
  `AgentApiPart_ExistsInItsFeatureFolder`, since it no longer checks registration. Live references (two in
  `ProjectLayoutTests`, one in `AgentMcpServerRegistration`) were repointed to `AgentApiReachabilityTests`, and
  ADR 0040 carries an amendment note. Completed specs and older ADR prose keep the old name as history.
- **The reachability test's `IGrainFactory` stub** is the existing `FakeProfileGrainFactory`; no new stub was added.
- **`SharedApiFixture.AddRouteMatcherServices`** keeps its ephemeral `IProviderSecretProtector` / `IMcpSecretProtector`
  instances (registered first, so `TryAdd` keeps them, which is what AC8a tests); only its `McpOAuthClient` line was
  replaced by `AddAgent42Api()`. Replacing the protectors too would have needed `AddDataProtection()` in the test hosts,
  whose default key ring on Windows persists to the user profile.
- **`McpOAuthClient` is registered through a factory** (`TryAddSingleton(_ => new McpOAuthClient(new HttpClient()))`)
  instead of an instance, so `TryAdd` does not construct an `HttpClient` that is then discarded.
- **Extra tests beyond the AC table:** `AddAgent42Silo_OneMissingPort_NamesOnlyThatOne`, and an equality check in
  AC5's test that pins the reflected required set to the test's explicit port list plus the two shared invariants.
- **Smoke (AC10), done:** DevHost booted, `POST /api/chat` streamed the canned reply, and the image model returned the
  canned PNG. With DevHost's `IGeocoder` line commented out, boot failed with
  `InvalidOperationException: This silo host does not register every grain dependency. Register each of these before
  calling AddAgent42Silo(): IGeocoder (needed by AgentGrain).`
- **Mutation (Step 1), done:** with `MapFleetEndpoints(app);` commented out, `EveryEndpointPart_IsReachableFromMapAgentApi`
  failed with "MapFleetEndpoints is not reachable from MapAgentApi; missing: GET /api/fleet/config, …".
- **No latent gaps were found:** all seven leaf test silos passed validation on the first full run, so none was missing
  a direct grain dependency.
