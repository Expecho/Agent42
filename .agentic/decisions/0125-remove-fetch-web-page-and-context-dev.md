---
id: 0125
date: 2026-11-14
status: accepted
tags: [web-search, tools, egress, security, context-dev, ssrf, no-migration]
supersedes: [0027]
extends: []
amends: [0011, 0066, 0067, 0097, 0029, 0077]
---

# Remove `fetch_web_page` and the context.dev dependency, with no replacement

## Context
`fetch_web_page` (ADR 0027) read ordinary web pages through the paid context.dev scrape API. ADR 0066 already
recorded that context.dev "is being retired on cost". The original ask here was to replace it with a guarded
in-container fetch plus a local HTML→text pass (AngleSharp). **In the interview the user narrowed that: remove
the tool entirely, with no replacement and no HTML→text pass.**

One more option was researched and rejected: the context.dev MCP server (`https://mcp.context.dev/mcp`) is the
same paid service, has no documented equivalent of `followLinkFilter` / `maxFollowedLinks`, and would bypass the
whole-URL allowlist, the 8 KB result cap and the timeout.

## Decision
1. Delete `fetch_web_page`, `IWebPageFetcher` and its implementations, the context.dev named client, the
   `ContextScrape` config section, the `CONTEXT_API_KEY` secret / `contextApiKey` Bicep parameter / ACA secret, the
   config-health presence check and probe, and every model-facing mention, in the chat grain **and** the fleet.
2. `read_web_document` (ADR 0066: PDF / Office / text, `PrivateAddressGuard` at connect time) becomes the only
   model-invoked web fetch. It **keeps rejecting `text/html`**; only its message changes, because it can no longer
   point at `fetch_web_page`.
3. `web_search` (SearXNG) and the whole-URL allowlist (`FetchableUrls`, `SeededFetchableUrls`, ADR 0011/0067) are
   unchanged. The allowlist is **not** dead code: it now guards `read_web_document` only. The security rationale
   that lived in `FetchWebPageAsync`'s doc (whole URL, not host; the "search for attacker.example `<secret>`, then
   fetch it" bypass) moves onto `IsFetchableUrl`.
4. The fleet loses its own fetch allowlist with its fetch (`FleetWebTools` keeps only `web_search`). Its search
   result cut now uses a new documented backstop, `WebToolLimits.MaxSearchPayloadLength` (24 KB), in place of the
   deleted page budgets.
5. No migration (cross-cutting rule 2, ADR 0021): persisted history holding old `fetch_web_page` call/result pairs
   replays as plain history, `AgentGrainState` gains or loses no field, and the tool-selection key stays
   `web_search`, so stored selections are unaffected.

## Alternatives considered
- **(a) A guarded direct fetch + AngleSharp** (the original ask). Rejected by the user in the interview.
- **(b) The context.dev MCP server.** Rejected: same paid service, no link-following controls, bypasses the
  allowlist, the cap and the timeout.
- **(c) Letting `read_web_document` ingest HTML.** Rejected: it needs HTML→text, which is (a) under another name.

## Consequences
- The model can no longer read ordinary or JavaScript-rendered web pages beyond search snippets. The routes that
  remain are user MCP servers (e.g. the ADR 0109 Chrome bridge), the Copilot built-in URL tools (ADR 0107) and hosted
  web search on Responses models (ADR 0015). The fleet loses page reading altogether.
- Link following is gone, and with it ADR 0027's allowlist-exempt channel: a security *gain*.
- **Egress (rule 13):** this removes an ADR 0013 third party (context.dev). The privacy page no longer lists it.
- ADR 0077's residual narrows: a model-chosen URL's host and path can still reach App Insights on a
  `GuardedDocumentFetcher` HTTP span, but the context.dev `?url=` half of that bullet is moot.
- **Hosted-tools proof:** `fetch_web_page`'s absence used to be the only proof that ADR 0015's hosted-tool swap drops
  the SearXNG shims. That proof is re-anchored on the tool *types* in `ChatOptions.Tools`: `FakeChatClient` echoes
  `[toolTypes:web_search=…,code_interpreter=…]` (`HostedWebSearchTool` vs `AIFunction`) and the hosted-tools and
  `FetchWebPage_IsNotRegistered` tests assert on it. The echo is limited to those two names because every echo is
  persisted into stored replies and counts against the session-size budget tests.
- Infra: dropping `contextApiKey` removes the `context-api-key` secret from the Bicep `configuration.secrets` list,
  which should remove it from the container app on the next deploy. That is **unverified**: confirm with
  `az containerapp secret list` after deploy.
- Operator follow-ups (never agent actions): delete the `CONTEXT_API_KEY` GitHub secret and cancel the context.dev
  account.
- ADRs 0012, 0013, 0015, 0018, 0028, 0053 and 0099 mention `fetch_web_page` or context.dev as history and are left
  as written, not bannered. ADR 0027 is `superseded`; 0011, 0029, 0066, 0067, 0077 and 0097 carry an amendment banner.
