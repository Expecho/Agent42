---
id: 0105
date: 2026-10-19
status: accepted
tags: [orleans, registration, hosts, devhost, tests, layout, quality]
supersedes: []
extends: [0039, 0040, 0050, 0051]
amends: [0040]
---

# One shared silo registration that validates grain dependencies, and a runtime `Map*` reachability test

## Context
Three kinds of host build the same Orleans silo: `src/Agent42.Backend/Program.cs`, `tools/Agent42.DevHost/Program.cs`
and twelve test `ISiloConfigurator`s. CLAUDE.md rule 7 ("a new mandatory grain dependency must be registered in every
silo host") existed because they drift. It was enforced only by memory: nothing in the suite runs `Program.cs`, and a
host missing a grain dependency booted fine and failed at the grain's first **activation**.

Separately, ADR 0040 §4's guard that every `AgentApi.Map*Endpoints` part is called from `MapAgentApi` was a
hand-kept test list plus a source-text `"{method}(app);"` match. It had fallen four parts behind (`Artifacts`,
`Export`, `Webhooks`, `Fleet`), and a commented-out call would have passed it.

The hosts share service **types**, not implementations: Backend registers `BlobAttachmentStore`, DevHost
`InMemoryAttachmentStore`, a test silo a recording fake it reads back. Only two grains inject services
(`AgentGrain`, 13 mandatory; `AgentWebhookGrain`, one), and all grains live in `Agent42.Agents`.

## Decision
1. **`AddAgent42Silo()`** (`src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs`) is called by Backend, DevHost and
   every **leaf** test silo, **after** the host's own registrations. It:
   - `TryAdd`s the grain-side services whose implementation is identical in every host: `IUserSkillProvider`,
     `IDocumentTextExtractor`, `DocumentChunker`, `DocumentIngestionService`, `IDocumentIngestor`;
   - then **validates**: it reflects every non-optional constructor parameter of every concrete `Grain` in the
     Agents assembly (excluding `IPersistentState<>`, `ILogger<>`, `ILoggerFactory`, and any parameter with a
     default value), and throws `InvalidOperationException` listing **every** unregistered type with the grain that
     needs it;
   - is idempotent through a private marker singleton, so a layered test configurator that wraps a leaf is safe.
2. **`AddAgent42Api()`** (`src/Agent42.Backend/Api/Agent42ApiRegistration.cs`) `TryAdd`s the endpoint-side
   invariants: `IProviderSecretProtector`, `IMcpSecretProtector`, `McpOAuthClient`. Separate because a test's API
   host and its silo are separate containers. Production and DevHost call both (API first);
   `SharedApiFixture.AddRouteMatcherServices` calls this one, and its ephemeral protectors, registered first, win.
3. **Membership rule:** a registration moves into a shared method only if its implementation is identical in
   Backend and DevHost **and** it is resolvable in every container that calls that method. Everything else is a
   **port** each host registers itself: the MCP connection cluster and `AddLocalBridge()` (no test silo registers an
   `IMcpConnectionFactory`, and the bridge throws without one), `AddAgentMcpServer()`, `AddDataProtection()` (Backend
   chains `PersistKeysToAzureBlobStorage` onto it), every guarded or resilience-stripped named `HttpClient` (rule 8),
   and every grain dependency with a per-host implementation.
4. **Validation is shallow, by design.** Only a grain constructor's **direct** parameters are checked. A transitive
   dependency (`UserSkillProvider` → `ISkillPackageStore`, `DocumentIngestionService` → the user's embedding
   generator) still fails at first resolve.
   The check is also by `ServiceType` presence only: a port satisfied solely through a keyed, open-generic or
   `IEnumerable<>` registration would read as missing. No grain takes one today.
5. **`AgentApiReachabilityTests`** replaces the source-text call check (amends ADR 0040 §4). It reflects
   `AgentApi` for `private static void Map*Endpoints(IEndpointRouteBuilder)` (≥ 24, so a rename cannot pass
   vacuously), maps each part **alone** into a slim host, and requires every one of its routes to appear in a host
   that maps `MapAgentApi`. No part uses `MapGroup`, so a part mapped alone yields exactly its routes. The folder
   theory (`ProjectLayoutTests.AgentApiPart_ExistsInItsFeatureFolder`) stays, and
   `AgentApiParts_MatchesReflectedMapMethods` keeps its list equal to the reflected set.
   `MapAgentMcpServer`, `MapOrleansDashboardUi` and `MapDefaultEndpoints` are not `AgentApi` parts and keep their
   own `Hosts_*` guards (ADR 0062/0070).
6. **Source-text guards:** `ProjectLayoutTests.Hosts_CallAddAgent42Silo` (both hosts call API then silo, after their
   push sender and attachment store, before `UseOrleans`) and `AllSiloConfigurators_CallAddAgent42Silo` (every test
   silo is a leaf that calls it, or a listed layered one that calls its inner configurator).

## Alternatives rejected
- **A compile-time `required`-member ports record** (`new Agent42SiloPorts { AttachmentStore = …, … }`). A missing
  port would fail to compile, which is stronger. But the test configurators register concrete recording types they
  resolve back, so every port would need a `Func<IServiceProvider, T>`: churn across every configurator and a second
  source of truth beside the grain constructors. Reflection keeps the constructor as the only one.
- **`TryAdd` in-memory or `Null*` defaults for the ports.** A production host that forgot a port would boot silently
  onto a no-op, for example a null push sender. Strictly worse than the activation failure it replaces.
- **Run `Program.cs` under `WebApplicationFactory`.** `Microsoft.AspNetCore.Mvc.Testing` is not referenced, and boot
  needs Cosmos, Blob, `WebSearch:Endpoint` and Orleans Cosmos storage. Startup validation plus the source-text host
  guard gets the same safety for grain dependencies without it.

## Consequences
- A host that misses a direct grain dependency now fails at **startup**, naming the type and grain, in production,
  DevHost and every test cluster start, instead of at the first activation that needs it.
- The shared methods remove only about ten duplicated lines. The value is the validator and the reachability test,
  not a shorter `Program.cs`.
- Logic in the registration classes is covered by every test cluster start, unlike `Program.cs`, which scores 0% on
  the new-code coverage gate (ADR 0050).
- Route-matcher (endpoint **parameter**) dependencies are still not validated; deriving them from handler signatures
  is a separate problem. `AddAgent42Api()` reduces the drift, and the route-matcher gotcha stays.
- `TestServiceRegistration.AddDocumentRagFakes` still registers the RAG pipeline itself, because many API-only hosts
  call it without `AddAgent42Silo()`. In a silo container its plain `AddSingleton`s come first and the shared
  `TryAdd`s leave them in place.
