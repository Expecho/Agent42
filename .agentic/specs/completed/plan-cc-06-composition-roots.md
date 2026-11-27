# Plan cc-06: shrink Program.cs and ChatClientFactory by feature

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md` (read its "Shared constraints" first)

## Overview

`src/Agent42.Backend/Program.cs` is 550 lines. The repo already has the right pattern (`AddHindsightMemory`,
`AddAgentWebhooks`, `AddInboundEmail`, `AddChatGateway`, `AddCopilotAgents`, `AddAgentFleet`, `AddAgentMcpServer`,
`AddLocalBridge`, `AddDocumentIndex`) but four inline blocks never got an `Add*`. `Models/ChatClientFactory.cs` (593 lines)
holds routing, pricing/capabilities, image/embedding/speech/transcription services and a nested per-kind `Provider` class.

## Affected areas (scout-verified at 02ebdf0; line numbers will drift: re-find by content)

`Program.cs` inline blocks to extract (each into its feature folder as `<Feature>Registration.cs`):
- `:95-150` model providers, catalog, user resolver, router (~55 lines) → `Models/ModelProvidersRegistration.cs`
- `:189-276` web search + document-fetch HttpClients, `IWebSearch`, `IWebDocumentFetcher` (EXTEXP0001 at `:215`, `:267`) → `Web/WebRegistration.cs`
- `:283-310` geocoding (EXTEXP0001 `:293`, `:300`) → `Location/GeocodingRegistration.cs`
- `:352-394` Web Push (EXTEXP0001 `:376`, `:382`) → `Push/WebPushRegistration.cs`

**Stays put (order-sensitive):** `:17-45` telemetry/auth prelude, `:406-427` `UseOrleans`, `:429-550` startup logs +
middleware pipeline (`UseForwardedHeaders` first `:493`; Entra `:500-505`; `UseOwnerGate` `:521`; `UseAgentMcpKeyGate`
`:528`; `UseLocalBridge` `:533`; map calls `:546-548`). `AddAgent42Api()`/`AddAgent42Silo()` (`:399-400`) must stay **after**
every port registration (`:396-398`): extracted groups go above them.

`ChatClientFactory.cs`: the nested `Provider` (`:293`) with kinds Foundry (`:396`), OpenAI-compatible (`:490`), Anthropic
(`:523`), GitHub Copilot (`:555`), decision model (`:562`); `ApiKeyHeaderPolicy` (`:578`); 3 pragma regions (4 directives)
at `:403/:406`, `:425-426…:452-453`, `:468/:472`.

## Tests that read these files as source text — update in the SAME step as the move

From `tests/Agent42.Tests/ProjectLayoutTests.cs`: `Program_TrustsForwardedProtoBeforeAnyOtherMiddleware` (`:487`),
`Program_EntraAuth_StamperAndGatePrecedeEveryReaderAndTheStaticFiles` (`:523`), `…_IsRegisteredAndMappedOnlyInEntraMode…`
(`:553`), `Program_GatesTheOrleansDashboardBeforeServingIt` (`:595`), `Hosts_GateTheAgentMcpSurfaceBeforeServingIt` (`:687`),
`Hosts_RegisterChatGateway` (`:724`), `Hosts_RegisterLocalBridge` (`:751`), `Hosts_CallAddAgent42Silo` (`:783`, needs literal
`services.AddAgent42Silo()` per host), `Hosts_RegisterTheOrleansDashboard` (`:810`), `Program_BuildsAndRegistersTheUserIdAliasMap`
(`:901`); `WebDocumentToolTests.cs:444 ProgramCs_WiresTheGuardIntoTheDocumentClientsPrimaryHandler` and `:462
ProgramCs_DocumentClientRemovesResilienceAndSetsATimeout` (they slice `Program.cs` from `AddHttpClient(GuardedDocumentFetcher`
to the next `builder.Services.` — **these break when the web block moves**); `HttpAgentWebhookSenderTests.cs:183`
(literal `builder.Services.AddAgentWebhooks();`); `TelemetryRedactionTests.cs:173` (literal
`builder.AddServiceDefaults(NonOwnerContentRedactor.Register)`); `LiveTelemetryCaptureWiringTests.cs:26` (literal
`builder.AddBackendTelemetry(`); `McpToolImageTests.cs:243 ToolImageChatClient_SitsBelowOpenTelemetry` (reads
`Models/ChatClientFactory.cs` `:245` **and `Agents/Models/IChatClientFactory.cs` `:251`**). `EveryNamedHttpClient_RemovesTheInheritedRetryPipeline`
scans all `src/`: safe. Docs: gotchas.md ~1149, 1186, 1216 cite `Provider.Foundry` in `ChatClientFactory.cs` — grep `CLAUDE.md .agentic`
for every moved symbol/path and fix references in the same MR (hard CI check `check-doc-paths.mjs`).

## Context & constraints

- Gotcha (`.agentic/gotchas.md`, "named HttpClient"): the document client's guard and resilience removal are security
  properties; the two `WebDocumentToolTests` must be **re-pointed at the new file and still assert the same guarantee**
  (guard on the primary handler; resilience removed; timeout set), not deleted. Prefer a helper that reads "the file that
  contains `AddHttpClient(GuardedDocumentFetcher`" over hard-coding a path, so the next move doesn't break it again.
- DevHost (`tools/Agent42.DevHost/Program.cs`) registers its own canned variants; do not make its registrations depend on
  Backend-only extension methods (ADR 0105: identical-in-every-host goes in `AddAgent42Silo/Api`; varying = a port).
- The `ChatClientFactory` split: move each provider kind's construction into its own file (`FoundryClientBuilder`,
  `AnthropicClientBuilder`, …) behind the existing nested `Provider` seam, **keeping the `#pragma` regions next to the
  experimental API they cover** (don't widen a pragma to a whole file). `ToolImageChatClient_SitsBelowOpenTelemetry` pins
  wrap *order*: the move must not change the order; re-point the test.
- Cognitive complexity must stay < 15 per method; an extraction must not create a 14-branch method by copying blocks.
- **New-code coverage is a CI hard block and will fail on this move (critic, verified).** `check-new-code-coverage.mjs` diffs
  against the merge base and counts moved lines as added; ADR 0105 says `Program.cs` "scores 0%" because no test runs it. The new
  `AddWebTools/AddGeocoding/AddWebPush/AddModelProviders` bodies run only in the Backend host (test silos and DevHost register their
  own variants), so they land at 0%. Constraint 7 forbids editing the gate and "explain it in the MR" does not unblock CI.
  **Fix: add registration tests in the `HindsightRegistrationTests` style** (build a `ServiceCollection`, call the extension with a
  test `IConfiguration`, resolve the services and assert the guard/handler/resilience wiring). These also replace the fragile
  source-text slice in `WebDocumentToolTests` with a real assertion.
- **The `WebDocumentToolTests` slice boundary will break silently.** `ClientRegistration()` slices `Program.cs` from
  `AddHttpClient(GuardedDocumentFetcher` to the next `builder.Services.`; inside an extension method that token is `services.`, so
  the slice would run to end-of-file and a neighbouring client (`SearxngProxy`) could satisfy the guard. A "read the file that
  contains it" helper does not fix the boundary: replace the slice with the resolved-service assertion above, or slice to the next
  `AddHttpClient(`.
- **Locals that outlive the block:** `models`/`modelOptions` from the model-provider block are used again at `Program.cs:~452`
  (`models.SkippedProviders`) and in the Backend-only startup log. `AddModelProviders` must return or expose them (e.g. return the
  options/holder), or the startup-log code must resolve them from the container. Decide in step 4.

## Steps

1. Extract the Web Push block (smallest, no pins) → `AddWebPush`. Verify build + `dotnet test --filter "Push"`.
2. Extract geocoding → `AddGeocoding`. Verify `Hosts_*` tests + location tests.
3. Extract web search/doc-fetch → `AddWebTools`; re-point the two `WebDocumentToolTests`. Verify them + `ProjectLayoutTests`.
4. Extract model providers → `AddModelProviders` (above `:399`). Verify full `ProjectLayoutTests`.
5. Split `ChatClientFactory` provider kinds into files; re-point `McpToolImageTests:243`. Verify Anthropic/Foundry/Copilot
   provider test classes.
6. Re-run `code-shape`: record `Program.cs` and `ChatClientFactory.cs` line counts before/after.

Steps 1–4 are independent of one another but all edit `Program.cs`: sequential.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Document client guard + resilience removal still pinned after the move | `ProgramCs_*`/renamed | `WebDocumentToolTests.cs` |
| Middleware order unchanged | the six `Program_*` tests above, unchanged | `ProjectLayoutTests.cs` |
| Both hosts still call shared registrations | `Hosts_*` tests, unchanged | `ProjectLayoutTests.cs` |
| Image wrapper still below OpenTelemetry | `ToolImageChatClient_SitsBelowOpenTelemetry` (re-pointed) | `McpToolImageTests.cs` |

## Quality gates

coverage_new_code_pct_min 80 — **moved code counts as added and Program.cs-bound code is at 0% today**: meet the gate with the
registration tests described above (not by explanation); cognitive_complexity_max 15; `dotnet format` clean.

## Out of scope

Reordering the middleware; changing what is registered; `FleetLessonService`/`ConfigHealth`/endpoint maps (plan 07).

## Review

Critic (fresh context): no blocking findings. One stale gotcha claim (`Program.cs` "no test will catch this") and one stale
source comment, both fixed in this MR. Other points (ADRs 0014/0031/0033 name `ChatClientFactory.cs`) are historical, left.

## Divergences from the plan

- **`ChatClientFactory` split uses partial classes, not `*ClientBuilder` types.** `MultiProviderChatClientFactory` and the
  nested `Provider` are `partial`; `Foundry` (+ `FoundryEmbeddingsEndpoint`, `ApiKeyHeaderPolicy`), `OpenAICompatible` and
  `Anthropic` each live in `ChatClientFactory.<Kind>.cs`. Every call site (`Provider.Foundry(...)`) is unchanged. `Build` stays in
  `ChatClientFactory.cs`, so `McpToolImageTests.ToolImageChatClient_SitsBelowOpenTelemetry` needed no re-pointing. GitHubCopilot and
  DecisionModel (one-liners) stay in the core file. `#pragma` regions stay around the same statements.
- **`AddModelProviders` returns the `ModelProviderSetup`** (decision for step 4): Program.cs keeps it for the skipped-provider log.
  It also owns the `ArmClient` registration and the Auto router registration.
- **Document-client pins are behavioural, not source slices.** `WebRegistrationTests` resolves the handler chain: no resilience
  handler (with a control client proving the probe sees one), per-client timeouts, loopback refused with `BlockedAddressException`,
  proxy handler flags. The two `ProgramCs_*` tests in `WebDocumentToolTests` were removed in favour of it.
- `ProjectLayoutTests.Hosts_CallAddAgent42Silo` gained a per-host push-sender token (`AddWebPush(` for Backend).
- Gotchas evidence paths re-pointed (path-only) and the "resilience assembled in Program.cs" claim reworded.

## Results

`Program.cs` 550 -> 329 lines; `ChatClientFactory.cs` 593 -> 394 (+ 231 in three kind files). Full suite: 4212 pass excluding
`Live*`; 5 `Live*` tests fail on credentials (HTTP 401 invalid subscription key), unrelated to this change. New-code coverage 94.2%
(194/206, `check-new-code-coverage.mjs main`). `dotnet format --verify-no-changes` clean; `check-doc-paths` passes.
