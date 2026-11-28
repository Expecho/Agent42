# Plan — Web search provider fallback chain

`planned-against: main @ 2714962`

## Overview

Make `web_search` resilient and less dependent on scraping Google/Bing from one datacenter IP. Add a
`FallbackWebSearch : IWebSearch` composite over an ordered list of providers — **Tavily, Firecrawl, Serper,
then the existing SearXNG sidecar** — that returns the first successful answer and benches a provider that
fails (credit exhausted, rate limited, bad key, timeout, 5xx). Keys are **operator-level, optional**
(the `GOOGLE_MAPS_API_KEY` / `HINDSIGHT_API_KEY` shape): no key means the provider is skipped, and with no
keys set the deployment behaves exactly as today.

Why now: a hosted API gives stable structured results, each vendor has a free tier, and the user wants to use
all three so that one running out of credits does not take search down. Only the SearXNG shim path is affected:
Responses-API models already use the hosted `web_search` tool (ADR 0015) and never reach `IWebSearch`.

**Part B (added 2026-11-28 at the user's request): Claude models use Anthropic's own hosted web search** instead
of the shim, with **automatic fallback** to the shim (and so to the chain) when Anthropic rejects the tool. Part A is
the chain (Steps 0–9); Part B is Steps B0–B5 below; Step 10 verifies both. The two parts are independent and can
ship as two MRs if the executor prefers; Part B does not need Part A's keys or code.

## Affected areas (verified against the tree @ 2714962)

| Path | Change |
|------|--------|
| `src/Agent42.Agents/Web/IWebSearch.cs` | Doc comment only: it claims "Backed by a self-hosted SearXNG instance … costs nothing". Becomes the chain. The interface does not change. |
| `src/Agent42.Backend/Web/` (new files) | `FallbackWebSearch`, `IWebSearchProvider`, `WebSearchProviderException` + failure kinds, the cooldown tracker, `TavilyWebSearch`, `FirecrawlWebSearch`, `SerperWebSearch`, `WebSearchChainRegistration` (own file, the `HindsightRegistration` shape — `Program.cs` is at the complexity ceiling, ADR 0050). |
| `src/Agent42.Backend/Web/WebRegistration.cs` | `AddWebTools` builds the chain instead of registering `SearxngWebSearch` directly as `IWebSearch`. `WebSearch:Endpoint` stays mandatory. |
| `src/Agent42.Backend/Web/SearxngWebSearch.cs` | Unchanged behaviour; becomes the last chain member. |
| `src/Agent42.Backend/Configuration/ConfigHealth.cs`, `ConfigHealthProbes.cs` | Per-provider key presence checks + one free (non-billable) chain-state probe. |
| **Part B:** `src/Agent42.Agents/Models/IChatClientFactory.cs` | `ModelCapabilities` gains a trailing optional `SupportsHostedWebSearch` (default false) and its doc says what it is: the model has a provider-side web search that the Backend swaps in at the client boundary. `SupportsHostedTools` is unchanged (it still means OpenAI's Responses hosted tools, ADR 0014/0015). |
| **Part B:** `src/Agent42.Backend/Models/ChatClientFactory.cs` (~116 derives `SupportsHostedTools`; ~127-134 `GetCapabilities` for configured models), `ChatClientFactory.Anthropic.cs` | Capability = Anthropic kind **and** `configured.SupportsTools`; the wrapper joins the Anthropic client composition. `Provider.Anthropic` is a **static factory** (also called directly by tests) with no config, DI or clock access, so it gains optional parameters. |
| **Part B:** `src/Agent42.Backend/Models/ModelProviderBuilder.cs` (~191), `ModelProvidersRegistration.cs` (builds `MultiProviderChatClientFactory`) | Carry the `WebSearch:AnthropicHosted` kill switch from configuration down to `Provider.Anthropic` as an optional parameter (default true), so existing direct callers compile unchanged. |
| **Part B:** `src/Agent42.Backend/Models/` (new) | `AnthropicHostedWebSearchChatClient : DelegatingChatClient` + a process-wide availability tracker (static `ConcurrentDictionary`, `ForgetAll()` for tests, the `ReasoningSummaryFallbackChatClient` shape) taking an optional `TimeProvider` (default `TimeProvider.System`). |
| **Part B:** `tools/Agent42.DevHost/Program.cs` (~44), `tests/Agent42.Tests/TestSiloConfigurator.cs` (~48-51), `tests/Agent42.Tests/ModelProviderTests.cs` (~774 uses record equality), the test `FakeChatClient` | Construct or compare `ModelCapabilities`; a trailing optional member keeps them compiling, but the Copilot kind must keep the false default or the equality test breaks. The grain test needs a new capability-flagged model constant and `FakeChatClient` must echo `AdditionalProperties` so the flag is observable. |
| **Part B:** `AgentGrain` stream projection (`ProjectStreamedContent`, ~AgentGrain.cs:1239-1269) | Harvest search-result URLs into `FetchableUrls` (B4). Its `default: return null` already keeps unknown content out of the browser. |
| **Part B:** `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` | Sets a per-request `ChatOptions.AdditionalProperties` flag (the `PromptCacheKey.PropertyName` exemplar in the same `ChatOptions` initializer) when the model's capability is true. The `web_search` shim stays registered; the Backend decides per request. Also a `StorageInputResponseMessageFilter` sibling of `StripReasoningFromStorage` (see B3). |
| **Part B:** `src/Agent42.Backend/Models/AnthropicCompatibilityChatClient.cs` | Read-only check that its zero-block message dropping is safe with server-tool content (B2). |
| `src/Agent42.Backend/Fleet/FleetWebTools.cs` | Comment only ("over the same IWebSearch (SearXNG)"). It picks the chain up through DI with no code change. |
| `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml` | Three optional secrets + env vars, emitted only when non-empty (the `googleMapsApiKey` / `hindsightApiKey` shape). |
| `compose.yaml`, `compose.azure.yaml`, `.env.example` | Optional key env vars for self-host (empty must mean absent). |
| `src/Agent42.Backend/wwwroot/legal/privacy.html` (line ~62) | Names the new third parties. |
| `docs/features/web-search.md`, `docs/features/azure-deployment.md`, `docs/features/self-hosting.md`, `CLAUDE.md` (manual-prereqs table + feature-map row) | Document the keys and behaviour. |
| `.agentic/decisions/0155-*.md`, `.agentic/gotchas.md` | New ADR (via `/decide`), gotcha entries (via `/gotcha`). |
| `tests/Agent42.Tests/` | See the AC table. Existing pins to keep green: `WebRegistrationTests`, `SearxngWebSearchTests`, `SearxngEngineConfigTests`, `ConfigHealth*Tests`, `SiloRegistrationTests`, `TestServiceRegistration`. |

## Context & constraints

Decisions made with the user (this conversation, 2026-11-28):
- Providers: Tavily, Firecrawl, Serper, with SearXNG as the keyless last resort. **Order is priority**, configured
  by `WebSearch:Providers`.
- **Operator-level keys**, not per-user. (Per-user would be the ADR 0018 overlay and is out of scope.)
- Failover on timeout, network error, 5xx, 401/403, 429 and credit exhaustion. **Empty results are not a failure.**
- The user already has a Firecrawl MCP server; that stays independent of this work.

Findings the executor cannot cheaply re-derive:
- `WebTools.WebSearchAsync` (`src/Agent42.Agents/Web/WebTools.cs`) catches every non-cancellation exception and
  returns `"The web search failed: {ex.Message}"` **to the model**. So the chain's final exception message must be a
  fixed curated string (provider names + failure kinds only — never a response body, URL or key). Same invariant as the
  config-health probes.
- `FleetWebTools` also calls `IWebSearch.SearchAsync` and catches the same way, so it inherits the chain.
- `AgentGrain` is non-reentrant: a slow search stalls the turn. Hence short per-attempt timeouts, a total chain
  cap, and **no retries inside a provider** — the chain is the retry. Cross-cutting rule 8 (`RemoveAllResilienceHandlers`
  on every named client, `WebRegistration.cs` comment) applies to every new client.
- An unset optional value reaches the app as an **empty string** through Bicep/env, not null — use
  `IsNullOrWhiteSpace` (`HindsightRegistration.cs` comment).
- Never put a key in a URL or query string (the geocoding probe's key rides in the query string, which is why probe
  messages are curated). New providers send keys in headers.
- The result shape stays `WebSearchResult(Title, Url, Snippet)` within `WebToolLimits` (5 × 300 chars): rule 5,
  tool results are persisted into the 2 MB session.
- Call each vendor in its **cheapest mode** (no raw content, no scraping, basic depth) so free tiers last.
- SearXNG's API: `GET search?q=&format=json`, 403 means JSON is off (gotchas, "SearXNG: the JSON API answers 403
  unless enabled"). Do not change `SearxngWebSearch`'s request or its 403 message; existing tests pin them.

Governing ADRs / gotchas (read before Step 1): ADR 0011, 0059, 0060 (SearXNG), **0013** (the egress ADR that
cross-cutting rule 13 points at — the new ADR extends it), 0015 (hosted tools swap the shim), 0056/0057
(the optional-key egress-switch precedent), 0121 (owner tier is advisory), 0125 (page fetch removed, stays removed),
0128 (empty means off). Gotchas: the "SearXNG is self-hosted, NOT private" entry, the "HostedWebSearchTool.Name
collides with the shim" entry, and the `RemoveAllResilienceHandlers` entry (`.agentic/gotchas.md` ~457).

Exemplars: `src/Agent42.Backend/Memory/HindsightRegistration.cs` (optional key, key-gated named client),
`src/Agent42.Backend/Web/WebRegistration.cs` (named clients, timeouts), `tests/Agent42.Tests/SearxngWebSearchTests.cs`
(`StubHandler` HTTP stubbing), `tests/Agent42.Tests/WebRegistrationTests.cs` (asserts the REAL resolved handler chain).

**Chain semantics (decided here; the ADR records them):**
1. Eligible providers = configured keys present, not benched, in `WebSearch:Providers` order.
2. Try the first eligible provider. Success (any 2xx parse, including zero results) returns.
3. A provider failure classifies into a kind and benches the provider: **Transient** (timeout, network, 5xx) short;
   **RateLimited** (429; honour `Retry-After` when present, capped) short; **Exhausted** (credits/plan limit) long;
   **Unauthorized** (401/403, bad key) long + a loud (Error-level) log. Caller cancellation is **not** a failure and
   propagates.
4. **`searxng` is always the last member and is never benched.** It is keyless and is the safety net, so the chain
   always has a last resort and there is no "every provider benched" state to handle. (An earlier draft ignored
   cooldowns when all were benched; that would re-hit exhausted/unauthorized keyed providers and log Error every
   call, so it was dropped — see Review.)
5. Time bounds, so a bad day cannot stall the non-reentrant grain: the hosted providers share one **hosted budget**
   (per-attempt timeout ≤ 4 s, hosted phase total ≤ 8 s); SearXNG then runs under its existing 10 s client timeout.
   Worst case is therefore ~18 s, and only on a call where every hosted provider is still unbenched — the first
   failure benches it. Step 1 sets the exact constants at or below these bounds; hitting the budget ends the hosted
   phase and falls through to SearXNG, not to an error.
6. All failed → throw one `InvalidOperationException` with the curated message.
7. Cooldown state is in-memory and per process. Single replica is load-bearing (rule 12); a restart relearns it.
8. `WebSearch:Providers` parsing: a blank or whitespace value means the **default order** (Bicep/env pass `""` for
   "unset", ADR 0128). An unknown name is **ignored with a startup warning** (a typo must not take the app down, the
   "app boots" rule for optional config). `searxng` is appended as the last member if the list omits it
   (`WebSearch:Endpoint` stays mandatory and the sidecar always runs), so `WebSearch:Providers=tavily` means
   tavily then searxng. The effective order shows in config health.
9. **No keyed provider configured ⇒ `AddWebTools` registers the bare `SearxngWebSearch` as `IWebSearch`**, not the
   composite. That keeps "no keys, behaves exactly as today" literally true and keeps the existing
   `WebRegistrationTests.RegistersTheSearchAndTheGuardedFetcher` (`Assert.IsType<SearxngWebSearch>`) green. With at
   least one key it registers `FallbackWebSearch`.
10. Known trade-off, recorded in the ADR: a keyed provider that answers with zero results suppresses the SearXNG
    attempt for that query.

**Part B findings and decisions (spike run 2026-11-28 against the pinned `Anthropic` SDK 13.12.0 + `Microsoft.Extensions.AI`
11.0.1, on a stub transport, no network — a throwaway file-based app outside the repo; not reproducible from a file, so
the facts are recorded here):**
- `HostedWebSearchTool` in `ChatOptions.Tools` is sent by `AnthropicClient.AsIChatClient` as
  `{"name":"web_search","type":"web_search_20250305"}`. So the SDK already supports it; no custom request code.
- A response with `server_tool_use` + `web_search_tool_result` + a text block with `web_search_result_location`
  citations maps to `WebSearchToolCallContent`, `WebSearchToolResultContent` and a `TextContent` carrying a
  `CitationAnnotation`; the request count surfaces as `Usage.AdditionalCounts["WebSearchRequests"]`.
- Replaying that response as history, the SDK **drops the server-tool blocks and sends only the text** (the second
  request body contained just `"Orleans got X."`). So stored history does not need them to replay.
- The SDK threw `'web_fetch_requests' cannot be absent` on a stub whose `usage.server_tool_use` omitted it; real
  responses are expected to include it, but this was **not verified against the live API**. The stub JSON was written
  from the API documentation, not captured from Anthropic, so the response shapes above are unproven against the real
  service. A live check needs the user's Anthropic key (cannot be done by the agent).
- Unverified and to settle in B0: which Claude models support the tool, the exact status code and message when the
  organization has not enabled web search in the Anthropic Console or the model lacks support, the `pause_turn` stop
  reason on long server-tool runs (and what the SDK does with it), and the per-search price.
- **Decision 1:** default **ON** for every Anthropic-kind model, with **automatic fallback**: on a classified rejection,
  retry once without the hosted tool, leave the `web_search` function shim in the request so the model uses it, and
  remember that hosted search is unavailable for a while (user choice, 2026-11-28).
- **Decision 2 (design):** the swap happens **below the agent's function-invoking layer, at the provider boundary**, not in
  `BuildAgent`'s tool list. The grain keeps registering the `web_search` function shim for Anthropic as today. A new
  `DelegatingChatClient` replaces that function with `HostedWebSearchTool` per request when it is allowed, and leaves it alone
  otherwise — so the fallback needs no second tool path and the shim's URL-remembering still works on the fallback.
  Registering both would give the model two tools named `web_search` (gotcha ~1223), which is why the swap is a replacement.
- **Decision 3:** the swap is **opt-in per request through a flag** the chat grain sets from `SupportsHostedWebSearch`.
  Without it the wrapper does nothing, so the **fleet is unaffected** (ADR 0097 d4: the fleet refuses hosted tool kinds and
  its `FleetWebTools` `web_search` function must stay a function) and so is any other caller of the Claude client.
- **Decision 4 (blast radius, from the Part B review):** `BuildAgent` also serves stored agents and `/api/agent-run`,
  the research agent, reminders and inbound email (untrusted input, ADR 0096). Hosted search is billed per search on the
  user's own key and the plan sets no `max_uses`, so it applies **only on interactive turns**: the wrapper additionally
  requires `TurnContext.CurrentOrClosed.Interactive` (rule 6: providers read the ambient context, and a missing context
  fails closed, which also keeps the fleet out). Every other entry point keeps the function shim and so the chain. B2 must
  read `TurnContext` to confirm exactly which entry points set `Interactive` and list them in the ADR.
- **Decision 5 (`FetchableUrls`, from the review):** the `read_web_document` description tells the model a URL must be
  "EXACTLY one that came back from web_search". On the hosted path nothing would call `FetchableUrlList.Remember`, so that
  description would mislead Claude. The URLs of the **search results** (`WebSearchToolResultContent`), not text-authored
  or citation-text URLs, are harvested into `FetchableUrls` in the stream projection, which is the same provenance as a
  search engine's results (ADR 0011/0067 allowlist rule). This must happen before B3 strips the blocks from storage.
- **Decision 6 (cooldown key, from the review):** a rejection is a property of one Anthropic organization and Console
  setting, i.e. of one user's API key, and every user shares the same endpoint. The benched set is keyed by provider
  identity (`kind|endpoint|` plus a short hash of the API key, never the key itself, never logged), **not** by
  `kind|endpoint|model` as in `ReasoningSummaryFallbackChatClient`, which is correct for a model property but would let one
  user with web search disabled turn it off for everyone.
- **Decision 7 (in-band errors):** Anthropic can return `web_search_tool_result_error` (too_many_requests, unavailable,
  max_uses_exceeded) **inside a 200 response**. Those are not request-level rejections: no retry, no benching; the model
  sees the error and answers. Recorded as out of scope; B0 records the shape.
- Consequences to record in the ADR: the hosted tool is billed per search by Anthropic and counted in `WebSearchRequests`;
  the query goes to Anthropic, which already holds the conversation, so this is not a new egress destination (ADR 0103),
  but record it in the ADR 0013 chain; ADR 0103 line ~102 lists "Claude's server tools (web search/fetch, code execution)"
  as out of scope, and this ADR is the one that brings web search in.

Open numbers to set in Step 1 (do not guess — put them in `WebSearchOptions` constants with a one-line reason each):
per-attempt timeout for hosted providers, total chain deadline, the three cooldown durations, `Retry-After` cap.

## Steps

**Step 0 — Vendor facts (blocking, do first).** Read the current official API references for Tavily `/search`,
Firecrawl `/v2/search`, and Serper `google.serper.dev/search`. Record in a table (it goes into ADR 0155 and drives
the test fixtures): auth header, request body for the cheapest mode, response field that maps to title / url /
snippet, **credit cost per call in that mode**, and the **exact status codes / body markers for out-of-credits,
rate limit, and bad key**. The details in this plan's prompt came from search summaries and are unverified — in
particular, the exact out-of-credits status code per vendor. Gate: a vendor whose failure signals cannot be
established from its docs is dropped from this MR (the chain still treats any non-2xx as a failure; classification
only tunes the cooldown). Verify with: the table exists and every cell cites a docs URL. Not parallelizable with 3–5.

**Step 1 — Contract + chain core.** Files: `WebSearchProviderException`, `WebSearchFailureKind`, `IWebSearchProvider`
(`Name` + `IWebSearch`), cooldown tracker (takes `TimeProvider`), `FallbackWebSearch`, `WebSearchOptions` (the
numbers above). No HTTP here. Verify: `dotnet test --filter FallbackWebSearch` (fake providers, fake time).
If `FakeTimeProvider` is not already referenced by the test project, do not add a package from memory (rule 4) —
use a minimal hand-written `TimeProvider` fake or resolve it with `dotnet add package`.

**Step 2 — SearXNG as a chain member.** A thin named adapter around the existing `SearxngWebSearch`
(`Name = "searxng"`, exempt from benching). Do **not** change `SearxngWebSearch` itself: it throws plain
`InvalidOperationException` (including the 403 "JSON API is not enabled" message), and classifying that 403 as
"Unauthorized" would wrongly suggest a key problem. Because SearXNG is last and never benched, its failures need no
classification; the chain just ends with the curated error. Verify: `SearxngWebSearchTests` and
`SearxngEngineConfigTests` still pass unchanged.

**Steps 3, 4, 5 — `TavilyWebSearch`, `FirecrawlWebSearch`, `SerperWebSearch` (parallelizable; each needs only
Steps 0–1).** Each: one POST, key in a header, cheapest mode, maps to `WebSearchResult` through the shared
snippet cap, classifies failures into `WebSearchProviderException`, never echoes a body or URL into a message or log.
Verify per provider: its own test file (`TavilyWebSearchTests`, …) driving `StubHandler` with recorded-shape payloads
from Step 0, asserting the request shape (header, no raw-content/scrape flags), the mapping, the cap, and one test
per classified failure.

**Step 6 — Registration.** `WebSearchChainRegistration.AddWebSearchChain(services, config)`, called from
`AddWebTools`. Key-gated named clients (a client is registered only when its key is non-blank, so `CreateClient`
can never hand back a resilience-pipeline-inheriting client — the geocoding/Hindsight reason), each
`RemoveAllResilienceHandlers`, explicit timeouts. Parse `WebSearch:Providers` (comma-separated so it works as an
env var); default `tavily,firecrawl,serper,searxng`; semantics 8 and 9 above. Verify: extend `WebRegistrationTests`
(real handler chain: new clients have no resilience handler; absent key ⇒ no client and no provider; present key ⇒
provider in the resolved chain in configured order; unknown name ignored; blank list ⇒ default). Keep
`RegistersTheSearchAndTheGuardedFetcher` as is (no keys ⇒ bare `SearxngWebSearch`) and add a sibling test asserting
`FallbackWebSearch` when a key is present. Also `SiloRegistrationTests` / `TestServiceRegistration` still resolve
`IWebSearch`.

**Step 7 — Observability + config health (parallelizable with 8–9 after Step 6).** Log which provider answered and
why earlier ones were skipped (kind only, no query text beyond what `WebTools` already logs, no keys); set
`Activity.Current` tags `websearch.provider` / `websearch.skipped` (no query in tags). Config health: a
`PresenceCheck` per key (missing = `Warning`, absent is a supported state — copy the Hindsight wording) and a
free `probe:websearch-chain` (`billable: false`) that reads the in-memory snapshot and returns a curated message
("tavily ok, firecrawl benched: credit exhausted until 14:20"). It must not call any vendor: a live probe would burn
credits. `ConfigHealthProbeSet` is a **positional record** constructed at `ConfigHealthApiTests.cs:56` and
`ConfigHealthTests.cs:216,237,255`: add the new probe as a **trailing optional member defaulting to null** so those
call sites compile unchanged, and have `FromServices` resolve the cooldown tracker with `GetService` (null ⇒ probe
null ⇒ the check stays `skipped`, the existing optional-resolve shape). Register a `probe:websearch-chain` key in
`ConfigHealth.BuildStatic` with `NotProbed(..., billable: false)` and wire it in `ProbeAsync`. `BuildStatic` is at 44
lines of code against the 80 limit, so three `PresenceCheck`s and one row fit. Verify:
`ConfigHealthProbeTests`/`ConfigHealthTests` extended; the "static runs zero probes" invariant test still passes.

**Step 8 — Infra and self-host (parallelizable).** `infra/main.bicep` + `infra/modules/container-app.bicep`: params
`tavilyApiKey`, `firecrawlApiKey`, `serperApiKey` (empty default, **no** `@minLength`, secret + env var emitted only
when non-empty, env names `WebSearch__Tavily__ApiKey` etc.), optional `webSearchProviders` emitted only when
non-empty; `.github/workflows/deploy.yml` passes `secrets.TAVILY_API_KEY` / `FIRECRAWL_API_KEY` / `SERPER_API_KEY`;
`compose.yaml`, `compose.azure.yaml`, `.env.example` (empty must mean absent; `ComposeFileTests` pins the
`SEARXNG_*` env set, so read it first and extend rather than fight it). **No existing test pins the optional-SECRET
shape** (none references `googleMapsApiKey` or `hindsightApiKey`), so add Bicep-text assertions in the style of
`InfraPortabilityTests.cs`: for each of the three params, the empty default with no `@minLength`, the conditional entry
in the secrets array, and the conditional `secretRef` env var (`container-app.bicep` ~204-209 and ~316-319 are the
shape to copy). `webSearchProviders` is a plain non-secret override, so add it to the existing Theory
`InfraPortabilityTests.AppConfigOverrides_AreEmittedOnlyWhenSet`. Also mandatory:
`InfraPortabilityTests.Docs_ListEveryVariableAndSecretTheWorkflowReads` requires `TAVILY_API_KEY`,
`FIRECRAWL_API_KEY` and `SERPER_API_KEY` in backticks in `docs/features/azure-deployment.md`. Verify: `bicep build`
(the CI check) and those tests.

**Step 9 — Docs, ADR, gotchas (parallelizable).** `/decide` → ADR 0155 ("web search provider fallback chain";
extends 0013 as an egress amendment, amends 0011; records the chain semantics, the vendor-facts table, the empty-result
trade-off, and "page reading stays removed, ADR 0125"). **It must name the alternative ADR 0011 rejected** —
"Tavily / Brave / Bing Search APIs … an API key, a per-query bill, and queries attributable to our account"
(0011 lines ~72-74) — and give the changed rationale: reliability from one datacenter IP, keys optional and off by
default, free tiers, and SearXNG kept as the never-benched fallback. Update the `amended_by` frontmatter and header
note of ADR 0011 and ADR 0013, and add the ADR to the CLAUDE.md feature-map row. `/gotcha` entries: (a) empty results
are not a failure and SearXNG is never benched; (b) the chain's final message reaches the model, so it is curated.
**Also edit what becomes false**: the gotcha "SearXNG is self-hosted, NOT private" (`.agentic/gotchas.md` ~464-482,
claims no API key / no per-query bill), the "costs nothing" claim in the `SearxngWebSearch.cs` class comment (lines
~8-13), the `WebRegistration.cs` summary (lines ~6-7, "web_search over the SearXNG sidecar"), the `IWebSearch.cs` doc,
and the `FleetWebTools` comment. Then `docs/features/web-search.md`, `azure-deployment.md` (secrets table, with the
three names in backticks — see Step 8), `self-hosting.md`, the CLAUDE.md manual-prereqs table (three optional
secrets), `privacy.html` (queries may go to Tavily/Firecrawl/Serper when their keys are set). Check
`websearch/SKILL.md` for any claim that web search is SearXNG-only or free. **No `sw.js` `CACHE` bump:** the legal
pages are standalone and outside `SHELL` (`sw.js` ~109). Run `/sync-docs`.

### Part B — Claude uses Anthropic's hosted web search

**Step B0 — Anthropic facts (blocking for B1–B4).** Read the current Anthropic docs for the web search tool and record in
ADR 0156: supported models, the tool version the SDK sends, what an admin must enable in the Console, the **exact status
code and error body** when the organization has not enabled it or the model lacks support (this defines the
"classified rejection" in B2), the `pause_turn` stop reason and how the SDK/M.E.AI surface it, the usage field, and the
per-search price, and the in-band `web_search_tool_result_error` shapes (Decision 7). **Re-run the spike on a streaming
stub** (`GetStreamingResponseAsync` with an SSE body: `content_block_start` for `server_tool_use` and
`web_search_tool_result`, `input_json_delta`, `citations_delta`): the first spike was a non-streamed response and does
not prove the streaming code path, which is the one the app always uses, and the SDK was already strict about
`server_tool_use` (it threw when `usage.server_tool_use.web_fetch_requests` was absent). Gate: if the rejection cannot be
classified from the docs, B2 retries only on an HTTP 400/403 whose message names the web search tool, and the ADR says
that is a guess to confirm with the live check. Verify: the facts table cites docs URLs and the streaming spike result is
recorded.

**Step B1 — Capability and per-request flag.** Add `SupportsHostedWebSearch` to `ModelCapabilities` as a trailing
optional member, true for the Anthropic kind **and a model that `SupportsTools`** (`ChatClientFactory.cs`, mind
`GetCapabilities` for configured models), false for every other kind (the Copilot kind must stay at the default or
`ModelProviderTests` ~774's record equality breaks). In `BuildAgent`, when it is true, add the flag to the existing
`ChatOptions.AdditionalProperties` (beside `PromptCacheKey.PropertyName`); define the property-name constant in the
same place and shape as `PromptCacheKey`. The Backend wrapper strips it on every call, as `PromptCacheKeyChatClient`
does. **Build the properties dictionary in an extracted helper, not inline in the `new ChatOptions { … }`
initializer:** `BuildAgent`/`ComposeAgent` have "little S3776 headroom" (comments at ~190 and ~363) and the initializer
already holds `capabilities.SupportsTools ? tools : []`. Update the `IChatClientFactory.cs` doc (`SupportsHostedTools`
stays Responses-only). Verify: `ModelProviderTests` (extend: Anthropic true, every other kind false, an Anthropic model
without tools false), a `BuildAgent` test that the flag is present for a flagged model and absent otherwise (add a model
constant to `TestSiloConfigurator` in the `HostedToolsModel` shape, and make `FakeChatClient` echo the
`AdditionalProperties` key so the flag is observable), and **`dotnet build` as the S3776/S138 check**.

**Step B2 — The wrapper.** `AnthropicHostedWebSearchChatClient : DelegatingChatClient`, applied in
`ChatClientFactory.Anthropic.cs` **outside** `AnthropicCompatibilityChatClient` (so the shim still sees the final tools).
Per request, on a clone of the options: if the flag is set, the turn is interactive (Decision 4), hosted search is not
benched for **this provider identity** (Decision 6) and the tools contain an `AIFunction` named `web_search`, replace that
function with `new HostedWebSearchTool()`. Always strip the flag. On a classified rejection (B0) from the inner call:
bench hosted search for that provider identity (process-wide tracker, a cooldown constant with a one-line reason), log
it once at Warning with a fixed message that names the Console setting (no key, no hash), and **retry once** with the
original options (the function shim intact). Rules: never retry on an unclassified error, on cancellation, or on a second
rejection; when benched, skip the swap entirely. **Streaming:** the rejection arrives before any update, so the retry must
happen before the first update is yielded — C# forbids `yield` inside a `try` with a `catch`, so pull the first element
through a manual enumerator; `ReasoningSummaryFallbackChatClient.cs` is the in-repo exemplar for "retry once before the
first update, remember the rejection", but **not for its key** (Decision 6). Prove with a test that no update is emitted
twice. **Plumbing:** the kill switch `WebSearch:AnthropicHosted` (default true; blank means default, per the empty-string
rule) travels `ModelProvidersRegistration` → `MultiProviderChatClientFactory` → `ModelProviderBuilder` (~191) →
`Provider.Anthropic(..., hostedWebSearch = true)` as an optional parameter, so direct callers (tests) compile unchanged.
`AnthropicCompatibilityChatClient.HasSendableContent` (~126-137) returns false for server-tool content, so a message
holding only server-tool blocks is dropped before the SDK sees it and the trailing-assistant "Continue." check then looks
at the previous message; this matches the SDK's own drop, but pin **both** outcomes in a test, and update that class's
doc comment ("sees the final options") now that a wrapper sits outside it. Verify: `AnthropicHostedWebSearchTests` on the
existing `AnthropicStub` / `RecordingHttpHandler` helpers (`tests/Agent42.Tests/AnthropicProviderTests.cs` shows the stub
shape), including: user A's rejection does not bench user B; a non-interactive turn keeps the function.

**Step B3 — Do not persist the server-tool blocks.** A `WebSearchToolResultContent` carries the search results with
Anthropic's encrypted content, and the SDK drops it on replay anyway (spike), so storing it only spends the 2 MB session
budget (ADR 0004, rule 5). `InMemoryChatHistoryProviderOptions` takes **one** `StorageInputResponseMessageFilter`
delegate (BuildAgent ~104, `StripReasoningFromStorage` at ~AgentGrain.cs:1311, which `AgentGrain.FleetAnalyzerAgent.cs:36`
also uses), so a "sibling" filter must be **composed** with it, not added: write the composition (or the new stripping)
as an extracted helper, since `StripReasoningFromStorage` is already branchy, and make sure the fleet analyzer's use of the
reasoning filter is unchanged. The new step removes `WebSearchToolCallContent` / `WebSearchToolResultContent` from stored
response messages and drops a message left with no content. Also decide, by measuring, whether the `CitationAnnotation`
payload on text should be kept. **Rule 2:** a persisted shape changes (annotations on stored `TextContent`, or their
absence), so prove it on the Cosmos JSON path in `GrainStorageSerializerTests`, not only in a session test. Verify: a
session test that a hosted-search turn serializes without the blocks and stays well under the session budget, a second
turn that still builds a valid request, and the Cosmos JSON test (the existing `ArtifactToolTests` 64 KB budget note in
the repo shows why budgets here are tested).

**Step B4 — Stream and UI.** Confirm how `AgentStreamEvent` mapping treats `WebSearchToolCallContent`,
`WebSearchToolResultContent` and `CitationAnnotation` (the Responses path is the precedent for hosted-tool content):
unknown content must neither throw nor leak encrypted payloads to the browser (the projection's `default: return null`
at ~AgentGrain.cs:1269 already does this, so the work is a test that pins it). The Responses path has **no** "searching"
chip for hosted calls (no hosted-tool handling exists in the projection), so **no chip is built** and no `wwwroot/js/`
change is expected; if one becomes necessary, bump `CACHE` in `sw.js` (rule 3). Implement Decision 5: in the stream
projection, call the grain's `FetchableUrlList.Remember` for each result URL in `WebSearchToolResultContent`, before B3
strips the blocks, with the same http/https check `WebTools.RememberFetchableUrl` applies; never for URLs found inside
text. Verify: a **streaming** stub test (the SSE shape recorded in B0), not the non-streamed spike response, that unknown
content neither throws nor leaks and that result URLs land in `FetchableUrls` while a URL inside answer text does not.

**Step B5 — ADR, gotchas, docs.** `/decide` → ADR 0156 ("Claude uses Anthropic's hosted web search with automatic
fallback"; extends 0015, amends 0103 (`SupportsHostedTools` stays false; the new capability is separate; name the line
~102 that listed Claude's server tools as out of scope) and 0011; records Decisions 1–7, the facts table from B0 including
the streaming spike, the unverified-against-live-API caveat, the per-search billing, the list of entry points that are and
are not interactive, and that Anthropic already holds the conversation). `/gotcha`: the swap happens at the provider
boundary below the function-invoking layer and is flag-gated so the fleet is untouched; and server-tool blocks are not
persisted. Update `docs/features/web-search.md`, the CLAUDE.md feature-map row for models/web search (add 0156), and
the `BuildAgent` comment that says only a Responses model reaches hosted tools. Run `/sync-docs`.

**Step 10 — Verify.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
`dotnet format Agent42.slnx --verify-no-changes`. The live check with real keys (a key actually answers; an exhausted
key actually benches) cannot be run by the agent: list it as an open follow-up for the user in the MR body. Part B has
its own live check the agent cannot run: a real Claude turn that searches (hosted path), a real rejection with the
Console setting off (fallback path), and a long search that returns `pause_turn`.

## Acceptance criteria → test mapping

| # | Criterion | Test (file) |
|---|-----------|-------------|
| AC1 | The first eligible provider's answer is returned; later providers are not called | `FallbackWebSearchTests.FirstSuccess_ShortCircuits` (`FallbackWebSearchTests.cs`) |
| AC2 | A failure falls through to the next provider, for each failure kind | `FallbackWebSearchTests.EachFailureKind_FallsThrough` |
| AC3 | Zero results is a success and does not fall through | `FallbackWebSearchTests.EmptyResults_AreNotAFailure` |
| AC4 | A benched provider is skipped until its cooldown ends, with per-kind durations | `FallbackWebSearchTests.Cooldown_SkipsThenRecovers` (fake time) |
| AC5 | `Retry-After` is honoured and capped | `FallbackWebSearchTests.RetryAfter_IsHonouredAndCapped` |
| AC6 | SearXNG is never benched: with every keyed provider benched the call still reaches it, and a SearXNG failure does not bench it | `FallbackWebSearchTests.Searxng_IsNeverBenched` |
| AC7 | Caller cancellation propagates and does not bench | `FallbackWebSearchTests.CallerCancellation_Propagates` |
| AC8 | The hosted budget ends the hosted phase and falls through to SearXNG (not to an error) | `FallbackWebSearchTests.HostedBudget_FallsThroughToSearxng` |
| AC9 | All providers failing throws one curated message with no body/URL/key | `FallbackWebSearchTests.AllFailed_MessageIsCurated` |
| AC10 | Each provider sends the key in a header, the cheapest request, maps to `WebSearchResult`, applies the snippet cap | `TavilyWebSearchTests`, `FirecrawlWebSearchTests`, `SerperWebSearchTests` |
| AC11 | Each provider classifies its documented failure signals | the same three files, one test per kind |
| AC12 | No key ⇒ provider skipped and no named client; blank string counts as no key; no keys ⇒ `IWebSearch` is the bare `SearxngWebSearch`; a key ⇒ `FallbackWebSearch` | `WebRegistrationTests` (extended; the existing `RegistersTheSearchAndTheGuardedFetcher` stays green) |
| AC13 | New clients have no inherited resilience handler | `WebRegistrationTests` (extended, the real handler chain) |
| AC14 | `WebSearch:Providers` order is honoured; an unknown name is ignored with a warning; a blank value means the default; an omitted `searxng` is appended last; all-unknown names leave SearXNG only | `WebSearchChainRegistrationTests.cs` |
| AC15 | `WebSearch:Endpoint` is still validated at startup | `WebRegistrationTests` (existing) |
| AC16 | Config health shows per-key presence and a non-billable chain-state probe that calls no vendor | `ConfigHealthTests` / `ConfigHealthProbeTests` (extended) |
| AC17 | The fleet's `web_search` uses the chain | No `FleetWebToolsTests` file exists, so a DI-resolution assertion in `WebRegistrationTests` (the `IWebSearch` the fleet resolves is the chain when a key is set) |
| AC18 | Bicep emits each secret and env var only when non-empty, with no `@minLength`; `webSearchProviders` is emitted only when set | new Bicep-text assertions for the three secrets in `InfraPortabilityTests.cs`; `webSearchProviders` added to `AppConfigOverrides_AreEmittedOnlyWhenSet` |
| AC19 | The three key names are documented, and the workflow's secrets are all listed in the docs | `InfraPortabilityTests.Docs_ListEveryVariableAndSecretTheWorkflowReads` (existing, must stay green) |
| AC20 | Compose passes an empty key as absent | `ComposeFileTests` (extended) |
| AC22 | The wrapper replaces the `web_search` function with `HostedWebSearchTool` only when the flag is set and hosted search is not benched; the flag never reaches the SDK | `AnthropicHostedWebSearchTests.cs` |
| AC23 | Without the flag (the fleet, any other caller) the tools are untouched | `AnthropicHostedWebSearchTests.cs` + a `FleetWebTools` assertion that its function stays a function |
| AC24 | A classified rejection retries once with the function shim intact, benches hosted search for the cooldown, and logs once | `AnthropicHostedWebSearchTests.cs` (stub returns the B0 rejection) |
| AC25 | An unclassified error, a cancellation or a second rejection is not retried | `AnthropicHostedWebSearchTests.cs` |
| AC26 | A streaming rejection is retried before any update is yielded and no update is emitted twice | `AnthropicHostedWebSearchTests.cs` |
| AC27 | The capability is true for the Anthropic kind only, and `SupportsHostedTools` stays false for it | `ModelProviderTests` (extended) |
| AC28 | `BuildAgent` sets the flag only for a model with the capability; the shim stays registered | grain test on the `HostedToolsModel` shape in `TestSiloConfigurator` |
| AC29 | A hosted-search turn is stored without server-tool blocks, stays under the session budget, and the next turn builds a valid request, including a message that held only server-tool content (both the drop and the trailing-assistant outcome are pinned) | session test + `AnthropicCompatibilityChatClientTests`; and the Cosmos JSON path in `GrainStorageSerializerTests` (rule 2) |
| AC30 | On a **streamed** response, the stream mapping neither throws on, nor leaks, the server-tool and citation content | streaming stub test (the SSE shape recorded in B0) |
| AC31 | The `WebSearch:AnthropicHosted` kill switch (blank = default true) turns the swap off, and reaches the wrapper through `ModelProvidersRegistration` → `MultiProviderChatClientFactory` → `ModelProviderBuilder` → `Provider.Anthropic` | `AnthropicHostedWebSearchTests.cs` + a registration test |
| AC32 | One user's rejection does not bench hosted search for another user | `AnthropicHostedWebSearchTests.cs` (two provider identities, same endpoint and model) |
| AC33 | A non-interactive turn (stored agent run, research, reminder, inbound email) keeps the function shim; an interactive turn swaps | `AnthropicHostedWebSearchTests.cs` over `TurnContext` |
| AC34 | Search-result URLs from the hosted tool are remembered as fetchable; a URL inside answer text is not | streaming stub test in B4 |
| AC35 | The `AgentGrain.BuildAgent` and filter changes build under the S3776/S138 gates, and the fleet analyzer's reasoning filter is unchanged | `dotnet build` + the existing fleet analyzer test |
| AC21 | The answering provider and skip reasons are logged without query text or keys, and `websearch.provider` / `websearch.skipped` are set on `Activity.Current` | `FallbackWebSearchTests.Observability_*` (a capturing logger + an `ActivityListener`) |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

- `coverage_new_code_pct_min`: 80 — `node .agentic/scripts/check-new-code-coverage.mjs`
- `cognitive_complexity_max`: 15 — SonarAnalyzer S3776, build error
- `method_lines_max`: 80 — SonarAnalyzer S138, build error
- `method_params_max`: 11 — SonarAnalyzer S107, build error
- `e2e_failing_tests_max`: 0 — Playwright smoke layers
- `vulnerable_packages`: 0 — no new package is expected; if Step 1's `FakeTimeProvider` needs one, resolve it with `dotnet add package`, never from memory
- Report SKIPPED (never estimate) for any gate the executor cannot measure.

## Out of scope

- Page reading / scraping (ADR 0125 stays; Firecrawl `/scrape` and Tavily `/extract` are not touched).
- Per-user keys and any per-user provider overlay (ADR 0018 shape).
- Reranking.
- For Part B: handling Anthropic's in-band `web_search_tool_result_error` (Decision 7); a sources/citations UI or a
  "searching" chip; a per-model flag (the choice was default ON with automatic fallback); hosted code
  execution for Claude; per-search cost in the usage dashboard (record `WebSearchRequests` in the ADR as a follow-up);
  the tool's options (`max_uses`, domain filters) and newer tool versions; the fleet (it stays on the chain).
- Making the SearXNG sidecar optional or removing it (`WebSearch:Endpoint` stays mandatory, the UI proxy and
  `SEARXNG_SECRET` stay).
- Round-robin / usage-spreading across providers (priority order only).
- Live probes that spend credits.
- Persisting cooldown state across restarts or sharing it across replicas.

## Review

Critic review (fresh-context `critic` subagent, 2026-11-28): no ADR contradiction; 3 high and 3 medium findings,
each checked against the tree before acting. Handling:

| # | Finding | Handling |
|---|---------|----------|
| 1 (high) | Step 8 / AC18 cited a non-existent test for the optional-secret shape | **Accepted, verified** (no test greps `googleMapsApiKey`). Step 8 now adds new Bicep-text assertions, uses `AppConfigOverrides_AreEmittedOnlyWhenSet` for the non-secret `webSearchProviders`, and names `Docs_ListEveryVariableAndSecretTheWorkflowReads` (AC18–AC20). |
| 2 (high) | `WebRegistrationTests.cs:123` asserts the bare `SearxngWebSearch`, so a composite would break a test the plan called "keep green" | **Accepted, verified.** Semantics 9: no keys ⇒ the bare `SearxngWebSearch`; a key ⇒ the composite. AC12 and Step 6 updated. |
| 3 (high) | `ConfigHealthProbeSet` is a positional record with four constructor call sites | **Accepted, verified.** Step 7: trailing optional member defaulting to null, `FromServices` resolves the tracker with `GetService`. |
| 4 (med) | Blank `Providers`, a list that omits or empties the chain, were undefined | **Accepted.** Semantics 8 and AC14: blank ⇒ default, unknown ignored, `searxng` appended last. |
| 5 (med) | "All benched ⇒ ignore cooldowns" re-hits exhausted/unauthorized providers and logs Error every call; SearXNG's 403 would be misclassified; time bounds were deferred | **Accepted, redesigned.** SearXNG is never benched (semantics 4, AC6), so there is no all-benched state; SearXNG is wrapped by an adapter and left unclassified (Step 2); hosted budget ≤ 8 s with ≤ 4 s per attempt, worst case ~18 s (semantics 5, AC8). |
| 6 (med) | ADR 0011 explicitly rejected Tavily/Brave/Bing; stale claims in docs/gotchas not listed | **Accepted.** Step 9 requires the ADR to name the rejected alternative and the changed rationale, and lists the gotcha (~464-482), the `SearxngWebSearch.cs` and `WebRegistration.cs` comments, and the 0011/0013 frontmatter. |
| low | `privacy.html` is outside `SHELL`; no AC for observability; AC17 has no `FleetWebToolsTests`; `FakeTimeProvider` not referenced | **Accepted.** No `CACHE` bump (Step 9); AC21 added; AC17 states the DI assertion plainly; Step 1 already says not to add a package from memory. |

Rejected findings: none.

**Part B review** (added after the first review; a second fresh-context `critic` pass, 2026-11-28). The design was
confirmed sound: a client below the function-invoking layer does see the `web_search` function, tool-name bookkeeping and
selection run before the swap, the stream projection's `default: return null` already keeps unknown content out of the
browser, and the fleet never sets the flag. Each finding was checked against the tree before acting:

| # | Finding | Handling |
|---|---------|----------|
| 1 (high) | A cooldown keyed by provider + model would bench hosted search for every user when one user's Console setting is off | **Accepted, verified** (`ReasoningSummaryFallbackChatClient` keys on `kind\|endpoint\|model`, no user). Decision 6, B2, AC32. |
| 2 (high) | No plumbing for the kill switch, tracker or clock: `Provider.Anthropic` is a static factory called from `ModelProviderBuilder` and tests | **Accepted, verified.** Affected areas and B2 now route the switch through `ModelProvidersRegistration` → `MultiProviderChatClientFactory` → `ModelProviderBuilder` → `Provider.Anthropic` as an optional parameter; the tracker is process-wide and takes an optional `TimeProvider`. AC31. |
| 3 (med) | S3776/S138 risk in `BuildAgent`; one storage-filter delegate cannot take a "sibling" | **Accepted.** B1 and B3 require extracted helpers and a composed filter; AC35 gates on `dotnet build`. |
| 4 (med) | The spike was non-streamed; the app always streams | **Accepted.** B0 re-runs the spike on an SSE stub; B4 and AC30 use a streaming stub. |
| 5 (med) | In-band `web_search_tool_result_error` is unhandled | **Accepted as out of scope** (Decision 7); B0 records the shape. |
| 6 (med) | Default ON with no `max_uses` reaches unattended, untrusted entry points | **Accepted.** Decision 4: the swap applies only on interactive turns, via `TurnContext`; AC33. This narrows the user's "Claude models use hosted search" to interactive chat, so it is called out in the summary for confirmation. |
| 7 (med) | `read_web_document`'s description would mislead Claude, since nothing seeds `FetchableUrls` | **Accepted.** Decision 5, B4, AC34: seed from search-result URLs only. |
| low | `ModelCapabilities` call sites and equality test, deriving the capability from `SupportsTools`, observability of the flag in tests, no existing chip to copy, compat-client drop, ADR 0103 line ~102, Cosmos JSON proof | **Accepted.** Folded into Affected areas, B1, B2, B3, B4, B5 and AC29. |

Rejected findings: none.

## Implementation status and divergences (2026-11-28)

**Part A (Steps 0-9) is implemented on `feature/web-search-fallback-chain`. Part B (Steps B0-B5) is NOT started** and the
spec therefore stays in `.agentic/specs/`; ADR 0156 is still reserved for it. The plan allowed two MRs.

Divergences from the plan:
- **Serper is dropped.** Step 0's gate: its API docs returned 404 and the homepage states no auth header, credit cost or
  failure codes. The chain is Tavily, Firecrawl, SearXNG; default order `tavily,firecrawl,searxng`. No `serperApiKey`,
  `SERPER_API_KEY`, or `WebSearch:Serper:ApiKey` exists. Adding it later needs verified facts plus one provider class and one
  registration entry. ADR 0155 records this.
- AC14 (provider-order parsing) is tested in `WebRegistrationTests`, not a separate `WebSearchChainRegistrationTests.cs`.
- Tavily and Firecrawl share one test file, `HostedWebSearchProviderTests.cs`.
- Critic findings applied: a 2xx without the results container is a Transient failure (not an empty answer); the new Bicep key
  params are `@secure()` (pinned by a test); the unknown-provider warning also fires with no key set; the provider-construction
  null-forgiving pattern was replaced. `hindsightApiKey` still lacks `@secure()` (pre-existing, left alone).
- Open live checks for the user (the agent cannot run them): a real Tavily and Firecrawl key answers; an exhausted key benches
  and falls through; Tavily 432 really means exhausted credits (the docs only imply it).
