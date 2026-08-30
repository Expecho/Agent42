---
id: 0060
date: 2026-08-30
status: accepted
tags: [web-search, searxng, engines, availability, container-image, pinning]
supersedes: []
extends: []
amends: [0011, 0059]
---

# Bump the pinned SearXNG image and actually enable Google and Bing

> **Amends [ADR 0011](0011-web-search-self-hosted-searxng-sidecar.md).** ADR 0011 and every doc that
> followed it described the engine set as "the `keep_only:` list", i.e. as if listing an engine there
> enabled it. It does not. For the whole life of the feature **DuckDuckGo was the only engine that ever
> answered a query**; Google was never loaded at all and Bing was loaded but never queried. The claim in
> `.agentic/gotchas.md` that the sidecar "forwards every query to DuckDuckGo/Bing" was therefore wrong on
> both counts. This ADR makes the engine set match what the docs always said it was.

## Context

`web_search` has been returning empty result lists. [ADR 0059](0059-searxng-ui-behind-easy-auth-proxy.md)
caught the mechanism while measuring for the proxied UI and recorded it in its own Context section — every
query on the spike instance returned `No results`, with the JSON endpoint explaining why:

```json
{"query": "wikipedia", "results": [], "unresponsive_engines": [["duckduckgo", "CAPTCHA"]]}
```

ADR 0059 shipped the UI that makes this *visible*. It did not fix it, and it could not have: with one live
engine there was nothing to fall back to. That single point of failure is the finding here.

**`keep_only` filters the upstream engine list; it does not switch anything on.** Upstream ships each
engine's default entry with its own flags, and two different ones were suppressing two of our three:

| engine | upstream default on `2026.7.12-c19d86faa` | effect |
|---|---|---|
| `google` | `inactive: true` | `load_engines` **skips the entry outright** (`searx/engines/__init__.py:318`). Never registered, absent from `/config`, never queried, never reported as failing. |
| `bing` | `disabled: true` | Loaded and registered, but **sits out the default query set** — reachable only by a `!bi` bang. |
| `duckduckgo` | (none) | The only engine actually answering. |

The two flags are not interchangeable, and the difference is the whole decision: `disabled` is overridable
from our `settings.yml`, **`inactive` is not**. Verified by running the pinned image with `inactive: false`
merged in — `/config` then reported `google | enabled=True` and every query still returned **zero results
with no error**, because the engine upstream had marked inactive was genuinely broken.

Upstream has since repaired it. On `2026.8.29-d226b78bc` Google's default entry is `disabled: true`, not
`inactive: true` — the engine loads, and a `settings.yml` override reaches it.

## Decision

Bump the pinned base image from `2026.7.12-c19d86faa` to **`2026.8.29-d226b78bc`** (the immutable tag whose
digest matched `latest` on 2026-08-30 — the same discipline the Dockerfile already applied, verified by
comparing `RepoDigests`), and add a top-level `engines:` override in `infra/searxng/settings.yml` turning
`disabled` off for **google** and **bing**.

A top-level `engines:` entry is matched by `name` and `update_dict`-ed onto the upstream default
(`searx/settings_loader.py:166-174`), so the override changes one key per engine and inherits the rest — no
engine definition is copied into our config, which is what keeps the next bump cheap.

Bumping the pin was explicitly out of scope for ADR 0059, which deferred it as its own change ("the pinned
upstream image is now attack surface on our origin"). **Be precise about what the bump buys, because the
first draft of this ADR overstated it and the measurement below corrects it:** the override alone, on the
**old** tag, already restores `web_search` — measured, 10 results via Bing, because Bing was only
`disabled`. The bump is load-bearing for **Google alone**, an engine this ADR also measures as returning
nothing but a CAPTCHA. It is kept for the reasons in Alternatives, not because search depends on it.

## Measured

Built from `infra/searxng/`, run with the deployed env shape (`SEARXNG_PORT=8888`,
`SEARXNG_BASE_URL=.../searxng/`):

| Question | Before (`2026.7.12`) | After (`2026.8.29` + override) |
|---|---|---|
| Engines in `/config` | `duckduckgo` enabled; `bing` **disabled**; `google` **absent** | all three `enabled=True` |
| `/search?q=...&format=json` results | **0** | **10** |
| Which engine answered | — | `bing` (10/10) |
| `unresponsive_engines` | `[[duckduckgo, CAPTCHA]]` | `[[duckduckgo, CAPTCHA], [google, CAPTCHA]]` |
| Query latency | 0.06-0.15 s | 0.16-0.49 s |

And the case the Decision's precision depends on — **old tag, override applied**:

| Question | `2026.7.12` + the `engines:` override |
|---|---|
| Engines in `/config` | `bing` **and** `duckduckgo` enabled; `google` still **absent** (`inactive` is unreachable) |
| `/search?q=...&format=json` results | **10**, all from `bing` |

So the override alone fixes search; the bump adds only Google, which then CAPTCHAs.

**Google and DuckDuckGo both CAPTCHA; Bing is what makes `web_search` work again.** That is measured from a
residential IP — the production egress is a datacenter IP, which Google blocks harder, not softer, so
Google should be expected to contribute nothing there either. It is kept enabled anyway because it now
**reports** its failure into `unresponsive_engines` and the ADR 0059 UI, where before it was invisible; an
engine that says "CAPTCHA" is strictly better than one that is silently absent.

The latency cost of a failing engine is negligible: SearXNG suspends an engine after its first failure, so
subsequent queries do not retry it. Nothing approaches the `request_timeout: 5.0` that would matter to a
non-reentrant `AgentGrain` turn.

### ADR 0059's proxy properties re-measured on the new tag

The bump moves the image ADR 0059 §2 measured its "no markup is rewritten" property against, so all of it
was re-measured:

| ADR 0059 claim | On `2026.8.29` |
|---|---|
| §2 asset URLs root-relative and prefixed | **holds** — `http://localhost:8888/searxng/static/themes/simple/sxng-ltr.min.css` and its sxng-core.min.js sibling, plus the prefixed preferences, manifest and opensearch links |
| §2 `/searxng/` -> 200 **and** `/` -> 200 | **holds** (so `web_search`'s own `localhost:8888/search` is unaffected) |
| §2 bare `/searxng` -> 308 to an **absolute** URL | **holds** |
| §2 autocomplete off (the CSP sandbox depends on it) | **holds** — `/config` reports it empty |
| §2 JSON API answers 200, not 403 | **holds** (the format list is still honoured from the baked config) |
| §2 search form | **CHANGED** — `<form id="search" method="GET" action="/searxng/search">`; ADR 0059 recorded `method="POST"`. The **action is unchanged**, which is what the CSP argument rests on. |
| §4 a preferences save emits many `Set-Cookie`, **all `Path=/`** | **holds** — **21** headers (ADR 0059 measured 22), every one `Path=/`, so `NamespaceSetCookie` is still load-bearing |
| §5 a preferences save answers 302 with an **absolute** URL from the base URL | **holds** — `302`, `Location: http://localhost:8888/searxng/`, so `RewriteLocation`/`IsTrusted` still have exactly the input they exist for |

The form-method change is harmless on both counts that could have bitten. The catch-all route already
accepts `["GET", "POST"]` and already forwards the query string, and CSP `form-action` source paths ending
in `/` match as a **prefix**, so `form-action {scheme}://{host}/searxng/` still permits an action of
`/searxng/search` whichever method it uses. ADR 0059's CSP is unchanged.

## Alternatives considered

- **Override `inactive: false` without bumping the tag.** Measured: `/config` reports the engine enabled
  and every query returns zero results with no error — strictly worse than leaving it out, because it
  looks fixed. Rejected on evidence.
- **Enable Google only, as literally asked.** Rejected once Bing was measured: Google CAPTCHAs and Bing is
  the only engine returning anything, so enabling Google alone would have left `web_search` still broken
  while appearing to address it.
- **Drop Google, ship Bing + DuckDuckGo.** Rejected for the observability reason above — a CAPTCHAing
  Google is now visible in the ADR 0059 UI, and costs nothing.
- **Override Bing only and do NOT bump the image.** The minimal-risk option, and measured to work: it
  restores `web_search` on the old tag with a two-line config change and no new upstream code. Rejected,
  but narrowly and on judgement rather than data — the pinned image is now proxied onto our own origin
  (ADR 0059), so sitting seven weeks behind upstream on it is its own risk, and the bump was already
  identified there as worth doing. **The honest summary: this ADR bundles a deferred maintenance bump
  with a config fix that did not require it.** If the bump ever needs reverting, revert it alone — the
  `engines:` override keeps working, minus Google.
- **Add a paid search API (Brave/Tavily).** Out of scope, and it re-opens the API-key/bill/attribution cost
  ADR 0011 rejected. Revisit only if Bing also starts blocking.

## Consequences

- **`web_search` works again.** It was returning empty lists; it now returns results, via Bing.
- **Egress widens by one engine.** ADR 0011's honest framing is unchanged in kind — SearXNG is a
  *meta*search engine and the model-written query still reaches upstream engines unauthenticated from the
  container's IP. Google is now among them. What self-hosting buys (no API key, no bill, no per-user
  attribution) is unaffected.
- **The single point of failure is gone.** Three engines, of which one currently answers. If Bing starts
  CAPTCHAing too, the ADR 0059 UI and `unresponsive_engines` will say so instead of the feature going
  quiet.
- **`GET /api/config-health`'s SearXNG probe still cannot see this.** It checks reachability (200/403) and
  would have reported `ok` throughout the outage. Left as-is: an engine-health check is a separate change,
  and the ADR 0059 UI now covers the diagnosis.
- **The next bump is cheap.** The override names two keys, not two engine definitions, so upstream's own
  engine config keeps flowing through.
- **This change is on the availability path, and both halves of it are.** ACA gates revision health on
  *every* container in the replica, so a sidecar that will not start takes the **backend** down with it
  (ADR 0011) — and this change alters both inputs that can cause that: a new upstream image, and a new
  top-level `settings.yml` section upstream could reject. That is why the measurements above were taken by
  building `infra/searxng/` and running the container rather than by reading upstream's source: the
  container demonstrably starts, serves and searches with exactly this config.
- **A failing engine costs latency inside a non-reentrant grain turn, bounded by suspension.** Measured at
  0.16-0.49 s with two engines CAPTCHAing, because SearXNG suspends an engine after its first failure
  instead of retrying it. But that was measured from a residential IP, and this ADR's own reasoning says
  the datacenter IP should fare worse — a fast CAPTCHA and a slow tarpit are different shapes against
  `request_timeout: 5.0`. The suspension mechanism is what bounds the bad case; if a turn ever stalls on
  search, that is the thing to check first.
- **An unverified residual:** every measurement here is from a residential IP. Which engines answer from
  the ACA egress IP is knowable only after deploy, through the ADR 0059 UI at `/searxng/`.
