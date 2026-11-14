planned-against: main @ ec7c9e5

# Plan: remove `fetch_web_page` and the context.dev dependency

## Overview

context.dev is being retired on cost grounds (recorded in ADR 0066's Context). The original ask was to
replace `fetch_web_page` with a guarded in-container fetch plus local HTML→text. **In the interview the
user changed the scope: remove `fetch_web_page` entirely, with no replacement and no HTML→text pass.**
`read_web_document` (ADR 0066: PDF/Office/text, `PrivateAddressGuard` at connect time) becomes the only
model-invoked web fetch, and it **keeps rejecting `text/html`**. `web_search` (SearXNG) and the whole-URL
allowlist stay. A new ADR fully supersedes ADR 0027 and amends 0011/0066 (and others listed in Step 1).

## Affected areas (verified against the tree at ec7c9e5)

**Delete**
- `src/Agent42.Backend/Web/ContextWebPageFetcher.cs`
- `src/Agent42.Agents/Web/IWebPageFetcher.cs`, `src/Agent42.Agents/Web/InMemoryWebPageFetcher.cs`,
  `src/Agent42.Agents/Web/WebPageContent.cs` (holds `WebPageContent` + `FollowedPage`)
- `tests/Agent42.Tests/ContextWebPageFetcherTests.cs`

**Backend / hosts / infra**
- `src/Agent42.Backend/Program.cs`: the context.dev named client (~L248-260), the `IWebPageFetcher`
  registration (~L313-315), and comments that cite context.dev (~L231, ~L389)
- `src/Agent42.Backend/appsettings.json` (`ContextScrape` section)
- `src/Agent42.AppHost/AppHost.cs` (the `ContextScrape__ApiKey` forward, ~L53-56)
- `tools/Agent42.DevHost/Program.cs` (`InMemoryWebPageFetcher` registration)
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` (presence check, `probe:context.dev`,
  `ConfigHealthProbeSet.ContextScrape`, `ProbeContextAsync`, comments) and `AgentApi.Configuration.cs` (comments)
- `src/Agent42.Backend/Fleet/FleetWebTools.cs`, `FleetToolCatalog.cs`, `FleetPrompts.cs` (~L60);
  `src/Agent42.Backend/wwwroot/fleet/fleet.js` (~L527, comment only)
- `src/Agent42.Backend/Web/GuardedDocumentFetcher.cs` (HTML-rejection message says "use fetch_web_page"),
  `src/Agent42.Backend/Web/PrivateAddressGuard.cs` (doc)
- `src/Agent42.Backend/Models/ChatClientFactory.cs` (~L27), `src/Agent42.Backend/ImageGeneration/OpenAIImageGenerator.cs` (~L35),
  `src/Agent42.Backend/Telemetry/LiveTelemetryBuffer.cs` (~L73). All three are comments that cite `fetch_web_page`/`MaxFollowedLinks`
- `src/Agent42.Backend/wwwroot/legal/privacy.html` (~L59, context.dev recipient)
- `src/Agent42.Backend/wwwroot/index.html` (~L980, MCP seeding note "fetched as a page or read as a document")
  → **`src/Agent42.Backend/wwwroot/sw.js` `CACHE` bump** (index.html is in `SHELL`)
- `infra/main.bicep` (`contextApiKey` param ~L45, pass-through ~L270, plus other params' `@description`s
  saying "like contextApiKey"), `infra/modules/container-app.bicep` (param ~L85-86, secret ~L198-203,
  env ~L293-298, and other params' `@description`s at ~L89/92/100/104), `.github/workflows/deploy.yml` (~L101)

**Agents**
- `src/Agent42.Agents/Web/AgentGrain.WebTools.cs`: delete `FetchWebPageAsync` + `BuildFetchPayload`; keep
  `WebSearchAsync` and every allowlist helper (`IsFetchableUrl`, `RememberFetchableUrl*`,
  `RememberSeededUrlsFromToolResult`, `Normalize`, both regexes). `read_web_document` uses them.
- `src/Agent42.Agents/Conversations/AgentGrain.cs` (`_webPageFetcher` field + ctor param)
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (the `fetchWebPage` registration ~L231-247, the
  `web_search` description ~L228, the `read_web_document` description ~L269, comment ~L297)
- `src/Agent42.Agents/Tools/ToolCatalog.cs` (`["fetch_web_page"] = WebSearch` ~L253; the `WebSearch`
  description ~L285 "Searches the web and fetches pages…"; doc ~L89)
- `src/Agent42.Agents/Web/WebToolLimits.cs` (drop `MaxPageTextLength`, `TruncationMarker`,
  `MaxFollowedLinks`, `MaxTotalPageTextLength`; see Step 3 for the fleet search backstop)
- `src/Agent42.Agents/UserSkills/SkillToolLimits.cs` (~L29 cref to `MaxPageTextLength`)
- `src/Agent42.Agents/Documents/AgentGrain.WebDocumentTool.cs` (doc ~L12/22, `url` description ~L48,
  message ~L79), `src/Agent42.Agents/Documents/IWebDocumentFetcher.cs` (doc)
- `src/Agent42.Agents/Conversations/AgentGrainState.cs` (docs on `FetchableUrls` ~L92 and `SeededFetchableUrls` ~L201)
- `src/Agent42.Agents/Models/IChatClientFactory.cs` (~L17), `src/Agent42.Agents/Mcp/UserMcpConfig.cs` (~L65),
  `src/Agent42.Agents/Mcp/UrlHarvestingMcpTool.cs` (~L7)
- `src/Agent42.Agents/Skills/websearch/SKILL.md`

**Tests**
- `tests/Agent42.Tests/AgentGrainTests.cs`: `FetchWebPage_*` (~L1740-1890), the session-budget test (~L1890-),
  hosted-tools tests (~L3084-3150, ~L3607, ~L3788)
- `tests/Agent42.Tests/CapSizedWebPageFetcher.cs`: holds `CapSizedWebPageFetcher` (delete) **and**
  `CapSizedWebDocumentFetcher` + `CapSizedFetcherSiloConfigurator` (keep). Rename the file to
  `CapSizedWebDocumentFetcher.cs` with `git mv`.
- `tests/Agent42.Tests/FakeChatClient.cs` (fetch scripts ~L626-654, seeding call ~L765, `fetch-call-2` ~L888, ~L1016/1033)
- `tests/Agent42.Tests/McpUrlSeedingTests.cs`, `FleetToolCatalogTests.cs`, `FleetTaskServiceTests.cs` (~L131/140),
  `CopilotAgentTests.cs` (~L173), `ConfigHealthTests.cs`, `ConfigHealthProbeTests.cs`, `ConfigHealthApiTests.cs`,
  `SiloRegistrationTests.cs` (~L22), `TestServiceRegistration.cs` (~L54-65), `TestHttpServer.cs` (~L14 doc),
  `PrivateAddressGuardTests.cs` (~L12 doc), `WebDocumentToolTests.cs` (~L21 doc)

**Docs / knowledge**
- `CLAUDE.md` (the `CONTEXT_API_KEY` prerequisites row; the Web search feature-map row)
- `README.md` (~L218, ~L306)
- `docs/features/web-search.md` (major rewrite), `configuration-health.md`, `code-interpreter.md`,
  `agent-fleet.md`, `remote-mcp-servers.md`, `document-qa.md`, `docs/features/README.md`
- `.agentic/gotchas.md`: exemplar row L40; the return-budget entry (~L220-253); "TWO web fetch paths" (~L289-332);
  "Following links is exempt" (~L334-359, delete); the seeding entry (~L361-430, "six surfaces"); ~L460, ~L474,
  ~L491; the hosted-tools entry (~L1342-1352)
- Active specs: `.agentic/specs/plan-chat-gateway.md` (~L251, exemplar points at the context.dev client),
  `plan-deferred-tool-loading.md`, `plan-agent-authored-tools.md`
- ADRs (Step 1): 0027, 0011, 0066, 0067, 0097, 0029, 0077

## Context & constraints

- **User decisions (interview, 2026-11-14):**
  1. Remove `fetch_web_page` entirely: chat grain, fleet, and every description, skill and doc. There is **no
     guarded direct-fetch replacement and no HTML→text**, and AngleSharp stays out of the repo.
  2. `read_web_document` **keeps rejecting `text/html`**. Only its wording changes: it can no longer point at
     `fetch_web_page`.
  3. The new ADR **fully supersedes ADR 0027** (`status: superseded`, `superseded_by`). Everything 0027 decided
     (context.dev, following, the total budget, the guidance rewrite) is reversed. Its one surviving point, the
     whole-URL allowlist, was always ADR 0011's.
- **Prior research (don't redo it):** the context.dev MCP server (`https://mcp.context.dev/mcp`) is the same paid
  service. It has no documented `followLinkFilter`/`maxFollowedLinks` equivalent, and it would bypass the
  allowlist, the 8 KB cap and the timeout, so it was rejected as a replacement.
- **The allowlist is NOT dead code.** `FetchableUrls` / `SeededFetchableUrls` and the whole-URL rule now guard
  `read_web_document` only. The security rationale in `FetchWebPageAsync`'s XML doc (whole URL vs host, the
  "search for attacker.example <secret>, then fetch it" bypass) must **move** to `IsFetchableUrl`'s doc, not be
  deleted with the method. The ADR 0011 consequence "`RememberFetchableUrlsInUserText` is never called on the
  reminder path" still holds.
- **Allowlist regression tests must be PORTED, not deleted.** `FetchWebPage_RefusesAttackerUrlWhoseHostItsOwnSearchSurfaced`,
  `FetchWebPage_BareHostFromUser_GrantsOnlyTheHomepage`, `FetchWebPage_AllowsHostFromSearchResult`,
  `FetchWebPage_AllowsHostFromUserMessage` and `FetchWebPage_RefusesUnknownHost` pin the control. For each one, either
  name the existing `WebDocumentToolTests` test that already proves the same property over `read_web_document`, or
  port it to `read_web_document` (the `FakeChatClient` scripts take a tool name). Only `ReadWebDocument_RefusesUrlOutsideAllowlist`
  exists today, so the whole-URL-vs-host and bare-host-homepage cases almost certainly need porting.
- **Fleet loses its allowlist with its fetch.** `FleetWebTools._fetchable` and the request-URL harvesting exist
  only to gate `fetch_web_page`, and the fleet has no `read_web_document`. Delete them; leaving them would be dead
  state. `FleetWebTools` keeps `web_search` (the class name stays).
- **The hosted-tools proof needs a new anchor (critic M1).** `AgentGrainTests.cs:3148-3150` uses `fetch_web_page`'s
  absence as the ONLY proof that ADR 0015's swap dropped the SearXNG shims (the hosted tool reuses the name
  `web_search`, and `ToolNames(...)` only reads the fake's `[tools:…]` name echo, which can't tell hosted from shim).
  Deleting the tool makes `HostedToolsModel_SwapsWebShimsForHostedTools` assert nothing. **Required mechanism:** extend
  `FakeChatClient` to also capture the *types* of `ChatOptions.Tools` (e.g. an echo `[toolTypes:…]` listing
  `HostedWebSearchTool` vs `AIFunction`), and assert on the hosted branch that `web_search` is the hosted type and on the
  shim branch that it is an `AIFunction`. If that proves unobservable, the executor must STOP and record the lost
  coverage explicitly in the ADR rather than ship a vacuous test. Also edit the expected-name lists at `:3121`, `:3607`
  (`Assert.Contains("fetch_web_page", registered)` becomes a negative) and `:3788`, and read gotchas ~L1342-1352 first.
- **No migration (rule 2 / ADR 0021).** Persisted history containing old `fetch_web_page` call/result pairs
  replays as plain history. `AgentGrainState` gains or loses no field. The tool-selection key stays `web_search`
  (only the `tool name → key` map entry goes), so stored selections are unaffected. No `[Id]` changes.
- **Egress (rule 13):** this *removes* an ADR 0013 third party. Record that in the ADR and on the privacy page. ADR
  0077's residual still stands: a model-chosen URL's host+path reaches App Insights on a `GuardedDocumentFetcher`
  HTTP span. The context.dev `?url=` half of that bullet becomes moot.
- **Infra:** dropping `contextApiKey` and the `context-api-key` secret from Bicep should remove the ACA secret on the
  next deploy, because `configuration.secrets` is declared in full. That's **unverified**: list it as a post-deploy
  check, not as a fact. Operator follow-ups (not agent actions): delete the `CONTEXT_API_KEY` GitHub secret and
  cancel the context.dev account.
- **Doc-path integrity (CI):** `.agentic/scripts/check-doc-paths.mjs` scans `CLAUDE.md`, `gotchas.md` and
  every **non-totally-superseded** ADR. Once 0027 is `status: superseded` it is skipped. The critic grepped the
  accepted ADRs and found none quoting a deleted path, so the banners are for history, not CI. The real risk is the
  `gotchas.md` evidence lines. Run the script and fix whatever it flags. Completed specs are not scanned. Leave them alone.
- **sw.js:** `index.html` is in `SHELL`, so changing its seeding note needs a `CACHE` bump (currently
  `agent42-shell-v77`). `legal/privacy.html` and `wwwroot/fleet/` are outside `SHELL` and need none. Four tests pin
  `CACHE` (see memory `fleet-from-chat`). Grep the tests for the literal and update them together.
- **Governing docs:** ADRs 0011, 0027, 0066, 0067, 0077, 0097, 0029, 0015 (hosted swap, unchanged), 0021,
  0004. Gotchas entries listed above. Cross-cutting rules 2, 3, 5, 7 (`AddAgent42Silo` fails startup on a missing
  *direct* grain dependency, so dropping the ctor param is enough), 13.

## Steps

### Step 0: Branch
`git switch -c feature/remove-context-dev-and-fetch-web-page` from `main @ ec7c9e5`. If `main` has moved,
re-run the Step 7 greps before starting.

### Step 1: ADR (`/decide`)
Write `.agentic/decisions/0125-remove-fetch-web-page-and-context-dev.md`. Take the next free number from
`origin/main` at merge time and fix it if taken. Front matter: `supersedes: [0027]`, `amends: [0011, 0066, 0067, 0097, 0029, 0077]`.
Tags: `web-search, tools, egress, security, context-dev, ssrf`.
- Context: cost retirement; the user's scope call; the MCP-server rejection (above).
- Decision: delete `fetch_web_page` and context.dev, with no replacement. `read_web_document` is the only fetch path
  and still rejects HTML. `web_search` and the allowlist are unchanged.
- Alternatives: (a) guarded direct fetch + AngleSharp: the original ask, rejected by the user in the interview;
  (b) the context.dev MCP server: rejected (above); (c) letting `read_web_document` ingest HTML: rejected, since it
  needs HTML→text, which is the same thing as (a).
- Consequences: the model can't read ordinary or JS web pages beyond search snippets, except through user MCP servers
  (e.g. the ADR 0109 Chrome bridge), Copilot built-in URL tools (ADR 0107) or hosted web search on Responses models
  (ADR 0015). Link following is gone, and with it 0027's allowlist-exempt channel: a security *gain*. One fewer egress
  third party. No migration. The fleet loses page reading. The 0077 residual narrows. Operator follow-ups.
- Same commit: set 0027 to `status: superseded` + `superseded_by: 0125`. Add a dated amendment banner to the top of
  0011, 0066, 0067, 0097, 0029 and 0077, in the style of the existing banners. Never edit their decision bodies.
**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~AdrNumbering|FullyQualifiedName~DocPathChecker"` and
`node .agentic/scripts/check-doc-paths.mjs` (the latter is re-run in Step 7).

### Step 2: Remove the context.dev plumbing (backend, hosts, config health, infra)
Delete `ContextWebPageFetcher.cs` and its tests. Remove the named client and its registration from `Program.cs`, the
`ContextScrape` section from `appsettings.json`, the AppHost forward, the ConfigHealth presence check + probe + record
member + `ProbeContextAsync`, the Bicep param/secret/env (and reword the sibling `@description`s that say "like
contextApiKey" to name `googleMapsApiKey` instead), the `deploy.yml` line, and the privacy-page recipient. Update
`ConfigHealth*Tests` for the 3-member `ConfigHealthProbeSet` and the removed check.
**Verify:** `dotnet build Agent42.slnx` (expect errors only from Step 3's still-present `IWebPageFetcher` users if
done in this order. Doing Steps 2+3 before building is fine.), `dotnet test --filter "FullyQualifiedName~ConfigHealth"`,
and `az bicep build --file infra/main.bicep` if the CLI is available (else record SKIPPED).

### Step 3: Remove `fetch_web_page` (grain, catalog, fleet, limits)
Delete `IWebPageFetcher`, `InMemoryWebPageFetcher` and `WebPageContent`. Strip the tool from `AgentGrain.WebTools.cs`,
**moving the allowlist rationale onto `IsFetchableUrl`**. Drop the field/ctor param in `AgentGrain.cs`, the registration
and description references in `BuildAgent.cs`, and the map entry in `ToolCatalog.cs`, and reword the `WebSearch`
description ("Searches the web through the SearXNG sidecar."). Remove DevHost/test registrations
(`TestServiceRegistration`, `CapSizedFetcherSiloConfigurator`, `SiloRegistrationTests`' type list). Fleet:
`FleetWebTools` keeps only `web_search` and drops `_fetchable`/`Remember`/`TryNormalize`/the regex. Its search `Cut`
bound repoints from `MaxTotalPageTextLength` to a new `WebToolLimits.MaxSearchPayloadLength` (a documented
backstop, since `IWebSearch` already budgets results). `FleetToolCatalog` loses the fetcher parameter, and its web block
is gated on `webSearch` alone. Update `FleetPrompts`, then `WebToolLimits` and `SkillToolLimits`' cref.
**Verify:** `dotnet build Agent42.slnx` clean (S3776 included). New test **`FetchWebPage_IsNotRegistered`** in
`AgentGrainTests.cs`: on both the Chat-Completions and hosted-tools branches, the registered tool names don't contain
`fetch_web_page`. Fleet equivalent in `FleetToolCatalogTests.cs`: `Tools_DoNotIncludeFetchWebPage`.

### Step 4: Rewrite the model-facing text
`SKILL.md` (websearch): front-matter description, intro, "Search, then read", "Pages versus documents", the allowlist
section (now `read_web_document` only), and delete "Reading content that is one click away" and its follow-specific
safety paragraph. Keep the "a page/document is not a person" injection guidance, scoped to documents and search
results. Add plain guidance: web pages can't be read in full, so answer from snippets, say when a snippet isn't enough,
and suggest the user paste the text. `read_web_document`'s description and messages, and `GuardedDocumentFetcher`'s
HTML-rejection message, no longer name `fetch_web_page` ("this is a web page, not a document; web pages can't be read
in full here").
**Verify:** `dotnet test --filter "FullyQualifiedName~WebDocument|FullyQualifiedName~Skill"`. Add an assertion (to an
existing `WebDocumentToolTests` HTML-rejection test, or a new `GuardedDocumentFetcher_HtmlRejection_DoesNotNameFetchWebPage`)
that the message doesn't contain `fetch_web_page`.

### Step 5: Repair and port the tests
- `AgentGrainTests`: delete `FetchWebPage_ApiFailure_ReturnsMessageToModel` and `FetchWebPage_LabelsFollowedContentAsUntrusted`.
  **Port** the allowlist tests listed in Context to `read_web_document` (names `ReadWebDocument_…`) in `WebDocumentToolTests.cs`;
  only `ReadWebDocument_RefusesUrlOutsideAllowlist` exists today, so write all of them (critic gap): `…RefusesAttackerUrlWhoseHostItsOwnSearchSurfaced`,
  `…BareHostFromUser_GrantsOnlyTheHomepage`, `…AllowsUrlFromSearchResult`, `…AllowsUrlFromUserMessage`. `FakeChatClient` has
  `BuildReadWebDocumentCall` (~L676) and seeding-chain support (~L759) but **no search-then-read chain**: add one (a
  `web_search` call followed by a `read_web_document` of a different path on the surfaced host), plus a bare-host user-message variant.
- Session-budget test (critic M2): drop its `fetch_web_page` turns and keep the `read_web_document` turns
  (`Assert.Contains("magrathea-", …)` stays). The sanity assert at `AgentGrainTests.cs:1933`
  (`bytes > WebToolLimits.MaxPageTextLength`) won't compile once the constant is gone: re-anchor it on `WebDocumentLimits`.
  The 256 KB ceiling (`:1961`) was tuned with ~40 KB of page text in the session and the comment at `:1944-1949` records 1-2%
  headroom: **re-measure the persisted size without the page turns and tighten the ceiling** so AC7 genuinely binds, and
  update that comment. Delete `CapSizedWebPageFetcher` and `git mv` the file to `CapSizedWebDocumentFetcher.cs`.
- Hosted-tools tests: re-anchor the "shims dropped" proof (see Context). Remove `fetch_web_page` from the expected-name lists.
- `FakeChatClient`: delete the fetch/follow/fail scripts; repoint the exfiltration and seeding-chain calls to
  `read_web_document` (with a `question` arg).
- `McpUrlSeedingTests`: delete `SeededUrl_IsFetchableAsAPage` (`SeededUrl_IsReadableAsADocument` covers the property);
  repoint the rest from `fetch_web_page` to `read_web_document`.
- `FleetToolCatalogTests` (expected lists, `HugePageFetcher`, the fetch-allowlist test at ~L132-139, which is deleted with
  the fleet allowlist), `FleetTaskServiceTests` ~L140 (planner no longer lists `- fetch_web_page:`), `CopilotAgentTests` ~L173.
- Doc comments in `TestHttpServer`, `PrivateAddressGuardTests`, `WebDocumentToolTests`.
**Verify:** `dotnet test Agent42.slnx`, all green.

### Step 6: Frontend text + cache
`index.html` seeding note: "…can be read as a document without you pasting it back…". Bump `CACHE` in `sw.js` and
update the tests that pin the literal. `fleet.js` comment.
**Verify:** four tests pin `agent42-shell-v77` (critic L5): `ClientToolsAssetsTests.cs:19`, `PwaAssetsTests.cs:269`,
`ResearchAssetsTests.cs:19`, `SearchAssetsTests.cs:68`. Update all four, then run
`dotnet test --filter "FullyQualifiedName~ClientToolsAssets|FullyQualifiedName~PwaAssets|FullyQualifiedName~ResearchAssets|FullyQualifiedName~SearchAssets"`.

### Step 7: Docs, gotchas, specs + residue sweep
- `CLAUDE.md`: delete the `CONTEXT_API_KEY` row. Change the feature-map row to "Web search (SearXNG sidecar, engines,
  proxied UI); no page fetch (removed, 0125) | 0011, 0059, 0060, 0125".
- `README.md`, `docs/features/*` as listed. `web-search.md` loses its fetch diagram, snippets and the key-files row.
- `gotchas.md` (line numbers approximate; the real `check-doc-paths` tripwires are the evidence lines at L40, L325,
  L353-356 and L447-449, plus more `fetch_web_page` mentions at L373, L426, L491): drop `ContextWebPageFetcher.cs` from the L40 exemplar row (keep `GoogleGeocoder.cs`); rewrite the
  return-budget entry (no page budget; `read_web_document` and `read_document` remain); rewrite "TWO web fetch paths" as
  "ONE web fetch path: `read_web_document`, guarded", which keeps the guard/redirect/0067 threat-model bullets; delete the
  following-links entry; in the seeding entry, re-count the "six surfaces" now that the `fetch_web_page` description is
  gone; fix ~L460/474/491/1342-1352. Re-verify the dates on touched entries.
- Active specs: repoint `plan-chat-gateway.md` ~L251's exemplar to another `RemoveAllResilienceHandlers` + explicit-timeout
  client in `Program.cs` (e.g. the `GuardedDocumentFetcher` client); fix the `fetch_web_page` mentions in
  `plan-deferred-tool-loading.md` and `plan-agent-authored-tools.md`.
- Also fix the active spec `.agentic/specs/plan-phone-call-your-agent.md` (L67, L293, L461 cite `contextApiKey`; reword to
  `googleMapsApiKey`/`vapidPrivateKey`) (critic M3). ADRs 0028/0053/0015/0099/0013/0012/0018 that mention `fetch_web_page` or
  context.dev are left as history: say so in the new ADR rather than bannering them.
- **Residue sweep:** this must return nothing outside `.agentic/decisions/` (banners and the new ADR),
  `.agentic/specs/completed/` and **this plan file itself** (move it to `specs/completed/` at the end, as the workflow does):
  `rg -n -i "context\.dev|ContextScrape|CONTEXT_API_KEY|contextApiKey|context-api-key|fetch_web_page|FetchWebPage|WebPageFetcher|IWebPageFetcher|WebPageContent|FollowedPage|followLinkFilter|maxFollowedLinks|MaxTotalPageTextLength|MaxFollowedLinks"`
- New test **`ContextDevRemovalTests.NoContextDevReferencesRemain`**: scan `src/`, `infra/`, `.github/`, `tools/` for
  `context.dev` (case-insensitive), `ContextScrape`, `CONTEXT_API_KEY` and `contextApiKey`, and expect zero hits. Skip `bin/`
  and `obj/` (stale appsettings copies and PDBs would fail a dirty local tree) and filter to text files. `RepoRoot()` is
  private to `WebDocumentToolTests`: duplicate it in the new class.
**Verify:** `node .agentic/scripts/check-doc-paths.mjs`, `dotnet format Agent42.slnx --verify-no-changes`,
`dotnet test Agent42.slnx`.

**Parallelism:** Steps 2 and 3 touch disjoint files, but both must be in before anything builds, so run them
sequentially in one session. Step 7's docs work is independent of Steps 4-6 and could run in a worktree. Sequential
is simpler and is recommended.

## Acceptance criteria → tests

| AC | Proven by | File |
|----|-----------|------|
| AC1 `fetch_web_page` isn't offered on either capability branch | `FetchWebPage_IsNotRegistered` (new) + updated hosted-tools lists | `AgentGrainTests.cs` |
| AC2 Fleet offers `web_search` but not `fetch_web_page` | `Tools_DoNotIncludeFetchWebPage` (new) + updated expected lists | `FleetToolCatalogTests.cs` |
| AC3 Whole-URL allowlist still refuses a URL whose host a search surfaced (now via `read_web_document`) | `ReadWebDocument_RefusesAttackerUrlWhoseHostItsOwnSearchSurfaced` (ported) | `WebDocumentToolTests.cs` |
| AC4 A bare host from the user grants only its homepage | `ReadWebDocument_BareHostFromUser_GrantsOnlyTheHomepage` (ported) | `WebDocumentToolTests.cs` |
| AC5 URLs from search results / user messages are readable | `ReadWebDocument_AllowsUrlFromSearchResult`, `…FromUserMessage` (ported, or the existing equivalents named in the MR) | `WebDocumentToolTests.cs` |
| AC6 MCP-seeded URLs still feed `read_web_document` | `SeededUrl_IsReadableAsADocument` + repointed seeding tests | `McpUrlSeedingTests.cs` |
| AC7 Session-budget test still binds without the page fetcher | the budget test (fetch turns dropped, `magrathea-` assert kept) | `AgentGrainTests.cs` |
| AC8 Config health has no context.dev check or probe; a static call still runs zero probes | updated `ConfigHealthTests`, `ConfigHealthProbeTests`, `ConfigHealthApiTests` | those files |
| AC9 HTML is still rejected by `read_web_document`, and the message doesn't name `fetch_web_page` | the assertion added in Step 4 | `WebDocumentToolTests.cs` / `GuardedDocumentFetcher` tests |
| AC10 No context.dev reference remains in src/infra/.github/tools | `NoContextDevReferencesRemain` (new) | `ContextDevRemovalTests.cs` |
| AC11 The document client still has the guard wired + resilience removed | existing `ProgramCs_WiresTheGuardIntoTheDocumentClientsPrimaryHandler`, `ProgramCs_DocumentClientRemovesResilienceAndSetsATimeout` (unchanged, must stay green) | `WebDocumentToolTests.cs` |
| AC12 Docs are consistent | `check-doc-paths.mjs` + the Step 7 residue sweep | CI |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at ec7c9e5)

| Gate | Threshold | Tool |
|------|-----------|------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` over coverlet cobertura |
| `cognitive_complexity_max` | 15 (S3776, build error) | `dotnet build Agent42.slnx` |
| `vulnerable_packages` | 0 | CI package-freshness job. This plan adds **no** package. |
| build / tests / format / doc paths | must pass | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, `check-doc-paths.mjs` |

## Out of scope

- Any replacement page reader (guarded direct fetch, AngleSharp, the context.dev MCP server, headless browser).
- Letting `read_web_document` accept `text/html`.
- Changing `web_search`, SearXNG, the allowlist mechanism, `PrivateAddressGuard` or `GuardedDocumentFetcher`'s
  behaviour (only its message text changes).
- Suppressing `GuardedDocumentFetcher`'s HTTP span (ADR 0077 residual, unchanged).
- Editing `.agentic/specs/completed/*` or the decision bodies of accepted ADRs.
- Operator actions: deleting the `CONTEXT_API_KEY` GitHub secret, cancelling the context.dev account. Post-deploy:
  confirm the `context-api-key` ACA secret is gone (`az containerapp secret list`, read-only).

## Review

A `critic` subagent reviewed the plan against ec7c9e5. Verdict: no ADR contradiction, and the supersession mechanics are
correct (0027 `status: superseded` makes `check-doc-paths` skip it, 0125 is free, the allowlist helpers stay, ADR 0021/0105
are respected). Seven findings, **all accepted** and folded into the plan above:

| # | Sev | Finding | Handling |
|---|-----|---------|----------|
| M1 | Med | Hosted-tools "shims dropped" proof relied on `fetch_web_page`'s absence, so AC1's hosted branch became vacuous | Context now requires a `FakeChatClient` tool-**type** echo, or an explicit record of the lost coverage in the ADR. The `:3121/:3607/:3788` lists are named |
| M2 | Med | Session-budget test: `:1933` sanity assert won't compile and the 256 KB ceiling needs re-tuning | Step 5 re-anchors on `WebDocumentLimits` and requires re-measuring and tightening the ceiling |
| M3 | Med | Residue sweep matched the plan itself, missed `plan-phone-call-your-agent.md` and `WebPageFetcher` | Sweep pattern + plan-file exemption added. Phone-call spec added to Step 7. 0028/0053/0015/0099 are left as history, stated in the ADR |
| L4 | Low | `NoContextDevReferencesRemain` unspecified (bin/obj, case, private `RepoRoot`) | Step 7 specifies all three |
| L5 | Low | Step 6 filter ran only 1 of 4 `CACHE`-pinning tests | The four files are named |
| L6 | Low | Claim that accepted ADRs would fail doc-path CI was wrong, and gotchas line numbers were off | Reworded. Real tripwires are `gotchas.md` L40/325/353-356/447-449 |
| L7 | Low | `AgentGrainTests.cs:2316` asserts a string absent from SKILL.md (vacuous today) | Noted. Not blocking, so left alone as out of scope |

The critic also flagged a gap: AC3-AC5 need a search-then-read `FakeChatClient` script, which doesn't exist today. Step 5
now says to add it.

## Divergences from the plan (recorded at completion, 2026-11-14)

- **Hosted-tools proof:** implemented as planned, via a `FakeChatClient` `[toolTypes:…]` echo. Limited to `web_search` and
  `code_interpreter`: echoing every tool pushed `ArtifactToolTests`' 64 KB session budget over its ceiling.
- **Allowlist ports:** no new search-then-read script was needed. The existing "exfiltrate" chain, repointed to
  `read_web_document`, is the attack chain. One extra test was added (`ReadWebDocument_RefusesOtherPathOnAHostASearchSurfaced`);
  `FetchWebPage_RefusesUnknownHost` is covered by the existing `ReadWebDocument_RefusesUrlOutsideAllowlist`.
- **Session-budget ceiling:** tightened from 256 KB to 128 KB (measured ~106 KB net of echoes); the "ceiling still binds without the
  excerpt cap" claim in its comment was not mutation-tested.
- **Residue sweep:** not literally empty. It still hits negative assertions (`DoesNotContain("fetch_web_page", …)`), the
  `FetchWebPage_IsNotRegistered`/`Tools_DoNotIncludeFetchWebPage` test names, `ContextDevRemovalTests`, and a few explanatory
  comments in src/tests/gotchas naming the removed tool. `ContextDevRemovalTests` covers the shipped surface (src, infra, .github, tools).
- **`FleetToolCatalog.Build/BuildAsync`** still take an unused `request` parameter (kept to avoid churn): a follow-up.
- **Environment:** `dotnet build Agent42.slnx` reports one error in `tools/Agent42.DevHost` (SDK 11.0.100 Blazor HotReload
  scoped-css targets) that also occurs on clean `main`. Tests were run per project.
- **Test flake seen once:** `LiveCopilotTests.LiveCopilot_BuiltIns_ShellSeesFileToolWrite_AndResumes` (live model); green on rerun.
