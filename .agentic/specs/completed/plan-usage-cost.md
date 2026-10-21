planned-against: main @ 2f19fcf

# Plan: estimated cost per turn and on /usage

## Overview
Show an **estimated USD cost** next to the per-turn token line and on the `/usage` dashboard. Tokens are already
measured on both surfaces (ADR 0031, ADR 0104); nothing knows what a model costs, and nothing on the usage path knows
which *kind* of provider served a call. The design follows the sibling `Expecho/GenAI.Telemetry.Dashboard` ADR 0012
(cost derived, never stored; an effective-dated price table; "unpriced" is not "$0"), adapted to Agent42's four
provider kinds. User asked for it 2026-10-21, with implementation, PR and merge in the same session.

## Affected areas (verified against the tree at 2f19fcf)
- `src/Agent42.Agents/Telemetry/` — new `ModelPricing.cs` (price table + `CostFormula`); `UsageEvent.cs`,
  `UsageRollupState.cs` (`TokenBucket`), `UsageContracts.cs`, `UsageSpanProjector.cs`.
- `src/Agent42.Agents/Models/IChatClientFactory.cs` — a default-implemented `GetProviderKind(modelId)`.
- `src/Agent42.Agents/Conversations/AgentGrain.cs` (`TurnTokenTotals.ToDoneEvent`, where `Done` is built),
  `AgentStreamEvent.cs` (trailing fields).
- `src/Agent42.Backend/Models/ChatClientFactory.cs` (`Build`, `GetProviderKind`), new
  `src/Agent42.Backend/Models/ProviderKindTagChatClient.cs`.
- `src/Agent42.Backend/wwwroot/js/usage.js`, `thread.js` (`showUsage`), `index.html` (cost tile/columns),
  `styles.css` if needed, `sw.js` (`CACHE` bump — cross-cutting rule 3).
- Docs: new ADR `.agentic/decisions/0110-estimated-cost-from-a-checked-in-price-table.md`, banner on ADR 0031,
  gotcha entry, CLAUDE.md feature map row (Telemetry), `docs/features/` usage page if it names the columns.
- Tests: `tests/Agent42.Tests/` — new `ModelPricingTests.cs`; extend `UsageSpanProjectorTests.cs`,
  `UsageRollupTests.cs`, `GrainStorageSerializerTests.cs`, `UsageAssetsTests.cs`, a provider-kind tag test.

## Context & constraints
- **Anthropic input includes BOTH cache reads and cache writes.** Spike 2026-10-21 (Anthropic 12.58.0,
  stubbed `HttpClient`, wire usage `input 10 / cache_creation 50 / cache_read 100 / output 5`) → M.E.AI
  `InputTokenCount = 160`, `CachedInputTokenCount = 100`, `AdditionalCounts["CacheCreationInputTokens"] = 50`.
  So `uncached = input − cached − cacheWrite`. For Azure/OpenAI, cached is a subset of input and reasoning a subset
  of output (sibling ADR 0012, verified there) — **no reasoning term**.
- **Formula** (USD per 1M tokens, never rounded before summing):
  `max(0, input − cached − cacheWrite)·in + cached·(cachedIn ?? in) + cacheWrite·(cacheWrite ?? in) + output·out`.
  A missing cached/cache-write rate bills at the input rate (not free). Output tokens with no output rate → unpriced.
- **Three outcomes, never conflated:** `Priced(usd)`, `Included` (provider kind `GitHubCopilot` — subscription, no
  per-token bill), `Unpriced` (no table row). Unpriced renders `—`, never `$0`. OpenAICompatible (GitHub Models,
  Ollama, vLLM) has **no rows** → unpriced: it can be free, local, or anything; guessing Azure rates is wrong.
- **`gen_ai.provider.name` does not discriminate Azure from OpenAI-compatible** (both `"openai"`, sibling ADR 0012).
  So the provider kind rides its own span tag, `agent42.provider.kind`, set by a `DelegatingChatClient` placed
  **inside** `UseOpenTelemetry` in `MultiProviderChatClientFactory.Build`, writing to `Activity.Current` (the chat
  span). Not RequestContext: the factory, not the grain, knows the kind. A test must prove the tag lands on the
  `chat` span on both the streaming and non-streaming paths.
- **Model matching is exact after normalization**, not prefix (sibling ADR 0012's `gpt-4o`→`0806` near-miss and
  `5.4` vs `5.4 pro` warnings): lower-case, strip a trailing `-YYYY-MM-DD` or `-YYYYMMDD` date suffix. `gpt-5.4-mini`
  must NOT price as `gpt-5.4`. Span model is `response_model ?? request_model`; an Azure deployment name that is not
  a model name is simply unpriced.
- **Effective dating:** each row has `ValidFrom`; lookup takes the latest row with `ValidFrom ≤ the call's time`
  (bucket start for `/usage`, now for a live turn). A reprice adds a row; old rows stay.
- **Prices seeded 2026-10-21:** Azure Retail Prices API (`serviceName eq 'Foundry Models'`, eastus2, Consumption,
  **Global** meters, normalized to per-1M — older meters are per-1K) and the Claude API pricing reference (Anthropic
  first-party; cache write 5-min = 1.25× input; cache read per model, e.g. Opus 5.5 $0.20, Fable 5.1 $0.25,
  others 0.1× input). Global tier only, one profile — Data Zone/regional deployments price ~10% differently and are
  an accepted, documented inaccuracy ("estimated"). Haiku 5.5's >100K-prompt tier is ignored (documented).
- **Storage:** `TokenBucket` gets trailing `[Id(11)] ProviderKind = ""`, and the bucket key gains the kind. Old
  buckets have `""` → unpriced (they predate the tag) — correct and honest, no migration (cross-cutting rule 2).
  Cost is computed in `UsageRollupState.Query`, **never stored** in grain state.
- **Wire contract:** ADR 0031 says "NO cost field anywhere" — the new ADR amends that. `ModelUsage` gains
  `ProviderKind` (and groups by it) + `CostUsd` (`decimal?`) + `CostStatus` (`"priced"|"included"|"unpriced"`);
  `UsageTotals` and `UserUsage` gain `CostUsd` (sum of priced) + `UnpricedCallCount`. All trailing `[Id]`s.
- **Per turn:** `AgentStreamEvent` gains trailing `[Id(20)] decimal? CostUsd`, `[Id(21)] string? CostStatus`.
  The grain resolves the kind through `IChatClientFactory.GetProviderKind(modelId)` (default `""` → unpriced, so
  `SingleChatClientFactory`/DevHost need no change) and prices the turn's totals in `ToDoneEvent`. The model is the
  bare model of the conversation's qualified id. Fleet/research/Copilot paths are out of scope.
- **Egress:** none. The table is a checked-in file; no price API is called at runtime. Not an ADR 0013 extension.
- Governing: ADR 0031 (amended), 0104, 0013/0018 (provider kinds), 0039 (flat namespace), gotchas "The usage
  dashboard counts tokens from `chat` spans ONLY" and "A GitHub model id contains a `/`".

## Steps
1. **Pricing core** (`ModelPricing.cs`, `ModelPricingTests.cs`): `ModelPrice` record, the seeded table, `Normalize`,
   `Resolve(kind, model, at)`, `CostFormula.Compute(...)` → `CostEstimate(Status, Usd)`. Verify:
   `dotnet test --filter ModelPricingTests`.
2. **Provider-kind tag** (`ProviderKindTagChatClient.cs`, `ChatClientFactory.cs` `Build` + `GetProviderKind`,
   `IChatClientFactory.cs`, `UsageSpanProjector.cs` `ProviderKindTag`, `UsageEvent` trailing `[Id(12)]`). Verify: a
   test with a real `UseOpenTelemetry` + `ActivityListener` shows the tag on the `chat` span, streaming and not.
3. **Rollup + contract** (`UsageRollupState.cs`, `UsageContracts.cs`). Verify: `UsageRollupTests` (cost per row,
   included, unpriced, legacy `""` bucket), `GrainStorageSerializerTests` (TokenBucket JSON round-trip without the new
   field), `UsageApiTests`.
4. **Per-turn** (`AgentStreamEvent.cs`, `AgentGrain.cs`). Verify: a unit test of the Done-event pricing.
5. **UI** (`usage.js`, `thread.js`, `index.html`, `sw.js`). Cost tile, a cost column on the model and user tables,
   `≈ $0.0123` / `included` on the turn line, `—` for unpriced. Verify: `UsageAssetsTests` + PWA cache bump tests.
6. **Docs**: ADR 0110, ADR 0031 banner, gotcha, CLAUDE.md map, feature doc. Verify: doc-path check
   (`node .agentic/scripts/check-doc-paths.mjs`).
7. Full `dotnet build`, `dotnet test Agent42.slnx`, `dotnet format --verify-no-changes`, coverage gate.

Steps 1→2→3→4 are sequential (shared types); 5 and 6 depend on 3/4.

## Acceptance criteria → tests
| AC | Test | File |
|----|------|------|
| Azure cached input billed at the cached rate, no reasoning term | `Compute_AzureCachedSubsetOfInput` | ModelPricingTests.cs |
| Anthropic cache read+write subtracted from input and billed at their rates | `Compute_AnthropicInputIncludesCacheReadAndWrite` | ModelPricingTests.cs |
| Missing cached rate bills at input rate | `Compute_NoCachedRate_BillsAtInputRate` | ModelPricingTests.cs |
| Copilot → included; OpenAICompatible/unknown → unpriced | `Resolve_CopilotIsIncluded`, `Resolve_UnknownIsUnpriced` | ModelPricingTests.cs |
| Dated suffix stripped; `gpt-5.4-mini` ≠ `gpt-5.4` | `Normalize_StripsDateSuffix`, `Resolve_NoPrefixMatch` | ModelPricingTests.cs |
| Effective dating picks the row in force | `Resolve_PicksRowInForceAtTime` | ModelPricingTests.cs |
| Provider kind tag lands on the chat span | `ProviderKindTag_IsOnChatSpan` (stream + non-stream) | ProviderKindTagTests.cs |
| Projector carries the kind | `ChatSpan_CarriesProviderKind` | UsageSpanProjectorTests.cs |
| /usage rows priced/included/unpriced; totals sum priced, count unpriced | `Query_ComputesCostPerModelRow` etc. | UsageRollupTests.cs |
| Old TokenBucket JSON (no kind) deserializes as `""` | `TokenBucket_WithoutProviderKind_Deserializes` | GrainStorageSerializerTests.cs |
| Done event carries cost | `DoneEvent_PricesTurn` | a grain/unit test |
| UI renders cost and `—` | asset assertions | UsageAssetsTests.cs |

## Quality gates (from `.agentic/quality-gates.yaml` at plan time)
| Gate | Threshold | Tool |
|------|-----------|------|
| coverage_new_code_pct_min | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| cognitive_complexity_max | 15 | SonarAnalyzer S3776 at build |
| vulnerable_packages | 0 | package-freshness job |

## Out of scope
- A live price refresh from the Azure Retail Prices API (the obvious follow-up; the table shape allows it).
- Pricing embedding, image, speech, fleet and Copilot-SDK-agent spend; Anthropic cache writes on `/usage` (no OTel tag).
- Regional / Data Zone / batch tiers; Haiku 5.5's long-prompt tier; currencies other than USD.
- Per-user price overrides.

## Review
Critic pass 2026-10-21: no ADR contradictions. Findings and handling:
1. *Done-event cost unreachable from grain tests* — fixed: `SingleChatClientFactory` gains an optional
   `pricingIdentity`; the test sits next to `Done_CarriesCacheWriteTokens` in `AgentGrainTests.cs`. The interface
   member is `GetPricingIdentity(modelId) → (kind, model)`, resolved ONCE at turn start (not a grain field read at Done).
2. *No-provider guard* — fixed: returns `("", "")` when `_providers.Count == 0`. The stale-id warning in `Resolve`
   fires at most once more per turn — accepted.
3. *Anthropic cache writes: /usage bills them at 1.0×, the turn line at 1.25×* — accepted and stated in ADR 0110
   and the dashboard note; a rollup test pins `CacheWrite = 0` on the /usage path.
4. *`UsageAssetsTests` pins `showUsage`'s destructuring* — updated in step 5; `send.js` checked, no change needed.
5. *Enum lives in Backend* — the kind strings are `ModelPricing.*Kind` constants; a test pins them equal to
   `ProviderKind.ToString()`.
6. *Tag could land on a parent span* — fixed: tagged only when the current span's `gen_ai.operation.name` is `chat`.
7. *Other bare Done builders* (`AgentGrain.cs` no-model/image paths, `AgentGrain.ImageTurn.cs`) — checked, no tokens,
   cost stays null; left alone.
- Cross-user cost visibility on /usage: stated in ADR 0110 (same exposure ADR 0031 accepted for tokens).
