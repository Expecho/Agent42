---
id: 0155
date: 2026-11-28
status: accepted
tags: [web-search, tools, egress, reliability, config, infra, no-migration]
supersedes: []
amends: [0011]
extends: [0013, 0056, 0057, 0128]
amended_by: []
---

# Web search: an optional provider fallback chain in front of SearXNG

## Context
`web_search` ran only on the SearXNG sidecar (ADR 0011), which scrapes Google, Bing and DuckDuckGo from one datacenter IP
(ADR 0060). Those engines throttle and block such an address, so search degraded to "no results" with no recourse. ADR 0011
rejected Tavily, Brave and Bing APIs as "an API key, a per-query bill, and queries attributable to our account" (a
cost-and-coupling argument, not a privacy one).

## Decision
1. **A chain, not a replacement.** `FallbackWebSearch : IWebSearch` over an ordered list of providers: **Tavily, Firecrawl**,
   then the SearXNG sidecar. The first success wins. `WebSearch:Providers` (comma-separated, blank = `tavily,firecrawl,searxng`)
   sets the order; an unknown name is ignored with a startup warning; `searxng` is always last.
2. **Keys are operator-level and optional** (the `GOOGLE_MAPS_API_KEY` / `HINDSIGHT_API_KEY` shape, ADR 0056/0128): an unset
   value is an empty string, which means the provider is skipped and has no HTTP client. **With no key, `IWebSearch` is the bare
   `SearxngWebSearch`**, so today's behaviour is unchanged.
3. **Failure classification and cooldowns** (in-memory, per process; single replica is load-bearing, rule 12). A failure benches
   the provider: Transient (timeout, network, 5xx) 30 s; RateLimited (429, `Retry-After` honoured, capped at 5 min, default 1 min);
   Exhausted (out of credits) 1 h; Unauthorized (401/403) 6 h and an Error log. Caller cancellation is not a failure.
4. **Empty results are a success** and end the chain. A 2xx *without* the results container (schema drift, `success:false`) is
   not an empty answer: it is a Transient failure. A budget that expires while a provider is in flight benches that provider as
   Transient, because it was slow. Known trade-off: a keyed provider answering with zero results suppresses the
   SearXNG attempt for that query.
5. **SearXNG is never benched.** It is the keyless safety net, so there is no "everything benched" state. It is wrapped by an
   adapter and left unclassified (its 403 means "JSON format off", not a bad key, gotchas).
6. **Time bounds** (the grain is non-reentrant): per attempt 4 s, hosted phase 8 s in total, then SearXNG under its own 10 s client
   timeout; worst case ~18 s, only while nothing is benched. No retries inside a provider: the chain is the retry. Spending the
   hosted budget falls through to SearXNG, not to an error.
7. **The final error is curated.** `WebTools` returns the exception message to the model, so the message holds provider names and
   failure kinds only, never a body, URL, key or query.
8. **Cheapest mode, key in a header.** No raw content, no scraping, no generated answer. Page reading stays removed (ADR 0125).

## Vendor facts (read from the vendors' docs 2026-11-28)
| | Tavily | Firecrawl |
|---|---|---|
| Endpoint | `POST https://api.tavily.com/search` | `POST https://api.firecrawl.dev/v2/search` |
| Auth | `Authorization: Bearer <key>` (docs.tavily.com/documentation/api-headers) | `Authorization: Bearer fc-<key>` (docs.firecrawl.dev/authentication) |
| Cheapest body | `query`, `search_depth: basic`, `include_raw_content: false`, `include_answer: false`, `max_results` | `query`, `limit`; no `scrapeOptions` (docs.firecrawl.dev/features/search) |
| Results | `results[].title / url / content` | `data.web[].title / url / description` |
| Cost | 1 credit per basic request (docs.tavily.com/documentation/api-credits) | 2 credits per 10 results (docs.firecrawl.dev/features/search, "Cost Implications") |
| Out of credits | 432 (key/plan limit), 433 (pay-as-you-go limit), search page responses | 402 "Insufficient credits" (docs.firecrawl.dev/api-reference/errors) |
| Rate limit | 429, `Retry-After` when present (docs.tavily.com/documentation/rate-limits) | 429, `Retry-After` when available (errors page) |
| Bad key | 401 | 401 |
| Free tier | 1,000 credits a month | 500 searches (pricing page) |

Not documented and so not relied on: that 432 means credit exhaustion specifically (it is the closest match), a Firecrawl 402 body
marker, whether Firecrawl's default `highlights: true` changes the cost (left at its default).

## Serper is not in this MR
Serper was wanted as a third provider. Its API documentation page returned 404 and the homepage does not state the auth header,
the cost per call or the failure codes, so the Step 0 gate dropped it: a provider whose failure signals cannot be established
from its docs is not shipped. The chain takes a new `IWebSearchProvider` and one `WebSearchChainRegistration` entry, so adding it
later needs only verified facts.

## Consequences
- **An ADR 0013 egress extension.** Setting a key sends web search queries to that vendor, which is a new third party that can
  attribute them to the operator's account. Opt-in by operator key, absent by default. `privacy.html` names them.
- Changes ADR 0011's rationale: it rejected a keyed API for cost and coupling. The reason to reconsider is reliability from one
  datacenter IP; keys stay optional, free tiers cover light use, and SearXNG is kept as the fallback that never goes away.
- The fleet's `web_search` (`FleetWebTools`) uses the chain through DI with no code change.
- Config health shows per-key presence and a free `probe:websearch-chain` that reads the cooldown snapshot and calls no vendor
  (a live probe would spend credits).
- Responses-API models still use the provider's hosted search (ADR 0015) and never reach the chain.

## Alternatives considered
- **Round-robin across providers to spread credits.** Rejected: priority order is simpler to reason about; the chain already
  moves on when a provider runs dry.
- **Per-user keys** (the ADR 0018 overlay). Out of scope.
- **A live health probe per vendor.** Rejected: it spends credits.
- **Ignoring cooldowns when everything is benched.** Rejected: it re-hits exhausted keys and logs Error on every call. The
  never-benched SearXNG member removes the case.
