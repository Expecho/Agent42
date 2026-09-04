# Plan — `read_web_document`: ingest a web-hosted document into conversation RAG

planned-against: main @ 8a4210a

## Overview

Give the model a tool that turns a document it found on the web (a PDF in a `web_search`
result, a spec the user pasted) into retrievable RAG context for the current conversation,
instead of being limited to `fetch_web_page`'s 8 KB of scraped page text.

Why now: `fetch_web_page` runs on the hosted **context.dev** scrape API, which returns
markdown and cannot deliver raw document bytes — and the user is retiring context.dev on
cost grounds in the near future. This feature needs bytes, so it fetches them in-container
on a **second, document-only path**, restoring the SSRF guard ADR 0027 deleted. It is
deliberately scoped so `fetch_web_page` and context.dev are **untouched**, which is what
lets it ship independently of the context.dev removal — and it leaves behind the restored
guard that removal will need.

## Affected areas

Verified against the tree at `8a4210a`.

**New files**
- `src/Agent42.Backend/Web/PrivateAddressGuard.cs` (restored from `6c8cb90^`)
- `src/Agent42.Backend/Web/GuardedDocumentFetcher.cs`
- `src/Agent42.Agents/Documents/IWebDocumentFetcher.cs`
- `src/Agent42.Agents/Documents/InMemoryWebDocumentFetcher.cs` (offline/DevHost/tests)
- `src/Agent42.Agents/Documents/WebDocumentLimits.cs`
- `src/Agent42.Agents/Documents/IDocumentIngestor.cs`
- `src/Agent42.Backend/Documents/WebDocumentIngestor.cs`
- `src/Agent42.Agents/Documents/AgentGrain.WebDocumentTool.cs`
- `.agentic/decisions/0066-in-container-document-fetch-for-rag.md`
- Tests: `PrivateAddressGuardTests.cs`, `GuardedDocumentFetcherTests.cs`,
  `WebDocumentToolTests.cs`

**Modified — code**
- `src/Agent42.Agents/Conversations/AgentGrainState.cs` — trailing `[Id(11)] WebDocumentNames`
- `src/Agent42.Agents/Documents/AgentGrain.Documents.cs` — provenance-aware
  `WithDocumentAwareness`
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — deferred-rebuild flag + **two** consume
  sites (`ChatCoreAsync` and `ReceiveReminder`)
- `src/Agent42.Agents/Tools/ToolCatalog.cs` — key, descriptor, name mapping (appended last)
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — register the tool
- `src/Agent42.Backend/Documents/DocumentIngestionService.cs` — optional `maxChunks`,
  return the embedded chunks
- `src/Agent42.Backend/Program.cs` — named client + guarded handler + registration
- `tools/Agent42.DevHost/Program.cs` — offline fetcher + ingestor registration
- `src/Agent42.Agents/Skills/websearch/SKILL.md` — document the new tool

**Modified — tests**
- `tests/Agent42.Tests/TestServiceRegistration.cs` — offline fetcher for the silo
- `tests/Agent42.Tests/ToolSelectionApiTests.cs` — expected catalog rows (`:57`)
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — `AgentGrainParts()` row + docstring
- `tests/Agent42.Tests/AgentGrainTests.cs` — extend
  `ToolResults_DoNotBlowTheSessionSizeBudget`
- `tests/Agent42.Tests/DocumentIngestionServiceTests.cs` — `maxChunks`

**Modified — docs (all of these are stale after this MR; hard rule 2)**
- `.agentic/gotchas.md:182` — scope the SSRF entry to `fetch_web_page`
- `.agentic/gotchas.md:416` — the `get_current_location` entry says "there is no longer even
  a `fetch_web_page` guard to be asymmetric with"; that clause becomes false
- `.agentic/decisions/0027-*.md` — inline amendment note (repo convention: `0011:107`)
- `.agentic/decisions/0011-*.md:107` — its amendment note says the guard mechanism is
  superseded; it is partially restored
- `.agentic/decisions/0056-*.md` — add `read_web_document` to its injection-vector list
- `CLAUDE.md` — the ADR 0027 bullet asserting the SSRF sink "is gone (deleted)" is now
  wrong; **and** the ADR 0039 bullet says "13 partial files" while there are **12** (the
  13th file it counts is `AgentGrainState.cs`, not a partial). Fix both, add the new bullet.

## Context & constraints

Judgment and findings the executor cannot cheaply re-derive. Code facts are pointed at,
not restated.

### The ADR 0027 collision — settled by the user, must be recorded honestly

`.agentic/decisions/0027-hosted-page-scrape-via-context-dev.md` lists under *Alternatives
considered*: **"Key-present / key-absent fallback to a direct fetch … Rejected by the
user: it would keep the SSRF sink (and its whole guard) alive for the fallback, doubling
the surface for no benefit. Full replace, one path."**

This feature re-adopts that shape. **The user was asked and chose to re-adopt it**, on the
grounds that the premise changed: context.dev is being retired on cost, and it cannot
deliver document bytes at all — so this path has a benefit the rejected fallback did not.

1. **ADR 0027 is NOT superseded.** Its decision governs `fetch_web_page`, which stays on
   context.dev, unchanged. Its own wording is "protect[s] nothing **on this path**"; a
   second path is outside that claim. ADR 0066 records the re-adoption and cross-references
   0027; 0027 gets an inline amendment note, not a supersession. (Precedent: ADR 0063
   re-adopting an alternative 0053 had rejected — see CLAUDE.md.)
2. **The gotcha at `.agentic/gotchas.md:182`** is **scoped, not rewritten**. Its bold "Do
   not reintroduce a `PrivateAddressGuard` here" stays true for `fetch_web_page`.

### The deleted guard is recoverable and was well built — restore, don't reinvent

`git show 6c8cb90^:src/Agent42.Backend/PrivateAddressGuard.cs` (121 lines) plus its tests
(98 lines). Verified properties worth preserving verbatim:

- It hangs off `SocketsHttpHandler.ConnectCallback`, so **redirects are covered** — each
  hop opens its own connection through the same callback.
- It **resolves DNS itself** inside the callback and then dials the *vetted IPs*, not the
  hostname. That is what closes DNS rebinding; a naive "resolve, check, then connect by
  name" re-resolves and is TOCTOU-broken. Do not simplify this.
- It handles IPv6 bypasses: IPv4-mapped, IPv4-compatible, NAT64 `64:ff9b::/96`.

**Do NOT restore `WebPageFetcher` or the `AngleSharp` package** that `6c8cb90` removed.
This path rejects HTML, so there is no HTML→text pass and **no new package reference at
all**. Only the guard and the capped-read loop come back.

`tests/Agent42.Tests/TestHttpServer.cs` still exists and is in active use — API unchanged
from what the deleted tests used. Reuse it; do not add a harness. The deleted tests'
policy-swap trick (`LoopbackAllowed()` — inject
`ip => IPAddress.IsLoopback(ip) || IsPubliclyRoutable(ip)`) is how a guarded fetcher is
tested against a loopback test server. Copy that.

**A guard that is built, tested and never wired is the highest-consequence way this MR can
ship green.** Class-level tests cannot see `Program.cs`. AC19 exists for exactly this.

### The provider is attached at build time — this is the trap that decides the design

`BuildAgent` attaches the RAG `TextSearchProvider` only when `DocumentNames.Count > 0`,
decided when the agent is built, before the turn runs. So a mid-turn ingest gives the
current turn no retrieval. Two rules follow:

- **The tool returns the excerpts itself**, inline, budget-capped. That is what makes the
  current turn work, and why the tool takes a `question` parameter.
- **Do NOT call `RegisterDocumentAsync` from the tool.** It does
  snapshot→`BuildAgent`→restore, i.e. swaps `_session` out from under a framework run that
  is iterating it. Every existing call site is an API endpoint *outside* a turn
  (`AgentApi.Attachments.cs:97`, `AgentApi.StoredAgents.cs:116`) — never mid-run.

The rebuild is deferred to after the run completes. **There are TWO consume sites, and
missing the second is a silent bug:**

- `ChatCoreAsync` — immediately after `PersistTurnUnlessEphemeralAsync()`
  (`AgentGrain.cs:520`). Covers the live chat path and `/api/agent-run`, which drains it.
- `ReceiveReminder` — it does **not** drain `ChatCoreAsync`; it calls `_agent.RunAsync`
  directly (`AgentGrain.cs:884`) and does its own serialize/write/index. Without a consume
  site here, a reminder turn that ingests persists `DocumentNames` and then loses the
  rebuild, leaving retrieval detached for the rest of the activation while the instructions
  claim a document exists.

Both call sites are plain method calls, so they add **zero** cognitive complexity to their
containing methods (S3776 counts branching, not calls) — keep the branch inside the new
method. The gate is `cognitive_complexity_max: 15` at `error` severity.

### The inline search ranks in-process — it must NOT read back from `IDocumentIndex`

Rank the chunks **just embedded during this ingest**, in memory, against the embedded
`question`. Do not call `IDocumentIndex.SearchAsync`. Three reasons, all load-bearing:

1. The Cosmos vector path is **prod-only and unverified by CI** (ADR 0007). A read-back
   test passes offline against `InMemoryDocumentIndex` (immediately consistent) and can
   return nothing in production. This repo has that exact scar — ADR 0061's missing
   `compositeIndexes` entry shipped broken in prod while every offline test was green.
2. `IDocumentIndex.SearchAsync(ownerKey, …)` has **no per-document filter**
   (`IDocumentIndex.cs:32`), so a read-back would mix in excerpts from the user's *other*
   documents and label them as this URL's content.
3. It keeps `IDocumentIndex` / `CosmosDocumentIndex` / `InMemoryDocumentIndex` out of the
   diff entirely.

Consequence for the port: `IDocumentIngestor` returns the embedded chunks (or the ranked
top-K) — not just a count. `DocumentIngestionService.IngestAsync` is extended to return
them; both existing call sites keep using the count.

### Provenance: the model must not be told an attacker's document came from the user

`WithDocumentAwareness` (`AgentGrain.Documents.cs:65`) currently emits *"The user has
uploaded the following document(s)…"*. Pushing a web-fetched document into `DocumentNames`
would assert **user provenance** for attacker-authored content — the strongest trust signal
in the prompt, and the exact inverse of `BuildFetchPayload`'s `"UNTRUSTED: …"` labelling
that ADR 0027 treats as load-bearing.

So: a **trailing `[Id(11)] List<string> WebDocumentNames`** on `AgentGrainState` (the
trailing-optional idiom of ADR 0016 `[Id(7)]` and ADR 0045 `[Id(5)]` — zero migration), and
`WithDocumentAwareness` renders two sentences: uploaded documents as today, web-fetched ones
under an explicit untrusted-provenance sentence. The RAG provider gate becomes
`DocumentNames.Count + WebDocumentNames.Count > 0`.

The filename is derived from the URL path and is therefore **attacker-influenced text
inside the system prompt and inside every citation `SourceName`**. Sanitize hard: strip
path separators and control characters, allow a conservative character set, cap the length
(64 chars), fall back to the host plus an extension when nothing usable remains.

### Why state is written immediately

After a successful ingest the tool mutates the name list and writes state **immediately**
rather than waiting for the turn tail: if the stream is abandoned, the Cosmos chunks are
already written, and an unpersisted name list would leave them orphaned. The extra write is
cheap relative to ingestion and safe — it persists the *previous* turn's `SerializedSession`,
which the turn tail overwrites. Ephemeral turns never reach here.

### Binding rules confirmed against ADRs/gotchas

- **ADR 0008 must be argued, not ignored.** `0008:31-35` rejects *"`TextSearchProvider` in
  `OnDemandFunctionCalling` mode — exposes retrieval as a model-invoked tool"*. The inline
  top-K here is a model-invoked retrieval call, i.e. the shape 0008 refused. The
  justification ADR 0066 must state: it is **additive to** always-on retrieval, not a
  replacement (the always-on provider still attaches from the next turn), and it exists
  solely because `BuildAgent` cannot re-run mid-run. Do not write "following ADR 0008".
- **Incognito (ADR 0034):** the refusal goes **inside the tool** against `_ephemeral`,
  exactly as `SchedulePromptAsync` refuses scheduling — not in `BuildAgent`, which is read
  at build time (see the deliberately-opposite gates in `AgentGrain.Memory.cs:21`).
- **Tool returns are persisted (ADR 0004 / gotchas:150):** budget them in
  `WebDocumentLimits`, and extend the named guard
  `ToolResults_DoNotBlowTheSessionSizeBudget` in `AgentGrainTests.cs` rather than writing a
  parallel check.
- **Named `HttpClient` (gotchas:252):** call `RemoveAllResilienceHandlers()` **and** set an
  explicit `http.Timeout`, as every named client in `Program.cs` does (`:254, :268, :287,
  :325, :376`) — the pipeline's total timeout goes away with the retries. Because the
  bounded read loop requires `ResponseHeadersRead`, `HttpClient.Timeout` does **not** bound
  the body read: add a separate deadline (`CancellationTokenSource`) over the whole
  download, or a slow-drip body stalls a non-reentrant grain indefinitely.
  Note gotchas:263 — the shipped pipeline is assembled in `Program.cs` and no unit test
  constructs it, so this is a **source-text** assertion (AC18), not a runtime one.
- **New catalog key (ADR 0021):** lands **OFF for anyone who ever pressed Save**. No
  migration, by design.
- **Append the key LAST** in both `AllSelectableToolKeys` and `Descriptors`, after `Memory`.
  `ToolCatalog.cs` documents that appending is what keeps the order-sensitive
  `Sanitize_OrdersStaticFirst_*` guards from needing an expectation edit.
- **Hosted-tools branch:** `BuildAgent:101-110` *swaps* the SearXNG shims out on
  `SupportsHostedTools`. `read_web_document` is **not** part of that swap — it is document
  ingestion, which no hosted tool provides — so it registers on both branches. Consequence
  to record: on a hosted model nothing calls `RememberFetchableUrl` (the native
  `HostedWebSearchTool` cannot), so `FetchableUrls` is user-text-only there and the tool is
  reachable only for URLs the user typed.
- **`PdfLock` is process-wide:** `DocumentTextExtractor` serializes PDF extraction (PDFium
  is not thread-safe) and `infra/main.bicep` pins a single replica, so a large PDF blocks
  every other PDF extraction in the process. Accepted; record it.
- **Flat namespaces:** every new file declares `Agent42.Agents` or `Agent42.Backend`
  regardless of folder (`ProjectLayoutTests.EveryFileStaysInTheFlatNamespace`). No new
  feature folder is created — `Documents/` and `Web/` already exist in both projects.

### Threat-model changes ADR 0066 must state

- **Memory makes the poisoning UNBOUNDED, not conversation-bounded.** `Memory` is in
  `AllSelectableToolKeys`, so it is ON for every `null == all` conversation, and retain runs
  on every non-incognito turn. ADR 0056 already names this failure mode for ADR 0027
  followed links, ADR 0024 MCP results and ADR 0049 SKILL.md uploads;
  `read_web_document` is a **fourth and larger** member of that list, because it ingests a
  whole document. Deleting the conversation clears the Cosmos chunks and the name list; it
  does **not** clear the Hindsight bank, which has no delete path. Add the fourth entry to
  ADR 0056's list. Do not describe this feature as "bounded to the conversation".
- **`/api/agent-run` reachability (ADR 0022).** That endpoint is on `excludedPaths`
  (publicly reachable with an API key) and drains `ChatCoreAsync`, so
  `RememberFetchableUrlsInUserText` whitelists URLs in its prompt. After this change an API
  key holder can therefore cause **our container** — which holds the managed identity — to
  open an outbound connection to a URL of their choosing. `PrivateAddressGuard` is exactly
  the right control; the point is that the reachability changed and the ADR must say so.
- **ADR 0061:** `ProjectForSearch` runs over `GetHistoryAsync()` turns, not raw tool
  messages, so the inline excerpts do not themselves enter the `chat-messages` index — only
  the assistant reply quoting them does. Worth one line; not a blocker.

### User decisions from the interview

- Re-adopt ADR 0027's rejected alternative, new ADR states why; **0027 not superseded**.
- Download cap **10 MB**, paired with a chunk-count cap so the grain stall is bounded by
  chunks and not only by bytes.

## Steps

**Step 1 — ADR 0066.** Write
`.agentic/decisions/0066-in-container-document-fetch-for-rag.md` via `/decide`, covering
every point in "Threat-model changes" and the ADR 0008 argument above, plus: the two-path
surface as an accepted cost; the new-key-is-OFF-for-savers consequence; the `PdfLock`
single-replica note; the hosted-model near-inertness; and the follow-up
`POST /api/conversations/{id}/documents/from-url` noted but not built.
*Verify:* the repo's doc-path CI check passes.

**Step 2 — Restore the guard.** `src/Agent42.Backend/Web/PrivateAddressGuard.cs` from
`6c8cb90^`, namespace `Agent42.Backend`, plus `PrivateAddressGuardTests.cs`. Keep the
self-resolving `ConnectCallback` shape and the IPv6 bypass handling.
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~PrivateAddressGuard`.

**Step 3 — Fetcher port + implementations.** *(depends on 2)* `IWebDocumentFetcher`,
`InMemoryWebDocumentFetcher` (exemplar: `src/Agent42.Agents/Web/InMemoryWebPageFetcher.cs`)
and `WebDocumentLimits` in `src/Agent42.Agents/Documents/`; `GuardedDocumentFetcher` in
`src/Agent42.Backend/Web/`. Limits: `MaxDownloadBytes` 10 MB, `MaxChunks`, `MaxExcerpts`,
`MaxReturnTextLength`, `DownloadDeadline`. The fetcher must: reject non-http(s) schemes; cap
**while streaming** (`Content-Length` pre-check *and* a bounded read loop — a 500 MB body
must never be buffered); bound the whole download with its own deadline; resolve the media
type from the **response `Content-Type`**, falling back to the URL extension only when the
server sends a generic type; reject anything `DocumentMediaTypes.IsSupported` rejects **and**
explicitly reject `text/html` (an HTML error page served for a `.pdf` URL is the common
case); derive and hard-sanitize the filename.
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~GuardedDocumentFetcher`.

**Step 4 — Ingestor port.** *(parallelizable with 2–3)* `IDocumentIngestor` in
`src/Agent42.Agents/Documents/`; `WebDocumentIngestor` in
`src/Agent42.Backend/Documents/`. Extend `DocumentIngestionService.IngestAsync` with an
**optional** `maxChunks` (default null = unlimited) and have it return the embedded chunks
alongside the count, so both existing call sites compile untouched. Report truncation
explicitly (mirror `WebPageContent.Truncated`) rather than dropping the tail silently.
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~DocumentIngestionService`.

**Step 5 — State + provenance.** *(parallelizable with 2–4)* Trailing `[Id(11)]
WebDocumentNames` on `AgentGrainState`; provenance-aware `WithDocumentAwareness`; widen the
RAG provider gate in `BuildAgent`. Add a golden-document case to
`GrainStorageSerializerTests` — a pre-`[Id(11)]` `AgentGrainState` must still deserialize on
the **Cosmos JSON** path (the ADR 0045 precedent: Newtonsoft binding a missing property is
the real risk, and the binary assertion is secondary).
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~GrainStorageSerializer`.

**Step 6 — The grain tool.** *(depends on 3, 4, 5)*
`src/Agent42.Agents/Documents/AgentGrain.WebDocumentTool.cs` with `ReadWebDocumentAsync`.
Guard order: absolute http(s) → `IsFetchableUrl` → `_ephemeral` refusal → fetch → ingest →
name-list mutation + `WriteStateAsync` → set the deferred-rebuild flag → in-process top-K
rank → budgeted return. Wrap in the `WebSearchAsync`-style
`catch (Exception ex) when (ex is not OperationCanceledException)`. Add the deferred rebuild
method and **both** consume sites (`AgentGrain.cs:520` and in `ReceiveReminder` after its
`WriteStateAsync`); declare the flag beside `_ephemeral` and clear it in the same three
places.
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~WebDocumentTool`.

**Step 7 — Catalog + registration.** Key/descriptor/`ToolNameToKey` entry in `ToolCatalog`
(appended last); `AIFunctionFactory.Create` registration in `BuildAgent` on both
capability branches; named client with the guarded handler,
`RemoveAllResilienceHandlers()` and an explicit timeout in `Program.cs`; port registration
in **both** hosts (DevHost gets `InMemoryWebDocumentFetcher`); `TestServiceRegistration.cs`
fake; `ProjectLayoutTests.AgentGrainParts()` row **and** its "twelve" docstring.
If `Program.cs` tips S3776, extract a registration extension in the
`DocumentIndexRegistration` / `HindsightRegistration` idiom — never a ternary.
*Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~ToolCatalog|FullyQualifiedName~ToolSelectionApi|FullyQualifiedName~ProjectLayout"`.

**Step 8 — Docs.** Every entry under "Modified — docs", plus `SKILL.md`. Then `/sync-docs`.
*Verify:* `dotnet test Agent42.slnx` + `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | Guard blocks loopback/private/link-local at connect time | `Guard_BlocksPrivateRanges` | `PrivateAddressGuardTests.cs` |
| AC2 | A redirect to `169.254.169.254` is blocked on the redirect hop | `Fetch_BlocksRedirectToImds` | `GuardedDocumentFetcherTests.cs` |
| AC3 | IPv4-mapped / NAT64 IPv6 forms do not bypass the guard | `Guard_BlocksMappedAndNat64Forms` | `PrivateAddressGuardTests.cs` |
| AC4 | `text/html` is rejected even when the URL ends `.pdf` | `Fetch_RejectsHtmlServedForPdfUrl` | `GuardedDocumentFetcherTests.cs` |
| AC5 | Media type is taken from the response, not the URL | `Fetch_ResolvesMediaTypeFromResponseHeader` | `GuardedDocumentFetcherTests.cs` |
| AC6 | Download is capped while streaming, not after buffering | `Fetch_StopsReadingAtMaxDownloadBytes` | `GuardedDocumentFetcherTests.cs` |
| AC7 | A slow-drip body is abandoned at the deadline | `Fetch_AbandonsSlowBodyAtDeadline` | `GuardedDocumentFetcherTests.cs` |
| AC8 | A filename derived from a hostile URL path is sanitized and length-capped | `Fetch_SanitizesFileNameFromUrlPath` | `GuardedDocumentFetcherTests.cs` |
| AC9 | A URL not in `FetchableUrls` is refused | `ReadWebDocument_RefusesUrlOutsideAllowlist` | `WebDocumentToolTests.cs` |
| AC10 | An ephemeral turn refuses to ingest and writes nothing | `ReadWebDocument_RefusedOnEphemeralTurn` | `WebDocumentToolTests.cs` |
| AC11 | No embedding model → message to the model, no throw | `ReadWebDocument_NoEmbeddingModel_ReturnsMessage` | `WebDocumentToolTests.cs` |
| AC12 | Dimension mismatch → message to the model, no throw, no upsert | `ReadWebDocument_DimensionMismatch_ReturnsMessage` | `WebDocumentToolTests.cs` |
| AC13 | A fetch failure returns a message, never bricks the turn | `ReadWebDocument_FetchFailure_DoesNotThrow` | `WebDocumentToolTests.cs` |
| AC14 | Excerpts are ranked in-process and never read back from the index | `ReadWebDocument_RanksWithoutQueryingTheIndex` (index double asserts zero `SearchAsync` calls) | `WebDocumentToolTests.cs` |
| AC15 | Instructions label a web-fetched document as untrusted, never "the user uploaded" | `WebDocument_IsNotDescribedAsUserUploaded` | `WebDocumentToolTests.cs` |
| AC16 | RAG provider is attached on the next model call after a **chat** turn that ingested | `ReadWebDocument_AttachesRetrievalAfterChatTurn` | `WebDocumentToolTests.cs` |
| AC17 | …and after a **reminder** turn that ingested | `ReadWebDocument_AttachesRetrievalAfterReminderTurn` | `WebDocumentToolTests.cs` |
| AC18 | The named client sets a timeout and removes the resilience pipeline | source-text assertion over `Program.cs` | `WebDocumentToolTests.cs` |
| AC19 | `Program.cs` wires the guard into the client's primary handler | source-text assertion naming `PrivateAddressGuard.IsPubliclyRoutable` | `WebDocumentToolTests.cs` |
| AC20 | Chunk cap bounds embedding work and reports truncation | `Ingest_RespectsMaxChunksAndReportsTruncation` | `DocumentIngestionServiceTests.cs` |
| AC21 | Pre-`[Id(11)]` Cosmos state still deserializes | `DocumentWrittenBeforeWebDocumentNames_StillDeserializes` | `GrainStorageSerializerTests.cs` |
| AC22 | Tool return stays inside the session budget | extend `ToolResults_DoNotBlowTheSessionSizeBudget` | `AgentGrainTests.cs` |
| AC23 | New key appended last; ordering guards unchanged | existing `Sanitize_OrdersStaticFirst_*` pass **unmodified** | `ToolCatalogTests.cs` |
| AC24 | `GET /api/tools` exposes the new descriptor with a description | `Tools_Catalog_ReturnsSelectableKeys` (row added at `:57`) | `ToolSelectionApiTests.cs` |
| AC25 | Deselecting the key removes the tool from the built agent | `ReadWebDocument_AbsentWhenDeselected` | `WebDocumentToolTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time — do not re-derive from memory.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — **`error`, so a violation fails `dotnet build`** |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI package-freshness job |
| `add_policy` | latest-stable | resolved at add time, never from memory — **but this plan adds no package** |

Branch: `feature/*` off `main`. Never commit to `main` (`branches.protected`).

## Out of scope

- **Removing context.dev / rewriting `fetch_web_page`.** Explicitly untouched. This MR
  leaves the restored guard behind for that work to reuse.
- **HTML pages.** This path rejects them; `fetch_web_page` keeps serving them.
- **A user-facing "paste a URL to ingest" endpoint.** `IDocumentIngestor` makes
  `POST /api/conversations/{id}/documents/from-url` (behind Easy Auth) a natural follow-up.
  Note it in the ADR; do not build it here.
- **Restoring `AngleSharp`** or any other package.
- **Following links**, depth > 1, or any allowlist exemption.
- **A delete path for retained Hindsight memories.** Out of scope here as in ADR 0056 —
  which is precisely why the ADR must state the residual rather than claim boundedness.
- **A per-document filter on `IDocumentIndex`.** Avoided by ranking in-process.
- **Backfilling the new tool key** into stored selections. It is OFF for explicit savers.

## Review

A `critic` pass ran against this spec at `main @ 8a4210a` and returned four HIGH, eight
MEDIUM and six LOW findings. Its `path:line` citations were spot-checked and hold.

**All findings were accepted and folded in.** Summary of the resulting changes:

- **H1** (poisoning claimed as conversation-bounded; memory makes it unbounded) → the
  boundedness claim is removed, ADR 0056 is now in scope for a fourth injection-vector
  entry, and the honest residual is stated.
- **H2** (`ReceiveReminder` does not drain `ChatCoreAsync`, so the deferred rebuild is lost
  on the reminder path) → a second consume site plus AC17. Verified at `AgentGrain.cs:884`.
- **H3** (`WithDocumentAwareness` would assert user provenance for attacker content; the
  filename is attacker-influenced prompt text) → trailing `[Id(11)] WebDocumentNames`,
  provenance-aware instructions, hard filename sanitization, AC8 + AC15 + AC21.
- **H4** (unrecorded ADR 0008 collision) → ADR 0066 must argue it explicitly; wording
  supplied in "Binding rules".
- **M1** (inline search unspecified; prod-only Cosmos path; no per-document filter) → the
  search now ranks **in-process** over the just-embedded chunks, which removes the
  prod-only dependency, the cross-document contamination and the `IDocumentIndex` change
  all at once. AC14 asserts the index is never queried.
- **M2** (no timeout; `ResponseHeadersRead` means `HttpClient.Timeout` does not bound the
  body) → explicit timeout plus a separate download deadline, AC7.
- **M3** (AC18 not implementable as a runtime test; nothing asserts the guard is *wired*)
  → AC18 becomes a source-text assertion and AC19 is added for the wiring.
- **M4** (doc debt) → four further doc targets added, including CLAUDE.md's now-wrong ADR
  0027 bullet.
- **M5** (`ProjectLayoutTests` not listed) → added. **One correction to the finding:** there
  are **12** grain partials, not 13 — the 13th file is `AgentGrainState.cs`, not a partial.
  So the "twelve" docstring is currently *correct* and CLAUDE.md's "13 partial files" is
  *already wrong*; both are fixed in Step 8.
- **M6** (`Skills/websearch/SKILL.md`) → added to scope.
- **M7** (no offline fetcher, so DevHost could not exercise it) → `InMemoryWebDocumentFetcher`
  added, exemplar named.
- **M8** (`/api/agent-run` reachability change unstated) → added to the threat model.
- **L1–L6** → hosted-tools branch specified; truncation reported (Step 4); AC12 added for
  dimension mismatch; `Program.cs` S3776 headroom budgeted in Step 7; AC22 extends the
  existing budget guard rather than duplicating it; the stale "Step 4 critic pass"
  self-reference removed.

**Nothing was rejected.**

## Execution notes — where the implementation diverged from the plan

Executed 2026-09-04 on `feature/web-document-rag-tool`, off `main @ 8a4210a` (the spec's
`planned-against:` ref exactly — zero drift). All 8 steps done, all 25 ACs implemented with
tests. Full suite green (1189 passed), `dotnet format --verify-no-changes` clean,
`check-doc-paths` clean, no vulnerable packages, no package added.

### Divergences from the plan as written

1. **`DocumentIngestionService.IngestAsync` changed its RETURN TYPE, so its two existing call
   sites were touched after all.** The plan said "have it return the embedded chunks alongside
   the count, so both existing call sites compile untouched"; those two things are not
   simultaneously achievable without a duplicate method or an implicit conversion, both worse
   than the alternative. It now returns `IngestedDocument` and the two call sites gained
   `.ChunkCount` — one word each.
2. **One extra file: `src/Agent42.Agents/Documents/VectorSimilarity.cs`.** The in-process ranking
   needs cosine similarity, which already existed as a private method on
   `InMemoryDocumentIndex`. Extracted rather than duplicated, because the inline ranking and the
   next turn's retrieval must order chunks identically. `InMemoryDocumentIndex` therefore appears
   in the diff — a behaviour-preserving extraction, not the interface change the plan ruled out.
3. **`GuardedDocumentFetcher` takes an optional `downloadDeadline`.** Purely so AC7 can prove the
   slow-body abandonment in 200 ms rather than the production 30 s. Production passes nothing.
4. **`WebDocumentLimits` owns `SanitizeFileName`.** The plan put filename derivation in the
   fetcher (Step 3). It has to be shared by *both* `IWebDocumentFetcher` implementations, and the
   only assembly both can reach is `Agent42.Agents` — so it lives beside the limits it is capped by.
5. **A new test-only silo, `WebDocumentProbeSiloConfigurator`.** AC11/AC12/AC14 need to observe
   things the standard fakes cannot express (index queries counted, no embedding model, a
   fixed-dimension store). It follows the `NoModelSiloConfigurator` precedent and is used through
   a per-test cluster, not a fixture.
6. **`FakeChatClient.LastUserIndex` now skips `AIContextProvider`-attributed messages.** Not
   planned, and it was a real latent defect: a `TextSearchProvider` injects its excerpts as a
   `ChatRole.User` message, so in any conversation holding a document the fake was keying its
   tool selection off the *excerpt block* instead of the prompt — and silently stopped calling
   tools the moment retrieval switched on. `ContextEcho` is now trimmed to 400 chars for a
   related reason (see divergence 11).

### Changes made in response to the `critic` pass on the diff

The critic returned 1 HIGH, 4 MEDIUM and 4 LOW. All were accepted; none was disputed.

7. **(HIGH 1) Provenance was enforced in the instructions only.** The always-on provider injects
   web-fetched and uploaded excerpts through the *same* channel, whose only per-excerpt metadata
   is `SourceName` — so a fetched `/handbook.pdf` cited identically to an uploaded `handbook.pdf`
   and **collided with it**. The derived name now carries a `(web: {host})` qualifier and is built
   from the **whole path**, so citations are self-describing and near-unique per URL. The residual
   — that this is a naming convention rather than a typed field on `DocumentChunk` — is stated in
   ADR 0066 and the gotcha.
8. **(MEDIUM 2) `PurgeAsync` did not clear the new lists.** A real bug: purge deactivates *on
   idle*, so a request in that window met a grain whose chunks were gone but whose instructions
   still named attacker-influenced filenames and still gated retrieval. Fixed beside the existing
   `DocumentNames.Clear()`, with a test that asserts it on the same activation.
9. **(MEDIUM 3) Redirects are followed and the docs denied it.** `AllowAutoRedirect` is on (5
   hops, each address-guarded), so bytes can come from a host the allowlist never saw. The media
   type and file name are now resolved from the **final** URI so hop N's content is never
   presented under the allowlisted URL's name, and the residual is recorded in ADR 0066, the
   gotcha and CLAUDE.md rather than denied.
10. **(MEDIUM 4) No idempotence and no cap.** Chunk ids are fresh `Guid`s, so re-reading a URL
    *duplicated* its chunks — and the trigger is model-driven, reachable from `/api/agent-run` and
    from a **repeating reminder**. Added a trailing `[Id(12)] WebDocumentUrls` (exact URL identity,
    deliberately not the length-capped name) that short-circuits a re-read before the fetch, plus
    `WebDocumentLimits.MaxWebDocuments` bounding the list oldest-first — the
    `AgentGrain.MaxFetchableUrls` shape.
11. **(MEDIUM 5) `MaxReturnTextLength` is arithmetically unreachable** at `MaxExcerpts` 5 ×
    `ChunkSize` 1000, so the cap-sized fixture's stated mutation property was false. The constant
    is kept as the invariant that survives a change to either number, but now says plainly that it
    does not bind today, and the fixture's docstring was corrected to claim only what it proves.
    Separately, the budget test's document arm now reads a **different URL per round** (idempotence
    would otherwise have it measure one ingest while claiming five).
12. **(LOW 6) `[Id(11)]`/`[Id(12)]` had no forward round-trip coverage.** The committed binary
    golden predates them and is deliberately never regenerated, so a self-round-trip through the
    configured Cosmos serializer was added instead, with different values in each list — the
    failure it guards is the two swapping.
13. **(LOW 7) `ProbeDocumentIndex.Reset()` was dead code** — the shape ADR 0055 warns about.
    Deleted, with a note saying why there is none.
14. **(LOW 9) The content-type gate admitted any `text/*`** (`DocumentMediaTypes.IsSupported`, a
    rule written for browser uploads where a human picked the file). Replaced with an explicit
    allowlist, matching the discipline the rest of the feature uses.

### Deliberately not done

- **(LOW 8) No DevHost browser leg.** Recorded under *Unverified* in ADR 0066 rather than
  glossed: this change touches no frontend file, `wwwroot/` is untouched, no `sw.js` `CACHE` bump
  was due, and the one user-visible effect — a new switch row in the ADR 0065 pickers — is
  rendered from `GET /api/tools` by existing code and covered over the wire by
  `ToolSelectionApiTests`. Nobody has *looked* at it.
- Everything under the plan's own "Out of scope" section, unchanged.

### Mutation checks actually run

- Commenting out `ReceiveReminder`'s deferred-rebuild call fails
  `ReadWebDocument_AttachesRetrievalAfterReminderTurn` **and only that test** (AC17 — the consume
  site the plan flags as the silent one).
- Removing `ConfigurePrimaryHttpMessageHandler` from `Program.cs` fails
  `ProgramCs_WiresTheGuardIntoTheDocumentClientsPrimaryHandler` (AC19 — a guard built, tested and
  never wired).
