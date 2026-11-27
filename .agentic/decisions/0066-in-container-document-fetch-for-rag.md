---
id: 0066
date: 2026-09-04
status: accepted
tags: [rag, documents, web, ssrf, security, prompt-injection, tools, no-migration]
supersedes: []
extends: [0007, 0008, 0021]
amends: [0011, 0027, 0056]
amended_by: [0067, 0098, 0106, 0125, 0150]
---

# `read_web_document`: fetch a web-hosted document in-container and ingest it into conversation RAG

> **Amended by [ADR 0125](0125-remove-fetch-web-page-and-context-dev.md) (2026-11-14).** `fetch_web_page` is gone, so `read_web_document` is the only model-invoked web fetch and still rejects HTML. The "two fetch paths" comparison and the "context.dev is being retired" premise below are now history: the retirement happened.

> **Amended by [ADR 0106](0106-agentgrain-services-and-turn-context.md) (2026-10-19): the "a document was ingested this turn" flag is `TurnContext.WebDocumentIngested` on the run's own context.** The consume sites read it after the run, and there is no field to clear.

> **Amends [ADR 0027](0027-hosted-page-scrape-via-context-dev.md) and, through it,
> [ADR 0011](0011-web-search-self-hosted-searxng-sidecar.md).** ADR 0027 is **not superseded** — its
> decision governs `fetch_web_page`, which stays on context.dev, untouched by this MR. What changes is
> that the repo again contains a `PrivateAddressGuard` and an in-container outbound fetch, on a
> **second, document-only path**. Also **amends [ADR 0056](0056-hindsight-long-term-memory.md)**: this
> is a fourth (and the largest) member of its durable-prompt-injection list.
>
> **Amended by [ADR 0067](0067-mcp-results-seed-the-fetch-allowlist.md) (2026-09-05) — the THREAT MODEL
> changes, the design does not.** ADR 0067 lets a URL from a *seeding* MCP server's tool result enter the
> conversation's fetchable-URL allowlist, so `read_web_document` can now be handed a URL **authored on a
> scraped page** rather than one a search engine or the user produced verbatim. Nothing in this ADR's
> mechanism moves — the guard, the allowlist check and the whole-URL rule are unchanged — but the second
> consequence below (an outbound connection from our managed-identity-bearing container to a URL someone
> else chose) becomes reachable from the MODEL, not only from an `excludedPaths` API-key holder.

## Context

`fetch_web_page` returns at most `WebToolLimits.MaxPageTextLength` (8 KB) of scraped **markdown**. That
is right for a web page and useless for a document: a 60-page PDF found in a `web_search` result, or a
spec the user pastes a link to, is either truncated to its first 8 KB or — far more often — not
retrievable at all, because context.dev returns rendered page markdown and **cannot deliver raw
document bytes**.

The conversation already has the machinery to do this properly. ADR 0007 built extract → chunk → embed →
upsert; ADR 0008 attached an always-on `TextSearchProvider` that injects the top excerpts before each
reply. Both are reached today only through a **browser upload**. The only thing missing between "the
model found a PDF" and "the model can answer from that PDF" is a way to get the bytes.

Two further facts shaped the decision rather than merely surrounding it:

1. **context.dev is being retired on cost grounds**, in the near future. Anything built on it is
   temporary by construction.
2. **This feature needs bytes.** No configuration of the hosted scrape API produces them.

## Decision

A new model-invoked tool, **`read_web_document`**, that fetches one document URL **from our own
container**, ingests it into the conversation's document index, and returns budget-capped excerpts
answering a model-supplied `question`.

It is a **second path**, deliberately disjoint from `fetch_web_page`:

| | `fetch_web_page` (ADR 0027) | `read_web_document` (this ADR) |
|---|---|---|
| Who dials the URL | context.dev | **our container** |
| Accepts | HTML / markdown pages | PDF / Office / text — **`text/html` is rejected** |
| SSRF guard | none needed (not our socket) | **`PrivateAddressGuard` at connect time** |
| Returns | up to 8 KB of page text | excerpts + a durable RAG index entry |
| Allowlist | root URL, whole-URL match | root URL, whole-URL match (**identical**) |
| Link following | depth-1, opt-in, allowlist-exempt | **none** (but redirects are followed — below) |

`fetch_web_page`, `ContextWebPageFetcher` and the context.dev client are **not touched**. That is what
lets this ship independently of the context.dev removal — and it leaves the restored guard behind for
that work to reuse.

### The guard is restored, not reinvented

`src/Agent42.Backend/Web/PrivateAddressGuard.cs` comes back verbatim from the commit that deleted it,
with three properties that must survive any future edit:

- it hangs off `SocketsHttpHandler.ConnectCallback`, so **every redirect hop** is checked;
- it **resolves DNS itself** and dials the *vetted IPs*, never the hostname — a resolve-then-connect-by-name
  guard is TOCTOU-broken against DNS rebinding;
- it judges IPv4-mapped, IPv4-compatible and NAT64 IPv6 forms as the IPv4 address they carry, so
  `::ffff:169.254.169.254` and `64:ff9b::a9fe:a9fe` cannot smuggle IMDS past a v6-only check.

`WebPageFetcher`, its HTML-to-text pass and the **`AngleSharp` package are NOT restored**. This path
rejects HTML, so there is no HTML to parse. **This MR adds no package reference at all.**

### Re-adopting an alternative ADR 0027 rejected

ADR 0027's *Alternatives considered* records, and rejects, "key-present / key-absent fallback to a direct
fetch … it would keep the SSRF sink (and its whole guard) alive for the fallback, doubling the surface
for no benefit. Full replace, one path."

**The user was asked and chose to re-adopt that shape.** The premise that changed is the clause "for no
benefit": the rejected fallback was a *duplicate* of a path that already worked, whereas this path does
something context.dev **cannot do at all**, and context.dev is on its way out. The cost the rejection
named is real and is accepted in full: there are now **two fetch paths and two mental models**, and a
`PrivateAddressGuard` exists again that a reader must know applies to one of them and not the other.
(Precedent for the shape of this move: ADR 0063 re-adopting an alternative ADR 0053 had rejected twice.)

Consequently `.agentic/gotchas.md`'s "`fetch_web_page` is NO LONGER an SSRF sink" entry is **scoped, not
deleted**. Its bold instruction — *do not reintroduce a `PrivateAddressGuard` here* — remains correct for
`fetch_web_page`.

### The collision with ADR 0008, stated rather than glossed

ADR 0008 lists under *Alternatives* and **rejects** "`TextSearchProvider` in `OnDemandFunctionCalling`
mode — exposes retrieval as a model-invoked tool". The inline top-K this tool returns **is** a
model-invoked retrieval call. That is the shape ADR 0008 refused, and it is being taken deliberately.

The justification is narrow and does not generalise: **it is additive to always-on retrieval, not a
replacement.** From the *next* model call onward the ADR 0008 provider is attached and does its usual
work; the inline excerpts exist only because `BuildAgent` decides the provider set **before** the run
starts, so a document ingested *during* a run cannot reach that run any other way. Nothing here makes
retrieval optional or model-discretionary in a conversation that already has documents.

**Do not write "following ADR 0008" about this tool.**

### Why the ranking happens in-process

The excerpts returned inline are ranked **in memory, over the chunks just embedded by this ingest**, by
cosine similarity against the embedded `question`. `IDocumentIndex.SearchAsync` is **not** called. Three
reasons, each independently sufficient:

1. The Cosmos vector path is **prod-only and unverified by CI** (ADR 0007). A read-back would be green
   offline against the immediately-consistent `InMemoryDocumentIndex` and could return nothing in
   production — the exact shape of ADR 0061's missing `compositeIndexes` entry, which shipped broken in
   prod with every offline test passing.
2. `IDocumentIndex.SearchAsync(ownerKey, …)` has **no per-document filter**, so a read-back would mix in
   excerpts from the conversation's *other* documents and present them as this URL's content.
3. It keeps `IDocumentIndex` and both implementations out of the diff.

### Provenance: a web-fetched document is not a user upload

`WithDocumentAwareness` tells the model *"The user has uploaded the following document(s)…"*. Pushing an
attacker-authored document into `DocumentNames` would assert **user provenance** — the strongest trust
signal in the prompt — for content the model must treat as data. That is the exact inverse of ADR 0027's
`"UNTRUSTED: …"` labelling.

So `AgentGrainState` gains a **trailing `[Id(11)] List<string> WebDocumentNames`** (the trailing-optional
idiom of ADR 0016 `[Id(7)]` and ADR 0045 `[Id(5)]` — **zero migration**), and the instructions render two
sentences: uploaded documents as before, web-fetched ones under an explicit untrusted-provenance
sentence. The RAG provider gate widens to `DocumentNames.Count + WebDocumentNames.Count > 0`.

The **filename is derived from the URL path and is therefore attacker-influenced text inside the system
prompt and inside every citation `SourceName`.** It is sanitized hard: an allowlisted character set
(`[A-Za-z0-9.-_ ]`), percent-decoding *before* the path is flattened, relative segments dropped, and a
64-character cap.

**The instructions are only half of the provenance control, and the other half is the name itself.** The
always-on provider serves web-fetched and uploaded excerpts through the *same* channel — a
`ChatRole.User` message whose only per-excerpt metadata is `SourceName`, since `DocumentChunk` carries no
provenance field. So a fetched `/handbook.pdf` would otherwise produce a citation indistinguishable from
an uploaded `handbook.pdf` in the same conversation, and would collide with it outright. The derived name
therefore carries a **`(web: {host})` qualifier** and is built from the **whole path**, so it is
self-describing and near-unique per URL. The honest residual: this is a *naming* convention, not a typed
field — a future change that wants structured provenance should add it to `DocumentChunk`, not to the
string.

### The rebuild is deferred, and there are TWO consume sites

The tool must **not** call `RegisterDocumentAsync`: that does snapshot → `BuildAgent` → restore, i.e. it
swaps `_session` out from under a framework run that is iterating it. Every existing call site is an API
endpoint *outside* a turn. Instead the tool sets a deferred-rebuild flag and the rebuild happens after
the run completes, at **both** places a run completes:

- `ChatCoreAsync`, after `PersistTurnUnlessEphemeralAsync()` — the live chat path, and `/api/agent-run`,
  which drains it.
- `ReceiveReminder`, after its own `WriteStateAsync` — it does **not** drain `ChatCoreAsync`. Without a
  consume site here a reminder turn that ingested would persist the name list and lose the rebuild,
  leaving retrieval detached for the rest of the activation while the instructions claim a document
  exists.

State is written **immediately** on a successful ingest rather than at the turn tail: the Cosmos chunks
are already durable at that point, and an unpersisted name list would orphan them if the stream were
abandoned.

### Redirects are followed, and that is a small residual of the same kind as ADR 0027's

`AllowAutoRedirect = true`, `MaxAutomaticRedirections = 5`. Every hop goes through the same
`ConnectCallback`, so the **SSRF** guard covers all of them — that part is strictly good. But it means the
bytes may be served by a URL that was never on the allowlist, which is the same *kind* of exemption ADR
0027 records for followed links, in a much smaller form: it is not opt-in, it is not model-directed, and
the redirect is chosen by a host the allowlisted URL already pointed at.

It is mitigated rather than closed: the media type **and the file name are resolved from the final URI**,
not the requested one, so hop 5's content is never presented to the model under the allowlisted URL's
name. (`WebDocument.Url` still reports the URL the model asked for, so the conversation records what was
allowlisted.) Refusing redirects outright was considered and rejected — a very large share of real
document links are CDN or shortener redirects, and the feature would mostly not work.

### Reading the same URL twice, and how many documents a conversation may hold

`CosmosDocumentIndex` gives every chunk a fresh `Guid` id, so a second read **duplicates** a document's
chunks rather than overwriting them. That is unremarkable on the browser-upload path, where a human
decides to upload again; here the trigger is the **model**, reachable from `/api/agent-run` and from a
**repeating reminder** — an hourly reminder could re-ingest the same PDF forever.

So a read of a URL already in `WebDocumentUrls` **short-circuits before the fetch** and tells the model the
document is already available through automatic retrieval. Identity is the normalized **URL**, not the
derived name: the name is length-capped and could collide, and a false positive there would silently
refuse to read a genuinely different document.

The name list is also **bounded** (`MaxWebDocuments`, oldest dropped first), for the reason
`AgentGrain.MaxFetchableUrls` is: it is rendered into the instructions on every turn and lives in the same
2 MB item. Two residuals, both accepted: an evicted URL can be read again and its chunks will duplicate;
and eviction drops the name while the chunks stay indexed until the conversation is deleted.

> **Amended by [0098](0098-read-document-sequential-reading.md):** re-ingesting a file name now replaces its
> chunks, so a re-read no longer duplicates them. The short-circuit stays, to avoid a needless fetch and re-embed.

### Limits

`WebDocumentLimits`: **10 MB** download cap (enforced *while streaming* — a `Content-Length` pre-check
**and** a bounded read loop, so a 500 MB body is never buffered), a **separate download deadline**
(`HttpClient.Timeout` does not bound a body read under `ResponseHeadersRead`), a **chunk cap** so the
grain stall is bounded by embedding work and not only by bytes, and the return budget (excerpt count and
total characters) that ADR 0004's 2 MB session requires.

## Consequences

- **This is a fourth, and the largest, member of ADR 0056's durable-injection list, and the poisoning is
  UNBOUNDED — not conversation-bounded.** `memory` is in `AllSelectableToolKeys`, so it is ON for every
  `null == all` conversation, and retain runs on every non-incognito turn. A hostile document ingested
  once can therefore become a **durable memory re-injected into every future conversation of that user**.
  Deleting the conversation clears the Cosmos chunks and the name list; it does **not** clear the
  Hindsight bank, which has **no delete path** (out of scope here as in ADR 0056). Do not describe this
  feature as bounded to the conversation.
- **`/api/agent-run` reachability changed (ADR 0022).** That endpoint is on `excludedPaths` — publicly
  reachable with an API key — and drains `ChatCoreAsync`, so `WebTools.RememberFromUserText` whitelists
  URLs written in its prompt. An API key holder can therefore now cause **our container**, which holds
  the managed identity, to open an outbound connection to a URL of their choosing. `PrivateAddressGuard`
  is precisely the right control and is why it is restored; the point recorded here is that the
  *reachability* changed, not that it is unmitigated.
- **A second door onto that same property was opened by [ADR 0067](0067-mcp-results-seed-the-fetch-allowlist.md).**
  A URL harvested from a seeding MCP server's tool result is allowlisted too, so the model itself — not
  only an API-key holder — can now cause this container to dial a URL that neither a search engine nor
  the user produced. `PrivateAddressGuard` remains the right control and remains no defence against the
  channel that actually matters (it permits every public address by construction); ADR 0067 records that
  as the sharpest of its consequences.
- **Two fetch paths now exist with different security models.** Accepted, above.
- **The new `read_web_document` catalog key lands OFF for anyone who has ever pressed Save** (ADR 0021's
  standing consequence: a stored selection is an explicit list). No migration, by design. The remedy for
  regular chats is one press of Save in Preferences → Tools; already-bound agent chats have no in-UI
  remedy, as with `memory` (ADR 0057).
- **On a hosted-tools model the tool is nearly inert.** It registers on both capability branches — it is
  document ingestion, which no hosted tool provides, so it is not part of ADR 0015's swap. But nothing
  calls `FetchableUrlList.Remember` there (the native `HostedWebSearchTool` cannot), so `FetchableUrls` is
  user-text-only on that branch and the tool is reachable only for URLs the **user** typed.
- **`PdfLock` is process-wide.** `DocumentTextExtractor` serializes PDF extraction because PDFium is not
  thread-safe, and `infra/main.bicep` pins a single replica — so ingesting a large PDF blocks every other
  PDF extraction in the process for its duration. Accepted; the chunk and byte caps bound it.
- **ADR 0061 is unaffected.** `ProjectForSearch` runs over `GetHistoryAsync()` turns, not raw tool
  messages, so the inline excerpts do not themselves enter the `chat-messages` index — only an assistant
  reply quoting them does.
- Egress is otherwise unchanged: the document's text reaches whatever model the conversation is on, which
  is ADR 0013's standing property, not a new one.

## Alternatives considered

- **Extend `fetch_web_page` to handle documents.** Rejected: it runs on context.dev, which cannot return
  bytes, and coupling this to a component being retired would make both harder to remove.
- **Read back the top-K from `IDocumentIndex` after ingest.** Rejected — see "Why the ranking happens
  in-process"; the prod-only Cosmos path and the absent per-document filter each rule it out.
- **Call `RegisterDocumentAsync` from the tool.** Rejected: it rebuilds the agent mid-run and swaps
  `_session` out from under the iterating framework run.
- **Add a per-document filter to `IDocumentIndex`.** Rejected: it changes the prod Cosmos adapter for a
  problem in-process ranking removes entirely.
- **Push web-fetched documents into `DocumentNames`.** Rejected: it asserts user provenance for
  attacker-authored content.
- **Follow links, or fetch depth > 1.** Out of scope. ADR 0027's allowlist exemption for followed links
  is an accepted residual on *that* path; it is not extended to a path that ingests whole documents
  durably.
- **A user-facing `POST /api/conversations/{id}/documents/from-url`.** A natural follow-up that
  `IDocumentIngestor` makes cheap — behind Easy Auth, with the user rather than the model choosing the
  URL. Deliberately **not built here**; noted so the next person does not re-derive it.

## Unverified

- **The Cosmos vector upsert on this path is prod-only**, exactly as `CosmosDocumentIndex` has been since
  ADR 0007. Offline and CI exercise `InMemoryDocumentIndex`. The in-process ranking is what keeps the
  *returned* excerpts independent of that gap; the durable index entry is not.
- **No test dials the public internet.** The guard is proven at connect time against a loopback test
  server with a swapped *policy*; the fetch of a real remote PDF happens first in production.
- **No DevHost browser leg was run.** Unlike ADRs 0058/0061/0064/0065 this change touches no frontend
  file at all — `wwwroot/` is untouched and no `sw.js` `CACHE` bump was due. The only user-visible effect
  is one extra switch row in the three ADR 0065 tool pickers, which is rendered from `GET /api/tools`
  by the existing `renderToolChecks`; `ToolSelectionApiTests` covers the row and its description over the
  wire. The residual is that nobody has *looked* at it.
